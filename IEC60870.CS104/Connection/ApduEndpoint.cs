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
    private readonly IIec104ApduSink _sink;

    /// <summary>类型处理器注册表（透传给每个 ApduConnection；null = 全局默认）。</summary>
    private readonly AsduTypeHandlerRegistry _typeHandlers;

    private ApduConnection _connection;
    private CancellationTokenSource _cts;
    private Iec104TimerScheduler _scheduler;
    private volatile bool _intentionalClose;

    /// <summary>
    /// 连接代际号：每次 <see cref="Rebuild"/> +1。旧的失效收尾任务（<see cref="RequestTransportClose"/>
    /// 在 <see cref="Task.Run(Action)"/> 中的 finally）可能在新连接建立之后才执行，必须靠代际号
    /// 识别「我已过期」并跳过清理，否则会把刚刚重建的新连接/新调度器 Dispose 掉。
    /// </summary>
    private int _generation;

    /// <param name="sink">发送管道（即宿主端自身，借道其 TouchSocket SendAsync）。</param>
    public ApduEndpoint(APCIParameters apci, ApplicationLayerParameters al, IIec104ApduSink sink,
        AsduTypeHandlerRegistry typeHandlers = null)
    {
        _typeHandlers = typeHandlers;
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
    /// <param name="closeAsync">宿主端关闭动作（TouchSocket CloseAsync），参数为真实断开原因。</param>
    /// <param name="onFailed">
    /// 连接失效后【必须执行一次】的宿主清理（唤醒挂起的命令确认等待者 / 注销服务端会话等）。
    /// 由 <see cref="RequestTransportClose"/> 在关闭传输后无条件调用，<b>不依赖</b>
    /// TouchSocket 的 <c>OnTcpClosed</c> 回调 —— 实测该回调在部分关闭路径下不触发。
    /// 必须幂等（<c>OnTcpClosed</c> 正常触发时会重复调一次）。
    /// </param>
    public void Rebuild(CancellationToken externalToken, Action<ApduConnection> wireEvents,
        Func<ConnectionCloseReason, Task> closeAsync, Action onFailed = null)
    {
        // 代际号必须在释放旧资源之前递增：旧连接的收尾任务（见 RequestTransportClose）
        // 靠它判断自己是否已被新连接取代。若在末尾递增，旧任务会在窗口期内把新连接清掉。
        var generation = Interlocked.Increment(ref _generation);

        _scheduler?.Dispose();
        _cts?.Dispose();
        _connection?.Dispose();

        _intentionalClose = false;
        _transportCloseRequested = 0;
        _closeTransport = closeAsync;
        _onFailed = onFailed;
        _connection = new ApduConnection(_apci, _al, _sink, _typeHandlers);
        // 连接自身判定失效（发送失败等）时回到本端点统一处理：先同步派发 ConnectionClosed，
        // 再异步关闭传输层 —— 保证「状态/事件」与「传输关闭」解耦，见 FailConnection。
        // 同样做代际校验：旧连接的致命回调若在 Rebuild 之后才触发，不能关掉新连接。
        _connection.SetFatalHandler(reason =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            OnConnectionFailed(reason);
        });
        wireEvents?.Invoke(_connection);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _scheduler = new Iec104TimerScheduler(
            token => _connection.CheckTimeoutsAsync(token),
            _connection.GetSuggestedDelayMs,
            OnSchedulerFatal,
            _cts.Token);

        void OnSchedulerFatal()
        {
            // 致命超时同样走统一失效路径：原先只 MarkCloseReason + closeAsync，
            // 若底层关闭事件不触发（实测存在），应用层同样收不到 ConnectionClosed。
            // 旧调度器（已 Dispose）理论上不会再回调；仍校验代际以防极端竞态。
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            FailConnection(ConnectionCloseReason.Timeout);
        }
    }

    private Func<ConnectionCloseReason, Task> _closeTransport;
    private Action _onFailed;
    private int _transportCloseRequested;

    /// <summary>连接失效回调（由 <see cref="ApduConnection"/> 注入调用）。</summary>
    private void OnConnectionFailed(ConnectionCloseReason reason) => RequestTransportClose();

    /// <summary>
    /// 判定当前连接已失效：同步完成「记录原因 + 置非激活 + 派发一次
    /// <see cref="ApduConnectionEvent.ConnectionClosed"/>」，随后异步关闭传输层。
    /// </summary>
    /// <param name="reason">失效原因。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只靠 socket 关闭回调</b>：原先 <c>ConnectionClosed</c> 只在宿主
    /// <c>OnTcpClosed</c> 里派发。实测「协议错误 → 收包回调内 CloseAsync」这条路径下
    /// TouchSocket 的 Closed 事件不会触发，于是链路已死但事件不派发、<c>IsActive</c>
    /// 仍为 true，应用层既收不到通知也无法重连（500 装置压测下 7% 链路如此，
    /// 见 examples/104/cs104-stress-500/STRESS-TEST-REPORT.md §2.1）。
    /// </para>
    /// <para>
    /// 可在任意上下文调用（含接收回调内部与发送失败处）：状态与事件同步生效，
    /// 传输关闭经线程池异步执行，避免在 TouchSocket 收包回调内同步关闭适配器。
    /// 幂等：重复调用只关闭一次传输。
    /// </para>
    /// </remarks>
    public void FailConnection(ConnectionCloseReason reason)
    {
        _connection?.FailConnection(reason);
        RequestTransportClose();
    }

    private void RequestTransportClose()
    {
        var reason = _connection?.CloseReason ?? ConnectionCloseReason.RemoteClosed;
        // 先取关闭动作：未装配（理论上不会）时不消耗「已请求」标志，避免后续请求被吞掉
        var close = _closeTransport;
        if (close == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _transportCloseRequested, 1) != 0)
        {
            return;
        }

        // 捕获本次关闭所属的代际与宿主清理回调：Task.Run 可能在 Rebuild 之后才执行，
        // 届时字段已被新连接覆盖，不能再用 _onFailed / CleanupConnection() 的当前值。
        var generation = Volatile.Read(ref _generation);
        var onFailed = _onFailed;

        // 不在调用者上下文里同步关传输：可能正处在 TouchSocket 收包回调内部，
        // 同步 CloseAsync 会走到适配器拆解，实测导致套接字仍报在线、Closed 不再触发。
        _ = Task.Run(async () =>
        {
            try
            {
                // 代际校验必须放在任何副作用之前：close() 关闭的是宿主共享的
                // TouchSocket 连接；若期间已 Rebuild，这个旧请求关闭的将是新链路。
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                await close(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.Debug("FailConnection: transport close failed: " + ex.Message);
            }
            finally
            {
                // 代际校验：若期间已 Rebuild（重连），本任务属于旧连接，绝不能再清理
                // 新连接的调度器注册/状态机（否则新链路刚建就被 Dispose，表现为"连上即断"）。
                // 注意：finally 内不能 return（CS0157），用条件块表达同样的跳过语义。
                if (generation == Volatile.Read(ref _generation))
                {
                    // 端点自有的资源释放【必须】在这里完成，不交给宿主回调：
                    // 共享调度器注册（_liveCount / 物理 Timer）与状态机若只在
                    // OnTcpClosed 里释放，而该回调正是本次修复认定「可能不触发」的
                    // 路径，就会泄漏。放在这里使宿主无论怎么接线都不会漏。幂等。
                    CleanupConnection(generation);

                    // 宿主侧清理（唤醒挂起命令确认 / 注销会话）：即使 OnTcpClosed
                    // 不触发也必须执行，否则调用方要等满 CommandConfirmationTimeout。
                    // 回调要求幂等：OnTcpClosed 正常触发时会再走一次。
                    try
                    {
                        onFailed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger?.Debug("FailConnection: cleanup callback failed: " + ex.Message);
                    }
                }
            }
        });
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
    /// 仅当 <paramref name="generation"/> 仍是当前代际时才释放连接状态机。
    /// 供 <see cref="RequestTransportClose"/> 的异步收尾使用，避免旧连接的收尾
    /// 清理掉 <see cref="Rebuild"/> 新建的连接/调度器。
    /// </summary>
    /// <param name="generation">调用方捕获的代际号。</param>
    public void CleanupConnection(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return; // 已被更新的连接取代：旧代际的清理不能再碰当前资源
        }

        CleanupConnection();
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
            // 与 OnIFrame 的 AsduReceived 隔离对称：RawFrameReceived 订阅者用于抓包/落盘，
            // 抛一次异常（JSON 序列化、磁盘 IO）就会跳过下面的 ProcessApdu/PumpAsync
            // 并冒泡进 TouchSocket 适配器异常路径，导致 S 确认发不出甚至链路被拆。
            // 订阅者故障绝不能杀死链路，只记录到诊断字段与日志。
            try
            {
                RawFrameReceived?.Invoke(apdu.RawFrame);
            }
            catch (Exception ex)
            {
                _connection?.RecordProtocolErrorDetail($"RawFrameReceived callback error: {ex.Message}");
                Logger?.Debug("RawFrameReceived callback error: " + ex.Message);
            }
        }

        if (!_connection.ProcessApdu(apdu))
        {
            // 协议错误：序号歧义不可恢复，走统一失效路径（派发 ConnectionClosed + 异步关传输）。
            // 宿主端不再需要在自己收到 false 后同步 CloseAsync（那正是僵尸链路的成因）。
            FailConnection(ConnectionCloseReason.ProtocolError);
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
