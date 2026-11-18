//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 读命令（C_RD_NA_1）应答匹配器：登记「发送并等待读应答」的挂起槽位，并按
/// (公共地址 CA, 信息对象地址 IOA) 唤醒等待者。
/// </summary>
/// <remarks>
/// <para><b>为什么关联键里没有 TypeID</b>：读命令的应答类型由被读点决定
/// （M_SP_NA_1 / M_ME_NC_1 …），请求方事先不知道，回显的 102 只在拒绝时出现。
/// 因此只能按 (CA, IOA) 关联，再用传输原因与类型范围排除「不像读应答」的帧。</para>
/// <para><b>为什么每个键是队列而不是单个槽位</b>：并发对同一 IOA 发起多次读时，
/// 装置的多个相同应答无法区分归属，只能先进先出逐个交付；用单槽位会让先登记者
/// 只能等超时（旧的控制命令匹配器正是这个缺陷）。不同 IOA 的等待者互不影响。</para>
/// <para><b>热路径开销</b>：无挂起读时仅一次 <see cref="Volatile.Read"/> 早退，
/// 不取锁、不解码；只有存在等待者时才解析信息对象。</para>
/// </remarks>
internal sealed class ReadResponseTracker
{
    private readonly object _gate = new object();

    // 每个 (CA, IOA) 一个 FIFO 等待队列。读是低频操作，队列长度通常为 1。
    private readonly Dictionary<long, List<TaskCompletionSource<ReadResponse>>> _pending
        = new Dictionary<long, List<TaskCompletionSource<ReadResponse>>>();

    // 挂起等待者总数：接收热路径的无锁早退依据（只在锁内改写）。
    private int _pendingCount;

    /// <summary>构造 (CA, IOA) 二元组关联键。CA 最多 2 字节、IOA 最多 3 字节。</summary>
    internal static long MakeKey(int ca, int ioa) =>
        ((long)(ca & 0xffff) << 32) | (uint)(ioa & 0xffffff);

    /// <summary>登记一个等待槽位（追加到该 IOA 的队尾）。</summary>
    public void Register(long key, TaskCompletionSource<ReadResponse> tcs)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var queue))
            {
                queue = new List<TaskCompletionSource<ReadResponse>>(1);
                _pending.Add(key, queue);
            }

            queue.Add(tcs);
            _pendingCount++;
        }
    }

    /// <summary>移除一个等待槽位（超时/取消/完成后调用，避免悬挂）。</summary>
    public void Unregister(long key, TaskCompletionSource<ReadResponse> tcs)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var queue) && queue.Remove(tcs))
            {
                _pendingCount--;
                if (queue.Count == 0)
                {
                    _pending.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// 尝试唤醒匹配该 ASDU 的等待者。返回 true 表示该 ASDU 已被某个读等待者消费。
    /// </summary>
    /// <remarks>
    /// 认领规则（全部满足）：
    /// <list type="number">
    ///   <item>公共地址与等待者一致；</item>
    ///   <item>传输原因不是主动上送类（周期/突发/召唤响应/计数召唤响应…）——
    ///         否则同一 IOA 的自发变位会被误当成本次读的答案；</item>
    ///   <item>类型标识属于监视方向（1…40）；命令/系统类型（45 以上）不会伪装成读应答。</item>
    /// </list>
    /// 另外，装置拒绝读时回的是 C_RD_NA_1 的否定应答（或未知地址类 COT），
    /// 此时立即交付「否定」结果，不让调用方白等到超时。
    /// </remarks>
    public bool TryComplete(in AsduView view, ApplicationLayerParameters al, AsduTypeHandlerRegistry registry)
    {
        if (Volatile.Read(ref _pendingCount) == 0)
        {
            return false;
        }

        var cot = view.Cot;

        // 装置拒绝读：C_RD_NA_1 回显 + P/N 位或「未知 XX」类 COT。
        if (view.TypeId == TypeID.C_RD_NA_1)
        {
            if (!view.IsNegative && !IsUnknownAddressCause(cot))
            {
                // 不带否定位的 102 回显（部分装置先回 ACT_CON 再回数据）：继续等真正的数据帧。
                return false;
            }

            var ioa = ControlConfirmMatcher.ReadIoa(view, al);
            var waiter = Dequeue(view.Ca, ioa);
            if (waiter == null)
            {
                return false;
            }

            waiter.TrySetResult(new ReadResponse(
                view.TypeId, view.Ca, ioa, isNegative: true, value: null, view.IsTest));
            return true;
        }

        // 只认监视方向的数据类型；命令/参数/文件/系统类型一律不认。
        var typeCode = (int)view.TypeId;
        if (typeCode < 1 || typeCode > 40)
        {
            return false;
        }

        if (IsUnsolicitedCause(cot))
        {
            return false;
        }

        var count = view.NumberOfElements;
        if (count <= 0)
        {
            return false;
        }

        // 常见情形：单元素非序列 ASDU —— 直接读 IOA，避免为无关帧解码。
        if (!view.IsSequence && count == 1)
        {
            var ioa = ControlConfirmMatcher.ReadIoa(view, al);
            var waiter = Dequeue(view.Ca, ioa);
            if (waiter == null)
            {
                return false;
            }

            waiter.TrySetResult(new ReadResponse(
                view.TypeId, view.Ca, ioa, isNegative: false, DecodeElement(view, 0, registry), view.IsTest));
            return true;
        }

        // 多元素 / 序列寻址：逐个解码取 IOA，命中即交付（一帧可同时满足多个等待者）。
        var completed = false;
        for (var i = 0; i < count; i++)
        {
            var value = DecodeElement(view, i, registry);
            if (value == null)
            {
                continue;
            }

            var waiter = Dequeue(view.Ca, value.ObjectAddress);
            if (waiter == null)
            {
                continue;
            }

            waiter.TrySetResult(new ReadResponse(
                view.TypeId, view.Ca, value.ObjectAddress, isNegative: false, value, view.IsTest));
            completed = true;
        }

        return completed;
    }

    /// <summary>连接终止时唤醒所有挂起等待者（抛 TaskCanceledException），避免其悬挂至超时。</summary>
    public void CompleteAllCanceled()
    {
        TaskCompletionSource<ReadResponse>[] pending;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var total = _pendingCount;
            pending = new TaskCompletionSource<ReadResponse>[total];
            var index = 0;
            foreach (var queue in _pending.Values)
            {
                queue.CopyTo(pending, index);
                index += queue.Count;
            }

            _pending.Clear();
            _pendingCount = 0;
        }

        for (var i = 0; i < pending.Length; i++)
        {
            pending[i].TrySetCanceled();
        }
    }

    /// <summary>取走该 (CA, IOA) 的队首等待者；无等待者返回 null。</summary>
    private TaskCompletionSource<ReadResponse> Dequeue(int ca, int ioa)
    {
        var key = MakeKey(ca, ioa);
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                return null;
            }

            var tcs = queue[0];
            queue.RemoveAt(0);
            _pendingCount--;

            if (queue.Count == 0)
            {
                _pending.Remove(key);
            }

            return tcs;
        }
    }

    /// <summary>
    /// 解码单个元素；任何解析异常都不得击穿接收循环，失败时返回 null
    /// （调用方仍会交付 ReadResponse，只是 Value 为空、TypeId 有效）。
    /// </summary>
    private static InformationObject DecodeElement(in AsduView view, int index, AsduTypeHandlerRegistry registry)
    {
        try
        {
            return view.GetElement(index, registry);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>主动上送类传输原因：这些帧不属于任何读事务。</summary>
    private static bool IsUnsolicitedCause(CauseOfTransmission cot)
    {
        switch (cot)
        {
            case CauseOfTransmission.PERIODIC:
            case CauseOfTransmission.BACKGROUND_SCAN:
            case CauseOfTransmission.SPONTANEOUS:
            case CauseOfTransmission.INITIALIZED:
            case CauseOfTransmission.RETURN_INFO_REMOTE:
            case CauseOfTransmission.RETURN_INFO_LOCAL:
                return true;
        }

        // 20…43：各类召唤 / 计数召唤响应，属于其它业务（如并行总召唤），不能当读应答。
        var code = (int)cot;
        return code >= 20 && code <= 43;
    }

    /// <summary>「未知类型/原因/公共地址/信息体地址」——装置拒绝读时的标准否定 COT。</summary>
    private static bool IsUnknownAddressCause(CauseOfTransmission cot)
    {
        switch (cot)
        {
            case CauseOfTransmission.UNKNOWN_TYPE_ID:
            case CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION:
            case CauseOfTransmission.UNKNOWN_COMMON_ADDRESS_OF_ASDU:
            case CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS:
                return true;
            default:
                return false;
        }
    }
}
