// IEC 60870-5-104 录波文件召唤示例
//
// 演示标准 104 的文件传输流程（对应 IEC 60870-5-101 文件传输附录）：
//
//   主站                          从站
//   F_SC_NA_1 选择文件(SCQ=1)  →
//                              ←   F_FR_NA_1 文件就绪（NOF / 文件长度 / FRQ）
//   F_SC_NA_1 请求文件(SCQ=2)  →
//                              ←   F_SR_NA_1 段就绪（段号 / 段长度 / SRQ）
//                              ←   F_SG_NA_1 段数据 ×N（每帧一个段）
//                              ←   F_LS_NA_1 最后段（LSQ 指示是否随后停止激活）
//   F_SC_NA_1 停止激活(SCQ=3)  →
//                              ←   F_LS_NA_1 确认（COT=ACTIVATION_TERM）
//
// 说明：许继 103 的录波走 TI=24/26/30/31（私有），104 的录波走这里的 120~127
// 文件传输，两者是完全不同的机制。
//
// 用法：
//   dotnet run                # 自环：从站 + 主站同进程，端口 2410
//   dotnet run -- 2411        # 指定端口

using System.Text;
using IEC60870.Core;
using IEC60870.CS104;

Console.OutputEncoding = Encoding.UTF8;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 2410;

// 单帧段数据上限：ASDU 249 - 头(6) - IO 固定头(IOA3+NOF2+段号1+段长1) = 236，留余量取 200
const int SegmentDataSize = 200;

// ── 构造一条模拟录波文件（COMTRADE 格式）──────────────────────────
// COMTRADE 由 .cfg（通道定义，文本）+ .dat（采样数据，CSV）两个文件组成。
// 104 文件传输一次只传一个流，因此把两个文件打包成一个传输包：
//   [4B "CTP1"][4B cfg 长度 LE][cfg 字节][dat 字节]
// 主站收到后按此结构拆回 .cfg / .dat 两个文件。
var recordFile = BuildComtradePackage(out var cfgText, out var datText);
var cfgBytes = Encoding.ASCII.GetBytes(cfgText);
var datBytes = Encoding.ASCII.GetBytes(datText);

Console.WriteLine($"COMTRADE 录波: cfg {cfgBytes.Length} B + dat {datBytes.Length} B " +
                  $"= 打包 {recordFile.Length} B，分 {Ceil(recordFile.Length, SegmentDataSize)} 段");

// 从站侧源文件落盘，便于与主站收到的文件直接对比
var sourceDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
Directory.CreateDirectory(sourceDir);
await File.WriteAllTextAsync(Path.Combine(sourceDir, "source.cfg"), cfgText, Encoding.ASCII);
await File.WriteAllTextAsync(Path.Combine(sourceDir, "source.dat"), datText, Encoding.ASCII);
Console.WriteLine($"源文件已保存: {sourceDir}\\source.cfg + source.dat");

using var server = new Iec104Server();

// ── 从站：处理文件传输命令 ────────────────────────────────────────
server.AsduReceived += (Iec104Session session, in AsduView view) =>
{
    var raw = view.Raw.ToArray();
    var asdu = new ASDU(server.Parameters, raw, 0, raw.Length);
    var al = server.Parameters;

    switch (asdu.TypeId)
    {
        case TypeID.F_SC_NA_1:
            HandleFileCommand(session, asdu, al);
            break;
    }
};

await server.StartAsync(port);
Console.WriteLine($"104 从站已监听 {port}");
Console.WriteLine();

// ── 主站：走完整召唤流程 ──────────────────────────────────────────
try
{
    await RunMasterAsync(port, recordFile);
}
finally
{
    await server.StopAsync();
}

// ══════════════════════════════════════════════════════════════════
//  从站侧
// ══════════════════════════════════════════════════════════════════

void HandleFileCommand(Iec104Session session, ASDU cmd, ApplicationLayerParameters al)
{
    var io = (FileCallOrSelect)cmd.GetElement(0);
    Console.WriteLine($"[从站] F_SC_NA_1 SCQ={io.SCQ} NOF={io.NOF} 段号={io.NameOfSection}");

    switch (io.SCQ)
    {
        case SelectAndCallQualifier.SELECT_FILE:
            // 回 ACTIVATION_CON 表示已选中
            SendSimple(session, al, cmd, TypeID.F_SC_NA_1, CauseOfTransmission.ACTIVATION_CON, io);
            break;

        case SelectAndCallQualifier.REQUEST_FILE:
            // ① 文件就绪 → ② 逐段数据 → ③ 最后段
            _ = Task.Run(async () =>
            {
                await SendSimpleAsync(session, al, cmd, TypeID.F_FR_NA_1,
                    CauseOfTransmission.ACTIVATION_CON,
                    new FileReady(0, NameOfFile.DISTURBANCE_DATA, recordFile.Length, positive: true));

                var segmentCount = Ceil(recordFile.Length, SegmentDataSize);
                for (var seg = 0; seg < segmentCount; seg++)
                {
                    var offset = seg * SegmentDataSize;
                    var len = Math.Min(SegmentDataSize, recordFile.Length - offset);
                    var chunk = new byte[len];
                    Array.Copy(recordFile, offset, chunk, 0, len);

                    // 段就绪
                    await SendSimpleAsync(session, al, cmd, TypeID.F_SR_NA_1,
                        CauseOfTransmission.ACTIVATION_CON,
                        new SectionReady(0, NameOfFile.DISTURBANCE_DATA, (byte)seg, len, notReady: false));

                    // 段数据
                    await SendSimpleAsync(session, al, cmd, TypeID.F_SG_NA_1,
                        CauseOfTransmission.ACTIVATION_CON,
                        new FileSegment(0, NameOfFile.DISTURBANCE_DATA, (byte)seg, chunk));

                    // 最后段：指示传输结束，随后等主站停止激活
                    if (seg == segmentCount - 1)
                    {
                        await SendSimpleAsync(session, al, cmd, TypeID.F_LS_NA_1,
                            CauseOfTransmission.ACTIVATION_CON,
                            new FileLastSegmentOrSection(0, NameOfFile.DISTURBANCE_DATA, (byte)seg,
                                LastSectionOrSegmentQualifier.FILE_TRANSFER_WITH_DEACT, checksum: 0));
                    }
                }

                Console.WriteLine($"[从站] 已发送 {segmentCount} 段，共 {recordFile.Length} 字节");
            });
            break;

        case SelectAndCallQualifier.DEACTIVATE_FILE:
            SendSimple(session, al, cmd, TypeID.F_LS_NA_1,
                CauseOfTransmission.ACTIVATION_TERMINATION,
                new FileLastSegmentOrSection(0, io.NOF, io.NameOfSection,
                    LastSectionOrSegmentQualifier.NOT_USED, checksum: 0));
            Console.WriteLine("[从站] 文件传输已停止激活");
            break;

        default:
            Console.WriteLine($"[从站] SCQ={io.SCQ} 未处理");
            break;
    }
}

void SendSimple(Iec104Session session, ApplicationLayerParameters al, ASDU req,
    TypeID typeId, CauseOfTransmission cot, InformationObject io)
    => _ = SendSimpleAsync(session, al, req, typeId, cot, io);

static async Task SendSimpleAsync(Iec104Session session, ApplicationLayerParameters al, ASDU req,
    TypeID typeId, CauseOfTransmission cot, InformationObject io)
{
    var asdu = new ASDU(al, cot, false, false, req.Oa, req.Ca, false);
    // AddInformationObject 会把 ASDU 的 TypeId 设为 io.Type，故不需要单独指定
    asdu.AddInformationObject(io);
    await session.SendAsync(asdu);
}

// ══════════════════════════════════════════════════════════════════
//  主站侧
// ══════════════════════════════════════════════════════════════════

async Task RunMasterAsync(int tcpPort, byte[] expected)
{
    await using var client = new Iec104Client("127.0.0.1", tcpPort);

    var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var collected = new List<byte[]>();
    var state = "init";

    client.AsduReceived += (in AsduView view) =>
    {
        var raw = view.Raw.ToArray();
        var asdu = new ASDU(client.Parameters, raw, 0, raw.Length);

        switch (asdu.TypeId)
        {
            case TypeID.F_FR_NA_1:
                {
                    var fr = (FileReady)asdu.GetElement(0);
                    Console.WriteLine($"[主站] F_FR_NA_1 文件就绪 NOF={fr.NOF} 长度={fr.LengthOfFile} 肯定={fr.Positive}");
                    state = "fileReady";
                    break;
                }

            case TypeID.F_SR_NA_1:
                {
                    var sr = (SectionReady)asdu.GetElement(0);
                    Console.WriteLine($"[主站] F_SR_NA_1 段就绪 段号={sr.NameOfSection} 段长={sr.LengthOfSection}");
                    state = "sectionReady";
                    break;
                }

            case TypeID.F_SG_NA_1:
                {
                    var sg = (FileSegment)asdu.GetElement(0);
                    collected.Add(sg.SegmentData);
                    Console.WriteLine($"[主站] F_SG_NA_1 段数据 段号={sg.NameOfSection} {sg.SegmentData.Length} 字节");
                    state = "segment";
                    break;
                }

            case TypeID.F_LS_NA_1:
                {
                    var ls = (FileLastSegmentOrSection)asdu.GetElement(0);
                    Console.WriteLine($"[主站] F_LS_NA_1 COT={asdu.Cot} LSQ={ls.LSQ}");

                    // 带 WITH_DEACT 意味着数据已送完、需主站随后发停止激活。
                    // 数据在这时候就齐了，先交付（停止激活的确认只是流程收尾）。
                    if (ls.LSQ is LastSectionOrSegmentQualifier.FILE_TRANSFER_WITH_DEACT
                        or LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT)
                    {
                        done.TrySetResult(Concat(collected));
                    }
                    break;
                }
        }
    };

    await client.ConnectAsync();
    await client.StartDataTransferAsync();
    Console.WriteLine("104 主站已连接\n");

    // ① 选择文件
    await client.SendAsync(MakeFileCommand(client.Parameters, SelectAndCallQualifier.SELECT_FILE, 0));
    await Task.Delay(300);

    // ② 请求文件（从站回 FR → SR → SG×N → LS）
    await client.SendAsync(MakeFileCommand(client.Parameters, SelectAndCallQualifier.REQUEST_FILE, 0));

    // 等最后一段（最多 5 秒）
    var data = await WaitWithTimeout(done.Task, TimeSpan.FromSeconds(5));

    // ③ 停止激活
    await client.SendAsync(MakeFileCommand(client.Parameters, SelectAndCallQualifier.DEACTIVATE_FILE, 0));
    await Task.Delay(300);

    Console.WriteLine();
    if (data is null)
    {
        Console.WriteLine("✘ 未在超时内收到完整文件");
        return;
    }

    Console.WriteLine($"文件接收完成: {data.Length} 字节");

    // 拆包并落盘
    if (!TryUnpackComtrade(data, out var rxCfg, out var rxDat))
    {
        Console.WriteLine("✘ 收到的数据不是有效的 COMTRADE 传输包（包头/长度不匹配）");
        return;
    }

    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var cfgFile = Path.Combine(sourceDir, $"received-{stamp}.cfg");
    var datFile = Path.Combine(sourceDir, $"received-{stamp}.dat");
    await File.WriteAllTextAsync(cfgFile, rxCfg, Encoding.ASCII);
    await File.WriteAllTextAsync(datFile, rxDat, Encoding.ASCII);

    Console.WriteLine($"已保存: {cfgFile}（{Encoding.ASCII.GetByteCount(rxCfg)} B）");
    Console.WriteLine($"        {datFile}（{Encoding.ASCII.GetByteCount(rxDat)} B）");

    var match = data.SequenceEqual(expected);
    Console.WriteLine(match
        ? "✔ 内容与从站源文件逐字节一致"
        : $"✘ 内容不一致（期望 {expected.Length} 字节）");

    Console.WriteLine($"✔ cfg 与源一致: {rxCfg == cfgText}");
    Console.WriteLine($"✔ dat 与源一致: {rxDat == datText}");
}

static ASDU MakeFileCommand(ApplicationLayerParameters al, SelectAndCallQualifier scq, byte section)
{
    var asdu = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
    // FileCallOrSelect.Type == F_SC_NA_1，AddInformationObject 会自动设置
    asdu.AddInformationObject(new FileCallOrSelect(0, NameOfFile.DISTURBANCE_DATA, section, scq));
    return asdu;
}

static async Task<byte[]?> WaitWithTimeout(Task<byte[]> task, TimeSpan timeout)
{
    var completed = await Task.WhenAny(task, Task.Delay(timeout));
    return completed == task ? await task : null;
}

static byte[] Concat(List<byte[]> parts)
{
    var total = parts.Sum(x => x.Length);
    var result = new byte[total];
    var offset = 0;
    foreach (var part in parts)
    {
        part.CopyTo(result, offset);
        offset += part.Length;
    }
    return result;
}

// ══════════════════════════════════════════════════════════════════
//  辅助
// ══════════════════════════════════════════════════════════════════

static int Ceil(int a, int b) => (a + b - 1) / b;

// ══════════════════════════════════════════════════════════════════
//  COMTRADE 生成与拆包
// ══════════════════════════════════════════════════════════════════

const int PointsPerChannel = 200;
const int SampleRate = 1000;          // 1000 Hz → 采样间隔 1 ms
const int Channels = 4;

/// <summary>
/// 生成 COMTRADE 录波并打包成单条传输流。
/// </summary>
/// <remarks>
/// 包结构：<c>[4B "CTP1"][4B cfg 长度 LE][cfg 文本][dat 文本]</c>。
/// 104 文件传输一次只传一个文件流，而 COMTRADE 需要 .cfg + .dat 两个文件，
/// 故在应用层自行打包。真实装置若支持传多个文件，可对每个文件单独走一轮
/// 文件传输流程（NOF 相同、文件名不同）。
/// </remarks>
static byte[] BuildComtradePackage(out string cfg, out string dat)
{
    var start = new DateTime(2026, 11, 27, 10, 30, 0, DateTimeKind.Local);

    // ── .cfg ──────────────────────────────────────────────
    var sb = new StringBuilder();

    // 1) 站名, 设备ID, 版本年
    sb.Append("XUJI,PROT-01,1999\r\n");

    // 2) 通道总数, 模拟量通道数(4I/8U 中的 A 后缀表示模拟量)
    sb.Append($"{Channels},{Channels}A,0D\r\n");

    // 3) 通道定义：通道号, 名称, 相别, 单位, 一次系数a, 二次系数b, 偏移, 有效值, 名称编码
    //    物理值 = a × 原始值 + b
    sb.Append("1,IA,A,A,0.01,0,0,0,0\r\n");
    sb.Append("2,IB,B,A,0.01,0,0,0,0\r\n");
    sb.Append("3,IC,C,A,0.01,0,0,0,0\r\n");
    sb.Append("4,3I0,N,A,0.01,0,0,0,0\r\n");

    // 4) 采样率, 每周期采样数（填 0 或实际采样率）
    sb.Append($"{SampleRate},0\r\n");

    // 5) 采样点数
    sb.Append($"{PointsPerChannel}\r\n");

    // 6) 第一个数据点的时间戳
    sb.Append($"{start:dd/MM/yyyy},{start:HH:mm:ss.ffffff}\r\n");

    // 7) 触发时间戳
    sb.Append($"{start.AddMilliseconds(50):dd/MM/yyyy},{start.AddMilliseconds(50):HH:mm:ss.ffffff}\r\n");

    // 8) 文件类型（ASCII = 文本 .dat）
    sb.Append("ASCII\r\n");

    // 9) 时间倍率（1 表示微秒）
    sb.Append("1\r\n");

    cfg = sb.ToString();

    // ── .dat ──────────────────────────────────────────────
    var db = new StringBuilder(PointsPerChannel * Channels * 12);
    for (var i = 0; i < PointsPerChannel; i++)
    {
        var timeUs = i * 1_000_000L / SampleRate;      // 微秒
        db.Append(i + 1).Append(',').Append(timeUs).Append(',')
          .Append(Sample(1, i)).Append(',')
          .Append(Sample(2, i)).Append(',')
          .Append(Sample(3, i)).Append(',')
          .Append(Sample(4, i)).Append("\r\n");
    }

    dat = db.ToString();

    // ── 打包 ──────────────────────────────────────────────
    var cfgBytes = Encoding.ASCII.GetBytes(cfg);
    var datBytes = Encoding.ASCII.GetBytes(dat);
    var package = new byte[8 + cfgBytes.Length + datBytes.Length];

    Encoding.ASCII.GetBytes("CTP1").CopyTo(package, 0);
    BitConverter.TryWriteBytes(package.AsSpan(4, 4), cfgBytes.Length);
    cfgBytes.CopyTo(package, 8);
    datBytes.CopyTo(package, 8 + cfgBytes.Length);

    return package;

    // 4 通道采样值：正弦波，原始码值（× 0.01 A 得实际安培数）
    static int Sample(int channel, int i)
        => (int)(Math.Sin((i + (channel - 1) * 15) * 0.05) * 10000);
}

/// <summary>从传输包拆回 .cfg / .dat 文本。</summary>
static bool TryUnpackComtrade(byte[] data, out string cfg, out string dat)
{
    cfg = dat = string.Empty;
    if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "CTP1")
    {
        return false;
    }

    var cfgLen = BitConverter.ToInt32(data, 4);
    if (cfgLen < 0 || 8 + cfgLen > data.Length)
    {
        return false;
    }

    cfg = Encoding.ASCII.GetString(data, 8, cfgLen);
    dat = Encoding.ASCII.GetString(data, 8 + cfgLen, data.Length - 8 - cfgLen);
    return true;
}
