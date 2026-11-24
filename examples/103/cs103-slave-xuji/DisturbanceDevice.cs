// 故障录波数据（扰动数据传输）从站实现。
//
// 规约 3.7.9 的完整流程（表39~表54）：
//   TI=24 TOO=1 选择故障          → TI=26 数据准备好（含 NOC 路数 / NOE 点数 / INT 间隔）
//   TI=24 TOO=2 请求送数据        → TI=26 数据准备好
//   TI=24 TOO=16 请求送状态量     → TI=29 状态量初始状态（无状态量时可跳过）
//   TI=24 TOO=8  请求某路模拟量   → TI=30 该路采样数据（NDV≤25 点/帧）+ TI=31 本路结束
//   TI=24 TOO=32 扰动结束
//
// 本实现提供 2 路模拟量（A 相电流、A 相电压），每路 50 点，采样间隔 1000μs。

using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;

/// <summary>故障录波数据从站：应答扰动数据命令并上送波形。</summary>
public sealed class DisturbanceDevice
{
    /// <summary>每帧最多 25 点采样（规约表51）。</summary>
    private const int MaxPointsPerFrame = 25;

    private readonly Iec103Server _server;

    /// <summary>按 CA 区分的录波状态（多装置时各装置各自的录波）。</summary>
    private readonly Dictionary<int, FaultRecord> _records = new();

    /// <summary>构造录波从站。</summary>
    /// <param name="server">已构建的 103 从站。</param>
    public DisturbanceDevice(Iec103Server server)
    {
        _server = server;
    }

    /// <summary>挂接到从站的 ASDU 回调上（处理 TI=24）。</summary>
    public void Attach() => _server.AsduReceived += OnAsdu;

    /// <summary>为一个 CA 登记一条故障录波数据。</summary>
    /// <param name="ca">公共地址。</param>
    /// <param name="record">录波内容。</param>
    public void SetRecord(int ca, FaultRecord record) => _records[ca] = record;

    private void OnAsdu(IIec103Session session, in Iec103AsduView asdu)
    {
        if (asdu.TypeId != TypeIds.DisturbanceCommand)
        {
            return;
        }

        var data = asdu.Data;
        if (data.Length < 5)
        {
            return;
        }

        // 表40/44/50：TOO(1) TOV(1) FAN(2) ACC(1)
        var too = data[0];
        var tov = data[1];
        var fan = (ushort)(data[2] | (data[3] << 8));
        var acc = data[4];

        if (!_records.TryGetValue(asdu.Ca, out var record))
        {
            ReplyNegAck(session, asdu.Ca, asdu.Fun, too, tov, fan, acc);
            return;
        }

        switch (too)
        {
            case TooCodes.SelectFault:
            case TooCodes.RequestDisturbanceData:
                ReplyReady(session, asdu.Ca, asdu.Fun, tov, fan, record);
                break;

            case TooCodes.RequestChannel:
                ReplyChannel(session, asdu.Ca, asdu.Fun, tov, fan, acc, record);
                break;

            case TooCodes.EndDisturbance:
                Console.WriteLine($"[录波] 扰动数据传输结束 FAN={fan}");
                break;

            default:
                // 状态量（16/17）、标签（16/17）、认可（64/65）等：本实现无状态量与标签，回成功认可
                ReplyAck(session, asdu.Ca, asdu.Fun, too, tov, fan, acc);
                break;
        }
    }

    /// <summary>表41：故障录波数据准备好（TI=26）。</summary>
    private static void ReplyReady(
        IIec103Session session, int ca, byte fun, byte tov, ushort fan, FaultRecord record)
    {
        // 未用(1) TOV(1) FAN(2) NOF(2) NOC(1) NOE(2) INT(2) 时标(4)
        var payload = new byte[15];
        payload[0] = 0;
        payload[1] = tov;
        payload[2] = (byte)(fan & 0xFF);
        payload[3] = (byte)(fan >> 8);
        payload[4] = (byte)(record.FaultNumber & 0xFF);
        payload[5] = (byte)(record.FaultNumber >> 8);
        payload[6] = (byte)record.Channels.Count;
        payload[7] = (byte)(record.PointsPerChannel & 0xFF);
        payload[8] = (byte)(record.PointsPerChannel >> 8);
        payload[9] = (byte)(record.SampleIntervalUs & 0xFF);
        payload[10] = (byte)(record.SampleIntervalUs >> 8);
        Cp32Time2a.FromDateTime(record.StartTime).Encode().CopyTo(payload, 11);

        _ = session.SendRawAsduAsync(XujiCreate(
            TypeIds.DisturbanceReady, ca, fun, 0, payload, CotCodes.DisturbanceTransfer));

        Console.WriteLine($"[录波] 数据准备好 FAN={fan} 路数={record.Channels.Count} " +
                          $"点数={record.PointsPerChannel} 间隔={record.SampleIntervalUs}μs");
    }

    /// <summary>表51：某路模拟量采样数据（TI=30，每帧 ≤25 点）。</summary>
    private static void ReplyChannel(
        IIec103Session session, int ca, byte fun, byte tov, ushort fan, byte acc, FaultRecord record)
    {
        var channel = record.Channels.FirstOrDefault(c => c.Acc == acc);
        if (channel is null)
        {
            ReplyAck(session, ca, fun, TooCodes.EndChannel, tov, fan, acc);
            return;
        }

        // 未用(1) 未用(1) 数据类型(1) FAN(2) 模拟量名称(1) NDV(1) NFE(2) 采样数据(2×NDV)
        var total = channel.Samples.Length;
        var offset = 0;
        while (offset < total)
        {
            var count = Math.Min(MaxPointsPerFrame, total - offset);
            var payload = new byte[9 + count * 2];

            payload[0] = 0;
            payload[1] = 0;
            payload[2] = 1;                              // 数据类型
            payload[3] = (byte)(fan & 0xFF);
            payload[4] = (byte)(fan >> 8);
            payload[5] = acc;                            // 模拟量名称
            payload[6] = (byte)count;                    // NDV
            payload[7] = (byte)(offset & 0xFF);          // NFE 低
            payload[8] = (byte)(offset >> 8);            // NFE 高

            for (var i = 0; i < count; i++)
            {
                var v = channel.Samples[offset + i];
                payload[9 + i * 2] = (byte)(v & 0xFF);
                payload[10 + i * 2] = (byte)((v >> 8) & 0xFF);
            }

            _ = session.SendRawAsduAsync(XujiCreate(
                TypeIds.ChannelData, ca, fun, 0, payload, CotCodes.DisturbanceTransfer));

            offset += count;
        }

        // 表52：本路模拟量发送结束（TI=31，TOO=35）
        var end = new byte[5];
        end[0] = TooCodes.EndChannel;
        end[1] = tov;
        end[2] = (byte)(fan & 0xFF);
        end[3] = (byte)(fan >> 8);
        end[4] = acc;

        _ = session.SendRawAsduAsync(XujiCreate(
            TypeIds.ChannelEnd, ca, fun, 0, end, CotCodes.DisturbanceTransfer));

        Console.WriteLine($"[录波] 通道 ACC={acc} 已发 {total} 点（{((total + MaxPointsPerFrame - 1) / MaxPointsPerFrame)} 帧）");
    }

    /// <summary>表47/54：回命令确认（TI=31，携带 TOO）。</summary>
    private static void ReplyAck(
        IIec103Session session, int ca, byte fun, byte too, byte tov, ushort fan, byte acc)
    {
        var payload = new byte[5];
        payload[0] = too;
        payload[1] = tov;
        payload[2] = (byte)(fan & 0xFF);
        payload[3] = (byte)(fan >> 8);
        payload[4] = acc;

        _ = session.SendRawAsduAsync(XujiCreate(
            TypeIds.ChannelEnd, ca, fun, 0, payload, CotCodes.DisturbanceTransfer));
    }

    /// <summary>无此 CA 的录波数据时回否定（TI=25）。</summary>
    private static void ReplyNegAck(
        IIec103Session session, int ca, byte fun, byte too, byte tov, ushort fan, byte acc)
    {
        var payload = new byte[5];
        payload[0] = TooCodes.NegativeAck;
        payload[1] = tov;
        payload[2] = (byte)(fan & 0xFF);
        payload[3] = (byte)(fan >> 8);
        payload[4] = acc;

        _ = session.SendRawAsduAsync(XujiCreate(
            TypeIds.DisturbanceAck, ca, fun, 0, payload, CotCodes.DisturbanceTransfer));

        Console.WriteLine($"[录波] CA=0x{ca:X4} 无录波数据，回否定");
    }

    private static byte[] XujiCreate(
        byte typeId, int ca, byte fun, byte inf, byte[] data, byte cot)
        => new Asdu(typeId, true, 1, cot, false, ca, fun, inf, data, XujiApduLayout.Instance).Encode();
}

/// <summary>一条故障录波数据。</summary>
/// <param name="FaultNumber">电网故障序号 NOF。</param>
/// <param name="PointsPerChannel">每路采样点数 NOE。</param>
/// <param name="SampleIntervalUs">采样间隔 INT（微秒）。</param>
/// <param name="StartTime">录波起始时刻。</param>
/// <param name="Channels">各模拟量通道。</param>
public sealed record FaultRecord(
    ushort FaultNumber,
    ushort PointsPerChannel,
    ushort SampleIntervalUs,
    DateTime StartTime,
    IReadOnlyList<AnalogChannel> Channels);

/// <summary>一路模拟量通道。</summary>
/// <param name="Acc">模拟量名称（ACC）：1~3 Iabc、4 IN、5~7 Uabc、8 3U0。</param>
/// <param name="Samples">采样值（规约里是原始码值，除以换算系数得二次值）。</param>
public sealed record AnalogChannel(byte Acc, short[] Samples);
