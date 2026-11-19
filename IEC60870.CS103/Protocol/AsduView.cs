//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 ASDU 的零拷贝只读视图。
/// </summary>
/// <remarks>
/// <c>readonly ref struct</c>：不复制 ASDU 字节，
/// 头部字段在构造时一次解析，数据区以 <see cref="ReadOnlySpan{T}"/> 暴露。
/// <para>
/// <b>生命周期</b>：仅在其回调（<c>AsduReceived</c>）期间有效，视图内所有 span
/// 指向接收缓冲区。需要跨回调留存时用 <see cref="Asdu.Materialize"/> 物化。
/// </para>
/// <para>
/// 厂商私有 TI/FUN/INF 在本视图里同样可读（不做白名单过滤），
/// 因此即使一个私有类型都没登记，也能通过 <see cref="Raw"/> 拿到完整报文。
/// </para>
/// </remarks>
public readonly ref struct Iec103AsduView
{
    private readonly ReadOnlySpan<byte> _raw;
    private readonly Iec103AsduLayout _layout;

    /// <summary>
    /// 从完整 ASDU 字节构造视图（按标准 103 的 6 字节头解析）。
    /// </summary>
    /// <exception cref="ArgumentException">长度不足 6 字节。</exception>
    public Iec103AsduView(ReadOnlySpan<byte> raw)
        : this(raw, Iec103AsduLayout.Standard)
    {
    }

    /// <summary>
    /// 从完整 ASDU 字节构造视图，显式指定头部布局。
    /// </summary>
    /// <param name="raw">完整 ASDU 字节。</param>
    /// <param name="layout">头部布局；标准 103 用 <see cref="Iec103AsduLayout.Standard"/>。</param>
    /// <exception cref="ArgumentException">长度不足该布局要求的头长度。</exception>
    public Iec103AsduView(ReadOnlySpan<byte> raw, Iec103AsduLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (raw.Length < layout.HeaderLength)
        {
            throw new ArgumentException($"ASDU 至少 {layout.HeaderLength} 字节，实际 {raw.Length}", nameof(raw));
        }

        _raw = raw;
        _layout = layout;
    }

    /// <summary>该视图使用的头部布局。</summary>
    public Iec103AsduLayout Layout => _layout;

    /// <summary>该布局下的 ASDU 头长度。</summary>
    public int HeaderLength => _layout.HeaderLength;

    /// <summary>类型标识（TI），可能是厂商私有值。</summary>
    public byte TypeId => _raw[0];

    /// <summary>VSQ 原始字节（bit7 SQ + bit0-6 数目）。</summary>
    public byte VsQ => _raw[1];

    /// <summary>序列标志（VSQ bit7）。</summary>
    public bool Sq => (_raw[1] & 0x80) != 0;

    /// <summary>信息对象数目（VSQ bit0-6）。</summary>
    public int Count => _raw[1] & 0x7F;

    /// <summary>COT 的首字节原始值（含 P/N 位）。</summary>
    public byte CotByte => _raw[_layout.CotOffset];

    /// <summary>传送原因（按布局的 COT 位定义去掉 P/N 位）。</summary>
    /// <remarks>
    /// 标准 103 取 bit6-0；若布局声明为 IEC 104 位序则取 bit5-0（bit7 是 T、bit6 是 P/N）。
    /// </remarks>
    public byte Cot => (byte)(_raw[_layout.CotOffset] & _layout.CotValueMask);

    /// <summary>否定确认标志（按布局的 COT 位定义取）。</summary>
    /// <remarks>标准 103 看 bit7；IEC 104 位序看 bit6。</remarks>
    public bool Negative => (_raw[_layout.CotOffset] & _layout.CotNegativeMask) != 0;

    /// <summary>
    /// 完整的传送原因字段（1 或 2 字节，小端）。
    /// </summary>
    /// <remarks>
    /// 标准 103 为 1 字节；按 104 实现的承载为 2 字节，其高字节是源发地址，
    /// 与原因值同属传送原因字段。库不解释该字节，原样返回整个字段。
    /// </remarks>
    public int CotField => _layout.ReadCot(_raw);

    /// <summary>
    /// 公共地址 CA。
    /// </summary>
    /// <remarks>
    /// 宽度由布局决定：标准 103 为 1 字节（高字节恒 0）；厂商私有承载可能为 2 字节小端。
    /// 2 字节场合的字节拆分（如「低字节扇区、高字节装置地址」）属厂商约定，
    /// <b>库不做解释</b>，原样返回 16 位值。
    /// </remarks>
    public int Ca => _layout.ReadCa(_raw);

    /// <summary>功能类型 FUN（103 两级寻址的第一级）。</summary>
    public byte Fun => _raw[_layout.FunOffset];

    /// <summary>信息序号 INF（103 两级寻址的第二级）。</summary>
    public byte Inf => _raw[_layout.InfOffset];

    /// <summary>信息体数据区（头部之后）。</summary>
    public ReadOnlySpan<byte> Data => _raw[_layout.HeaderLength..];

    /// <summary>完整 ASDU 字节（含头部），回调期有效。</summary>
    public ReadOnlySpan<byte> Raw => _raw;

    /// <summary>信息体数据长度。</summary>
    public int DataLength => _raw.Length - _layout.HeaderLength;

    /// <summary>该 ASDU 是否落在 FUN 专用范围（160-255，厂商私有）。</summary>
    public bool IsPrivateFun => Fun is >= Asdu.PrivateFunFrom and <= Asdu.PrivateFunTo;

    /// <summary>该 ASDU 是否为通用分类服务（FUN=254）。</summary>
    public bool IsGenericService => Fun == Asdu.GenericFun;

    /// <summary>尝试从数据区读取一个 CP56Time2a 时标（需至少 7 字节）。</summary>
    public bool TryGetCp56Time2a(int offset, out Cp56Time2a time)
    {
        var data = Data;
        if (offset < 0 || data.Length - offset < Cp56Time2a.EncodedLength)
        {
            time = default;
            return false;
        }

        time = Cp56Time2a.Decode(data.Slice(offset, Cp56Time2a.EncodedLength));
        return true;
    }

    /// <summary>尝试读取 TI=1 的 DPI/品质（数据区首字节）。</summary>
    public bool TryGetDpi(out DpiQuality dpi)
    {
        var data = Data;
        if (data.Length < 1)
        {
            dpi = default;
            return false;
        }

        dpi = DpiQuality.Decode(data[0]);
        return true;
    }

    /// <summary>诊断用描述。</summary>
    public string Describe()
        => $"TI={TypeId}({TypeIds.Describe(TypeId)}) COT={Cot}({CotCodes.Describe(CotByte)}) CA={Ca} FUN={Fun} INF={Inf} 数据 {DataLength} 字节";
}

/// <summary>零拷贝 ASDU 回调委托（主站侧）。</summary>
/// <param name="asdu">仅在回调期间有效的视图。</param>
public delegate void Iec103AsduViewHandler(in Iec103AsduView asdu);

/// <summary>零拷贝 ASDU 回调委托（从站侧，带会话）。</summary>
/// <param name="session">发起该 ASDU 的会话。</param>
/// <param name="asdu">仅在回调期间有效的视图。</param>
public delegate void Iec103ServerAsduHandler(IIec103Session session, in Iec103AsduView asdu);
