//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：IEC 60870-5-4 时间定义、DL/T 667-1999 7.2.6
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 时标 CP56Time2a（7 字节）。
/// </summary>
/// <remarks>
/// 布局：毫秒 LE16（<b>含秒，0-59999</b>）+ 分钟（低 6 位，bit7 IV）
/// + 小时（低 5 位，bit7 SU）+ 日（低 5 位）+ 月（低 4 位）+ 年（0-99）。
/// <para>
/// 注意与 <see cref="Cp32Time2a"/> 的区别：CP32 只有 ms(2)+min(1)+hour(1)，
/// 无日期，用于扰动录波就绪帧（TI=26）。
/// </para>
/// <para>
/// 103 用自己的值类型实现：零堆分配，贴合接收热路径；毫秒域是一个 0-59999 的整数
/// （含秒），年份为两位（2000 起算）——任何把秒/毫秒拆开、或以 1970 起算的实现
/// 都会静默改变语义。
/// </para>
/// </remarks>
public readonly record struct Cp56Time2a(
    int Millisecond,
    int Minute,
    int Hour,
    int Day,
    int Month,
    int Year,
    bool Invalid = false,
    bool SummerTime = false)
{
    /// <summary>编码长度（字节）。</summary>
    public const int EncodedLength = 7;

    /// <summary>毫秒域最大值（含秒，0-59999）。</summary>
    public const int MaxMillisecond = 59999;

    /// <summary>从 <see cref="DateTime"/> 构造（跨年后按 2000 起算，仅保留两位年）。</summary>
    public static Cp56Time2a FromDateTime(DateTime time) => new(
        Millisecond: time.Second * 1000 + time.Millisecond,
        Minute: time.Minute,
        Hour: time.Hour,
        Day: time.Day,
        Month: time.Month,
        Year: time.Year % 100,
        Invalid: false,
        SummerTime: time.IsDaylightSavingTime());

    /// <summary>转换为 <see cref="DateTime"/>（2000 + 两位年；越界字段自动钳制）。</summary>
    public DateTime ToDateTime()
    {
        var year = 2000 + Year;
        var month = Math.Clamp(Month, 1, 12);
        var day = Math.Clamp(Day, 1, DateTime.DaysInMonth(year, month));
        var ms = Math.Clamp(Millisecond, 0, MaxMillisecond);
        return new DateTime(year, month, day, Math.Clamp(Hour, 0, 23), Math.Clamp(Minute, 0, 59), ms / 1000, ms % 1000);
    }

    /// <summary>编码为 7 字节。</summary>
    public byte[] Encode()
    {
        var buffer = new byte[EncodedLength];
        EncodeTo(buffer);
        return buffer;
    }

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"CP56Time2a 需要 {EncodedLength} 字节", nameof(destination));
        }

        var ms = Math.Clamp(Millisecond, 0, MaxMillisecond);
        destination[0] = (byte)(ms & 0xFF);
        destination[1] = (byte)(ms >> 8);
        destination[2] = (byte)(Math.Clamp(Minute, 0, 59) | (Invalid ? 0x80 : 0));
        destination[3] = (byte)(Math.Clamp(Hour, 0, 23) | (SummerTime ? 0x80 : 0));
        destination[4] = (byte)(Math.Clamp(Day, 1, 31) & 0x1F);
        destination[5] = (byte)(Math.Clamp(Month, 1, 12) & 0x0F);
        destination[6] = (byte)(Math.Clamp(Year, 0, 99) & 0x7F);
        return EncodedLength;
    }

    /// <summary>从 7 字节解码。</summary>
    public static Cp56Time2a Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < EncodedLength)
        {
            throw new ArgumentException($"CP56Time2a 需要 {EncodedLength} 字节，实际 {bytes.Length}", nameof(bytes));
        }

        return new Cp56Time2a(
            Millisecond: bytes[0] | (bytes[1] << 8),
            Minute: bytes[2] & 0x3F,
            Hour: bytes[3] & 0x1F,
            Day: bytes[4] & 0x1F,
            Month: bytes[5] & 0x0F,
            Year: bytes[6] & 0x7F,
            Invalid: (bytes[2] & 0x80) != 0,
            SummerTime: (bytes[3] & 0x80) != 0);
    }

    /// <summary>尝试解码（长度不足返回 false，不抛异常）。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Cp56Time2a time)
    {
        if (bytes.Length < EncodedLength)
        {
            time = default;
            return false;
        }

        time = Decode(bytes);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Year + 2000:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Millisecond / 1000:D2}.{Millisecond % 1000:D3}"
            + (Invalid ? " IV" : string.Empty);
}

/// <summary>
/// IEC 60870-5-103 扰动录波时标 CP32Time2a（4 字节）。
/// </summary>
/// <remarks>
/// 布局：毫秒 LE16（含秒，0-59999）+ 分钟（低 6 位，bit7 IV）+ 小时（低 5 位）。
/// <b>无日期字段</b>，日期由装置当前时钟隐含——回放时应结合扰动的 CP56 时标或装置时钟。
/// 用于 TI=26（扰动传输准备就绪）的「故障时刻」。
/// </remarks>
public readonly record struct Cp32Time2a(int Millisecond, int Minute, int Hour, bool Invalid = false, bool SummerTime = false)
{
    /// <summary>编码长度（字节）。</summary>
    public const int EncodedLength = 4;

    /// <summary>从 <see cref="DateTime"/> 构造（丢弃日期部分）。</summary>
    public static Cp32Time2a FromDateTime(DateTime time) => new(
        Millisecond: time.Second * 1000 + time.Millisecond,
        Minute: time.Minute,
        Hour: time.Hour,
        Invalid: false,
        SummerTime: time.IsDaylightSavingTime());

    /// <summary>编码为 4 字节。</summary>
    public byte[] Encode()
    {
        var buffer = new byte[EncodedLength];
        EncodeTo(buffer);
        return buffer;
    }

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"CP32Time2a 需要 {EncodedLength} 字节", nameof(destination));
        }

        var ms = Math.Clamp(Millisecond, 0, Cp56Time2a.MaxMillisecond);
        destination[0] = (byte)(ms & 0xFF);
        destination[1] = (byte)(ms >> 8);
        destination[2] = (byte)(Math.Clamp(Minute, 0, 59) | (Invalid ? 0x80 : 0));
        destination[3] = (byte)(Math.Clamp(Hour, 0, 23) | (SummerTime ? 0x80 : 0));
        return EncodedLength;
    }

    /// <summary>从 4 字节解码。</summary>
    public static Cp32Time2a Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < EncodedLength)
        {
            throw new ArgumentException($"CP32Time2a 需要 {EncodedLength} 字节，实际 {bytes.Length}", nameof(bytes));
        }

        return new Cp32Time2a(
            Millisecond: bytes[0] | (bytes[1] << 8),
            Minute: bytes[2] & 0x3F,
            Hour: bytes[3] & 0x1F,
            Invalid: (bytes[2] & 0x80) != 0,
            SummerTime: (bytes[3] & 0x80) != 0);
    }

    /// <summary>尝试解码（长度不足返回 false）。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Cp32Time2a time)
    {
        if (bytes.Length < EncodedLength)
        {
            time = default;
            return false;
        }

        time = Decode(bytes);
        return true;
    }

    /// <summary>转换为 <see cref="DateTime"/>（CP32 不含日期，取当天）。</summary>
    public DateTime ToDateTime()
    {
        var today = DateTime.Today;
        var ms = Math.Clamp(Millisecond, 0, Cp56Time2a.MaxMillisecond);
        return new DateTime(today.Year, today.Month, today.Day,
            Math.Clamp(Hour, 0, 23), Math.Clamp(Minute, 0, 59), ms / 1000, ms % 1000);
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Hour:D2}:{Minute:D2}:{Millisecond / 1000:D2}.{Millisecond % 1000:D3}" + (Invalid ? " IV" : string.Empty);
}

/// <summary>
/// IEC 60870-5-103 双点信息 + 品质描述词（DIQ，监视方向信息体首字节）。
/// </summary>
/// <remarks>
/// <para>
/// 位布局（DL/T 667-1999 §7.2.6.1.3 DIQ）：
/// <c>bit0-1 DPI</c>（0/3 不确定、1 分、2 合）、<c>bit2-3 保留</c>、
/// <c>bit4 BL</c> 闭锁、<c>bit5 SB</c> 取代、<c>bit6 NT</c> 非当前、<c>bit7 IV</c> 无效。
/// </para>
/// <para>
/// 注意位序：<b>SB 在 BL 之上</b>（SB=0x20、BL=0x10）。
/// 部分厂商文档（如《许继103通信规约》表10）把这两列
/// 印反成 <c>IV NT BL SB</c>，属排版串列，不应据此改动实现。
/// </para>
/// </remarks>
public readonly record struct DpiQuality(
    int Dpi,
    bool Invalid = false,
    bool NonTopical = false,
    bool Substituted = false,
    bool Blocked = false)
{
    /// <summary>IV（无效）掩码，bit7。</summary>
    public const byte InvalidMask = 0x80;

    /// <summary>NT（非当前）掩码，bit6。</summary>
    public const byte NonTopicalMask = 0x40;

    /// <summary>SB（取代）掩码，bit5。</summary>
    public const byte SubstitutedMask = 0x20;

    /// <summary>BL（闭锁）掩码，bit4。</summary>
    public const byte BlockedMask = 0x10;

    /// <summary>DPI 取值掩码，bit0-1。</summary>
    public const byte DpiMask = 0x03;

    /// <summary>DPI 中文状态描述。</summary>
    public string State => Dpi switch
    {
        0 => "不确定",
        1 => "分（OFF）",
        2 => "合（ON）",
        _ => "不确定（品质异常）",
    };

    /// <summary>编码为 1 字节（保留位 bit2-3 恒 0）。</summary>
    public byte Encode()
    {
        var value = (byte)(Math.Clamp(Dpi, 0, 3) & DpiMask);

        if (Invalid)
        {
            value |= InvalidMask;
        }

        if (NonTopical)
        {
            value |= NonTopicalMask;
        }

        if (Substituted)
        {
            value |= SubstitutedMask;
        }

        if (Blocked)
        {
            value |= BlockedMask;
        }

        return value;
    }

    /// <summary>从 1 字节解码（保留位忽略）。</summary>
    public static DpiQuality Decode(byte value) => new(
        Dpi: value & DpiMask,
        Invalid: (value & InvalidMask) != 0,
        NonTopical: (value & NonTopicalMask) != 0,
        Substituted: (value & SubstitutedMask) != 0,
        Blocked: (value & BlockedMask) != 0);

    /// <summary>仅指定 DPI 状态（品质位全零），点位映射用。</summary>
    public static DpiQuality FromDpi(int dpi) => new(dpi);

    /// <inheritdoc />
    public override string ToString() => $"DPI={Dpi}({State})"
        + (Invalid ? " IV" : string.Empty)
        + (NonTopical ? " NT" : string.Empty)
        + (Substituted ? " SB" : string.Empty)
        + (Blocked ? " BL" : string.Empty);
}
