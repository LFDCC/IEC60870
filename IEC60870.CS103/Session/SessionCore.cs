//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 103 从站会话核心：与传输方式无关的链路层运行时。
/// </summary>
/// <remarks>
/// <para>
/// 一个 <see cref="SessionCore"/> 对应<b>一条非平衡链路</b>。
/// TCP 模式下，每个主站连接创建一个核心（多会话并发）；
/// 串口/Stream 模式下，整条物理链路只有一个核心（单链路）。
/// </para>
/// <para>
/// <b>职责</b>：字节流读取 → FT1.2 分帧 → 链路层应答（FCB/ACD/1-2 级队列）→
/// ASDU 分派 → 事件上报。所有传输特性（TCP 连接/串口打开）由
/// <see cref="IIec103LinkTransport"/> 承担。
/// </para>
/// <para>
/// <b>地址过滤</b>：串口/Stream 模式下启用（RS-485 总线上多站共享线路，
/// 必须忽略非本站地址；255 广播可配置是否接受）。TCP 模式关闭
/// （一条连接对应一个主站，地址由首帧确定）。
/// </para>
/// </remarks>
internal sealed class SessionCore : IAsyncDisposable
{
    private readonly IIec103LinkTransport _transport;
    private readonly Iec103LinkParameters _parameters;
    private readonly Iec103Server _server;
    private readonly long _sessionId;
    private readonly Action<string>? _logger;
    private readonly bool _filterByAddress;

    private readonly LinkLayerEngine _engine;
    private readonly Ft12FrameParser _parser = new();
    private readonly ConcurrentQueue<byte[]> _class1 = new();
    private readonly ConcurrentQueue<byte[]> _class2 = new();
    private readonly CancellationTokenSource _cts = new();

    // 上送队列上界：主站不轮询（或不及时轮询）时队列会无限增长直到 OOM。
    // 每个队列默认最多 10000 条，超出丢弃最旧数据并计入 DroppedAsduCount。
    private const int DefaultMaxQueueLength = 10000;

    private Task? _loop;
    private volatile bool _linkEstablished;
    private volatile bool _disposed;

    // 读循环自身退出时调用 DisposeAsync，避免「自己等自己退出」的两秒空等。
    private int _disposingFromReceiveLoop;
    private long _droppedAsduCount;
    private byte _linkAddress;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    // 数据帧 FCB 去重（从站侧）。主站因 ACK 丢失而原样重发同一 SEND/CONFIRM 帧时，
    // 只重发缓存的链路应答，绝不再派发 ASDU——否则遥控/对时/定值写会被执行两次。
    private bool _hasLastDataFcb;
    private bool _lastDataFcb;
    private byte _lastDataFcbAddress;
    private Ft12Frame? _lastDataResponse;
    private byte[]? _lastDataAsdu;

    /// <summary>创建会话核心。</summary>
    /// <param name="server">宿主从站（事件上报与私有注册表来源）。</param>
    /// <param name="transport">字节流传输。</param>
    /// <param name="parameters">链路层参数。</param>
    /// <param name="sessionId">会话标识（TCP 递增；串口固定 1）。</param>
    /// <param name="logger">日志回调。</param>
    /// <param name="filterByAddress">
    /// 是否启用链路地址过滤。串口/Stream 为 true；TCP 为 false。
    /// </param>
    public SessionCore(
        Iec103Server server,
        IIec103LinkTransport transport,
        Iec103LinkParameters parameters,
        long sessionId,
        Action<string>? logger,
        bool filterByAddress)
    {
        _server = server;
        _transport = transport;
        _parameters = parameters;
        _sessionId = sessionId;
        _logger = logger;
        _filterByAddress = filterByAddress;

        _engine = new LinkLayerEngine(transport, parameters, msg => RaiseFrameTraceText(_sessionId, msg));

        _parser.FrameRejected += (hex, reason) =>
            _server.RaiseFrameRejectedById(_sessionId, new Iec103FrameRejectedEventArgs(hex, reason));

        transport.TransportClosed += OnTransportClosed;
    }

    /// <summary>会话标识。</summary>
    public long SessionId => _sessionId;

    /// <summary>对端描述。</summary>
    public string RemoteEndPoint => _transport.EndPoint;

    /// <summary>链路地址（由主站复位帧或首帧确定；串口模式默认取配置的本站地址）。</summary>
    public byte LinkAddress => _linkAddress;

    /// <summary>链路是否已建立（收到有效请求帧或复位帧）。</summary>
    public bool LinkEstablished => _linkEstablished;

    /// <summary>最近一次活动时刻（UTC）。</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>链路引擎（诊断用）。</summary>
    public LinkLayerEngine Engine => _engine;

    /// <summary>
    /// 1 级 / 2 级上送队列各自允许的最大条数（默认 10000）。
    /// </summary>
    /// <remarks>
    /// 主站长时间不召唤时队列会持续增长，必须设上界。超出后丢弃最旧数据
    /// （保留最新数据，与现场「宁丢旧不丢新」的监视需求一致）。
    /// </remarks>
    internal int MaxQueueLength { get; set; } = DefaultMaxQueueLength;

    /// <summary>因队列达到上界而丢弃的 ASDU 累计条数（诊断）。</summary>
    internal long DroppedAsduCount => Interlocked.Read(ref _droppedAsduCount);

    /// <summary>启动读循环。</summary>
    public void Start()
    {
        if (_filterByAddress)
        {
            // 串口单链路：本站地址在链路建立前即确定，用于地址过滤
            _linkAddress = _parameters.LocalLinkAddress;
            _engine.Address = _parameters.LocalLinkAddress;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>入队一条待上送的 ASDU（TI=9 测量进 2 级，其余进 1 级）。</summary>
    public void Enqueue(Asdu asdu)
    {
        ArgumentNullException.ThrowIfNull(asdu);

        // 已释放后直接丢弃：宿主可能在会话销毁与业务线程入队之间竞态。
        if (_disposed)
        {
            return;
        }

        EnqueueRaw(asdu.Encode(), isClass1: asdu.TypeId != TypeIds.Measurand);
    }

    /// <summary>入队原始 ASDU 字节。</summary>
    public void EnqueueRaw(byte[] asduBytes, bool isClass1)
    {
        if (_disposed)
        {
            return;
        }

        EnqueueBounded(isClass1 ? _class1 : _class2, asduBytes);
    }

    /// <summary>入队并按上界裁剪（丢弃最旧数据并累计诊断计数）。</summary>
    private void EnqueueBounded(ConcurrentQueue<byte[]> queue, byte[] asduBytes)
    {
        queue.Enqueue(asduBytes);

        var limit = MaxQueueLength;
        if (limit < 0)
        {
            limit = 0;
        }

        var dropped = 0;
        while (queue.Count > limit && queue.TryDequeue(out _))
        {
            dropped++;
        }

        if (dropped == 0)
        {
            return;
        }

        var total = Interlocked.Add(ref _droppedAsduCount, dropped);

        // 只在首次丢弃、以及每跨过 1000 条边界时记一条 Warning，避免刷爆日志。
        var previous = total - dropped;
        if (previous == 0 || previous / 1000 != total / 1000)
        {
            _logger?.Invoke(
                $"[WARN] [会话 {_sessionId}] ASDU 队列超过上界 {limit}，已丢弃最旧数据（累计 {total} 条）");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                int read;
                try
                {
                    read = await _transport.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[会话 {_sessionId}] 读取失败: {ex.Message}");
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                foreach (var frame in _parser.Feed(buffer.AsSpan(0, read)))
                {
                    await HandleFrameAsync(frame, ct).ConfigureAwait(false);
                }

                // 半帧超时复位：装置发帧不全或线路中断时不卡死
                _parser.TryResetStale(_parameters.CharacterTimeoutMs);
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] 链路循环退出: {ex.Message}");
        }
        finally
        {
            // 统一收尾：异常退出、正常取消、对端关闭都必须让会话从服务端注册表消失、
            // 停止链路标记并释放资源，否则 BroadcastAsync 会继续往僵尸会话投递。
            // 三个步骤都幂等且各自隔离，避免一个失败影响其余收尾。
            _linkEstablished = false;

            try
            {
                _server.UnregisterSessionById(_sessionId, "链路循环退出");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[会话 {_sessionId}] 注销会话异常: {ex.Message}");
            }

            Volatile.Write(ref _disposingFromReceiveLoop, 1);
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[会话 {_sessionId}] 读循环退出后释放异常: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _disposingFromReceiveLoop, 0);
            }
        }
    }

    private async Task HandleFrameAsync(Ft12Frame frame, CancellationToken ct)
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        // RS-485 地址过滤：非本站地址且非广播的帧静默忽略（不回应答、不上报）
        if (_filterByAddress && !ShouldAcceptAddress(frame.Address))
        {
            return;
        }

        _server.RaiseFrameTracedById(_sessionId, "RX", frame);

        var control = LinkControl.FromByte(frame.Control);
        var isDuplicateDataFrame = IsDuplicateDataFrame(frame, control);

        if (frame.Format == Ft12FrameFormat.VariableLength)
        {
            var raw = frame.ToBytes();
            _server.RaiseRawFrameReceivedById(_sessionId, raw);

            _linkEstablished = true;
            if (!_filterByAddress)
            {
                // TCP：链路地址由主站首帧确定
                _linkAddress = frame.Address;
            }

            // 重复数据帧（FCB 与上次相同）只重发应答，绝不再派发 ASDU：
            // 主站重发是 ACK 丢失后的正常协议行为，应用层必须只执行一次。
            if (!isDuplicateDataFrame && frame.Asdu.Length >= Asdu.HeaderLength)
            {
                DispatchAsdu(frame.Asdu);
            }
        }

        if (control.IsPrimary)
        {
            var function = (PrimaryFunction)control.Function;
            if (function is PrimaryFunction.ResetCu or PrimaryFunction.ResetFcb)
            {
                _linkEstablished = true;
                if (!_filterByAddress)
                {
                    _linkAddress = frame.Address;
                }

                _engine.Address = frame.Address;
                ResetDataFrameDedup();
            }
        }

        // 重复帧仍然必须回应答，否则主站收不到响应会无限重发。
        if (isDuplicateDataFrame)
        {
            await SendSecondaryResponseAsync(_lastDataResponse!, ct).ConfigureAwait(false);
            return;
        }

        var response = _engine.BuildSecondaryResponse(
            frame,
            nextClass1: () => _class1.TryDequeue(out var a) ? a : null,
            nextClass2: () => _class2.TryDequeue(out var a) ? a : null,
            hasMoreClass1: () => !_class1.IsEmpty);

        if (response is null)
        {
            return;
        }

        // 缓存数据帧的应答，供同一 FCB 的重发帧原样回送。
        if (ShouldTrackDataFrame(frame, control))
        {
            _hasLastDataFcb = true;
            _lastDataFcb = control.FcbOrAcd;
            _lastDataFcbAddress = frame.Address;
            _lastDataResponse = response;
            _lastDataAsdu = frame.Asdu.ToArray();
        }

        await SendSecondaryResponseAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 判定是否为主站重发的数据帧：同链路地址 + 同 FCB 的 SEND/CONFIRM（FCV=1）。
    /// </summary>
    /// <remarks>
    /// 只覆盖携带应用层 ASDU 的 SEND/CONFIRM。REQUEST CLASS 1/2 是主站主动召唤
    /// （固定帧、无 ASDU），不参与 FCB 去重；广播（地址 255）不应答，同样不去重。
    /// </remarks>
    private bool IsDuplicateDataFrame(Ft12Frame frame, in LinkControl control)
        => ShouldTrackDataFrame(frame, control)
            && _hasLastDataFcb
            && _lastDataFcb == control.FcbOrAcd
            && _lastDataFcbAddress == frame.Address
            && _lastDataResponse is not null
            // 真重发是**逐字节相同**的帧；只比 FCB 会把"主站连续两帧但未翻转 FCB"
            // （某些实现如此）误判为重复，导致第二条命令被静默替换成上一条的应答。
            && _lastDataAsdu is not null
            && frame.Asdu.AsSpan().SequenceEqual(_lastDataAsdu);

    /// <summary>该帧是否属于需要按 FCB 去重的数据帧（SEND/CONFIRM、FCV=1、非广播）。</summary>
    private static bool ShouldTrackDataFrame(Ft12Frame frame, in LinkControl control)
        => frame.Format == Ft12FrameFormat.VariableLength
            && control.IsPrimary
            && (PrimaryFunction)control.Function == PrimaryFunction.SendConfirmed
            && control.FcvOrDfc
            && frame.Address != 255;

    /// <summary>链路复位后清空 FCB 去重状态（主站会从 FCB=0 重新开始）。</summary>
    private void ResetDataFrameDedup()
    {
        _hasLastDataFcb = false;
        _lastDataFcb = false;
        _lastDataFcbAddress = 0;
        _lastDataResponse = null;
        _lastDataAsdu = null;
    }

    private async Task SendSecondaryResponseAsync(Ft12Frame response, CancellationToken ct)
    {
        _server.RaiseFrameTracedById(_sessionId, "TX", response);

        try
        {
            await _transport.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] 应答发送失败: {ex.Message}");
        }
    }

    /// <summary>
    /// RS-485 总线地址判定：本站地址或（可配置的）255 广播。
    /// </summary>
    private bool ShouldAcceptAddress(byte address)
        => address == _parameters.LocalLinkAddress
            || (_parameters.AcceptBroadcast && address == _parameters.BroadcastAddress);

    /// <summary>
    /// 链路层诊断文本出口（帧收发/重发/重复帧/非法帧）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 主站侧有文本级 <see cref="Iec103Client.TraceFrame"/>；从站侧只有结构化的
    /// <see cref="Iec103Server.FrameTraced"/>（按帧给 <see cref="Ft12Frame"/> 对象），
    /// 而 <c>Iec103Server.RaiseFrameTraceText</c> 目前是空实现（不派发任何事件）。
    /// 由于本文件之外不得改动，这里直接走 <see cref="_logger"/> 输出，先保证诊断文本
    /// 不被吞掉；待服务器补上文本级事件后，只需改本方法一处即可接上，
    /// 不必再回到引擎回调里逐处修改。
    /// </para>
    /// <para>
    /// 原实现把引擎回调里的文本转手给上述空实现，等于静默丢弃 ——
    /// 与主站「有 TraceFrame」形成对称缺口。
    /// </para>
    /// </remarks>
    private void RaiseFrameTraceText(long sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _logger?.Invoke($"[会话 {sessionId}] {text}");
    }

    private void DispatchAsdu(byte[] asduBytes)
    {
        // 注意：Iec103AsduView 是 ref struct，不能捕获进 lambda，
        // 因此这里用显式 try/catch 而不是 SafeInvoke 包裹整段（与主站 DispatchAsdu 对称）。
        // UnknownTypePolicy.Throw 抛出的异常也必须在此就地隔离：若冒泡到 RunAsync 外层
        // catch，整个读循环会退出、会话静默死亡 —— 从站在 Throw 策略下将彻底不再收帧。
        try
        {
            var view = new Iec103AsduView(asduBytes);

            _server.RaiseAsduReceivedById(_sessionId, in view);

            var hasPrivate = _server.PrivateTypes.TryResolve(view.TypeId, view.Fun, view.Inf, view.CotByte, out _);

            if (!TypeIds.IsStandard(view.TypeId) && !hasPrivate)
            {
                // 尽量带上来源会话（原先恒为 null，与主站语义不一致）；
                // 会话表里查不到时保持 null，不因此丢弃事件。
                var unknown = new Iec103UnknownAsdu(
                    asduBytes,
                    session: _server.FindSession(_sessionId),
                    $"未登记 TI={view.TypeId} FUN={view.Fun} INF={view.Inf}");

                if (_server.UnknownTypePolicy == Iec103UnknownTypePolicy.Log)
                {
                    _logger?.Invoke(unknown.ToString());
                }

                _server.RaiseUnknownAsduById(_sessionId, unknown);

                // Throw 策略：与主站一致——先投递 UnknownAsduReceived 事件，再抛异常，
                // 保证「绝不丢帧」在严格校验模式下同样成立。
                if (_server.UnknownTypePolicy == Iec103UnknownTypePolicy.Throw)
                {
                    throw new Iec103UnknownAsduException(unknown);
                }
            }

            // 通用分类数据（TI=10, FUN=254）：逐条检查 GIN/KOD/GDD 是否登记，
            // 未登记的条目保留原始 GID 并上报 UnknownGenericDataReceived。
            if (view.TypeId == TypeIds.GenericData && view.Fun == Asdu.GenericFun && view.Data.Length >= 2)
            {
                var offset = 2; // RII(1) + NGD(1)
                while (GenericDataSet.TryDecode(view.Data[offset..], out var set, out var consumed) && set is not null)
                {
                    if (!_server.PrivateTypes.IsStandardEntry(set))
                    {
                        _server.RaiseUnknownGenericById(_sessionId,
                            new Iec103GenericUnknownEntry(set.Gin, set.Kod, set.Gdd, set.Gid, "未登记的私有 GIN/KOD/GDD"));
                    }

                    offset += consumed;
                    if (offset >= view.Data.Length)
                    {
                        break;
                    }
                }
            }
        }
        catch (Iec103UnknownAsduException ex)
        {
            // 严格校验模式下的「预期异常」：记日志后继续，读循环不受影响
            _logger?.Invoke($"[会话 {_sessionId}] {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] ASDU 分派异常已隔离，链路继续运行: {ex}");
        }
    }

    private void OnTransportClosed(object? sender, Iec103TransportClosedEventArgs e)
    {
        // 先退订：本核心的关闭回调只应生效一次。若残留订阅，旧核心之后的回调
        // 可能把服务端新建立的 _streamCore 置空（幽灵回调）。
        _transport.TransportClosed -= OnTransportClosed;
        _linkEstablished = false;

        // 真正移除会话表条目并派发一次关闭事件。
        // 原实现只发事件不注销：SessionCount 仍为 1、Mode 仍为 SerialPort，
        // 断开后无法重启，且 StopAsync 会二次派发 SessionClosed。
        _server.UnregisterSessionById(_sessionId, e.Reason);

        // 串口/Stream 单链路：由服务端取出并释放当前核心（含 CTS/写锁/读循环）。
        // 旧实现只把字段置 null，核心资源与旧订阅全部残留。
        _ = NotifyStreamLinkLostSafeAsync();

        // TCP 模式也统一由核心自行释放（Iec103Session 的 DisposeCoreAsync 是幂等的）。
        _ = DisposeSafeAsync();
    }

    private async Task NotifyStreamLinkLostSafeAsync()
    {
        try
        {
            await _server.NotifyStreamLinkLost(this).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] 通知链路丢失异常: {ex.Message}");
        }
    }

    private async Task DisposeSafeAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] 传输关闭后释放异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.TransportClosed -= OnTransportClosed;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // 读循环自身退出时不能再等待自己（会空等 2 秒），由 _disposingFromReceiveLoop 跳过。
        if (_loop is not null && Volatile.Read(ref _disposingFromReceiveLoop) == 0)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger?.Invoke($"[会话 {_sessionId}] 读循环未在 2 秒内退出");
            }
            catch (OperationCanceledException)
            {
                // 预期
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[会话 {_sessionId}] 等待读循环异常: {ex.Message}");
            }
        }

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[会话 {_sessionId}] 释放传输异常: {ex.Message}");
        }
        finally
        {
            // 无论传输释放是否异常，CTS 都必须释放。
            _cts.Dispose();
        }
    }
}
