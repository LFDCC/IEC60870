//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103) 第 7 章
//------------------------------------------------------------------------------

using System.Text;

namespace IEC60870.CS103;

/// <summary>
/// 103 ASDU 的公共头（TI/COT/CA/FUN/INF），解码结果都带着它。
/// </summary>
/// <remarks>
/// 103 的寻址是 ASDU 头里的 FUN/INF，信息体布局又随 TI 变化，所以只能<b>按 TI 解码整帧</b>；
/// 这个头就是让调用方拿到解码结果后仍然知道「这帧是谁、从哪来」。
/// </remarks>
/// <param name="TypeId">类型标识。</param>
/// <param name="Cot">传送原因（已去 P/N 位）。</param>
/// <param name="Negative">是否否定确认（COT bit7）。</param>
/// <param name="Ca">公共地址。</param>
/// <param name="Fun">功能类型。</param>
/// <param name="Inf">信息序号。</param>
public readonly record struct Iec103AsduHeader(byte TypeId, byte Cot, bool Negative, byte Ca, byte Fun, byte Inf)
{
    /// <summary>从零拷贝视图提取。</summary>
    public static Iec103AsduHeader From(in Iec103AsduView view)
        => new(view.TypeId, view.Cot, view.Negative, view.Ca, view.Fun, view.Inf);

    /// <summary>诊断描述。</summary>
    public string Describe()
        => $"TI={TypeId}({TypeIds.Describe(TypeId)}) COT={Cot}{(Negative ? "[NEG]" : string.Empty)} "
            + $"CA={Ca} FUN={Fun} INF={Inf}";
}

/// <summary>
/// 已解码 ASDU 的公共基类——标准类型与厂商私有类型都继承它。
/// </summary>
/// <remarks>
/// <para>
/// 好处是调用方只有一个入口类型：<c>switch (decoded)</c> 既能匹配标准类型，
/// 也能匹配私有类型，不需要先在 <c>object</c> 上做一次硬转。
/// </para>
/// <para>
/// <see cref="Header"/> 由解码器回填（<see cref="Iec103AsduDecoder"/> 或厂商自己的解析函数），
/// 因此各派生类型的构造函数里
/// 不需要再重复传一遍 TI/COT/CA/FUN/INF——发命令时也不必为它准备值。
/// </para>
/// </remarks>
public abstract record Iec103DecodedAsdu
{
    /// <summary>ASDU 公共头；由解码器回填，编码方向可忽略。</summary>
    public Iec103AsduHeader Header { get; set; }

    /// <summary>类型标识。</summary>
    public byte TypeId => Header.TypeId;

    /// <summary>传送原因（已去 P/N 位）。</summary>
    public byte Cot => Header.Cot;

    /// <summary>是否否定确认。</summary>
    public bool Negative => Header.Negative;

    /// <summary>公共地址。</summary>
    public byte Ca => Header.Ca;

    /// <summary>功能类型。</summary>
    public byte Fun => Header.Fun;

    /// <summary>信息序号。</summary>
    public byte Inf => Header.Inf;
}

/// <summary>多元素信息体的通用容器：一帧 N 个元素，元素类型由厂商定义。</summary>
/// <remarks>
/// 103 的很多类型是「一帧多个信息元素」（TI=40/41/43/50/36 等），元素本身是值类型，
/// 直接返回数组又不符合「解码结果必须是 <see cref="Iec103DecodedAsdu"/>」的约束。
/// 这个容器把数组包成解码对象，消费端 <c>is Iec103ElementList&lt;XujiSpStatus&gt; list</c> 即可，
/// 元素仍是结构体、不装箱。
/// </remarks>
/// <typeparam name="T">元素类型。</typeparam>
/// <param name="Elements">元素数组。</param>
public sealed record Iec103ElementList<T>(T[] Elements) : Iec103DecodedAsdu where T : struct
{
    /// <summary>元素个数。</summary>
    public int Count => Elements.Length;

    /// <summary>按下标取元素。</summary>
    /// <param name="index">下标。</param>
    public T this[int index] => Elements[index];
}

/// <summary>TI=1 的一个信息元素：DPI + 品质（+ 时标，+ 可选 4 字节附加信息）。</summary>
/// <param name="Inf">信息序号（与 ASDU 头一致；序列寻址时由装置定义）。</param>
/// <param name="Quality">双点信息 + 品质描述词。</param>
/// <param name="Time">CP56 时标；8 字节兼容布局没有时标，为 null。</param>
/// <param name="Extra">附加信息 4 字节（12 字节布局，兼容方向固定 FF）。</param>
public readonly record struct Iec103MonitorPoint(byte Inf, DpiQuality Quality, Cp56Time2a? Time, byte[] Extra);

/// <summary>TI=1：带时标监视方向信息（一帧可含多个元素）。</summary>
public sealed record Iec103MonitorMessage(IReadOnlyList<Iec103MonitorPoint> Points) : Iec103DecodedAsdu;

/// <summary>TI=2：带相对时标与故障序号的保护事件。</summary>
public sealed record Iec103ProtectionEventMessage(
    DpiQuality Quality, int RelativeTimeMs, int Fan, Cp56Time2a Time) : Iec103DecodedAsdu;

/// <summary>TI=5：标识报文（8 字节 ASCII）。</summary>
public sealed record Iec103IdentificationMessage(string Text, bool PrivateRange) : Iec103DecodedAsdu;

/// <summary>TI=6：时间同步。</summary>
public sealed record Iec103ClockSyncMessage(Cp56Time2a Time) : Iec103DecodedAsdu;

/// <summary>TI=7 / TI=8：总召唤启动 / 终止（信息体为扫描序号 SCN）。</summary>
public sealed record Iec103InterrogationMessage(byte Scn, bool IsEnd) : Iec103DecodedAsdu;

/// <summary>TI=9：测量值（int16 LE，一帧可含多个）。</summary>
public sealed record Iec103MeasurandMessage(short[] Values) : Iec103DecodedAsdu;

/// <summary>TI=10：通用分类数据（RII + NGD + 数据集序列）。</summary>
public sealed record Iec103GenericDataMessage(
    byte Rii, IReadOnlyList<GenericDataSet> Sets) : Iec103DecodedAsdu;

/// <summary>TI=20：一般命令（DCO + RII）。</summary>
public sealed record Iec103GeneralCommandMessage(int Dct, int Qu, byte Rii) : Iec103DecodedAsdu;

/// <summary>TI=23：被记录的扰动表。</summary>
public sealed record Iec103DisturbanceListMessage(
    IReadOnlyList<DisturbanceAsdu.DisturbanceEntry> Entries) : Iec103DecodedAsdu;

/// <summary>TI=30：通道扰动数据段。</summary>
public sealed record Iec103ChannelDataMessage(
    DisturbanceAsdu.ChannelDataSegment Segment) : Iec103DecodedAsdu;

/// <summary>
/// 标准 103 ASDU 的强类型解码器。
/// </summary>
/// <remarks>
/// <para>
/// 103 没有 IOA：寻址在 ASDU 头的 FUN/INF，信息体按 TI 解释成一段字节流，
/// 而且不同厂商还会复用同一个 TI 承载不同语义。所以正确姿势是<b>按 TI 整帧解码</b>，
/// 一次拿到该 TI 的全部字段。
/// </para>
/// <para>
/// 覆盖现场最常用的标准类型：TI=1/2/5/6/7/8/9/10/20/23/30。
/// 未覆盖的 TI 返回 false / null，由调用方走私有解析或未知兜底——
/// 与 <see cref="Iec103PrivateTypeRegistry"/> 登记的私有类型并行，互不干扰。
/// </para>
/// <para>
/// TI=23/30 复用 <see cref="DisturbanceAsdu"/> 的现成解码器，内部会物化一次；
/// 其余类型全部零拷贝直读。
/// </para>
/// </remarks>
public static class Iec103AsduDecoder
{
    /// <summary>解码一帧标准 ASDU。</summary>
    /// <param name="view">回调期有效的零拷贝视图。</param>
    /// <returns>解码结果；该 TI 不在覆盖范围内时返回 null。</returns>
    /// <exception cref="FormatException">帧结构不合法（长度不足、声明数目与实长不符）。</exception>
    public static Iec103DecodedAsdu? Decode(in Iec103AsduView view)
    {
        var header = Iec103AsduHeader.From(in view);

        return view.TypeId switch
        {
            TypeIds.TimeTaggedMessage => WithHeader(DecodeMonitor(in view), header),
            TypeIds.TimeTaggedMessagePrivate => WithHeader(DecodeProtection(in view), header),
            TypeIds.Identification => WithHeader(DecodeIdentification(in view), header),
            TypeIds.ClockSync => WithHeader(DecodeClockSync(in view), header),
            TypeIds.GeneralInterrogation => WithHeader(DecodeInterrogation(in view, isEnd: false), header),
            TypeIds.GeneralInterrogationEnd => WithHeader(DecodeInterrogation(in view, isEnd: true), header),
            TypeIds.Measurand => WithHeader(DecodeMeasurand(in view), header),
            TypeIds.GenericData => WithHeader(DecodeGenericData(in view), header),
            TypeIds.GeneralCommand => WithHeader(DecodeGeneralCommand(in view), header),
            TypeIds.DisturbanceList => WithHeader(DecodeDisturbanceList(in view), header),
            TypeIds.ChannelData => WithHeader(DecodeChannelData(in view), header),
            _ => null,
        };
    }

    /// <summary>尝试解码，不抛异常。</summary>
    /// <param name="view">回调期有效的零拷贝视图。</param>
    /// <param name="value">解码结果；未覆盖或解码失败时为 null。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否成功解码。</returns>
    public static bool TryDecode(in Iec103AsduView view, out Iec103DecodedAsdu? value, out string? error)
    {
        try
        {
            value = Decode(in view);
            error = value is null ? $"TI={view.TypeId} 不在标准解码器覆盖范围" : null;
            return value is not null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private static T WithHeader<T>(T value, Iec103AsduHeader header) where T : Iec103DecodedAsdu
    {
        value.Header = header;
        return value;
    }

    // ── TI=1 ───────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeMonitor(in Iec103AsduView view)
    {
        var data = view.Data;

        // 只有 6 字节 ASDU 头、数据区为空：没有任何信息元素可解。
        // 原实现让 ResolveElementSize 返回 0 后退化为 count=1，随后 data[0] 越界，
        // 且 TryDecode 的 catch 不覆盖 IndexOutOfRangeException，直接炸到业务线程。
        if (data.IsEmpty)
        {
            return new Iec103MonitorMessage(Array.Empty<Iec103MonitorPoint>());
        }

        var per = ResolveElementSize(data.Length, view.Count, 8, 12);
        var count = per == 0 ? 1 : data.Length / per;
        var points = new Iec103MonitorPoint[count];

        for (var i = 0; i < count; i++)
        {
            var offset = i * per;
            var quality = DpiQuality.Decode(data[offset]);

            // 12 字节布局：DIQ + 4 字节附加信息（兼容方向 FF）+ CP56；8 字节布局没有附加信息。
            Cp56Time2a? time = null;
            var extra = Array.Empty<byte>();

            if (per == 12)
            {
                extra = data.Slice(offset + 1, 4).ToArray();
                if (data.Length - (offset + 5) >= Cp56Time2a.EncodedLength)
                {
                    time = Cp56Time2a.Decode(data.Slice(offset + 5, Cp56Time2a.EncodedLength));
                }
            }
            else if (per == 8 && data.Length - (offset + 1) >= Cp56Time2a.EncodedLength)
            {
                time = Cp56Time2a.Decode(data.Slice(offset + 1, Cp56Time2a.EncodedLength));
            }

            points[i] = new Iec103MonitorPoint(view.Inf, quality, time, extra);
        }

        return new Iec103MonitorMessage(points);
    }

    // ── TI=2 ───────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeProtection(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < 12)
        {
            throw new FormatException($"TI=2 保护事件至少 12 字节，实际 {data.Length}");
        }

        return new Iec103ProtectionEventMessage(
            Quality: DpiQuality.Decode(data[0]),
            RelativeTimeMs: data[1] | (data[2] << 8),
            Fan: data[3] | (data[4] << 8),
            Time: Cp56Time2a.Decode(data.Slice(5, Cp56Time2a.EncodedLength)));
    }

    // ── TI=5 ───────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeIdentification(in Iec103AsduView view)
    {
        var length = Math.Min(8, view.DataLength);
        var text = Encoding.ASCII.GetString(view.Data[..length]).TrimEnd(' ', '\0');
        return new Iec103IdentificationMessage(text, PrivateRange: view.Inf == Asdu.InfPrivateIdent);
    }

    // ── TI=6 ───────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeClockSync(in Iec103AsduView view)
    {
        if (!view.TryGetCp56Time2a(0, out var time))
        {
            throw new FormatException($"TI=6 时间同步数据区不足 {Cp56Time2a.EncodedLength} 字节");
        }

        return new Iec103ClockSyncMessage(time);
    }

    // ── TI=7 / 8 ───────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeInterrogation(in Iec103AsduView view, bool isEnd)
        => new Iec103InterrogationMessage(view.DataLength >= 1 ? view.Data[0] : (byte)0, isEnd);

    // ── TI=9 ───────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeMeasurand(in Iec103AsduView view)
    {
        var data = view.Data;
        var values = new short[data.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
        }

        return new Iec103MeasurandMessage(values);
    }

    // ── TI=10 ──────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeGenericData(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < 2)
        {
            throw new FormatException($"TI=10 通用分类数据至少 2 字节，实际 {data.Length}");
        }

        var rii = data[0];
        var sets = new List<GenericDataSet>();
        var offset = 2; // RII(1) + NGD(1)

        while (offset < data.Length
               && GenericDataSet.TryDecode(data[offset..], out var set, out var consumed)
               && set is not null)
        {
            sets.Add(set);
            offset += consumed;
        }

        return new Iec103GenericDataMessage(rii, sets);
    }

    // ── TI=20 ──────────────────────────────────────────────────────
    private static Iec103DecodedAsdu DecodeGeneralCommand(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < 1)
        {
            throw new FormatException("TI=20 一般命令数据区为空");
        }

        return new Iec103GeneralCommandMessage(
            Dct: data[0] & 0x03,
            Qu: (data[0] >> 6) & 0x03,
            Rii: data.Length > 1 ? data[1] : (byte)0);
    }

    // ── TI=23 / 30（复用扰动解码器，需物化一次）──────────────────
    private static Iec103DecodedAsdu DecodeDisturbanceList(in Iec103AsduView view)
        => new Iec103DisturbanceListMessage(DisturbanceAsdu.DecodeDisturbanceList(Asdu.Materialize(in view)));

    private static Iec103DecodedAsdu DecodeChannelData(in Iec103AsduView view)
        => new Iec103ChannelDataMessage(DisturbanceAsdu.DecodeChannelData(Asdu.Materialize(in view)));

    /// <summary>
    /// 推断单个元素长度：优先按 VSQ 数目整除；整除不了再按单元素整帧长度判断；
    /// 都不成立返回 0（按 1 个元素处理）。
    /// </summary>
    private static int ResolveElementSize(int total, int declaredCount, params int[] candidates)
    {
        if (declaredCount > 0 && total % declaredCount == 0)
        {
            var per = total / declaredCount;
            if (Array.IndexOf(candidates, per) >= 0)
            {
                return per;
            }
        }

        foreach (var candidate in candidates)
        {
            if (total == candidate)
            {
                return candidate;
            }
        }

        return 0;
    }
}
