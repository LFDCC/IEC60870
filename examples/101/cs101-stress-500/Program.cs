/*
 *  Program.cs — cs101-stress-500
 *
 *  IEC 60870-5-101 压力测试：模拟 N（默认 500）台装置。
 *
 *  与 104 的压测工具（examples/104/cs104-stress-500）刻意保持同一口径，
 *  便于横向对比：
 *    - 每台装置一个独立 TCP 隧道端口 + 一个独立主站，一一对应；
 *    - 装置按 --spont 速率主动上送（平衡链路，从站自行推帧）；
 *    - 主站按 --cmd-interval 下发遥控命令并测量 RTT；
 *    - 主站按 --gi-interval 发起总召唤并测量 RTT。
 *
 *  101 与 104 的差别（这正是要量化的）：
 *    - 没有 APCI / k-w 窗口 / T1T2T3：链路层是 FT1.2 的 FCB 交替 + ACK，
 *      发送节流靠「上一帧被确认」而不是窗口；
 *    - 平衡模式下双方都可主动发帧，从站不需要等主站召唤；
 *    - 单帧最大 ASDU 受 FT1.2 的 L（1 字节）限制，装不下 104 那么大的批量点。
 *
 *  用法：
 *    dotnet run -c Release --project examples/101/cs101-stress-500 -- \
 *        --devices 500 --duration 60 --json results/metrics-500devices.json
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace cs101_stress_500
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var cfg = StressConfig.Parse(args);
            Console.OutputEncoding = Encoding.UTF8;

            // 500 条链路near-simultaneous 建链前先抬线程池下限，避免逐个注入线程的延迟
            ThreadPool.GetMinThreads(out var minW, out var minIo);
            ThreadPool.SetMinThreads(Math.Max(minW, Environment.ProcessorCount * 4), Math.Max(minIo, 64));

            Console.WriteLine("======================================================================");
            Console.WriteLine(" IEC 60870-5-101 压力测试 — 模拟 N 台装置 (cs101-stress-500)");
            Console.WriteLine("======================================================================");
            Console.WriteLine($" 装置数 (devices)      : {cfg.DeviceCount}");
            Console.WriteLine($" 端口范围 (ports)      : {cfg.BasePort}..{cfg.BasePort + cfg.DeviceCount - 1}");
            Console.WriteLine($" 链路建立速率 (ramp)   : {cfg.RampUpPerSecond}/s");
            Console.WriteLine($" 测量时长 (duration)   : {cfg.DurationSeconds}s");
            Console.WriteLine($" 每装置自发速率 (spont) : {cfg.SpontaneousPerSecondPerDevice}/s");
            Console.WriteLine($" 主站命令周期 (cmd)    : {cfg.CommandIntervalMs}ms");
            Console.WriteLine($" 主站总召唤周期 (gi)   : {cfg.GiIntervalMs}ms");
            Console.WriteLine($" 每报文测点数 (points)  : {cfg.PointsPerMonitorAsdu}");
            Console.WriteLine("----------------------------------------------------------------------");

            var metrics = new Metrics();
            var servers = new List<DeviceServer101>(cfg.DeviceCount);
            var masters = new List<MasterClient101>(cfg.DeviceCount);

            var runCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                runCts.Cancel();
            };

            var overall = Stopwatch.StartNew();

            try
            {
                // ── 1. 启动装置服务端 ────────────────────────────────────
                Console.WriteLine("[1/4] 启动装置服务端 ...");
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < cfg.DeviceCount; i++)
                {
                    var dev = new DeviceServer101(i, cfg.BasePort + i, cfg, metrics);
                    await dev.StartAsync().ConfigureAwait(false);
                    servers.Add(dev);
                }

                sw.Stop();
                Console.WriteLine($"      装置服务端就绪 {servers.Count}/{cfg.DeviceCount}，耗时 {sw.Elapsed.TotalSeconds:F1}s");

                // ── 2. 按限速建链 ───────────────────────────────────────
                Console.WriteLine("[2/4] 主站建链（限速）...");
                sw.Restart();
                var rampDelayMs = Math.Max(1, 1000 / Math.Max(cfg.RampUpPerSecond, 1));
                for (var i = 0; i < cfg.DeviceCount; i++)
                {
                    var master = new MasterClient101(i, cfg.BasePort + i, cfg, metrics);
                    try
                    {
                        await master.ConnectAsync(runCts.Token).ConfigureAwait(false);
                        masters.Add(master);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"      [#{i}] 建链失败: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (rampDelayMs > 0 && (i + 1) % cfg.RampUpPerSecond == 0)
                    {
                        await Task.Delay(1000, runCts.Token).ConfigureAwait(false);
                    }
                }

                sw.Stop();
                Console.WriteLine($"      建链完成 {masters.Count}/{cfg.DeviceCount}，耗时 {sw.Elapsed.TotalSeconds:F1}s");

                // ── 3. 预热 + 测量 ──────────────────────────────────────
                Console.WriteLine("[3/4] 预热与测量 ...");
                foreach (var m in masters)
                {
                    m.StartTraffic();
                }

                await Task.Delay(TimeSpan.FromSeconds(cfg.WarmupSeconds), runCts.Token).ConfigureAwait(false);
                metrics.ResetWindow();

                var sampler = new ResourceSampler();
                sampler.Start();

                var measureStart = Stopwatch.StartNew();
                var lastTick = 0.0;
                while (measureStart.Elapsed.TotalSeconds < cfg.DurationSeconds)
                {
                    await Task.Delay(1000, runCts.Token).ConfigureAwait(false);

                    var secs = measureStart.Elapsed.TotalSeconds;
                    if (cfg.Verbose)
                    {
                        var tick = metrics.Snapshot(secs, servers, masters, sampler);
                        Console.WriteLine($"      t={secs,4:F0}s  ↓{tick.DownPerSec,8:N0} ASDU/s  ↑{tick.UpPerSec,7:N0} ASDU/s"
                                        + $"  {tick.MbitPerSec,5:F1} Mbit/s  CPU {sampler.CpuPercentLast,5:F1}%"
                                        + $"  WS {sampler.WorkingSetMB,6:F0}MB  在线 {tick.LinksUp}/{masters.Count}");
                        lastTick = secs;
                    }
                }

                var measureElapsed = measureStart.Elapsed.TotalSeconds;
                sampler.Stop();
                overall.Stop();

                var report = BuildReport(cfg, metrics, sampler, servers, masters, measureElapsed, overall.Elapsed);

                Console.WriteLine("[4/4] 生成报告 ...");
                Console.WriteLine();
                Console.Write(report.Text);

                if (!string.IsNullOrEmpty(cfg.JsonOut))
                {
                    var path = System.IO.Path.GetFullPath(cfg.JsonOut);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, report.Json, Encoding.UTF8);
                    Console.WriteLine();
                    Console.WriteLine("JSON 指标已写入：" + path);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("已取消。");
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("正在停止主站业务流量 ...");
                foreach (var m in masters)
                {
                    m.StopTraffic();
                }

                await Task.Delay(500).ConfigureAwait(false);

                Console.WriteLine("正在关闭主站 ...");
                var swc = Stopwatch.StartNew();
                var pending = 0;
                foreach (var m in masters)
                {
                    if (!await m.CloseAsync().ConfigureAwait(false))
                    {
                        pending++;
                    }
                }

                swc.Stop();
                Console.WriteLine($"      主站关闭完成，耗时 {swc.Elapsed.TotalMilliseconds:F0} ms（{pending} 个超时未返回）");

                Console.WriteLine("正在关闭装置服务端 ...");
                swc.Restart();
                foreach (var s in servers)
                {
                    s.Dispose();
                }

                swc.Stop();
                Console.WriteLine($"      装置关闭完成，耗时 {swc.Elapsed.TotalMilliseconds:F0} ms");
                Console.WriteLine("完成。");
            }

            return 0;
        }

        private static Report BuildReport(StressConfig cfg, Metrics metrics, ResourceSampler sampler,
            List<DeviceServer101> servers, List<MasterClient101> masters, double secs, TimeSpan totalWall)
        {
            var up = metrics.UpAsdu;
            var down = metrics.DownAsdu;
            var linksUp = 0;
            foreach (var m in masters)
            {
                if (m.IsLinkAvailable)
                {
                    linksUp++;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine(" 可行性 / 可用性 报告 — IEC 60870-5-101 装置压力测试");
            sb.AppendLine("======================================================================");
            sb.AppendLine($" 生成时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($" 运行环境        : {Environment.OSVersion} / .NET {Environment.Version}");
            sb.AppendLine($" 处理器          : {Environment.ProcessorCount} 逻辑核");
            sb.AppendLine($" 测量时长        : {secs:F1}s（含预热后稳定期）；总运行 {totalWall.TotalSeconds:F1}s");
            sb.AppendLine();

            sb.AppendLine("── 1. 可用性 (Availability) ─────────────────────────────────────────");
            sb.AppendLine($" 装置服务端启动  : {servers.Count}/{cfg.DeviceCount} 成功");
            sb.AppendLine($" 主站链路建立    : {masters.Count}/{cfg.DeviceCount} 成功"
                          + (masters.Count < cfg.DeviceCount ? "  ← 有失败!" : ""));
            sb.AppendLine($" 报告时链路可用  : {linksUp}/{masters.Count}" + (linksUp < masters.Count ? "  ← 有失效链路!" : ""));
            sb.AppendLine($" 解码错误        : {metrics.DecodeErrors} 次");
            sb.AppendLine($" 发送失败        : {metrics.SendFailures} 次");
            foreach (var (reason, count) in metrics.SendFailureReasons)
            {
                sb.AppendLine($"       └ {count,7:N0} × {reason}");
            }

            sb.AppendLine($" 命令超时        : {metrics.CommandTimeouts} 次, 命令失败 {metrics.CommandFailures} 次");
            sb.AppendLine($" 总召唤超时      : {metrics.GiTimeouts} 次");
            var usable = masters.Count == cfg.DeviceCount && linksUp == masters.Count
                         && metrics.DecodeErrors == 0 && metrics.SendFailures == 0
                         && metrics.CommandTimeouts == 0 && metrics.CommandFailures == 0;
            sb.AppendLine($" ⇒ 可用性判定    : {(usable ? "通过 — 全量链路稳定，无超时/错误" : "需关注 — 见上方异常计数")}");
            sb.AppendLine();

            sb.AppendLine("── 2. 吞吐量 (Throughput) ──────────────────────────────────────────");
            var downPerSec = secs > 0 ? down / secs : 0;
            var upPerSec = secs > 0 ? up / secs : 0;
            sb.AppendLine($" 下行 ASDU (装置→主站): {down,12:N0}  = {downPerSec,10:N0} ASDU/s");
            sb.AppendLine($" 上行 ASDU (主站→装置): {up,12:N0}  = {upPerSec,10:N0} ASDU/s");
            sb.AppendLine($" 合计 ASDU            : {down + up,12:N0}  = {(downPerSec + upPerSec),10:N0} ASDU/s");
            sb.AppendLine($" 下行带宽             : {metrics.DownBytes * 8 / 1_000_000.0 / secs,9:F2} Mbit/s");
            sb.AppendLine($" 上行带宽             : {metrics.UpBytes * 8 / 1_000_000.0 / secs,9:F2} Mbit/s");
            sb.AppendLine($" 每装置下行速率       : {downPerSec / Math.Max(cfg.DeviceCount, 1),9:F1} ASDU/s");
            sb.AppendLine();

            sb.AppendLine("── 3. 时延 (Latency) ───────────────────────────────────────────────");
            sb.AppendLine(" " + metrics.DescribeLatency("命令往返 (发送→ACT_CON)", metrics.CommandLatency));
            sb.AppendLine(" " + metrics.DescribeLatency("总召唤往返 (命令→ACT_TERM)", metrics.GiLatency));
            sb.AppendLine($" 命令成功/尝试        : {metrics.CommandConfirmed:N0}/{metrics.CommandAttempts:N0}");
            sb.AppendLine($" 总召唤完成           : {metrics.GiCompleted:N0}/{metrics.GiIssued:N0} 轮");
            sb.AppendLine();

            sb.AppendLine("── 4. 资源占用 (Resource Footprint) ────────────────────────────────");
            sb.AppendLine($" CPU 平均占用         : {sampler.CpuPercentAvg:F1} % CPU-time（= 整机能力的 {sampler.CpuPercentAvg / Environment.ProcessorCount:F1}%，{Environment.ProcessorCount} 逻辑核）");
            sb.AppendLine($" CPU 峰值（秒级采样） : {sampler.CpuPercentPeak:F1} % CPU-time");
            sb.AppendLine($" 累计消耗 CPU 时间    : {sampler.CpuSecondsUsed:F1} s（窗口 {secs:F1}s）");
            sb.AppendLine($" 进程工作集           : 起始 {sampler.WorkingSetBeforeMB:F0} MB → 结束 {sampler.WorkingSetAfterMB:F0} MB (峰值 {sampler.WorkingSetPeakMB:F0} MB)");
            sb.AppendLine($" 私有内存             : 起始 {sampler.PrivateBeforeMB:F0} MB → 结束 {sampler.PrivateAfterMB:F0} MB");
            sb.AppendLine($" 托管堆               : 起始 {sampler.ManagedHeapBeforeMB:F0} MB → 结束 {sampler.ManagedHeapAfterMB:F0} MB");
            sb.AppendLine($" 进程句柄数           : 起始 {sampler.HandlesBefore} → 结束 {sampler.HandlesAfter}（净增 {sampler.HandlesAfter - sampler.HandlesBefore}）");
            sb.AppendLine($" 线程数               : 起始 {sampler.ThreadsBefore} → 结束 {sampler.ThreadsAfter}（净增 {sampler.ThreadsAfter - sampler.ThreadsBefore}）");
            sb.AppendLine($" 期间总分配           : {sampler.TotalAllocatedDeltaMB:F0} MB");
            sb.AppendLine($" GC 次数 (Gen0/1/2)   : {sampler.Gen0}/{sampler.Gen1}/{sampler.Gen2}");
            sb.AppendLine($" 每装置内存           : {sampler.WorkingSetAfterMB * 1024 / Math.Max(cfg.DeviceCount, 1):F0} KB (工作集 / 装置)");
            sb.AppendLine();

            sb.AppendLine("── 5. 判定结论 (Verdict) ───────────────────────────────────────────");
            sb.AppendLine(" 该测试在单进程内同时运行 N 个装置服务端与 N 个主站（共 2N 个端点），");
            sb.AppendLine(" 比「一个主站接 N 台装置」更苛刻。");
            sb.AppendLine("======================================================================");
            sb.AppendLine();

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine($"    \"devices\": {cfg.DeviceCount},");
            json.AppendLine($"    \"durationSeconds\": {cfg.DurationSeconds},");
            json.AppendLine($"    \"spontaneousPerDevicePerSecond\": {cfg.SpontaneousPerSecondPerDevice.ToString(CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"commandIntervalMs\": {cfg.CommandIntervalMs},");
            json.AppendLine($"    \"mastersConnected\": {masters.Count},");
            json.AppendLine($"    \"linksUpAtReport\": {linksUp},");
            json.AppendLine($"    \"decodeErrors\": {metrics.DecodeErrors},");
            json.AppendLine($"    \"sendFailures\": {metrics.SendFailures},");
            json.AppendLine($"    \"commandTimeouts\": {metrics.CommandTimeouts},");
            json.AppendLine($"    \"commandFailures\": {metrics.CommandFailures},");
            json.AppendLine($"    \"giTimeouts\": {metrics.GiTimeouts},");
            json.AppendLine($"    \"measureSeconds\": {secs.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"downAsduPerSec\": {downPerSec.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"upAsduPerSec\": {upPerSec.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"commandLatencyUs\": {metrics.CommandLatency.Snapshot().ToJson()},");
            json.AppendLine($"    \"giLatencyUs\": {metrics.GiLatency.Snapshot().ToJson()},");
            json.AppendLine($"    \"cpuPercentAvg\": {sampler.CpuPercentAvg.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"cpuSecondsUsed\": {sampler.CpuSecondsUsed.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"workingSetAfterMB\": {sampler.WorkingSetAfterMB.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"workingSetPeakMB\": {sampler.WorkingSetPeakMB.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"totalAllocatedDeltaMB\": {sampler.TotalAllocatedDeltaMB.ToString("F3", CultureInfo.InvariantCulture)},");
            json.AppendLine($"    \"handlesBefore\": {sampler.HandlesBefore}, \"handlesAfter\": {sampler.HandlesAfter},");
            json.AppendLine($"    \"threadsBefore\": {sampler.ThreadsBefore}, \"threadsAfter\": {sampler.ThreadsAfter}");
            json.AppendLine("}");

            return new Report { Text = sb.ToString(), Json = json.ToString() };
        }

        private sealed class Report
        {
            public string Text;
            public string Json;
        }
    }
}
