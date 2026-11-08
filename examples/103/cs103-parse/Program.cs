// IEC 60870-5-103 标准类型取值示例
//
// 关键差别（也是 103 和 104 最容易被搞混的地方）：
//   104 的 ASDU 是一棵对象树，每个信息对象自带 IOA，所以可以「按 TypeID 造对象 + 用 IOA 定位」。
//   103 没有 IOA：寻址是 ASDU 头里的 FUN/INF，信息体按 TI 解释成一段字节流，
//   而且不同厂商还会复用同一个 TI 承载不同语义。所以 103 的正确姿势是「按 TI 整帧解码」。
//
// 库已经把这件事做掉了：Iec103AsduDecoder.Decode(in view) 返回强类型记录，
//   TI=1  → Iec103MonitorMessage      （Points：品质 + 时标 + 附加信息）
//   TI=2  → Iec103ProtectionEventMessage
//   TI=5  → Iec103IdentificationMessage
//   TI=6  → Iec103ClockSyncMessage
//   TI=7/8→ Iec103InterrogationMessage
//   TI=9  → Iec103MeasurandMessage
//   TI=10 → Iec103GenericDataMessage
//   TI=20 → Iec103GeneralCommandMessage
//   TI=23 → Iec103DisturbanceListMessage
//   TI=30 → Iec103ChannelDataMessage
// 未覆盖的 TI 返回 false / null，交给私有处理器或未知兜底。
//
// 用法：
//   dotnet run                        # TCP 自环：从站推一批标准 ASDU，主站逐帧取值打印
//   dotnet run -- 2507                # 指定端口
//   dotnet run -- --hex 68-0F-0F-68-73-01-06-81-08-01-00-00-00-00-0A-11-07-0B-1A-4B-16
//                                     # 离线解析现场抓到的 FT1.2 帧

using IEC60870.CS103;

const byte Ca = 1;
const byte Fun = 160;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (TryGetArg(args, "--hex") is { } hexText)
{
    ParseHexFrame(hexText);
    return;
}

var port = 2407;
foreach (var arg in args)
{
    if (!arg.StartsWith("--") && int.TryParse(arg, out var parsed))
    {
        port = parsed;
    }
}

await RunLoopbackAsync(port);
return;

// ══════════════════════════════════════════════════════════════════
//  离线：解析一段抓到的 FT1.2 帧
// ══════════════════════════════════════════════════════════════════

static void ParseHexFrame(string text)
{
    var bytes = ParseHex(text);
    if (!Ft12Frame.TryDecode(bytes, out var frame, out var error) || frame is null)
    {
        Console.WriteLine($"帧解析失败：{error}");
        return;
    }

    Console.WriteLine($"帧格式 : {frame.Format}");
    Console.WriteLine($"控制域 : 0x{frame.Control:X2}（{DescribeControl(frame.Control)}）");
    Console.WriteLine($"链路地址: {frame.Address}");
    Console.WriteLine($"ASDU   : {Convert.ToHexString(frame.Asdu)}");
    Console.WriteLine();

    if (frame.Asdu.Length < Asdu.HeaderLength)
    {
        Console.WriteLine("固定帧/单字符帧，不含 ASDU。");
        return;
    }

    var view = new Iec103AsduView(frame.Asdu);
    Console.WriteLine(view.Describe());

    if (Iec103AsduDecoder.TryDecode(in view, out var decoded, out var reason))
    {
        Console.WriteLine(Format(decoded!));
    }
    else
    {
        Console.WriteLine($"该 TI 不在标准解码器覆盖范围：{reason}");
    }
}

// ══════════════════════════════════════════════════════════════════
//  自环：从站推一批标准 ASDU，主站逐帧取值
// ══════════════════════════════════════════════════════════════════

async Task RunLoopbackAsync(int tcpPort)
{
    Console.WriteLine($"103 标准类型取值示例（TCP 自环 127.0.0.1:{tcpPort}）\n");

    await using var server = new Iec103Server();
    server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
    {
        if (asdu.TypeId != TypeIds.GeneralInterrogation)
        {
            return;
        }

        foreach (var item in BuildStandardBatch(Ca, Fun))
        {
            session.SendAsduAsync(item);
        }

        session.SendAsduAsync(Asdu.GeneralInterrogationEnd(asdu.Ca, asdu.Data.Length > 0 ? asdu.Data[0] : (byte)0));
    };

    await server.StartAsync(tcpPort);
    Console.WriteLine($"[从站] 已监听 {tcpPort}");

    await using var client = new Iec103Client("127.0.0.1", tcpPort);

    var parsedCount = 0;
    client.AsduReceived += (in Iec103AsduView asdu) =>
    {
        if (!Iec103AsduDecoder.TryDecode(in asdu, out var decoded, out var reason))
        {
            Console.WriteLine($"[跳过] {asdu.Describe()}（{reason}）");
            return;
        }

        parsedCount++;
        Console.WriteLine(Format(decoded!));
    };
    client.UnknownAsduReceived += unknown => Console.WriteLine($"[未知] {unknown.Reason}");

    await client.ConnectAsync();
    await client.ResetLinkAsync(Ca);
    Console.WriteLine("[主站] 已复位链路\n");

    await client.GeneralInterrogationAsync(Ca);
    await client.RequestClass1Async(Ca);

    // TI=9 测量值走 2 级队列，需要单独召 2 级
    await client.RequestClass2Async(Ca);
    Console.WriteLine($"\n共解析 {parsedCount} 帧");
}

// ══════════════════════════════════════════════════════════════════
//  从站：构造一批覆盖标准 TI 的样例
// ══════════════════════════════════════════════════════════════════

static List<Asdu> BuildStandardBatch(byte ca, byte fun)
{
    var now = DateTime.Now;
    var cp56 = Cp56Time2a.FromDateTime(now);

    var list = new List<Asdu>
    {
        Asdu.Identification(ca, privateRange: false, "XJ-PROT1"),

        Asdu.TimeTaggedMessage(ca, fun, 1, DpiQuality.FromDpi(1), cp56, CotCodes.Spontaneous),
        Asdu.TimeTaggedMessage(ca, fun, 2, new DpiQuality(2, Invalid: true), cp56, CotCodes.Spontaneous),

        Asdu.ProtectionEvent(ca, fun, 3, DpiQuality.FromDpi(2), relativeTimeMs: 235, fan: 12, cp56, CotCodes.Spontaneous),

        Asdu.Measurement(ca, fun, 10, 1234),
        Asdu.Measurement(ca, fun, 11, -567),

        Asdu.GenericDataAsdu(ca, rii: 1, continuation: false, CotCodes.GenericRead, new[]
        {
            GenericDataSet.Ascii(new Gin(3, 1), KodCodes.Description, "CBZ-8000"),
            GenericDataSet.Float(new Gin(4, 2), KodCodes.ActualValue, 12.5f),
        }),

        Asdu.GeneralCommand(ca, fun, 20, dco: 0x02, rii: 7),
    };

    var record = new DisturbanceRecord { Fan = 1, FaultTime = now };
    var record2 = new DisturbanceRecord { Fan = 2, FaultTime = now.AddMinutes(-5) };
    list.Add(DisturbanceAsdu.DisturbanceList(ca, new[] { record, record2 }));

    list.Add(Asdu.ClockSyncEcho(cp56, ca));

    return list;
}

// ══════════════════════════════════════════════════════════════════
//  打印
// ══════════════════════════════════════════════════════════════════

static string Format(Iec103DecodedAsdu decoded) => decoded switch
{
    Iec103MonitorMessage m =>
        $"{m.Header.Describe()}\n    {string.Join("\n    ", m.Points.Select(DescribePoint))}",

    Iec103ProtectionEventMessage p =>
        $"{p.Header.Describe()}\n    品质={DescribeQuality(p.Quality)} 相对时标={p.RelativeTimeMs}ms "
        + $"故障序号={p.Fan} 时标={p.Time}",

    Iec103IdentificationMessage i =>
        $"{i.Header.Describe()}\n    标识=\"{i.Text}\"{(i.PrivateRange ? "（专用范围）" : string.Empty)}",

    Iec103ClockSyncMessage c =>
        $"{c.Header.Describe()}\n    时钟={c.Time}",

    Iec103InterrogationMessage q =>
        $"{q.Header.Describe()}\n    {(q.IsEnd ? "总召唤终止" : "总召唤启动")} SCN={q.Scn}",

    Iec103MeasurandMessage v =>
        $"{v.Header.Describe()}\n    测量值=[{string.Join(", ", v.Values)}]",

    Iec103GenericDataMessage g =>
        $"{g.Header.Describe()}\n    RII={g.Rii} 数据集 {g.Sets.Count} 条\n    "
        + string.Join("\n    ", g.Sets.Select(DescribeSet)),

    Iec103GeneralCommandMessage c =>
        $"{c.Header.Describe()}\n    DCT={c.Dct}（1 分 / 2 合） QU={c.Qu} RII={c.Rii}",

    Iec103DisturbanceListMessage d =>
        $"{d.Header.Describe()}\n    "
        + string.Join("\n    ", d.Entries.Select(e => $"FAN={e.Fan} {e.Status} 故障时刻={e.FaultTime:yyyy-MM-dd HH:mm:ss.fff}")),

    Iec103ChannelDataMessage cd =>
        $"{cd.Header.Describe()}\n    FAN={cd.Segment.Fan} 通道={cd.Segment.Acc} "
        + $"起始={cd.Segment.FirstElement} 点数={cd.Segment.Values.Length}",

    _ => decoded.Header.Describe(),
};

static string DescribePoint(Iec103MonitorPoint p)
{
    var time = p.Time is { } t ? $"{t}" : "无时标";
    var extra = p.Extra.Length > 0 ? $" 附加={Convert.ToHexString(p.Extra)}" : string.Empty;
    return $"INF={p.Inf} {p.Quality.State} 品质={DescribeQuality(p.Quality)} 时标={time}{extra}";
}

static string DescribeQuality(DpiQuality q)
    => $"IV={q.Invalid} NT={q.NonTopical} SB={q.Substituted} BL={q.Blocked}";

static string DescribeSet(GenericDataSet set)
    => $"GIN={set.Gin.Group:D2}/{set.Gin.Entry:D2} KOD={set.Kod}({KodCodes.Describe(set.Kod)}) "
        + $"GDD={set.Gdd.DataType}({GddTypes.Describe(set.Gdd.DataType)}) "
        + $"值=[{string.Join(", ", set.DecodeValues())}]";

static string DescribeControl(byte control)
{
    var lc = LinkControl.FromByte(control);
    return lc.IsPrimary
        ? $"主站 {(PrimaryFunction)lc.Function} FCB={lc.FcbOrAcd} FCV={lc.FcvOrDfc}"
        : $"从站 {(SecondaryFunction)lc.Function} ACD={lc.FcbOrAcd} DFC={lc.FcvOrDfc}";
}

static byte[] ParseHex(string text)
{
    var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
    if (clean.Length % 2 != 0)
    {
        throw new FormatException($"十六进制长度必须为偶数：{clean.Length}");
    }

    return Convert.FromHexString(clean);
}

static string? TryGetArg(string[] argv, string name)
{
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
}
