// IEC 60870-5-103 从站（串口 / RS-485）示例
//
// 模拟一台保护装置：监听 RS-485 总线，响应主站的复位/总召唤/1、2 级召唤，
// 并在故障时上送保护事件（TI=2）。
//
// 现场拓扑：
//   PC/上位机 ──RS-485 两线── 本模拟器（地址 1）── 其他装置（地址 2、3…）
//
// 关键点：RS-485 是共享总线，从站必须只响应本站地址与广播，
// 否则会与其他从站抢答导致总线冲突——库已内置该过滤（LocalLinkAddress）。
//
// 用法：
//   dotnet run -- COM2            # 默认 9600 8E1、本站地址 1
//   dotnet run -- COM4 19200 5    # 指定波特率与本站地址

using System.IO.Ports;
using IEC60870.CS103;

var portName = args.Length > 0 ? args[0] : "COM2";
var baudRate = args.Length > 1 ? int.Parse(args[1]) : 9600;
var localAddress = args.Length > 2 ? byte.Parse(args[2]) : (byte)1;

Console.WriteLine($"103 从站（串口）：{portName} {baudRate} 8E1，本站地址 {localAddress}");
Console.WriteLine("按 Ctrl+C 退出\n");

const byte Ca = 1;          // 公共地址
const byte FunCurrent = 160; // 兼容范围功能类型（160-255 为厂商专用）

using var serialPort = new SerialPort(portName, baudRate, Parity.Even, 8, StopBits.One);

var options = new Iec103Options()
    .UseLinkParameters(l =>
    {
        l.LocalLinkAddress = localAddress; // 本站链路地址（RS-485 过滤依据）
        l.AcceptBroadcast = true;          // 接受 255 广播
        l.MessageTimeoutMs = 2000;
        l.CharacterTimeoutMs = 50;
    });

await using var server = new Iec103Server(options);

// ── 诊断事件 ────────────────────────────────────────────────
server.SessionOpened += s => Console.WriteLine($"[链路] 主站接入 {s.RemoteEndPoint}");
server.SessionClosed += (s, reason) => Console.WriteLine($"[链路] 主站断开：{reason}");
server.FrameTraced += (s, dir, frame) =>
    Console.WriteLine($"[帧 {dir}] {frame.Format} C=0x{frame.Control:X2} A={frame.Address}");

// ── 业务：响应主站命令 ──────────────────────────────────────
server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
{
    Console.WriteLine($"[收到] {asdu.Describe()}");

    switch (asdu.TypeId)
    {
        case TypeIds.GeneralInterrogation:
            // 总召唤：先回送全部监视点，再发终止帧（TI=8）
            PushAllPoints(session, Ca, FunCurrent);
            break;

        case TypeIds.ClockSync:
            // 时间同步：更新装置时钟并回读（同 CS101/103 惯例：从站回读当前时钟）
            var time = Cp56Time2a.Decode(asdu.Data);
            Console.WriteLine($"  时钟同步 → {time}");
            session.SendAsduAsync(
                Asdu.ClockSyncEcho(Cp56Time2a.FromDateTime(time.ToDateTime()), asdu.Ca));
            break;

        case TypeIds.GeneralCommand:
            // 一般命令（遥控）：回肯定确认
            var (dct, qu, rii) = Asdu.DecodeGeneralCommand(Asdu.Materialize(asdu));
            Console.WriteLine($"  一般命令：DCT={dct}(1分/2合) QU={qu} RII={rii}");
            break;

        case TypeIds.GenericCommand:
            // 通用分类读命令：回送一条示例定值
            GenericReadHandler(session, asdu);
            break;
    }
};

// 总召唤：回送 3 个开关量 + 终止帧
static void PushAllPoints(IIec103Session session, byte ca, byte fun)
{
    var now = Cp56Time2a.FromDateTime(DateTime.Now);

    for (byte inf = 1; inf <= 3; inf++)
    {
        // 状态：1=分 2=合，模拟三相断路器位置
        var dpi = DpiQuality.FromDpi(inf == 2 ? 2 : 1);

        session.SendAsduAsync(Asdu.TimeTaggedMessage(
            ca: ca,
            fun: fun,
            inf: inf,
            dpi: dpi,
            time: now,
            cot: CotCodes.RemoteOperation));  // 总召唤回送用 COT=11（工程通行）
    }

    // 总召唤终止帧（主站据此结束召唤循环）
    session.SendAsduAsync(Asdu.GeneralInterrogationEnd(ca));

    Console.WriteLine($"  已回送 3 个监视点 + 终止帧");
}

// 通用分类读：示例回送一条 ASCII 描述
static void GenericReadHandler(IIec103Session session, in Iec103AsduView asdu)
{
    var rii = asdu.Data.Length > 0 ? asdu.Data[0] : (byte)0;

    var set = GenericDataSet.Ascii(
        new Gin(1, 1),
        KodCodes.Description,
        "模拟保护装置");

    session.SendAsduAsync(Asdu.GenericDataAsdu(
        ca: asdu.Ca,
        rii: rii,
        continuation: false,
        cot: CotCodes.GenericRead,
        sets: new[] { set }));

    Console.WriteLine($"  已回送通用分类读响应（RII={rii}）");
}

try
{
    await server.StartAsync(serialPort);
    Console.WriteLine($"从站已启动，等待主站复位链路…\n");

    // ── 模拟故障上送：每 15 秒产生一次保护事件 ──────────────
    var fan = 0;
    while (true)
    {
        await Task.Delay(15000);

        if (!server.Sessions.Any(s => s.LinkEstablished))
        {
            continue;
        }

        fan = (fan % 65535) + 1;

        // 保护事件（TI=2）：相对时标 + 故障序号 + CP56 时标
        await server.SendProtectionEventAsync(
            ca: Ca,
            fun: FunCurrent,
            inf: 1,
            dpi: DpiQuality.FromDpi(2),  // 合（模拟跳闸）
            relativeTimeMs: 1234,
            fan: fan,
            time: DateTime.Now);

        Console.WriteLine($"[上送] 保护事件 FAN={fan}（等待主站 1 级召唤取走）");
    }
}
catch (IOException ex)
{
    Console.WriteLine($"[串口错误] {ex.Message}");
    Console.WriteLine("排查建议：串口被占用，或线缆未连接。");
}
catch (OperationCanceledException)
{
    Console.WriteLine("已取消");
}
