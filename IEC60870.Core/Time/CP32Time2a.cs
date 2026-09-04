//------------------------------------------------------------------------------
//  IEC60870.Core.NET — CP32Time2a 二进制时刻值（4 字节：毫秒+时分+标志）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// CP32Time2a：4 字节二进制时刻值（IEC 60870-5-4 §5.15）。
/// 字节 0-1 小端毫秒计数（0…59999）；字节 2 低 6 位为分钟、bit7 = IV、bit6 = SU；
/// 字节 3 低 5 位为小时、bit7 = 夏令时标志。
/// </summary>
public class CP32Time2a
{
    private readonly byte[] _encodedValue = new byte[4];

    private const int MsPerSecond = 1000;
    private const byte MaskMinute = 0x3F;    // 分钟占 byte2 bit0-5
    private const byte MaskSubstituted = 0x40; // SU, byte2 bit6
    private const byte MaskInvalid = 0x80;   // IV, byte2 bit7
    private const byte MaskHour = 0x1F;      // 小时占 byte3 bit0-4
    private const byte MaskSummerTime = 0x80; // 夏令时, byte3 bit7

    /// <summary>从报文切片解析 4 字节编码。</summary>
    /// <exception cref="ASDUParsingException">剩余长度不足 4 字节时抛出。</exception>
    internal CP32Time2a(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + 4)
        {
            throw new ASDUParsingException("报文长度不足以解析 CP32Time2a");
        }

        msg.Slice(startIndex, 4).CopyTo(_encodedValue);
    }

    /// <summary>以时、分、秒、毫秒与标志位构造。</summary>
    public CP32Time2a(int hours, int minutes, int seconds, int milliseconds, bool invalid, bool summertime)
    {
        Hour = hours;
        Minute = minutes;
        Second = seconds;
        Millisecond = milliseconds;
        Invalid = invalid;
        SummerTime = summertime;
    }

    /// <summary>从 <see cref="DateTime"/> 的时间部分构造。</summary>
    public CP32Time2a(DateTime time)
    {
        Millisecond = time.Millisecond;
        Second = time.Second;
        Hour = time.Hour;
        Minute = time.Minute;
    }

    /// <summary>构造全零时刻值。</summary>
    public CP32Time2a()
    {
    }

    /// <summary>复制构造。</summary>
    public CP32Time2a(CP32Time2a original)
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
    public override bool Equals(object obj) => obj is CP32Time2a other && GetHashCode() == other.GetHashCode();

    /// <inheritdoc/>
    public override int GetHashCode() => new System.Numerics.BigInteger(_encodedValue).GetHashCode();

    /// <summary>叠加到参考日期上，生成完整日期时间。</summary>
    public DateTime GetDateTime(DateTime refTime)
        => new DateTime(refTime.Year, refTime.Month, refTime.Day, Hour, Minute, Second, Millisecond);

    /// <summary>以今天为参考日，生成完整日期时间。</summary>
    public DateTime GetDateTime() => GetDateTime(DateTime.Now);

    /// <summary>毫秒部分（0…999）。</summary>
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

    /// <summary>小时（0…23）。</summary>
    public int Hour
    {
        get => _encodedValue[3] & MaskHour;
        set => _encodedValue[3] = (byte)((_encodedValue[3] & ~MaskHour) | (value & MaskHour));
    }

    /// <summary>夏令时标志（byte3 bit7）。</summary>
    public bool SummerTime
    {
        get => (_encodedValue[3] & MaskSummerTime) != 0;
        set => _encodedValue[3] = (byte)(value ? _encodedValue[3] | MaskSummerTime : _encodedValue[3] & ~MaskSummerTime);
    }

    /// <summary>无效标志（IV，byte2 bit7）。</summary>
    public bool Invalid
    {
        get => (_encodedValue[2] & MaskInvalid) != 0;
        set => _encodedValue[2] = (byte)(value ? _encodedValue[2] | MaskInvalid : _encodedValue[2] & ~MaskInvalid);
    }

    /// <summary>替代标志（SU，byte2 bit6）。</summary>
    public bool Substituted
    {
        get => (_encodedValue[2] & MaskSubstituted) != 0;
        set => _encodedValue[2] = (byte)(value ? _encodedValue[2] | MaskSubstituted : _encodedValue[2] & ~MaskSubstituted);
    }

    /// <summary>内部编码数组引用（勿修改）。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配编码切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>将 4 字节编码写入目标缓冲。</summary>
    public void WriteTo(Span<byte> destination) => _encodedValue.AsSpan().CopyTo(destination);

    /// <inheritdoc/>
    public override string ToString()
        => $"[CP32Time2a: ms={Millisecond}, s={Second}, min={Minute}, h={Hour}, SU={SummerTime}, IV={Invalid}, SUB={Substituted}]";
}
