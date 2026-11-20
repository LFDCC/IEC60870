// 许继 103 over 104 全操作验收工具
//
// 用库内的许继私有实现，逐项执行许继 103 模拟器包含的全部操作，
// 校验应答并记录收发报文，用于与该模拟器行为比对。
//
// 用法：
//   dotnet run -- 2403             # 连到许继模拟器（或我们的从站）
//   dotnet run -- 2403 --verbose   # 逐帧打印收发

using System.Text;
using IEC60870.CS103;
using cs103_private_ioa.Xuji;

Console.OutputEncoding = Encoding.UTF8;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 2403;
var verbose = args.Contains("--verbose");

var ca = new XujiCa(sector: 1, device: 1).ToCommonAddress();
const byte Fun = 178;

var txFrames = new List<string>();
var rxFrames = new List<string>();
var traceLock = new object();

var options = new Iec103Options()
    .UseLinkParameters(l => l.FramingCodec = new XujiApduCodec());

await using var client = new Iec103Client("127.0.0.1", port, options);
XujiPrivateTypes.RegisterAll(client.PrivateTypes);

client.RawFrameSent += raw =>
{
    var hex = Convert.ToHexString(raw);
    lock (traceLock) { txFrames.Add(hex); }
    if (verbose) Console.WriteLine($"  TX {hex}");
};
client.RawFrameReceived += raw =>
{
    var hex = Convert.ToHexString(raw);
    lock (traceLock) { rxFrames.Add(hex); }
    if (verbose) Console.WriteLine($"  RX {hex}");
};

// 入站 ASDU 旁路镜像；ref struct 不能跨回调留存，收到即物化
var inbox = System.Threading.Channels.Channel.CreateUnbounded<Asdu>();
client.AsduReceived += (in Iec103AsduView v) =>
{
    try
    {
        inbox.Writer.TryWrite(Asdu.Materialize(v));
    }
    catch
    {
        // 解析失败不影响后续
    }
};

var results = new List<(string Name, bool Ok, string Detail)>();

Console.WriteLine($"连接 127.0.0.1:{port} ...");
await client.ConnectAsync();
Console.WriteLine($"已连接。CA=0x{ca:X4} FUN={Fun}");
Console.WriteLine();

try
{
    await RunAsync();
}
finally
{
    await client.DisconnectAsync();
    PrintSummary();
}

// ══════════════════════════════════════════════════════════════════
//  各操作
// ══════════════════════════════════════════════════════════════════

async Task RunAsync()
{
    await Step("调版本号 (TI=60 → TI=5)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            XujiTypeIds.VersionRequest, true, 1, CotCodes.CommandAck, false,
            ca, Fun, 6, Array.Empty<byte>(), XujiApduLayout.Instance));

        var r = await ExpectAsync(XujiTypeIds.VersionResponse, 3000);
        if (r is null) return (false, "未收到 TI=5 版本应答");

        var info = (XujiVersionInfo)XujiVersionHandler.Parse(
            new Iec103AsduView(r.Encode(), XujiApduLayout.Instance));
        return (true, $"型号={info.Model} 版本={info.VersionText} CRC=0x{info.RomCrc:X4} COT={r.Cot}");
    });

    await Step("总召唤 (TI=7)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.GeneralInterrogation, true, 1, CotCodes.CommandAck, false,
            ca, 255, 0, new byte[] { 0 }, XujiApduLayout.Instance));

        var collected = await CollectUntilAsync(TypeIds.GeneralInterrogationEnd, 4000);
        if (collected.Count == 0) return (false, "未收到任何总召应答帧");

        var yiXin = collected.Count(a => a.TypeId == TypeIds.TimeTaggedMessage);
        var yaoCe = collected.Count(a => a.TypeId == TypeIds.Measurand);
        var hasEnd = collected.Any(a => a.TypeId == TypeIds.GeneralInterrogationEnd);
        var giCot = collected.Where(a => a.TypeId != TypeIds.GeneralInterrogationEnd)
                             .All(a => a.Cot == CotCodes.GiStart);

        return (hasEnd, $"遥信 {yiXin} 帧、遥测 {yaoCe} 帧、终止帧 {(hasEnd ? "有" : "无")}、数据帧 COT=9 {(giCot ? "✓" : "✗")}");
    });

    await Step("时间同步 (TI=6)", async () =>
    {
        var now = DateTime.Now;
        await client.SendAsduAsync(new Asdu(
            TypeIds.ClockSync, true, 1, CotCodes.ClockSync, false,
            ca, 255, 0, Cp56Time2a.FromDateTime(now).Encode(), XujiApduLayout.Instance));

        await Task.Delay(300);
        return (true, $"已发送 {now:yyyy-MM-dd HH:mm:ss}（规约未要求应答）");
    });

    await Step("调定值 - 整区读 (TI=61 INF=100)", async () =>
    {
        var settings = new SettingClient(client, ca, Fun);
        var values = await settings.ReadAsync(cpu: 1, zone: 15, timeoutMs: 5000);
        if (values.Length == 0) return (false, "未读到定值");

        var sample = string.Join(", ", values.Take(4).Select(v => v.Raw));
        return (true, $"读到 {values.Length} 项，前 4 项=[{sample}]");
    });

    await Step("单项定值修改 - 预发/执行 (INF=107/109)", async () =>
    {
        var settings = new SettingClient(client, ca, Fun);
        var before = await settings.ReadAsync(cpu: 1, zone: 15, timeoutMs: 5000);
        if (before.Length < 3) return (false, "定值项不足 3 项");

        const int idx = 2;
        var oldRaw = before[idx].Raw;
        await settings.WriteSingleAsync(cpu: 1, zone: 15, index: idx, value: XujiSettingValue.Create(4321));

        var after = await settings.ReadAsync(cpu: 1, zone: 15, timeoutMs: 5000);
        var ok = after.Length > idx && after[idx].Raw == 4321;
        return (ok, ok
            ? $"第 {idx + 1} 项 {oldRaw} → 4321 ✓"
            : $"第 {idx + 1} 项为 {(after.Length > idx ? after[idx].Raw.ToString() : "越界")}（期望 4321）");
    });

    await Step("一般命令 - 信号复归 (TI=20)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.GeneralCommand, true, 1, CotCodes.CommandAck, false,
            ca, Fun, 31, new byte[] { 0x82, 0x00 }, XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.TimeTaggedMessage, 3000);
        return r is null
            ? (false, "未收到一般命令确认")
            : (true, $"TI=1 确认，COT={r.Cot} 体长={r.Data.Length}");
    });

    await Step("定值区切换 (TI=20 INF=101)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.GeneralCommand, true, 1, CotCodes.CommandAck, false,
            ca, Fun, 101, new byte[] { 0x01, 0x00 }, XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.TimeTaggedMessage, 3000);
        return r is null
            ? (false, "未收到区切换确认")
            : (true, $"TI=1 确认，COT={r.Cot}");
    });

    await Step("查当前定值区号 (TI=62)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            XujiTypeIds.FaultReportAck, true, 1, CotCodes.CommandAck, false,
            ca, Fun, 6, Array.Empty<byte>(), XujiApduLayout.Instance));

        var r = await ExpectAsync(XujiTypeIds.FaultReportAck, 3000);
        return r is null
            ? (false, "未收到区号应答")
            : (true, $"当前区号={(r.Data.Length > 0 ? r.Data[0] : -1)}");
    });

    await Step("电度冻结 (TI=88)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            XujiTypeIds.EnergyFreeze, true, 1, XujiCot.EnergyData, false,
            ca, 1, 0, new byte[] { 0x45, 0x00 }, XujiApduLayout.Instance));

        var r = await ExpectAsync(XujiTypeIds.EnergyFreeze, 3000);
        return r is null
            ? (false, "未收到冻结确认")
            : (true, $"COT={r.Cot} 体长={r.Data.Length}");
    });

    await Step("遥控 - 选择 (TI=64)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            XujiTypeIds.BreakerControl, true, 1, XujiCot.ControlCommand, false,
            ca, Fun, 1, new byte[] { 0x82, 0x01 }, XujiApduLayout.Instance));

        var r = await ExpectAsync(XujiTypeIds.BreakerControl, 3000);
        return r is null
            ? (false, "未收到遥控镜像帧")
            : (true, $"TI=64 镜像，COT={r.Cot}");
    });

    await Step("通用分类 - 读全目录 (TI=21)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 0,
            new byte[] { 0x01, 0x01, 0x00, 0x00, KodCodes.Description },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到通用分类应答");

        var (count, _) = NgdCodes.Decode(r.Data[1]);
        return (count > 0, $"NGD={count} COT={r.Cot}");
    });

    await Step("通用分类 - 读组条目 (TI=21 GIN=0E00)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 0,
            new byte[] { 0x02, 0x01, 0x0E, 0x00, KodCodes.ActualValue },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到应答");

        var (count, _) = NgdCodes.Decode(r.Data[1]);
        return (true, $"NGD={count} COT={r.Cot}" + (count == 0 ? "（组未定义，空应答）" : ""));
    });
}

// ── 辅助 ──────────────────────────────────────────────────────────

async Task Step(string name, Func<Task<(bool Ok, string Detail)>> action)
{
    Console.Write($"[..] {name} ");
    try
    {
        var (ok, detail) = await action();
        Console.WriteLine(ok ? "✅" : "❌");
        Console.WriteLine($"     {detail}");
        results.Add((name, ok, detail));
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ 异常");
        Console.WriteLine($"     {ex.GetType().Name}: {ex.Message}");
        results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
    }
}

async Task<Asdu?> ExpectAsync(byte typeId, int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
        try
        {
            var v = await inbox.Reader.ReadAsync(cts.Token);
            if (v.TypeId == typeId) return v;
        }
        catch (OperationCanceledException) { break; }
    }
    return null;
}

async Task<List<Asdu>> CollectUntilAsync(byte endTypeId, int timeoutMs)
{
    var list = new List<Asdu>();
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
        try
        {
            var v = await inbox.Reader.ReadAsync(cts.Token);
            list.Add(v);
            if (v.TypeId == endTypeId) return list;
        }
        catch (OperationCanceledException) { break; }
    }
    return list;
}

void PrintSummary()
{
    Console.WriteLine();
    Console.WriteLine("══════════════════════════════════════════════");
    var pass = results.Count(r => r.Ok);
    Console.WriteLine($"  通过 {pass}/{results.Count}");
    foreach (var (name, ok, detail) in results.Where(r => !r.Ok))
        Console.WriteLine($"  ❌ {name}: {detail}");
    Console.WriteLine("══════════════════════════════════════════════");

    lock (traceLock)
    {
        Console.WriteLine($"\n收发统计: TX {txFrames.Count} 帧 / RX {rxFrames.Count} 帧");

        var traceFile = Path.Combine(AppContext.BaseDirectory,
            $"client-trace-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            var lines = new List<string>
            {
                "# 客户端收发记录（TX = 主站发出，RX = 主站收到）",
                $"# 目标 127.0.0.1:{port}",
            };
            var max = Math.Max(txFrames.Count, rxFrames.Count);
            for (var i = 0; i < max; i++)
            {
                if (i < txFrames.Count) lines.Add($"TX {txFrames[i]}");
                if (i < rxFrames.Count) lines.Add($"RX {rxFrames[i]}");
            }
            File.WriteAllLines(traceFile, lines);
            Console.WriteLine($"记录已写入 {traceFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写记录失败: {ex.Message}");
        }
    }
}
