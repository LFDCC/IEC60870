//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  字节布局依据：《许继103通信规约》CBZ8000 变电站自动化系统，2005-12-11
//------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Text;
using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;


// ══════════════════════════════════════════════════════════════════
//  许继 103 扩展类型的强类型模型
// ══════════════════════════════════════════════════════════════════

/// <summary>遥信量状态（TI=40，表10）单个元素：SPI + 品质，无时标。</summary>
public readonly record struct XujiSpStatus(bool Spi, bool Invalid, bool NonTopical, bool Blocked, bool Substituted)
{
    /// <summary>按规约语义的状态文本（SPI：0 正常 / 1 异常）。</summary>
    public string State => Spi ? "异常" : "正常";
}

/// <summary>告警信号（TI=41，表22）单个元素：SPI 品质 + CP24 + SIN。</summary>
public readonly record struct XujiAlarmEvent(DpiQuality Quality, Cp32Time2a Time, byte Sin);

/// <summary>调压开关升降状态变位（TI=39，表17）：分接头位置 + 品质 + CP24。</summary>
public sealed record XujiTapChangerStatus(bool Transient, int TapPosition, DpiQuality Quality, Cp32Time2a Time) : Iec103DecodedAsdu;

/// <summary>断路器状态（TI=42，表18）：N 个 DPI + 末尾 RII。</summary>
public sealed record XujiBreakerStatus(DpiQuality[] Positions, byte Rii) : Iec103DecodedAsdu
{
    /// <summary>第一个位置（单断路器装置最常见的用法）。</summary>
    public DpiQuality First => Positions.Length > 0 ? Positions[0] : default;
}

/// <summary>断路器状态变位（TI=43，表16）单个元素：DPI + CP24。</summary>
public readonly record struct XujiBreakerChange(DpiQuality Quality, Cp32Time2a Time);

/// <summary>遥测值（TI=50，表12）单个元素：INF + int16 工程值。</summary>
public readonly record struct XujiMeasuredValue(byte Inf, short Value);

/// <summary>电度量（TI=36，表20）单个元素：4 字节读数 + IV/CA/CY/顺序号。</summary>
public readonly record struct XujiEnergyCounterValue(uint Reading, bool Invalid, bool Adjusted, bool Overflow, byte Sequence);

/// <summary>
/// 定值命令（TI=61，表26~28）。
/// 信息体 = 「CPU板地址|定值区号」(1) + 定值序号(1) [+ 定值 N×2B]。
/// </summary>
/// <remarks>
/// 规约把定值抽象为「定值数据(bit1-16) + 比例系数(bit17-18) + RES + 性质位(bit21) + RES + 符号位」，
/// 但表 26/28 的报文字面上每个定值只占 2 字节，本模型按报文取 <c>ushort</c> 原名值；
/// 比例系数/性质位/符号位的实际打包方式由装置信息表约定，故不做假设。
/// </remarks>
public sealed record XujiSettingCommand(byte CpuBoard, byte Zone, byte Index, ushort[] Values) : Iec103DecodedAsdu
{
    /// <summary>由规约的单字节「CPU板地址|定值区号」拆包（高半字节 CPU，低半字节定值区）。</summary>
    public static XujiSettingCommand FromPacked(byte packed, byte index, ushort[] values)
        => new((byte)(packed >> 4), (byte)(packed & 0x0F), index, values);
}

/// <summary>控制命令（TI=64/65/67，表15）：DCO + RII。</summary>
public sealed record XujiControlCommand(byte Dco, byte Rii) : Iec103DecodedAsdu
{
    /// <summary>bit0-1 命令值（断路器 1 跳 / 2 合；调压 1 降 / 2 升 / 3 急停）。</summary>
    public int CommandValue => Dco & 0x03;

    /// <summary>同期方式值（CCC 用 bit0-2：0 不允许 / 1 检同期 / 2 不检同期 / 3 检无压 / 4 合环）。</summary>
    public int SynchroCheckMode => Dco & 0x07;

    /// <summary>bit7-6 操作类型（0 执行 / 2 选择 / 3 撤消）。</summary>
    public int Operation => (Dco >> 6) & 0x03;

    /// <summary>操作类型文本。</summary>
    public string OperationText => Operation switch
    {
        0 => "执行",
        2 => "选择",
        3 => "撤消",
        _ => "未用",
    };
}

/// <summary>电度量冻结 / 数据结束（TI=88，表19）：FRZ + RII。</summary>
public sealed record XujiEnergyFreeze(byte Frz, byte Rii) : Iec103DecodedAsdu
{
    /// <summary>bit1-0 冻结/复位限定词（0 无 / 1 冻结不复位 / 2 冻结带复位 / 3 电度量复位）。</summary>
    public int FreezeMode => Frz & 0x03;

    /// <summary>bit7-2 电度量总请求（0 无要求 / 5 电度量总请求）。</summary>
    public int EnergyRequest => (Frz >> 2) & 0x3F;
}

/// <summary>故障类型字节（TI=70，表34）：bit6 TM、bit5-4 故障性质、bit3-0 故障相别。</summary>
public readonly record struct XujiFaultType(byte Raw)
{
    /// <summary>跳闸方式：false 三跳，true 选跳。</summary>
    public bool SelectiveTrip => (Raw & 0x40) != 0;

    /// <summary>故障性质（GT1|GT0）：00 不清楚 / 01 瞬时 / 10 永久 / 11 不清楚。</summary>
    public string Nature => ((Raw >> 4) & 0x03) switch
    {
        0b10 => "永久性故障",
        0b01 => "瞬时性故障",
        _ => "故障性质不清楚",
    };

    /// <summary>故障相别描述（bit0 N、bit1 A、bit2 B、bit3 C）。</summary>
    public string Phases
    {
        get
        {
            var sb = new StringBuilder(7);
            if ((Raw & 0x08) != 0) sb.Append('C');
            if ((Raw & 0x04) != 0) sb.Append('B');
            if ((Raw & 0x02) != 0) sb.Append('A');
            if (sb.Length == 0) return "故障相别不确定";
            if ((Raw & 0x01) != 0) sb.Append('N');
            return sb.ToString();
        }
    }

    /// <summary>编码：跳闸方式 + 故障性质 + 相别。</summary>
    public static XujiFaultType Create(bool selectiveTrip, int nature, int phaseBits)
        => new((byte)((selectiveTrip ? 0x40 : 0) | ((nature & 0x03) << 4) | (phaseBits & 0x0F)));
}

/// <summary>事故信号 / 故障报告（TI=70，表23 / 表34）单个信息对象。</summary>
public sealed record XujiFaultReport(
    DpiQuality Quality,
    int RelativeTimeMs,
    int Fan,
    Cp56Time2a Time,
    byte Sin,
    XujiFaultType FaultType,
    float[] Values) : Iec103DecodedAsdu;

/// <summary>TI=62 / 94：只带一个序号的命令与确认。</summary>
/// <param name="Value">故障报告序号（62）或信息记录段号（94）。</param>
public sealed record XujiSequenceNumber(byte Value) : Iec103DecodedAsdu;

/// <summary>TI=93：无信息体的结束帧。</summary>
public sealed record XujiEmptyPayload : Iec103DecodedAsdu;

/// <summary>自检与运行事件记录（TI=73/74，表37 / 表38）单个信息对象。</summary>
public sealed record XujiSelfCheckRecord(
    DpiQuality Quality,
    Cp56Time2a Time,
    float[] Values);

/// <summary>一帧自检记录 ASDU 的完整解码结果：N 个信息对象 + 末尾信息记录段号。</summary>
public sealed record XujiSelfCheckFrame(XujiSelfCheckRecord[] Records, byte Segment) : Iec103DecodedAsdu;


// ══════════════════════════════════════════════════════════════════
//  处理器基类
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// 许继扩展类型的编解码基类。
/// </summary>
/// <remarks>
/// 库里只登记「这个 TI 我认识」（<see cref="IIec103PrivateTypeHandler"/>），解析由本文件的
/// 各 <c>Parse</c> 方法负责，调用方按 <c>switch (asdu.TypeId)</c> 直接分派——
/// 103 没有 IOA，私有布局又全是厂商约定，没必要再绕一层注册表间接调用。
/// </remarks>
public abstract class XujiHandlerBase : IIec103PrivateTypeHandler
{
    /// <summary>构造处理器。</summary>
    /// <param name="typeId">许继扩展 TypeID。</param>
    protected XujiHandlerBase(byte typeId) => TypeId = typeId;

    /// <inheritdoc />
    public byte TypeId { get; }

    /// <inheritdoc />
    public string VendorTag => "Xuji";

    /// <inheritdoc />
    public byte? Fun => null;

    /// <inheritdoc />
    public byte? Inf => null;

    /// <inheritdoc />
    public abstract byte[] Encode(Iec103DecodedAsdu value);
}


// ══════════════════════════════════════════════════════════════════
//  TI=40 遥信量状态（表10，无时标）
// ══════════════════════════════════════════════════════════════════

/// <summary>TI=40：遥信量状态。信息体 = N × 1 字节品质（bit0 = SPI）。</summary>
public sealed class XujiSpStatusHandler : XujiHandlerBase
{
    /// <summary>单个元素长度。</summary>
    public const int ElementSize = 1;

    /// <summary>构造处理器。</summary>
    public XujiSpStatusHandler() : base(XujiTypeIds.SinglePointStatus) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        var result = new XujiSpStatus[data.Length / ElementSize];
        for (var i = 0; i < result.Length; i++)
        {
            var q = DpiQuality.Decode(data[i]);

            // SPI 只占 bit0；单点信息里 bit1 应为 0，按 bit0 判定可避免把噪声位当状态。
            result[i] = new XujiSpStatus((data[i] & 0x01) != 0, q.Invalid, q.NonTopical, q.Blocked, q.Substituted);
        }

        return new Iec103ElementList<XujiSpStatus>(result);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var items = ((Iec103ElementList<XujiSpStatus>)value).Elements;
        var data = new byte[items.Length * ElementSize];
        for (var i = 0; i < items.Length; i++)
        {
            var it = items[i];
            data[i] = new DpiQuality(
                Dpi: it.Spi ? 1 : 0,
                Invalid: it.Invalid,
                NonTopical: it.NonTopical,
                Blocked: it.Blocked,
                Substituted: it.Substituted).Encode();
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=41 告警信号（表22）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=41：告警信号（测控装置）。
/// 信息体 = N × [品质(1，bit0=SPI) + CP24(4) + SIN(1)]，共 6 字节/元素。
/// </summary>
public sealed class XujiAlarmHandler : XujiHandlerBase
{
    /// <summary>单个元素长度。</summary>
    public const int ElementSize = 1 + Cp32Time2a.EncodedLength + 1;

    /// <summary>构造处理器。</summary>
    public XujiAlarmHandler() : base(XujiTypeIds.AlarmSignal) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        var result = new XujiAlarmEvent[data.Length / ElementSize];
        for (var i = 0; i < result.Length; i++)
        {
            var off = i * ElementSize;
            result[i] = new XujiAlarmEvent(
                DpiQuality.Decode(data[off]),
                Cp32Time2a.Decode(data.Slice(off + 1, Cp32Time2a.EncodedLength)),
                data[off + ElementSize - 1]);
        }

        return new Iec103ElementList<XujiAlarmEvent>(result);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var items = ((Iec103ElementList<XujiAlarmEvent>)value).Elements;
        var data = new byte[items.Length * ElementSize];
        for (var i = 0; i < items.Length; i++)
        {
            var off = i * ElementSize;
            data[off] = items[i].Quality.Encode();
            items[i].Time.EncodeTo(data.AsSpan(off + 1));
            data[off + ElementSize - 1] = items[i].Sin;
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=39 调压开关升降状态（表17）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=39：调压开关升降状态变位。
/// 信息体 = SQ=1 单对象：[T|分接头位置(1) + 品质(1，bit0=OV) + CP24(4)]，共 6 字节。
/// </summary>
public sealed class XujiTapChangerStatusHandler : XujiHandlerBase
{
    /// <summary>信息体长度。</summary>
    public const int PayloadSize = 1 + 1 + Cp32Time2a.EncodedLength;

    /// <summary>构造处理器。</summary>
    public XujiTapChangerStatusHandler() : base(XujiTypeIds.TapChangerStatus) { }

    /// <inheritdoc />

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < PayloadSize)
        {
            throw new FormatException($"TI=39 调压开关状态需要 {PayloadSize} 字节，实际 {data.Length}");
        }

        return new XujiTapChangerStatus(
            Transient: (data[0] & 0x80) != 0,
            TapPosition: data[0] & 0x7F,
            Quality: DpiQuality.Decode(data[1]),
            Time: Cp32Time2a.Decode(data.Slice(2, Cp32Time2a.EncodedLength)));
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var it = (XujiTapChangerStatus)value;
        var data = new byte[PayloadSize];
        data[0] = (byte)((it.Transient ? 0x80 : 0) | (it.TapPosition & 0x7F));
        data[1] = it.Quality.Encode();
        it.Time.EncodeTo(data.AsSpan(2));
        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=42 断路器状态（表18）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=42：断路器状态（SQ=0，逐对象 DPI，<b>末尾</b>带 1 字节 RII）。
/// 信息体 = N × 品质(1) + RII(1)。
/// </summary>
public sealed class XujiBreakerStatusHandler : XujiHandlerBase
{
    /// <summary>构造处理器。</summary>
    public XujiBreakerStatusHandler() : base(XujiTypeIds.BreakerStatus) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < 1)
        {
            throw new FormatException("TI=42 断路器状态信息体不足 1 字节");
        }

        var count = data.Length - 1;
        var positions = new DpiQuality[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = DpiQuality.Decode(data[i]);
        }

        return new XujiBreakerStatus(positions, data[^1]);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var it = (XujiBreakerStatus)value;
        var data = new byte[it.Positions.Length + 1];
        for (var i = 0; i < it.Positions.Length; i++)
        {
            data[i] = it.Positions[i].Encode();
        }

        data[^1] = it.Rii;
        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=43 断路器状态变位（表16）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=43：断路器状态变位（监视方向）。
/// 信息体 = N × [品质(1) + CP24(4)] + RII(1)。
/// </summary>
public sealed class XujiBreakerChangeHandler : XujiHandlerBase
{
    /// <summary>单个事件元素长度。</summary>
    public const int ElementSize = 1 + Cp32Time2a.EncodedLength;

    /// <summary>构造处理器。</summary>
    public XujiBreakerChangeHandler() : base(XujiTypeIds.BreakerStatusChange) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < ElementSize)
        {
            throw new FormatException($"TI=43 变位信息至少 {ElementSize} 字节，实际 {data.Length}");
        }

        var count = (data.Length - 1) / ElementSize;
        var result = new XujiBreakerChange[count];
        for (var i = 0; i < count; i++)
        {
            var off = i * ElementSize;
            result[i] = new XujiBreakerChange(
                DpiQuality.Decode(data[off]),
                Cp32Time2a.Decode(data.Slice(off + 1, Cp32Time2a.EncodedLength)));
        }

        return new Iec103ElementList<XujiBreakerChange>(result);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var items = ((Iec103ElementList<XujiBreakerChange>)value).Elements;
        var data = new byte[items.Length * ElementSize + 1];
        for (var i = 0; i < items.Length; i++)
        {
            var off = i * ElementSize;
            data[off] = items[i].Quality.Encode();
            items[i].Time.EncodeTo(data.AsSpan(off + 1));
        }

        data[^1] = 0; // RII：自发上送时为 0
        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=50 遥测值（表12）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=50：遥测值（SQ=0，<b>逐元素带 INF</b>）。
/// 信息体 = N × [INF(1) + 被测值 int16 LE(2)]，共 3 字节/元素。
/// </summary>
/// <remarks>
/// 与 TI=9 的区别：TI=9 的 INF 在 ASDU 头里、信息体只有数值；TI=50 的 INF 在<b>每个元素</b>里，
/// 用于「越限主动上送」（COT=1）时一帧混装多个不同的点。
/// </remarks>
public sealed class XujiMeasuredValueHandler : XujiHandlerBase
{
    /// <summary>单个元素长度。</summary>
    public const int ElementSize = 1 + 2;

    /// <summary>构造处理器。</summary>
    public XujiMeasuredValueHandler() : base(XujiTypeIds.MeasuredValueWithInf) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        var result = new XujiMeasuredValue[data.Length / ElementSize];
        for (var i = 0; i < result.Length; i++)
        {
            var off = i * ElementSize;
            result[i] = new XujiMeasuredValue(
                data[off],
                BinaryPrimitives.ReadInt16LittleEndian(data.Slice(off + 1, 2)));
        }

        return new Iec103ElementList<XujiMeasuredValue>(result);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var items = ((Iec103ElementList<XujiMeasuredValue>)value).Elements;
        var data = new byte[items.Length * ElementSize];
        for (var i = 0; i < items.Length; i++)
        {
            var off = i * ElementSize;
            data[off] = items[i].Inf;
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(off + 1, 2), items[i].Value);
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=36 电度量（表20）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=36：电度量数据（COT=2）。
/// 信息体 = N × [二进制读数 4 字节 LE + IV|CA|CY|顺序号(1)]，共 5 字节/元素。
/// </summary>
public sealed class XujiEnergyHandler : XujiHandlerBase
{
    /// <summary>单个元素长度。</summary>
    public const int ElementSize = 4 + 1;

    /// <summary>构造处理器。</summary>
    public XujiEnergyHandler() : base(XujiTypeIds.EnergyCounter) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        var result = new XujiEnergyCounterValue[data.Length / ElementSize];
        for (var i = 0; i < result.Length; i++)
        {
            var off = i * ElementSize;
            var flags = data[off + 4];
            result[i] = new XujiEnergyCounterValue(
                BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off, 4)),
                Invalid: (flags & 0x80) != 0,
                Adjusted: (flags & 0x40) != 0,
                Overflow: (flags & 0x20) != 0,
                Sequence: (byte)(flags & 0x1F));
        }

        return new Iec103ElementList<XujiEnergyCounterValue>(result);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var items = ((Iec103ElementList<XujiEnergyCounterValue>)value).Elements;
        var data = new byte[items.Length * ElementSize];
        for (var i = 0; i < items.Length; i++)
        {
            var off = i * ElementSize;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off, 4), items[i].Reading);
            byte flags = (byte)(items[i].Sequence & 0x1F);
            if (items[i].Invalid) flags |= 0x80;
            if (items[i].Adjusted) flags |= 0x40;
            if (items[i].Overflow) flags |= 0x20;
            data[off + 4] = flags;
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=61 定值命令（表26~28）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=61：调定值 / 定值修改预发·返校·执行·撤销（表26~28）。
/// 信息体 = 「CPU板地址(高半字节)|定值区号(低半字节)」(1) + 定值序号(1) [+ 定值 N×2B]。
/// 子命令由 <b>INF</b> 区分（见 <see cref="XujiSettingInf"/>），与信息体无关。
/// </summary>
public sealed class XujiSettingHandler : XujiHandlerBase
{
    /// <summary>信息体固定头长度（不含定值数组）。</summary>
    public const int HeaderSize = 2;

    /// <summary>构造处理器。</summary>
    public XujiSettingHandler() : base(XujiTypeIds.SettingCommand) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < HeaderSize)
        {
            throw new FormatException($"TI=61 定值命令至少 {HeaderSize} 字节，实际 {data.Length}");
        }

        var values = new ushort[(data.Length - HeaderSize) / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(HeaderSize + i * 2, 2));
        }

        return XujiSettingCommand.FromPacked(data[0], data[1], values);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var cmd = (XujiSettingCommand)value;
        var data = new byte[HeaderSize + cmd.Values.Length * 2];
        data[0] = (byte)(((cmd.CpuBoard & 0x0F) << 4) | (cmd.Zone & 0x0F));
        data[1] = cmd.Index;
        for (var i = 0; i < cmd.Values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(HeaderSize + i * 2, 2), cmd.Values[i]);
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=64/65/67 控制命令（表15）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=64/65/67：断路器控制 / 调压开关升降 / 同期方式选择（表15）。
/// 信息体 = DCO(1) + RII(1)。
/// </summary>
/// <remarks>
/// 许继的 DCO 与 101/104 的不同：<c>bit7-6 = 操作类型</c>（0 执行 / 2 选择 / 3 撤消），
/// <c>bit1-0 = 命令值</c>，必须原样保留整字节，不能用 <c>dco &amp; 0x03</c> 抹掉高位。
/// </remarks>
public sealed class XujiControlHandler : XujiHandlerBase
{
    /// <summary>信息体长度。</summary>
    public const int PayloadSize = 2;

    /// <summary>用指定 TypeID 构造处理器。</summary>
    /// <param name="typeId">64 / 65 / 67。</param>
    public XujiControlHandler(byte typeId) : base(typeId) { }

    /// <inheritdoc />

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < PayloadSize)
        {
            throw new FormatException($"TI={view.TypeId} 控制命令需要 {PayloadSize} 字节，实际 {data.Length}");
        }

        return new XujiControlCommand(data[0], data[1]);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var cmd = (XujiControlCommand)value;
        return new[] { cmd.Dco, cmd.Rii };
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=70 事故信号 / 故障报告（表23 / 表34）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=70：事故信号 / 故障报告。
/// 每个信息对象 = 品质(1) + 相对时间 RET(2 LE ms) + 故障序号 FAN(2 LE)
/// + CP56(7) + SIN(1) + 故障类型(1) + 附带结果(N-1) × 4B(R32.23 浮点)。
/// </summary>
public sealed class XujiFaultReportHandler : XujiHandlerBase
{
    /// <summary>固定头长度（品质 + RET + FAN + CP56 + SIN + 故障类型）。</summary>
    public const int HeaderSize = 1 + 2 + 2 + Cp56Time2a.EncodedLength + 1 + 1;

    /// <summary>构造处理器。</summary>
    public XujiFaultReportHandler() : base(XujiTypeIds.FaultReport) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < HeaderSize)
        {
            throw new FormatException($"TI=70 事故信号至少 {HeaderSize} 字节，实际 {data.Length}");
        }

        var values = new float[(data.Length - HeaderSize) / 4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(HeaderSize + i * 4, 4));
        }

        return new XujiFaultReport(
            Quality: DpiQuality.Decode(data[0]),
            RelativeTimeMs: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1, 2)),
            Fan: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(3, 2)),
            Time: Cp56Time2a.Decode(data.Slice(5, Cp56Time2a.EncodedLength)),
            Sin: data[5 + Cp56Time2a.EncodedLength],
            FaultType: new XujiFaultType(data[HeaderSize - 1]),
            Values: values);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var it = (XujiFaultReport)value;
        var data = new byte[HeaderSize + it.Values.Length * 4];
        data[0] = it.Quality.Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1, 2), (ushort)Math.Clamp(it.RelativeTimeMs, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3, 2), (ushort)Math.Clamp(it.Fan, 0, ushort.MaxValue));
        it.Time.EncodeTo(data.AsSpan(5));
        data[5 + Cp56Time2a.EncodedLength] = it.Sin;
        data[HeaderSize - 1] = it.FaultType.Raw;
        for (var i = 0; i < it.Values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(HeaderSize + i * 4, 4), it.Values[i]);
        }

        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=73/74 自检与运行事件记录（表37 / 表38）
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// TI=73/74：自检与运行事件信息记录（COT=64）。
/// 每个信息对象 = 品质(1) + CP56(7) + 附带结果数目 M(1) + M×4B(R32.23 浮点)；
/// 整帧末尾额外带 1 字节「信息记录段号」。
/// </summary>
/// <remarks>
/// 73 为继电保护装置（品质用 DPI），74 为测控装置（品质用 SPI）。
/// 每个对象的 M 可以不同，故本类型只能整帧变长解析，不能按固定元素长度切分。
/// </remarks>
public sealed class XujiSelfCheckRecordHandler : XujiHandlerBase
{
    /// <summary>单个对象固定头长度（品质 + CP56 + 附带结果数目）。</summary>
    public const int HeaderSize = 1 + Cp56Time2a.EncodedLength + 1;

    /// <summary>用指定 TypeID 构造处理器。</summary>
    /// <param name="typeId">73 / 74。</param>
    public XujiSelfCheckRecordHandler(byte typeId) : base(typeId) { }

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < HeaderSize + 1)
        {
            throw new FormatException($"TI={view.TypeId} 自检记录至少 {HeaderSize + 1} 字节，实际 {data.Length}");
        }

        // 末字节是信息记录段号，先扣掉
        var body = data[..^1];
        var singlePoint = view.TypeId == XujiTypeIds.SelfCheckRecordControl;

        var records = new List<XujiSelfCheckRecord>();
        var offset = 0;
        while (offset + HeaderSize <= body.Length)
        {
            var quality = DpiQuality.Decode(body[offset]);
            var time = Cp56Time2a.Decode(body.Slice(offset + 1, Cp56Time2a.EncodedLength));
            var count = body[offset + HeaderSize - 1];
            var valuesOffset = offset + HeaderSize;
            var valuesBytes = count * 4;
            if (valuesOffset + valuesBytes > body.Length)
            {
                throw new FormatException($"TI={view.TypeId} 自检记录附带结果数目 {count} 超出数据区");
            }

            var values = new float[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(valuesOffset + i * 4, 4));
            }

            records.Add(new XujiSelfCheckRecord(quality, time, values));
            offset = valuesOffset + valuesBytes;
        }

        return new XujiSelfCheckFrame(records.ToArray(), data[^1]);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var frame = (XujiSelfCheckFrame)value;
        var singlePoint = TypeId == XujiTypeIds.SelfCheckRecordControl;

        var total = 1;
        foreach (var rec in frame.Records)
        {
            total += HeaderSize + rec.Values.Length * 4;
        }

        var data = new byte[total];
        var offset = 0;
        foreach (var rec in frame.Records)
        {
            if ((uint)rec.Values.Length > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "附带结果数目超过 255，需拆帧");
            }

            data[offset] = rec.Quality.Encode();
            rec.Time.EncodeTo(data.AsSpan(offset + 1));
            data[offset + HeaderSize - 1] = (byte)rec.Values.Length;
            for (var i = 0; i < rec.Values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + HeaderSize + i * 4, 4), rec.Values[i]);
            }

            offset += HeaderSize + rec.Values.Length * 4;
        }

        data[^1] = frame.Segment;
        return data;
    }
}


// ══════════════════════════════════════════════════════════════════
//  TI=88 冻结 / 62 故障报告 / 94 调记录 / 93 结束帧
// ══════════════════════════════════════════════════════════════════

/// <summary>TI=88：电度量冻结 / 冻结确认 / 电度量数据结束（表19）。信息体 = FRZ(1) + RII(1)。</summary>
public sealed class XujiEnergyFreezeHandler : XujiHandlerBase
{
    /// <summary>信息体长度。</summary>
    public const int PayloadSize = 2;

    /// <summary>构造处理器。</summary>
    public XujiEnergyFreezeHandler() : base(XujiTypeIds.EnergyFreeze) { }

    /// <inheritdoc />

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
    {
        var data = view.Data;
        if (data.Length < PayloadSize)
        {
            throw new FormatException($"TI=88 电度量冻结需要 {PayloadSize} 字节，实际 {data.Length}");
        }

        return new XujiEnergyFreeze(data[0], data[1]);
    }

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value)
    {
        var it = (XujiEnergyFreeze)value;
        return new[] { it.Frz, it.Rii };
    }
}

/// <summary>
/// TI=62 / 94：只带 1 字节序号的命令与确认。
/// 62 = 故障报告序号（0 最新 / 1 前一次 / …）；94 = 信息记录段号（0 全部，1.. 分段）。
/// </summary>
public sealed class XujiSequenceNumberHandler : XujiHandlerBase
{
    /// <summary>信息体长度。</summary>
    public const int PayloadSize = 1;

    /// <summary>用指定 TypeID 构造处理器。</summary>
    /// <param name="typeId">62 / 94。</param>
    public XujiSequenceNumberHandler(byte typeId) : base(typeId) { }

    /// <inheritdoc />

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view)
        => new XujiSequenceNumber(view.DataLength >= 1 ? view.Data[0] : (byte)0);

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value) => new[] { ((XujiSequenceNumber)value).Value };
}

/// <summary>TI=93：调遥测值的否定确认 / 遥测值结束（表14），无信息体。</summary>
public sealed class XujiEmptyPayloadHandler : XujiHandlerBase
{
    /// <summary>构造处理器。</summary>
    public XujiEmptyPayloadHandler() : base(XujiTypeIds.MeasuredValueAck) { }

    /// <inheritdoc />

    /// <inheritdoc />
    public static Iec103DecodedAsdu Parse(in Iec103AsduView view) => new XujiEmptyPayload();

    /// <inheritdoc />
    public override byte[] Encode(Iec103DecodedAsdu value) => Array.Empty<byte>();
}


// ══════════════════════════════════════════════════════════════════
//  集中注册与组帧入口
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// 许继 103 扩展类型的集中入口：注册 + 解析 + 组帧。
/// </summary>
/// <remarks>
/// <para>用法：<c>XujiPrivateTypes.RegisterAll(client.PrivateTypes);</c>，
/// 然后在 <c>AsduReceived</c> 里调 <c>XujiPrivateTypes.Decode(in asdu)</c>。</para>
/// <para>
/// 注册的作用是「告诉库这个 TI 我认识」：<b>解析由本文件的 switch 负责</b>，
/// 注册只影响未识别兜底（未注册的非标准 TI 才会走 <c>UnknownAsduReceived</c>）。
/// 这样现场出现规约未列的类型时报文依然可读，<b>绝不丢帧</b>。
/// </para>
/// <para>
/// FUN/INF 的具体取值由各装置信息表定义（规约原文「INF 见装置的相应信息表」），
/// 因此这里只按 TypeID 注册，不绑定 FUN/INF。需要精确分派时用
/// <c>RegisterFunInf</c> / <c>RegisterFunRange</c> 追加。
/// </para>
/// </remarks>
public static class XujiPrivateTypes
{
    /// <summary>
    /// 按 TypeID 解析一帧许继扩展 ASDU；不是许继的 TI 返回 null。
    /// </summary>
    /// <remarks>
    /// 私有类型解析归业务代码：这里就是一个 <c>switch</c>，不经过注册表。
    /// 典型用法是「先问许继，再问库的标准解码器」：
    /// <code>
    /// var decoded = XujiPrivateTypes.Decode(in asdu) ?? Iec103AsduDecoder.Decode(in asdu);
    /// </code>
    /// </remarks>
    /// <param name="view">回调期有效的 ASDU 视图。</param>
    public static Iec103DecodedAsdu? Decode(in Iec103AsduView view) => view.TypeId switch
    {
        XujiTypeIds.EnergyCounter => XujiEnergyHandler.Parse(in view),                 // 36
        XujiTypeIds.TapChangerStatus => XujiTapChangerStatusHandler.Parse(in view),    // 39
        XujiTypeIds.SinglePointStatus => XujiSpStatusHandler.Parse(in view),           // 40
        XujiTypeIds.AlarmSignal => XujiAlarmHandler.Parse(in view),                    // 41
        XujiTypeIds.BreakerStatus => XujiBreakerStatusHandler.Parse(in view),          // 42
        XujiTypeIds.BreakerStatusChange => XujiBreakerChangeHandler.Parse(in view),    // 43
        XujiTypeIds.MeasuredValueWithInf => XujiMeasuredValueHandler.Parse(in view),   // 50
        XujiTypeIds.SettingCommand => XujiSettingHandler.Parse(in view),               // 61
        XujiTypeIds.FaultReportAck => XujiSequenceNumberHandler.Parse(in view),        // 62
        XujiTypeIds.BreakerControl
            or XujiTypeIds.TapChangerControl
            or XujiTypeIds.SynchroCheckControl => XujiControlHandler.Parse(in view),   // 64/65/67
        XujiTypeIds.FaultReport => XujiFaultReportHandler.Parse(in view),              // 70
        XujiTypeIds.SelfCheckRecordProtection
            or XujiTypeIds.SelfCheckRecordControl => XujiSelfCheckRecordHandler.Parse(in view), // 73/74
        XujiTypeIds.EnergyFreeze => XujiEnergyFreezeHandler.Parse(in view),            // 88
        XujiTypeIds.MeasuredValueAck => XujiEmptyPayloadHandler.Parse(in view),        // 93
        XujiTypeIds.SelfCheckCommand => XujiSequenceNumberHandler.Parse(in view),      // 94
        _ => null,
    };

    /// <summary>把许继全部扩展类型与附加 COT 语义登记到指定注册表。</summary>
    /// <param name="registry">目标注册表（通常为 client.PrivateTypes / server.PrivateTypes）。</param>
    public static void RegisterAll(Iec103PrivateTypeRegistry registry)
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        // ── 监视方向（上行）──
        registry.RegisterType(new XujiEnergyHandler());                    // 36
        registry.RegisterType(new XujiTapChangerStatusHandler());          // 39
        registry.RegisterType(new XujiSpStatusHandler());                  // 40
        registry.RegisterType(new XujiAlarmHandler());                     // 41
        registry.RegisterType(new XujiBreakerStatusHandler());             // 42
        registry.RegisterType(new XujiBreakerChangeHandler());             // 43
        registry.RegisterType(new XujiMeasuredValueHandler());             // 50
        registry.RegisterType(new XujiFaultReportHandler());               // 70
        registry.RegisterType(new XujiSelfCheckRecordHandler(XujiTypeIds.SelfCheckRecordProtection)); // 73
        registry.RegisterType(new XujiSelfCheckRecordHandler(XujiTypeIds.SelfCheckRecordControl));    // 74

        // ── 双向（命令与回送确认共用同一 TI）──
        registry.RegisterType(new XujiSettingHandler());                                   // 61
        registry.RegisterType(new XujiSequenceNumberHandler(XujiTypeIds.FaultReportAck));  // 62
        registry.RegisterType(new XujiControlHandler(XujiTypeIds.BreakerControl));         // 64
        registry.RegisterType(new XujiControlHandler(XujiTypeIds.TapChangerControl));      // 65
        registry.RegisterType(new XujiControlHandler(XujiTypeIds.SynchroCheckControl));    // 67
        registry.RegisterType(new XujiEnergyFreezeHandler());                              // 88
        registry.RegisterType(new XujiEmptyPayloadHandler());                              // 93
        registry.RegisterType(new XujiSequenceNumberHandler(XujiTypeIds.SelfCheckCommand)); // 94

        // ── 附加 COT 语义（仅影响描述，不改变分派）──
        registry.RegisterCot(XujiCot.ControlCommand, "控制命令");
        registry.RegisterCot(XujiCot.ReportCall, "报告召唤");
        registry.RegisterCot(XujiCot.EnergyFreezeNegative, "电度量冻结否定确认");
    }

    /// <summary>按 TypeID 构造对应的编解码器。</summary>
    /// <param name="typeId">许继扩展 TypeID。</param>
    /// <exception cref="ArgumentOutOfRangeException">该 TypeID 未实现。</exception>
    public static IIec103PrivateTypeHandler CreateHandler(byte typeId) => typeId switch
    {
        XujiTypeIds.EnergyCounter => new XujiEnergyHandler(),
        XujiTypeIds.TapChangerStatus => new XujiTapChangerStatusHandler(),
        XujiTypeIds.SinglePointStatus => new XujiSpStatusHandler(),
        XujiTypeIds.AlarmSignal => new XujiAlarmHandler(),
        XujiTypeIds.BreakerStatus => new XujiBreakerStatusHandler(),
        XujiTypeIds.BreakerStatusChange => new XujiBreakerChangeHandler(),
        XujiTypeIds.MeasuredValueWithInf => new XujiMeasuredValueHandler(),
        XujiTypeIds.SettingCommand => new XujiSettingHandler(),
        XujiTypeIds.FaultReportAck => new XujiSequenceNumberHandler(typeId),
        XujiTypeIds.BreakerControl or XujiTypeIds.TapChangerControl or XujiTypeIds.SynchroCheckControl
            => new XujiControlHandler(typeId),
        XujiTypeIds.FaultReport => new XujiFaultReportHandler(),
        XujiTypeIds.SelfCheckRecordProtection or XujiTypeIds.SelfCheckRecordControl
            => new XujiSelfCheckRecordHandler(typeId),
        XujiTypeIds.EnergyFreeze => new XujiEnergyFreezeHandler(),
        XujiTypeIds.MeasuredValueAck => new XujiEmptyPayloadHandler(),
        XujiTypeIds.SelfCheckCommand => new XujiSequenceNumberHandler(typeId),
        _ => throw new ArgumentOutOfRangeException(nameof(typeId), $"未实现的许继 TypeID={typeId}"),
    };

    /// <summary>
    /// 组一帧许继扩展 ASDU。
    /// </summary>
    /// <param name="typeId">许继扩展 TypeID。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="fun">功能类型（由装置信息表定义）。</param>
    /// <param name="inf">信息序号（由装置信息表定义；TI=61 时见 <see cref="XujiSettingInf"/>）。</param>
    /// <param name="value">强类型值，须与 TI 匹配。</param>
    /// <param name="cot">传送原因；null 时按 TI 取规约默认值。</param>
    /// <param name="sq">序列标志。许继这套类型均为 SQ=0（TI=39/42 的 SQ=1 由厂商自行约定）。</param>
    /// <param name="count">VSQ 数目，默认 1。</param>
    public static Asdu CreateAsdu(
        byte typeId,
        byte ca,
        byte fun,
        byte inf,
        Iec103DecodedAsdu value,
        byte? cot = null,
        bool sq = false,
        int count = 1)
    {
        var handler = CreateHandler(typeId);
        var payload = handler.Encode(value);
        return new Asdu(typeId, sq, count, cot ?? DefaultCot(typeId), false, ca, fun, inf, payload);
    }

    /// <summary>取该 TypeID 的默认传送原因（规约表格中的「传输原因」列）。</summary>
    /// <param name="typeId">许继扩展 TypeID。</param>
    public static byte DefaultCot(byte typeId) => typeId switch
    {
        XujiTypeIds.TapChangerStatus => CotCodes.Spontaneous,
        XujiTypeIds.BreakerStatusChange => CotCodes.Spontaneous,
        XujiTypeIds.AlarmSignal => CotCodes.Spontaneous,
        XujiTypeIds.SinglePointStatus or XujiTypeIds.BreakerStatus => CotCodes.GiStart,
        XujiTypeIds.MeasuredValueWithInf or XujiTypeIds.EnergyCounter => XujiCot.EnergyData,
        XujiTypeIds.SettingCommand => XujiCot.Positive,
        XujiTypeIds.FaultReportAck or XujiTypeIds.SelfCheckCommand => XujiCot.Positive,
        XujiTypeIds.BreakerControl or XujiTypeIds.TapChangerControl or XujiTypeIds.SynchroCheckControl
            => XujiCot.ControlCommand,
        XujiTypeIds.FaultReport or XujiTypeIds.SelfCheckRecordProtection or XujiTypeIds.SelfCheckRecordControl
            => XujiCot.ReportCall,
        _ => CotCodes.Spontaneous,
    };
}

/// <summary>TI=61 定值命令的 INF 取值（表26~28）。</summary>
public static class XujiSettingInf
{
    /// <summary>调定值（读）。</summary>
    public const byte Read = 100;

    /// <summary>全部定值修改预发。</summary>
    public const byte PreSet = 101;

    /// <summary>全部定值修改返校。</summary>
    public const byte Verify = 102;

    /// <summary>全部定值修改执行。</summary>
    public const byte Execute = 103;

    /// <summary>全部定值修改执行确认。</summary>
    public const byte ExecuteAck = 104;

    /// <summary>全部定值修改撤销。</summary>
    public const byte Cancel = 105;

    /// <summary>全部定值修改撤销确认。</summary>
    public const byte CancelAck = 106;

    /// <summary>该 INF 是否为「主站下发」方向（其余为装置应答）。</summary>
    public static bool IsRequest(byte inf) => inf is Read or PreSet or Execute or Cancel;
}
