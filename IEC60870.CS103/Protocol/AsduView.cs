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

    /// <summary>从完整 ASDU 字节构造视图（至少 6 字节）。</summary>
    /// <exception cref="ArgumentException">长度不足 6 字节。</exception>
    public Iec103AsduView(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Asdu.HeaderLength)
        {
            throw new ArgumentException($"ASDU 至少 {Asdu.HeaderLength} 字节，实际 {raw.Length}", nameof(raw));
        }

        _raw = raw;
    }

    /// <summary>类型标识（TI），可能是厂商私有值。</summary>
    public byte TypeId => _raw[0];

    /// <summary>VSQ 原始字节（bit7 SQ + bit0-6 数目）。</summary>
    public byte VsQ => _raw[1];

    /// <summary>序列标志（VSQ bit7）。</summary>
    public bool Sq => (_raw[1] & 0x80) != 0;

    /// <summary>信息对象数目（VSQ bit0-6）。</summary>
    public int Count => _raw[1] & 0x7F;

    /// <summary>COT 原始字节（含 P/N 位）。</summary>
    public byte CotByte => _raw[2];

    /// <summary>传送原因（去 P/N 位）。</summary>
    public byte Cot => (byte)(_raw[2] & CotCodes.ValueMask);

    /// <summary>否定确认标志（COT bit7）。</summary>
    public bool Negative => (_raw[2] & CotCodes.NegativeMask) != 0;

    /// <summary>公共地址 CA。</summary>
    public byte Ca => _raw[3];

    /// <summary>功能类型 FUN（103 两级寻址的第一级）。</summary>
    public byte Fun => _raw[4];

    /// <summary>信息序号 INF（103 两级寻址的第二级）。</summary>
    public byte Inf => _raw[5];

    /// <summary>信息体数据区（FUN/INF 之后）。</summary>
    public ReadOnlySpan<byte> Data => _raw[Asdu.HeaderLength..];

    /// <summary>完整 ASDU 字节（含 6 字节头），回调期有效。</summary>
    public ReadOnlySpan<byte> Raw => _raw;

    /// <summary>信息体数据长度。</summary>
    public int DataLength => _raw.Length - Asdu.HeaderLength;

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
