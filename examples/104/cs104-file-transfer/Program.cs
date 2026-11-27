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

// ── 构造一条模拟录波文件 ──────────────────────────────────────────
// 文件按段切分：每段 1 帧（受 ASDU 长度限制，单帧数据约 200 字节以内）
var recordFile = BuildDisturbanceFile();
Console.WriteLine($"录波文件: {recordFile.Length} 字节，分 {Ceil(recordFile.Length, SegmentDataSize)} 段");

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
    var match = data.SequenceEqual(expected);
    Console.WriteLine(match
        ? "✔ 内容与从站源文件逐字节一致"
        : $"✘ 内容不一致（期望 {expected.Length} 字节）");
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

// 生成一段模拟录波数据（正弦波，3 通道 × 200 点）
static byte[] BuildDisturbanceFile()
{
    const int pointsPerChannel = 200;
    const int channels = 3;
    var buffer = new byte[pointsPerChannel * channels * 2];
    var index = 0;

    for (var ch = 0; ch < channels; ch++)
    {
        for (var i = 0; i < pointsPerChannel; i++)
        {
            var value = (short)(Math.Sin((i + ch * 20) * 0.05) * 10000);
            buffer[index++] = (byte)(value & 0xFF);
            buffer[index++] = (byte)((value >> 8) & 0xFF);
        }
    }

    return buffer;
}
