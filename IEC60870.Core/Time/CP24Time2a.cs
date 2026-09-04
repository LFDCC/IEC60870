//------------------------------------------------------------------------------
//  IEC60870.Core.NET — CP24Time2a 七进制时间值（3 字节：毫秒+分钟）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// CP24Time2a：3 字节二进制时间值（IEC 60870-5-4 §5.14）。
/// 字节 0-1 为小端毫秒计数（0…59999），字节 2 低 6 位为分钟（0…59），
/// bit7 = IV（无效），bit6 = SU（被替代）。
/// </summary>
public class CP24Time2a
{
    private readonly byte[] _encodedValue = new byte[3];

    private const int MsPerMinute = 60000;
    private const int MsPerSecond = 1000;
    private const ushort LowBitsMask = 0xFFFF;   // 字节 0-1 拼合后的毫秒域
    private const byte MaskMinute = 0x3F;        // 分钟占 bit0-5
    private const byte MaskSubstitued = 0x40;    // SU
    private const byte MaskInvalid = 0x80;       // IV

    /// <summary>从报文切片解析 3 字节编码。</summary>
    /// <exception cref="ASDUParsingException">剩余长度不足 3 字节时抛出。</exception>
    public CP24Time2a(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + 3)
        {
            throw new ASDUParsingException("报文长度不足以解析 CP24Time2a");
        }

        msg.Slice(startIndex, 3).CopyTo(_encodedValue);
    }

    /// <summary>以分、秒、毫秒构造。</summary>
    public CP24Time2a(int minute, int second, int millisecond)
    {
        Millisecond = millisecond;
        Second = second;
        Minute = minute;
    }

    /// <summary>构造全零时间值。</summary>
    public CP24Time2a()
    {
    }

    /// <summary>复制构造。</summary>
    public CP24Time2a(CP24Time2a original)
    {
        original._encodedValue.CopyTo(_encodedValue, 0);
    }

    // ── 内部毫秒域（字节 0-1 小端）读写辅助 ─────────────────────
    private int RawMs
    {
        get => _encodedValue[0] | (_encodedValue[1] << 8);
        set
        {
            _encodedValue[0] = (byte)value;
            _encodedValue[1] = (byte)(value >> 8);
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is CP24Time2a other && GetHashCode() == other.GetHashCode();

    /// <inheritdoc/>
    public override int GetHashCode() => new System.Numerics.BigInteger(_encodedValue).GetHashCode();

    /// <summary>自分钟起点累计的总毫秒数。</summary>
    public int GetMilliseconds() => Minute * MsPerMinute + Second * MsPerSecond + Millisecond;

    /// <summary>毫秒部分（0…999，即总毫秒对 1000 取余）。</summary>
    public int Millisecond
    {
        get => RawMs % MsPerSecond;
        set => RawMs = Second * MsPerSecond + value;
    }

    /// <summary>秒（0…59）。</summary>
    public int Second
    {
        get => RawMs / MsPerSecond;
        set => RawMs = value * MsPerSecond + RawMs % MsPerSecond;
    }

    /// <summary>分钟（0…59）。</summary>
    public int Minute
    {
        get => _encodedValue[2] & MaskMinute;
        set => _encodedValue[2] = (byte)((_encodedValue[2] & ~MaskMinute) | (value & MaskMinute));
    }

    /// <summary>无效标志（IV，bit7 of byte2）。</summary>
    public bool Invalid
    {
        get => (_encodedValue[2] & MaskInvalid) != 0;
        set => SetFlag(MaskInvalid, value);
    }

    /// <summary>替代标志（SU，bit6 of byte2；时间值被中间站替代时置位）。</summary>
    public bool Substitued
    {
        get => (_encodedValue[2] & MaskSubstitued) != 0;
        set => SetFlag(MaskSubstitued, value);
    }

    private void SetFlag(byte mask, bool on)
    {
        if (on)
        {
            _encodedValue[2] |= mask;
        }
        else
        {
            _encodedValue[2] &= (byte)~mask;
        }
    }

    /// <summary>内部编码数组引用（勿修改），供兼容路径使用。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配编码切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>将 3 字节编码写入目标缓冲。</summary>
    public void WriteTo(Span<byte> destination) => _encodedValue.AsSpan().CopyTo(destination);

    /// <inheritdoc/>
    public override string ToString()
        => $"[CP24Time2a: ms={Millisecond}, s={Second}, min={Minute}, IV={Invalid}, SU={Substitued}]";
}
