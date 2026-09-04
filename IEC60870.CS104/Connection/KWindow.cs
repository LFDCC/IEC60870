//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 k 窗口：已发未确认 I 帧环形缓冲 + 发送槽位背压 + O(1) 确认校验。
/// 从 <see cref="ApduConnection"/> 提取的纯数据组件。
/// </summary>
/// <remarks>
/// <para>
/// <b>线程模型</b>：本类自身无锁，全部方法【必须在宿主连接的 <c>_kLock</c> 临界区内
/// 调用】——连接用一把锁统一保护序列号/窗口/定时器字段，保证接收线程与定时器/
/// 发送线程间读写原子性与内存可见性（happens-before）。
/// </para>
/// <para>
/// <b>环形缓冲</b>：按发送顺序登记在途 I 帧；<see cref="Ack"/> 自最旧端成批移除
/// 已确认条目。条目 <c>SeqNo</c> 存 N(S)+1（模 32768），与对端 N(R)「下一个期望序号」
/// 语义一致，确认区间按模距离直接计算。
/// </para>
/// <para>
/// <b>槽位背压</b>：发送侧 <see cref="TryAcquireSlot"/>（有空槽同步通过，零分配热路径），
/// 满窗时 <see cref="EnqueueWaiter"/> 入队挂起；确认释放槽位时 <see cref="ReleaseSlots"/>
/// 将槽位【直接转移】给队头等待者（占用计数不变，逐个唤醒、无 thundering herd）。
/// 取消的调用方留在队列中，待槽位转移给它时由宿主归还并链式推进，槽位永不泄漏。
/// <see cref="Dispose"/> 唤醒全部等待者（取消），不悬挂。
/// </para>
/// </remarks>
internal sealed class KWindow
{
    /// <summary>单个已发送未确认 I 帧的登记条目。</summary>
    internal struct SentApdu
    {
        public long SentTime;
        public int SeqNo;
    }

    private readonly int _capacity;
    private readonly SentApdu[] _buffer;
    private int _oldest = -1;   // 最旧未确认条目的环形索引；-1 = 窗口空
    private int _newest = -1;   // 最新未确认条目的环形索引；-1 = 窗口空
    private int _nextSendSeq;   // 下一个待分配的 N(S)

    // ── 槽位背压（与环形缓冲同锁保护）──────────────────────────────
    private int _inFlight;
    private Queue<TaskCompletionSource<bool>> _waiters;
    private bool _disposed;

    /// <summary>创建容量为 <paramref name="capacity"/>（即 APCI 参数 K）的窗口。</summary>
    public KWindow(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _buffer = new SentApdu[capacity];
    }

    /// <summary>最旧未确认条目的环形索引；-1 = 窗口空（宿主定时器据此取 SentTime）。</summary>
    public int OldestIndex => _oldest;

    /// <summary>最旧未确认 I 帧的发送时刻（<see cref="Environment.TickCount64"/> 基准）；窗口空时为 0。</summary>
    public long OldestSentTime => _oldest == -1 ? 0 : _buffer[_oldest].SentTime;

    /// <summary>窗口是否仍有在途未确认 I 帧。</summary>
    public bool HasUnacked => _oldest != -1;

    /// <summary>下一个待分配的发送序列号 N(S)（窗口空时对端 N(R) 的唯一合法值）。</summary>
    public int NextSendSeq => _nextSendSeq;

    /// <summary>我方下一期望接收序号（对最后一个已收 I 帧 N(S)+1）；发送 I 帧时作为 N(R) 回填 APCI。</summary>
    public int NextRecvSeq { get; private set; }

    /// <summary>在途未确认条目数（0..K）。O(1)：由首尾环形索引直接推得。</summary>
    public int UnackedCount => _oldest == -1
        ? 0
        : (_oldest <= _newest
            ? _newest - _oldest + 1
            : _capacity - _oldest + _newest + 1);

    /// <summary>分配下一个发送序列号 N(S) 并推进（模 32768）。</summary>
    public int TakeSendSeq()
    {
        var seq = _nextSendSeq;
        _nextSendSeq = SequenceNumbers.Increment(_nextSendSeq);
        return seq;
    }

    /// <summary>接收侧推进：登记已收 N(S)=<paramref name="recvSeq"/> 的 I 帧，返回我方应回的 N(R)。</summary>
    public int AdvanceRecvSeq(int recvSeq)
    {
        NextRecvSeq = SequenceNumbers.Increment(recvSeq);
        return NextRecvSeq;
    }

    // ── 发送登记与确认 ───────────────────────────────────────────

    /// <summary>
    /// 登记一个已成功发送的 I 帧（其 N(S) 为 <paramref name="seqUsed"/>）。
    /// 须在发送成功后调用（异常路径不登记，保证条目与槽位一致）。
    /// </summary>
    public void RegisterSent(int seqUsed, long sentTime)
    {
        var newIndex = _oldest == -1 ? 0 : (_newest + 1) % _capacity;
        _buffer[newIndex].SeqNo = SequenceNumbers.Increment(seqUsed);
        _buffer[newIndex].SentTime = sentTime;
        _newest = newIndex;
        if (_oldest == -1)
        {
            _oldest = newIndex;
        }
    }

    /// <summary>
    /// 以对端 N(R) 确认并移除已确认帧——O(1) 模距离校验，替代旧版
    /// do/while+溢出标志的逐条扫描。
    /// </summary>
    /// <param name="seqNo">对端回送的接收序号 N(R)（其下一个期望的 N(S)）。</param>
    /// <param name="checkEnabled">宿主 CheckSequenceNumbers 开关；false 时跳过校验与移除。</param>
    /// <returns>释放的槽位数；-1 表示 N(R) 越界（协议错误，宿主应关闭连接）。</returns>
    /// <remarks>
    /// 记 S = 最旧在途帧的 N(S)（= 最旧条目 SeqNo − 1），缓冲内登记 SeqNo 为
    /// S+1 .. 下一发送序号（共 unacked 个，连续模 32768）。
    /// <para>
    /// 合法 N(R) ∈ [S, 下一发送序号]（模 32768 闭区间，共 unacked+1 个取值）：
    /// 下界 S 是对端对全部在途帧的冗余确认（N(R) = 最旧帧自身序号，确认到最旧前一拍），
    /// 释放 0 个；上界「下一发送序号」确认全部在途帧（窗口空时的唯一合法值）。
    /// 越界（区间外）返回 -1。
    /// </para>
    /// <para>
    /// 释放数 = (N(R) − S) mod 32768（冗余确认时为 0，确认全部时为 unacked），
    /// 区间合法保证该值 ≤ unacked，无需钳制。
    /// </para>
    /// </remarks>
    public int Ack(int seqNo, bool checkEnabled)
    {
        if (!checkEnabled)
        {
            return 0;
        }

        var unacked = UnackedCount;

        // 合法区间 [lower, upper]（模 32768 闭区间）：lower = S（最旧在途帧 N(S)，冗余确认），upper = 下一发送序号
        var upper = _nextSendSeq;
        var lower = _oldest == -1
            ? upper
            : (_buffer[_oldest].SeqNo + 32767) % 32768;

        // seqNo 位于区间内 ⇔ 从 lower 顺时针到 seqNo 的步数 ≤ unacked（区间共 unacked+1 个取值）
        if (SequenceNumbers.Distance(lower, seqNo) > unacked)
        {
            return -1;
        }

        // 释放数 = (N(R) − S) mod 32768；冗余确认（seqNo == lower）为 0，确认全部（seqNo == upper）为 unacked
        var freed = SequenceNumbers.Distance(lower, seqNo);

        while (freed-- > 0)
        {
            if (_oldest == _newest)
            {
                _oldest = -1;
                _newest = -1;
            }
            else
            {
                _oldest = (_oldest + 1) % _capacity;
            }
        }

        return unacked - UnackedCount;
    }

    // ── 槽位背压 ─────────────────────────────────────────────────

    /// <summary>
    /// 尝试同步占用一个发送槽位：有空槽（且无排队等待者）时占用并返回 true；
    /// 满窗返回 false（宿主改走 <see cref="EnqueueWaiter"/>）。
    /// 已 Dispose 时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public bool TryAcquireSlot()
    {
        if (_disposed)
        {
            throw new OperationCanceledException("connection has been disposed");
        }

        if (_inFlight < _capacity && (_waiters is null || _waiters.Count == 0))
        {
            _inFlight++;
            return true;
        }
        return false;
    }

    /// <summary>满窗时入队等待者，返回其 TCS 供宿主 await（TrySetResult=true = 槽位已转移）。</summary>
    public TaskCompletionSource<bool> EnqueueWaiter()
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        (_waiters ??= new Queue<TaskCompletionSource<bool>>()).Enqueue(waiter);
        return waiter;
    }

    /// <summary>
    /// 释放 <paramref name="count"/> 个槽位（发送失败归还 / 对端确认释放）。
    /// 优先将槽位直接转移给等待队列队头（计数不变），余量才归还占用计数。
    /// </summary>
    public void ReleaseSlots(int count)
    {
        if (count <= 0)
        {
            return;
        }

        while (count-- > 0)
        {
            if (_waiters is { Count: > 0 })
            {
                _waiters.Dequeue().TrySetResult(true);
            }
            else if (_inFlight > 0)
            {
                _inFlight--;
            }
        }
    }

    /// <summary>
    /// 终结窗口：置 disposed（后续 TryAcquireSlot 抛 OCE）并唤醒全部等待者（取消），
    /// 不悬挂。与宿主连接 Dispose 协同调用。
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (_waiters is { Count: > 0 })
        {
            var all = _waiters.ToArray();
            _waiters = null;
            foreach (var w in all)
            {
                w.TrySetCanceled();
            }
        }
    }
}
