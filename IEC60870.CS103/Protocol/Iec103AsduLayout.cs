//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103)
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// ASDU 头部布局：各字段在 ASDU 中的偏移与宽度。
/// </summary>
/// <remarks>
/// <para>
/// 标准 103（DL/T 667-1999）的头部固定 6 字节：
/// </para>
/// <code>TI(1) | VSQ(1) | COT(1) | CA(1) | FUN(1) | INF(1) | 数据</code>
/// <para>
/// 本类型存在的原因是部分厂商在网口上自定义了不同的 ASDU 头（例如把 COT 与 CA 都扩成
/// 2 字节）。这类承载不属于任何标准，实现由 <see cref="IIec103FramingCodec"/> 提供，
/// 库只描述布局。标准 FT1.2 承载恒用 <see cref="Standard"/>。
/// </para>
/// </remarks>
/// <param name="HeaderLength">头部总长度（标准为 6）。</param>
/// <param name="CotOffset">传送原因字段的起始偏移。</param>
/// <param name="CotWidth">传送原因字段宽度（1 或 2 字节）。</param>
/// <param name="CaOffset">公共地址字段的起始偏移。</param>
/// <param name="CaWidth">公共地址字段宽度（1 或 2 字节；2 字节时按小端解释）。</param>
/// <param name="FunOffset">功能类型 FUN 的偏移。</param>
/// <param name="InfOffset">信息序号 INF 的偏移。</param>
/// <param name="CotNegativeMask">
/// COT 首字节里「否定确认」位的掩码：标准 103 是 bit7（<c>0x80</c>），
/// IEC 104 位序是 bit6（<c>0x40</c>，bit7 留给试验位 T）。
/// </param>
/// <param name="CotValueMask">
/// COT 首字节里「原因值」的掩码：标准 103 是 bit6-0（<c>0x7F</c>），
/// IEC 104 位序是 bit5-0（<c>0x3F</c>）。
/// </param>
public sealed record Iec103AsduLayout(
    int HeaderLength,
    int CotOffset,
    int CotWidth,
    int CaOffset,
    int CaWidth,
    int FunOffset,
    int InfOffset,
    byte CotNegativeMask = 0x80,
    byte CotValueMask = 0x7F)
{
    /// <summary>标准 103（DL/T 667-1999）的 6 字节头部布局。</summary>
    /// <remarks>
    /// <c>TI(0) VSQ(1) COT(2) CA(3) FUN(4) INF(5)</c>，COT 与 CA 均为 1 字节，
    /// COT 按 103 位序：<c>bit7=P/N</c>、<c>bit6-0=原因</c>。
    /// </remarks>
    public static Iec103AsduLayout Standard { get; } = new(
        HeaderLength: 6,
        CotOffset: 2,
        CotWidth: 1,
        CaOffset: 3,
        CaWidth: 1,
        FunOffset: 4,
        InfOffset: 5,
        CotNegativeMask: 0x80,
        CotValueMask: 0x7F);

    /// <summary>该布局的 COT 是否采用 IEC 104 位序（bit7=T、bit6=P/N、bit5-0=原因）。</summary>
    public bool UsesIec104Cot => CotNegativeMask == 0x40;

    /// <summary>按本布局从 ASDU 字节中读出公共地址（2 字节时为小端）。</summary>
    /// <param name="asdu">完整 ASDU 字节。</param>
    /// <exception cref="ArgumentException">长度不足以覆盖头部。</exception>
    public int ReadCa(ReadOnlySpan<byte> asdu)
    {
        EnsureLength(asdu);
        return CaWidth == 2
            ? asdu[CaOffset] | (asdu[CaOffset + 1] << 8)
            : asdu[CaOffset];
    }

    /// <summary>
    /// 按本布局读出完整的传送原因字段（1 或 2 字节，小端）。
    /// </summary>
    /// <param name="asdu">完整 ASDU 字节。</param>
    /// <remarks>
    /// 标准 103 的 COT 只有 1 字节；按 104 实现的承载为 2 字节，
    /// 其高字节是「源发地址」，与原因值同属传送原因字段。
    /// </remarks>
    public int ReadCot(ReadOnlySpan<byte> asdu)
    {
        EnsureLength(asdu);
        return CotWidth == 2
            ? asdu[CotOffset] | (asdu[CotOffset + 1] << 8)
            : asdu[CotOffset];
    }

    private void EnsureLength(ReadOnlySpan<byte> asdu)
    {
        if (asdu.Length < HeaderLength)
        {
            throw new ArgumentException($"ASDU 至少 {HeaderLength} 字节，实际 {asdu.Length}", nameof(asdu));
        }
    }

    /// <summary>校验布局自身是否自洽（构造后由 <see cref="IIec103FramingCodec"/> 实现方保证）。</summary>
    /// <exception cref="ArgumentException">偏移/宽度非法。</exception>
    public Iec103AsduLayout Validate()
    {
        if (HeaderLength < 6)
        {
            throw new ArgumentException($"头部长度至少 6 字节（TI/VSQ/COT/CA/FUN/INF），实际 {HeaderLength}", nameof(HeaderLength));
        }

        if (CotWidth is not (1 or 2))
        {
            throw new ArgumentException($"COT 宽度只能是 1 或 2，实际 {CotWidth}", nameof(CotWidth));
        }

        if (CaWidth is not (1 or 2))
        {
            throw new ArgumentException($"CA 宽度只能是 1 或 2，实际 {CaWidth}", nameof(CaWidth));
        }

        if (CotOffset != 2)
        {
            throw new ArgumentException($"COT 必须紧随 TI/VSQ 之后（偏移 2），实际 {CotOffset}", nameof(CotOffset));
        }

        RequireInsideHeader(CotOffset + CotWidth - 1, "CotOffset+CotWidth");

        // CA 必须紧接 COT 之后
        if (CaOffset != CotOffset + CotWidth)
        {
            throw new ArgumentException(
                $"CA 必须紧接 COT 之后（偏移 {CotOffset + CotWidth}），实际 {CaOffset}", nameof(CaOffset));
        }

        RequireInsideHeader(CaOffset + CaWidth - 1, "CaOffset+CaWidth");
        RequireInsideHeader(FunOffset, "FunOffset");
        RequireInsideHeader(InfOffset, "InfOffset");

        return this;
    }

    private void RequireInsideHeader(int offset, string name)
    {
        if (offset < 0 || offset >= HeaderLength)
        {
            throw new ArgumentException($"{name} 必须在 0-{HeaderLength - 1} 内，实际 {offset}", name);
        }
    }
}