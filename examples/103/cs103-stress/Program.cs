// IEC 60870-5-103 压力/性能测试工具
//
// 拓扑：N 个「装置从站」（TCP，端口 BasePort+i）+ N 个主站，各自独立连接。
// 每个主站循环做三件事，从站同时后台主动上送，用来压满解码/分派/链路状态机：
//   · 召唤 1 级数据（突发上送，主路径）
//   · 召唤 2 级数据（周期量测）
//   · 隔一段时间发一次一般命令 + 总召唤（测命令 RTT 与 GI RTT）
//
// 用法：
//   dotnet run -- -c Release                          # 默认 100 装置 / 30 秒
//   dotnet run -- -c Release -- --devices 200 --duration 60
//   dotnet run -- -c Release -- --burst               # 从站对每次 1 级召唤都回 points 帧（吞吐上限）
//   dotnet run -- -c Release -- --json results.json
//
// 说明：103 是主从式、无 APCI，所以没有 104 的 k/w 窗口——吞吐上限由
// 「一次召唤能带走多少帧（guard 上限 4096）」和链路往返决定，本工具正是量这个。

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IEC60870.CS103;

var cfg = StressConfig.Parse(args);
if (cfg.Help)
{
    StressConfig.PrintHelp();
    return;
}

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("IEC 60870-5-103 压力测试");
Console.WriteLine($"  装置数        : {cfg.DeviceCount}");
Console.WriteLine($"  基准端口      : {cfg.BasePort}");
Console.WriteLine($"  测量时长      : {cfg.DurationSeconds}s");
Console.WriteLine($"  建链速率      : {cfg.RampUpPerSecond}/s");
Console.WriteLine($"  主动上送      : {cfg.SpontaneousPerSecondPerDevice}/s/装置");
Console.WriteLine($"  1 级召唤间隔  : {cfg.Class1IntervalMs}ms");
Console.WriteLine($"  命令间隔      : {cfg.CommandIntervalMs}ms");
Console.WriteLine($"  总召唤间隔    : {cfg.GiIntervalMs}ms");
Console.WriteLine($"  burst 模式    : {(cfg.Burst ? "开（每次 1 级召唤回 " + cfg.BurstPoints + " 帧）" : "关")}");
Console.WriteLine();

var metrics = new Metrics();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var devices = new List<DeviceRuntime>(cfg.DeviceCount);
var clients = new List<Task>(cfg.DeviceCount);

try
{
    // ── 1. 起从站 ─────────────────────────────────────────────
    var startedAt = Stopwatch.StartNew();
    for (var i = 0; i < cfg.DeviceCount; i++)
    {
        var port = cfg.BasePort + i;
        var device = new DeviceRuntime(port, (byte)1, cfg, metrics);
        await device.StartAsync(cts.Token);
        devices.Add(device);

        if ((i + 1) % Math.Max(1, cfg.RampUpPerSecond) == 0)
        {
            await Task.Delay(1000, cts.Token);
        }
    }

    Console.WriteLine($"从站全部就绪（{startedAt.Elapsed.TotalSeconds:F1}s），开始建链…\n");

    // ── 2. 起主站 ─────────────────────────────────────────────
    foreach (var device in devices)
    {
        clients.Add(RunMasterAsync(device.Port, cfg, metrics, cts.Token));
    }

    // ── 3. 稳态测量 ───────────────────────────────────────────
    var measurementStart = Stopwatch.StartNew();
    var baselineAllocated = GC.GetTotalAllocatedBytes(precise: true);
    var baselineGc0 = GC.CollectionCount(0);
    var baselineGc1 = GC.CollectionCount(1);
    var baselineGc2 = GC.CollectionCount(2);
    int baselineHandles, baselineThreads;
    using (var self = Process.GetCurrentProcess())
    {
        baselineHandles = self.HandleCount;
        baselineThreads = self.Threads.Count;
    }

    while (measurementStart.Elapsed.TotalSeconds < cfg.DurationSeconds && !cts.IsCancellationRequested)
    {
        await Task.Delay(cfg.Verbose ? 1000 : 5000, cts.Token).ContinueWith(_ => { });

        if (cfg.Verbose && !cts.IsCancellationRequested)
        {
            var elapsed = measurementStart.Elapsed.TotalSeconds;
            Console.WriteLine(
                $"[{elapsed,6:F1}s] 下行 {metrics.DownAsdu / elapsed,9:N0}/s  "
                + $"上行 {metrics.UpAsdu / elapsed,7:N0}/s  "
                + $"1级RTT {metrics.Class1AverageMs,6:F2}ms  "
                + $"命令RTT {metrics.CommandAverageMs,6:F2}ms  "
                + $"超时 {metrics.Timeouts,5}  错误 {metrics.Errors,3}");
        }

        if (cts.IsCancellationRequested)
        {
            break;
        }
    }

    var seconds = measurementStart.Elapsed.TotalSeconds;
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - baselineAllocated;

    cts.Cancel();
    await Task.WhenAll(clients.Select(t => t.ContinueWith(_ => { })));
    foreach (var device in devices)
    {
        await device.StopAsync();
    }

    // ── 4. 汇总 ───────────────────────────────────────────────
    var downPerSec = metrics.DownAsdu / seconds;
    var upPerSec = metrics.UpAsdu / seconds;

    Console.WriteLine();
    Console.WriteLine("══════════════ 结果 ══════════════");
    Console.WriteLine($"测量时长           : {seconds:F1}s");
    Console.WriteLine($"会话（从站侧/主站建链）: {metrics.ServerSessions}/{metrics.ClientConnected}  失败 {metrics.ConnectFailures}");
    Console.WriteLine($"下行 ASDU          : {metrics.DownAsdu:N0}  ({downPerSec:N0}/s)");
    Console.WriteLine($"上行 ASDU          : {metrics.UpAsdu:N0}  ({upPerSec:N0}/s)");
    Console.WriteLine($"下行字节           : {metrics.DownBytes / 1024.0 / 1024.0:F1} MiB  ({metrics.DownBytes / 1024.0 / seconds:F1} KiB/s)");
    Console.WriteLine($"合计 ASDU 吞吐     : {downPerSec + upPerSec:N0}/s");
    Console.WriteLine($"1 级召唤 RTT       : 平均 {metrics.Class1AverageMs:F2}ms  最大 {metrics.Class1MaxMs:F2}ms  (n={metrics.Class1Requests:N0})");
    Console.WriteLine($"总召唤 RTT         : 平均 {metrics.GiAverageMs:F2}ms  最大 {metrics.GiMaxMs:F2}ms  (n={metrics.GiRequests:N0})");
    Console.WriteLine($"命令 RTT           : 平均 {metrics.CommandAverageMs:F2}ms  最大 {metrics.CommandMaxMs:F2}ms  (n={metrics.Commands:N0})");
    Console.WriteLine($"超时/错误          : {metrics.Timeouts} / {metrics.Errors}");
    Console.WriteLine($"托管分配           : {allocated / 1024.0 / 1024.0:F1} MiB  "
        + $"(速率 {allocated / 1024.0 / 1024.0 / Math.Max(seconds, 1e-6):F1} MiB/s)");
    Console.WriteLine($"GC (0/1/2)         : {GC.CollectionCount(0) - baselineGc0}/"
        + $"{GC.CollectionCount(1) - baselineGc1}/{GC.CollectionCount(2) - baselineGc2}");
    Console.WriteLine($"进程工作集         : {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0:F0} MiB");
    using (var self = Process.GetCurrentProcess())
    {
        // 长跑泄漏判据：句柄/线程只增不回落，基本就是 Socket 或后台循环未释放
        Console.WriteLine($"进程句柄数 / 线程  : {self.HandleCount} / {self.Threads.Count}（起始 {baselineHandles} / {baselineThreads}）");
    }
    Console.WriteLine("═════════════════════════════════");

    if (cfg.JsonOut is { Length: > 0 })
    {
        var payload = new
        {
            devices = cfg.DeviceCount,
            seconds,
            burst = cfg.Burst,
            downAsduPerSec = downPerSec,
            upAsduPerSec = upPerSec,
            totalAsduPerSec = downPerSec + upPerSec,
            downBytesPerSec = metrics.DownBytes / seconds,
            serverSessions = metrics.ServerSessions,
            clientConnected = metrics.ClientConnected,
            connectFailures = metrics.ConnectFailures,
            class1Rtt = new { avgMs = metrics.Class1AverageMs, maxMs = metrics.Class1MaxMs, n = metrics.Class1Requests },
            giRtt = new { avgMs = metrics.GiAverageMs, maxMs = metrics.GiMaxMs, n = metrics.GiRequests },
            commandRtt = new { avgMs = metrics.CommandAverageMs, maxMs = metrics.CommandMaxMs, n = metrics.Commands },
            timeouts = metrics.Timeouts,
            errors = metrics.Errors,
            allocatedMiB = allocated / 1024.0 / 1024.0,
            bytesPerAsdu = (metrics.DownAsdu + metrics.UpAsdu) > 0
                ? allocated / (double)(metrics.DownAsdu + metrics.UpAsdu)
                : 0,
        };
        await File.WriteAllTextAsync(cfg.JsonOut, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"JSON 已写入 {cfg.JsonOut}");
    }
}
finally
{
    cts.Cancel();
    foreach (var device in devices)
    {
        await device.StopAsync();
    }
}

// ══════════════════════════════════════════════════════════════════
//  主站
// ══════════════════════════════════════════════════════════════════

static async Task RunMasterAsync(int port, StressConfig cfg, Metrics metrics, CancellationToken ct)
{
    await using var client = new Iec103Client("127.0.0.1", port);
    client.AsduReceived += (in Iec103AsduView asdu) => metrics.CountDown(asdu.Raw.Length);

    try
    {
        await client.ConnectAsync(ct);
        await client.ResetLinkAsync((byte)1, ct);
        metrics.IncClientConnected();
    }
    catch (Exception)
    {
        metrics.IncConnectFailure();
        return;
    }

    var lastCommand = Stopwatch.GetTimestamp();
    var lastGi = Stopwatch.GetTimestamp();

    while (!ct.IsCancellationRequested)
    {
        // ── 1 级召唤（主路径）──
        var start = Stopwatch.GetTimestamp();
        try
        {
            await client.RequestClass1Async(1, ct: ct);
            metrics.RecordClass1(ElapsedMs(start));
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception)
        {
            metrics.IncTimeout();
        }

        // ── 2 级召唤（周期量测）──
        try
        {
            await client.RequestClass2Async(1, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception)
        {
            metrics.IncError();
        }

        var now = Stopwatch.GetTimestamp();

        // ── 命令（测 RTT）──
        if (ElapsedMsBetween(lastCommand, now) >= cfg.CommandIntervalMs)
        {
            lastCommand = now;
            var cmdStart = Stopwatch.GetTimestamp();
            try
            {
                await client.SendCommandAsync(1, 160, 1, dco: 0x02, rii: 1, ct);
                metrics.RecordCommand(ElapsedMs(cmdStart));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                metrics.IncTimeout();
            }
        }

        // ── 总召唤（测 GI RTT）──
        if (ElapsedMsBetween(lastGi, now) >= cfg.GiIntervalMs)
        {
            lastGi = now;
            var giStart = Stopwatch.GetTimestamp();
            try
            {
                await client.GeneralInterrogationAsync(1, ct);
                metrics.RecordGi(ElapsedMs(giStart));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                metrics.IncTimeout();
            }
        }

        if (cfg.Class1IntervalMs > 0)
        {
            try
            {
                await Task.Delay(cfg.Class1IntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    try
    {
        await client.DisconnectAsync(CancellationToken.None);
    }
    catch
    {
        // 关闭期忽略
    }
}

static double ElapsedMs(long startTimestamp)
    => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

static double ElapsedMsBetween(long startTimestamp, long endTimestamp)
    => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;


// ══════════════════════════════════════════════════════════════════
//  从站（装置）
// ══════════════════════════════════════════════════════════════════

internal sealed class DeviceRuntime
{
    private readonly int _port;
    private readonly byte _ca;
    private readonly StressConfig _cfg;
    private readonly Metrics _metrics;
    private Iec103Server? _server;
    private readonly List<SessionPump> _pumps = new();
    private int _burstInf;

    public DeviceRuntime(int port, byte ca, StressConfig cfg, Metrics metrics)
    {
        _port = port;
        _ca = ca;
        _cfg = cfg;
        _metrics = metrics;
    }

    public int Port => _port;

    public async Task StartAsync(CancellationToken ct)
    {
        var options = new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.LocalLinkAddress = _ca;
                l.AcceptBroadcast = true;
                l.MessageTimeoutMs = 5000;
                l.CharacterTimeoutMs = 200;
            });

        _server = new Iec103Server(options);
        _server.SessionOpened += session =>
        {
            _metrics.IncServerSession();
            var pump = new SessionPump(session, _ca, _cfg, _metrics);
            lock (_pumps)
            {
                _pumps.Add(pump);
            }

            pump.Start(ct);
        };
        _server.SessionClosed += (session, _) =>
        {
            lock (_pumps)
            {
                _pumps.RemoveAll(p => p.Session == session);
            }
        };

        _server.FrameTraced += OnFrameTraced;

        // 上行统计 + 总召唤/命令应答
        _server.AsduReceived += (IIec103Session session, in Iec103AsduView asdu) =>
        {            _metrics.CountUp(asdu.Raw.Length);

            switch (asdu.TypeId)
            {
                case TypeIds.GeneralInterrogation:
                    // 总召唤：回 points 个带时标遥信 + 终止帧
                    var now = Cp56Time2a.FromDateTime(DateTime.Now);
                    for (var i = 0; i < _cfg.GiPoints; i++)
                    {
                        session.SendAsduAsync(Asdu.TimeTaggedMessage(
                            _ca, 160, (byte)(i & 0xFF), DpiQuality.FromDpi(2),
                            now, CotCodes.GiStart));
                    }

                    session.SendAsduAsync(Asdu.GeneralInterrogationEnd(_ca, 0));
                    break;

                case TypeIds.GeneralCommand:
                    // 一般命令：回一帧肯定的控后状态（TI=1）
                    session.SendAsduAsync(Asdu.TimeTaggedMessage(
                        _ca, asdu.Fun, asdu.Inf, DpiQuality.FromDpi(2),
                        Cp56Time2a.FromDateTime(DateTime.Now), CotCodes.CommandAck));
                    break;
            }
        };

        await _server.StartAsync(_port, ct);
    }

    /// <summary>
    /// burst 模式：主站每召唤一次 1 级数据，就补 BurstPoints 帧进 1 级队列。
    /// 这形成「一次问、一次答」的自计时闭环，用来量链路+解码的吞吐上限。
    /// </summary>
    private void OnFrameTraced(IIec103Session session, string direction, Ft12Frame frame)
    {
        if (!_cfg.Burst || direction != "RX")
        {
            return;
        }

        if (frame.Format != Ft12FrameFormat.FixedLength)
        {
            return;
        }

        var control = LinkControl.FromByte(frame.Control);
        if (!control.IsPrimary || control.Function != (byte)PrimaryFunction.RequestClass1)
        {
            return;
        }

        var now = Cp56Time2a.FromDateTime(DateTime.Now);
        for (var i = 0; i < _cfg.BurstPoints; i++)
        {
            var inf = (byte)(Interlocked.Increment(ref _burstInf) & 0xFF);
            session.SendAsduAsync(Asdu.Measurement(_ca, 160, inf, (short)(inf % 1000)));
        }
    }

    public async Task StopAsync()
    {
        List<SessionPump> pumps;
        lock (_pumps)
        {
            pumps = new List<SessionPump>(_pumps);
            _pumps.Clear();
        }

        foreach (var pump in pumps)
        {
            pump.Stop();
        }

        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
            _server = null;
        }
    }
}

/// <summary>单个会话的后台主动上送泵。</summary>
internal sealed class SessionPump
{
    private readonly IIec103Session _session;
    private readonly byte _ca;
    private readonly StressConfig _cfg;
    private readonly Metrics _metrics;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private int _inf;

    public SessionPump(IIec103Session session, byte ca, StressConfig cfg, Metrics metrics)
    {
        _session = session;
        _ca = ca;
        _cfg = cfg;
        _metrics = metrics;
    }

    public IIec103Session Session => _session;

    public void Start(CancellationToken outer)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _cts.Token);
        _task = Task.Run(() => RunAsync(linked.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 关闭期忽略
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_cfg.SpontaneousPerSecondPerDevice <= 0)
        {
            return;
        }

        var periodMs = (int)Math.Clamp(1000.0 / _cfg.SpontaneousPerSecondPerDevice, 1, 60_000);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = Cp56Time2a.FromDateTime(DateTime.Now);
            for (var i = 0; i < _cfg.PointsPerSpontaneous; i++)
            {
                var inf = (byte)(Interlocked.Increment(ref _inf) & 0xFF);
                _ = _session.SendAsduAsync(Asdu.TimeTaggedMessage(
                    _ca, 160, inf, DpiQuality.FromDpi(2), now, CotCodes.Spontaneous));
            }

            // 2 级：周期量测
            _ = _session.SendAsduAsync(Asdu.Measurement(_ca, 160, (byte)(_inf & 0xFF), (short)(_inf % 1000)));
            _metrics.AddQueued();
        }
    }
}
// ══════════════════════════════════════════════════════════════════
//  指标
// ══════════════════════════════════════════════════════════════════

internal sealed class Metrics
{
    private long _upAsdu;
    private long _upBytes;
    private long _downAsdu;
    private long _downBytes;
    private long _queued;
    private long _serverSessions;
    private long _clientConnected;
    private long _connectFailures;
    private long _timeouts;
    private long _errors;

    private long _class1Count;
    private long _class1TotalUs;
    private long _class1MaxUs;
    private long _giCount;
    private long _giTotalUs;
    private long _giMaxUs;
    private long _cmdCount;
    private long _cmdTotalUs;
    private long _cmdMaxUs;

    public long UpAsdu => Interlocked.Read(ref _upAsdu);
    public long UpBytes => Interlocked.Read(ref _upBytes);
    public long DownAsdu => Interlocked.Read(ref _downAsdu);
    public long DownBytes => Interlocked.Read(ref _downBytes);
    public long Queued => Interlocked.Read(ref _queued);
    public long ServerSessions => Interlocked.Read(ref _serverSessions);
    public long ClientConnected => Interlocked.Read(ref _clientConnected);
    public long ConnectFailures => Interlocked.Read(ref _connectFailures);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long Errors => Interlocked.Read(ref _errors);

    public long Class1Requests => Interlocked.Read(ref _class1Count);
    public long GiRequests => Interlocked.Read(ref _giCount);
    public long Commands => Interlocked.Read(ref _cmdCount);

    public double Class1AverageMs => Average(Class1Requests, _class1TotalUs);
    public double Class1MaxMs => Interlocked.Read(ref _class1MaxUs) / 1000.0;
    public double GiAverageMs => Average(GiRequests, _giTotalUs);
    public double GiMaxMs => Interlocked.Read(ref _giMaxUs) / 1000.0;
    public double CommandAverageMs => Average(Commands, _cmdTotalUs);
    public double CommandMaxMs => Interlocked.Read(ref _cmdMaxUs) / 1000.0;

    private static double Average(long count, long totalUs)
        => count > 0 ? Interlocked.Read(ref totalUs) / (double)count / 1000.0 : 0;

    public void CountUp(int bytes)
    {
        Interlocked.Increment(ref _upAsdu);
        Interlocked.Add(ref _upBytes, bytes);
    }

    public void CountDown(int bytes)
    {
        Interlocked.Increment(ref _downAsdu);
        Interlocked.Add(ref _downBytes, bytes);
    }

    public void AddQueued() => Interlocked.Increment(ref _queued);
    public void IncServerSession() => Interlocked.Increment(ref _serverSessions);
    public void IncClientConnected() => Interlocked.Increment(ref _clientConnected);
    public void IncConnectFailure() => Interlocked.Increment(ref _connectFailures);
    public void IncTimeout() => Interlocked.Increment(ref _timeouts);
    public void IncError() => Interlocked.Increment(ref _errors);

    public void RecordClass1(double ms) => Record(ref _class1Count, ref _class1TotalUs, ref _class1MaxUs, ms);
    public void RecordGi(double ms) => Record(ref _giCount, ref _giTotalUs, ref _giMaxUs, ms);
    public void RecordCommand(double ms) => Record(ref _cmdCount, ref _cmdTotalUs, ref _cmdMaxUs, ms);

    private static void Record(ref long count, ref long totalUs, ref long maxUs, double ms)
    {
        var us = (long)(ms * 1000);
        Interlocked.Increment(ref count);
        Interlocked.Add(ref totalUs, us);

        var current = Interlocked.Read(ref maxUs);
        while (us > current)
        {
            var previous = Interlocked.CompareExchange(ref maxUs, us, current);
            if (previous == current)
            {
                break;
            }

            current = previous;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  配置
// ══════════════════════════════════════════════════════════════════

internal sealed class StressConfig
{
    public int DeviceCount = 100;
    public int BasePort = 25500;
    public int DurationSeconds = 30;
    public int RampUpPerSecond = 25;
    public double SpontaneousPerSecondPerDevice = 4.0;
    public int PointsPerSpontaneous = 4;
    public int GiPoints = 8;
    public int Class1IntervalMs = 0;
    public int CommandIntervalMs = 500;
    public int GiIntervalMs = 5000;
    public bool Burst;
    public int BurstPoints = 16;
    public bool Verbose = true;
    public string? JsonOut;
    public bool Help;

    public static StressConfig Parse(string[] args)
    {
        var cfg = new StressConfig();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "--devices": cfg.DeviceCount = int.Parse(Next()!); break;
                case "--port": cfg.BasePort = int.Parse(Next()!); break;
                case "--duration": cfg.DurationSeconds = int.Parse(Next()!); break;
                case "--ramp": cfg.RampUpPerSecond = int.Parse(Next()!); break;
                case "--spont": cfg.SpontaneousPerSecondPerDevice = double.Parse(Next()!); break;
                case "--points": cfg.PointsPerSpontaneous = int.Parse(Next()!); break;
                case "--gi-points": cfg.GiPoints = int.Parse(Next()!); break;
                case "--class1-interval": cfg.Class1IntervalMs = int.Parse(Next()!); break;
                case "--cmd-interval": cfg.CommandIntervalMs = int.Parse(Next()!); break;
                case "--gi-interval": cfg.GiIntervalMs = int.Parse(Next()!); break;
                case "--burst": cfg.Burst = true; break;
                case "--burst-points": cfg.BurstPoints = int.Parse(Next()!); break;
                case "--quiet": cfg.Verbose = false; break;
                case "--json": cfg.JsonOut = Next(); break;
                case "-h":
                case "--help": cfg.Help = true; break;
            }
        }

        return cfg;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            "cs103-stress — IEC 60870-5-103 压力/性能测试\n"
            + "用法: cs103-stress [options]\n"
            + "  --devices N          装置数（默认 100）\n"
            + "  --port P             基准 TCP 端口（默认 25500）\n"
            + "  --duration S         测量秒数（默认 30）\n"
            + "  --ramp N             建链速率/s（默认 25）\n"
            + "  --spont X            每装置每秒主动上送轮次（默认 4）\n"
            + "  --points N           每轮主动上送的帧数（默认 4）\n"
            + "  --gi-points N        总召唤回送帧数（默认 8）\n"
            + "  --class1-interval M  1 级召唤之间的休眠毫秒（默认 0 = 不停）\n"
            + "  --cmd-interval M     命令间隔毫秒（默认 500）\n"
            + "  --gi-interval M      总召唤间隔毫秒（默认 5000）\n"
            + "  --burst              从站对每次 1 级召唤都回 --burst-points 帧\n"
            + "  --burst-points N     burst 模式每次回送的帧数（默认 16）\n"
            + "  --quiet              关闭每秒进度输出\n"
            + "  --json PATH          写出 JSON 结果\n");
    }
}
