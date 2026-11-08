//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 扰动数据传输服务（ASDU 23/24/25/26/27/29/30/31）
//------------------------------------------------------------------------------

using System.Buffers.Binary;

namespace IEC60870.CS103;

/// <summary>扰动数据传输命令 TOO。</summary>
public static class TooCodes
{
    /// <summary>选择故障。</summary>
    public const byte SelectFault = 1;

    /// <summary>请求扰动数据。</summary>
    public const byte RequestDisturbanceData = 2;

    /// <summary>中止扰动数据。</summary>
    public const byte AbortDisturbanceData = 3;

    /// <summary>请求通道（ACC 指定通道）。</summary>
    public const byte RequestChannel = 8;

    /// <summary>中止通道。</summary>
    public const byte AbortChannel = 9;

    /// <summary>请求标签。</summary>
    public const byte RequestTags = 16;

    /// <summary>中止标签。</summary>
    public const byte AbortTags = 17;

    /// <summary>请求被记录的扰动表。</summary>
    public const byte RequestDisturbanceList = 24;

    /// <summary>扰动数据传输结束（无中止）。</summary>
    public const byte EndDisturbance = 32;

    /// <summary>通道传输结束（无中止）。</summary>
    public const byte EndChannel = 35;

    /// <summary>标签传输结束（无中止）。</summary>
    public const byte EndTags = 38;

    /// <summary>扰动数据传输成功（主站认可）。</summary>
    public const byte PositiveAck = 64;

    /// <summary>扰动数据传输失败（主站认可）。</summary>
    public const byte NegativeAck = 65;

    /// <summary>TOO 语义描述（含 +1/+2 的中止细分）。</summary>
    public static string Describe(byte too) => too switch
    {
        SelectFault => "选择故障",
        RequestDisturbanceData => "请求扰动数据",
        AbortDisturbanceData => "中止扰动数据",
        RequestChannel => "请求通道",
        AbortChannel => "中止通道",
        RequestTags => "请求标签",
        AbortTags => "中止标签",
        RequestDisturbanceList => "请求被记录的扰动表",
        EndDisturbance => "扰动数据传输结束（无中止）",
        EndDisturbance + 1 => "扰动数据传输结束（主站中止）",
        EndDisturbance + 2 => "扰动数据传输结束（装置中止）",
        EndChannel => "通道传输结束（无中止）",
        EndChannel + 1 => "通道传输结束（主站中止）",
        EndChannel + 2 => "通道传输结束（装置中止）",
        EndTags => "标签传输结束（无中止）",
        EndTags + 1 => "标签传输结束（主站中止）",
        EndTags + 2 => "标签传输结束（装置中止）",
        PositiveAck => "扰动数据传输成功（肯定）",
        NegativeAck => "扰动数据传输失败（否定）",
        _ => $"TOO {too}",
    };
}

/// <summary>扰动数据传输类型 TOV。</summary>
public static class TovCodes
{
    /// <summary>瞬时值。</summary>
    public const byte InstantaneousValues = 1;
}

/// <summary>ACC 扰动通道归属（相别）。</summary>
/// <remarks>0 全局、1-3 IL1-IL3、4 IN、5-7 VL1E-VL3E，8 以上为装置自定义。</remarks>
public static class AccCodes
{
    /// <summary>全局。</summary>
    public const byte Global = 0;

    /// <summary>ACC 语义描述。</summary>
    public static string Describe(byte acc) => acc switch
    {
        Global => "全局",
        1 => "IL1",
        2 => "IL2",
        3 => "IL3",
        4 => "IN",
        5 => "VL1E",
        6 => "VL2E",
        7 => "VL3E",
        _ => $"ACC {acc}",
    };
}

/// <summary>
/// 故障状态字节 SOF：bit7 TP 保护跳闸、bit6 TM 跳闸命令、bit5 TEST 试验、bit4 OTEV 事件由装置启动。
/// </summary>
public readonly record struct Sof(bool TripProtection, bool TripCommand, bool Test, bool EquipmentStart)
{
    /// <summary>编码为 1 字节。</summary>
    public byte Encode()
    {
        var value = (byte)0;
        if (TripProtection)
        {
            value |= 0x80;
        }

        if (TripCommand)
        {
            value |= 0x40;
        }

        if (Test)
        {
            value |= 0x20;
        }

        if (EquipmentStart)
        {
            value |= 0x10;
        }

        return value;
    }

    /// <summary>从 1 字节解码。</summary>
    public static Sof Decode(byte value) => new(
        TripProtection: (value & 0x80) != 0,
        TripCommand: (value & 0x40) != 0,
        Test: (value & 0x20) != 0,
        EquipmentStart: (value & 0x10) != 0);

    /// <inheritdoc />
    public override string ToString()
        => (TripProtection ? "TP " : string.Empty)
            + (TripCommand ? "TM " : string.Empty)
            + (Test ? "TEST " : string.Empty)
            + (EquipmentStart ? "OTEV" : string.Empty);
}

/// <summary>扰动录波通道：ACC 相别 + 一次/二次值与变比 + 归一化采样（-32767…32767）。</summary>
public sealed class DisturbanceChannel
{
    /// <summary>通道归属（ACC：1-3 三相电流、4 中性点等）。</summary>
    public int Acc { get; set; }

    /// <summary>一次额定值（RPV）。</summary>
    public double Primary { get; set; }

    /// <summary>二次额定值（RSV）。</summary>
    public double Secondary { get; set; }

    /// <summary>额定因数（RFA）。</summary>
    public double Factor { get; set; } = 1.0;

    /// <summary>采样点数（全通道一致，见记录的就绪帧 NOE）。</summary>
    public int SampleCount { get; set; }

    /// <summary>归一化采样值（发送/接收缓冲）。</summary>
    public short[] Samples { get; set; } = Array.Empty<short>();
}

/// <summary>一条扰动记录：故障序号 FAN + 状态 SOF + 起始时刻 + 采样间隔 + 通道集。</summary>
public sealed class DisturbanceRecord
{
    /// <summary>故障序号（1-65535）。</summary>
    public int Fan { get; set; }

    /// <summary>故障状态字节。</summary>
    public Sof Status { get; set; }

    /// <summary>故障起始时刻。</summary>
    public DateTime FaultTime { get; set; } = DateTime.Now;

    /// <summary>采样间隔（微秒）。</summary>
    public int SampleIntervalUs { get; set; } = 1000;

    /// <summary>通道集。</summary>
    public List<DisturbanceChannel> Channels { get; } = new();

    /// <summary>每通道采样点数（取首通道）。</summary>
    public int TotalSamples => Channels.Count > 0 ? Channels[0].SampleCount : 0;
}

/// <summary>
/// 扰动/录波 ASDU 的编解码（FUN=255 全局，COT=31）。
/// </summary>
/// <remarks>
/// 服务流程（TOO 驱动）：
/// <code>
/// 主站 ASDU24 TOO=24 ─→ ASDU23 扰动表
/// 主站 ASDU24 TOO=1(FAN) ─→ ASDU26 就绪（NOC/NOE/间隔/时刻）
/// 主站 ASDU25 TOO=64 ─→ （认可）
/// 主站 ASDU24 TOO=2 ─→ ASDU27 × 每通道（一次值/二次值/因数）
/// 主站 ASDU24 TOO=8(ACC) ─→ ASDU30 数据段（分帧）+ ASDU31 TOO=35 通道结束
/// 全部通道完成 ─→ ASDU31 TOO=32 扰动结束
/// </code>
/// </remarks>
public static class DisturbanceAsdu
{
    /// <summary>ASDU 23 被记录的扰动表：每条 FAN(2)+SOF(1)+CP56(7)，VSQ 记录数。</summary>
    public static Asdu DisturbanceList(byte ca, IReadOnlyList<DisturbanceRecord> records)
    {
        var data = new byte[records.Count * 10];
        var offset = 0;
        foreach (var record in records)
        {
            var fan = (ushort)record.Fan;
            data[offset++] = (byte)(fan & 0xFF);
            data[offset++] = (byte)(fan >> 8);
            data[offset++] = record.Status.Encode();
            Cp56Time2a.FromDateTime(record.FaultTime).Encode().CopyTo(data, offset);
            offset += Cp56Time2a.EncodedLength;
        }

        return new Asdu(TypeIds.DisturbanceList, true, records.Count, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0, data);
    }

    /// <summary>ASDU 24/25：TOO(1)+TOV(1)+FAN(2)+ACC(1)。</summary>
    public static Asdu DisturbanceCommand(byte typeId, byte ca, byte too, int fan, byte acc, byte cot)
        => new(typeId, true, 1, cot, false, ca, Asdu.GlobalFun, 0,
            new[] { too, TovCodes.InstantaneousValues, (byte)(fan & 0xFF), (byte)(fan >> 8), acc });

    /// <summary>解码 ASDU 24/25 命令。</summary>
    /// <exception cref="FormatException">数据区不足 5 字节（TOO+TOV+FAN+ACC）。</exception>
    public static (byte Too, byte Tov, int Fan, byte Acc) DecodeCommand(Asdu asdu)
    {
        if (asdu.Data.Length < 5)
        {
            throw new FormatException($"扰动命令数据区不足 5 字节（TOO+TOV+FAN+ACC），实际 {asdu.Data.Length}");
        }

        return (asdu.Data[0], asdu.Data[1], asdu.Data[2] | (asdu.Data[3] << 8), asdu.Data[4]);
    }

    /// <summary>
    /// ASDU 26 扰动数据传输准备就绪：
    /// TOO+TOV+FAN+NOF(2)+NOC(1)+NOE(2)+INT(2 µs)+CP32(4)。
    /// </summary>
    public static Asdu TransferReady(byte ca, byte too, DisturbanceRecord record, int recordedCount)
    {
        var data = new byte[15];
        data[0] = too;
        data[1] = TovCodes.InstantaneousValues;
        var fan = (ushort)record.Fan;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = (byte)(recordedCount & 0xFF);
        data[5] = (byte)(recordedCount >> 8);
        data[6] = (byte)record.Channels.Count;
        var noe = (ushort)record.TotalSamples;
        data[7] = (byte)(noe & 0xFF);
        data[8] = (byte)(noe >> 8);
        var interval = (ushort)record.SampleIntervalUs;
        data[9] = (byte)(interval & 0xFF);
        data[10] = (byte)(interval >> 8);
        new Cp32Time2a(
            record.FaultTime.Second * 1000 + record.FaultTime.Millisecond,
            record.FaultTime.Minute,
            record.FaultTime.Hour).Encode().CopyTo(data, 11);
        return new Asdu(TypeIds.DisturbanceReady, true, 1, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0, data);
    }

    /// <summary>ASDU 27 通道一次值：TOO+TOV+FAN+ACC+RPV(4f)+RSV(4f)+RFA(4f)。</summary>
    public static Asdu ChannelRating(byte ca, byte too, int fan, DisturbanceChannel channel)
    {
        var data = new byte[17];
        data[0] = too;
        data[1] = TovCodes.InstantaneousValues;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = (byte)channel.Acc;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(5, 4), (float)channel.Primary);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(9, 4), (float)channel.Secondary);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(13, 4), (float)channel.Factor);
        return new Asdu(TypeIds.ChannelRating, true, 1, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0, data);
    }

    /// <summary>
    /// ASDU 30 通道扰动数据段：TOO+TOV+FAN+ACC+NDV(1)+NFE(2)+SDV×NDV（归一化 int16 LE）。
    /// </summary>
    public static Asdu ChannelData(byte ca, byte too, int fan, byte acc, short[] samples, int offset, int count)
    {
        // 头部 8 字节（TOO/TOV/FAN/ACC/NDV/NFE）+ NDV×2。
        // 曾误写为 9+2N，导致线路帧尾部恒带一个多余的 0x00；
        // 解码守卫用的是 8+2N，两侧必须一致。
        var data = new byte[8 + count * 2];
        data[0] = too;
        data[1] = TovCodes.InstantaneousValues;
        data[2] = (byte)(fan & 0xFF);
        data[3] = (byte)(fan >> 8);
        data[4] = acc;
        data[5] = (byte)count;
        var nfe = (ushort)(offset + 1); // 元素序号从 1 起
        data[6] = (byte)(nfe & 0xFF);
        data[7] = (byte)(nfe >> 8);
        for (var i = 0; i < count; i++)
        {
            var raw = samples[offset + i];
            data[8 + i * 2] = (byte)(raw & 0xFF);
            data[9 + i * 2] = (byte)((raw >> 8) & 0xFF);
        }

        return new Asdu(TypeIds.ChannelData, true, 1, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0, data);
    }

    /// <summary>ASDU 31 通道/扰动传输终止：TOO+TOV+FAN+ACC。</summary>
    public static Asdu ChannelEnd(byte ca, byte too, int fan, byte acc)
        => new(TypeIds.ChannelEnd, true, 1, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0,
            new[] { too, TovCodes.InstantaneousValues, (byte)(fan & 0xFF), (byte)(fan >> 8), acc });

    /// <summary>ASDU 29 带标签的通道扰动数据：FAN(2)+NOT(1)+TAP(2)+NOT×(FUN+INF+DPI)。</summary>
    public static Asdu DisturbanceTags(byte ca, int fan, int tap, IReadOnlyList<(byte Fun, byte Inf, int Dpi)> tags)
    {
        var data = new byte[5 + tags.Count * 3];
        data[0] = (byte)(fan & 0xFF);
        data[1] = (byte)(fan >> 8);
        data[2] = (byte)tags.Count;
        var tap16 = (ushort)tap;
        data[3] = (byte)(tap16 & 0xFF);
        data[4] = (byte)(tap16 >> 8);
        for (var i = 0; i < tags.Count; i++)
        {
            data[5 + i * 3] = tags[i].Fun;
            data[6 + i * 3] = tags[i].Inf;
            data[7 + i * 3] = (byte)(tags[i].Dpi & 0x03);
        }

        return new Asdu(TypeIds.DisturbanceTags, true, 1, CotCodes.DisturbanceTransfer, false, ca, Asdu.GlobalFun, 0, data);
    }

    // ═══════════════════════════════════════════════════════════════
    //  接收侧解码
    // ═══════════════════════════════════════════════════════════════

    /// <summary>扰动表条目。</summary>
    /// <param name="Fan">故障序号。</param>
    /// <param name="Status">故障状态字节。</param>
    /// <param name="FaultTime">故障时刻。</param>
    public sealed record DisturbanceEntry(int Fan, Sof Status, DateTime FaultTime);

    /// <summary>解码 ASDU 23 扰动表。</summary>
    public static List<DisturbanceEntry> DecodeDisturbanceList(Asdu asdu)
    {
        var entries = new List<DisturbanceEntry>();
        for (var offset = 0; offset + 10 <= asdu.Data.Length; offset += 10)
        {
            entries.Add(new DisturbanceEntry(
                Fan: asdu.Data[offset] | (asdu.Data[offset + 1] << 8),
                Status: Sof.Decode(asdu.Data[offset + 2]),
                FaultTime: Cp56Time2a.Decode(asdu.Data.AsSpan(offset + 3, Cp56Time2a.EncodedLength)).ToDateTime()));
        }

        return entries;
    }

    /// <summary>通道数据段解码结果。</summary>
    /// <param name="Fan">故障序号。</param>
    /// <param name="Acc">通道归属。</param>
    /// <param name="FirstElement">首个元素序号（1 起）。</param>
    /// <param name="Values">采样值。</param>
    public sealed record ChannelDataSegment(int Fan, byte Acc, int FirstElement, short[] Values);

    /// <summary>解码 ASDU 30 通道数据段。</summary>
    /// <exception cref="FormatException">数据区长度不足或声明的点数与实长不符。</exception>
    public static ChannelDataSegment DecodeChannelData(Asdu asdu)
    {
        // 头部 8 字节（TOO/TOV/FAN/ACC/NDV/NFE）+ NDV×2；第三方装置短帧须报错而非越界
        if (asdu.Data.Length < 8)
        {
            throw new FormatException($"通道数据段数据区不足 8 字节（{asdu.Data.Length}）");
        }

        var count = asdu.Data[5];
        if (asdu.Data.Length < 8 + count * 2)
        {
            throw new FormatException($"通道数据段声明 {count} 点但数据区仅 {asdu.Data.Length} 字节");
        }

        var values = new short[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (short)(asdu.Data[8 + i * 2] | (asdu.Data[9 + i * 2] << 8));
        }

        return new ChannelDataSegment(
            Fan: asdu.Data[2] | (asdu.Data[3] << 8),
            Acc: asdu.Data[4],
            FirstElement: asdu.Data[6] | (asdu.Data[7] << 8),
            Values: values);
    }

    /// <summary>解码 ASDU 27 通道一次值。</summary>
    public static (int Fan, byte Acc, float Primary, float Secondary, float Factor) DecodeChannelRating(Asdu asdu)
    {
        if (asdu.Data.Length < 17)
        {
            throw new FormatException($"通道一次值数据区不足 17 字节（{asdu.Data.Length}）");
        }

        return (
            asdu.Data[2] | (asdu.Data[3] << 8),
            asdu.Data[4],
            BinaryPrimitives.ReadSingleLittleEndian(asdu.Data.AsSpan(5, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(asdu.Data.AsSpan(9, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(asdu.Data.AsSpan(13, 4)));
    }

    /// <summary>解码 ASDU 26 就绪帧。</summary>
    public static (int Fan, int RecordedCount, int ChannelCount, int SamplesPerChannel, int IntervalUs, Cp32Time2a FaultTime)
        DecodeTransferReady(Asdu asdu)
    {
        if (asdu.Data.Length < 15)
        {
            throw new FormatException($"扰动就绪帧数据区不足 15 字节（{asdu.Data.Length}）");
        }

        return (
            asdu.Data[2] | (asdu.Data[3] << 8),
            asdu.Data[4] | (asdu.Data[5] << 8),
            asdu.Data[6],
            asdu.Data[7] | (asdu.Data[8] << 8),
            asdu.Data[9] | (asdu.Data[10] << 8),
            Cp32Time2a.Decode(asdu.Data.AsSpan(11, Cp32Time2a.EncodedLength)));
    }
}
