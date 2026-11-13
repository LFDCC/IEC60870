//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 主站（Client）。
/// </summary>
/// <remarks>
/// <para><b>异步模型</b>：全部 I/O 为 <c>XxxAsync(..., CancellationToken)</c>。
/// 后台读循环由生命周期 <see cref="CancellationTokenSource"/> 托管，异常经
/// <see cref="Logger"/> 与 <see cref="Iec103LinkEvent"/> 上报，不做 fire-and-forget 吞异常。</para>
/// <para><b>事务匹配</b>：不再用「全局响应队列 + 睡眠轮询」。
/// 每轮链路服务由 <see cref="LinkLayerEngine"/> 在本轮内等待应答，
/// 应答不会串到下一个事务——消除了 103 原实现「迟到帧被下一事务当作自己的答案」的缺陷。</para>
/// <para><b>帧分派</b>：读循环把从站上送的<b>可变帧</b>（用户数据）解析为 ASDU 后
/// 走注册表分派；固定帧/单字符帧作为链路应答交给链路引擎。
/// 这样突发上送（1 级数据）与命令应答在同一通道但语义分离。</para>
/// </remarks>
public sealed class Iec103Client : IIec103Master
{
    private readonly Iec103Options _options;
    private readonly Iec103LinkParameters _linkParameters;
    private readonly Action<TouchSocketConfig>? _configureConfig;

    private IIec103LinkTransport? _transport;
    private LinkLayerEngine? _engine;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _receiveLoop;
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private readonly LinkTransactionDispatcher103 _transactions = new();
    private readonly Ft12FrameParser _parser = new();

    // 生命周期串行化：Connect/Disconnect/Dispose 可被并发调用，
    // 若不串行化会出现两个读循环、字段互相覆盖、对已 Dispose 的 CTS 取 Token 等问题。
    // 与 _transactionLock 一致：不 Dispose 该信号量（并发 Release 可能抛 ObjectDisposedException）。
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private volatile bool _connected;
    private volatile bool _disposed;

    // Disconnected 事件去重：被动断线路径（传输关闭事件 + 读循环退出）可能先后触发，
    // 但对外只应上报一次。
    private int _disconnectedRaised;

    // 传输来源（三选一）：TCP 地址 / 已有串口 / 任意字节流
    private readonly string? _host;
    private readonly int _port;
    private readonly SerialPort? _serialPort;
    private readonly Stream? _stream;

    /// <summary>创建 TCP 主站实例。</summary>
    /// <param name="host">从站主机名或 IP。</param>
    /// <param name="port">从站端口。</param>
    /// <param name="options">组件配置；为 null 时使用默认配置。</param>
    /// <param name="configureConfig">TouchSocket 配置回调（插件、日志等）。</param>
    public Iec103Client(string host, int port, Iec103Options? options = null, Action<TouchSocketConfig>? configureConfig = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _options = (options ?? new Iec103Options()).Clone();
        _linkParameters = _options.Link;
        _configureConfig = configureConfig;

        _parser.FrameRejected += OnFrameRejected;
    }

    /// <summary>
    /// 创建串口主站实例（RS-485 / RS-232 上的 FT1.2）。
    /// </summary>
    /// <param name="port">串口实例；未打开时由库负责打开，所有权仍归调用方（库只 Close 不 Dispose）。</param>
    /// <param name="options">组件配置；为 null 时使用默认配置。</param>
    /// <remarks>
    /// 串口与网口在字节层面完全一致：都是裸 FT1.2 帧。
    /// 区别仅在通道建立方式，因此协议栈与链路层完全复用。
    /// </remarks>
    public Iec103Client(SerialPort port, Iec103Options? options = null)
    {
        _serialPort = port ?? throw new ArgumentNullException(nameof(port));
        _options = (options ?? new Iec103Options()).Clone();
        _linkParameters = _options.Link;

        _parser.FrameRejected += OnFrameRejected;
    }

    /// <summary>
    /// 创建基于任意字节流的主站实例。
    /// </summary>
    /// <param name="stream">双向字节流（无线数传、协议网关、内存管道等）。</param>
    /// <param name="options">组件配置；为 null 时使用默认配置。</param>
    public Iec103Client(Stream stream, Iec103Options? options = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _options = (options ?? new Iec103Options()).Clone();
        _linkParameters = _options.Link;

        _parser.FrameRejected += OnFrameRejected;
    }

    private void OnFrameRejected(string hex, string reason)
    {
        Logger?.Warning($"帧舍弃: {reason} RAW={hex}");

        // 这条链在 _parser.Feed() 内部同步触发，不在 DispatchFrame 的隔离范围内：
        // 订阅者抛异常会击穿 ReceiveLoopAsync。必须单独隔离（与 DispatchFrame 对称）。
        SafeInvoke("Iec103LinkEvent(FrameRejected)",
            () => RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.FrameRejected, reason)));
    }

    /// <inheritdoc />
    public string ProtocolName => Iec103Protocol.Iec103;

    /// <inheritdoc />
    public ILog? Logger { get; set; }

    /// <inheritdoc />
    public Iec103PrivateTypeRegistry PrivateTypes { get; } = new();

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <summary>
    /// 最近一次链路服务使用的链路地址。
    /// </summary>
    /// <remarks>
    /// 一条通道下挂多台装置时，本属性只反映「最后一次操作的装置」，
    /// <b>不能</b>用来判断收到的 ASDU 属于哪台——用 <see cref="Asdu.Ca"/>。
    /// </remarks>
    public byte LinkAddress { get; private set; }

    /// <inheritdoc />
    public event Iec103AsduViewHandler? AsduReceived;

    /// <inheritdoc />
    public event Action<Iec103UnknownAsdu>? UnknownAsduReceived;

    /// <inheritdoc />
    public event Action<Iec103LinkEvent>? Iec103LinkEvent;

    /// <inheritdoc />
    public event Action<byte[]>? RawFrameReceived;

    /// <inheritdoc />
    public event Action<byte[]>? RawFrameSent;

    /// <summary>收到未识别通用分类条目。</summary>
    public event Action<Iec103GenericUnknownEntry>? UnknownGenericDataReceived;

    /// <summary>
    /// 链路层帧追踪：收发<b>每个</b> FT1.2 帧（含固定帧/单字符 ACK）的十六进制文本。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="RawFrameReceived"/> 互补：后者给结构化帧，本事件给原始十六进制，
    /// 便于现场直接与规约仪/抓包比对。默认无订阅者零开销。
    /// </remarks>
    public event Action<string>? TraceFrame;

    /// <summary>
    /// 链路层帧追踪（结构化）：收发的每个 FT1.2 帧，方向取 <c>"RX"</c> / <c>"TX"</c>。
    /// </summary>
    /// <remarks>与 <see cref="Iec103Server.FrameTraced"/> 同款签名，便于主从两侧统一排障。</remarks>
    public event Action<string, Ft12Frame>? FrameTraced;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_connected)
            {
                return;
            }

            // 重连/重复调用：先收掉上一轮的接收循环与生命周期令牌。
            // 旧实现把这一步放在创建新传输之后，且旧传输既不退订 TransportClosed
            // 也不 DisposeAsync —— 每次重连都会泄漏一个连接对象，且两个读循环
            // 可能争抢同一个字节流（被动断线后重连的典型症状）。
            try
            {
                _lifetimeCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_receiveLoop is not null)
            {
                try
                {
                    await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger?.Warning("重连时上一轮接收循环未在 2 秒内退出");
                }
                catch (OperationCanceledException)
                {
                    // 预期
                }
            }

            _receiveLoop = null;

            try
            {
                _lifetimeCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _lifetimeCts = null;

            // 必须在创建新传输之前释放旧传输（退订事件 + DisposeAsync）。
            await DetachTransportAsync().ConfigureAwait(false);

            if (_host is not null)
            {
                var tcp = new TcpClientLinkTransport(_host, _port, Logger);
                tcp.TransportClosed += OnTransportClosed;
                try
                {
                    await tcp.ConnectAsync(_linkParameters.MessageTimeoutMs, ct, _configureConfig).ConfigureAwait(false);
                }
                catch
                {
                    // 建连失败（连不上/超时/SetupAsync 失败）：必须退订并释放刚创建的
                    // TcpClient，否则每次重试都泄漏一个未 Dispose 的连接对象。
                    tcp.TransportClosed -= OnTransportClosed;
                    try
                    {
                        await tcp.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // 保留原始建连异常
                    }

                    throw;
                }

                _transport = tcp;
            }
            else if (_serialPort is not null)
            {
                var serial = new SerialLinkTransport(_serialPort);
                serial.TransportClosed += OnTransportClosed;
                try
                {
                    serial.Open();
                }
                catch
                {
                    serial.TransportClosed -= OnTransportClosed;
                    try
                    {
                        await serial.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // 保留原始打开异常
                    }

                    throw;
                }

                _transport = serial;
            }
            else
            {
                var stream = new StreamLinkTransport(_stream!, "字节流");
                stream.TransportClosed += OnTransportClosed;
                _transport = stream;
            }

            _engine = new LinkLayerEngine(
                _transport,
                _linkParameters,
                msg =>
                {
                    Logger?.Debug(msg);
                    // 链路层诊断（帧收发/重发/重复帧）同时发给 TraceFrame 订阅者，
                    // 便于现场无日志基础设施时仍可观测链路行为。必须隔离：该回调在
                    // 接收循环/发送路径内同步执行，订阅者抛异常会打死链路。
                    SafeInvoke("TraceFrame", () => TraceFrame?.Invoke(msg));
                });

            _lifetimeCts = new CancellationTokenSource();
            _connected = true;
            Interlocked.Exchange(ref _disconnectedRaised, 0);
            _parser.Reset();
            _transactions.End();

            // 接收循环必须常开：所有请求（RequestClass*/总召/定值…）都靠它把应答
            // 读出来并完成事务，关掉它只会让每个请求静默超时。
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetimeCts.Token));

            RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.Connected, _transport.EndPoint));
            Logger?.Info($"103 主站已连接 {_transport.EndPoint}");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>断开连接的实际实现（调用方必须已持有 <see cref="_lifecycleLock"/>）。</summary>
    private async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (!_connected && _transport is null && _receiveLoop is null && _lifetimeCts is null)
        {
            return;
        }

        _connected = false;

        // 先上报一次断开：读循环退出路径也会上报，但去重标志保证只发一次，
        // 且此处先发能保留「主动断开」这个更准确的原因。
        RaiseDisconnectedOnce("主动断开");

        if (_lifetimeCts is not null)
        {
            // 取消生命周期令牌：通过 ExecuteLinkServiceAsync 的 linked CTS
            // 唤醒所有在途事务，避免它们悬挂 MessageTimeoutMs × (MaxRepeat+1)。
            try
            {
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // 等所有在途事务退出（它们 finally 会释放 _transactionLock）。
        // 不做这一步就拆 transport，旧事务仍持旧引擎，且会窃取重连后的首帧。
        try
        {
            await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
            _transactionLock.Release();
        }
        catch (OperationCanceledException)
        {
            // 调用方取消，仍继续清理
        }

        _transactions.End();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger?.Warning("接收循环未在 2 秒内退出");
            }
            catch (OperationCanceledException)
            {
                // 预期
            }
        }

        _receiveLoop = null;

        // 统一收尾：退订 TransportClosed 并释放传输（幂等）。
        await DetachTransportAsync().ConfigureAwait(false);

        try
        {
            _lifetimeCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _lifetimeCts = null;
    }

    /// <summary>
    /// 退订并释放当前传输（幂等）。调用方必须持有 <see cref="_lifecycleLock"/>，
    /// 或通过 <see cref="ScheduleTransportDetach"/> 在锁内调用。
    /// </summary>
    private async Task DetachTransportAsync()
    {
        // 调用方持有生命周期锁（或由 ScheduleTransportDetach 在锁内调用），
        // 先取出本地引用再清空字段，避免与读循环竞态。
        var transport = Volatile.Read(ref _transport);
        Volatile.Write(ref _transport, null);
        _engine = null;

        if (transport is null)
        {
            return;
        }

        transport.TransportClosed -= OnTransportClosed;

        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.Warning($"释放 103 传输时异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 被动断线后异步释放传输（fire-and-forget）。
    /// 仅当 <paramref name="expected"/> 仍是当前传输时才释放，防止旧读循环
    /// 的退出把重连后的新传输误释放。
    /// </summary>
    private void ScheduleTransportDetach(IIec103LinkTransport? expected)
    {
        if (expected is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(Volatile.Read(ref _transport), expected))
                    {
                        // 已被新的生命周期接管（重连），不能再动新传输。
                        return;
                    }

                    await DetachTransportAsync().ConfigureAwait(false);
                }
                finally
                {
                    _lifecycleLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning($"后台释放 103 传输时异常: {ex.Message}");
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  链路服务
    // ═══════════════════════════════════════════════════════════════

    /// <summary>控制帧（RESET/TEST/REQUEST LINK STATUS）期望的从站应答：ACK / NACK / LINK STATUS。</summary>
    private static readonly IReadOnlySet<byte> ControlFrameResponses =
        new HashSet<byte>
        {
            (byte)SecondaryFunction.Ack,
            (byte)SecondaryFunction.Nack,
            (byte)SecondaryFunction.LinkStatus,
        };

    /// <summary>
    /// 数据召唤（REQUEST CLASS 1/2）期望的从站应答：以数据响应 / 无数据。
    /// 必须排除 ACK —— 否则上一轮迟到的 ACK 会被本轮的数据召唤当成应答，
    /// 造成"取到空数据却认为成功"。
    /// </summary>
    private static readonly IReadOnlySet<byte> DataCallResponses =
        new HashSet<byte>
        {
            (byte)SecondaryFunction.RespondData,
            (byte)SecondaryFunction.RespondNoData,
        };

    /// <summary>
    /// 用户数据确认（SEND/CONFIRM）期望的应答：ACK / NACK / 以数据响应
    /// （103 中数据帧本身即可充当链路层确认）。
    /// </summary>
    private static readonly IReadOnlySet<byte> SendConfirmResponses =
        new HashSet<byte>
        {
            (byte)SecondaryFunction.Ack,
            (byte)SecondaryFunction.Nack,
            (byte)SecondaryFunction.RespondData,
        };

    /// <inheritdoc />
    public async Task ResetLinkAsync(byte ca, CancellationToken ct = default)
    {
        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            LinkAddress = ca;
            engine.Address = ca;
            engine.ResetFcbState(ca);

            var response = await engine.SendControlAndWaitAsync(
                LinkControl.ResetCu(), transaction, linkedCt).ConfigureAwait(false);

            var control = LinkControl.FromByte(response.Control);
            if (control.Function != (byte)SecondaryFunction.Ack
                && control.Function != (byte)SecondaryFunction.LinkStatus)
            {
                Logger?.Warning($"复位链路收到非 ACK 应答: {control}");
            }

            RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.LinkReset, $"CA={ca}"));
        }, ct, ca, ControlFrameResponses).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResetFcbAsync(byte ca, CancellationToken ct = default)
    {
        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = ca;
            await engine.SendControlAndWaitAsync(LinkControl.ResetFcb(), transaction, linkedCt).ConfigureAwait(false);
            engine.ResetFcbState(ca);
        }, ct, ca, ControlFrameResponses).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Ft12Frame> RequestLinkStatusAsync(byte ca, CancellationToken ct = default)
    {
        Ft12Frame? result = null;

        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = ca;
            result = await engine.SendControlAndWaitAsync(
                LinkControl.RequestLinkStatus(), transaction, linkedCt).ConfigureAwait(false);
            RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.LinkStatus, $"CA={ca}"));
        }, ct, ca, ControlFrameResponses).ConfigureAwait(false);

        return result!;
    }

    // ═══════════════════════════════════════════════════════════════
    //  应用层服务
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task SendClockSyncAsync(byte ca, DateTime time, CancellationToken ct = default)
    {
        var asdu = Asdu.ClockSync(Cp56Time2a.FromDateTime(time), ca);
        await SendConfirmedAsduAsync(asdu, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asdu>> GeneralInterrogationAsync(byte ca, CancellationToken ct = default)
    {
        var collected = new List<Asdu>();

        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = ca;

            // 不要在这里 ResetFcbState()：它只清主站侧的 _nextFcb，
            // 并不向从站发 RESET FCB(FC=7)。从站仍保留上轮的 _lastReceivedFcb，
            // 连续两次总召唤时第二轮 FCB=0 会被判为重复帧、重发陈旧应答。
            // FCB 状态由链路层随每轮服务自然翻转即可。

            // 1. 总召唤命令（SEND/CONFIRM 携带 TI=7）。
            //    标准应答是固定帧 ACK/NACK，数据必须由后续 REQUEST CLASS1 取走。
            //    （旧版从确认帧里收 ASDU 是为配合「从站把数据塞进确认」的非标准行为，
            //     现已与从站侧一并改为标准语义。）
            await SendConfirmedAsduCoreAsync(engine, Asdu.GeneralInterrogation(ca), transaction, linkedCt).ConfigureAwait(false);
            engine.FlipFcb(ca);

            // 2. 反复召唤 1 级直到收到总召唤终止帧（TI=8）
            var terminated = false;
            var guard = 0;
            const int maxRounds = 4096;

            while (!terminated && guard++ < maxRounds)
            {
                if (linkedCt.IsCancellationRequested)
                {
                    break;
                }

                var control = LinkControl.RequestClass1(engine.GetNextFcb(ca));
                var response = await engine.SendControlAndWaitAsync(control, transaction, linkedCt).ConfigureAwait(false);
                engine.FlipFcb(ca);

                if (response.Format != Ft12FrameFormat.VariableLength)
                {
                    // 无所召唤数据
                    break;
                }

                terminated = CollectFromResponse(response, collected);
            }
        }, ct, ca).ConfigureAwait(false);

        return collected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asdu>> RequestClass1Async(
        byte ca,
        Func<Asdu, bool>? stop = null,
        CancellationToken ct = default)
    {
        var collected = new List<Asdu>();

        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = ca;
            var guard = 0;
            const int maxRounds = 4096;

            while (guard++ < maxRounds && !linkedCt.IsCancellationRequested)
            {
                var response = await engine.SendControlAndWaitAsync(
                    LinkControl.RequestClass1(engine.GetNextFcb(ca)), transaction, linkedCt).ConfigureAwait(false);
                engine.FlipFcb(ca);

                if (response.Format != Ft12FrameFormat.VariableLength)
                {
                    break;
                }

                if (!Asdu.TryDecode(response.Asdu, out var asdu, out var error) || asdu is null)
                {
                    Logger?.Warning($"1 级数据帧解析失败: {error}");
                    break;
                }

                collected.Add(asdu);

                if (stop is not null && stop(asdu))
                {
                    break;
                }

                if (!_linkParameters.DrainClass1UntilEmpty && !HasMoreClass1(response))
                {
                    break;
                }
            }
        }, ct, ca, DataCallResponses).ConfigureAwait(false);

        return collected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Asdu>> RequestClass2Async(byte ca, CancellationToken ct = default)
    {
        var collected = new List<Asdu>();

        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = ca;
            var guard = 0;
            const int maxRounds = 4096;

            while (guard++ < maxRounds && !linkedCt.IsCancellationRequested)
            {
                var response = await engine.SendControlAndWaitAsync(
                    LinkControl.RequestClass2(engine.GetNextFcb(ca)), transaction, linkedCt).ConfigureAwait(false);
                engine.FlipFcb(ca);

                if (response.Format != Ft12FrameFormat.VariableLength)
                {
                    break;
                }

                if (!Asdu.TryDecode(response.Asdu, out var asdu, out var error) || asdu is null)
                {
                    Logger?.Warning($"2 级数据帧解析失败: {error}");
                    break;
                }

                collected.Add(asdu);
            }
        }, ct, ca, DataCallResponses).ConfigureAwait(false);

        return collected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GenericDataSet>> ReadGenericProfileAsync(byte ca, CancellationToken ct = default)
    {
        var request = Asdu.GenericReadAll(ca, rii: 1);
        return await TransactGenericAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GenericDataSet>> ReadGenericAsync(byte ca, params (Gin Gin, byte Kod)[] entries)
    {
        var request = Asdu.GenericRead(ca, rii: 2, entries);
        return await ReadGenericCoreAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>读取指定通用分类条目（可取消）。</summary>
    public Task<IReadOnlyList<GenericDataSet>> ReadGenericAsync(
        byte ca,
        CancellationToken ct,
        params (Gin Gin, byte Kod)[] entries)
        => ReadGenericCoreAsync(Asdu.GenericRead(ca, rii: 2, entries), ct);

    private Task<IReadOnlyList<GenericDataSet>> ReadGenericCoreAsync(Asdu request, CancellationToken ct)
        => TransactGenericAsync(request, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GenericDataSet>> WriteGenericAsync(
        byte ca,
        IReadOnlyList<GenericDataSet> sets,
        CancellationToken ct = default)
    {
        var request = Asdu.GenericDataAsdu(ca, rii: 3, continuation: false, CotCodes.GenericWrite, sets);
        return await TransactGenericAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(byte ca, byte fun, byte inf, int dco, byte rii = 0, CancellationToken ct = default)
    {
        var asdu = Asdu.GeneralCommand(ca, fun, inf, dco, rii);
        await SendConfirmedAsduAsync(asdu, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendPrivateAsync(byte ca, byte fun, byte inf, Iec103DecodedAsdu value, CancellationToken ct = default)
        => SendPrivateAsync(ca, fun, inf, typeId: null, value, ct);

    /// <summary>发送厂商私有 ASDU（可显式指定 TI，供按 TypeID 注册的处理器使用）。</summary>
    /// <param name="ca">公共地址。</param>
    /// <param name="fun">功能类型。</param>
    /// <param name="inf">信息序号。</param>
    /// <param name="typeId">私有 TI；为 null 时由 FUN/INF 注册表解析。</param>
    /// <param name="value">待编码的厂商对象。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task SendPrivateAsync(byte ca, byte fun, byte inf, byte? typeId, Iec103DecodedAsdu value, CancellationToken ct = default)
    {
        IIec103PrivateTypeHandler? handler;
        byte resolvedTypeId;

        if (typeId.HasValue)
        {
            // 按 TI 注册是最自然的私有类型登记方式；原实现硬编码 TryResolve(0,...)
            // 使 RegisterType(handler) 永远解析不到。
            if (!PrivateTypes.TryGetType(typeId.Value, out handler) || handler is null)
            {
                throw new InvalidOperationException($"未登记 TI={typeId.Value} 的私有类型处理器，无法编码");
            }

            resolvedTypeId = typeId.Value;
        }
        else
        {
            if (!PrivateTypes.TryResolve(0, fun, inf, CotCodes.Spontaneous, out handler) || handler is null)
            {
                throw new InvalidOperationException($"未登记 FUN={fun} INF={inf} 的私有类型处理器，无法编码");
            }

            resolvedTypeId = handler.TypeId;
        }

        var payload = handler.Encode(value);
        // 命令方向用 COT=20（命令肯定确认），不要用监视方向的 COT=1（突发）
        var asdu = new Asdu(resolvedTypeId, true, 1, CotCodes.CommandAck, false, ca, fun, inf, payload);
        await SendConfirmedAsduAsync(asdu, ct).ConfigureAwait(false);
    }

    /// <summary>发送任意 ASDU（走 SEND/CONFIRM）。</summary>
    public async Task SendAsduAsync(Asdu asdu, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);
        await SendConfirmedAsduAsync(asdu, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  内部实现
    // ═══════════════════════════════════════════════════════════════

    private async Task SendConfirmedAsduAsync(Asdu asdu, CancellationToken ct)
    {
        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = asdu.Ca;
            LinkAddress = asdu.Ca;
            await SendConfirmedAsduCoreAsync(engine, asdu, transaction, linkedCt).ConfigureAwait(false);
            engine.FlipFcb(asdu.Ca);
        }, ct, asdu.Ca, SendConfirmResponses).ConfigureAwait(false);
    }

    private async Task<Ft12Frame> SendConfirmedAsduCoreAsync(
        LinkLayerEngine engine,
        Asdu asdu,
        LinkTransaction103 transaction,
        CancellationToken ct)
    {
        var payload = asdu.Encode();
        var response = await engine.SendConfirmedAndWaitAsync(payload, transaction, ct).ConfigureAwait(false);

        var control = LinkControl.FromByte(response.Control);
        if (control.Function == (byte)SecondaryFunction.Nack)
        {
            throw new InvalidOperationException("从站返回 NACK（链路忙或报文未接收）");
        }

        RaiseRawFrameSent(payload);
        return response;
    }    /// <summary>通用分类事务：发送读/写命令，累积 ASDU 10 响应直到后续标志为 false。</summary>
    private async Task<IReadOnlyList<GenericDataSet>> TransactGenericAsync(Asdu request, CancellationToken ct)
    {
        var results = new List<GenericDataSet>();
        var pendingUnknown = new List<Iec103GenericUnknownEntry>();

        await ExecuteLinkServiceAsync(async (engine, transaction, linkedCt) =>
        {
            engine.Address = request.Ca;

            // 同 GeneralInterrogationAsync：不静默清主站 FCB（从站侧不会同步）。
            await SendConfirmedAsduCoreAsync(engine, request, transaction, linkedCt).ConfigureAwait(false);
            engine.FlipFcb(request.Ca);

            var guard = 0;
            const int maxRounds = 256;

            while (guard++ < maxRounds && !linkedCt.IsCancellationRequested)
            {
                var response = await engine.SendControlAndWaitAsync(
                    LinkControl.RequestClass1(engine.GetNextFcb(request.Ca)), transaction, linkedCt).ConfigureAwait(false);
                engine.FlipFcb(request.Ca);

                if (response.Format != Ft12FrameFormat.VariableLength)
                {
                    break;
                }

                var asduBytes = response.Asdu;
                if (asduBytes.Length < Asdu.HeaderLength)
                {
                    break;
                }

                var view = new Iec103AsduView(asduBytes);
                if (view.Fun != Asdu.GenericFun || view.TypeId != TypeIds.GenericData)
                {
                    // 非通用分类数据（可能是突发事件），继续取
                    continue;
                }

                // Data 布局：RII(1) + NGD(1) + 数据集序列。
                // 原实现直接取 view.Data[1]，短帧时 IndexOutOfRangeException 打断调用。
                if (view.Data.Length < 2)
                {
                    Logger?.Warning($"通用分类响应数据区过短（{view.Data.Length}），忽略");
                    break;
                }

                // RII 必须匹配本轮请求，否则是上一个事务的迟到续帧，不能收
                var expectedRii = request.Data.Length > 0 ? request.Data[0] : (byte)0;
                if (view.Data[0] != expectedRii)
                {
                    Logger?.Debug($"通用分类响应 RII={view.Data[0]} 与请求 RII={expectedRii} 不符，忽略迟到帧");
                    continue;
                }

                // COT=43 表示读命令无效响应，不当作有效数据
                if (view.Cot == CotCodes.GenericReadInvalid)
                {
                    Logger?.Warning("收到通用分类读命令无效响应（COT=43）");
                    break;
                }

                var (count, continuation) = NgdCodes.Decode(view.Data[1]);
                DecodeGenericSets(view.Data[2..], count, results, pendingUnknown);

                if (!continuation)
                {
                    break;
                }
            }
        }, ct, request.Ca, DataCallResponses).ConfigureAwait(false);

        foreach (var entry in pendingUnknown)
        {
            UnknownGenericDataReceived?.Invoke(entry);
        }

        return results;
    }

    private void DecodeGenericSets(
        ReadOnlySpan<byte> payload,
        int count,
        List<GenericDataSet> results,
        List<Iec103GenericUnknownEntry> unknown)
    {
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            if (!GenericDataSet.TryDecode(payload[offset..], out var set, out var consumed) || set is null)
            {
                Logger?.Warning($"通用分类数据集解析失败（第 {i + 1}/{count} 条，偏移 {offset}）");
                break;
            }

            results.Add(set);
            offset += consumed;

            // 私有 GIN 处理器（仅用于诊断/附加解码，数据仍完整保留在 set 中）
            if (PrivateTypes.TryResolveGin(set.Gin, out var handler) && handler is not null)
            {
                Logger?.Debug($"通用分类 {set.Gin} 由 {handler.VendorTag} 处理: "
                    + handler.Describe(set.Gin, set.Kod, set.Gdd, set.Gid));
            }
            else if (!PrivateTypes.IsStandardEntry(set))
            {
                unknown.Add(new Iec103GenericUnknownEntry(set.Gin, set.Kod, set.Gdd, set.Gid, "未登记私有 GIN"));
            }

            if (offset >= payload.Length)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 从链路应答帧中提取 ASDU 并加入收集列表；返回是否收到总召唤终止帧。
    /// </summary>
    /// <remarks>
    /// 103 的从站可能在<b>任何</b>应答里携带用户数据（FC=8 以数据响应），
    /// 包括对 SEND/CONFIRM 的确认。忽略这一点会丢掉总召唤的第一条数据。
    /// </remarks>
    private bool CollectFromResponse(Ft12Frame response, List<Asdu> collected)
    {
        if (response.Format != Ft12FrameFormat.VariableLength || response.Asdu.Length < Asdu.HeaderLength)
        {
            return false;
        }

        if (!Asdu.TryDecode(response.Asdu, out var asdu, out var error) || asdu is null)
        {
            Logger?.Warning($"应答帧 ASDU 解析失败: {error}");
            return false;
        }

        collected.Add(asdu);
        return asdu.TypeId == TypeIds.GeneralInterrogationEnd;
    }

    /// <summary>
    /// 串行化执行一轮链路服务：持锁 + 建立事务 + linked 取消 + 收尾清空。
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item>状态检查移入锁内（原实现在锁外检查，等待锁期间被 Disconnect 置 null 会 NRE）。</item>
    ///   <item>每轮服务建立一个 <see cref="LinkTransaction103"/>，业务代码只从它读应答，
    ///         迟到帧不会串到下一事务。</item>
    ///   <item><b>事务按本次操作的链路地址校验</b>：一条通道下挂多台装置时，装置 A 的
    ///         迟到应答必须被装置 B 的事务拒绝，否则 B 的召唤结果会混入 A 的数据。</item>
    ///   <item>用 <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/>
    ///         把调用方 ct 与生命周期 ct 关联：Disconnect 能取消在途事务，不再悬挂 8 秒。</item>
    ///   <item>finally 里 End 事务并释放锁；Dispose 前不会再有活动事务。</item>
    /// </list>
    /// </remarks>
    /// <param name="action">链路服务主体。</param>
    /// <param name="ct">调用方取消令牌。</param>
    /// <param name="expectedAddress">
    /// 本轮期望的链路地址（即 <c>ca</c>）。传 <c>null</c> 表示不校验地址
    /// （仅在无法确定目标地址的特殊场景使用）。
    /// </param>
    /// <param name="acceptedFunctions">本轮可接受的从站功能码集合。</param>
    private async Task ExecuteLinkServiceAsync(
        Func<LinkLayerEngine, LinkTransaction103, CancellationToken, Task> action,
        CancellationToken ct,
        byte? expectedAddress = null,
        IReadOnlySet<byte>? acceptedFunctions = null)
    {
        ThrowIfDisposed();

        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        LinkTransaction103? transaction = null;
        CancellationTokenSource? linked = null;

        try
        {
            var engine = _engine;
            if (engine is null || !_connected)
            {
                throw new InvalidOperationException("链路未连接，请先调用 ConnectAsync");
            }

            // 把生命周期取消与调用方取消合并，使 DisconnectAsync 能收束在途事务
            var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);

            // 事务按「本轮目标地址 + 期望的从站功能码集合」做相关性校验：
            // 之前 Begin() 不带地址 → 上一轮/上一台装置的迟到帧会被本轮 await 消费掉，
            // 表现为「取到别人的数据却认为成功」。多装置共通道时这是必现缺陷。
            transaction = _transactions.Begin(expectedAddress, acceptedFunctions);
            await action(engine, transaction, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
            {
                _transactions.End(transaction);
            }

            linked?.Dispose();
            _transactionLock.Release();
        }
    }

    /// <summary>后台读循环：读字节 → 分帧 → 分类（链路应答 / 用户数据）。</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var transport = Volatile.Read(ref _transport);
        if (transport is null)
        {
            return;
        }

        Exception? failure = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await transport.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 正常关闭（Disconnect/Dispose 取消生命周期令牌）
                    break;
                }

                if (read == 0)
                {
                    // 对端关闭（传输队列完成）：不能静默 break，否则 IsConnected
                    // 仍报 true、传输也不释放。
                    break;
                }

                var frames = _parser.Feed(buffer.AsSpan(0, read));
                foreach (var frame in frames)
                {
                    DispatchFrame(frame);
                }

                // 半帧超时复位：线路中断或装置发帧不全时避免状态机卡死
                _parser.TryResetStale(_linkParameters.CharacterTimeoutMs);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // 所有退出路径统一收尾：置 _connected=false、结束事务、上报一次 Disconnected
            // 并触发后台释放传输。Dispose 路径下由 DisposeAsync 负责收尾，不重复上报。
            if (!_disposed)
            {
                HandleReceiveLoopExit(failure, transport);
            }
        }
    }

    /// <summary>
    /// 读循环退出的统一收尾（幂等）。
    /// </summary>
    /// <remarks>
    /// 原实现内层 catch 与 <c>read == 0</c> 分支直接 break：既不置
    /// <c>_connected=false</c>，也不释放传输，被动断线后 IsConnected 仍为 true，
    /// ConnectAsync 因 <c>if (_connected) return</c> 永远无法恢复。
    /// </remarks>
    private void HandleReceiveLoopExit(Exception? failure, IIec103LinkTransport? loopTransport)
    {
        // 该循环若已不是当前传输（重连已接管），绝不能改动全局状态：
        // 否则旧循环的退出会把新连接的 _connected 清成 false。
        if (loopTransport is not null && !ReferenceEquals(Volatile.Read(ref _transport), loopTransport))
        {
            ScheduleTransportDetach(loopTransport);
            return;
        }

        _connected = false;
        _transactions.End();

        if (failure is not null)
        {
            Logger?.Error($"接收循环异常退出: {failure}");
            RaiseDisconnectedOnce(failure.Message);
        }
        else
        {
            RaiseDisconnectedOnce("连接已关闭");
        }

        // fire-and-forget：此处可能正被 DisconnectAsync 等待（其持有生命周期锁），
        // 同步等待 DetachTransportAsync 会自锁。
        ScheduleTransportDetach(loopTransport ?? Volatile.Read(ref _transport));
    }

    /// <summary>上报一次 Disconnected（去重）。</summary>
    private void RaiseDisconnectedOnce(string? reason)
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
        {
            return;
        }

        RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.Disconnected, reason));
    }

    /// <summary>帧分派：先投当前事务（相关性校验），不匹配的帧另作 ASDU/诊断处理。</summary>
    /// <remarks>
    /// <para><b>所有同步回调都必须隔离异常</b>：用户订阅者抛异常不能击穿接收循环，
    /// 否则链路永久失效而 <c>IsConnected</c> 仍报 true（P0 缺陷）。</para>
    /// <para><b>可变帧也要投事务</b>：103 的「以数据响应」(FC=8) 既是本轮召唤的
    /// 链路应答也是应用数据，两者必须同时成立。</para>
    /// <para><b>不匹配/迟到的帧</b>：不当应答、也不丢弃——可变帧走 ASDU 事件，
    /// 其余记诊断日志。这消除了「全局队列串答」的根因。</para>
    /// </remarks>
    private void DispatchFrame(Ft12Frame frame)
    {
        var raw = frame.ToBytes();

        SafeInvoke("RawFrameReceived", () => RawFrameReceived?.Invoke(raw));
        SafeInvoke("FrameTraced", () => FrameTraced?.Invoke("RX", frame));

        var isVariableData = frame.Format == Ft12FrameFormat.VariableLength
            && frame.Asdu.Length >= Asdu.HeaderLength;

        // 先尝试作为当前事务的应答。事务按 PRM=0 / 地址 / 功能码校验。
        var accepted = SafeInvoke("事务分派", () => _transactions.Dispatch(frame), false);

        if (isVariableData)
        {
            // 无论是否被事务接受，用户数据都要投递 ASDU 事件
            DispatchAsdu(frame.Asdu);
        }

        if (!accepted && !isVariableData)
        {
            // 没有活动事务、或帧不满足应答特征：可能是迟到的重复 ACK / 总线上的杂帧
            SafeInvoke("链路诊断", () =>
                Logger?.Debug($"[未匹配帧] {Convert.ToHexString(raw)}"));
        }
    }

    /// <summary>隔离单个同步回调的异常，保证接收循环继续运行。</summary>
    private void SafeInvoke(string name, Action action)
        => SafeInvoke(name, () => { action(); return true; }, false);

    private T SafeInvoke<T>(string name, Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            Logger?.Error($"[{name}] 回调异常已隔离，链路继续运行: {ex}");

            // 注意：这里【不能】再调 RaiseLinkEvent —— 它本身也是经 SafeInvoke 派发的，
            // 订阅者持续抛异常会形成「SafeInvoke → RaiseLinkEvent → SafeInvoke」无限递归。
            // 只记日志即可（诊断事件自身故障不应再引发新的故障）。
            return fallback;
        }
    }

    private void DispatchAsdu(byte[] asduBytes)
    {
        // 注意：Iec103AsduView 是 ref struct，不能捕获进 lambda，
        // 因此这里用显式 try/catch 而不是 SafeInvoke 包裹整段。
        try
        {
            var view = new Iec103AsduView(asduBytes);

            // ① 统一入口：标准报文与厂商私有报文都从这里出去。
            //    标准类型调 Iec103AsduDecoder.Decode；私有类型由业务代码按 TypeId 自行解析。
            AsduReceived?.Invoke(in view);

            // ② 兜底：既不是标准 TI、也没有登记处理器的报文走 UnknownAsduReceived，绝不丢帧。
            //    Throw 策略先投递事件再抛，且异常被本方法隔离，不会杀接收循环。
            if (!TypeIds.IsStandard(view.TypeId)
                && !PrivateTypes.TryResolve(view.TypeId, view.Fun, view.Inf, view.CotByte, out _))
            {
                var unknown = new Iec103UnknownAsdu(asduBytes, reason: $"未登记 TI={view.TypeId} FUN={view.Fun} INF={view.Inf}");

                if (_options.UnknownTypePolicy == Iec103UnknownTypePolicy.Log)
                {
                    Logger?.Warning(unknown.ToString());
                }

                UnknownAsduReceived?.Invoke(unknown);

                if (_options.UnknownTypePolicy == Iec103UnknownTypePolicy.Throw)
                {
                    throw new Iec103UnknownAsduException(unknown);
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.Error($"[ASDU 分派] 回调异常已隔离，链路继续运行: {ex}");
            try
            {
                RaiseLinkEvent(new Iec103LinkEvent(Iec103LinkEventKind.FrameRejected, $"ASDU 分派: {ex.Message}"));
            }
            catch
            {
                // 诊断事件本身也抛异常时不再外传
            }
        }
    }

    private void OnTransportClosed(object? sender, Iec103TransportClosedEventArgs e)
    {
        // 迟到/幽灵回调：sender 已不是当前传输（重连已接管）时，绝不能影响新链路——
        // 否则会把新连接的 _connected 清成 false、取消新的生命周期令牌。
        var closedTransport = sender as IIec103LinkTransport;
        if (closedTransport is not null && !ReferenceEquals(Volatile.Read(ref _transport), closedTransport))
        {
            return;
        }

        _connected = false;
        _transactions.End();

        // 与主动 DisconnectAsync 对称：被动断线也必须取消生命周期令牌，
        // 否则在途事务不会醒来，要一直等到 MessageTimeoutMs × (MaxRepeat+1)。
        try
        {
            _lifetimeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已 Dispose，忽略
        }

        RaiseDisconnectedOnce(e.Reason);
        Logger?.Info($"103 链路已断开: {e.Reason}");

        // 被动断线同样要释放传输：旧实现只清 _connected，TcpClient 连接对象
        // 与 TransportClosed 订阅一直残留（重连即泄漏）。
        ScheduleTransportDetach(sender as IIec103LinkTransport);
    }

    private static bool HasMoreClass1(Ft12Frame response)
    {
        var control = LinkControl.FromByte(response.Control);
        return control.FcbOrAcd; // 从站 ACD=1 表示还有 1 级数据
    }

    private void RaiseRawFrameReceived(byte[] raw)
        => SafeInvoke("RawFrameReceived", () => RawFrameReceived?.Invoke(raw));

    private void RaiseRawFrameSent(byte[] asduPayload)
        => SafeInvoke("RawFrameSent", () => RawFrameSent?.Invoke(asduPayload));

    private void RaiseLinkEvent(Iec103LinkEvent evt)
        => SafeInvoke("Iec103LinkEvent", () => Iec103LinkEvent?.Invoke(evt));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Iec103Client));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Dispose 与 DisconnectAsync 不能嵌套取同一把锁：内部统一走 DisconnectCoreAsync。
        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        // 不 Dispose(_transactionLock/_lifecycleLock)：DisconnectAsync 已等待在途事务排空，
        // 但并发的 DisposeAsync/迟到的 WaitAsync 仍可能在 finally 里 Release。
        // 主动 Dispose 会抛 ObjectDisposedException 掩盖原始异常；
        // SemaphoreSlim 无未托管资源，交给 GC 回收即可。
    }
}
