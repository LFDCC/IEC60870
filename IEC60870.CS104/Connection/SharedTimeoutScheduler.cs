//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 多连接共享的 T1/T2/T3 调度器：单个物理 <see cref="System.Threading.Timer"/> 加
/// 按「绝对到期时刻」排序的分片最小堆，替代每连接一个自调度 Timer。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么</b>：每连接一个物理 Timer 时，10k 连接满载（窗口频繁非空、poll 落到
/// 10ms 下限）会产生每秒数十万次唤醒，实测占整机 8.6% CPU；且 10k 个内核 Timer 对象
/// 本身是隐形成本。共享后单 Timer 只在「最早 deadline」唤醒，醒来只处理已到期连接。
/// </para>
/// <para>
/// <b>各连接间隔不同怎么办</b>：排序键是<b>绝对到期时刻（Environment.TickCount64 毫秒）</b>，
/// 不是间隔。每个连接各自持有独立 <see cref="APCIParameters"/>（T1/T2/T3 可以完全不同），
/// 算出自己的下一个 deadline 后注册进来；早到期的排前、晚到期的排后，互不影响。
/// 这与 Netty <c>HashedWheelTimer</c> 同思路：共享的是「谁下一个到点」而非「多久查一次」。
/// </para>
/// <para>
/// <b>分片</b>：按连接 Id 哈希分 <see cref="ShardCount"/> 个独立堆 + 各自锁。连接在接收线程里
/// 重排 deadline 与调度线程出堆发生在不同分片上时不互斥；同分片才有锁竞争，把争用降到 1/S。
/// </para>
/// <para>
/// <b>惰性删除</b>：取消注册只置代位标记 + 清空槽位引用，堆内残留条目被弹出时按代位校验丢弃，
/// 不需要在堆里定位删除（O(1) 取消，与 Go runtime timer 的 modtimer 思路一致）。
/// </para>
/// <para>
/// <b>单例与释放</b>：进程级懒加载单例（<see cref="Instance"/>），首个连接创建、最后一个
/// 连接 Dispose 时关闭物理 Timer 并清空。测试可 <see cref="ForceShutdown"/> 强制关闭。
/// </para>
/// </remarks>
internal sealed class SharedTimeoutScheduler : IDisposable
{
    /// <summary>分片数：2 的幂便于位与哈希；16 分片下 10k 连接每分片约 625 条。</summary>
    private const int ShardCount = 16;
    private const int ShardMask = ShardCount - 1;

    /// <summary>进程级单例。</summary>
    private static SharedTimeoutScheduler _instance;

    /// <summary>单例锁。</summary>
    private static readonly object SingletonGate = new object();

    /// <summary>
    /// 获取进程级共享调度器；首个调用时创建（内部已加锁，多连接并发建连安全）。
    /// </summary>
    public static SharedTimeoutScheduler Instance
    {
        get
        {
            if (_instance != null)
            {
                return _instance;
            }

            lock (SingletonGate)
            {
                return _instance ??= new SharedTimeoutScheduler();
            }
        }
    }

    /// <summary>单分片堆 + 该分片锁。</summary>
    internal sealed class Shard
    {
        public readonly object Gate = new object();

        /// <summary>该分片的最小堆，元素为注册句柄。</summary>
        public readonly List<Entry> Heap = new List<Entry>();
    }

    private readonly Shard[] _shards;
    private readonly Timer _timer;

    /// <summary>注册序号（单调递增，仅用于把相邻连接错开到不同分片）。</summary>
    private long _nextId;

    /// <summary>当前存活（已注册未注销）的连接数，用于单例释放。</summary>
    private int _liveCount;

    /// <summary>已释放（最后一个连接注销后置位，之后所有操作静默忽略）。</summary>
    private volatile bool _shutdown;

    /// <summary>调度线程在跑标志（重入保护，同一时刻最多一次批处理）。</summary>
    private int _running;

    private SharedTimeoutScheduler()
    {
        _shards = new Shard[ShardCount];
        for (var i = 0; i < ShardCount; i++)
        {
            _shards[i] = new Shard();
        }

        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ═══════════════════════════════════════════════════════════════
    //  注册句柄（每连接一个，由 ApduEndpoint 持有并随连接生命周期释放）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 一个连接在调度器中的注册句柄。持有超时检查委托与致命回调；
    /// <see cref="Unregister"/> 必须且只能由宿主调用一次（<see cref="Dispose"/> 等价）。
    /// </summary>
    public sealed class Entry : IDisposable
    {
        internal readonly Func<CancellationToken, ValueTask<Iec104TimerScheduler.SchedulerAction>> Check;
        internal readonly Action OnFatal;
        internal readonly Func<int> DelayProvider;
        internal readonly CancellationToken CancellationToken;
        internal readonly SharedTimeoutScheduler Owner;

        /// <summary>所属分片（构造时按句柄哈希固定，避免重算）。</summary>
        internal readonly Shard Shard;

        /// <summary>代位：每次注册/重排 +1；堆内条目只有代位与句柄当前代位一致才有效。</summary>
        internal long Generation;

        /// <summary>当前在堆中的下标；-1 表示未入堆。</summary>
        internal int HeapIndex = -1;

        /// <summary>已出堆待执行（检查进行中）：此时应被重新入堆，而不是视为已取消。</summary>
        internal bool Dequeued;

        /// <summary>是否已被取消（Unregister 置位）：取消后不再入堆。</summary>
        internal bool Cancelled;

        /// <summary>下一次检查的绝对时刻（Environment.TickCount64 毫秒）。</summary>
        internal long Deadline;

        internal Entry(SharedTimeoutScheduler owner, Shard shard,
            Func<CancellationToken, ValueTask<Iec104TimerScheduler.SchedulerAction>> check,
            Func<int> delayProvider,
            Action onFatal,
            CancellationToken ct)
        {
            Owner = owner;
            Shard = shard;
            Check = check;
            DelayProvider = delayProvider;
            OnFatal = onFatal;
            CancellationToken = ct;
        }

        /// <summary>等价于 <see cref="SharedTimeoutScheduler.Unregister"/>：从共享调度器注销。幂等。</summary>
        public void Dispose()
        {
            Owner.Unregister(this);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  注册 / 取消 / 重排
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 注册一个连接。首次调度立即按 <paramref name="initialDelayMs"/> 到期。
    /// </summary>
    /// <param name="check">超时检查（即 <see cref="ApduConnection.CheckTimeoutsAsync"/>）；返回 false 表示致命超时。</param>
    /// <param name="delayProvider">非致命检查后的下一次延迟提供（即 <see cref="ApduConnection.GetSuggestedDelayMs"/>）。</param>
    /// <param name="onFatal">致命超时回调（宿主：标记 CloseReason + 关闭连接）。</param>
    /// <param name="cancellationToken">检查使用的取消令牌（连接生命周期令牌）。</param>
    /// <param name="initialDelayMs">首次检查延迟（毫秒，至少 1）。</param>
    public Entry Register(
        Func<CancellationToken, ValueTask<Iec104TimerScheduler.SchedulerAction>> check,
        Func<int> delayProvider,
        Action onFatal,
        CancellationToken cancellationToken,
        int initialDelayMs)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        if (delayProvider == null) throw new ArgumentNullException(nameof(delayProvider));
        if (onFatal == null) throw new ArgumentNullException(nameof(onFatal));

        // 分片按注册序号轮转：相邻连接错开分片，避免同批建连全落在同一片。
        var idx = (int)(Interlocked.Increment(ref _nextId) & ShardMask);
        var shard = _shards[idx];
        var entry = new Entry(this, shard, check, delayProvider, onFatal, cancellationToken);
        Interlocked.Increment(ref _liveCount);

        var due = Environment.TickCount64 + Math.Max(1, initialDelayMs);

        bool arm;
        lock (shard.Gate)
        {
            entry.Generation++;
            entry.Deadline = due;
            HeapPush(shard, entry);
            arm = !_shutdown;
        }

        if (arm)
        {
            ArmTimer();
        }

        return entry;
    }

    /// <summary>
    /// 取消注册（连接关闭/重建时调用）。幂等：重复调用无副作用。
    /// </summary>
    public void Unregister(Entry entry)
    {
        if (entry == null)
        {
            return;
        }

        var shard = entry.Shard;
        lock (shard.Gate)
        {
            if (entry.Cancelled)
            {
                return; // 已取消
            }

            entry.Cancelled = true;
            entry.Generation++;          // 使堆内残留条目失效（惰性删除）
            if (entry.HeapIndex >= 0)
            {
                HeapRemove(shard, entry.HeapIndex);
            }

            entry.HeapIndex = -1;
            entry.Dequeued = false;
        }

        // 引用计数：最后一个连接注销后释放单例，避免物理 Timer 常驻。
        // 注意：Register 里 Interlocked.Increment(_liveCount) 在返回前完成，
        // 若此处减到 0 说明没有在飞的新注册，ForceShutdown 安全。
        if (Interlocked.Decrement(ref _liveCount) <= 0)
        {
            ForceShutdown();
        }
    }

    /// <summary>
    /// 重排某连接的下一次检查时刻。由宿主在检查完成后按
    /// <see cref="ApduConnection.GetSuggestedDelayMs"/> 调用。
    /// </summary>
    public void Reschedule(Entry entry, int delayMs)
    {
        if (entry == null || _shutdown)
        {
            return;
        }

        var shard = entry.Shard;
        var due = Environment.TickCount64 + Math.Max(1, delayMs);

        lock (shard.Gate)
        {
            if (entry.Cancelled)
            {
                return; // 已取消
            }

            entry.Generation++;
            entry.Deadline = due;

            if (entry.Dequeued)
            {
                // 检查已出堆执行：重新入堆（推迟到堆顶右侧，保持最小堆性质）。
                HeapPush(shard, entry);
                entry.Dequeued = false;
            }
            else
            {
                HeapUpdate(shard, entry.HeapIndex);
            }
        }

        ArmTimer();
    }

    // ═══════════════════════════════════════════════════════════════
    //  物理 Timer 驱动
    // ═══════════════════════════════════════════════════════════════

    private void ArmTimer()
    {
        if (_shutdown)
        {
            return;
        }

        // 取所有分片的最早 deadline（分片锁逐个短暂持有，无嵌套锁）。
        var earliest = long.MaxValue;
        for (var i = 0; i < _shards.Length; i++)
        {
            var s = _shards[i];
            lock (s.Gate)
            {
                if (s.Heap.Count > 0)
                {
                    var d = s.Heap[0].Deadline;
                    if (d < earliest)
                    {
                        earliest = d;
                    }
                }
            }
        }

        if (earliest == long.MaxValue)
        {
            return; // 无待调度项
        }

        var delta = (int)Math.Clamp(earliest - Environment.TickCount64, 1, int.MaxValue);
        try
        {
            _timer.Change(delta, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 与关闭竞态：忽略，单例已停
        }
    }

    private void OnTimer(object state)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            // 极端竞态防御：一次性 Timer 语义下本不会并发；即便并发也只跑一批。
            ArmTimer();
            return;
        }

        try
        {
            var due = new List<(Entry, long)>();

            for (var i = 0; i < _shards.Length; i++)
            {
                var s = _shards[i];
                lock (s.Gate)
                {
                    var now = Environment.TickCount64;
                    while (s.Heap.Count > 0 && s.Heap[0].Deadline <= now)
                    {
                        var top = s.Heap[0];
                        HeapRemove(s, 0);
                        top.Dequeued = true;   // 出堆待执行：后续 Reschedule 应重新入堆
                        due.Add((top, top.Generation));
                    }
                }
            }

            // 在锁外逐个执行检查：检查可能 await（发 TESTFR 等），不应持锁。
            foreach (var (entry, gen) in due)
            {
                if (_shutdown || entry.Cancelled)
                {
                    continue; // 已取消
                }

                _ = RunOneAsync(entry, gen);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }

        ArmTimer();
    }

    private async Task RunOneAsync(Entry entry, long gen)
    {
        Iec104TimerScheduler.SchedulerAction action;
        try
        {
            action = await entry.Check(entry.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 连接终止：不再重排，不触发 onFatal。
            return;
        }
        catch (Exception)
        {
            // 检查中的瞬态异常（如 sink 写入噪声）不应让调度器崩溃：
            // 视为非致命，继续由宿主的关闭路径处理致命情况。
            action = Iec104TimerScheduler.SchedulerAction.Continue;
        }

        if (_shutdown)
        {
            return;
        }

        if (action == Iec104TimerScheduler.SchedulerAction.Fatal)
        {
            try
            {
                entry.OnFatal();
            }
            catch { /* 关闭动作失败忽略，连接最终由上层清理 */ }

            return; // 致命：不再重排
        }

        if (action == Iec104TimerScheduler.SchedulerAction.Stop)
        {
            return; // 正常终止：不再重排，不触发 onFatal
        }

        // Continue：按连接自己给出的下一次检查延迟精确重排（各连接 T1/T2/T3 可不同）。
        int next;
        try
        {
            next = entry.DelayProvider();
        }
        catch
        {
            next = MaxDelayMs;
        }

        Reschedule(entry, next);
    }

    /// <summary>无法取得连接建议间隔时的兜底值。</summary>
    private const int MaxDelayMs = 250;

    // ═══════════════════════════════════════════════════════════════
    //  最小堆（按 Deadline 升序）
    // ═══════════════════════════════════════════════════════════════

    private static void HeapPush(Shard shard, Entry e)
    {
        shard.Heap.Add(e);
        e.HeapIndex = shard.Heap.Count - 1;
        HeapUp(shard, e.HeapIndex);
    }

    private static void HeapRemove(Shard shard, int index)
    {
        var heap = shard.Heap;
        var last = heap.Count - 1;
        var removed = heap[index];
        heap[index] = heap[last];
        heap[index].HeapIndex = index;
        heap.RemoveAt(last);
        removed.HeapIndex = -1;

        if (index < heap.Count)
        {
            HeapDown(shard, index);
            HeapUp(shard, index);
        }
    }

    private static void HeapUpdate(Shard shard, int index)
    {
        HeapDown(shard, index);
        HeapUp(shard, index);
    }

    private static void HeapUp(Shard shard, int index)
    {
        var heap = shard.Heap;
        var item = heap[index];
        while (index > 0)
        {
            var parent = (index - 1) >> 1;
            if (heap[parent].Deadline <= item.Deadline)
            {
                break;
            }

            heap[index] = heap[parent];
            heap[index].HeapIndex = index;
            index = parent;
        }

        heap[index] = item;
        item.HeapIndex = index;
    }

    private static void HeapDown(Shard shard, int index)
    {
        var heap = shard.Heap;
        var item = heap[index];
        var count = heap.Count;
        while (true)
        {
            var left = (index << 1) + 1;
            if (left >= count)
            {
                break;
            }

            var best = left;
            var right = left + 1;
            if (right < count && heap[right].Deadline < heap[left].Deadline)
            {
                best = right;
            }

            if (item.Deadline <= heap[best].Deadline)
            {
                break;
            }

            heap[index] = heap[best];
            heap[index].HeapIndex = index;
            index = best;
        }

        heap[index] = item;
        item.HeapIndex = index;
    }

    // ═══════════════════════════════════════════════════════════════
    //  释放
    // ═══════════════════════════════════════════════════════════════

    /// <summary>是否已关闭。</summary>
    public bool IsShutdown => _shutdown;

    /// <summary>
    /// 强制关闭单例（仅测试使用）：停物理 Timer、清空堆、断开单例引用，
    /// 使下一个 <see cref="Instance"/> 访问重建全新调度器。
    /// </summary>
    public static void ForceShutdown()
    {
        lock (SingletonGate)
        {
            if (_instance == null)
            {
                return;
            }

            _instance.ShutdownInternal();
            _instance = null;
        }
    }

    /// <summary>
    /// 实例释放：仅在测试中作为 using 对象使用；生产路径由单例托管，
    /// 连接关闭只 <see cref="Unregister"/>，不关闭共享调度器。
    /// </summary>
    public void Dispose()
    {
        ShutdownInternal();
    }

    private void ShutdownInternal()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;

        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }

        try
        {
            _timer.Dispose();
        }
        catch { /* ignore */ }

        for (var i = 0; i < _shards.Length; i++)
        {
            var s = _shards[i];
            lock (s.Gate)
            {
                foreach (var e in s.Heap)
                {
                    e.HeapIndex = -1;
                }

                s.Heap.Clear();
            }
        }
    }
}
