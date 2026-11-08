//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103) 7.2.6.31 ~ 7.2.6.35
//------------------------------------------------------------------------------

using System.Buffers.Binary;

namespace IEC60870.CS103;

/// <summary>
/// 通用分类标识号 GIN：组（1 字节）+ 条目（1 字节）。
/// </summary>
/// <remarks>
/// 组 0 条目 0 用于「读全目录」。厂商私有信息（定值、软压板、装置描述）
/// 大量落在私有 GIN 段，通过 <see cref="Iec103PrivateTypeRegistry.RegisterGin"/> 扩展。
/// </remarks>
public readonly record struct Gin(byte Group, byte Entry)
{
    /// <summary>GIN 0.0（读全目录用）。</summary>
    public static Gin Zero => new(0, 0);

    /// <summary>编码长度（字节）。</summary>
    public const int EncodedLength = 2;

    /// <summary>是否为 GIN 0.0。</summary>
    public bool IsZero => Group == 0 && Entry == 0;

    /// <summary>编码为 2 字节。</summary>
    public byte[] Encode() => new[] { Group, Entry };

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"GIN 需要 {EncodedLength} 字节", nameof(destination));
        }

        destination[0] = Group;
        destination[1] = Entry;
        return EncodedLength;
    }

    /// <summary>从 2 字节解码。</summary>
    /// <exception cref="FormatException">不足 2 字节。</exception>
    public static Gin Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            throw new FormatException($"GIN 需要 2 字节，实际 {bytes.Length}");
        }

        return new Gin(bytes[0], bytes[1]);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Group}.{Entry}";
}

/// <summary>描述类型 KOD（DL/T 667-1999 7.2.6.32）。</summary>
/// <remarks>
/// 厂商可能使用标准外的 KOD（如 200+）扩展定值语义，
/// 通过 <see cref="Iec103PrivateTypeRegistry.RegisterKod"/> 登记友好名称。
/// </remarks>
public static class KodCodes
{
    /// <summary>实际值。</summary>
    public const byte ActualValue = 1;

    /// <summary>缺省值。</summary>
    public const byte DefaultValue = 2;

    /// <summary>量程（最小、最大、步长）。</summary>
    public const byte Range = 3;

    /// <summary>精度（n,m）。</summary>
    public const byte Precision = 5;

    /// <summary>因子。</summary>
    public const byte Factor = 6;

    /// <summary>% 基准。</summary>
    public const byte PercentReference = 7;

    /// <summary>枚举。</summary>
    public const byte Enumeration = 8;

    /// <summary>量纲。</summary>
    public const byte Dimension = 9;

    /// <summary>描述。</summary>
    public const byte Description = 10;

    /// <summary>口令录入。</summary>
    public const byte PasswordEntry = 12;

    /// <summary>只读。</summary>
    public const byte ReadOnly = 13;

    /// <summary>只写。</summary>
    public const byte WriteOnly = 14;

    /// <summary>对应功能类型与信息序号。</summary>
    public const byte CorrespondingFunInf = 19;

    /// <summary>对应事件。</summary>
    public const byte CorrespondingEvent = 20;

    /// <summary>枚举文本数组。</summary>
    public const byte EnumeratedTextArray = 21;

    /// <summary>枚举值数组。</summary>
    public const byte EnumeratedValueArray = 22;

    /// <summary>相关条目。</summary>
    public const byte RelatedEntries = 23;

    /// <summary>KOD 语义描述；非标准值返回 <c>"KOD {n}"</c>。</summary>
    public static string Describe(byte kod) => kod switch
    {
        ActualValue => "实际值",
        DefaultValue => "缺省值",
        Range => "量程（最小、最大、步长）",
        Precision => "精度（n,m）",
        Factor => "因子",
        PercentReference => "% 基准",
        Enumeration => "枚举",
        Dimension => "量纲",
        Description => "描述",
        PasswordEntry => "口令录入",
        ReadOnly => "只读",
        WriteOnly => "只写",
        CorrespondingFunInf => "对应功能类型与信息序号",
        CorrespondingEvent => "对应事件",
        EnumeratedTextArray => "枚举文本数组",
        EnumeratedValueArray => "枚举值数组",
        RelatedEntries => "相关条目",
        _ => $"KOD {kod}",
    };
}

/// <summary>通用分类数据类型（GDD 的类型字段，DL/T 667-1999 7.2.6.33）。</summary>
public static class GddTypes
{
    /// <summary>无数据。</summary>
    public const byte None = 0;

    /// <summary>ASCII 字符串（OS8ASCII，国内装置常用 GB18030 编码）。</summary>
    public const byte Ascii = 1;

    /// <summary>位串（宽度为位数）。</summary>
    public const byte Bitstring = 2;

    /// <summary>无符号整数。</summary>
    public const byte UnsignedInt = 3;

    /// <summary>有符号整数。</summary>
    public const byte SignedInt = 4;

    /// <summary>无符号定点（/256）。</summary>
    public const byte UnsignedFixed = 5;

    /// <summary>归一化（/32767）。</summary>
    public const byte Normalized = 6;

    /// <summary>短实数 R32.23。</summary>
    public const byte ShortReal = 7;

    /// <summary>实数 R64.53。</summary>
    public const byte Real = 8;

    /// <summary>双点信息。</summary>
    public const byte DoublePoint = 9;

    /// <summary>单点信息。</summary>
    public const byte SinglePoint = 10;

    /// <summary>二进制时间。</summary>
    public const byte BinaryTime = 14;

    /// <summary>通用分类标识号。</summary>
    public const byte Gin = 15;

    /// <summary>相对时间。</summary>
    public const byte RelativeTime = 16;

    /// <summary>功能类型与信息序号。</summary>
    public const byte FunInf = 17;

    /// <summary>外部文本号。</summary>
    public const byte ExternalText = 21;

    /// <summary>通用分类回复码。</summary>
    public const byte GenericReplyCode = 22;

    /// <summary>数据结构。</summary>
    public const byte DataStructure = 23;

    /// <summary>索引。</summary>
    public const byte Index = 24;

    /// <summary>GDD 类型描述；非标准值返回 <c>"类型 {n}"</c>。</summary>
    public static string Describe(byte dataType) => dataType switch
    {
        None => "无数据",
        Ascii => "ASCII 字符串",
        Bitstring => "位串",
        UnsignedInt => "无符号整数",
        SignedInt => "有符号整数",
        UnsignedFixed => "无符号定点（/256）",
        Normalized => "归一化（/32767）",
        ShortReal => "短实数 R32.23",
        Real => "实数 R64.53",
        DoublePoint => "双点信息",
        SinglePoint => "单点信息",
        BinaryTime => "二进制时间",
        Gin => "通用分类标识号",
        RelativeTime => "相对时间",
        FunInf => "功能类型与信息序号",
        ExternalText => "外部文本号",
        GenericReplyCode => "通用分类回复码",
        DataStructure => "数据结构",
        Index => "索引",
        _ => $"类型 {dataType}",
    };
}

/// <summary>通用分类回复码 GRC（写确认数据集的值，GDD 类型 22）。</summary>
/// <remarks>细分码因装置而异，<see cref="UnknownGin"/> 与 <see cref="ReadOnlyEntry"/> 是现场最常见取值。</remarks>
public static class GrcCodes
{
    /// <summary>认可。</summary>
    public const byte Acknowledge = 0;

    /// <summary>通用否定（未认可）。</summary>
    public const byte NotAcknowledged = 1;

    /// <summary>常见否定细分：GIN 不存在。</summary>
    public const byte UnknownGin = 2;

    /// <summary>常见否定细分：条目为只读。</summary>
    public const byte ReadOnlyEntry = 7;

    /// <summary>GRC 描述；非标准值返回 <c>"回复码 {n}"</c>。</summary>
    public static string Describe(byte grc) => grc switch
    {
        Acknowledge => "认可",
        UnknownGin => "GIN 不存在",
        ReadOnlyEntry => "条目只读",
        _ => $"回复码 {grc}",
    };
}

/// <summary>
/// 通用分类数据描述 GDD（3 字节）：数据类型 + 数据宽度 + 数量（bit0-6）与连续标志 CON（bit7）。
/// </summary>
/// <remarks>
/// 位串类型的宽度是<b>位数</b>（按字节向上取整），其余类型的宽度是<b>字节数</b>。
/// GID 总长度 = 元素字节数 × 数量。
/// </remarks>
public readonly record struct Gdd(byte DataType, byte DataWidth, byte Number, bool Continuation)
{
    /// <summary>编码长度（字节）。</summary>
    public const int EncodedLength = 3;

    /// <summary>编码为 3 字节。</summary>
    public byte[] Encode() => new[] { DataType, DataWidth, (byte)((Number & 0x7F) | (Continuation ? 0x80 : 0)) };

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"GDD 需要 {EncodedLength} 字节", nameof(destination));
        }

        destination[0] = DataType;
        destination[1] = DataWidth;
        destination[2] = (byte)((Number & 0x7F) | (Continuation ? 0x80 : 0));
        return EncodedLength;
    }

    /// <summary>从 3 字节解码。</summary>
    /// <exception cref="FormatException">不足 3 字节。</exception>
    public static Gdd Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3)
        {
            throw new FormatException($"GDD 需要 3 字节，实际 {bytes.Length}");
        }

        return new Gdd(
            DataType: bytes[0],
            DataWidth: bytes[1],
            Number: (byte)(bytes[2] & 0x7F),
            Continuation: (bytes[2] & 0x80) != 0);
    }

    /// <summary>单个元素的编码字节数（位串按位向上取整到字节）。</summary>
    public int ElementSize
        => DataType == GddTypes.Bitstring ? Math.Clamp((DataWidth - 1) / 8 + 1, 1, 32) : DataWidth;

    /// <summary>该 GDD 描述的 GID 总字节数。</summary>
    public int GidSize => ElementSize * Number;
}

/// <summary>
/// 通用分类数据集（ASDU 10 的一个条目）：GIN + KOD + GDD + GID。
/// </summary>
/// <remarks>
/// 编码：<c>GIN(2) + KOD(1) + GDD(3) + GID</c>。
/// 提供 ASCII/整数/浮点/量程/回复码的便捷构造与按 GDD 类型的值解码。
/// <para>
/// <b>OS8ASCII 编码</b>：国内装置通用分类描述通行 GB2312 中文（8 位字符集），
/// 用 GB18030（向下兼容）编解码，纯 ASCII 文本行为与 ASCII 完全一致。
/// </para>
/// </remarks>
public sealed record GenericDataSet(Gin Gin, byte Kod, Gdd Gdd, byte[] Gid)
{
    /// <summary>数据集固定头长度（GIN 2 + KOD 1 + GDD 3）。</summary>
    public const int HeaderLength = 6;

    /// <summary>单数据集 ASCII 字节预算：253(ASDU 上限) - 6(ASDU 头) - 2(RII+NGD) - 6(数据集头)。</summary>
    public const int MaxAsciiBytes = 239;

    private static readonly Encoding Os8Text = CreateOs8Encoding();

    private static Encoding CreateOs8Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    }

    /// <summary>该数据集编码后的总字节数。</summary>
    public int EncodedSize => HeaderLength + Gid.Length;

    /// <summary>编码为字节。</summary>
    public byte[] Encode()
    {
        var payload = new byte[EncodedSize];
        EncodeTo(payload);
        return payload;
    }

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        if (destination.Length < EncodedSize)
        {
            throw new ArgumentException($"目标缓冲至少需要 {EncodedSize} 字节", nameof(destination));
        }

        destination[0] = Gin.Group;
        destination[1] = Gin.Entry;
        destination[2] = Kod;
        destination[3] = Gdd.DataType;
        destination[4] = Gdd.DataWidth;
        destination[5] = (byte)((Gdd.Number & 0x7F) | (Gdd.Continuation ? 0x80 : 0));
        Gid.CopyTo(destination.Slice(HeaderLength));
        return EncodedSize;
    }

    /// <summary>从字节解码一个数据集，<paramref name="consumed"/> 返回消耗的字节数。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out GenericDataSet? set, out int consumed)
    {
        if (bytes.Length < HeaderLength)
        {
            set = null;
            consumed = 0;
            return false;
        }

        var gdd = Gdd.Decode(bytes.Slice(3, Gdd.EncodedLength));
        var gidSize = gdd.GidSize;
        if (gidSize < 0 || bytes.Length < HeaderLength + gidSize)
        {
            set = null;
            consumed = 0;
            return false;
        }

        set = new GenericDataSet(
            Gin.Decode(bytes),
            bytes[2],
            gdd,
            bytes.Slice(HeaderLength, gidSize).ToArray());
        consumed = HeaderLength + gidSize;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  便捷构造
    // ═══════════════════════════════════════════════════════════════

    /// <summary>GB18030 编码（与数据集发送/解码一致的编码器）。</summary>
    public static byte[] EncodeGb18030(string text) => Os8Text.GetBytes(text);

    /// <summary>GB18030 解码。</summary>
    public static string DecodeGb18030(ReadOnlySpan<byte> bytes) => Os8Text.GetString(bytes);

    /// <summary>构造 ASCII（GB18030）描述数据集，超过单帧预算时按字符边界截断。</summary>
    /// <remarks>
    /// 截断必须落在字符边界：拦腰截 GB18030 多字节序列会产生乱码尾，
    /// 且宽度字节须始终等于 GID 实际长度，否则对端解析失步。
    /// </remarks>
    public static GenericDataSet Ascii(Gin gin, byte kod, string text)
    {
        var data = Os8Text.GetBytes(text);
        if (data.Length > MaxAsciiBytes)
        {
            // 按 Unicode 标量（而非 UTF-16 char）迭代：非 BMP 字符（代理对）
            // 逐 char 编码会把孤代理变成替换符，破坏「截断落在字符边界」的承诺。
            var truncated = new List<byte>(MaxAsciiBytes);
            for (var i = 0; i < text.Length;)
            {
                var scalarLen = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? 2
                    : 1;

                var encoded = Os8Text.GetBytes(text.Substring(i, scalarLen));
                if (truncated.Count + encoded.Length > MaxAsciiBytes)
                {
                    break;
                }

                truncated.AddRange(encoded);
                i += scalarLen;
            }

            data = truncated.ToArray();
        }

        return new GenericDataSet(gin, kod, new Gdd(GddTypes.Ascii, (byte)Math.Min(data.Length, 255), 1, false), data);
    }

    /// <summary>构造无符号整数数据集（小端，宽度字节数）。</summary>
    public static GenericDataSet Unsigned(Gin gin, byte kod, uint value, byte width = 4)
    {
        var data = new byte[width];
        for (var i = 0; i < width; i++)
        {
            data[i] = (byte)(value >> (8 * i));
        }

        return new GenericDataSet(gin, kod, new Gdd(GddTypes.UnsignedInt, width, 1, false), data);
    }

    /// <summary>构造有符号整数数据集（小端，宽度字节数）。</summary>
    public static GenericDataSet Signed(Gin gin, byte kod, int value, byte width = 4)
    {
        var data = new byte[width];
        var raw = value;
        for (var i = 0; i < width; i++)
        {
            data[i] = (byte)(raw & 0xFF);
            raw >>= 8;
        }

        return new GenericDataSet(gin, kod, new Gdd(GddTypes.SignedInt, width, 1, false), data);
    }

    /// <summary>构造短实数（R32.23 float）数据集。</summary>
    public static GenericDataSet Float(Gin gin, byte kod, float value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, value);
        return new GenericDataSet(gin, kod, new Gdd(GddTypes.ShortReal, 4, 1, false), data);
    }

    /// <summary>量程（KOD=3）：最小值、最大值、步长三个短实数。</summary>
    public static GenericDataSet Range(Gin gin, float min, float max, float step)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0, 4), min);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), max);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8, 4), step);
        return new GenericDataSet(gin, KodCodes.Range, new Gdd(GddTypes.ShortReal, 4, 3, false), data);
    }

    /// <summary>回复码数据集（写确认用，GDD 类型 22）。</summary>
    public static GenericDataSet ReplyCode(Gin gin, byte kod, byte grc)
        => new(gin, kod, new Gdd(GddTypes.GenericReplyCode, 1, 1, false), new[] { grc });

    // ═══════════════════════════════════════════════════════════════
    //  值解码
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 按 GDD 类型解码 GID 的<b>首个</b>元素：ASCII→string，整数→long，浮点→float/double，
    /// 位串/其他→十六进制字符串。
    /// </summary>
    /// <remarks>
    /// 兼容保留的单值入口。GDD.Number &gt; 1 时请用 <see cref="DecodeValues"/>
    /// 取全部元素（例如 KOD=3 量程的 3 个短实数 min/max/step）。
    /// </remarks>
    /// <returns>无法识别的私有 GDD 类型返回原始十六进制串，绝不返回 null 丢失信息。</returns>
    public object DecodeValue() => DecodeValues()[0];

    /// <summary>
    /// 按 GDD 类型解码 GID 的<b>全部</b>元素。元素宽度取 <see cref="Gdd.ElementSize"/>，
    /// 数量取 <see cref="Gdd.Number"/>；长度不足或私有类型时对应元素返回原始十六进制串。
    /// </summary>
    /// <remarks>
    /// 全部整数/浮点读取都用 <see cref="BinaryPrimitives"/> 的显式小端 API——
    /// 线路格式是小端，不能依赖 <see cref="BitConverter"/> 的主机字节序
    /// （大端平台如 s390x/ppc64 会整体反转）。
    /// </remarks>
    public IReadOnlyList<object> DecodeValues()
    {
        var results = new List<object>();
        if (Gdd.DataType == GddTypes.Ascii)
        {
            results.Add(Os8Text.GetString(Gid).TrimEnd('\0', ' '));
            return results;
        }

        var elementSize = Gdd.ElementSize;
        if (elementSize <= 0)
        {
            results.Add(Convert.ToHexString(Gid));
            return results;
        }

        var count = Math.Max(1, (int)Gdd.Number);
        for (var i = 0; i < count; i++)
        {
            var offset = i * elementSize;
            if (offset + elementSize > Gid.Length)
            {
                // 截断的数据集：保留剩余原始字节，不丢信息
                results.Add(Convert.ToHexString(Gid.AsSpan(offset)));
                break;
            }

            results.Add(DecodeElement(Gid.AsSpan(offset, elementSize)));
        }

        return results;
    }

    private object DecodeElement(ReadOnlySpan<byte> element) => Gdd.DataType switch
    {
        GddTypes.UnsignedInt => DecodeUnsigned(element),
        GddTypes.SignedInt => DecodeSigned(element),
        GddTypes.UnsignedFixed => DecodeUnsigned(element) / 256f,
        GddTypes.Normalized => element.Length >= 2
            ? BinaryPrimitives.ReadInt16LittleEndian(element) / 32767f
            : (object)Convert.ToHexString(element),
        GddTypes.ShortReal => element.Length >= 4
            ? BinaryPrimitives.ReadSingleLittleEndian(element)
            : (object)Convert.ToHexString(element),
        GddTypes.Real => element.Length >= 8
            ? BinaryPrimitives.ReadDoubleLittleEndian(element)
            : (object)Convert.ToHexString(element),
        _ => Convert.ToHexString(element),
    };

    /// <summary>读 LE 无符号整数（最多 8 字节，超出部分忽略）。</summary>
    private static long DecodeUnsigned(ReadOnlySpan<byte> element)
    {
        long value = 0;
        for (var i = 0; i < element.Length && i < 8; i++)
        {
            value |= (long)element[i] << (8 * i);
        }

        return value;
    }

    /// <summary>
    /// 读 LE 有符号整数并按<b>单元素宽度</b>做符号扩展。
    /// 原实现用整个 GID 的最后一字节判符号、按总长算 bits，
    /// 在 Number&gt;1 时会把前一个元素的高字节误当符号位。
    /// </summary>
    private static long DecodeSigned(ReadOnlySpan<byte> element)
    {
        if (element.Length == 0)
        {
            return 0;
        }

        var value = DecodeUnsigned(element);
        var bits = Math.Min(element.Length, 8) * 8;
        if (bits is > 0 and < 64 && (element[element.Length - 1] & 0x80) != 0)
        {
            value |= -1L << bits;
        }

        return value;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var value = DecodeValue();
        return $"GIN={Gin} KOD={KodCodes.Describe(Kod)} {GddTypes.Describe(Gdd.DataType)}"
            + (Gdd.Number > 1 ? $"×{Gdd.Number}" : string.Empty)
            + $" = {value}";
    }
}

/// <summary>NGD（数据集数目与后续标志）：bit0-5 数目、bit7 后续标志 D（后续 ASDU 同 RII）。</summary>
public static class NgdCodes
{
    /// <summary>数目掩码（bit0-5）。</summary>
    public const byte CountMask = 0x3F;

    /// <summary>后续标志（bit7）。</summary>
    public const byte ContinuationMask = 0x80;

    /// <summary>编码 NGD。</summary>
    public static byte Encode(int count, bool continuation)
        => (byte)(Math.Clamp(count, 0, CountMask) | (continuation ? ContinuationMask : 0));

    /// <summary>解码 NGD。</summary>
    public static (int Count, bool Continuation) Decode(byte ngd) => (ngd & CountMask, (ngd & ContinuationMask) != 0);
}
