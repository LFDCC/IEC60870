//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 从站（Server）。
/// </summary>
/// <remarks>
/// <para><b>三种传输</b>（按启动方式选择，对外 API 一致）：
/// <list type="bullet">
///   <item><see cref="StartAsync(int, CancellationToken)"/> —— TCP 监听，<b>支持多主站并发</b>。</item>
///   <item><see cref="StartAsync(SerialPort, CancellationToken)"/> —— 串口（RS-485），单链路 + 地址过滤。</item>
///   <item><see cref="StartAsync(Stream, CancellationToken)"/> —— 任意字节流，单链路 + 地址过滤。</item>
/// </list>
/// </para>
/// <para><b>为什么不再继承 <c>TcpService</c></b>：串口是单链路、没有「连接建立」事件，
/// 无法套用 TouchSocket 的 TCP 多连接模型。改为「持有传输 + 组合」后，
/// 两种模式共用同一份 <see cref="SessionCore"/> 链路逻辑。
/// </para>
/// <para><b>103 的多会话要求</b>：现场常有多套主站（监控后台、保护信息子站、
/// 便携式试验仪）同时连同一装置，故 TCP 模式必须多会话。</para>
/// </remarks>
public sealed class Iec103Server : IIec103Slave
{
    private readonly Iec103Options _options;
    private readonly Action<TouchSocketConfig>? _configureConfig;
    private readonly string _protocolName;
    private readonly ConcurrentDictionary<long, IIec103Session> _sessions = new();
    private long _nextSessionId;

    private Iec103TcpHost? _tcpHost;
    private SessionCore? _streamCore;
    private Iec103StreamSession? _streamSession;
    private int _port;
    private bool _disposed;
    private readonly object _startLock = new();

    /// <summary>创建从站实例。</summary>
    /// <param name="options">组件配置；为 null 时使用默认配置。</param>
    /// <param name="configureConfig">TouchSocket 配置回调（插件、日志等；仅 TCP 模式生效）。</param>
    public Iec103Server(Iec103Options? options = null, Action<TouchSocketConfig>? configureConfig = null)
    {
        _options = (options ?? new Iec103Options()).Clone();
        _configureConfig = configureConfig;
        _protocolName = Iec103Protocol.Iec103;
    }

    /// <summary>
    /// 日志接口。为 null 时不记录（库不做任何隐式输出）。
    /// </summary>
    /// <remarks>
    /// 以前由 <c>TcpService</c> 基类提供；去继承化后需自行声明，
    /// 保持外部设置方式不变（`server.Logger = ...`）。
    /// </remarks>
    public ILog? Logger { get; set; }

    /// <summary>链路层参数快照（组件构造期固定）。</summary>
    internal Iec103LinkParameters Iec103LinkParameters => _options.Link;

    /// <summary>未识别 ASDU 处理策略。</summary>
    internal Iec103UnknownTypePolicy UnknownTypePolicy => _options.UnknownTypePolicy;

    /// <inheritdoc />
    public string ProtocolName => _protocolName;

    /// <inheritdoc />
    public Iec103PrivateTypeRegistry PrivateTypes { get; } = new();

    /// <summary>TCP 监听端口（仅 TCP 模式有效）。</summary>
    public int Port => _port;

    /// <summary>当前传输模式。</summary>
    public Iec103ServerMode Mode { get; private set; } = Iec103ServerMode.Stopped;

    /// <inheritdoc />
    public IReadOnlyList<IIec103Session> Sessions => _sessions.Values.ToList();

    /// <summary>在线会话数（TCP 模式为连接数；串口/Stream 模式建链后为 1）。</summary>
    public int SessionCount => _sessions.Count;

    /// <inheritdoc />
    public event Iec103ServerAsduHandler? AsduReceived;

    /// <inheritdoc />
    public event Action<IIec103Session>? SessionOpened;

    /// <inheritdoc />
    public event Action<IIec103Session, string?>? SessionClosed;

    /// <inheritdoc />
    public event Action<IIec103Session, byte[]>? RawFrameReceived;

    /// <summary>收到未识别 ASDU（兜底，绝不丢帧）。</summary>
    public event Action<IIec103Session, Iec103UnknownAsdu>? UnknownAsduReceived;

    /// <summary>收到未识别通用分类条目（保留原始 GID）。</summary>
    public event Action<IIec103Session, Iec103GenericUnknownEntry>? UnknownGenericDataReceived;

    /// <summary>帧被舍弃（校验错/半帧超时等）。</summary>
    public event Action<IIec103Session, Iec103FrameRejectedEventArgs>? FrameRejected;

    /// <summary>
    /// 链路层帧追踪：收发<b>每个</b> FT1.2 帧（含固定帧/单字符 ACK），用于现场诊断。
    /// </summary>
    /// <remarks>
    /// 参数为 <c>(会话, 方向, 帧)</c>，方向取 <c>"RX"</c> / <c>"TX"</c>。
    /// 与 <see cref="RawFrameReceived"/> 的区别：后者只报可变帧（用户数据），
    /// 本事件覆盖链路层交互，复位/召唤失败时可据以判断从站到底回了什么。
    /// </remarks>
    public event Action<IIec103Session, string, Ft12Frame>? FrameTraced;

    // ═══════════════════════════════════════════════════════════════
    //  启动 / 停止
    // ═══════════════════════════════════════════════════════════════

    /// <summary>以 TCP 方式启动监听（支持多主站并发）。</summary>
    /// <param name="port">监听端口。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        ThrowIfDisposedState();

        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口须在 1-65535 之间");
        }

        lock (_startLock)
        {
            EnsureNotStarted();
            // 立即占位，避免两个并发 StartAsync 都通过检查后重复创建 TcpService
            Mode = Iec103ServerMode.Starting;
        }

        _port = port;
        var host = new Iec103TcpHost(this, _options, _configureConfig);

        try
        {
            await host.StartAsync(port, ct).ConfigureAwait(false);
            _tcpHost = host;
            Mode = Iec103ServerMode.Tcp;
            Logger?.Info($"103 从站已启动（TCP），监听端口 {port}");
        }
        catch
        {
            host.Dispose();
            _tcpHost = null;
            lock (_startLock)
            {
                Mode = Iec103ServerMode.Stopped;
            }

            throw;
        }
    }

    /// <summary>以串口方式启动（RS-485 单链路，启用地址过滤）。</summary>
    /// <param name="port">串口实例；未打开时由库负责打开。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>串口对象所有权归调用方：库只 <c>Close</c> 不 <c>Dispose</c>，关闭后可重新 <c>Open</c> 复用。</remarks>
    public Task StartAsync(SerialPort port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(port);
        return StartStreamCoreAsync(new SerialLinkTransport(port), ct, $"串口 {port.PortName}");
    }

    /// <summary>以任意字节流方式启动（单链路，启用地址过滤）。</summary>
    /// <param name="stream">双向字节流。</param>
    /// <param name="ct">取消令牌。</param>
    public Task StartAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return StartStreamCoreAsync(new StreamLinkTransport(stream), ct, "字节流");
    }

    private async Task StartStreamCoreAsync(IIec103LinkTransport transport, CancellationToken ct, string modeName)
    {
        ThrowIfDisposedState();
        lock (_startLock)
        {
            EnsureNotStarted();
            Mode = Iec103ServerMode.Starting;
        }

        try
        {
            // 串口模式：若传输是串口且未打开，先打开
            if (transport is SerialLinkTransport serial)
            {
                serial.Open();
            }

            var session = new Iec103StreamSession(this, transport, _options.Link, logger: msg => Logger?.Debug(msg));
            _streamCore = session.Core;
            _streamSession = session;

            _sessions[session.SessionId] = session;
            session.Core.Start();

            Mode = Iec103ServerMode.SerialPort;
            SafeInvoke("SessionOpened", () => SessionOpened?.Invoke(session));

            Logger?.Info($"103 从站已启动（{modeName}，链路地址 {string.Join(",", _options.Link.LinkAddresses)}），单链路模式");

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch
        {
            // 回滚已启动的读循环与会话表条目：异常发生在 Core.Start() 之后时，
            // 仅 Dispose 传输会让读循环永远阻塞在一个已弃用的流上，且 SessionCount 不减。
            var core = _streamCore;
            _streamCore = null;

            if (core is not null)
            {
                try
                {
                    await core.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger?.Warning($"回滚 Stream/串口会话核心异常: {ex.Message}");
                }
            }

            if (_streamSession is not null)
            {
                _sessions.TryRemove(_streamSession.SessionId, out _);
                _streamSession = null;
            }

            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.Warning($"回滚 Stream/串口传输异常: {ex.Message}");
            }

            lock (_startLock)
            {
                Mode = Iec103ServerMode.Stopped;
            }

            throw;
        }
    }

    /// <summary>停止监听并断开全部会话。</summary>
    public async Task StopAsync()
    {
        // 显式收束每个会话的链路核心。TouchSocket 关闭连接不保证等待
        // OnTcpClosed 异步回调，仅靠该回调 dispose 会泄漏 CTS / 读循环 / 队列
        // （CS104 已就同一问题加过 SafetyDispose 兜底）。
        foreach (var session in _sessions.Values.ToList())
        {
            if (session is Iec103Session tcpSession)
            {
                await tcpSession.DisposeCoreAsync().ConfigureAwait(false);
            }

            UnregisterSessionById(session.SessionId, "从站停止");
        }

        if (_tcpHost is not null)
        {
            await _tcpHost.StopAsync().ConfigureAwait(false);
            _tcpHost.Dispose();
            _tcpHost = null;
        }

        SessionCore? streamCore;
        lock (_startLock)
        {
            streamCore = _streamCore;
            _streamCore = null;
            _streamSession = null;
        }

        if (streamCore is not null)
        {
            await streamCore.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        Mode = Iec103ServerMode.Stopped;
        Logger?.Info("103 从站已停止");
    }

    /// <summary>停止监听并断开全部会话（接口签名）。</summary>
    Task IIec103Slave.StopAsync(CancellationToken ct) => StopAsync();

    // ═══════════════════════════════════════════════════════════════
    //  上送
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task BroadcastAsync(Asdu asdu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);

        if (_streamCore is not null)
        {
            // 串口/Stream：单链路无广播概念，直接入队
            _streamCore.Enqueue(asdu);
            return;
        }

        // 原实现用 .AsTask().GetAwaiter().GetResult() 同步等待，在
        // SynchronizationContext/线程池受限时会阻塞死锁（sync-over-async），
        // 且与「返回 Task 的方法」契约不符：改为真正的异步等待。
        foreach (var session in _sessions.Values)
        {
            if (session.LinkEstablished)
            {
                await session.SendAsduAsync(asdu, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task SendToAsync(IIec103Session session, Asdu asdu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);
        ArgumentNullException.ThrowIfNull(session);

        if (session is not Iec103Session && session is not Iec103StreamSession)
        {
            throw new ArgumentException("会话实例不属于本从站", nameof(session));
        }

        return session.SendAsduAsync(asdu, ct).AsTask();
    }

    /// <summary>上送保护事件（TI=2，1 级队列）。</summary>
    public Task SendProtectionEventAsync(
        byte ca, byte fun, byte inf, DpiQuality dpi, int relativeTimeMs, int fan, DateTime time, CancellationToken ct = default)
    {
        var asdu = Asdu.ProtectionEvent(ca, fun, inf, dpi, relativeTimeMs, fan, Cp56Time2a.FromDateTime(time), CotCodes.Spontaneous);
        return BroadcastAsync(asdu, ct);
    }

    /// <summary>上送带时标监视信息（TI=1，1 级队列）。</summary>
    public Task SendTimeTaggedAsync(
        byte ca, byte fun, byte inf, DpiQuality dpi, DateTime time, byte cot = CotCodes.Spontaneous, CancellationToken ct = default)
    {
        var asdu = Asdu.TimeTaggedMessage(ca, fun, inf, dpi, Cp56Time2a.FromDateTime(time), cot);
        return BroadcastAsync(asdu, ct);
    }

    /// <summary>上送测量值（TI=9，2 级队列）。</summary>
    public Task SendMeasurementAsync(byte ca, byte fun, byte inf, short value, CancellationToken ct = default)
    {
        var asdu = Asdu.Measurement(ca, fun, inf, value);
        return BroadcastAsync(asdu, ct);
    }

    /// <summary>上送任意 ASDU 原始字节（厂商私有类型直接构造字节时使用）。</summary>
    public async Task BroadcastRawAsduAsync(ReadOnlyMemory<byte> asduBytes, CancellationToken ct = default)
    {
        var copy = asduBytes.ToArray();

        if (_streamCore is not null)
        {
            _streamCore.EnqueueRaw(copy, isClass1: true);
            return;
        }

        // 同 BroadcastAsync：去掉 .AsTask().GetAwaiter().GetResult() 的 sync-over-async
        foreach (var session in _sessions.Values)
        {
            if (session.LinkEstablished)
            {
                await session.SendRawAsduAsync(copy, ct).ConfigureAwait(false);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  内部协作（供 TCP 宿主与 Stream 会话回调）
    // ═══════════════════════════════════════════════════════════════

    internal long NextSessionId() => Interlocked.Increment(ref _nextSessionId);

    internal void RegisterSession(IIec103Session session)
    {
        _sessions[session.SessionId] = session;
        // 订阅者抛异常不能打断会话建立；与 AsduReceived/FrameTraced 等事件保持一致。
        SafeInvoke("SessionOpened", () => SessionOpened?.Invoke(session));
    }

    internal void UnregisterSession(IIec103Session session, string? reason)
    {
        // 只有真正移除了条目才发事件，保证会话关闭事件幂等。
        if (_sessions.TryRemove(session.SessionId, out _))
        {
            SafeInvoke("SessionClosed", () => SessionClosed?.Invoke(session, reason));
        }
    }

    internal void RaiseAsduReceived(IIec103Session session, in Iec103AsduView view)
        => AsduReceived?.Invoke(session, in view);

    internal void RaiseRawFrameReceived(IIec103Session session, byte[] raw)
        => SafeInvoke("RawFrameReceived", () => RawFrameReceived?.Invoke(session, raw));

    internal void RaiseUnknownAsdu(IIec103Session session, Iec103UnknownAsdu unknown)
        => SafeInvoke("UnknownAsduReceived", () => UnknownAsduReceived?.Invoke(session, unknown));

    internal void RaiseFrameRejected(IIec103Session session, Iec103FrameRejectedEventArgs rejected)
        => SafeInvoke("FrameRejected", () => FrameRejected?.Invoke(session, rejected));

    internal void RaiseFrameTraced(IIec103Session session, string direction, Ft12Frame frame)
        => SafeInvoke("FrameTraced", () => FrameTraced?.Invoke(session, direction, frame));

    /// <summary>
    /// 同步订阅者回调的异常隔离（与 <see cref="Iec103Client"/> 的 SafeInvoke 对称）。
    /// 订阅者抛异常若冒泡到 <c>SessionCore</c> 的读循环外层 catch，该会话会直接退出且
    /// 不通知任何一方 —— 会话静默死亡。主站早已隔离，从站原先全是裸 <c>?.Invoke</c>，
    /// 属「只修一半」的对称缺口。
    /// </summary>
    private void SafeInvoke(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger?.Error($"CS103 SLAVE: [{name}] 回调异常已隔离，会话继续运行: {ex}");
        }
    }

    // ── 按 sessionId 的便捷转发（供 SessionCore 使用，避免其持有会话对象）──

    internal IIec103Session? FindSession(long sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    internal void RaiseAsduReceivedById(long sessionId, in Iec103AsduView view)
    {
        var session = FindSession(sessionId);
        if (session is not null)
        {
            // in 参数无法被 lambda 捕获，这里直接 try/catch（与 SafeInvoke 等价）
            try
            {
                AsduReceived?.Invoke(session, in view);
            }
            catch (Exception ex)
            {
                Logger?.Error($"CS103 SLAVE: [AsduReceived] 回调异常已隔离，会话继续运行: {ex}");
            }
        }
    }

    internal void RaiseRawFrameReceivedById(long sessionId, byte[] raw)
    {
        var session = FindSession(sessionId);
        if (session is null)
        {
            return;
        }

        SafeInvoke("RawFrameReceived", () => RawFrameReceived?.Invoke(session, raw));

        // 会话级事件也要触发（IIec103Session.RawFrameReceived 是对外契约，
        // 此前从未 Invoke，订阅者永远收不到）。
        SafeInvoke("SessionRawFrameReceived", () =>
        {
            if (session is Iec103Session tcp)
            {
                tcp.RaiseRawFrameReceived(raw);
            }
            else if (session is Iec103StreamSession stream)
            {
                stream.RaiseRawFrameReceived(raw);
            }
        });
    }

    /// <summary>转发未识别的通用分类条目到服务端事件。</summary>
    internal void RaiseUnknownGenericById(long sessionId, Iec103GenericUnknownEntry entry)
    {
        var session = FindSession(sessionId);
        if (session is not null)
        {
            SafeInvoke("UnknownGenericDataReceived", () => UnknownGenericDataReceived?.Invoke(session, entry));
        }
    }

    internal void RaiseUnknownAsduById(long sessionId, Iec103UnknownAsdu unknown)
    {
        var session = FindSession(sessionId);
        if (session is not null)
        {
            SafeInvoke("UnknownAsduReceived", () => UnknownAsduReceived?.Invoke(session, unknown));
        }
    }

    internal void RaiseFrameRejectedById(long sessionId, Iec103FrameRejectedEventArgs rejected)
    {
        var session = FindSession(sessionId);
        if (session is not null)
        {
            SafeInvoke("FrameRejected", () => FrameRejected?.Invoke(session, rejected));
        }
    }

    internal void RaiseFrameTracedById(long sessionId, string direction, Ft12Frame frame)
    {
        var session = FindSession(sessionId);
        if (session is not null)
        {
            SafeInvoke("FrameTraced", () => FrameTraced?.Invoke(session, direction, frame));
        }
    }

    /// <summary>
    /// 注销会话并派发一次 SessionClosed（幂等）。断开事件、StopAsync、
    /// OnTcpClosed 都走这里，避免「事件发了但 SessionCount 不减」或重复派发。
    /// </summary>
    internal void UnregisterSessionById(long sessionId, string? reason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            // 裸 Invoke 会让订阅者异常冒泡打断 StopAsync/关闭路径，导致会话无法停止。
            SafeInvoke("SessionClosed", () => SessionClosed?.Invoke(session, reason));
        }
    }

    /// <summary>
    /// 串口/字节流链路断开后复位状态，允许再次 StartAsync。
    /// TCP 模式下会话断开不影响服务端监听，故不做处理。
    /// </summary>
    /// <param name="lostCore">
    /// 报告链路丢失的核心。只有它仍是当前 <c>_streamCore</c> 时才处理，
    /// 防止旧核心的幽灵回调把重连后的新核心置空。
    /// </param>
    internal async Task NotifyStreamLinkLost(SessionCore? lostCore = null)
    {
        SessionCore? core;

        lock (_startLock)
        {
            if (Mode != Iec103ServerMode.SerialPort)
            {
                return;
            }

            core = _streamCore;
            if (core is null)
            {
                return;
            }

            if (lostCore is not null && !ReferenceEquals(core, lostCore))
            {
                // 幽灵回调：当前核心已经是新的，不能动。
                return;
            }

            Mode = Iec103ServerMode.Stopped;
            _streamCore = null;
            _streamSession = null;
        }

        // 取出后必须真正释放：CTS / 写锁 / 读循环都在核心内部，
        // 旧实现只把字段置 null，等于把资源交给 GC 但读循环仍在跑。
        try
        {
            await core.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.Warning($"释放 Stream/串口会话核心异常: {ex.Message}");
        }
    }

    internal void RaiseFrameTraceText(long sessionId, string text)
    {
        _ = sessionId;
        _ = text;
        // 文本级追踪由 Iec103Client.TraceFrame 提供；从站侧用 FrameTraced 结构化事件
    }

    private void EnsureNotStarted()
    {
        if (Mode != Iec103ServerMode.Stopped)
        {
            throw new InvalidOperationException($"从站已启动（{Mode}），请先 StopAsync");
        }
    }

    private void ThrowIfDisposedState()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Iec103Server));
        }
    }

    /// <summary>释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.Warning($"停止从站时异常: {ex.Message}");
        }
    }
}

/// <summary>从站传输模式。</summary>
public enum Iec103ServerMode
{
    /// <summary>未启动。</summary>
    Stopped = 0,

    /// <summary>TCP 监听（多主站并发）。</summary>
    Tcp = 1,

    /// <summary>串口/字节流（单链路）。</summary>
    SerialPort = 2,

    /// <summary>启动中（并发 StartAsync 的占位状态，不应被外部观察到）。</summary>
    Starting = 3,
}

/// <summary>
/// 103 从站的 TCP 宿主：只负责建连、建会话、转发事件。
/// </summary>
/// <remarks>
/// 把 TouchSocket 的 <c>TcpService</c> 继承限制在这个私有类型内，
/// 使 <see cref="Iec103Server"/> 本身不依赖具体传输——
/// 这样串口模式才能复用同一套上层 API 与事件契约。
/// </remarks>
internal sealed class Iec103TcpHost : TcpService<Iec103Session>
{
    private readonly Iec103Server _server;
    private readonly Iec103Options _options;
    private readonly Action<TouchSocketConfig>? _configureConfig;

    public Iec103TcpHost(Iec103Server server, Iec103Options options, Action<TouchSocketConfig>? configureConfig)
    {
        _server = server;
        _options = options;
        _configureConfig = configureConfig;
    }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        var config = new TouchSocketConfig();
        config.SetListenIPHosts(new IPHost(port));
        _configureConfig?.Invoke(config);

        await SetupAsync(config).ConfigureAwait(false);
        await base.StartAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Iec103Session NewClient()
    {
        var id = _server.NextSessionId();
        return new Iec103Session(id, _server, _options.Link);
    }
}

/// <summary>
/// 103 从站 TCP 会话：TouchSocket 宿主适配层，链路逻辑全部委托 <see cref="SessionCore"/>。
/// </summary>
public sealed class Iec103Session : TcpSessionClient, IIec103Session
{
    private readonly long _sessionId;
    private readonly Iec103Server _server;
    private readonly Iec103LinkParameters _parameters;
    private SessionCore? _core;
    private TcpSessionLinkTransport? _transport;
    private bool _closeRequested;

    /// <summary>由 <see cref="Iec103TcpHost"/> 工厂创建。</summary>
    internal Iec103Session(long sessionId, Iec103Server server, Iec103LinkParameters parameters)
    {
        _sessionId = sessionId;
        _server = server;
        _parameters = parameters;
        Protocol = new Protocol(Iec103Protocol.Iec103);
    }

    /// <inheritdoc />
    public long SessionId => _sessionId;

    /// <summary>对端地址（<c>IP:Port</c>）。</summary>
    public new string RemoteEndPoint => base.RemoteEndPoint?.ToString() ?? "unknown";

    /// <inheritdoc />
    public byte LinkAddress => _core?.LinkAddress ?? 0;

    /// <inheritdoc />
    public bool LinkEstablished => _core?.LinkEstablished ?? false;

    /// <inheritdoc />
    public DateTime LastActivityUtc => _core?.LastActivityUtc ?? DateTime.UtcNow;

    /// <inheritdoc />
    public event Action<byte[]>? RawFrameReceived;

    /// <summary>由服务端在收到帧时调用，触发本会话的 RawFrameReceived。</summary>
    internal void RaiseRawFrameReceived(byte[] raw) => RawFrameReceived?.Invoke(raw);

    /// <summary>链路引擎（诊断/测试可见）。</summary>
    internal LinkLayerEngine? Engine => _core?.Engine;

    /// <inheritdoc />
    public ValueTask SendAsduAsync(Asdu asdu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);
        _core?.Enqueue(asdu);
        return default;
    }

    /// <inheritdoc />
    public ValueTask SendRawAsduAsync(ReadOnlyMemory<byte> asduBytes, CancellationToken ct = default)
    {
        _core?.EnqueueRaw(asduBytes.ToArray(), isClass1: true);
        return default;
    }

    /// <summary>入队一条 ASDU（兼容内部旧调用）。</summary>
    internal Task EnqueueAndSendAsync(Asdu asdu, CancellationToken ct)
    {
        _core?.Enqueue(asdu);
        return Task.CompletedTask;
    }

    /// <summary>入队原始 ASDU 字节（兼容内部旧调用）。</summary>
    internal void EnqueueRaw(byte[] asduBytes, bool isClass1) => _core?.EnqueueRaw(asduBytes, isClass1);

    /// <inheritdoc />
    protected override async Task OnTcpConnected(ConnectedEventArgs e)
    {
        _transport = new TcpSessionLinkTransport(this);

        _core = new SessionCore(
            _server,
            _transport,
            _parameters,
            _sessionId,
            msg => Logger?.Debug(msg),
            filterByAddress: false); // TCP：一条连接对应一个主站，不做地址过滤

        _server.RegisterSession(this);
        _core.Start();

        await base.OnTcpConnected(e).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
    {
        if (!e.Memory.IsEmpty)
        {
            _transport?.PushData(e.Memory.Span);
        }

        await base.OnTcpReceived(e).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <summary>显式释放会话链路核心（StopAsync 兜底，幂等）。</summary>
    internal async Task DisposeCoreAsync()
    {
        if (_core is not null)
        {
            await _core.DisposeAsync().ConfigureAwait(false);
            _core = null;
        }
    }

    protected override async Task OnTcpClosed(ClosedEventArgs e)
    {
        // 必须先把「传输已关闭」告诉链路核心，再 dispose 它。
        // 此前 TcpSessionLinkTransport.NotifyClosed 全仓库无调用者，
        // 导致 TCP 模式下 SessionCore.OnTransportClosed 永远不执行：
        // 读循环收不到 Complete 信号、LinkEstablished 不复位、
        // 与串口/Stream（传输自己 RaiseClosed）语义不一致。
        // 注意顺序：DisposeCoreAsync 会退订 TransportClosed，先通知才不会白通知。
        try
        {
            _transport?.NotifyClosed(e.Exception?.Message ?? "对端关闭", e.Exception);
        }
        catch (Exception ex)
        {
            // 订阅方（SessionClosed 用户回调等）抛异常不能打断后面的收尾，
            // 否则 dispose 与注销都被跳过，会话对象泄漏（NotifyClosed 内部是裸 Invoke）
            Logger?.Warning($"[会话 {SessionId}] 传输关闭通知异常已隔离: {ex.Message}");
        }

        await DisposeCoreAsync().ConfigureAwait(false);
        _server.UnregisterSessionById(SessionId, e.Exception?.Message ?? "对端关闭");

        await base.OnTcpClosed(e).ConfigureAwait(false);
    }

    /// <summary>请求关闭会话（服务端主动停止时调用）。</summary>
    internal async Task RequestCloseAsync(string reason)
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;

        try
        {
            await CloseAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.Debug($"[会话 {_sessionId}] 关闭异常: {ex.Message}");
        }
    }
}

/// <summary>
/// 103 从站串口/字节流会话：单链路伪会话（实现 <see cref="IIec103Session"/>）。
/// </summary>
/// <remarks>
/// 串口没有「多连接」概念，但上层 API（<c>Sessions</c>、<c>SendToAsync</c>、
/// 事件参数）需要统一的会话对象，故提供此伪会话。
/// <see cref="SessionId"/> 固定为 1，<see cref="LinkAddress"/> 取配置的本站地址。
/// </remarks>
public sealed class Iec103StreamSession : IIec103Session
{
    private readonly Iec103Server _server;

    /// <summary>构造串口/字节流伪会话。</summary>
    internal Iec103StreamSession(
        Iec103Server server,
        IIec103LinkTransport transport,
        Iec103LinkParameters parameters,
        Action<string>? logger)
    {
        _server = server;
        Core = new SessionCore(
            server,
            transport,
            parameters,
            sessionId: 1,
            logger,
            filterByAddress: true); // 串口/Stream：RS-485 总线，启用地址过滤
    }

    /// <summary>会话核心（诊断/测试可见）。</summary>
    internal SessionCore Core { get; }

    /// <inheritdoc />
    public long SessionId => 1;

    /// <inheritdoc />
    public string RemoteEndPoint => Core.RemoteEndPoint;

    /// <inheritdoc />
    public byte LinkAddress => Core.LinkAddress;

    /// <inheritdoc />
    public bool LinkEstablished => Core.LinkEstablished;

    /// <inheritdoc />
    public DateTime LastActivityUtc => Core.LastActivityUtc;

    /// <inheritdoc />
    public event Action<byte[]>? RawFrameReceived;

    /// <summary>由服务端在收到帧时调用，触发本会话的 RawFrameReceived。</summary>
    internal void RaiseRawFrameReceived(byte[] raw) => RawFrameReceived?.Invoke(raw);

    /// <inheritdoc />
    public ValueTask SendAsduAsync(Asdu asdu, CancellationToken ct = default)
    {
        Core.Enqueue(asdu);
        return default;
    }

    /// <inheritdoc />
    public ValueTask SendRawAsduAsync(ReadOnlyMemory<byte> asduBytes, CancellationToken ct = default)
    {
        Core.EnqueueRaw(asduBytes.ToArray(), isClass1: true);
        return default;
    }
}
