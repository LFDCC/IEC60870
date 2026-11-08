//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 103 主站链路事务：一问一答的相关性匹配。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：103 非平衡链路上，主站每发一个请求帧
/// （固定帧或 SEND/CONFIRM 可变帧）都期待一个从站应答。若用「全局队列 + 睡眠轮询」，
/// 任何迟到帧（上一轮超时重发留下的重复应答、突发上送的数据帧）都会被下一个事务
/// 当作自己的答案，造成读写错位。</para>
/// <para>本类把「当前事务」显式化：接收循环只向<b>当前活动事务</b>投递帧，
/// 事务按 <c>PRM=0</c>、链路地址、期望的从站功能码集合做校验；
/// 不匹配的帧交给 <see cref="Iec103Client"/> 的 <c>UnmatchedFrame</c> 处理
/// （作为 ASDU 或诊断事件），绝不静默丢弃、也绝不污染下一事务。</para>
/// <para>事务结束（成功/超时/取消）后调用 <see cref="Complete"/> 清空，
/// 之后到达的帧不再属于任何事务。</para>
/// </remarks>
internal sealed class LinkTransaction103
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Queue<Ft12Frame> _frames = new();
    private bool _completed;

    /// <summary>本事务期待的链路地址；<c>null</c> 表示不校验地址。</summary>
    /// <remarks>
    /// 默认不校验：事务在 <c>ExecuteLinkServiceAsync</c> 里建立，而链路地址
    /// 要到 lambda 内才由 <c>engine.Address = ca</c> 设定，建立时拿不到正确值。
    /// TCP 单连接与 RS-485 地址过滤已在传输/链路层保证，无需在事务层重复。
    /// </remarks>
    public byte? ExpectedAddress { get; }

    /// <summary>
    /// 本事务可接受的从站功能码集合。
    /// <c>null</c> 表示接受任意 PRM=0 的帧（用于兼容特殊装置）。
    /// </summary>
    public IReadOnlySet<byte>? AcceptedFunctions { get; }

    public LinkTransaction103(byte? expectedAddress = null, IReadOnlySet<byte>? acceptedFunctions = null)
    {
        ExpectedAddress = expectedAddress;
        AcceptedFunctions = acceptedFunctions;
    }

    /// <summary>
    /// 由接收循环投递帧。返回 true 表示帧被本事务接受并排队等待业务读取。
    /// </summary>
    public bool TryOffer(Ft12Frame frame)
    {
        if (!IsMatch(frame))
        {
            return false;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            _frames.Enqueue(frame);
        }

        _signal.Release(1);
        return true;
    }

    private bool IsMatch(Ft12Frame frame)
    {
        var control = LinkControl.FromByte(frame.Control);

        // 从站应答必须 PRM=0；PRM=1 的帧是总线上其它主站的请求，不该被本事务消费
        if (control.IsPrimary)
        {
            return false;
        }

        // 地址仅在显式指定时校验（默认不校验，见 ExpectedAddress 备注）
        if (ExpectedAddress.HasValue && frame.Address != ExpectedAddress.Value)
        {
            return false;
        }

        if (AcceptedFunctions is not null && !AcceptedFunctions.Contains(control.Function))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 等待并取出应答帧。取消（超时）时抛 <see cref="OperationCanceledException"/>；
    /// 事务已被 <see cref="Complete"/> 结束时抛 <see cref="Iec103TransactionAbortedException"/>。
    /// </summary>
    /// <remarks>
    /// 必须区分这两种情况：原先 <see cref="Complete"/> 只是 Release 一次信号，读方见队列为空
    /// 就抛 <c>OperationCanceledException</c>（token 往往并未取消），恰好命中上层
    /// <c>catch (OperationCanceledException) when (!ct.IsCancellationRequested)</c> 的
    /// <b>超时重发</b>分支 —— 事务"正常结束"被误报为"本帧超时"并多驱动一次重发。
    /// </remarks>
    public async ValueTask<Ft12Frame> ReadAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (_frames.Count > 0)
            {
                return _frames.Dequeue();
            }

            if (_completed)
            {
                throw new Iec103TransactionAbortedException();
            }
        }

        // 理论上不可达：每个 Release 对应一个入队帧或一次 Complete。
        // 兜底抛取消，让上层进入重发逻辑，绝不返回 null 伪造应答。
        throw new OperationCanceledException(ct);
    }

    /// <summary>结束事务，丢弃残留帧并拒绝后续投递。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _frames.Clear();
        }

        // 唤醒可能正在等待的读取方，使其走取消路径
        _signal.Release(1);
    }

    /// <summary>已排队但未被读取的帧数（诊断用）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }
}

/// <summary>
/// 主站侧事务调度器：保证同一时刻只有一个活动事务，并统一分派收到的帧。
/// </summary>
/// <remarks>
/// 与 <c>_transactionLock</c> 配合：<c>ExecuteLinkServiceAsync</c> 持锁期间
/// 通过 <see cref="Begin"/> 建立事务，业务代码 await <c>ReadAsync</c>，
/// <c>finally</c> 里 <see cref="End"/> 清空。接收循环只需调
/// <see cref="Dispatch"/>，不关心当前是谁在等。
/// </remarks>
internal sealed class LinkTransactionDispatcher103
{
    private readonly object _gate = new();
    private LinkTransaction103? _current;

    /// <summary>开始一个新事务；返回可等待的事务对象。</summary>
    public LinkTransaction103 Begin(byte? address = null, IReadOnlySet<byte>? acceptedFunctions = null)
    {
        var transaction = new LinkTransaction103(address, acceptedFunctions);
        lock (_gate)
        {
            _current?.Complete();
            _current = transaction;
        }

        return transaction;
    }

    /// <summary>
    /// 把收到的帧投递给当前事务。
    /// 返回 true 表示已作为应答排队；false 表示无活动事务或帧不匹配，
    /// 调用方应把它当作「非应答帧」（ASDU 上送/迟到的重复帧）另行处理。
    /// </summary>
    public bool Dispatch(Ft12Frame frame)
    {
        LinkTransaction103? current;
        lock (_gate)
        {
            current = _current;
        }

        return current is not null && current.TryOffer(frame);
    }

    /// <summary>结束当前事务。</summary>
    public void End(LinkTransaction103? transaction = null)
    {
        lock (_gate)
        {
            if (transaction is not null && !ReferenceEquals(_current, transaction))
            {
                // 已被更新事务替换，旧事务自己 Complete 即可
                transaction.Complete();
                return;
            }

            _current?.Complete();
            _current = null;
        }
    }

    /// <summary>当前活动事务（诊断用）。</summary>
    public LinkTransaction103? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }
}
