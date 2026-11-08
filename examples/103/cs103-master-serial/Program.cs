// IEC 60870-5-103 主站（串口 / RS-485）示例
//
// 现场拓扑：
//   PC/上位机 ──RS-485 两线── 保护装置（地址 1）── 保护装置（地址 2）── ...
//
// 103 的串口与网口在字节层面完全一致：都是裸 FT1.2 帧（无 APCI）。
// 区别仅在通道建立方式，所以协议栈、链路状态机、解析方式完全相同——
// 对照 cs103-master-tcp 看，除了构造客户端的几行，其余逻辑一字不差。
//
// 用法：
//   dotnet run -- COM1            # 默认 9600 8E1（103 现场惯例：偶校验）
//   dotnet run -- COM3 19200

using System.IO.Ports;
using IEC60870.CS103;

var portName = args.Length > 0 ? args[0] : "COM1";
var baudRate = args.Length > 1 ? int.Parse(args[1]) : 9600;
const byte StationAddress = 1;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"103 主站（串口）：{portName} {baudRate} 8E1，链路地址 {StationAddress}");
Console.WriteLine("按 Ctrl+C 退出\n");

// 103 现场惯例：9600 波特率、8 数据位、偶校验、1 停止位
// 注意：串口对象所有权归调用方，库只 Close 不 Dispose
using var serialPort = new SerialPort(portName, baudRate, Parity.Even, 8, StopBits.One);

var options = new Iec103Options()
    .UseLinkParameters(l =>
    {
        l.MessageTimeoutMs = 2000;   // 等待从站应答的超时
        l.CharacterTimeoutMs = 50;   // 帧内字符间隔（半帧判定）
        l.MaxRepeat = 3;             // 链路层超时重发次数
    });

await using var client = new Iec103Client(serialPort, options);

// ── 诊断事件：现场排障三件套 ────────────────────────────────
client.Iec103LinkEvent += evt => Console.WriteLine($"[链路] {evt}");
client.FrameTraced += (direction, frame) =>
    Console.WriteLine($"[帧 {direction}] {frame.Format} C=0x{frame.Control:X2} A={frame.Address} ASDU={Convert.ToHexString(frame.Asdu)}");
client.UnknownAsduReceived += unknown => Console.WriteLine($"[未知] {unknown}");

// ── 业务：标准类型交给库解码器，私有类型由自己 switch 解析 ──
client.AsduReceived += (in Iec103AsduView asdu) =>
{
    if (Iec103AsduDecoder.TryDecode(in asdu, out var decoded, out _))
    {
        Print(decoded!);
    }
    else
    {
        Console.WriteLine($"[未解码] {asdu.Describe()}");
    }
};

try
{
    await client.ConnectAsync();

    // 1. 复位通信单元（RESET CU）—— 每次建立链路后必须做
    await client.ResetLinkAsync(StationAddress);
    Console.WriteLine("链路已复位");

    // 2. 时间同步
    await client.SendClockSyncAsync(StationAddress, DateTime.Now);
    Console.WriteLine("时间同步已发送");

    // 3. 总召唤（TI=7），取回全部监视点直到终止帧（TI=8）
    var gi = await client.GeneralInterrogationAsync(StationAddress);
    Console.WriteLine($"总召唤完成：{gi.Count} 条 ASDU");

    // 4. 循环轮询：1 级事件 + 2 级量测（AsduReceived 已负责打印）
    Console.WriteLine("\n开始轮询（1 级事件 / 2 级量测），Ctrl+C 退出…");
    var lastClass2 = 0L;

    while (true)
    {
        await client.RequestClass1Async(StationAddress);

        var now = Environment.TickCount64;
        if (now - lastClass2 >= 5000)
        {
            lastClass2 = now;
            await client.RequestClass2Async(StationAddress);
        }

        await Task.Delay(2000);
    }
}
catch (TimeoutException ex)
{
    Console.WriteLine($"[超时] 从站无响应：{ex.Message}");
    Console.WriteLine("排查建议：");
    Console.WriteLine("  1. 确认串口号正确（设备管理器）");
    Console.WriteLine("  2. 确认波特率/校验与装置一致（103 常用 9600 8E1）");
    Console.WriteLine("  3. 确认链路地址正确（本站地址由装置定值决定）");
    Console.WriteLine("  4. 用 FrameTraced 观察是否收到任何应答帧");
}
catch (IOException ex)
{
    Console.WriteLine($"[串口错误] {ex.Message}");
    Console.WriteLine("排查建议：串口被其他程序占用，或线缆未连接。");
}
catch (OperationCanceledException)
{
    Console.WriteLine("已取消");
}

// ══════════════════════════════════════════════════════════════════
//  打印：标准 TI 的强类型结果
// ══════════════════════════════════════════════════════════════════

static void Print(Iec103DecodedAsdu decoded)
{
    switch (decoded)
    {
        case Iec103MonitorMessage m:
            foreach (var p in m.Points)
            {
                Console.WriteLine($"  [监视] FUN={m.Fun} INF={p.Inf} {p.Quality.State} "
                    + $"品质=IV:{p.Quality.Invalid} NT:{p.Quality.NonTopical} BL:{p.Quality.Blocked} SB:{p.Quality.Substituted} "
                    + $"时标={p.Time?.ToString() ?? "无"}");
            }

            break;

        case Iec103ProtectionEventMessage p:
            Console.WriteLine($"  [保护事件] FUN={p.Fun} INF={p.Inf} {p.Quality.State} "
                + $"相对时标={p.RelativeTimeMs}ms 故障序号={p.Fan} 时标={p.Time}");
            break;

        case Iec103MeasurandMessage v:
            Console.WriteLine($"  [测量] FUN={v.Fun} INF={v.Inf} 值=[{string.Join(", ", v.Values)}]");
            break;

        case Iec103IdentificationMessage i:
            Console.WriteLine($"  [标识] {i.Text}");
            break;

        case Iec103ClockSyncMessage c:
            Console.WriteLine($"  [时钟] {c.Time}");
            break;

        case Iec103InterrogationMessage q:
            Console.WriteLine($"  [总召唤] {(q.IsEnd ? "终止" : "启动")} SCN={q.Scn}");
            break;

        case Iec103GenericDataMessage g:
            Console.WriteLine($"  [通用分类] RII={g.Rii} 数据集 {g.Sets.Count} 条");
            foreach (var set in g.Sets)
            {
                Console.WriteLine($"      GIN={set.Gin.Group:D2}/{set.Gin.Entry:D2} "
                    + $"KOD={KodCodes.Describe(set.Kod)} 值=[{string.Join(", ", set.DecodeValues())}]");
            }

            break;

        case Iec103GeneralCommandMessage c:
            Console.WriteLine($"  [命令] DCT={c.Dct} QU={c.Qu} RII={c.Rii}");
            break;

        default:
            Console.WriteLine($"  [{decoded.Header.Describe()}]");
            break;
    }
}
