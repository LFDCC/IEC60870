//------------------------------------------------------------------------------
//  IEC60870.Core.NET — QDS 质量描述符
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 质量描述符（Quality Descriptor, QDS），IEC 60870-5-101 §7.2.6.3。
/// 单字节编码：bit0 = OV（溢出），bit4~bit7 = BL/SB/NT/IV；bit1~bit3 保留。
/// </summary>
public class QualityDescriptor
{
    // ── 质量位掩码（bit1~bit3 保留恒 0）──────────────────────────
    private const byte MaskOverflow = 0x01;      // OV
    private const byte MaskBlocked = 0x10;       // BL
    private const byte MaskSubstituted = 0x20;   // SB
    private const byte MaskNonTopical = 0x40;    // NT
    private const byte MaskInvalid = 0x80;       // IV

    private byte _value;

    /// <summary>创建全部质量位清零（有效）的描述符。</summary>
    public static QualityDescriptor VALID() => new();

    /// <summary>创建 IV（无效）置位的描述符。</summary>
    public static QualityDescriptor INVALID() => new(MaskInvalid);

    /// <summary>构造编码值为 0（全部有效）的描述符。</summary>
    public QualityDescriptor() => _value = 0;

    /// <summary>以原始编码字节构造描述符。</summary>
    /// <param name="encodedValue">编码字节。</param>
    public QualityDescriptor(byte encodedValue) => _value = encodedValue;

    /// <summary>复制构造。</summary>
    public QualityDescriptor(QualityDescriptor original) => _value = original._value;

    // ── 单个质量位的读写辅助 ────────────────────────────────────
    private bool GetFlag(byte mask) => (_value & mask) != 0;

    private void SetFlag(byte mask, bool on)
    {
        if (on)
        {
            _value |= mask;
        }
        else
        {
            _value &= (byte)~mask;
        }
    }

    /// <summary>溢出标志（OV，bit0）：测量值超出正常表示范围。</summary>
    public bool Overflow
    {
        get => GetFlag(MaskOverflow);
        set => SetFlag(MaskOverflow, value);
    }

    /// <summary>封锁标志（BL，bit4）：值被封锁，不应传送给应用。</summary>
    public bool Blocked
    {
        get => GetFlag(MaskBlocked);
        set => SetFlag(MaskBlocked, value);
    }

    /// <summary>替代标志（SB，bit5）：值非实际采集（手动置数/旁路替代）。</summary>
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

    /// <summary>无效标志（IV，bit7）：值无效（采集失败/通信中断等）。</summary>
    public bool Invalid
    {
        get => GetFlag(MaskInvalid);
        set => SetFlag(MaskInvalid, value);
    }

    /// <summary>原始编码字节（低 4 位中的保留位原样透传）。</summary>
    public byte EncodedValue
    {
        get => _value;
        set => _value = value;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj)
        => obj is QualityDescriptor other && other._value == _value;

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc/>
    public override string ToString()
        => $"QDS [OV={Overflow} BL={Blocked} SB={Substituted} NT={NonTopical} IV={Invalid}] raw=0x{_value:X2}";
}
