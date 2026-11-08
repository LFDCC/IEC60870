//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 异步客户端（主站）。基于 TouchSocket <see cref="TcpClient"/>，
/// 接收管线由 <see cref="Iec104Adapter"/>（CustomDataHandlingAdapter）完成三态解析与滑动重同步，
/// 协议状态机与定时循环聚合在 <see cref="ApduEndpoint"/>，热路径零拷贝。
/// </summary>
/// <remarks>
/// 用法：
/// <code>
/// await using var client = new Iec104Client("127.0.0.1", 2404);
/// client.AsduReceived += (in AsduView a) => { /* 处理 */ };
/// await client.ConnectAsync();
/// await client.StartDataTransferAsync();
/// await client.SendAsync(asdu);
/// </code>
/// 标准命令动词（总召唤/时钟同步/控制命令等）为扩展方法，见 <see cref="Iec104MasterExtension"/>。
/// </remarks>
public sealed class Iec104Client : TcpClient, IIec104Master, IIec104ApduSink
{
    private readonly string _host;
    private readonly int _port;
    private readonly APCIParameters _apci;
    private readonly ApplicationLayerParameters _al;
    private readonly ClientSslOption _sslOption;
    private readonly ApduEndpoint _endpoint;
    private readonly ControlConfirmationTracker _confirmationTracker = new ControlConfirmationTracker();

    private bool _setupDone;
    private bool _dataTransferStarted;
    private AsduViewHandler _asduReceived;
    private Action<ApduConnectionEvent> _connEvent;

    #region 属性

    /// <summary>控制命令确认匹配器（供 <see cref="Iec104MasterExtension.SendControlCommandAndWaitAsync"/> 使用）。</summary>
    internal ControlConfirmationTracker ConfirmationTracker => _confirmationTracker;

    /// <summary>应用层参数（可在连接前调整字段宽度）。</summary>
    public ApplicationLayerParameters Parameters => _al;

    /// <summary>
    /// ASDU 类型处理器注册表（实例级可替换）。
    /// 默认共享全局 <see cref="AsduTypeHandlerRegistry.Default"/>；
    /// 私有类型场景可整体替换或直接在其上 Register 自定义 <see cref="IAsduTypeHandler"/>。
    /// </summary>
    public AsduTypeHandlerRegistry TypeHandlers { get; set; } = AsduTypeHandlerRegistry.Default;

    /// <summary>
    /// <see cref="Iec104MasterExtension.SendControlCommandAndWaitAsync"/> 等待 ACT-CON 确认的超时时长,默认 5 秒。
    /// 超时/取消不关闭连接（请求-响应匹配语义:挂起槽自动清理,迟到确认丢弃）。
    /// </summary>
    public TimeSpan CommandConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>数据传输是否已激活。</summary>
    public bool IsActivated => _endpoint.Connection?.IsActive ?? false;

    /// <summary>
    /// 最近一次底层连接断开的原因。仅当 <see cref="ConnectionEvent"/> 收到
    /// <see cref="ApduConnectionEvent.ConnectionClosed"/> 后有意义；未连接或对象已释放时为 null。
    /// 可用于重连策略的日志与差异化处理。
    /// </summary>
    public ConnectionCloseReason? LastCloseReason => _endpoint.CloseReason;

    /// <summary>
    /// 连接成功后是否自动发送 STARTDT_ACT 激活数据传输。默认 <c>true</c>，与原库
    /// <c>autostart</c> 行为一致；设为 <c>false</c> 则需手动调用 <see cref="StartDataTransferAsync"/>。
    /// </summary>
    public bool Autostart { get; set; } = true;

    #endregion 属性

    #region 事件

    /// <summary>收到 ASDU 的同步零拷贝事件（支持多订阅者）。示例：<c>client.AsduReceived += (in AsduView a) => { ... };</c></summary>
    public event AsduViewHandler AsduReceived
    {
        add
        {
            _asduReceived += value;
            if (_endpoint.Connection != null)
            {
                _endpoint.Connection.AsduReceived += value;
            }
        }
        remove
        {
            _asduReceived -= value;
            if (_endpoint.Connection != null)
            {
                _endpoint.Connection.AsduReceived -= value;
            }
        }
    }

    /// <summary>
    /// 上行原始报文（从站→本客户端）。参数为完整单个 APDU（含 0x68 APCI 头），
    /// 生命周期安全（已拷贝为 byte[]，可安全长期持有/入队/落盘）。
    /// 仅在有订阅者时才会分配该数组，无订阅者零开销。适用于报文调试/抓包。
    /// </summary>
    public event Action<byte[]> RawFrameReceived
    {
        add => _endpoint.RawFrameReceived += value;
        remove => _endpoint.RawFrameReceived -= value;
    }

    /// <summary>
    /// 下行原始报文（本客户端→从站）。参数为即将发送的完整单个 APDU（含 0x68 APCI 头），
    /// 覆盖 I/S/U 三类帧。生命周期安全（已拷贝为 byte[]）。
    /// 仅在有订阅者时分配，无订阅者零开销。
    /// </summary>
    public event Action<byte[]> RawFrameSent;

    /// <summary>连接层事件回调（STARTDT_CON 等，支持多订阅者）。</summary>
    public event Action<ApduConnectionEvent> ConnectionEvent
    {
        add
        {
            _connEvent += value;
            if (_endpoint.Connection != null)
            {
                _endpoint.Connection.EventHandler += value;
            }
        }
        remove
        {
            _connEvent -= value;
            if (_endpoint.Connection != null)
            {
                _endpoint.Connection.EventHandler -= value;
            }
        }
    }

    #endregion 事件

    #region 构造

    /// <summary>用独立参数构造客户端。</summary>
    /// <param name="host">从站主机名或 IP。</param>
    /// <param name="port">从站端口（默认 2404）。</param>
    /// <param name="apciParameters">APCI 参数（k/w/T0..T3）；null 时使用库默认。</param>
    /// <param name="alParameters">应用层参数；null 时使用库默认。</param>
    /// <param name="sslOption">客户端 TLS 配置；null 表示明文 TCP。</param>
    /// <param name="autostart">连接成功后是否自动 STARTDT（默认 true）。</param>
    public Iec104Client(string host, int port = 2404,
        APCIParameters apciParameters = null,
        ApplicationLayerParameters alParameters = null,
        ClientSslOption sslOption = null,
        bool autostart = true)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _apci = apciParameters ?? new APCIParameters();
        _al = alParameters ?? new ApplicationLayerParameters();
        _sslOption = sslOption;
        Autostart = autostart;
        Protocol = new Protocol(Iec60870Utility.Iec104);
        _endpoint = new ApduEndpoint(_apci, _al, this, TypeHandlers);
    }

    /// <summary>用 <see cref="Iec104Options"/> 构造客户端（Options 对象模式）。</summary>
    /// <param name="host">从站主机名或 IP。</param>
    /// <param name="port">从站端口。</param>
    /// <param name="options">配置项；构造时快照，后续修改原对象不影响本客户端。</param>
    public Iec104Client(string host, int port, Iec104Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options.Clone();
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _apci = snapshot.ApciParameters;
        _al = snapshot.AlParameters;
        _sslOption = snapshot.ClientSslOption;
        Autostart = snapshot.Autostart;
        CommandConfirmationTimeout = snapshot.CommandConfirmationTimeout;
        Protocol = new Protocol(Iec60870Utility.Iec104);
        _endpoint = new ApduEndpoint(_apci, _al, this, TypeHandlers);
    }

    #endregion 构造

    #region IIec104ApduSink

    ValueTask IIec104ApduSink.SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
    {
        // 下行原始报文（有订阅者才分配拷贝，无订阅者零开销）
        RawFrameSent?.Invoke(apdu.ToArray());
        return new ValueTask(base.SendAsync(apdu, cancellationToken));
    }

    bool IIec104ApduSink.IsConnected => Online;

    #endregion IIec104ApduSink

    #region 连接生命周期

    /// <summary>建立 TCP 连接（不自动 STARTDT）。</summary>
    /// <param name="cancellationToken">取消令牌（同时约束 T0 建连超时）。</param>
    /// <param name="configureConfig">可选的 TouchSocket 配置回调，在库默认配置（远程地址/SSL/收发日志插件）
    /// 之后执行，可追加插件、KeepAlive、缓冲等配置（用户回调追加式配置合并）。</param>
    /// <remarks>以 <see cref="Iec104Client"/> 类型调用时解析到本实现（额外完成 Setup/SSL/建连超时）；
    /// 基类-typed 引用（如 TouchSocket 内部）调用 <c>ConnectAsync(CancellationToken)</c> 则连接为纯网络握手。</remarks>
    public async Task ConnectAsync(CancellationToken cancellationToken = default,
        Action<TouchSocketConfig> configureConfig = null)
    {
        // 重连/重复调用：端点统一释放上一次连接遗留的 CTS / 状态机 / 定时器，避免资源泄漏。
        // closeAsync 供调度器在 T1/TESTFR 致命超时后回调（等效于旧 Task.Run 循环的关闭路径）。
        // onFailed 是「断开后必须执行」的宿主清理：不依赖 TouchSocket 的 OnTcpClosed
        // （实测该回调在部分关闭路径下不触发），保证挂起的命令确认等待者被及时唤醒。
        _endpoint.Rebuild(cancellationToken, conn =>
        {
            // 把连接前已订阅的事件转发到新建连接（支持"先订阅后连接"）
            if (_asduReceived != null)
            {
                conn.AsduReceived += _asduReceived;
            }

            if (_connEvent != null)
            {
                conn.EventHandler += _connEvent;
            }
            // 内置控制命令确认匹配器（多播订阅，不干扰用户订阅；无挂起命令时为空转）
            conn.AsduReceived += OnConfirmationAsdu;
        }, reason => CloseAsync("104 " + reason), OnConnectionFailed);

        _dataTransferStarted = false;

        var config = new TouchSocketConfig();
        config.SetRemoteIPHost(new IPHost($"{_host}:{_port}"));
        if (_sslOption != null)
        {
            config.SetClientSslOption(o =>
            {
                o.TargetHost = _sslOption.TargetHost;
                o.ClientCertificates = _sslOption.ClientCertificates;
                o.SslProtocols = _sslOption.SslProtocols;
                o.CheckCertificateRevocation = _sslOption.CheckCertificateRevocation;
                o.CertificateValidationCallback = _sslOption.CertificateValidationCallback;
            });
        }
        // 用户自定义配置在库默认项之后应用，可追加插件、日志、KeepAlive 等。
        // 配置只装载一次：重复 SetupAsync 会重建插件管理器，使 ReconnectionPlugin 等
        // 已注册插件实例失效（其内部 CTS 被 dispose，外部重连循环随后抛
        // ObjectDisposedException）。远程地址/SSL 均来自构造参数，重连时无需重装。
        if (!_setupDone)
        {
            configureConfig?.Invoke(config);
            await SetupAsync(config).ConfigureAwait(false);
            _setupDone = true;
        }
        // SetupAsync 之后基类 Logger 已由容器解析，转发给端点供定时器循环记录异常。
        _endpoint.Logger = Logger;
        // TouchSocket 4.x 的 ConnectAsync 不再接收超时参数，用取消令牌保留 IEC104 T0 建连超时语义。
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(_apci.T0 * 1000));
        await base.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

        if (Autostart)
        {
            // T0 覆盖「TCP 握手 + STARTDT 完成」全过程（APCIParameters 定义）。
            // 原实现用原始 cancellationToken，STARTDT_CON 迟迟不来时 ConnectAsync
            // 不受 T0 约束；现统一用 connectTimeout.Token。
            await StartDataTransferAsync(connectTimeout.Token).ConfigureAwait(false);
        }

        // 定时器循环已内建于 ApduEndpoint（Iec104TimerScheduler，单 Timer 自调度），
        // 无需再启动常驻 Task.Run 轮询任务；连接关闭时随端点确定性释放。
    }

    /// <summary>
    /// 连接建立时装载 IEC104 数据处理适配器（定长头三态解析 + 滑动重同步）。
    /// 捕获开关延迟到每帧解析时读取端点订阅状态，故连接前订阅同样生效。
    /// </summary>
    protected override Task OnTcpConnecting(ConnectingEventArgs e)
    {
        SetAdapter(new Iec104Adapter(() => _endpoint.HasRawFrameSubscribers));
        return base.OnTcpConnecting(e);
    }

    /// <summary>发送 STARTDT_ACT 并等待 STARTDT_CON，激活数据传输。</summary>
    /// <remarks>幂等：已激活或 <see cref="Autostart"/> 已自动触发时，重复调用为无操作。</remarks>
    public async Task StartDataTransferAsync(CancellationToken cancellationToken = default)
    {
        if (_dataTransferStarted)
        {
            return;
        }

        // 必须在 STARTDT_CON 确认成功后再置位，否则超时抛异常会使标志卡在 true，
        // 重连/重试时 StartDataTransferAsync 变为 no-op，连接永不激活。
        try
        {
            using var link = _endpoint.LinkScoped(cancellationToken);
            await _endpoint.Connection.StartDataTransferAsync(link.Token).AsTask().ConfigureAwait(false);
            _dataTransferStarted = true;
        }
        catch
        {
            _dataTransferStarted = false; // 允许重试
            throw;
        }
    }

    /// <summary>发送 STOPDT_ACT 并等待 STOPDT_CON。成功后复位激活标志，允许再次 STARTDT 重新激活。</summary>
    public async Task StopDataTransferAsync(CancellationToken cancellationToken = default)
    {
        using var link = _endpoint.LinkScoped(cancellationToken);
        await _endpoint.Connection.StopDataTransferAsync(link.Token).AsTask().ConfigureAwait(false);

        // 与 StartDataTransferAsync 的失败复位对称：STOPDT_CON 已把状态机置为非激活，
        // 若不复位标志，再次 StartDataTransferAsync 会被 _dataTransferStarted 守卫
        // 静默 no-op，链路永久无法重新激活。
        _dataTransferStarted = false;
    }

    #endregion 连接生命周期

    #region 发送

    /// <summary>发送一个 ASDU（I 帧）。k 窗口满时异步背压等待，不阻塞线程。</summary>
    /// <exception cref="InvalidOperationException">尚未连接（未调用 <see cref="ConnectAsync"/>）时抛出，
    /// 而不是 NRE —— 调用方需要能区分「忘了连接」与库缺陷。</exception>
    public async Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default)
    {
        var connection = _endpoint.Connection
            ?? throw new InvalidOperationException("连接未建立：请先调用 ConnectAsync");

        using var link = _endpoint.LinkScoped(cancellationToken);
        await connection.SendAsduAsync(asdu, link.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一条控制命令 ASDU，并异步等待其 ACTIVATION_CON（或否定确认）。
    /// 内部用 Select 位区分"预发"与"执行"的确认，二者可顺序串行调用；
    /// 超时/取消不关闭连接，挂起槽位自动清理，迟到确认丢弃不串台。
    /// </summary>
    /// <param name="cot">发起控制用 ACTIVATION。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="io">控制命令信息对象（SingleCommand/DoubleCommand/Setpoint...），其 Select 位决定预发或执行。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>服务端回送的确认（含是否否定）。</returns>
    /// <exception cref="TimeoutException">超过 <see cref="CommandConfirmationTimeout"/> 未收到匹配确认时抛出，避免调用方永久挂起。</exception>
    public Task<ControlConfirmation> SendControlCommandAndWaitAsync(
        CauseOfTransmission cot, int ca, InformationObject io,
        CancellationToken cancellationToken = default)
        => Iec104MasterExtension.SendControlCommandAndWaitAsync(this, cot, ca, io, cancellationToken);

    /// <summary>
    /// 内置确认匹配回调：将收到的 ACT-CON 交给 <see cref="ControlConfirmationTracker"/> 唤醒等待者。
    /// </summary>
    private void OnConfirmationAsdu(in AsduView view)
        => _confirmationTracker.TryComplete(in view, _al);

    #endregion 发送

    #region 断开与清理

    /// <summary>主动断开连接。</summary>
    public async Task DisconnectAsync()
    {
        _endpoint.MarkIntentionalClose();
        _endpoint.CancelLife();
        try
        {
            await CloseAsync("client disconnect").ConfigureAwait(false);
        }
        catch { /* ignore */ }
        await CleanupAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
    {
        if (e.RequestInfo is IecApdu apdu && _endpoint.Connection != null)
        {
            if (!await _endpoint.HandleReceivedAsync(apdu).ConfigureAwait(false))
            {
                // 协议错误已由端点统一处理（派发 ConnectionClosed + 异步关闭传输）。
                // 此处【不能】再同步 CloseAsync("protocol error")：在 TouchSocket 收包回调
                // 内部同步关闭会走到适配器拆解的中途，实测导致套接字仍报 Online、
                // TouchSocket 的 Closed 事件不再触发 —— 链路已死，但应用层既收不到
                // ConnectionClosed、IsActivated 也仍为 true（僵尸链路，见压测报告 §2.1）。
                LogProtocolError();
                return;
            }
        }

        await base.OnTcpReceived(e).ConfigureAwait(false);
    }

    /// <summary>协议错误日志（含具体分支原因，便于现场自证）。</summary>
    private void LogProtocolError()
    {
        var detail = _endpoint.Connection?.LastProtocolErrorDetail;

        Logger?.Warning("CS104 client: protocol error -> closing connection"
            + (string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")"));
    }

    /// <summary>
    /// 连接失效后的宿主清理：与 <see cref="OnTcpClosed"/> 里的 <see cref="CleanupAsync"/>
    /// **完全等价**（含释放调度器/状态机），不依赖 TouchSocket 的 <c>OnTcpClosed</c>。
    /// 必须幂等：<c>OnTcpClosed</c> 正常触发时会再调一次。
    /// </summary>
    private void OnConnectionFailed()
    {
        // 取消生命周期令牌，让定时器循环与 k 窗口背压退出（不等 OnTcpClosed）
        _endpoint.CancelLife();
        _confirmationTracker.CompleteAllCanceled();

        // 关键：失效路径也必须释放共享调度器注册与状态机。
        // 只做上面两步是「修一半」：OnTcpClosed 不触发时（本次修复的前提场景），
        // SharedTimeoutScheduler 的 _liveCount 不会递减、Timer 不关、_sendLock 不释放。
        _endpoint.CleanupConnection();
    }

    /// <inheritdoc/>
    protected override async Task OnTcpClosed(ClosedEventArgs e)
    {
        // 仅当连接因远端关闭/超时/协议错误等非主动原因断开时，向订阅方派发 ConnectionClosed 事件。
        // 主动 DisconnectAsync 已置 intentional close，不重复通知。
        _endpoint.NotifyClosedIfUnexpected();
        await CleanupAsync().ConfigureAwait(false);
        await base.OnTcpClosed(e).ConfigureAwait(false);
    }

    private ValueTask CleanupAsync()
    {
        _endpoint.CleanupConnection();

        // 连接终止：唤醒所有等待命令确认的调用者（抛 TaskCanceledException），
        // 避免其悬挂至 CommandConfirmationTimeout 超时——断开后确认永远不会到达。
        _confirmationTracker.CompleteAllCanceled();

        // 注意：此处【不】释放生命周期 CTS。ReconnectionPlugin 等外部重连器可能在断开回调
        // 尚未走完时就发起下一次 ConnectAsync（Rebuild 统一释放旧 CTS 并重建），若在此
        // 处释放会与新连接的 LinkScoped 竞态 → ObjectDisposedException。
        // CTS 生命周期由 Rebuild/DisposeAsync 单点管理（外部重连调用是串行的）。
        return default;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _endpoint.FinalizeSession();
        base.Dispose();
    }

    /// <summary>
    /// 同步 <c>Dispose()</c> 兜底：<see cref="TcpClient"/> 的同步释放只走基类
    /// <c>SafetyDispose</c>（关闭传输），不会经过 <see cref="DisposeAsync"/>，
    /// 因此 <c>using (var client = new Iec104Client(...))</c> 原先会泄漏
    /// <see cref="_endpoint"/>（生命周期 CTS、连接状态机、共享调度器注册）。
    /// 这里补上与 <see cref="DisposeAsync"/> 等价的端点终结（幂等）。
    /// </summary>
    protected override void SafetyDispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _endpoint?.FinalizeSession();
            }
            catch
            {
                // Dispose 约定：释放路径不得抛出
            }
        }

        base.SafetyDispose(disposing);
    }

    #endregion 断开与清理
}
