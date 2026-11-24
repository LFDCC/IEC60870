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

    // ── 私有 INF（工具实测用到，规约正文未定义）──────────────────
    // 信息体结构与 INF=0 完全相同，只是 INF 字节不同；应答必须回显该 INF。

    await Step("通用分类 - 读所有组标题 (TI=21 INF=240)", async () =>
    {
        // 工具实测帧：15 81 2A 00 01 01 FE F0 | 00 00
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 240,
            new byte[] { 0x00, 0x00 },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到应答");
        if (r.Inf != 240) return (false, $"INF 未回显：期望 240，实际 {r.Inf}");

        var (count, _) = NgdCodes.Decode(r.Data[1]);
        return (count > 0, $"NGD={count} COT={r.Cot} INF={r.Inf}");
    });

    await Step("通用分类 - 读所有组标题 (TI=21 INF=245)", async () =>
    {
        // 工具实测帧：15 81 2A 00 01 01 FE F5 | 01 00
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 245,
            new byte[] { 0x01, 0x00 },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到应答");
        if (r.Inf != 245) return (false, $"INF 未回显：期望 245，实际 {r.Inf}");

        var (count, _) = NgdCodes.Decode(r.Data[1]);
        return (count > 0, $"NGD={count} COT={r.Cot} INF={r.Inf}");
    });

    await Step("通用分类 - 读组条目 (TI=21 INF=241)", async () =>
    {
        // 工具实测帧：15 81 2A 00 01 01 FE F1 | 01 01 0E 00 0A
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 241,
            new byte[] { 0x01, 0x01, 0x0E, 0x00, KodCodes.Description },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到应答");
        if (r.Inf != 241) return (false, $"INF 未回显：期望 241，实际 {r.Inf}");

        var (count, _) = NgdCodes.Decode(r.Data[1]);
        var ok = r.Cot == CotCodes.GenericRead && count > 0;
        return (ok, $"NGD={count} COT={r.Cot} INF={r.Inf}");
    });

    await Step("通用分类 - 读条目属性 (TI=21 INF=244)", async () =>
    {
        // 工具实测帧：15 81 2A 00 01 01 FE F4 | 01 01 03 01 01
        // 组 3 未定义 → 应回 COT=43（读响应无效），且 INF 回显 244
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 244,
            new byte[] { 0x01, 0x01, 0x03, 0x01, KodCodes.ActualValue },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "未收到应答");
        if (r.Inf != 244) return (false, $"INF 未回显：期望 244，实际 {r.Inf}");

        return (r.Cot == CotCodes.GenericReadInvalid,
            $"COT={r.Cot}（43=读无效，组未定义时正确）INF={r.Inf}");
    });

    // ── TI=10 写条目三步（INF=249 预发 / 250 执行 / 251 中止）────────

    await Step("通用分类写 - 预发+执行 (INF=249→250)", async () =>
    {
        const byte grp = 0x12, entry = 2;

        // 249 预发：把 0x12.02 改为 2（合）
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericData, true, 1, CotCodes.GenericWrite, false,
            ca, Asdu.GenericFun, 249,
            WriteBody(rii: 0x00, grp, entry, value: new byte[] { 0x02 }),
            XujiApduLayout.Instance));

        var ack1 = await ExpectAsync(TypeIds.GenericData, 3000);
        if (ack1 is null) return (false, "预发未收到确认");
        if (ack1.Inf != 249) return (false, $"预发确认 INF 未回显：{ack1.Inf}");

        // 250 执行
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericData, true, 1, CotCodes.GenericWrite, false,
            ca, Asdu.GenericFun, 250,
            new byte[] { 0x00, 0x01, grp, entry, 0x01, GddTypes.DoublePoint, 0x01, 0x01, 0x02 },
            XujiApduLayout.Instance));

        var ack2 = await ExpectAsync(TypeIds.GenericData, 3000);
        if (ack2 is null) return (false, "执行未收到确认");
        if (ack2.Inf != 250) return (false, $"执行确认 INF 未回显：{ack2.Inf}");

        // 回读验证
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 0,
            new byte[] { 0x03, 0x01, grp, entry, KodCodes.ActualValue },
            XujiApduLayout.Instance));

        var rd = await ExpectAsync(TypeIds.GenericData, 3000);
        if (rd is null) return (false, "回读未收到应答");

        // Data = RII(1) + NGD(1) + GIN(2) + KOD(1) + GDD(3) + GID(n)
        // GID 从下标 8 开始
        var gid = rd.Data.Length >= 9 ? rd.Data[8] : (byte)0xFF;
        return (gid == 2, $"回读 0x12.{entry} = {gid}（期望 2）");
    });

    await Step("通用分类写 - 中止 (INF=251)", async () =>
    {
        // 251 中止：不带数据集，NGD=0
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericData, true, 1, CotCodes.GenericWrite, false,
            ca, Asdu.GenericFun, 251,
            new byte[] { 0x00, 0x00 },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.GenericData, 3000);
        if (r is null) return (false, "中止未收到确认");
        if (r.Inf != 251) return (false, $"INF 未回显：期望 251，实际 {r.Inf}");

        return (r.Cot == CotCodes.GenericWrite, $"COT={r.Cot} INF={r.Inf}");
    });

    // ── 多装置/多扇区：同一从站下挂两个 CA ──────────────────────────

    await Step("多装置 - 装置A(CA=0x0101) 调定值", async () =>
    {
        var settings = new SettingClient(client, ca, Fun);
        var values = await settings.ReadAsync(cpu: 1, zone: 0, timeoutMs: 5000);
        if (values.Length == 0) return (false, "未读到定值");

        // 装置 A 的区 0 是 i*10；装置 B 是 i*7，用第 2 项区分
        var v1 = values.Length > 1 ? values[1].Raw : -1;
        return (v1 == 10, $"读到 {values.Length} 项，第2项={v1}（期望 10 = 装置A）");
    });

    await Step("多装置 - 装置B(CA=0x0201) 调定值", async () =>
    {
        var caB = new XujiCa(sector: 1, device: 2).ToCommonAddress();
        var settingsB = new SettingClient(client, caB, Fun);
        var values = await settingsB.ReadAsync(cpu: 1, zone: 0, timeoutMs: 5000);
        if (values.Length == 0) return (false, "未读到定值");

        var v1 = values.Length > 1 ? values[1].Raw : -1;
        var ok = values.Length == 10 && v1 == 7;
        return (ok, $"读到 {values.Length} 项，第2项={v1}（期望 10 项、第2项=7 = 装置B）");
    });

    await Step("多装置 - 两装置数据互不干扰", async () =>
    {
        // 写装置 B 第 1 项，装置 A 不应变化
        var caB = new XujiCa(sector: 1, device: 2).ToCommonAddress();
        var settingsA = new SettingClient(client, ca, Fun);
        var settingsB = new SettingClient(client, caB, Fun);

        var beforeA = await settingsA.ReadAsync(cpu: 1, zone: 0, timeoutMs: 5000);
        await settingsB.WriteSingleAsync(cpu: 1, zone: 0, index: 0,
            XujiSettingValue.Create(7777));
        var afterA = await settingsA.ReadAsync(cpu: 1, zone: 0, timeoutMs: 5000);
        var afterB = await settingsB.ReadAsync(cpu: 1, zone: 0, timeoutMs: 5000);

        var aUnchanged = beforeA[0].Raw == afterA[0].Raw;
        var bChanged = afterB[0].Raw == 7777;
        return (aUnchanged && bChanged,
            $"装置A首项 {beforeA[0].Raw}→{afterA[0].Raw}（应不变），装置B首项→{afterB[0].Raw}（应为 7777）");
    });

    await Step("多装置 - 装置B 定值区不存在回否定", async () =>
    {
        var caB = new XujiCa(sector: 1, device: 2).ToCommonAddress();
        var settingsB = new SettingClient(client, caB, Fun);

        try
        {
            await settingsB.ReadAsync(cpu: 1, zone: 3, timeoutMs: 2000);
            return (false, "装置B 不存在的区 3 竟然读成功了");
        }
        catch (InvalidOperationException)
        {
            return (true, "正确回否定（COT=21），装置B 只有区 0~1");
        }
    });

    await Step("多装置 - 通用分类按 CA 隔离", async () =>
    {
        var caB = new XujiCa(sector: 1, device: 2).ToCommonAddress();

        // 读装置 A 的组 0x0E 条目数（7 条 + 标题）
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            ca, Asdu.GenericFun, 0,
            new byte[] { 0x01, 0x01, 0x0E, 0x00, KodCodes.ActualValue },
            XujiApduLayout.Instance));
        var a = await ExpectAsync(TypeIds.GenericData, 3000, ca);
        if (a is null) return (false, "装置A 未应答");

        // 读装置 B 的组 0x0E 条目数（只有 1 条）
        await client.SendAsduAsync(new Asdu(
            TypeIds.GenericCommand, true, 1, CotCodes.GenericRead, false,
            caB, Asdu.GenericFun, 0,
            new byte[] { 0x02, 0x01, 0x0E, 0x00, KodCodes.ActualValue },
            XujiApduLayout.Instance));
        var b = await ExpectAsync(TypeIds.GenericData, 3000, caB);
        if (b is null) return (false, "装置B 未应答");

        var (na, _) = NgdCodes.Decode(a.Data[1]);
        var (nb, _) = NgdCodes.Decode(b.Data[1]);
        return (na != nb && nb == 1,
            $"装置A 组0x0E NGD={na}，装置B NGD={nb}（应不同，B 只有 1 条）");
    });

    // ── 故障录波完整流程（TI=24/26/30/31）──────────────────────────

    await Step("录波 - 选择故障 (TI=24 TOO=1 → TI=26)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 1, 1, 0x00, 0x00, 0x00 },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.DisturbanceReady, 3000, ca);
        if (r is null) return (false, "未收到 TI=26 数据准备好");

        // 未用(1) TOV(1) FAN(2) NOF(2) NOC(1) NOE(2) INT(2) 时标(4)
        var noc = r.Data[6];
        var noe = r.Data[7] | (r.Data[8] << 8);
        var intv = r.Data[9] | (r.Data[10] << 8);
        var ok = noc == 2 && noe == 50 && intv == 1000;
        return (ok, $"NOC={noc} 路 NOE={noe} 点 INT={intv}μs（期望 2/50/1000）");
    });

    await Step("录波 - 请求 A 相电流通道 (TOO=8 → TI=30/31)", async () =>
    {
        // 先选择故障，再请求通道 1
        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 1, 1, 0x00, 0x00, 0x00 },
            XujiApduLayout.Instance));
        _ = await ExpectAsync(TypeIds.DisturbanceReady, 3000, ca);

        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 8, 1, 0x00, 0x00, 0x01 },
            XujiApduLayout.Instance));

        var frames = await CollectUntilAsync(TypeIds.ChannelEnd, 4000);
        var dataFrames = frames.Count(a => a.TypeId == TypeIds.ChannelData);
        var hasEnd = frames.Any(a => a.TypeId == TypeIds.ChannelEnd);

        // 累计采样点数
        var totalPoints = 0;
        var firstValue = (short)0;
        var seen = false;
        foreach (var f in frames.Where(a => a.TypeId == TypeIds.ChannelData))
        {
            var ndv = f.Data[6];
            totalPoints += ndv;
            if (!seen && ndv > 0)
            {
                firstValue = (short)(f.Data[9] | (f.Data[10] << 8));
                seen = true;
            }
        }

        // 50 点 / 每帧 25 点 = 2 帧
        var ok = dataFrames == 2 && totalPoints == 50 && hasEnd;
        return (ok, $"{dataFrames} 数据帧、共 {totalPoints} 点、结束帧 {(hasEnd ? "有" : "无")}" +
                    (seen ? $"、首点={firstValue}" : ""));
    });

    await Step("录波 - 请求 A 相电压通道 (TOO=8 ACC=5)", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 1, 1, 0x00, 0x00, 0x00 },
            XujiApduLayout.Instance));
        _ = await ExpectAsync(TypeIds.DisturbanceReady, 3000, ca);

        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 8, 1, 0x00, 0x00, 0x05 },
            XujiApduLayout.Instance));

        var frames = await CollectUntilAsync(TypeIds.ChannelEnd, 4000);
        var totalPoints = frames.Where(a => a.TypeId == TypeIds.ChannelData)
                                .Sum(f => (int)f.Data[6]);

        return (totalPoints == 50, $"ACC=5 通道共 {totalPoints} 点（期望 50）");
    });

    await Step("录波 - 不存在的通道回结束帧", async () =>
    {
        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 1, 1, 0x00, 0x00, 0x00 },
            XujiApduLayout.Instance));
        _ = await ExpectAsync(TypeIds.DisturbanceReady, 3000, ca);

        // ACC=9 不存在
        await client.SendAsduAsync(new Asdu(
            TypeIds.DisturbanceCommand, true, 1, CotCodes.DisturbanceTransfer, false,
            ca, 255, 0, new byte[] { 8, 1, 0x00, 0x00, 0x09 },
            XujiApduLayout.Instance));

        var r = await ExpectAsync(TypeIds.ChannelEnd, 3000, ca);
        if (r is null) return (false, "未收到结束帧");
        return (r.Data[0] == 35, $"正确回本路结束 TOO={r.Data[0]}（35=本路模拟量发送结束）");
    });
}

/// <summary>构造 TI=10 写条目信息体：RII + NGD=1 + GIN(2) KOD(1) GDD(3) GID(n)。</summary>
static byte[] WriteBody(byte rii, byte grp, byte entry, byte[] value)
{
    var body = new byte[2 + 6 + value.Length];
    body[0] = rii;
    body[1] = NgdCodes.Encode(1, false);
    body[2] = grp;
    body[3] = entry;
    body[4] = KodCodes.ActualValue;
    body[5] = GddTypes.DoublePoint;
    body[6] = 1;
    body[7] = 1;
    value.CopyTo(body, 8);
    return body;
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

async Task<Asdu?> ExpectAsync(byte typeId, int timeoutMs, int? expectCa = null)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
        try
        {
            var v = await inbox.Reader.ReadAsync(cts.Token);
            if (v.TypeId != typeId) continue;
            if (expectCa is int want && v.Ca != want) continue;
            return v;
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
