//------------------------------------------------------------------------------
//  IEC60870.Core.NET — QDP 保护设备事件质量描述符
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// QDP —— 保护设备事件的质量描述符（Quality Descriptor for Protection Events），
/// 对应 IEC 60870-5-101:2003 §7.2.6.4。
/// 单字节编码：在通用 QDS 的 BL/SB/NT/IV（bit4~bit7）之上，
/// 额外定义了保留位 RES（bit2）与「经过时间无效」标志 EI（bit3）。
/// </summary>
public class QualityDescriptorP
{
    // 各质量位在单字节中的掩码。
    private const byte MaskReserved = 0x04;   // RES，bit2，保留
    private const byte MaskElapsedInvalid = 0x08; // EI，bit3，经过时间无效
    private const byte MaskBlocked = 0x10;    // BL，bit4，封锁
    private const byte MaskSubstituted = 0x20; // SB，bit5，替代
    private const byte MaskNonTopical = 0x40; // NT，bit6，非当前
    private const byte MaskInvalid = 0x80;    // IV，bit7，无效

    private byte _encodedValue;

    /// <summary>构造编码值为 0（全部质量位有效）的描述符。</summary>
    public QualityDescriptorP() => _encodedValue = 0;

    /// <summary>以原始编码字节构造描述符。</summary>
    /// <param name="encodedValue">原始编码字节。</param>
    public QualityDescriptorP(byte encodedValue) => _encodedValue = encodedValue;

    /// <summary>以另一实例的编码值构造副本。</summary>
    /// <param name="original">被复制的源对象。</param>
    public QualityDescriptorP(QualityDescriptorP original) => _encodedValue = original._encodedValue;

    /// <inheritdoc/>
    public override bool Equals(object obj)
        => obj is QualityDescriptorP other && other._encodedValue == _encodedValue;

    /// <inheritdoc/>
    public override int GetHashCode() => _encodedValue.GetHashCode();

    // 单个质量位的读写辅助。
    private bool GetFlag(byte mask) => (_encodedValue & mask) != 0;

    private void SetFlag(byte mask, bool on)
    {
        if (on)
        {
            _encodedValue |= mask;
        }
        else
        {
            _encodedValue &= (byte)~mask;
        }
    }

    /// <summary>保留位（RES，bit2，标准未指定其含义）。</summary>
    public bool Reserved
    {
        get => GetFlag(MaskReserved);
        set => SetFlag(MaskReserved, value);
    }

    /// <summary>经过时间无效标志（EI，bit3）：保护事件的等待时间不可信。</summary>
    public bool ElapsedTimeInvalid
    {
        get => GetFlag(MaskElapsedInvalid);
        set => SetFlag(MaskElapsedInvalid, value);
    }

    /// <summary>封锁标志（BL，bit4）：值被封锁，不应向应用层传送。</summary>
    public bool Blocked
    {
        get => GetFlag(MaskBlocked);
        set => SetFlag(MaskBlocked, value);
    }

    /// <summary>替代标志（SB，bit5）：值由手动输入或来源替代。</summary>
    public bool Substituted
    {
        get => GetFlag(MaskSubstituted);
        set => SetFlag(MaskSubstituted, value);
    }

    /// <summary>非当前标志（NT，bit6）：值已过期，不再代表当前状态。</summary>
    public bool NonTopical
    {
        get => GetFlag(MaskNonTopical);
        set => SetFlag(MaskNonTopical, value);
    }

    /// <summary>无效标志（IV，bit7）：值无效。</summary>
    public bool Invalid
    {
        get => GetFlag(MaskInvalid);
        set => SetFlag(MaskInvalid, value);
    }

    /// <summary>原始编码字节（保留位原样透传）。</summary>
    public byte EncodedValue
    {
        get => _encodedValue;
        set => _encodedValue = value;
    }
}
