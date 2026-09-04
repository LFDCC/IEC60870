//------------------------------------------------------------------------------
//  IEC60870.Core.NET — CP56Time2a 七字节二进制时间戳（毫秒…年 + 标志位）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// CP56Time2a：7 字节二进制时间值（IEC 60870-5-4 §5.16），
/// 承载毫秒、秒、分、时、星期、日、月、年，以及 IV / SU / 夏令时标志。
/// <code>
/// [0..1] 毫秒（小端 0…59999）
/// [2]    分 bit0-5 | SU bit6 | IV bit7
/// [3]    时 bit0-4 | 夏令时 bit7
/// [4]    日 bit0-4 | 星期 bit5-7
/// [5]    月 bit0-3
/// [6]    年 bit0-6 | 世纪有效性 bit7
/// </code>
/// </summary>
public class CP56Time2a
{
    private const int Size = 7;
    private const int MsPerSecond = 1000;

    // byte2：分钟 / SU / IV
    private const byte MaskMinute = 0x3F;
    private const byte MaskSubstituted = 0x40;
    private const byte MaskInvalid = 0x80;
    // byte3：小时 / 夏令时
    private const byte MaskHour = 0x1F;
    private const byte MaskSummerTime = 0x80;
    // byte4：日 / 星期
    private const byte MaskDayOfMonth = 0x1F;
    private const int DayOfWeekShift = 5;
    private const byte MaskDayOfWeek = 0xE0;
    // byte5：月
    private const byte MaskMonth = 0x0F;
    // byte6：年 / 世纪有效位
    private const byte MaskYear = 0x7F;
    private const byte MaskCenturyValid = 0x80;

    private readonly byte[] _encodedValue = new byte[Size];

    /// <summary>从报文切片解析 7 字节编码。</summary>
    /// <exception cref="ASDUParsingException">剩余长度不足 7 字节时抛出。</exception>
    public CP56Time2a(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + Size)
        {
            throw new ASDUParsingException("报文长度不足以解析 CP56Time2a");
        }

        msg.Slice(startIndex, Size).CopyTo(_encodedValue);
    }

    /// <summary>由 <see cref="DateTime"/> 构造（年份取两位，世纪由 <see cref="CenturyValid"/> 表达）。</summary>
    public CP56Time2a(DateTime time)
    {
        Millisecond = time.Millisecond;
        Second = time.Second;
        Year = time.Year % 100;
        Month = time.Month;
        DayOfMonth = time.Day;
        Hour = time.Hour;
        Minute = time.Minute;
    }

    /// <summary>构造全零时间戳。</summary>
    public CP56Time2a()
    {
    }

    /// <summary>复制构造。</summary>
    public CP56Time2a(CP56Time2a original)
    {
        original._encodedValue.CopyTo(_encodedValue, 0);
    }

    // ── 字节 0-1 毫秒域读写辅助 ─────────────────────────────────
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
    public override bool Equals(object obj) => obj is CP56Time2a other && Equals(other);

    /// <summary>逐字节值比较（避免哈希碰撞被误判为相等）。</summary>
    public bool Equals(CP56Time2a other)
    {
        if (other is null)
        {
            return false;
        }

        return _encodedValue.AsSpan().SequenceEqual(other._encodedValue);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // 稳定散列，无需分配 BigInteger。
        var h = 17;
        for (var i = 0; i < Size; i++)
        {
            h = (h * 31) + _encodedValue[i];
        }

        return h;
    }

    /// <summary>
    /// 结合 <paramref name="startYear"/> 推定世纪，生成完整日期时间。
    /// 月/日为 0（协议允许的空值）时按 1 处理；字段越界则返回 <see cref="DateTime.MinValue"/> 等价的零值。
    /// </summary>
    public DateTime GetDateTime(int startYear)
    {
        var baseYear = (startYear / 100) * 100;

        if (Year < (startYear % 100))
        {
            baseYear += 100;
        }

        var month = Month == 0 ? 1 : Month;
        var dayOfMonth = DayOfMonth == 0 ? 1 : DayOfMonth;

        try
        {
            return new DateTime(baseYear + Year, month, dayOfMonth, Hour, Minute, Second, Millisecond);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTime(0);
        }
    }

    /// <summary>以 1970 年为跨世纪参考，生成完整日期时间。</summary>
    public DateTime GetDateTime() => GetDateTime(1970);

    /// <summary>毫秒部分（0…999）。</summary>
    public int Millisecond
    {
        get => RawMs % MsPerSecond;
        set => RawMs = (Second * MsPerSecond) + value;
    }

    /// <summary>秒（0…59）。</summary>
    public int Second
    {
        get => RawMs / MsPerSecond;
        set => RawMs = (value * MsPerSecond) + (RawMs % MsPerSecond);
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

    /// <summary>星期（1=周一 … 7=周日）。</summary>
    public int DayOfWeek
    {
        get => (_encodedValue[4] & MaskDayOfWeek) >> DayOfWeekShift;
        set => _encodedValue[4] = (byte)((_encodedValue[4] & MaskDayOfMonth) | ((value & 0x07) << DayOfWeekShift));
    }

    /// <summary>月中日（1…31）。</summary>
    public int DayOfMonth
    {
        get => _encodedValue[4] & MaskDayOfMonth;
        set => _encodedValue[4] = (byte)((_encodedValue[4] & MaskDayOfWeek) | (value & MaskDayOfMonth));
    }

    /// <summary>月份（1…12）。</summary>
    public int Month
    {
        get => _encodedValue[5] & MaskMonth;
        set => _encodedValue[5] = (byte)((_encodedValue[5] & ~MaskMonth) | (value & MaskMonth));
    }

    /// <summary>年份（0…99，set 时按 100 取模）。</summary>
    public int Year
    {
        get => _encodedValue[6] & MaskYear;
        set => _encodedValue[6] = (byte)((_encodedValue[6] & MaskCenturyValid) | ((value % 100) & MaskYear));
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

    /// <summary>替代标志（SU，byte2 bit6；时间值由中间站替代时置位）。</summary>
    public bool Substituted
    {
        get => (_encodedValue[2] & MaskSubstituted) != 0;
        set => _encodedValue[2] = (byte)(value ? _encodedValue[2] | MaskSubstituted : _encodedValue[2] & ~MaskSubstituted);
    }

    /// <summary>内部编码数组引用（勿修改）。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配编码切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>将 7 字节编码写入目标缓冲。</summary>
    public void WriteTo(Span<byte> destination) => _encodedValue.AsSpan().CopyTo(destination);

    /// <inheritdoc/>
    public override string ToString()
        => $"[CP56Time2a: ms={Millisecond}, s={Second}, min={Minute}, h={Hour}, dow={DayOfWeek}, dom={DayOfMonth}, mon={Month}, yr={Year}, SU={SummerTime}, IV={Invalid}, SUB={Substituted}]";
}
