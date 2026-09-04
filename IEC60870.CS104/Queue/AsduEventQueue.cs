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

    /// <summary>异步取出队头快照；队列空时挂起等待。</summary>
    public async Task<byte[]> DequeueAsync(CancellationToken cancellationToken)
    {
        await _notEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var node = _items.First;

            if (node == null)
            {
                // 理论上不可达：每个入队都对应一次 Release。防御性归还许可。
                _notEmpty.Release();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("事件队列许可与内容不一致");
            }

            _items.RemoveFirst();
            return node.Value;
        }
    }

    /// <summary>清空队列并归还全部等待许可（服务端停止时调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var drained = _items.Count;
            _items.Clear();

            for (var i = 0; i < drained; i++)
            {
                _notEmpty.Release();
            }
        }
    }
}
