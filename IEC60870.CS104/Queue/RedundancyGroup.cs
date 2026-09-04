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
public sealed class RedundancyGroup
{
    private readonly List<IPAddress> _allowedClients; // null/空 = catch-all
    private readonly List<Iec104Session> _standby = new();
    private readonly SemaphoreSlim _activeSignal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private AsduEventQueue _queue;
    private ApplicationLayerParameters _al;
    private Task _drainTask;
    private volatile Iec104Session _active;

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
            _activeSignal.Release();
        }
    }

    /// <summary>连接关闭：若为主用则提升备用，否则仅从备用列表移除。</summary>
    internal void OnClosed(Iec104Session session) => ClearActiveAndPromote(session);

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
            _activeSignal.Release();
        }
    }

    private async Task DrainAsync()
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

            try
            {
                await target.SendEncodedAsync(snapshot, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _queue.RequeueFront(snapshot);
                break;
            }
            catch
            {
                // 目标连接在发送中失效：快照回插队头，清主用并提升备用，循环继续。
                _queue.RequeueFront(snapshot);
                ClearActiveAndPromote(target);
            }
        }
    }

    /// <summary>
    /// 请求停止排空循环：取消令牌、清空队列、唤醒挂起的等待者。
    /// 幂等；不等待循环退出（同步兜底路径用，见 <see cref="Iec104Server.SafetyDispose"/>）。
    /// </summary>
    internal void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已停止过
        }

        _queue?.Clear();
        _activeSignal.Release();
    }

    /// <summary>停止排空循环并清空队列（服务端停止时调用）。</summary>
    internal async Task StopAsync()
    {
        if (_queue == null)
        {
            return;
        }

        Cancel();

        try
        {
            await _drainTask.ConfigureAwait(false);
        }
        catch
        {
            // 排空循环在取消路径上的异常无需上抛
        }
    }
}
