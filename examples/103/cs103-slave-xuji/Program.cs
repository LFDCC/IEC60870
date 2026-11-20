// 许继网口 103 从站模拟器（103 over 104 APCI 承载）
//
// 用途：配合许继测试客户端做全功能联调。覆盖：
//   · STARTDT 握手（连上即回 STARTDT_CON）
//   · 总召唤（TI=7）→ 回遥信/遥测（COT=9）+ 终止帧（TI=8 COT=10）
//   · 调版本号（TI=60）→ 回 TI=5（COT=64）
//   · 一般命令（TI=20）：切换定值区 / 信号复归 / 软压板 → 回 TI=1 确认（COT=20）
//   · 查当前定值区号（TI=62）
//   · 定值读写（TI=61）：INF=100~106 全部 + 107~112 单项
//   · 遥控（TI=64/65/67）：选择/执行/撤销 → 回同类型镜像帧
//   · 电度冻结（TI=88）
//   · 自发上送：每 N 秒推 1 帧遥测（COT=2）+ 1 帧遥信（COT=1）
//
// 用法：
//   dotnet run                 # 默认监听 2403
//   dotnet run -- 2404         # 指定端口
//   dotnet run -- --no-spont   # 关闭自发上送（只测命令应答）
//   dotnet run -- --spont-ms 2000   # 自发上送间隔

using System.Text;
using IEC60870.CS103;
using cs103_private_ioa.Xuji;

Console.OutputEncoding = Encoding.UTF8;

var port = 2403;
var spontMs = 1000;
var enableSpont = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--no-spont": enableSpont = false; break;
        case "--spont-ms": spontMs = int.Parse(args[++i]); break;
        default:
            if (int.TryParse(args[i], out var p)) port = p;
            break;
    }
}

const int Ca = 0x0101;       // 扇区=1、装置地址=1
const byte Fun = 178;        // 线路保护

// 原始帧抓包：自定义承载下唯一能确认"字节到没到"的观察点
var codec = new XujiApduCodec();
codec.ControlFrameReceived += raw =>
    Console.WriteLine($"{(raw.Length > 2 && raw[2] == 0x01 ? "[S帧]" : "[U帧]")} {Convert.ToHexString(raw)}");

var options = new Iec103Options()
    .UseLinkParameters(l => l.FramingCodec = codec);

await using var server = new Iec103Server(options);
XujiPrivateTypes.RegisterAll(server.PrivateTypes);

server.SessionOpened += s =>
{
    Console.WriteLine($"[链路] 主站接入 {s.RemoteEndPoint}");
    if (enableSpont)
    {
        _ = Task.Run(() => SpontaneousLoopAsync(s));
    }
};
server.SessionClosed += (s, r) => Console.WriteLine($"[链路] 断开: {r}");

// 原始帧抓包：自定义承载下唯一能确认"字节到没到"的观察点
server.RawFrameReceived += (s, raw) => Console.WriteLine($"[RAW] {Convert.ToHexString(raw)}");
server.FrameRejected += (s, e) => Console.WriteLine($"[丢弃] {e.RawHex} 原因={e.Reason}");

// 定值：CPU1 区0~区3 各 27 项
var device = new SettingDevice(server, Ca, Fun);
for (byte zone = 0; zone < 4; zone++)
{
    device.SetTable(1, zone, Enumerable.Range(0, 27)
        .Select(i => XujiSettingValue.Create(zone * 1000 + i * 10))
        .ToArray());
}
device.Attach();

// 当前定值区（切换定值区命令会改它）
byte currentZone = 0;

server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
{
    Console.WriteLine($"[RX] {asdu.Describe()}");

    var ca = asdu.Ca;
    var fun = asdu.Fun;
    var inf = asdu.Inf;
    var typeId = asdu.TypeId;
    var data = asdu.Data.ToArray();

    switch (typeId)
    {
        case TypeIds.GeneralInterrogation:
            _ = Task.Run(() => GeneralInterrogationAsync(session, ca));
            break;

        case XujiTypeIds.VersionRequest:
            ReplyVersion(session, ca, fun);
            break;

        case TypeIds.GeneralCommand:
            currentZone = HandleGeneralCommand(session, ca, fun, inf, data, currentZone);
            device.SetCurrentZone(currentZone);
            break;

        case XujiTypeIds.FaultReportAck:
            // 查当前定值区号
            _ = session.SendRawAsduAsync(XujiCreate(XujiTypeIds.FaultReportAck, Ca, fun, inf,
                new[] { currentZone }, XujiCot.Positive));
            Console.WriteLine($"  → 当前定值区 = {currentZone}");
            break;

        case XujiTypeIds.BreakerControl:
        case XujiTypeIds.TapChangerControl:
        case XujiTypeIds.SynchroCheckControl:
            ReplyControlMirror(session, typeId, ca, fun, inf, data);
            break;

        case XujiTypeIds.EnergyFreeze:
            ReplyEnergyFreeze(session, ca, fun, inf, data);
            break;

        case XujiTypeIds.MeasuredValueAck:
        case XujiTypeIds.SelfCheckCommand:
        case XujiTypeIds.FaultReport:
            // 这些是装置主动上送的，收到即忽略
            break;
    }
};

await server.StartAsync(port);
Console.WriteLine($"许继从站已监听 {port}（CA=0x{Ca:X4}, FUN={Fun}）");
Console.WriteLine($"定值: CPU1 区0~区3 各 27 项");
Console.WriteLine($"自发上送: {(enableSpont ? $"每 {spontMs}ms" : "关闭")}");
Console.WriteLine("Ctrl+C 退出\n");

await Task.Delay(Timeout.Infinite);

// ── 应答实现 ──────────────────────────────────────────────────────

static byte[] XujiCreate(byte typeId, int ca, byte fun, byte inf, byte[] data, byte cot, bool negative = false)
    => new Asdu(typeId, true, 1, cot, negative, ca, fun, inf, data, XujiApduLayout.Instance).Encode();

/// <summary>总召唤：遥信 + 遥测各 5 条（COT=9），末帧 TI=8（COT=10）。</summary>
static async Task GeneralInterrogationAsync(IIec103Session session, int ca)
{
    for (var i = 1; i <= 5; i++)
    {
        var statusData = new byte[12];
        statusData[0] = (byte)(i % 2 == 0 ? 2 : 1);
        statusData.AsSpan(1, 4).Fill(0xFF);
        Cp56Time2a.FromDateTime(DateTime.Now).Encode().CopyTo(statusData, 5);
        await session.SendRawAsduAsync(XujiCreate(TypeIds.TimeTaggedMessage, ca, 178, (byte)i, statusData, CotCodes.GiStart));

        var measure = new byte[] { (byte)(i * 100), (byte)((i * 100) >> 8) };
        await session.SendRawAsduAsync(XujiCreate(TypeIds.Measurand, ca, 178, (byte)i, measure, CotCodes.GiStart));
    }

    await session.SendRawAsduAsync(XujiCreate(TypeIds.GeneralInterrogationEnd, ca, 255, 0, new byte[] { 0 }, CotCodes.GiStop));
    Console.WriteLine("  → 总召唤应答完成（5 遥信 + 5 遥测 + 终止帧）");
}

/// <summary>调版本号：回 TI=5（COT=64）。</summary>
static void ReplyVersion(IIec103Session session, int ca, byte fun)
{
    var info = new XujiVersionInfo(Compatibility: 2, Model: "PCS-931",
        VersionFraction: 0x05, VersionInteger: 0x02, RomCrc: 0x1234);
    var payload = new XujiVersionHandler().Encode(info);
    _ = session.SendRawAsduAsync(XujiCreate(XujiTypeIds.VersionResponse, ca, fun, 6, payload, XujiCot.ReportCall));
    Console.WriteLine($"  → 版本 {info.Model} v{info.VersionText}");
}

/// <summary>一般命令：回 TI=1 确认（COT=20），DCO+CP24+RII。返回新的当前定值区。</summary>
static byte HandleGeneralCommand(IIec103Session session, int ca, byte fun, byte inf, byte[] data, byte currentZone)
{
    if (data.Length < 2)
    {
        return currentZone;
    }

    var dco = data[0];
    var rii = data[1];

    // INF=101 且 FUN=178 视为切换定值区：DCO 低 4 位是目标区号
    var newZone = currentZone;
    if (inf == 101 && fun == 178)
    {
        newZone = (byte)(dco & 0x0F);
    }

    var payload = new byte[6];
    payload[0] = dco;
    Cp32Time2a.FromDateTime(DateTime.Now).Encode().CopyTo(payload, 1);
    payload[5] = rii;

    _ = session.SendRawAsduAsync(XujiCreate(TypeIds.TimeTaggedMessage, ca, fun, inf, payload, CotCodes.CommandAck));

    Console.WriteLine(inf == 101 && fun == 178
        ? $"  → 切换定值区 {currentZone} → {newZone}"
        : $"  → 一般命令确认 INF={inf} DCO={dco:X2}");

    return newZone;
}

/// <summary>遥控：回同类型镜像帧（COT=13 控制命令）。</summary>
static void ReplyControlMirror(IIec103Session session, byte typeId, int ca, byte fun, byte inf, byte[] data)
{
    if (data.Length < 2)
    {
        return;
    }

    var payload = new[] { data[0], data[1] };
    _ = session.SendRawAsduAsync(XujiCreate(typeId, ca, fun, inf, payload, XujiCot.ControlCommand));

    var op = (data[0] >> 6) & 0x03;
    var opText = op switch { 0 => "执行", 2 => "选择", 3 => "撤销", _ => $"op{op}" };
    Console.WriteLine($"  → 遥控 {opText} 命令值={data[0] & 0x03} RII={data[1]}");
}

/// <summary>电度冻结：回冻结确认（COT=2）。</summary>
static void ReplyEnergyFreeze(IIec103Session session, int ca, byte fun, byte inf, byte[] data)
{
    var payload = data.Length >= 2 ? new[] { data[0], data[1] } : new byte[] { 0, 0 };
    _ = session.SendRawAsduAsync(XujiCreate(XujiTypeIds.EnergyFreeze, ca, fun, inf, payload, XujiCot.EnergyData));
    Console.WriteLine($"  → 电度冻结确认 FRZ={payload[0]:X2} RII={payload[1]}");
}

// ── 自发上送 ──────────────────────────────────────────────────────

async Task SpontaneousLoopAsync(IIec103Session session)
{
    var seq = 0;
    while (true)
    {
        await Task.Delay(spontMs);
        try
        {
            seq++;

            // TI=9 遥测（COT=2 循环）
            var value = (short)(500 + seq % 100);
            var measure = new byte[] { (byte)value, (byte)(value >> 8) };
            await session.SendRawAsduAsync(XujiCreate(TypeIds.Measurand, Ca, Fun, (byte)(0x40 + seq % 0x40), measure, CotCodes.Cyclic));

            // TI=1 遥信（COT=1 突发）
            var statusData = new byte[12];
            statusData[0] = (byte)(seq % 2 == 0 ? 2 : 1);
            statusData.AsSpan(1, 4).Fill(0xFF);
            Cp56Time2a.FromDateTime(DateTime.Now).Encode().CopyTo(statusData, 5);
            await session.SendRawAsduAsync(XujiCreate(TypeIds.TimeTaggedMessage, Ca, Fun, (byte)(0x40 + seq % 0x40), statusData, CotCodes.Spontaneous));

            Console.WriteLine($"[TX] 自发 #{seq}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TX] 自发上送失败: {ex.Message}");
            return;
        }
    }
}
