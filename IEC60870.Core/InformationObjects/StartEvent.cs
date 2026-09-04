//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 保护设备启动事件（SPE）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System.Text;

namespace IEC60870.Core;

/// <summary>
/// 保护设备启动事件（SPE），见 IEC 60870-5-101:2003 7.2.6.11。
/// 字节各位：bit0=通用启动(GS)，bit1-3=L1/L2/L3 相启动，
/// bit4=接地电流启动(SIE)，bit5=反向启动(SRD)，bit6-7=保留位。
/// </summary>
public class StartEvent
{
    // 位域常量。
    private const byte MaskGs = 0x01;
    private const byte MaskSl1 = 0x02;
    private const byte MaskSl2 = 0x04;
    private const byte MaskSl3 = 0x08;
    private const byte MaskSie = 0x10;
    private const byte MaskSrd = 0x20;
    private const byte MaskRes1 = 0x40;
    private const byte MaskRes2 = 0x80;

    private byte _encodedValue;

    public StartEvent()
    {
        _encodedValue = 0;
    }

    public StartEvent(byte encodedValue)
    {
        _encodedValue = encodedValue;
    }

    public StartEvent(StartEvent original)
    {
        _encodedValue = original._encodedValue;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj)
    {
        if (obj is not StartEvent other)
        {
            return false;
        }

        return _encodedValue == other._encodedValue;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => _encodedValue.GetHashCode();

    /// <summary>通用启动（General start of operation）。</summary>
    public bool GS
    {
        get => (_encodedValue & MaskGs) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskGs, value);
    }

    /// <summary>L1 相启动。</summary>
    public bool SL1
    {
        get => (_encodedValue & MaskSl1) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskSl1, value);
    }

    /// <summary>L2 相启动。</summary>
    public bool SL2
    {
        get => (_encodedValue & MaskSl2) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskSl2, value);
    }

    /// <summary>L3 相启动。</summary>
    public bool SL3
    {
        get => (_encodedValue & MaskSl3) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskSl3, value);
    }

    /// <summary>接地电流（IE）启动。</summary>
    public bool SIE
    {
        get => (_encodedValue & MaskSie) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskSie, value);
    }

    /// <summary>反向启动。</summary>
    public bool SRD
    {
        get => (_encodedValue & MaskSrd) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskSrd, value);
    }

    /// <summary>保留位 1。</summary>
    public bool RES1
    {
        get => (_encodedValue & MaskRes1) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskRes1, value);
    }

    /// <summary>保留位 2。</summary>
    public bool RES2
    {
        get => (_encodedValue & MaskRes2) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskRes2, value);
    }

    /// <summary>原始编码字节。</summary>
    public byte EncodedValue
    {
        get => _encodedValue;
        set => _encodedValue = value;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder(30);

        if (GS)
        {
            sb.Append("[GS]");
        }

        if (SL1)
        {
            sb.Append("[SL1]");
        }

        if (SL2)
        {
            sb.Append("[SL2]");
        }

        if (SL3)
        {
            sb.Append("[SL3]");
        }

        if (SIE)
        {
            sb.Append("[SIE]");
        }

        if (SRD)
        {
            sb.Append("[SRD]");
        }

        if (RES1)
        {
            sb.Append("[RES1]");
        }

        if (RES2)
        {
            sb.Append("[RES2]");
        }

        return sb.ToString();
    }

    private static byte SetFlag(byte value, byte mask, bool set) =>
        set ? (byte)(value | mask) : (byte)(value & ~mask);
}