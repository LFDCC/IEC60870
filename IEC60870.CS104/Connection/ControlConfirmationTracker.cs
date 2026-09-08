//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 控制命令确认匹配器：登记「发送并等待确认」的挂起槽位，并按
/// (TypeID, CA, IOA, Select) 关联键唤醒等待者。
/// 仅拦截 ACTIVATION_CON，直接从 <see cref="AsduView"/> 原始字节读字段（零分配）。
/// </summary>
internal sealed class ControlConfirmationTracker
{
    private readonly object _gate = new object();
    private readonly Dictionary<long, TaskCompletionSource<ControlConfirmation>> _pending
        = new Dictionary<long, TaskCompletionSource<ControlConfirmation>>();

    /// <summary>登记一个等待槽位。</summary>
    public void Register(long key, TaskCompletionSource<ControlConfirmation> tcs)
    {
        lock (_gate)
        {
            _pending[key] = tcs;
        }
    }

    /// <summary>移除一个等待槽位（超时/取消/完成后调用，避免悬挂）。</summary>
    public void Unregister(long key, TaskCompletionSource<ControlConfirmation> tcs)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, tcs))
            {
                _pending.Remove(key);
            }
        }
    }

    /// <summary>
    /// 尝试唤醒匹配 <paramref name="view"/> 的等待者。返回 true 表示该 ASDU 已被消费。
    /// </summary>
    public bool TryComplete(in AsduView view, ApplicationLayerParameters al)
    {
        if (view.Cot != CauseOfTransmission.ACTIVATION_CON)
        {
            return false;
        }

        var ioa = ControlConfirmMatcher.ReadIoa(view, al);
        var selectBit = ControlConfirmMatcher.ReadSelectBit(view, al);
        var key = ControlConfirmMatcher.MakeKey(view.TypeId, view.Ca, ioa, selectBit);

        TaskCompletionSource<ControlConfirmation> tcs;
        lock (_gate)
        {
            _pending.TryGetValue(key, out tcs);
        }

        if (tcs == null)
        {
            return false;
        }

        tcs.TrySetResult(new ControlConfirmation(
            view.TypeId, view.Ca, ioa,
            selectBit, view.IsNegative, view.IsTest));
        return true;
    }

    /// <summary>
    /// 连接终止时唤醒所有挂起等待者（抛 TaskCanceledException），避免其悬挂至确认超时。
    /// </summary>
    public void CompleteAllCanceled()
    {
        TaskCompletionSource<ControlConfirmation>[] pending;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            pending = new TaskCompletionSource<ControlConfirmation>[_pending.Count];
            _pending.Values.CopyTo(pending, 0);
            _pending.Clear();
        }

        foreach (var tcs in pending)
        {
            tcs.TrySetCanceled();
        }
    }
}
