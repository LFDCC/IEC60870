//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 保护设备跳闸/合闸回路信息（OCI）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System.Text;

namespace IEC60870.Core;

/// <summary>
/// 保护设备的输出回路信息（OCI），见 IEC 60870-5-101:2003 7.2.6.12。
/// bit0=通用命令(GC)，bit1-3=L1/L2/L3 相命令，bit4-7 保留。
/// </summary>
public class OutputCircuitInfo
{
    // 位域常量。
    private const byte MaskGc = 0x01;
    private const byte MaskCl1 = 0x02;
    private const byte MaskCl2 = 0x04;
    private const byte MaskCl3 = 0x08;

    internal byte _encodedValue;

    /// <summary>原始编码字节。</summary>
    public byte EncodedValue
    {
        get => _encodedValue;
        set => _encodedValue = value;
    }

    public OutputCircuitInfo()
    {
        _encodedValue = 0;
    }

    /// <summary>以原始编码字节构造。</summary>
    public OutputCircuitInfo(byte encodedValue)
    {
        _encodedValue = encodedValue;
    }

    /// <summary>以各相命令位构造。</summary>
    public OutputCircuitInfo(bool gc, bool cl1, bool cl2, bool cl3)
    {
        _encodedValue = (byte)((gc ? MaskGc : 0) | (cl1 ? MaskCl1 : 0)
                               | (cl2 ? MaskCl2 : 0) | (cl3 ? MaskCl3 : 0));
    }

    public OutputCircuitInfo(OutputCircuitInfo original)
    {
        _encodedValue = original._encodedValue;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj)
    {
        if (obj is not OutputCircuitInfo other)
        {
            return false;
        }

        return _encodedValue == other._encodedValue;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => _encodedValue.GetHashCode();

    /// <summary>通用跳闸/合闸命令位。</summary>
    public bool GC
    {
        get => (_encodedValue & MaskGc) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskGc, value);
    }

    /// <summary>L1 相输出回路命令位。</summary>
    public bool CL1
    {
        get => (_encodedValue & MaskCl1) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskCl1, value);
    }

    /// <summary>L2 相输出回路命令位。</summary>
    public bool CL2
    {
        get => (_encodedValue & MaskCl2) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskCl2, value);
    }

    /// <summary>L3 相输出回路命令位。</summary>
    public bool CL3
    {
        get => (_encodedValue & MaskCl3) != 0;
        set => _encodedValue = SetFlag(_encodedValue, MaskCl3, value);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder(20);

        if (GC)
        {
            sb.Append("[GC]");
        }

        if (CL1)
        {
            sb.Append("[CL1]");
        }

        if (CL2)
        {
            sb.Append("[CL2]");
        }

        if (CL3)
        {
            sb.Append("[CL3]");
        }

        return sb.ToString();
    }

    private static byte SetFlag(byte value, byte mask, bool set) =>
        set ? (byte)(value | mask) : (byte)(value & ~mask);
}