//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103)
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 应用服务数据单元（物化模型）。
/// </summary>
/// <remarks>
/// 头部布局（6 字节）：
/// <c>TI(1) | VSQ(1) | COT(1) | CA(1) | FUN(1) | INF(1)</c> + 数据。
/// <list type="bullet">
///   <item><b>VSQ</b>：bit7 = SQ（序列），bit0-6 = 数目。</item>
///   <item><b>COT</b>：bit7 = P/N（否定），bit0-6 = 传送原因。</item>
///   <item><b>FUN/INF</b>：103 特有的<b>两级寻址</b>——兼容范围 0-127、
///         专用范围 160-255、254 = 通用分类、255 = 全局。</item>
/// </list>
/// <para>
/// 接收热路径请优先使用零拷贝的 <see cref="Iec103AsduView"/>；需要跨回调留存时才物化为本类型。
/// </para>
/// </remarks>
public sealed record Asdu(
    byte TypeId,
    bool Sq,
    int Count,
    byte Cot,
    bool Negative,
    int Ca,
    byte Fun,
    byte Inf,
    byte[] Data,
    Iec103AsduLayout? Layout = null)
{
    /// <summary>标准 103 的 ASDU 固定头长度（TI/VSQ/COT/CA/FUN/INF）。</summary>
    public const int HeaderLength = 6;

    /// <summary>本 ASDU 实际使用的头部布局（<c>null</c> 等同标准布局）。</summary>
    public Iec103AsduLayout EffectiveLayout => Layout ?? Iec103AsduLayout.Standard;

    /// <summary>全局功能类型（FUN=255）。</summary>
    public const byte GlobalFun = 255;

    /// <summary>通用分类功能类型（FUN=254，GEN）。</summary>
    public const byte GenericFun = 254;

    /// <summary>专用范围功能类型下界（FUN 160-255 为厂商私有）。</summary>
    public const byte PrivateFunFrom = 160;

    /// <summary>专用范围功能类型上界。</summary>
    public const byte PrivateFunTo = 255;

    /// <summary>全局信息序号：80 = 兼容范围标识报文。</summary>
    public const byte InfCompatIdent = 80;

    /// <summary>全局信息序号：81 = 专用范围标识报文。</summary>
    public const byte InfPrivateIdent = 81;

    /// <summary>编码为 ASDU 字节（不含帧头）。</summary>
    public byte[] Encode()
    {
        var payload = new byte[Data.Length + EffectiveLayout.HeaderLength];
        EncodeTo(payload);
        return payload;
    }

    /// <summary>编码到目标 span，返回写入长度；目标不足时抛 <see cref="ArgumentException"/>。</summary>
    public int EncodeTo(Span<byte> destination)
    {
        var layout = EffectiveLayout;
        var total = Data.Length + layout.HeaderLength;
        if (destination.Length < total)
        {
            throw new ArgumentException($"目标缓冲至少需要 {total} 字节", nameof(destination));
        }

        if (Ca < 0 || Ca > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(Ca), $"公共地址须在 0-65535，实际 {Ca}");
        }

        // CA 超宽必须报错而不是截断：静默截断会把发往 CA=0x0101 的报文发到 CA=0x01
        if (layout.CaWidth == 1 && Ca > 0xFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Ca), $"当前头部布局的公共地址只有 1 字节（0-255），实际 {Ca}；2 字节 CA 需要对应的厂商布局");
        }

        destination[0] = TypeId;
        destination[1] = VsQBits(Sq, Count);
        destination[layout.CotOffset] = CotBits(Cot, Negative, layout);

        // COT 为 2 字节时（按 104 实现），高字节是源发地址，本库不解释、写 0
        if (layout.CotWidth == 2)
        {
            destination[layout.CotOffset + 1] = 0;
        }

        if (layout.CaWidth == 2)
        {
            destination[layout.CaOffset] = (byte)(Ca & 0xFF);
            destination[layout.CaOffset + 1] = (byte)(Ca >> 8);
        }
        else
        {
            destination[layout.CaOffset] = (byte)Ca;
        }

        destination[layout.FunOffset] = Fun;
        destination[layout.InfOffset] = Inf;

        Data.CopyTo(destination.Slice(layout.HeaderLength));
        return total;
    }

    /// <summary>
    /// 拼装 VSQ 字节。count 必须在 0-127，越界直接抛异常——
    /// 原实现用 <c>count &amp; 0x7F</c> 静默截断，导致 Encode 与 TryDecode 不互逆
    /// （Count=200 编码后解出 72），且会掩盖上层数目错误。
    /// </summary>
    public static byte VsQBits(bool sq, int count)
    {
        if ((uint)count > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"VSQ 数目须在 0-127，实际 {count}");
        }

        return (byte)((sq ? 0x80 : 0) | count);
    }

    /// <summary>拼装 COT 字节（标准 103 位序：bit7 = P/N）。</summary>
    public static byte CotBits(byte cot, bool negative) => (byte)((cot & CotCodes.ValueMask) | (negative ? CotCodes.NegativeMask : 0));

    /// <summary>按指定布局的 COT 位定义拼装 COT 字节。</summary>
    /// <param name="cot">传送原因值。</param>
    /// <param name="negative">是否否定确认。</param>
    /// <param name="layout">头部布局（决定 P/N 与原因值占哪些位）。</param>
    public static byte CotBits(byte cot, bool negative, Iec103AsduLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return (byte)((cot & layout.CotValueMask) | (negative ? layout.CotNegativeMask : 0));
    }

    /// <summary>从 ASDU 字节解码为物化模型（按标准 103 的 6 字节头）。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Asdu? asdu, out string? error)
        => TryDecode(bytes, Iec103AsduLayout.Standard, out asdu, out error);

    /// <summary>从 ASDU 字节解码为物化模型，显式指定头部布局。</summary>
    /// <param name="bytes">完整 ASDU 字节。</param>
    /// <param name="layout">头部布局；标准 103 用 <see cref="Iec103AsduLayout.Standard"/>。</param>
    /// <param name="asdu">解码结果。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, Iec103AsduLayout layout, out Asdu? asdu, out string? error)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (bytes.Length < layout.HeaderLength)
        {
            asdu = null;
            error = $"ASDU 至少 {layout.HeaderLength} 字节，实际 {bytes.Length}";
            return false;
        }

        asdu = new Asdu(
            TypeId: bytes[0],
            Sq: (bytes[1] & 0x80) != 0,
            Count: bytes[1] & 0x7F,
            Cot: (byte)(bytes[layout.CotOffset] & layout.CotValueMask),
            Negative: (bytes[layout.CotOffset] & layout.CotNegativeMask) != 0,
            Ca: layout.ReadCa(bytes),
            Fun: bytes[layout.FunOffset],
            Inf: bytes[layout.InfOffset],
            Data: bytes[layout.HeaderLength..].ToArray(),
            Layout: ReferenceEquals(layout, Iec103AsduLayout.Standard) ? null : layout);
        error = null;
        return true;
    }

    /// <summary>从零拷贝视图物化（回调期外留存用）。</summary>
    public static Asdu Materialize(in Iec103AsduView view) => new(
        view.TypeId, view.Sq, view.Count, view.Cot, view.Negative,
        view.Ca, view.Fun, view.Inf, view.Data.ToArray(),
        ReferenceEquals(view.Layout, Iec103AsduLayout.Standard) ? null : view.Layout);

    /// <summary>该 ASDU 是否落在 FUN 专用范围（160-255，厂商私有）。</summary>
    public bool IsPrivateFun => Fun is >= PrivateFunFrom and <= PrivateFunTo;

    /// <summary>该 ASDU 是否为通用分类服务（FUN=254）。</summary>
    public bool IsGenericService => Fun == GenericFun;

    /// <summary>诊断用描述：<c>TI(名称) COT(名称) CA FUN INF 长度</c>。</summary>
    public string Describe()
        => $"TI={TypeId}({TypeIds.Describe(TypeId)}) COT={Cot}({CotCodes.Describe(CotBits(Cot, Negative, EffectiveLayout))}) CA={Ca} FUN={Fun} INF={Inf} 数据 {Data.Length} 字节";

    // ═══════════════════════════════════════════════════════════════
    //  基础四件套工厂
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 时间同步命令（TI=6，COT=8，数据 CP56Time2a）。
    /// </summary>
    /// <remarks>主站实测多见 FUN=0（如 ZLG E8000 例程），从站回读帧用 FUN=255（全局）。</remarks>
    public static Asdu ClockSync(Cp56Time2a time, int ca, byte fun = 0)
        => new(TypeIds.ClockSync, true, 1, CotCodes.ClockSync, false, ca, fun, 0, time.Encode());

    /// <summary>从站时间同步回读（FUN=255 全局，携带设备当前时钟）。</summary>
    public static Asdu ClockSyncEcho(Cp56Time2a time, int ca)
        => ClockSync(time, ca, GlobalFun);

    /// <summary>总召唤命令（TI=7，FUN=255 INF=0，COT=9，数据为扫描序号 SCN）。</summary>
    public static Asdu GeneralInterrogation(int ca, byte scn = 0)
        => new(TypeIds.GeneralInterrogation, true, 1, CotCodes.GiStart, false, ca, GlobalFun, 0, new[] { scn });

    /// <summary>总召唤终止（TI=8，COT=10，回带 SCN）。</summary>
    public static Asdu GeneralInterrogationEnd(int ca, byte scn = 0)
        => new(TypeIds.GeneralInterrogationEnd, true, 1, CotCodes.GiStop, false, ca, GlobalFun, 0, new[] { scn });

    /// <summary>
    /// 标识报文（TI=5，FUN=255，INF=80 兼容范围 / 81 专用范围），8 字节 ASCII（空格填充）。
    /// </summary>
    public static Asdu Identification(int ca, bool privateRange, string ascii)
    {
        var text = ascii.Length > 8 ? ascii[..8] : ascii.PadRight(8);
        var data = Encoding.ASCII.GetBytes(text);
        return new Asdu(
            TypeIds.Identification, true, 1,
            privateRange ? CotCodes.Restart : CotCodes.ResetCu,
            false, ca, GlobalFun, privateRange ? InfPrivateIdent : InfCompatIdent, data);
    }

    /// <summary>
    /// 带时标 CP56Time2a 的监视方向信息（TI=1）。
    /// 数据 = DPI/品质(1) + 4 个附加信息字节（兼容方向固定 FF）+ CP56Time2a(7)。
    /// </summary>
    public static Asdu TimeTaggedMessage(int ca, byte fun, byte inf, DpiQuality dpi, Cp56Time2a time, byte cot, byte[]? extraInfo = null)
    {
        var data = new byte[12];
        data[0] = dpi.Encode();

        // 附加信息固定 4 字节（兼容方向为 FF）。调用方传入不足 4 字节时
        // 按实际长度复制、剩余补 FF，避免 AsSpan(0,4) 越界抛异常。
        data.AsSpan(1, 4).Fill(0xFF);
        if (extraInfo is { Length: > 0 })
        {
            extraInfo.AsSpan(0, Math.Min(4, extraInfo.Length)).CopyTo(data.AsSpan(1));
        }

        time.Encode().AsSpan().CopyTo(data.AsSpan(5));
        return new Asdu(TypeIds.TimeTaggedMessage, true, 1, cot, false, ca, fun, inf, data);
    }

    // ═══════════════════════════════════════════════════════════════
    //  通用分类（FUN=254）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>读全目录命令（TI=21，COT=42）：RII + NOG=1 + GIN 0000 + KOD=描述。</summary>
    public static Asdu GenericReadAll(int ca, byte rii)
    {
        var data = new byte[5];
        data[0] = rii;
        data[1] = 1;
        Gin.Zero.Encode().CopyTo(data, 2);
        data[4] = KodCodes.Description;
        return new Asdu(TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false, ca, GenericFun, 0, data);
    }

    /// <summary>读指定条目命令（TI=21，COT=42）：RII + NOG + 每条 GIN/KOD。</summary>
    public static Asdu GenericRead(int ca, byte rii, params (Gin Gin, byte Kod)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(entries), $"NOG 为 1 字节，最多 255 条，实际 {entries.Length}");
        }

        var data = new byte[2 + entries.Length * 3];
        data[0] = rii;
        data[1] = (byte)entries.Length;
        var offset = 2;
        foreach (var (gin, kod) in entries)
        {
            gin.Encode().CopyTo(data, offset);
            data[offset + 2] = kod;
            offset += 3;
        }

        return new Asdu(TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false, ca, GenericFun, 0, data);
    }

    /// <summary>
    /// 通用分类数据（TI=10）：数据 = RII + NGD + 数据集序列。
    /// COT 语义：40 主站写命令 / 42 读响应（有效）/ 43 读响应（无效）。
    /// </summary>
    public static Asdu GenericDataAsdu(int ca, byte rii, bool continuation, byte cot, IReadOnlyList<GenericDataSet> sets)
    {
        var total = 2;
        foreach (var set in sets)
        {
            total += set.EncodedSize;
        }

        var data = new byte[total];
        data[0] = rii;
        data[1] = NgdCodes.Encode(sets.Count, continuation);
        var offset = 2;
        foreach (var set in sets)
        {
            set.Encode().CopyTo(data, offset);
            offset += set.EncodedSize;
        }

        return new Asdu(TypeIds.GenericData, true, 1, cot, false, ca, GenericFun, 0, data);
    }

    /// <summary>通用分类写确认（TI=10，COT=40/41，带 GRC 回复码）。</summary>
    public static Asdu GenericWriteAck(int ca, byte rii, Gin gin, byte kod, byte grc, bool negative)
    {
        var set = GenericDataSet.ReplyCode(gin, kod, grc);
        var data = new byte[2 + set.EncodedSize];
        data[0] = rii;
        data[1] = NgdCodes.Encode(1, false);
        set.Encode().CopyTo(data, 2);
        var cot = negative ? CotCodes.GenericWriteNegative : CotCodes.GenericWrite;
        return new Asdu(TypeIds.GenericData, true, 1, cot, false, ca, GenericFun, 0, data);
    }

    // ═══════════════════════════════════════════════════════════════
    //  保护事件 / 一般命令 / 测量值
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 带相对时标和故障序号的监视方向信息（TI=2，专用范围 FUN 160-255）。
    /// 数据 = DPI/品质(1) + 相对时标 RET(2 LE ms) + 故障序号 FAN(2 LE) + CP56Time2a(7)。
    /// </summary>
    public static Asdu ProtectionEvent(int ca, byte fun, byte inf, DpiQuality dpi, int relativeTimeMs, int fan, Cp56Time2a time, byte cot)
    {
        var data = new byte[12];
        data[0] = dpi.Encode();
        data[1] = (byte)(relativeTimeMs & 0xFF);
        data[2] = (byte)((relativeTimeMs >> 8) & 0xFF);
        data[3] = (byte)(fan & 0xFF);
        data[4] = (byte)((fan >> 8) & 0xFF);
        time.Encode().AsSpan().CopyTo(data.AsSpan(5));
        return new Asdu(TypeIds.TimeTaggedMessagePrivate, true, 1, cot, false, ca, fun, inf, data);
    }

    /// <summary>
    /// 一般命令（TI=20，COT=20）：FUN/INF 指定目标，数据 = DCO+RII。
    /// DCO 按 DL/T 667：bit0-1 命令值 DCT（01 分 / 10 合 / 11 不确定），bit6-7 限定词 QU。
    /// </summary>
    public static Asdu GeneralCommand(int ca, byte fun, byte inf, int dco, byte rii)
        => new(TypeIds.GeneralCommand, true, 1, CotCodes.CommandAck, false, ca, fun, inf,
            // 原实现用 dco & 0x03 抹掉了 QU（bit6-7），使工厂与 DecodeGeneralCommand
            // 不对称、且 SendCommandAsync 无法发送带限定词的命令。DCO 须原样写入。
            new[] { (byte)dco, rii });

    /// <summary>测量值（TI=9，COT=2 循环）：FUN/INF 定位，数据 = 有符号 int16 LE。</summary>
    public static Asdu Measurement(int ca, byte fun, byte inf, short value)
        => new(TypeIds.Measurand, true, 1, CotCodes.Cyclic, false, ca, fun, inf,
            new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) });

    /// <summary>解码测量值（TI=9）数据。</summary>
    /// <exception cref="FormatException">数据区不足 2 字节。</exception>
    public static short DecodeMeasurement(Asdu asdu)
    {
        if (asdu.Data.Length < 2)
        {
            throw new FormatException($"测量值数据区不足 2 字节（{asdu.Data.Length}）");
        }

        return (short)(asdu.Data[0] | (asdu.Data[1] << 8));
    }

    /// <summary>解码一般命令 DCO：命令值 bit0-1（DCT）、QU bit6-7；RII 缺省 0。</summary>
    /// <exception cref="FormatException">数据区为空。</exception>
    public static (int Dct, int Qu, byte Rii) DecodeGeneralCommand(Asdu asdu)
    {
        if (asdu.Data.Length < 1)
        {
            throw new FormatException("一般命令数据区为空");
        }

        return (asdu.Data[0] & 0x03, asdu.Data[0] >> 6, asdu.Data.Length > 1 ? asdu.Data[1] : (byte)0);
    }

    /// <summary>解码保护事件（TI=2）的相对时标（ms）与故障序号。</summary>
    /// <exception cref="FormatException">数据区不足 5 字节。</exception>
    public static (int RelativeTimeMs, int Fan) DecodeProtectionEvent(Asdu asdu)
    {
        if (asdu.Data.Length < 5)
        {
            throw new FormatException($"保护事件数据区不足 5 字节（{asdu.Data.Length}）");
        }

        return (asdu.Data[1] | (asdu.Data[2] << 8), asdu.Data[3] | (asdu.Data[4] << 8));
    }
}
