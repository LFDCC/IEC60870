// IEC 60870-5-103 从站（网口 / TCP）示例
//
// 模拟一台保护装置的网口 103 服务：TCP 监听，接受多个主站（监控后台 + 保护信息子站 +
// 掌上测试仪可同时连入）。帧格式与串口完全相同——裸 FT1.2，没有 104 的 APCI。
//
// 与串口版的区别：
//   · 不需要 LocalLinkAddress 地址过滤（一条 TCP 连接对应一个主站）；
//   · 可以同时有多个会话，Sessions 里能看到每个连接。
//
// 用法：
//   dotnet run                 # 监听 2406（配合 cs103-master-tcp）
//   dotnet run -- 2404         # 指定端口

using IEC60870.CS103;

var port = args.Length > 0 ? int.Parse(args[0]) : 2406;
const byte Ca = 1;
const byte FunCurrent = 160;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"103 从站（TCP）：监听 {port}，CA={Ca}");
Console.WriteLine("按 Ctrl+C 退出\n");

await using var server = new Iec103Server();

// ── 诊断事件 ────────────────────────────────────────────────
server.SessionOpened += s => Console.WriteLine($"[链路] 主站接入 {s.RemoteEndPoint}");
server.SessionClosed += (s, reason) => Console.WriteLine($"[链路] 主站断开 {s.RemoteEndPoint}：{reason}");
server.FrameTraced += (s, direction, frame) =>
    Console.WriteLine($"[帧 {direction}] {frame.Format} C=0x{frame.Control:X2} A={frame.Address}");
server.UnknownAsduReceived += (s, unknown) => Console.WriteLine($"[未知] {unknown}");

// ── 业务 ────────────────────────────────────────────────────
server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
{
    if (Iec103AsduDecoder.TryDecode(in asdu, out var decoded, out _))
    {
        Console.WriteLine($"[收到] {decoded!.Header.Describe()}");
    }
    else
    {
        Console.WriteLine($"[收到] {asdu.Describe()}");
    }

    switch (asdu.TypeId)
    {
        case TypeIds.GeneralInterrogation:
            PushAllPoints(session, asdu.Ca);
            break;

        case TypeIds.ClockSync:
            // 时钟同步：回读当前时钟（工程惯例）
            session.SendAsduAsync(Asdu.ClockSyncEcho(
                Cp56Time2a.FromDateTime(DateTime.Now), asdu.Ca));
            break;

        case TypeIds.GenericCommand:
            RespondGenericRead(session, in asdu);
            break;
    }
};

await server.StartAsync(port);
Console.WriteLine($"从站已启动，等待主站复位链路…\n");

// ── 后台模拟 ────────────────────────────────────────────────
// 每 5 秒一次周期量测（TI=9，走 2 级队列），每 15 秒一次保护事件（TI=2，走 1 级队列）
var fan = 0;
var tick = 0;
while (true)
{
    await Task.Delay(5000);
    tick++;

    if (!server.Sessions.Any(s => s.LinkEstablished))
    {
        continue;
    }

    // 2 级数据：三相电流（int16 标度值）
    for (byte inf = 10; inf <= 12; inf++)
    {
        await server.SendMeasurementAsync(Ca, FunCurrent, inf, (short)(100 + inf * 7 + tick));
    }

    if (tick % 3 != 0)
    {
        continue;
    }

    fan = (fan % 65535) + 1;
    await server.SendProtectionEventAsync(
        ca: Ca,
        fun: FunCurrent,
        inf: 1,
        dpi: DpiQuality.FromDpi(2),
        relativeTimeMs: 125,
        fan: fan,
        time: DateTime.Now);

    Console.WriteLine($"  → 已上送保护事件 FAN={fan}");
}

// ══════════════════════════════════════════════════════════════════
//  总召唤：回送监视点 + 终止帧
// ══════════════════════════════════════════════════════════════════

static void PushAllPoints(IIec103Session session, byte ca)
{
    var now = Cp56Time2a.FromDateTime(DateTime.Now);

    for (byte inf = 1; inf <= 3; inf++)
    {
        // 模拟三相断路器位置：1 分 / 2 合
        var dpi = DpiQuality.FromDpi(inf == 2 ? 2 : 1);

        // 总召唤回送用 COT=11（工程通行；部分装置用 5/9）
        _ = session.SendAsduAsync(Asdu.TimeTaggedMessage(ca, FunCurrent, inf, dpi, now, CotCodes.RemoteOperation));
    }

    _ = session.SendAsduAsync(Asdu.GeneralInterrogationEnd(ca));
    Console.WriteLine("  → 已回送 3 个监视点 + 终止帧");
}

// ══════════════════════════════════════════════════════════════════
//  通用分类读：回送一条描述
// ══════════════════════════════════════════════════════════════════

static void RespondGenericRead(IIec103Session session, in Iec103AsduView asdu)
{
    var rii = asdu.DataLength > 0 ? asdu.Data[0] : (byte)0;

    var set = GenericDataSet.Ascii(new Gin(1, 1), KodCodes.Description, "模拟保护装置 (TCP)");
    _ = session.SendAsduAsync(Asdu.GenericDataAsdu(asdu.Ca, rii, continuation: false, CotCodes.GenericRead, new[] { set }));

    Console.WriteLine($"  → 已回送通用分类读响应（RII={rii}）");
}
