//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

using System.Net;

namespace IEC60870.CS104;

/// <summary>
/// 冗余组：一个事件队列 + 一组连接，组内同一时刻仅一个活动连接负责把队列快照
/// 按序排空发送；活动连接断开时自动提升一个备用连接，未发送/发送失败的事件
/// 通过队头回插保序重投，实现冗余切换不丢事件。
/// </summary>
/// <remarks>
/// 仅在 <see cref="ServerMode.MULTIPLE_REDUNDANCY_GROUPS"/> 下由用户显式创建并
/// <see cref="Iec104Server.AddRedundancyGroup"/>；其余模式下服务端内部自动建组。
/// 允许客户端列表为空的组是 catch-all 组，接收所有未匹配到具体组的连接。
/// </remarks>
public sealed class RedundancyGroup : IDisposable
{
    private readonly List<IPAddress> _allowedClients; // null/空 = catch-all
    private readonly List<Iec104Session> _standby = new();
    private readonly SemaphoreSlim _activeSignal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private AsduEventQueue _queue;
    private ApplicationLayerParameters _al;
    private Task _drainTask;
    private volatile Iec104Session _active;

    /// <summary>已释放标志（0=未释放，1=已释放）：保证 <see cref="Dispose"/> 幂等，且释放后不再操作已释放的 CTS/信号量。</summary>
    private int _disposed;

    /// <summary>
    /// 日志记录器（可选；为 null 时静默）。由 <see cref="Iec104Server"/> 在 SetupAsync 之后
    /// 从自身 Logger 转发，用于记录排空循环的致命异常（原先该异常被吞，循环死亡无痕迹）。
    /// </summary>
    internal ILog Logger { get; set; }

    /// <summary>创建一个冗余组。<paramref name="allowedClients"/> 为空时成为 catch-all 组。</summary>
    public RedundancyGroup(string name = "", params IPAddress[] allowedClients)
    {
        Name = name ?? string.Empty;
        _allowedClients = allowedClients is { Length: > 0 } ? new List<IPAddress>(allowedClients) : null;
    }

    /// <summary>组名（仅调试用途）。</summary>
    public string Name { get; }

    /// <summary>是否为兜底组（未显式指定允许客户端）。</summary>
    public bool IsCatchAll => _allowedClients == null;

    /// <summary>当前队列中待发送的 ASDU 数量（未初始化时为 0）。</summary>
    public int QueueLength => _queue?.Count ?? 0;

    /// <summary>该 IP 是否被本组显式允许（catch-all 组恒返回 false，由调用方另行处理）。</summary>
    internal bool Matches(IPAddress ip) => !IsCatchAll && ip != null && _allowedClients.Contains(ip);

    /// <summary>由服务端在加入时注入队列配置并启动排空循环。</summary>
    internal void Init(int maxQueueSize, EnqueueMode enqueueMode, ApplicationLayerParameters al)
    {
        if (_queue != null)
        {
            return; // 幂等：已初始化
        }

        _al = al;
        _queue = new AsduEventQueue(maxQueueSize, enqueueMode);
        _drainTask = Task.Run(DrainAsync);
    }

    /// <summary>
    /// 把一个 ASDU 编码为快照入队。入队即返回，实际发送由排空循环在连接可用时完成。
    /// </summary>
    /// <exception cref="ASDUQueueException">队列满且策略为 <see cref="EnqueueMode.THROW_EXCEPTION"/>。</exception>
    internal void Enqueue(ASDU asdu)
    {
        var snapshot = asdu.AsByteArray();

        if (snapshot == null)
        {
            throw new InvalidOperationException("ASDU 编码长度与预期不符，无法入队");
        }

        _queue.TryEnqueue(snapshot);
    }

    /// <summary>连接激活（收到 STARTDT_ACT）：无活动连接则设为主用，否则进备用。</summary>
    internal void OnActivated(Iec104Session session)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return; // 组已释放（服务端停止/Dispose）：不再接纳连接
        }

        bool becameActive;

        lock (_standby)
        {
            if (_active == null)
            {
                _active = session;
                becameActive = true;
            }
            else
            {
                _standby.Add(session);
                becameActive = false;
            }
        }

        if (becameActive)
        {
            SignalActive();
        }
    }

    /// <summary>连接关闭：若为主用则提升备用，否则仅从备用列表移除。</summary>
    internal void OnClosed(Iec104Session session) => ClearActiveAndPromote(session);

    /// <summary>
    /// STOPDT 去激活：复用主用提升语义，但不停止连接（会话仍存活，可再次 STARTDT）。
    /// 与 <see cref="OnClosed"/> 分开，避免把「去激活」误当成「连接关闭」。
    /// </summary>
    internal void OnDeactivated(Iec104Session session) => ClearActiveAndPromote(session);

    private void ClearActiveAndPromote(Iec104Session dead)
    {
        var promoted = false;

        lock (_standby)
        {
            _standby.Remove(dead);

            if (ReferenceEquals(_active, dead))
            {
                _active = _standby.Count > 0 ? _standby[0] : null;

                if (_active != null)
                {
                    _standby.RemoveAt(0);
                    promoted = true;
                }
            }
        }

        if (promoted)
        {
            SignalActive();
        }
    }

    /// <summary>唤醒排空循环（信号量已释放时静默忽略：服务端停止/Dispose 与事件回调存在竞态）。</summary>
    private void SignalActive()
    {
        try
        {
            _activeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            var ct = _cts.Token;

            while (!ct.IsCancellationRequested)
            {
                // 先等到有活动连接再出队：避免把一条快照"挂"在循环里等待，
                // 使队列计数准确、且无活动连接时事件完整滞留队列。
                var target = _active;

                while (target == null)
                {
                    try
                    {
                        await _activeSignal.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    target = _active;
                }

                byte[] snapshot;

                try
                {
                    snapshot = await _queue.DequeueAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (snapshot == null)
                {
                    // 队列已 Clear（停止/取消后处于 completed 态）：无数据可取，退出循环。
                    // 不再空转等待，也不把它当作正常快照发送。
                    break;
                }

                try
                {
                    await target.SendEncodedAsync(snapshot, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _queue.RequeueFront(snapshot);

                    // OCE 有两种来源，必须区分：
                    //  1) 循环自身的取消令牌（服务端停止）→ 退出排空循环；
                    //  2) 目标连接已失效（ApduConnection.ThrowIfFailed 抛
                    //     OperationCanceledException("connection failed: …")，token 未取消）
                    //     → 这是【连接失效】，不是停止信号：必须清主用并提升备用，
                    //       否则失效后循环永久停摆、事件不断堆积、备用永不接管。
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    ClearActiveAndPromote(target);
                }
                catch
                {
                    // 目标连接在发送中失效：快照回插队头，清主用并提升备用，循环继续。
                    _queue.RequeueFront(snapshot);
                    ClearActiveAndPromote(target);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（Cancel 已取消令牌）：静默退出。
        }
        catch (Exception ex)
        {
            // 兜底：单点非 OCE 异常（如队列 Clear 竞态、信号量已释放、发送链路的
            // 非取消异常）绝不能让排空循环"死掉后无声无息" —— 原先只有
            // OperationCanceledException 被捕获，一个 InvalidOperationException
            // 就会让该组此后永不发送，且无异常无日志。
            // 记录后退出（不重试，避免异常每轮刷屏；服务端 Stop/重建会接手）。
            Logger?.Warning("CS104 redundancy group '" + Name + "' drain loop terminated: "
                + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 请求停止排空循环：取消令牌、清空队列、唤醒挂起的等待者。
    /// 幂等；不等待循环退出（同步兜底路径用，见 <see cref="Iec104Server.SafetyDispose"/>）。
    /// </summary>
    internal void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return; // 已 Dispose：CTS/信号量已释放，不能再触碰
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已停止过
        }

        _queue?.Clear();
        SignalActive();
    }

    /// <summary>停止排空循环并清空队列（服务端停止时调用）。</summary>
    internal async Task StopAsync()
    {
        if (_queue == null)
        {
            return;
        }

        Cancel();

        var drain = _drainTask;
        if (drain == null)
        {
            return;
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch
        {
            // 排空循环在取消路径上的异常无需上抛
        }
    }

    /// <summary>
    /// 释放组持有的生命周期资源（CTS、信号量、备用连接引用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原先 <see cref="Cancel"/> / <see cref="StopAsync"/> 只 Cancel，<see cref="CancellationTokenSource"/>
    /// 与 <see cref="SemaphoreSlim"/> 永不释放；服务端每次 Stop→Start、每个会话专属组都留下
    /// 一份未释放资源（500 装置 = 500 份/轮）。
    /// </para>
    /// <para>
    /// 幂等：重复调用（例如 SafetyDispose 与 StopAsync 都经过清理）无副作用，不抛异常。
    /// 释放前先 Cancel 并唤醒排空循环；循环对由此产生的 ODE/OCE 已由 <see cref="DrainAsync"/>
    /// 的兜底 catch 处理。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _queue?.Clear();
        SignalActive();

        try
        {
            _cts.Dispose();
        }
        catch
        {
            // 并发释放：忽略
        }

        try
        {
            _activeSignal.Dispose();
        }
        catch
        {
            // 并发释放/仍有等待者：忽略
        }

        lock (_standby)
        {
            _standby.Clear();
        }

        _active = null;
    }
}
