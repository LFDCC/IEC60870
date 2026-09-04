//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 104 端点宿主组件：聚合 <see cref="ApduConnection"/> 状态机、生命周期 CTS、
/// T1/T2/T3 定时器循环与上行原始帧事件，由 <see cref="Iec104Client"/>（主站）与
/// <see cref="Iec104Session"/>（从站会话）共用，消除两端的重复接线代码。
/// TouchSocket 契合点（连接/关闭/适配器装载）仍由各宿主端自行负责。
/// </summary>
internal sealed class ApduEndpoint
{
    private readonly APCIParameters _apci;
    private readonly ApplicationLayerParameters _al;
    private readonly IApduSink _sink;

    private ApduConnection _connection;
    private CancellationTokenSource _cts;
    private Iec104TimerScheduler _scheduler;
    private volatile bool _intentionalClose;

    /// <param name="sink">发送管道（即宿主端自身，借道其 TouchSocket SendAsync）。</param>
    public ApduEndpoint(APCIParameters apci, ApplicationLayerParameters al, IApduSink sink)
    {
        _apci = apci ?? throw new ArgumentNullException(nameof(apci));
        _al = al ?? throw new ArgumentNullException(nameof(al));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// 日志记录器（TouchSocket <see cref="ILog"/>）。由宿主端在 Rebuild 时从自身
    /// <c>Logger</c>（SetupAsync 自容器解析）转发；为 null 时静默。
    /// </summary>
    public ILog Logger { get; set; }

    /// <summary>当前连接状态机（重建/清理后可能为 null）。</summary>
    public ApduConnection Connection => _connection;

    public ConnectionCloseReason? CloseReason => _connection?.CloseReason;

    public bool IsIntentionalClose => _intentionalClose;

    /// <summary>
    /// 上行原始报文（对端→本端）。参数为完整单个 APDU（含 0x68 APCI 头），
    /// 帧数据已拷贝为 byte[]，订阅者可安全长期持有；仅在有订阅者时分配，无订阅者零开销。
    /// </summary>
    public event Action<byte[]> RawFrameReceived;

    /// <summary>
    /// 原始帧捕获开关（适配器 <see cref="Iec104Adapter"/> 每帧解析时读取最新值）：
    /// 存在订阅者时为 true，指示适配器保留完整帧拷贝。
    /// </summary>
    internal bool HasRawFrameSubscribers => RawFrameReceived is not null;

    // ── 生命周期管理 ─────────────────────────────────────────────

    /// <summary>
    /// 新建（或重连重建）状态机与生命周期 CTS：先释放旧实例避免重连泄漏；
    /// <paramref name="wireEvents"/> 把外部事件订阅（AsduReceived/EventHandler/内置确认匹配）
    /// 接到新连接上，实现"先订阅后连接/重连"。
    /// </summary>
    /// <param name="externalToken">外部取消令牌（客户端建连超时用；会话传 default）。</param>
    /// <param name="closeAsync">致命超时时的宿主端关闭动作（TouchSocket CloseAsync）。</param>
    public void Rebuild(CancellationToken externalToken, Action<ApduConnection> wireEvents, Func<Task> closeAsync)
    {
        _scheduler?.Dispose();
        _cts?.Dispose();
        _connection?.Dispose();

        _intentionalClose = false;
        _connection = new ApduConnection(_apci, _al, _sink);
        wireEvents?.Invoke(_connection);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _scheduler = new Iec104TimerScheduler(
            token => _connection.CheckTimeoutsAsync(token),
            _connection.GetSuggestedDelayMs,
            OnSchedulerFatal,
            _cts.Token);

        void OnSchedulerFatal()
        {
            _connection.MarkCloseReason(ConnectionCloseReason.Timeout);
            _ = closeAsync();
        }
    }

    /// <summary>标记本次关闭由本地主动发起，避免重复派发 ConnectionClosed。</summary>
    public void MarkIntentionalClose() => _intentionalClose = true;

    /// <summary>取消生命周期令牌（触发定时器循环/发送背压退出）。</summary>
    public void CancelLife()
    {
        try
        {
            _cts?.Cancel(); } catch { /* already disposed */ }
    }

    /// <summary>非主动关闭（远端断开/超时/协议错误）时派发 ConnectionClosed 事件。</summary>
    public void NotifyClosedIfUnexpected()
    {
        if (!_intentionalClose)
        {
            _connection?.NotifyClosed();
        }
    }

    /// <summary>释放连接状态机。不释放 CTS：重连器可能已并发重建（见 Iec104Client）。</summary>
    public void CleanupConnection()
    {
        _scheduler?.Dispose();
        _connection?.Dispose();
    }

    /// <summary>
    /// 会话式终结：取消并释放 CTS、调度器与状态机（会话随连接终结，无重连语义；可重入）。
    /// </summary>
    public void FinalizeSession()
    {
        CancelLife();
        _scheduler?.Dispose();
        _connection?.Dispose();
        try
        {
            _cts?.Dispose();
        }
        catch { /* already disposed */ }
    }

    // ── 接收与定时 ───────────────────────────────────────────────

    /// <summary>
    /// 处理一帧上行 APDU：派发原始帧事件 → 送状态机解析入队 → 冲刷待发控制帧。
    /// 返回 false 表示协议错误，宿主端应关闭连接。
    /// </summary>
    public async ValueTask<bool> HandleReceivedAsync(IecApdu apdu)
    {
        // 上行原始报文：仅在存在订阅者时适配器才保留帧拷贝（RawFrame 非 null），零订阅零拷贝
        if (apdu.RawFrame != null)
        {
            RawFrameReceived?.Invoke(apdu.RawFrame);
        }

        if (!_connection.ProcessApdu(apdu))
        {
            _connection.MarkCloseReason(ConnectionCloseReason.ProtocolError);
            return false;
        }

        await _connection.PumpAsync(_cts.Token).ConfigureAwait(false);
        return true;
    }

    // ── 令牌链接 ─────────────────────────────────────────────────

    /// <summary>
    /// 将外部 <see cref="CancellationToken"/> 与连接生命周期 <c>_cts</c> 链接的轻量句柄。
    /// 仅在传入可取消的 token 时才真正分配 <see cref="CancellationTokenSource"/>，
    /// 并在 <c>using</c> 结束时释放，避免热路径（每次 SendAsync）泄漏 CTS。
    /// 无可取消外部 token 时复用 <c>_cts.Token</c>，零分配。
    /// </summary>
    internal readonly struct CtsLink(CancellationTokenSource src, bool owns) : IDisposable
    {
        public CancellationToken Token => src?.Token ?? default;
        public void Dispose()
        {
            if (owns)
            {
                src?.Dispose();
            }
        }
    }

    public CtsLink LinkScoped(CancellationToken ct)
        => ct.CanBeCanceled
            ? new CtsLink(CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token), owns: true)
            : new CtsLink(_cts, owns: false);
}
