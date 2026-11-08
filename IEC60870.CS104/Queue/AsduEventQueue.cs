//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 有界异步事件队列：存放已编码的 ASDU 快照（不含 APCI 头的字节数组）。
/// 生产者（应用层 <c>EnqueueAsync</c>）与消费者（冗余组排空循环）解耦，
/// 队列满时按 <see cref="EnqueueMode"/> 处理；支持 <see cref="RequeueFront"/>
/// 把发送失败的快照放回队头，保证冗余切换后的顺序与至少一次投递。
/// </summary>
/// <remarks>
/// 与同步库的环形缓冲不同，这里用 <see cref="LinkedList{T}"/> + <see cref="SemaphoreSlim"/>
/// 实现：链表天然支持 O(1) 队头/队尾操作（重发回插需要队头插入），
/// 信号量提供异步背压唤醒，无需轮询。快照在入队时即完成编码，
/// 因此调用方复用同一个 <see cref="ASDU"/> 对象也不会污染队列内容。
/// </remarks>
internal sealed class AsduEventQueue
{
    private readonly int _maxSize;
    private readonly EnqueueMode _enqueueMode;
    private readonly object _gate = new();
    private readonly LinkedList<byte[]> _items = new();
    private readonly SemaphoreSlim _notEmpty = new(0);

    /// <summary>
    /// 队列已作废标志（<see cref="Clear"/> 后置位）。作废后 <see cref="DequeueAsync"/>
    /// 返回 null 表示"无数据"，由排空循环退出，而不是抛异常。
    /// </summary>
    private bool _completed;

    public AsduEventQueue(int maxSize, EnqueueMode enqueueMode)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), "队列容量必须为正数");
        }

        _maxSize = maxSize;
        _enqueueMode = enqueueMode;
    }

    /// <summary>当前队列中的 ASDU 数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// 入队一条已编码快照。
    /// </summary>
    /// <param name="snapshot">ASDU 编码字节（队列接管所有权，不再拷贝）。</param>
    /// <returns>
    /// <c>true</c> 已入队；<c>false</c> 表示队列满且策略为 <see cref="EnqueueMode.IGNORE"/>（快照被丢弃）。
    /// </returns>
    /// <exception cref="ASDUQueueException">队列满且策略为 <see cref="EnqueueMode.THROW_EXCEPTION"/>。</exception>
    public bool TryEnqueue(byte[] snapshot)
    {
        lock (_gate)
        {
            if (_items.Count >= _maxSize)
            {
                switch (_enqueueMode)
                {
                    case EnqueueMode.REMOVE_OLDEST:
                        _items.RemoveFirst();
                        // 同时收回被淘汰元素对应的许可：否则每淘汰一条就多出一个
                        // "有许可、无元素"的幽灵许可，消费者取到它会走到下面的空队分支
                        // 并抛 InvalidOperationException（默认 REMOVE_OLDEST 下队列一满就触发，
                        // 排空循环随即永久死亡）。Wait(0) 非阻塞，锁内调用安全。
                        _notEmpty.Wait(0);
                        break;
                    case EnqueueMode.IGNORE:
                        return false;
                    case EnqueueMode.THROW_EXCEPTION:
                        throw new ASDUQueueException("事件队列已满");
                }
            }

            _items.AddLast(snapshot);
        }

        _notEmpty.Release();
        return true;
    }

    /// <summary>
    /// 把一条快照放回队头（发送失败/冗余切换时保序重发）。
    /// 容量语义：重插不检查容量——此前出队已腾出空位，必然放得下。
    /// </summary>
    public void RequeueFront(byte[] snapshot)
    {
        lock (_gate)
        {
            _items.AddFirst(snapshot);
        }

        _notEmpty.Release();
    }

    /// <summary>
    /// 异步取出队头快照；队列空时挂起等待。
    /// </summary>
    /// <returns>队头快照；当队列已被 <see cref="Clear"/> 作废时返回 <c>null</c>（表示"无数据"，调用方应退出消费循环）。</returns>
    /// <remarks>
    /// 许可（SemaphoreSlim）在本设计中是"可能有一条数据"的信号，不保证与元素严格一对一：
    /// <see cref="EnqueueMode.REMOVE_OLDEST"/> 淘汰与 <see cref="Clear"/> 归还许可都会留下
    /// "多余许可"。因此取到许可但队列为空时消费掉该许可并重新等待，而不是抛
    /// <see cref="InvalidOperationException"/> —— 后者会让排空循环永久死亡（原缺陷：队列一满
    /// 或 Clear 竞态即触发）。
    /// </remarks>
    public async Task<byte[]> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _notEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_completed)
                {
                    // Clear() 之后队列已作废：返回"无数据"，不得抛异常。
                    return null;
                }

                var node = _items.First;

                if (node == null)
                {
                    // 多余许可（REMOVE_OLDEST 淘汰 / Clear 竞态）：消费之，继续等待真实数据。
                    continue;
                }

                _items.RemoveFirst();
                return node.Value;
            }
        }
    }

    /// <summary>清空队列并归还全部等待许可（服务端停止时调用）。此后 <see cref="DequeueAsync"/> 返回 null。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _completed = true;

            var drained = _items.Count;
            _items.Clear();

            for (var i = 0; i < drained; i++)
            {
                _notEmpty.Release();
            }
        }
    }
}
