// IEC 60870-5-103 主站（网口 / TCP）示例
//
// 103 的网口不是「APCI over TCP」——它就是裸 FT1.2 帧跑在 TCP 上，没有 104 的
// STARTDT/k-w 窗口。所以主站代码与串口版几乎一致，只是建立连接的方式不同。
//
// 现场拓扑：
//   主站/工程师站 ──TCP── 装置（网口 103 服务端，常见端口 2404/2406/5000）
//
// 用法：
//   dotnet run                        # 默认 127.0.0.1:2406（配合 cs103-slave-tcp）
//   dotnet run -- 192.168.1.10 2404   # 指定装置 IP 与端口

using IEC60870.CS103;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 2406;
const byte StationAddress = 1;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"103 主站（TCP）：{host}:{port}，链路地址 {StationAddress}");
Console.WriteLine("按 Ctrl+C 退出\n");

var options = new Iec103Options()
    .UseLinkParameters(l =>
    {
        l.MessageTimeoutMs = 3000;    // 等待从站应答的超时
        l.CharacterTimeoutMs = 100;   // 帧内字符间隔（半帧判定）
        l.MaxRepeat = 3;              // 链路层超时重发次数
    });

await using var client = new Iec103Client(host, port, options);

// ── 诊断事件 ────────────────────────────────────────────────
client.Iec103LinkEvent += evt => Console.WriteLine($"[链路] {evt}");
client.FrameTraced += (direction, frame) =>
    Console.WriteLine($"[帧 {direction}] {frame.Format} C=0x{frame.Control:X2} A={frame.Address} ASDU={Convert.ToHexString(frame.Asdu)}");
client.UnknownAsduReceived += unknown => Console.WriteLine($"[未知] {unknown}");

// ── 业务：所有 ASDU 都走这一个入口，标准类型直接拿强类型 ──
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
    Console.WriteLine("TCP 已连接");

    // 1. 复位通信单元（RESET CU）—— 每次建立链路后必须做
    await client.ResetLinkAsync(StationAddress);
    Console.WriteLine("链路已复位");

    // 2. 时间同步
    await client.SendClockSyncAsync(StationAddress, DateTime.Now);
    Console.WriteLine("时间同步已发送");

    // 3. 总召唤
    var gi = await client.GeneralInterrogationAsync(StationAddress);
    Console.WriteLine($"总召唤完成：{gi.Count} 条 ASDU");

    // 4. 循环轮询：1 级（突发事件）+ 2 级（周期量测）
    //    注意：AsduReceived 事件已经负责打印，这里不再重复打印返回列表。
    Console.WriteLine("\n开始轮询（1 级事件 / 2 级量测），Ctrl+C 退出…");
    var lastClass2 = 0L;

    while (true)
    {
        await client.RequestClass1Async(StationAddress);

        // 2 级数据按现场惯例低频轮询（每 5 秒）
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
    Console.WriteLine("  1. 确认 IP/端口正确，装置是否开放网口 103");
    Console.WriteLine("  2. 确认链路地址（common address）与装置配置一致");
    Console.WriteLine("  3. 用 FrameTraced 观察是否收到任何应答帧");
}
catch (IOException ex)
{
    Console.WriteLine($"[网络错误] {ex.Message}");
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
