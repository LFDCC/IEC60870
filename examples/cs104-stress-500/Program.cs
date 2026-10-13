/*
 *  Program.cs — cs104-stress-500
 *
 *  IEC 60870-5-104 stress harness: simulates N (default 500) slave devices, each
 *  a standalone Iec104Server on its own port, plus N master clients (one per
 *  device) that drive every common TypeID, and measures throughput, latency,
 *  error and resource metrics to produce a feasibility/usability report.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;

namespace cs104_stress_500
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var cfg = StressConfig.Parse(args);
            Console.OutputEncoding = Encoding.UTF8;

            if (cfg.Bench)
            {
                MicroBench.Run(cfg.BenchIterations);
                Console.WriteLine();
                MicroBench.RunComparable(cfg.BenchIterations);
                return 0;
            }

            // Warm the thread pool ahead of the ramp so the burst of 500 near-simultaneous
            // connects does not pay the ~500ms-per-thread injection penalty.
            ThreadPool.GetMinThreads(out var minW, out var minIo);
            ThreadPool.SetMinThreads(Math.Max(minW, Environment.ProcessorCount * 4), Math.Max(minIo, 64));

            Console.WriteLine("======================================================================");
            Console.WriteLine(" IEC 60870-5-104 压力测试 — 模拟 500 台装置 (cs104-stress-500)");
            Console.WriteLine("======================================================================");
            Console.WriteLine($" 装置数 (devices)      : {cfg.DeviceCount}");
            Console.WriteLine($" 端口范围 (ports)      : {cfg.BasePort}..{cfg.BasePort + cfg.DeviceCount - 1}");
            Console.WriteLine($" 链路建立速率 (ramp)   : {cfg.RampUpPerSecond}/s");
            Console.WriteLine($" 测量时长 (duration)   : {cfg.DurationSeconds}s");
            Console.WriteLine($" 每装置自发速率 (spont) : {cfg.SpontaneousPerSecondPerDevice}/s");
            Console.WriteLine($" 主站命令周期 (cmd)    : {cfg.CommandIntervalMs}ms");
            Console.WriteLine($" 每报文测点数 (points)  : {cfg.PointsPerMonitorAsdu}");
            Console.WriteLine("----------------------------------------------------------------------");

            var metrics = new Metrics();
            var servers = new List<DeviceServer>(cfg.DeviceCount);
            var masters = new List<MasterClient>(cfg.DeviceCount);

            var runCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                runCts.Cancel();
            };

            var overall = Stopwatch.StartNew();

            try
            {
                // ── Phase 1: start all device servers ──────────────────────
                Console.WriteLine("[1/4] 启动装置服务端 ...");
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < cfg.DeviceCount; i++)
                {
                    var dev = new DeviceServer(i, cfg.BasePort + i, cfg, metrics);
                    await dev.StartAsync().ConfigureAwait(false);
                    servers.Add(dev);
                }

                sw.Stop();
                Console.WriteLine($"      已启动 {servers.Count} 个 Iec104Server，耗时 {sw.ElapsedMilliseconds} ms "
                                  + $"(均值 {sw.Elapsed.TotalMilliseconds / Math.Max(1, servers.Count):F2} ms/装置)");

                // ── Phase 2: ramp up all masters (clients) ─────────────────
                Console.WriteLine($"[2/4] 建立主站链路 (ramp {cfg.RampUpPerSecond}/s) ...");
                var rampDelayMs = cfg.RampUpPerSecond > 0 ? (int)Math.Max(1, 1000.0 / cfg.RampUpPerSecond) : 0;
                var rampSw = Stopwatch.StartNew();
                var connected = 0;
                var failures = 0;
                // Pace the ramp with a Stopwatch rather than Task.Delay: Task.Delay has ~15 ms
                // granularity on Windows, which caps the ramp far below the requested rate.
                var rampPace = Stopwatch.StartNew();
                for (var i = 0; i < cfg.DeviceCount; i++)
                {
                    if (rampDelayMs > 0)
                    {
                        var targetMs = (long)((i + 1L) * rampDelayMs);
                        var waitMs = targetMs - rampPace.ElapsedMilliseconds;
                        while (waitMs > 0)
                        {
                            await Task.Delay((int)Math.Min(waitMs, 15), runCts.Token).ConfigureAwait(false);
                            waitMs = targetMs - rampPace.ElapsedMilliseconds;
                        }
                    }

                    var m = new MasterClient(i, cfg.BasePort + i, cfg, metrics);
                    try
                    {
                        await m.ConnectAsync(runCts.Token).ConfigureAwait(false);
                        masters.Add(m);
                        m.StartTraffic();
                        connected++;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        if (failures <= 5)
                        {
                            Console.WriteLine($"      [warn] master #{i} connect failed: {ex.Message}");
                            Console.Out.Flush();
                        }
                    }

                    if (cfg.Verbose && (i + 1) % Math.Max(1, cfg.DeviceCount / 10) == 0)
                    {
                        Console.WriteLine($"      已连接 {connected}/{cfg.DeviceCount}，活动会话 {CountActive(servers)}");
                        Console.Out.Flush();
                    }
                }

                rampSw.Stop();
                Console.WriteLine($"      链路建立完成：{connected} 成功 / {failures} 失败，耗时 {rampSw.Elapsed.TotalSeconds:F1}s");

                // Let the connections settle (server-side sessions get Activated).
                await WaitForActivationAsync(servers, cfg.DeviceCount, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                var activeNow = CountActive(servers);
                Console.WriteLine($"      服务端活动会话：{activeNow}/{cfg.DeviceCount}");

                // ── Phase 3: measurement ───────────────────────────────────
                Console.WriteLine($"[3/4] 测量阶段 ({cfg.DurationSeconds}s) ...");
                await Task.Delay(500).ConfigureAwait(false); // let spontaneous traffic spin up

                var sampler = new ResourceSampler();
                sampler.Sample(); // prime deltas
                var sampleBefore = metrics.TakeSample();
                var resBefore = Snapshot(sampler);

                var measureSw = Stopwatch.StartNew();
                var last = metrics.TakeSample();
                var lastWall = Stopwatch.StartNew();
                var secondSeries = new List<SecondTick>();

                while (measureSw.Elapsed.TotalSeconds < cfg.DurationSeconds)
                {
                    await Task.Delay(1000, runCts.Token).ConfigureAwait(false);
                    sampler.Sample();

                    var cur = metrics.TakeSample();
                    var dt = lastWall.Elapsed.TotalSeconds;
                    lastWall.Restart();

                    var upRate = (cur.UpAsdu - last.UpAsdu) / dt;
                    var downRate = (cur.DownAsdu - last.DownAsdu) / dt;
                    var mbitRate = (cur.DownBytes - last.DownBytes + cur.UpBytes - last.UpBytes) * 8.0 / dt / 1_000_000.0;

                    var tick = new SecondTick
                    {
                        T = measureSw.Elapsed.TotalSeconds,
                        DownAsduPerSec = downRate,
                        UpAsduPerSec = upRate,
                        MbitPerSec = mbitRate,
                        CpuPercent = sampler.CpuPercentLast,
                        CpuSeconds = sampler.CpuSecondsTotal,
                        WorkingSetMB = sampler.WorkingSetBytes / 1024.0 / 1024.0,
                        ActiveSessions = CountActive(servers)
                    };
                    secondSeries.Add(tick);
                    last = cur;

                    if (cfg.Verbose && ((int)measureSw.Elapsed.TotalSeconds) % 5 == 0)
                    {
                        Console.WriteLine($"      t={measureSw.Elapsed.TotalSeconds,4:F0}s  ↓{downRate,8:F0} ASDU/s  ↑{upRate,7:F0} ASDU/s  "
                                          + $"{mbitRate,6:F1} Mbit/s  CPU {sampler.CpuPercentLast,5:F1}%  WS {tick.WorkingSetMB,7:F0}MB  会话 {tick.ActiveSessions}");
                    }
                }

                measureSw.Stop();
                var sampleAfter = metrics.TakeSample();
                sampler.Sample();
                var resAfter = Snapshot(sampler);

                // ── Phase 4: report ────────────────────────────────────────
                Console.WriteLine("[4/4] 生成报告 ...\n");
                var report = BuildReport(cfg, metrics, sampler, servers, masters,
                    secondSeries, sampleBefore, sampleAfter, measureSw.Elapsed,
                    resBefore, resAfter, overall.Elapsed);
                Console.WriteLine(report.Text);

                if (!string.IsNullOrEmpty(cfg.JsonOut))
                {
                    File.WriteAllText(cfg.JsonOut, report.Json, new UTF8Encoding(false));
                    Console.WriteLine($"\nJSON 指标已写入：{Path.GetFullPath(cfg.JsonOut)}");
                }

                // ── Shutdown ───────────────────────────────────────────────
                // Stop new traffic first, let in-flight sends drain, then tear down.
                // Tear down in parallel (bounded) and time it: sequential disposal of
                // hundreds of TouchSocket clients serialises hundreds of TCP closes,
                // which is slow and can stall. A per-item timeout guarantees the process
                // exits even if one client's async close never completes.
                Console.WriteLine("\n正在停止主站业务流量 ...");
                foreach (var m in masters)
                {
                    m.StopTraffic();
                }

                await Task.Delay(2000).ConfigureAwait(false);

                Console.WriteLine("正在关闭主站 ...");
                var masterShutdownSw = Stopwatch.StartNew();
                var masterStalled = await DisposeAllAsync(masters.Select(m => m.DisposeAsync().AsTask()), 30000)
                    .ConfigureAwait(false);
                masterShutdownSw.Stop();
                Console.WriteLine($"      主站关闭完成，耗时 {masterShutdownSw.ElapsedMilliseconds} ms"
                                  + (masterStalled > 0 ? $"（{masterStalled} 个超时未返回）" : ""));

                Console.WriteLine("正在关闭装置服务端 ...");
                var serverShutdownSw = Stopwatch.StartNew();
                var serverStalled = await DisposeAllAsync(servers.Select(s => s.DisposeAsync().AsTask()), 30000)
                    .ConfigureAwait(false);
                serverShutdownSw.Stop();
                Console.WriteLine($"      装置关闭完成，耗时 {serverShutdownSw.ElapsedMilliseconds} ms"
                                  + (serverStalled > 0 ? $"（{serverStalled} 个超时未返回）" : ""));

                Console.WriteLine("完成。");
                return failures == 0 ? 0 : 2;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n用户中断，正在清理 ...");
                foreach (var m in masters) { await m.DisposeAsync().ConfigureAwait(false); }
                foreach (var s in servers) { await s.DisposeAsync().ConfigureAwait(false); }
                return 130;
            }
            catch (Exception ex)
            {
                Console.WriteLine("致命错误：" + ex);
                foreach (var m in masters) { try { await m.DisposeAsync(); } catch { } }
                foreach (var s in servers) { try { await s.DisposeAsync(); } catch { } }
                return 1;
            }
        }

        private static int CountActive(List<DeviceServer> servers)
        {
            var n = 0;
            for (var i = 0; i < servers.Count; i++)
            {
                if (servers[i].IsActivated)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Await all disposal tasks in parallel with a per-item timeout; returns how many stalled.</summary>
        private static async Task<int> DisposeAllAsync(IEnumerable<Task> tasks, int perItemTimeoutMs)
        {
            var pending = new List<Task>();
            foreach (var t in tasks)
            {
                pending.Add(t);
            }

            var all = Task.WhenAll(pending);
            var finished = await Task.WhenAny(all, Task.Delay(perItemTimeoutMs)).ConfigureAwait(false);
            if (ReferenceEquals(finished, all))
            {
                // Observe any exceptions so they don't surface as unobserved.
                try { await all.ConfigureAwait(false); } catch { /* ignore */ }
                return 0;
            }

            var stalled = 0;
            foreach (var t in pending)
            {
                if (!t.IsCompleted)
                {
                    stalled++;
                }
            }

            return stalled;
        }

        private static async Task WaitForActivationAsync(List<DeviceServer> servers, int expected, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (CountActive(servers) >= expected)
                {
                    return;
                }
            }
        }

        private static ResSnapshot Snapshot(ResourceSampler s) => new ResSnapshot
        {
            WorkingSetMB = s.WorkingSetBytes / 1024.0 / 1024.0,
            PrivateMB = s.PrivateBytes / 1024.0 / 1024.0,
            ManagedHeapMB = s.ManagedHeapBytes / 1024.0 / 1024.0,
            TotalAllocatedMB = s.TotalAllocatedBytes / 1024.0 / 1024.0,
            CpuSeconds = s.CpuSecondsTotal,
            Gen0 = s.Gen0, Gen1 = s.Gen1, Gen2 = s.Gen2
        };

        private struct ResSnapshot
        {
            public double WorkingSetMB, PrivateMB, ManagedHeapMB, TotalAllocatedMB, CpuSeconds;
            public int Gen0, Gen1, Gen2;
        }

        private struct SecondTick
        {
            public double T, DownAsduPerSec, UpAsduPerSec, MbitPerSec, CpuPercent, CpuSeconds, WorkingSetMB;
            public int ActiveSessions;
        }

        private sealed class Report
        {
            public string Text;
            public string Json;
        }

        private static Report BuildReport(
            StressConfig cfg, Metrics metrics, ResourceSampler sampler,
            List<DeviceServer> servers, List<MasterClient> masters,
            List<SecondTick> series, Metrics.Sample before, Metrics.Sample after,
            TimeSpan duration, ResSnapshot resBefore, ResSnapshot resAfter, TimeSpan totalWall)
        {
            var secs = duration.TotalSeconds;
            var downAsdu = after.DownAsdu - before.DownAsdu;
            var upAsdu = after.UpAsdu - before.UpAsdu;
            var downBytes = after.DownBytes - before.DownBytes;
            var upBytes = after.UpBytes - before.UpBytes;

            var downAsduPerSec = downAsdu / secs;
            var upAsduPerSec = upAsdu / secs;
            var totalAsduPerSec = downAsduPerSec + upAsduPerSec;
            var downMbit = downBytes * 8.0 / secs / 1_000_000.0;
            var upMbit = upBytes * 8.0 / secs / 1_000_000.0;

            var cmdLat = metrics.CommandLatency;
            var giLat = metrics.GiLatency;

            var coverage = metrics.CoverageSnapshot();
            var cmdCoverage = metrics.CommandCoverageSnapshot();
            var perType = metrics.PerTypeDown();

            var activeSessions = CountActive(servers);
            var expectedSessions = masters.Count;
            var giIssued = masters.Sum(m => m.GiIssued);
            var giCompleted = masters.Sum(m => m.GiCompleted);

            // CPU: the per-second Process.Refresh() readings are sample-aliased on Windows
            // (0% / spike alternation), so compute the authoritative average from the
            // cumulative processor-time delta over the whole measurement window.
            var cpuSecondsUsed = resAfter.CpuSeconds - resBefore.CpuSeconds;
            var cores = Environment.ProcessorCount;
            var cpuAvg = secs > 0 ? 100.0 * cpuSecondsUsed / secs : 0;             // process % (sum over cores)
            var cpuAvgOfMachine = cpuAvg / cores;                                   // % of total machine capacity
            var peakCpuRaw = series.Count > 0 ? series.Max(s => s.CpuPercent) : 0;
            var peakCpu = Math.Max(peakCpuRaw, cpuAvg);                             // never report peak below avg
            var peakWs = series.Count > 0 ? series.Max(s => s.WorkingSetMB) : resAfter.WorkingSetMB;

            // ── text ───────────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine(" 可行性 / 可用性 报告 — IEC 60870-5-104 500 装置压力测试");
            sb.AppendLine("======================================================================");
            sb.AppendLine($" 生成时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($" 运行环境        : {Environment.OSVersion} / .NET {Environment.Version}");
            sb.AppendLine($" 处理器          : {Environment.ProcessorCount} 逻辑核");
            sb.AppendLine($" 测量时长        : {secs:F1}s（含预热后稳定期）；总运行 {totalWall.TotalSeconds:F1}s");
            sb.AppendLine();

            sb.AppendLine("── 1. 可用性 (Availability) ─────────────────────────────────────────");
            sb.AppendLine($" 装置服务端启动  : {servers.Count}/{cfg.DeviceCount} 成功");
            sb.AppendLine($" 主站链路建立    : {masters.Count}/{cfg.DeviceCount} 成功" + (masters.Count < cfg.DeviceCount ? "  ← 有失败!" : ""));
            sb.AppendLine($" 服务端活动会话  : {activeSessions}/{expectedSessions}");
            sb.AppendLine($" 运行期链路断开  : 客户端侧 {metrics.ClientConnectionClosed} 次, 服务端侧 {metrics.ServerConnectionClosed} 次");
            var closeReasons = metrics.CloseReasons();
            if (closeReasons.Count > 0)
            {
                var detail = string.Join(", ", closeReasons.Select(kv => $"{kv.Key}={kv.Value}"));
                sb.AppendLine($"   断开原因明细   : {detail}");
            }

            sb.AppendLine($" 解码错误        : {metrics.DecodeErrors} 次");
            sb.AppendLine($" 服务端处理异常  : {metrics.ServerAsduErrors} 次");
            sb.AppendLine($" 命令超时        : {metrics.CommandTimeouts} 次, 其它命令失败 {metrics.CommandFailures} 次");
            sb.AppendLine($" 预发/执行 Select 位不匹配 : {metrics.SelectMismatches} 次");
            sb.AppendLine($" 总召唤超时      : {metrics.GiTimeouts} 次");
            var usable = masters.Count == cfg.DeviceCount && metrics.ClientConnectionClosed == 0
                         && metrics.ServerConnectionClosed == 0 && metrics.DecodeErrors == 0
                         && metrics.ServerAsduErrors == 0 && metrics.CommandTimeouts == 0
                         && metrics.SelectMismatches == 0;
            sb.AppendLine($" ⇒ 可用性判定    : {(usable ? "通过 — 全量链路稳定，无断链/解码错误" : "需关注 — 见上方异常计数")}");
            sb.AppendLine();

            sb.AppendLine("── 2. 吞吐量 (Throughput) ──────────────────────────────────────────");
            sb.AppendLine($" 下行 ASDU (装置→主站): {downAsdu,12:N0}  = {downAsduPerSec,10:N0} ASDU/s");
            sb.AppendLine($" 上行 ASDU (主站→装置): {upAsdu,12:N0}  = {upAsduPerSec,10:N0} ASDU/s");
            sb.AppendLine($" 合计 ASDU            : {downAsdu + upAsdu,12:N0}  = {totalAsduPerSec,10:N0} ASDU/s");
            sb.AppendLine($" 下行带宽             : {downMbit,8:F2} Mbit/s");
            sb.AppendLine($" 上行带宽             : {upMbit,8:F2} Mbit/s");
            sb.AppendLine($" 合计带宽             : {downMbit + upMbit,8:F2} Mbit/s");
            sb.AppendLine($" 每装置下行速率       : {downAsduPerSec / Math.Max(1, masters.Count),8:F1} ASDU/s");
            sb.AppendLine($" 自发报文占比         : {Pct(metrics.DownSpontaneous, metrics.DownAsdu)}  (自发 {metrics.DownSpontaneous:N0})");
            sb.AppendLine($" 总召唤响应报文       : {metrics.DownInterrogated,12:N0}");
            sb.AppendLine($" 确认报文             : {metrics.DownConfirmation,12:N0}");
            sb.AppendLine();

            sb.AppendLine("── 3. 时延 (Latency) ───────────────────────────────────────────────");
            sb.AppendLine($" 命令往返 (发送→ACT_CON): {Fmt(cmdLat)}");
            sb.AppendLine($" 总召唤往返 (命令→ACT_TERM): {Fmt(giLat)}");
            sb.AppendLine($" 总召唤完成           : {giCompleted}/{giIssued} 轮");
            sb.AppendLine();

            sb.AppendLine("── 4. 资源占用 (Resource Footprint) ────────────────────────────────");
            sb.AppendLine($" CPU 平均占用         : {cpuAvg:F1} % CPU-time（= 整机能力的 {cpuAvgOfMachine:F1}%，{cores} 逻辑核）");
            sb.AppendLine($" CPU 峰值（秒级采样） : {peakCpu:F1} % CPU-time");
            sb.AppendLine($" 累计消耗 CPU 时间    : {cpuSecondsUsed:F1} s（窗口 {secs:F1}s）");
            sb.AppendLine($" 进程工作集           : 起始 {resBefore.WorkingSetMB:F0} MB → 结束 {resAfter.WorkingSetMB:F0} MB (峰值 {peakWs:F0} MB)");
            sb.AppendLine($" 私有内存             : 起始 {resBefore.PrivateMB:F0} MB → 结束 {resAfter.PrivateMB:F0} MB");
            sb.AppendLine($" 托管堆               : 起始 {resBefore.ManagedHeapMB:F0} MB → 结束 {resAfter.ManagedHeapMB:F0} MB");
            sb.AppendLine($" 期间总分配           : {resAfter.TotalAllocatedMB - resBefore.TotalAllocatedMB:F0} MB");
            sb.AppendLine($" GC 次数 (Gen0/1/2)   : {sampler.Gen0}/{sampler.Gen1}/{sampler.Gen2}");
            sb.AppendLine($" 每装置内存           : {(resAfter.WorkingSetMB / Math.Max(1, masters.Count)) * 1024:F0} KB (工作集 / 装置)");
            sb.AppendLine();

            sb.AppendLine("── 5. 协议类型覆盖 (TypeID Coverage) ───────────────────────────────");
            var monitorExpected = TypeCoverage.MonitorTypes.Length;
            var monitorSeen = coverage.Count(c => c.ToString().StartsWith("M_"));
            sb.AppendLine($" 监视方向 (M_*)       : 接收到 {monitorSeen}/{monitorExpected} 种");
            var cmdExpected = TypeCoverage.CommandTypes.Length;
            var cmdSeen = cmdCoverage.Count(c => c.ToString().StartsWith("C_") && IsControlCommandType(c));
            sb.AppendLine($" 控制方向 (C_*) 确认  : {cmdSeen}/{cmdExpected} 种");
            var cmdDetail = string.Join(", ", cmdCoverage.Where(IsControlCommandType).Select(t => t.ToString()).OrderBy(x => x));
            sb.AppendLine($" 控制命令覆盖明细     : {cmdDetail}");
            var cmdStats = metrics.CommandStats();
            var missing = new List<string>();
            var failing = new List<string>();
            foreach (var t in TypeCoverage.CommandTypes)
            {
                if (cmdStats.TryGetValue(t, out var st))
                {
                    if (st.confirmed == 0 && st.timeouts > 0)
                    {
                        failing.Add($"{t} (尝试 {st.attempts}, 超时 {st.timeouts})");
                    }
                    else if (st.confirmed == 0)
                    {
                        missing.Add(t.ToString());
                    }
                }
                else
                {
                    missing.Add(t.ToString());
                }
            }

            if (failing.Count > 0)
            {
                var failingDetail = string.Join("; ", failing);
                sb.AppendLine($" ⚠ 确认失败的 C_* 类型 : {failingDetail}");
                sb.AppendLine("   说明：这些类型的 ACTIVATION_CON 无法被客户端确认匹配器关联，");
                sb.AppendLine("   客户端将等待至 CommandConfirmationTimeout（5s）后抛 TimeoutException。");
            }

            if (missing.Count > 0)
            {
                sb.AppendLine($" 未触发确认的 C_* 类型 : {string.Join(", ", missing)}");
            }

            sb.AppendLine();

            sb.AppendLine("── 6. 判定结论 (Verdict) ───────────────────────────────────────────");
            var stability = metrics.ClientConnectionClosed == 0 && metrics.ServerConnectionClosed == 0
                            && metrics.DecodeErrors == 0 && metrics.ServerAsduErrors == 0
                            && metrics.CommandTimeouts == 0 && metrics.SelectMismatches == 0;
            sb.AppendLine($" 可行性 : {(stability ? "可行" : "基本可行（存在异常计数）")} — 单机单进程可承载 {cfg.DeviceCount} 台装置的 104 通信。");
            sb.AppendLine($" 可用性 : {(usable ? "高" : "中")} — 全量链路在线，{totalAsduPerSec:N0} ASDU/s，CPU {cpuAvg:F0}%（整机 {cpuAvgOfMachine:F0}%），内存 {resAfter.WorkingSetMB:F0}MB。");
            sb.AppendLine("======================================================================");

            var text = sb.ToString();

            // ── json ───────────────────────────────────────────────────────
            var json = BuildJson(cfg, metrics, sampler, masters, servers,
                downAsdu, upAsdu, downBytes, upBytes, secs,
                cmdLat, giLat, coverage, cmdCoverage, perType,
                activeSessions, giIssued, giCompleted,
                cpuAvg, cpuAvgOfMachine, cpuSecondsUsed, peakCpu, peakWs, resBefore, resAfter, duration, totalWall, series);

            return new Report { Text = text, Json = json };
        }

        private static bool IsControlCommandType(TypeID t) => TypeCoverage.CommandTypes.Contains(t);

        private static double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;

        private static string Fmt(LatencyStats s) =>
            s.Count == 0
                ? "无样本"
                : $"n={s.Count:N0}  均值={s.MeanUs / 1000.0:F2}ms  P50={s.P50Us / 1000.0:F2}ms  "
                  + $"P90={s.P90Us / 1000.0:F2}ms  P99={s.P99Us / 1000.0:F2}ms  P99.9={s.P999Us / 1000.0:F2}ms  最大={s.MaxUs / 1000.0:F2}ms";

        private static string BuildJson(
            StressConfig cfg, Metrics metrics, ResourceSampler sampler,
            List<MasterClient> masters, List<DeviceServer> servers,
            long downAsdu, long upAsdu, long downBytes, long upBytes, double secs,
            LatencyStats cmdLat, LatencyStats giLat,
            List<TypeID> coverage, List<TypeID> cmdCoverage, Dictionary<TypeID, long> perType,
            int activeSessions, long giIssued, long giCompleted,
            double cpuAvg, double cpuAvgOfMachine, double cpuSecondsUsed, double peakCpu, double peakWs,
            ResSnapshot resBefore, ResSnapshot resAfter, TimeSpan duration, TimeSpan totalWall,
            List<SecondTick> series)
        {
            var ci = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("F3", ci);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"generated\": \"{DateTime.Now:O}\",\n");
            sb.Append($"  \"config\": {{ \"devices\": {cfg.DeviceCount}, \"basePort\": {cfg.BasePort}, \"durationSeconds\": {cfg.DurationSeconds}, "
                      + $"\"rampPerSecond\": {cfg.RampUpPerSecond}, \"spontaneousPerDevicePerSecond\": {cfg.SpontaneousPerSecondPerDevice.ToString(ci)}, "
                      + $"\"commandIntervalMs\": {cfg.CommandIntervalMs}, \"pointsPerMonitorAsdu\": {cfg.PointsPerMonitorAsdu} }},\n");
            sb.Append($"  \"environment\": {{ \"os\": \"{Escape(Environment.OSVersion.ToString())}\", \"dotnet\": \"{Escape(Environment.Version.ToString())}\", "
                      + $"\"logicalCores\": {Environment.ProcessorCount} }},\n");

            sb.Append("  \"availability\": {\n");
            sb.Append($"    \"mastersConnected\": {masters.Count},\n");
            sb.Append($"    \"devicesStarted\": {servers.Count},\n");
            sb.Append($"    \"expectedDevices\": {cfg.DeviceCount},\n");
            sb.Append($"    \"serverActiveSessions\": {activeSessions},\n");
            sb.Append($"    \"clientConnectionClosed\": {metrics.ClientConnectionClosed},\n");
            sb.Append($"    \"serverConnectionClosed\": {metrics.ServerConnectionClosed},\n");
            sb.Append($"    \"decodeErrors\": {metrics.DecodeErrors},\n");
            sb.Append($"    \"serverAsduErrors\": {metrics.ServerAsduErrors},\n");
            sb.Append($"    \"commandTimeouts\": {metrics.CommandTimeouts},\n");
            sb.Append($"    \"commandFailures\": {metrics.CommandFailures},\n");
            sb.Append($"    \"selectMismatches\": {metrics.SelectMismatches},\n");
            sb.Append($"    \"giTimeouts\": {metrics.GiTimeouts}\n");
            sb.Append("  },\n");

            sb.Append("  \"throughput\": {\n");
            sb.Append($"    \"measureSeconds\": {N(secs)},\n");
            sb.Append($"    \"downAsdu\": {downAsdu}, \"upAsdu\": {upAsdu},\n");
            sb.Append($"    \"downAsduPerSec\": {N(downAsdu / secs)}, \"upAsduPerSec\": {N(upAsdu / secs)},\n");
            sb.Append($"    \"downMbitPerSec\": {N(downBytes * 8.0 / secs / 1e6)}, \"upMbitPerSec\": {N(upBytes * 8.0 / secs / 1e6)},\n");
            sb.Append($"    \"spontaneousAsdu\": {metrics.DownSpontaneous}, \"interrogatedAsdu\": {metrics.DownInterrogated}, \"confirmationAsdu\": {metrics.DownConfirmation}\n");
            sb.Append("  },\n");

            sb.Append("  \"latencyMs\": {\n");
            sb.Append($"    \"commandRtt\": {{ \"count\": {cmdLat.Count}, \"mean\": {N(cmdLat.MeanUs / 1000.0)}, \"p50\": {N(cmdLat.P50Us / 1000.0)}, "
                      + $"\"p90\": {N(cmdLat.P90Us / 1000.0)}, \"p99\": {N(cmdLat.P99Us / 1000.0)}, \"p999\": {N(cmdLat.P999Us / 1000.0)}, \"max\": {N(cmdLat.MaxUs / 1000.0)} }},\n");
            sb.Append($"    \"giRtt\": {{ \"count\": {giLat.Count}, \"mean\": {N(giLat.MeanUs / 1000.0)}, \"p50\": {N(giLat.P50Us / 1000.0)}, "
                      + $"\"p90\": {N(giLat.P90Us / 1000.0)}, \"p99\": {N(giLat.P99Us / 1000.0)}, \"max\": {N(giLat.MaxUs / 1000.0)} }}\n");
            sb.Append("  },\n");

            sb.Append("  \"resources\": {\n");
            sb.Append($"    \"cpuPercentAvg\": {N(cpuAvg)}, \"cpuPercentAvgOfMachine\": {N(cpuAvgOfMachine)}, "
                      + $"\"cpuSecondsUsed\": {N(cpuSecondsUsed)}, \"cpuPercentPeakSampled\": {N(peakCpu)},\n");
            sb.Append($"    \"workingSetBeforeMB\": {N(resBefore.WorkingSetMB)}, \"workingSetAfterMB\": {N(resAfter.WorkingSetMB)}, \"workingSetPeakMB\": {N(peakWs)},\n");
            sb.Append($"    \"privateBeforeMB\": {N(resBefore.PrivateMB)}, \"privateAfterMB\": {N(resAfter.PrivateMB)},\n");
            sb.Append($"    \"managedHeapBeforeMB\": {N(resBefore.ManagedHeapMB)}, \"managedHeapAfterMB\": {N(resAfter.ManagedHeapMB)},\n");
            sb.Append($"    \"totalAllocatedDeltaMB\": {N(resAfter.TotalAllocatedMB - resBefore.TotalAllocatedMB)},\n");
            sb.Append($"    \"gen0\": {sampler.Gen0}, \"gen1\": {sampler.Gen1}, \"gen2\": {sampler.Gen2},\n");
            sb.Append($"    \"kbPerDevice\": {N(resAfter.WorkingSetMB / Math.Max(1, masters.Count) * 1024)}\n");
            sb.Append("  },\n");

            sb.Append("  \"coverage\": {\n");
            var monitorSeenJson = coverage.Count(c => c.ToString().StartsWith("M_"));
            var cmdSeenJson = cmdCoverage.Count(IsControlCommandType);
            sb.Append($"    \"monitorTypesSeen\": {monitorSeenJson},\n");
            sb.Append($"    \"monitorTypesExpected\": {TypeCoverage.MonitorTypes.Length},\n");
            sb.Append($"    \"controlTypesConfirmed\": {cmdSeenJson},\n");
            sb.Append($"    \"controlTypesExpected\": {TypeCoverage.CommandTypes.Length},\n");
            sb.Append("    \"perTypeDownCount\": {");
            sb.Append(string.Join(", ", perType.OrderBy(kv => (int)kv.Key).Select(kv => $"\"{(int)kv.Key}\": {kv.Value}")));
            sb.Append("}\n");
            sb.Append("  },\n");

            sb.Append("  \"commandCoverage\": {");
            var cmdStatsJson = metrics.CommandStats();
            var cmdEntries = new List<string>();
            foreach (var t in TypeCoverage.CommandTypes)
            {
                cmdStatsJson.TryGetValue(t, out var st);
                cmdEntries.Add($"\"{t}\": {{ \"attempts\": {st.attempts}, \"confirmed\": {st.confirmed}, \"timeouts\": {st.timeouts} }}");
            }

            sb.Append(string.Join(", ", cmdEntries));
            sb.Append("},\n");

            sb.Append("  \"gi\": {");
            sb.Append($" \"issued\": {giIssued}, \"completed\": {giCompleted} }},\n");

            sb.Append("  \"timeline\": [");
            for (var i = 0; i < series.Count; i++)
            {
                var s = series[i];
                var entry = "{\"t\": " + N(s.T)
                    + ", \"downAsduPerSec\": " + N(s.DownAsduPerSec)
                    + ", \"upAsduPerSec\": " + N(s.UpAsduPerSec)
                    + ", \"mbitPerSec\": " + N(s.MbitPerSec)
                    + ", \"cpuPercent\": " + N(s.CpuPercent)
                    + ", \"cpuSeconds\": " + N(s.CpuSeconds)
                    + ", \"workingSetMB\": " + N(s.WorkingSetMB)
                    + ", \"activeSessions\": " + s.ActiveSessions + "}";
                sb.Append(entry);
                if (i < series.Count - 1) sb.Append(", ");
            }
            sb.Append(']');
            sb.Append("\n}\n");

            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
