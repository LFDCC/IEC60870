// IEC 60870-5-103 私有/扩展类型示例（许继规约）
//
// 本示例演示三件事：
//   1. 如何把厂商扩展类型登记到 Iec103PrivateTypeRegistry，让库自动解码成强类型对象；
//   2. 如何从站侧按规约字节布局组帧上送（一帧一个类型，覆盖许继全部 18 个扩展 TI）；
//   3. 未登记的 TI 不会丢帧——会走 UnknownAsduReceived 兜底。
//
// 许继 103 的「私有类型」不是 104 那种 128-170 专用区，而是把标准只定义到 31 的
// TypeID 空间扩展到 36~94（详见 Xuji/XujiTypeIds.cs 的表格号注释）。
// 规约原文写的是 RS-232/RS-485，但 FT1.2 帧与承载介质无关——同一套报文可直接跑 TCP。
//
// 用法：
//   dotnet run                 # 默认 TCP 自环（127.0.0.1:2406），无需硬件
//   dotnet run -- 2506         # 指定端口
//   dotnet run -- --serial COM3  # 走串口（需另一个主站，或配合 cs103-master-serial）
//
// 自环模式用「同一个进程里的从站 + 主站」跑完整链路（复位→总召唤→1 级数据），
// 因此可以当作回归脚本使用。

using IEC60870.CS103;
using cs103_private_ioa.Xuji;

const byte Ca = 1;           // 公共地址
const byte Fun = 161;        // 功能类型（许继装置信息表定义，此处取一个专用范围值作为示例）
const byte FunMeas = 161;    // 遥测用的 FUN（实际由装置信息表决定）

var serialPortName = GetArg(args, "--serial");
var port = 2406;
foreach (var arg in args)
{
    if (!arg.StartsWith("--") && int.TryParse(arg, out var parsed))
    {
        port = parsed;
    }
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (serialPortName is not null)
{
    await RunSerialAsync(serialPortName);
}
else
{
    await RunLoopbackAsync(port);
}

return;

// ══════════════════════════════════════════════════════════════════
//  TCP 自环：从站 + 主站同进程
// ══════════════════════════════════════════════════════════════════

async Task RunLoopbackAsync(int tcpPort)
{
    Console.WriteLine($"许继 103 扩展类型示例（TCP 自环 127.0.0.1:{tcpPort}）");
    Console.WriteLine("规约依据：《许继103通信规约》表10~表38\n");

    await using var server = new Iec103Server();

    // 从站也注册同一套类型：既用于日志里显示语义，也便于将来做「校验再回送」
    XujiPrivateTypes.RegisterAll(server.PrivateTypes);

    server.SessionOpened += s => Console.WriteLine($"[从站] 主站接入 {s.RemoteEndPoint}");

    // 总召唤时把许继扩展类型全部推一遍
    server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
    {
        Console.WriteLine($"[从站] 收到 {asdu.Describe()}");

        switch (asdu.TypeId)
        {
            case TypeIds.GeneralInterrogation:
                PushAllXujiPoints(session, asdu.Ca);
                session.SendAsduAsync(Asdu.GeneralInterrogationEnd(asdu.Ca, asdu.Data.Length > 0 ? asdu.Data[0] : (byte)0));
                break;
        }
    };

    await server.StartAsync(tcpPort);
    Console.WriteLine($"[从站] 已监听 {tcpPort}\n");

    await using var client = new Iec103Client("127.0.0.1", tcpPort);

    // ── 主站侧：登记 + 订阅 ──────────────────────────────────
    XujiPrivateTypes.RegisterAll(client.PrivateTypes);

    var received = new List<Iec103DecodedAsdu>();

    // 只有一个 ASDU 入口。解析顺序：先问厂商解码器（switch TypeID），再问库的标准解码器。
    client.AsduReceived += (in Iec103AsduView asdu) =>
    {
        var decoded = XujiPrivateTypes.Decode(in asdu) ?? Iec103AsduDecoder.Decode(in asdu);
        if (decoded is null)
        {
            return;
        }

        received.Add(decoded);
        PrintDecoded(decoded);
    };
    client.UnknownAsduReceived += unknown => Console.WriteLine($"[主站][未登记] {unknown.Reason}");

    await client.ConnectAsync();
    Console.WriteLine("[主站] 已连接");

    // 1. 复位通信单元（RESET CU）
    await client.ResetLinkAsync(Ca);
    Console.WriteLine("[主站] 链路已复位");

    // 2. 总召唤：从站会连续推 18 种扩展类型
    var gi = await client.GeneralInterrogationAsync(Ca);
    Console.WriteLine($"[主站] 总召唤收到 {gi.Count} 条 ASDU");

    // 3. 轮询 1 级数据，直到收够或超时
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (received.Count < XujiSamples.Count && DateTime.UtcNow < deadline)
    {
        await client.RequestClass1Async(Ca);
        await Task.Delay(50);
    }

    Console.WriteLine($"\n── 汇总 ──");
    Console.WriteLine($"主站解码出的许继扩展对象：{received.Count} 个（预期 {XujiSamples.Count} 个）");
    Console.WriteLine(received.Count == XujiSamples.Count ? "✔ 全部类型解码成功" : "✘ 有类型未收到");

    // ── 4. 定值读写（TI=61）─────────────────────────────────────
    Console.WriteLine("\n── 定值读写（TI=61）──");

    // 装置侧：模拟「CPU 板 1 / 定值区 0」的参数区
    var device = new SettingDevice(server, Ca, Fun);
    device.SetTable(cpu: 1, zone: 0, new ushort[] { 100, 200, 300, 400, 500 });
    device.Attach();

    var settings = new SettingClient(client, Ca, Fun);

    // 4.1 读定值（INF=100）
    var loaded = await settings.ReadAsync(cpu: 1, zone: 0);
    Console.WriteLine($"[主站] 读到定值 {loaded.Length} 项：{string.Join(", ", loaded)}");

    // 4.2 改一项，写回（预发 101 → 返校 102 → 执行 103 → 确认 104）
    loaded[2] = 999;
    await settings.WriteAsync(cpu: 1, zone: 0, loaded);
    Console.WriteLine($"[主站] 已写入：{string.Join(", ", loaded)}");

    // 4.3 回读校验
    var after = await settings.ReadAsync(cpu: 1, zone: 0);
    Console.WriteLine($"[主站] 回读：{string.Join(", ", after)}");
    Console.WriteLine(after.SequenceEqual(loaded) ? "✔ 定值读写校验通过" : "✘ 定值回读不一致");

    // 4.4 读一个不存在的定值区 → 装置回否定确认（COT=21）
    try
    {
        await settings.ReadAsync(cpu: 1, zone: 3, timeoutMs: 1000);
        Console.WriteLine("✘ 不存在的定值区竟然读成功了");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"✔ 不存在的定值区被正确拒绝：{ex.Message}");
    }

    await server.StopAsync();
}

// ══════════════════════════════════════════════════════════════════
//  串口模式：只做从站（配合 cs103-master-serial 或真实主站）
// ══════════════════════════════════════════════════════════════════

async Task RunSerialAsync(string portName)
{
    Console.WriteLine($"许继 103 扩展类型从站（串口 {portName} 9600 8E1）");
    Console.WriteLine("按 Ctrl+C 退出\n");

    using var serial = new System.IO.Ports.SerialPort(
        portName, 9600, System.IO.Ports.Parity.Even, 8, System.IO.Ports.StopBits.One);

    var options = new Iec103Options()
        .UseLinkParameters(l =>
        {
            l.LocalLinkAddress = 1;      // RS-485 总线上的本站地址
            l.AcceptBroadcast = true;
            l.MessageTimeoutMs = 2000;
            l.CharacterTimeoutMs = 50;
        });

    await using var server = new Iec103Server(options);
    XujiPrivateTypes.RegisterAll(server.PrivateTypes);

    server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
    {
        Console.WriteLine($"[从站] {asdu.Describe()}");
        if (asdu.TypeId == TypeIds.GeneralInterrogation)
        {
            PushAllXujiPoints(session, asdu.Ca);
            session.SendAsduAsync(Asdu.GeneralInterrogationEnd(asdu.Ca, 0));
        }
    };

    await server.StartAsync(serial);
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

// ══════════════════════════════════════════════════════════════════
//  从站上送：把 18 个许继扩展类型各推一帧
// ══════════════════════════════════════════════════════════════════

void PushAllXujiPoints(IIec103Session session, byte ca)
{
    var now = DateTime.Now;
    var cp56 = Cp56Time2a.FromDateTime(now);
    var cp24 = Cp32Time2a.FromDateTime(now);

    foreach (var asdu in XujiSamples.Build(ca, Fun, FunMeas, cp56, cp24))
    {
        session.SendAsduAsync(asdu);
    }

    Console.WriteLine($"[从站] 已上送 {XujiSamples.Count} 个许继扩展类型");
}

// ══════════════════════════════════════════════════════════════════
//  打印解码结果
// ══════════════════════════════════════════════════════════════════

void PrintDecoded(Iec103DecodedAsdu obj)
{
    switch (obj)
    {
        case Iec103ElementList<XujiSpStatus> list:
            Console.WriteLine($"[主站] TI=40 遥信量状态 ×{list.Count} → {string.Join("，", list.Elements.Select(x => x.State))}");
            break;

        case Iec103ElementList<XujiAlarmEvent> alarms:
            foreach (var a in alarms.Elements)
            {
                Console.WriteLine($"[主站] TI=41 告警信号 SIN={a.Sin} {(a.Quality.Dpi != 0 ? "动作" : "返回")} @ {a.Time} 品质={DescribeQ(a.Quality)}");
            }

            break;

        case XujiTapChangerStatus tap:
            Console.WriteLine($"[主站] TI=39 调压开关 档位={tap.TapPosition} 瞬变={tap.Transient} @ {tap.Time}");
            break;

        case XujiBreakerStatus breaker:
            Console.WriteLine($"[主站] TI=42 断路器状态 RII={breaker.Rii} → {string.Join("，", breaker.Positions.Select(x => x.State))}");
            break;

        case Iec103ElementList<XujiBreakerChange> changes:
            foreach (var c in changes.Elements)
            {
                Console.WriteLine($"[主站] TI=43 断路器变位 {c.Quality.State} @ {c.Time}");
            }

            break;

        case Iec103ElementList<XujiMeasuredValue> values:
            Console.WriteLine($"[主站] TI=50 遥测值 ×{values.Count} → {string.Join("，", values.Elements.Select(v => $"INF{v.Inf}={v.Value}"))}");
            break;

        case Iec103ElementList<XujiEnergyCounterValue> energy:
            Console.WriteLine($"[主站] TI=36 电度量 ×{energy.Count} → {string.Join("，", energy.Elements.Select(e => $"{e.Reading}(序号{e.Sequence})"))}");
            break;

        case XujiFaultReport fault:
            Console.WriteLine($"[主站] TI=70 事故信号 RET={fault.RelativeTimeMs}ms FAN={fault.Fan} "
                + $"相别={fault.FaultType.Phases} 性质={fault.FaultType.Nature} "
                + $"故障值=[{string.Join(", ", fault.Values)}]");
            break;

        case XujiSelfCheckFrame check:
            Console.WriteLine($"[主站] TI=73/74 自检记录 段号={check.Segment} 共 {check.Records.Length} 条");
            foreach (var r in check.Records)
            {
                Console.WriteLine($"         {r.Quality.State} @ {r.Time} 附带结果={r.Values.Length} 个");
            }

            break;

        case XujiSettingCommand setting:
            Console.WriteLine($"[主站] TI=61 定值命令 CPU={setting.CpuBoard} 区={setting.Zone} 序号={setting.Index} "
                + $"定值=[{string.Join(", ", setting.Values)}]");
            break;

        case XujiSequenceNumber seq:
            Console.WriteLine($"[主站] TI=62/94 序号={seq.Value}");
            break;

        case XujiControlCommand control:
            Console.WriteLine($"[主站] TI=64/65/67 控制命令 {control.OperationText} 命令值={control.CommandValue} RII={control.Rii}");
            break;

        case XujiEnergyFreeze freeze:
            Console.WriteLine($"[主站] TI=88 电度量冻结 模式={freeze.FreezeMode} 总请求={freeze.EnergyRequest} RII={freeze.Rii}");
            break;

        case XujiEmptyPayload:
            Console.WriteLine("[主站] TI=93 结束帧（无信息体）");
            break;

        default:
            Console.WriteLine($"[主站] {obj.GetType().Name}");
            break;
    }
}

static string DescribeQ(DpiQuality q)
    => (q.Invalid ? "IV " : "") + (q.NonTopical ? "NT " : "")
        + (q.Blocked ? "BL " : "") + (q.Substituted ? "SB " : "");

static string? GetArg(string[] argv, string name)
{
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
}


// ══════════════════════════════════════════════════════════════════
//  许继扩展类型样例数据
// ══════════════════════════════════════════════════════════════════

/// <summary>构造覆盖全部许继扩展 TI 的样例帧。</summary>
internal static class XujiSamples
{
    /// <summary>样例帧数量（一个 TI 一帧）。</summary>
    public static int Count => Build(1, 161, 161, default, default).Count;

    /// <summary>按规约表逐类型组帧。</summary>
    public static List<Asdu> Build(byte ca, byte fun, byte funMeas, Cp56Time2a cp56, Cp32Time2a cp24)
    {
        var list = new List<Asdu>();
        byte inf = 1;

        // 36 电度量：读数 + 品质/序号
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.EnergyCounter, ca, fun, inf++,
            new Iec103ElementList<XujiEnergyCounterValue>(new[]
            {
                new XujiEnergyCounterValue(123456, Invalid: false, Adjusted: false, Overflow: false, Sequence: 1),
                new XujiEnergyCounterValue(654321, Invalid: false, Adjusted: true, Overflow: false, Sequence: 2),
            })));

        // 39 调压开关状态
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.TapChangerStatus, ca, fun, inf++,
            new XujiTapChangerStatus(
                Transient: false,
                TapPosition: 9,
                Quality: DpiQuality.FromDpi(0),
                Time: cp24)));

        // 40 遥信量状态（无时标）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SinglePointStatus, ca, fun, inf++,
            new Iec103ElementList<XujiSpStatus>(new[]
            {
                new XujiSpStatus(Spi: false, Invalid: false, NonTopical: false, Blocked: false, Substituted: false),
                new XujiSpStatus(Spi: true, Invalid: false, NonTopical: true, Blocked: false, Substituted: false),
            })));

        // 41 告警信号（SPI + CP24 + SIN）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.AlarmSignal, ca, fun, inf++,
            new Iec103ElementList<XujiAlarmEvent>(new[]
            {
                new XujiAlarmEvent(DpiQuality.FromDpi(1), cp24, Sin: 0),
            })));

        // 42 断路器状态（N×DPI + RII）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.BreakerStatus, ca, fun, inf++,
            new XujiBreakerStatus(
                new[]
                {
                    DpiQuality.FromDpi(2),  // 合
                    DpiQuality.FromDpi(1),  // 分
                },
                Rii: 0)));

        // 43 断路器状态变位（DPI + CP24）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.BreakerStatusChange, ca, fun, inf++,
            new Iec103ElementList<XujiBreakerChange>(new[]
            {
                new XujiBreakerChange(DpiQuality.FromDpi(2), cp24),
            })));

        // 50 遥测值（逐条带 INF）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.MeasuredValueWithInf, ca, fun, inf++,
            new Iec103ElementList<XujiMeasuredValue>(new[]
            {
                new XujiMeasuredValue(1, 1234),
                new XujiMeasuredValue(2, -567),
            })));

        // 61 定值命令（CPU/区 + 序号 + 定值）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SettingCommand, ca, fun, XujiSettingInf.Read,
            new XujiSettingCommand(CpuBoard: 1, Zone: 0, Index: 1, Values: new ushort[] { 100, 200, 300 })));

        // 62 故障报告确认/结束
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.FaultReportAck, ca, fun, 0, new XujiSequenceNumber(1)));

        // 64 断路器控制（DCO + RII）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.BreakerControl, ca, fun, inf++,
            new XujiControlCommand(Dco: 0x02, Rii: 7)));   // 执行 + 合

        // 65 调压开关升降控制（选择）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.TapChangerControl, ca, fun, inf++,
            new XujiControlCommand(Dco: 0x81, Rii: 8)));   // 选择 + 降一步

        // 67 同期方式选择（执行 + 检同期）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SynchroCheckControl, ca, fun, inf++,
            new XujiControlCommand(Dco: 0x01, Rii: 9)));   // 执行 + 检同期

        // 70 事故信号（DPI + RET + FAN + CP56 + SIN + 故障类型 + 故障值）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.FaultReport, ca, fun, inf++,
            new XujiFaultReport(
                Quality: DpiQuality.FromDpi(2),         // 动作
                RelativeTimeMs: 235,
                Fan: 12,
                Time: cp56,
                Sin: 1,
                FaultType: XujiFaultType.Create(selectiveTrip: true, nature: 0b01, phaseBits: 0b0011), // 瞬时 AN
                Values: new[] { 12.34f, 56.78f })));

        // 73 自检记录（保护装置，DPI 品质）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SelfCheckRecordProtection, ca, fun, inf++,
            new XujiSelfCheckFrame(
                new[]
                {
                    new XujiSelfCheckRecord(DpiQuality.FromDpi(1), cp56, new[] { 1.5f }),
                    new XujiSelfCheckRecord(DpiQuality.FromDpi(2), cp56, Array.Empty<float>()),
                },
                Segment: 1)));

        // 74 自检记录（测控装置，SPI 品质）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SelfCheckRecordControl, ca, fun, inf++,
            new XujiSelfCheckFrame(
                new[] { new XujiSelfCheckRecord(DpiQuality.FromDpi(1), cp56, new[] { 2.5f, 3.5f }) },
                Segment: 2)));

        // 88 电度量冻结（FRZ + RII）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.EnergyFreeze, ca, fun, 0,
            new XujiEnergyFreeze(Frz: 0x17, Rii: 3)));   // 总请求 5 + 冻结带复位

        // 93 调遥测值结束（无信息体）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.MeasuredValueAck, ca, funMeas, 0,
            new XujiEmptyPayload(), cot: XujiCot.EndOfData));

        // 94 调自检记录命令（段号）
        list.Add(XujiPrivateTypes.CreateAsdu(XujiTypeIds.SelfCheckCommand, ca, fun, 0, new XujiSequenceNumber(1)));

        return list;
    }
}
