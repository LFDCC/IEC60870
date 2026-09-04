//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 单事件（保护设备事件中的单元素状态）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>单事件状态（双位编码，与双点值语义一致）。</summary>
public enum EventState
{
    /// <summary>不确定（00）。</summary>
    INDETERMINATE_0 = 0,
    /// <summary>分（01）。</summary>
    OFF = 1,
    /// <summary>合（10）。</summary>
    ON = 2,
    /// <summary>不确定（11）。</summary>
    INDETERMINATE_3 = 3
}

/// <summary>
/// 保护设备事件中的单事件元素：bit0-1 = 状态，bit2-7 = QDP 质量位（含 EI/RES）。
/// </summary>
public class SingleEvent
{
    private const byte MaskState = 0x03;

    private QualityDescriptorP _qdp;
    private EventState _eventState;

    /// <summary>构造全零（不确定状态 + 有效质量）事件。</summary>
    public SingleEvent()
    {
        _eventState = EventState.INDETERMINATE_0;
        _qdp = new QualityDescriptorP();
    }

    /// <summary>以原始编码字节构造。</summary>
    public SingleEvent(byte encodedValue)
    {
        _eventState = (EventState)(encodedValue & MaskState);
        _qdp = new QualityDescriptorP(encodedValue);
    }

    /// <summary>复制构造。</summary>
    public SingleEvent(SingleEvent orignal)
    {
        _eventState = orignal._eventState;
        _qdp = new QualityDescriptorP(orignal._qdp);
    }

    /// <summary>事件状态。</summary>
    public EventState State
    {
        get => _eventState;
        set => _eventState = value;
    }

    /// <summary>质量描述符（QDP）。</summary>
    public QualityDescriptorP QDP
    {
        get => _qdp;
        set => _qdp = value;
    }

    /// <summary>完整编码字节（质量高 6 位 + 状态低 2 位）。</summary>
    public byte EncodedValue => (byte)((_qdp.EncodedValue & ~MaskState) + (int)_eventState);

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is SingleEvent other && EncodedValue == other.EncodedValue;

    /// <inheritdoc/>
    public override int GetHashCode() => EncodedValue.GetHashCode();
}
