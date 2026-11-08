/*
 *  SoakAndReconnectTests.cs
 *
 *  生产可用性验证：长跑稳定性 + 重连风暴 + 并发压力。
 *
 *  与 RobustnessFuzzTests 互补：
 *    - fuzz 验证"畸形输入不崩"；
 *    - 本文件验证"正常长时间运行 / 反复断连重连 / 多连接并发"下
 *      内存与句柄不漂移、状态不泄漏、链路可恢复。
 *
 *  这些用例刻意带较长超时（生产级 soak 的缩小版），默认随全量测试运行，
 *  总耗时控制在 ~30s 内。
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class SoakAndReconnectTests
    {
        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        private static LinkLayerParameters Ll() => new LinkLayerParameters
        {
            AddressLength = 1,
            UseSingleCharACK = true,
        };

        /// <summary>等待条件成立，超时则断言失败。</summary>
        private static async Task WaitUntil(Func<bool> cond, int timeoutMs, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待超时（{timeoutMs}ms）：{what}");
        }

        // ══════════════════════════════════════════════════════════
        //  1. CS104 重连风暴：反复断开/重连，资源不得泄漏
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task CS104重连风暴_反复断开重连_链路必须可恢复且资源不泄漏()
        {
            const int port = 24401;
            var al = Al();

            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(port);

            // 用服务端事件队列持续推自发数据（不依赖总召唤，更贴近真实装置的心跳/遥测流）
            var resp = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            resp.AddInformationObject(new MeasuredValueScaled(100, 1, new QualityDescriptor()));

            await Task.Delay(300);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var heapBefore = GC.GetTotalMemory(true);

            const int cycles = 12;
            var succeeded = 0;

            for (var i = 0; i < cycles; i++)
            {
                var client = new Iec104Client("127.0.0.1", port, null, al);
                var cts = new CancellationTokenSource();

                try
                {
                    await client.ConnectAsync(cts.Token);

                    var got = 0;
                    client.AsduReceived += (in AsduView v) => { Interlocked.Increment(ref got); };

                    // 服务端每 20ms 推一条自发数据
                    for (var k = 0; k < 5; k++)
                    {
                        await server.EnqueueAsync(resp);
                        await Task.Delay(20);
                    }

                    await WaitUntil(() => Volatile.Read(ref got) >= 3, 4000, $"第 {i} 轮自发数据接收");
                    succeeded++;
                }
                catch (Exception ex)
                {
                    Assert.Fail($"第 {i} 轮重连失败：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { cts.Cancel(); } catch { }
                    try { await client.DisconnectAsync(); } catch { }
                    try { client.Dispose(); } catch { }
                    cts.Dispose();
                }
            }

            Assert.That(succeeded, Is.EqualTo(cycles), "每一轮重连都必须成功完成总召唤");

            // 服务端会话必须随客户端断开而回收（不残留）
            await WaitUntil(() => server.SessionCount == 0, 5000,
                $"重连风暴后服务端会话应清零，实际 {server.SessionCount}");

            try { await server.StopAsync(); } catch { }
            try { server.Dispose(); } catch { }


            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var heapAfter = GC.GetTotalMemory(true);

            var growthMb = (heapAfter - heapBefore) / 1024.0 / 1024.0;

            // 12 次重连后托管堆不应显著增长（允许 8MB 抖动，覆盖 JIT/池化/字典扩容）
            Assert.That(growthMb, Is.LessThan(8.0),
                $"重连风暴后托管堆增长 {growthMb:F1} MB，疑似泄漏（{cycles} 轮）");
        }

        // ══════════════════════════════════════════════════════════
        //  2. CS101 重连风暴（平衡链路）
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task CS101重连风暴_反复断开重连_链路必须可恢复()
        {
            const int port = 24402;
            var al = Al();
            var ll = Ll();

            var slave = new Iec101Server(port, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;
            slave.SetInterrogationHandler((parameter, connection, asdu, qoi) =>
            {
                connection.SendACT_CON(asdu, false);
                var resp = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);
                resp.AddInformationObject(new MeasuredValueScaled(100, 7, new QualityDescriptor()));
                connection.SendASDU(resp);
                connection.SendACT_TERM(asdu);
                return true;
            }, null);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token));
            await Task.Delay(300);

            const int cycles = 8;
            for (var i = 0; i < cycles; i++)
            {
                var master = new Iec101Client("127.0.0.1", port, LinkLayerMode.BALANCED, ll);
                master.OwnAddress = 1;
                master.SlaveAddress = 3;

                var got = 0;
                master.SetASDUReceivedHandler((_, _, _) => { Interlocked.Increment(ref got); return true; }, null);

                var cts = new CancellationTokenSource();
                _ = master.StartAsync(cts.Token);

                try
                {
                    await WaitUntil(() => master.GetLinkLayerState() == LinkLayerState.AVAILABLE,
                        5000, $"第 {i} 轮 101 链路建立");

                    master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, 1,
                        QualifierOfInterrogation.STATION);

                    await WaitUntil(() => Volatile.Read(ref got) >= 3, 6000,
                        $"第 {i} 轮 101 总召唤应答（实际 {got}）");
                }
                finally
                {
                    try { master.Stop(); } catch { }
                    try { master.Dispose(); } catch { }
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                    await Task.Delay(150);   // 让服务端感知断开
                }
            }

            try { slave.Stop(); } catch { }
            try { slave.Dispose(); } catch { }
            try { slaveCts.Cancel(); } catch { }
            try { await slaveTask; } catch { }
            slaveCts.Dispose();
        }

        // ══════════════════════════════════════════════════════════
        //  3. 多连接并发吞吐（短 soak）
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task CS104多连接并发_持续收发_无错误()
        {
            const int port = 24403;
            const int clients = 8;
            var al = Al();

            // 多客户端并发测试必须用 CONNECTION_IS_REDUNDANCY_GROUP：SINGLE 模式下
            // 冗余组只把事件投递给"主用"那一个会话，其余客户端收不到（这是 104 冗余语义，
            // 不是缺陷）。每个连接独立成组才能让 8 个客户端都收到数据。
            var server = new Iec104Server(mode: ServerMode.CONNECTION_IS_REDUNDANCY_GROUP);
            await server.StartAsync(port);

            // 服务端持续推自发数据（模拟真实装置的遥测流），供多客户端并发接收
            var resp = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            resp.AddInformationObject(new MeasuredValueScaled(100, 1, new QualityDescriptor()));

            var feeding = new CancellationTokenSource();
            var feeder = Task.Run(async () =>
            {
                try
                {
                    while (!feeding.IsCancellationRequested)
                    {
                        await server.EnqueueAsync(resp, feeding.Token).ConfigureAwait(false);
                        await Task.Delay(20, feeding.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });

            await Task.Delay(300);

            var received = new ConcurrentBag<int>();
            var errors = new ConcurrentBag<string>();
            var clientTasks = new Task[clients];
            var stop = new CancellationTokenSource();

            for (var c = 0; c < clients; c++)
            {
                var idx = c;
                clientTasks[c] = Task.Run(async () =>
                {
                    var client = new Iec104Client("127.0.0.1", port, null, al);
                    try
                    {
                        await client.ConnectAsync(stop.Token);

                        var count = 0;
                        client.AsduReceived += (in AsduView v) =>
                        {
                            if (v.Cot == CauseOfTransmission.SPONTANEOUS)
                            {
                                Interlocked.Increment(ref count);
                                received.Add(idx);
                            }
                        };

                        // 10 秒 soak：持续接收服务端自发数据
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (DateTime.UtcNow < deadline && !stop.IsCancellationRequested)
                        {
                            await Task.Delay(100, stop.Token).ConfigureAwait(false);
                        }

                        if (count < 10)
                        {
                            errors.Add($"客户端 {idx} 10 秒内只收到 {count} 条数据");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"客户端 {idx}: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        try { await client.DisconnectAsync(); } catch { }
                        try { client.Dispose(); } catch { }
                    }
                });
            }

            await Task.WhenAll(clientTasks);

            stop.Cancel();

            try { feeding.Cancel(); } catch { }
            try { await feeder; } catch { }
            feeding.Dispose();

            try { await server.StopAsync(); } catch { }
            try { server.Dispose(); } catch { }

            stop.Dispose();

            Assert.That(errors, Is.Empty,
                "多连接并发下不得出现错误：\n" + string.Join("\n", errors));
            Assert.That(received.Count, Is.GreaterThan(clients * 10),
                $"8 个客户端并发 10 秒应收到足够数据，实际 {received.Count} 条");
        }

        // ══════════════════════════════════════════════════════════
        //  4. 病态对端：连接后立即断开
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task CS104病态对端_连接后立即断开_服务端不得泄漏会话()
        {
            const int port = 24404;
            var al = Al();

            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(port);
            await Task.Delay(200);

            // 20 个客户端：连上、立刻断（不握手、不发数据）
            for (var i = 0; i < 20; i++)
            {
                var client = new Iec104Client("127.0.0.1", port, null, al);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    await client.ConnectAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 预期：握手未来得及完成就取消
                }
                catch
                {
                    // 其它异常同样可接受（连接被重试/复用），关键是不崩溃
                }
                finally
                {
                    try { client.Dispose(); } catch { }
                }
            }

            // 服务端最终必须把会话回收干净
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && server.SessionCount > 0)
            {
                await Task.Delay(50);
            }

            Assert.That(server.SessionCount, Is.EqualTo(0),
                $"病态对端（连上即断）后服务端会话必须清零，实际残留 {server.SessionCount}");

            try { await server.StopAsync(); } catch { }
            try { server.Dispose(); } catch { }

        }
    }
}
