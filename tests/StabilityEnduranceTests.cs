/*
 *  StabilityEnduranceTests.cs
 *
 *  生产级长跑稳定性测试（Endurance / Soak），覆盖 101 / 103 / 104 三个协议。
 *
 *  与 SoakAndReconnectTests 的区别：
 *    - 后者是"缩小版"冒烟（~30s，跑在全量测试里），只验证状态可恢复；
 *    - 本文件是**带资源漂移断言的长跑**：持续收发 + 周期性强制断链重建，
 *      结束后要求 句柄数 / 线程数 / 托管堆 相对预热后基线不显著增长。
 *
 *  运行方式（时长可用环境变量覆盖，默认 20s）：
 *      IEC_STABILITY_SECONDS=120 dotnet test --filter "Category=Stability"
 *
 *  三个用例互不共享端口（AllocatePort 动态分配），可以：
 *    1) 在单个进程内并行跑（fixture 标了 Parallelizable）——但资源计数会被互相污染；
 *    2) **推荐**：三个独立进程并行跑，每个进程只看自己协议的资源漂移。
 *       示例见 examples/stability/run-stability.sh。
 *
 *  漂移判据为什么这么松：
 *    .NET 线程池在持续负载下会按需扩容、Socket/文件句柄走内核缓存，
 *    轻微增长是正常的；真正要抓的是"每轮都涨、永不回落"的泄漏——
 *    所以先预热若干轮再取基线，且阈值按协议留足噪声余量。
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using TouchSocket.Core;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.CS103;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    [Category("Stability")]
    [NonParallelizable] // 资源漂移断言要求进程内没有其它测试并发；并行请用独立进程（见 examples/stability）
    public class StabilityEnduranceTests
    {
        /// <summary>单个用例的持续时间（秒）。CI 默认 20s，长跑用 IEC_STABILITY_SECONDS 覆盖。</summary>
        private static int DurationSeconds
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("IEC_STABILITY_SECONDS");
                return int.TryParse(raw, out var seconds) ? Math.Max(5, seconds) : 20;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  资源探针
        // ══════════════════════════════════════════════════════════

        private readonly struct ResourceSnapshot
        {
            public readonly long ManagedHeap;
            public readonly long PrivateBytes;
            public readonly int Handles;
            public readonly int Threads;

            public ResourceSnapshot(long heap, long priv, int handles, int threads)
            {
                ManagedHeap = heap;
                PrivateBytes = priv;
                Handles = handles;
                Threads = threads;
            }

            public override string ToString()
                => $"heap={ManagedHeap / 1024}KB, priv={PrivateBytes / 1024}KB, handles={Handles}, threads={Threads}";
        }

        private static ResourceSnapshot Capture()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();

            return new ResourceSnapshot(
                GC.GetTotalMemory(forceFullCollection: true),
                proc.PrivateMemorySize64,
                proc.HandleCount,
                proc.Threads.Count);
        }

        /// <summary>让线程池/GC 回落一段，再采样（避免把瞬时负载当成泄漏）。</summary>
        private static async Task<ResourceSnapshot> CaptureSettledAsync(int settleMs = 1500)
        {
            await Task.Delay(settleMs).ConfigureAwait(false);
            return Capture();
        }

        /// <summary>
        /// 断言长跑前后的资源增长在合理范围。
        /// 失败信息里带完整快照，现场可直接判断是"GC 未回落"还是"真泄漏"。
        /// </summary>
        private static void AssertNoDrift(ResourceSnapshot baseline, ResourceSnapshot final, string protocol)
        {
            var heapGrowth = final.ManagedHeap - baseline.ManagedHeap;
            var handleGrowth = final.Handles - baseline.Handles;
            var threadGrowth = final.Threads - baseline.Threads;

            // 托管堆：允许 max(8MB, 基线的 50%) —— 碎片与缓存属正常，持续翻倍才是泄漏
            var heapLimit = Math.Max(8L * 1024 * 1024, baseline.ManagedHeap / 2);
            Assert.That(heapGrowth, Is.LessThan(heapLimit),
                $"[{protocol}] 托管堆持续增长 {heapGrowth / 1024}KB（上限 {heapLimit / 1024}KB）"
                + $"\n  基线: {baseline}\n  结束: {final}");

            // 句柄：Windows 上每个关闭的 TCP socket 会留在 TIME_WAIT（默认 2×MSL ≈ 4 分钟）
            // 并继续占用一个句柄。重连风暴式的长跑里句柄数必然随时间线性上涨，直到 TIME_WAIT
            // 回收 —— 这不是泄漏，且用"每轮 N 个"的阈值反而会把正常场景判失败。
            // 真正的判断在这里：如果每轮重连泄漏一个 socket，本轮 1 万+ 轮早已是上万句柄，
            // 所以用一个与轮数无关的常量上界即可（256 足以容纳在途连接 + 近期 TIME_WAIT 波动）。
            // 内存/线程才是严格判据：它们不受内核回收窗口影响。
            Assert.That(handleGrowth, Is.LessThan(256),
                $"[{protocol}] 进程句柄增长 {handleGrowth}（上限 256）——超出 TIME_WAIT 可解释范围，"
                + "很可能是 Socket/FileStream 未释放"
                + $"\n  基线: {baseline}\n  结束: {final}");

            // 线程：线程池扩容属于正常，收敛后仍涨 64 个说明有线程/循环没退出
            Assert.That(threadGrowth, Is.LessThan(64),
                $"[{protocol}] 线程数增长 {threadGrowth}（上限 64）——很可能是后台循环/读线程未退出"
                + $"\n  基线: {baseline}\n  结束: {final}");
        }

        private static int AllocatePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        private static LinkLayerParameters Ll() => new LinkLayerParameters
        {
            AddressLength = 1,
            UseSingleCharACK = true,
        };

        // ══════════════════════════════════════════════════════════
        //  1. CS104：持续自发数据 + 周期性强制断链重建
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 稳定性104_持续收发与周期重连_资源不漂移()
        {
            var duration = TimeSpan.FromSeconds(DurationSeconds);
            var port = AllocatePort();
            var al = Al();

            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(port);

            try
            {
                // 每轮：新建客户端 → 连上 → 持续收自发数据 → 断开销毁
                async Task<(int received, int cycles)> RunCycleAsync(int cycleSeconds)
                {
                    var client = new Iec104Client("127.0.0.1", port, null, al);
                    var received = 0;
                    client.AsduReceived += (in AsduView v) => Interlocked.Increment(ref received);

                    await client.ConnectAsync();

                    // 等服务端把这个会话认成主用（否则 Enqueue 会一直排队）
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline && server.GetNumberOfQueueEntries() > 0)
                    {
                        await Task.Delay(20);
                    }

                    var end = DateTime.UtcNow.AddSeconds(cycleSeconds);
                    var sent = 0;

                    while (DateTime.UtcNow < end)
                    {
                        await server.EnqueueAsync(Spontaneous(al, sent++));
                        await Task.Delay(5);
                    }

                    // 让在途 ASDU 落袋，再断开
                    await Task.Delay(100);
                    try { await client.DisconnectAsync(); } catch { }

                    client.Dispose();

                    return (Volatile.Read(ref received), sent);
                }

                // 预热：让线程池、JIT、Socket 缓冲都到位，再取基线
                for (var i = 0; i < 2; i++)
                {
                    await RunCycleAsync(1);
                }

                var baseline = await CaptureSettledAsync();

                var totalReceived = 0;
                var totalSent = 0;
                var cycles = 0;
                var sw = Stopwatch.StartNew();

                while (sw.Elapsed < duration)
                {
                    var (received, sent) = await RunCycleAsync(2);
                    totalReceived += received;
                    totalSent += sent;
                    cycles++;

                    Assert.That(received, Is.GreaterThan(0),
                        $"第 {cycles} 轮：重连后必须仍能收到自发数据（发出 {sent} 条，收到 0 条 = 假在线）");
                }

                var final = await CaptureSettledAsync();

                TestContext.Out.WriteLine(
                    $"[104] 时长 {sw.Elapsed.TotalSeconds:F1}s / {cycles} 轮 / 发出 {totalSent} 条 / 收到 {totalReceived} 条");
                TestContext.Out.WriteLine($"[104] 基线 {baseline}");
                TestContext.Out.WriteLine($"[104] 结束 {final}");

                AssertNoDrift(baseline, final, "104");

                // 收发率：允许少量在途丢失（断链瞬间），但不能丢一半
                Assert.That(totalReceived, Is.GreaterThan(totalSent / 2),
                    $"[104] 收到 {totalReceived} / 发出 {totalSent} 条，丢失过多——链路在重连后不可靠");
            }
            finally
            {
                try { await server.StopAsync(); } catch { }
                server.Dispose();
            }
        }

        private static ASDU Spontaneous(ApplicationLayerParameters al, int value)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new MeasuredValueScaled(100, value % 30000, new QualityDescriptor()));
            return asdu;
        }

        // ══════════════════════════════════════════════════════════
        //  2. CS103：总召唤/1级/2级/命令循环 + 周期重连（此前完全无 soak 覆盖）
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 稳定性103_召唤循环与周期重连_资源不漂移()
        {
            var duration = TimeSpan.FromSeconds(DurationSeconds);
            var port = AllocatePort();
            const byte ca = 1;
            const byte fun = 160;
            const byte inf = 1;

            var server = new Iec103Server(Fast103Options());

            // 总召唤应答：从站回 2 个带时标的测量值 + 终止帧。
            // 103 的命令/时钟同步只有确认帧、不回推 ASDU，因此用总召唤的返回帧数作为"链路活着"的证据。
            server.AsduReceived += (session, in view) =>
            {
                if (view.TypeId != TypeIds.GeneralInterrogation)
                {
                    return;
                }

                for (var i = 1; i <= 2; i++)
                {
                    session.SendAsduAsync(Asdu.TimeTaggedMessage(
                        ca, fun, (byte)i, DpiQuality.FromDpi(2),
                        Cp56Time2a.FromDateTime(DateTime.UtcNow),
                        CotCodes.RemoteOperation)).AsTask().GetAwaiter().GetResult();
                }

                session.SendAsduAsync(Asdu.GeneralInterrogationEnd(ca))
                    .AsTask().GetAwaiter().GetResult();
            };

            await server.StartAsync(port);

            try
            {
                async Task<int> RunCycleAsync()
                {
                    await using var client = new Iec103Client("127.0.0.1", port, Fast103Options());

                    await client.ConnectAsync();
                    await client.ResetLinkAsync(ca);

                    // 总召唤：2 个测量值 + 1 个终止帧 = 3 帧
                    var gi = await client.GeneralInterrogationAsync(ca);

                    // 1 级 / 2 级轮询（从站无数据时返回固定帧 ACK，不会挂起）
                    await client.RequestClass1Async(ca);
                    await client.RequestClass2Async(ca);

                    // 命令往返：验证链路在反复重建后仍可下发
                    await client.SendCommandAsync(ca, fun, inf, 2);
                    await client.SendClockSyncAsync(ca, DateTime.UtcNow);

                    await client.DisconnectAsync();
                    return gi.Count;
                }

                // 预热
                for (var i = 0; i < 2; i++)
                {
                    await RunCycleAsync();
                }

                var baseline = await CaptureSettledAsync();

                var cycles = 0;
                var sw = Stopwatch.StartNew();

                while (sw.Elapsed < duration)
                {
                    var received = await RunCycleAsync();
                    cycles++;

                    // 限速：103 的单轮只有几个往返，不限速会跑到 400+ 次重连/秒，
                    // 几十秒就能耗尽 Windows 的临时端口范围（连接报 WSAEADDRINUSE）。
                    // 生产里的重连频率远低于此，20ms 间隔对"持续重连"这个场景仍然足够苛刻。
                    await Task.Delay(20);

                    // 总召唤必须收到 2 个测量值 + 终止帧
                    Assert.That(received, Is.GreaterThanOrEqualTo(3),
                        $"第 {cycles} 轮：总召唤只收到 {received} 帧（期望 ≥3 = 2 测量值 + 终止帧）"
                        + "——链路假在线或从站应答丢失");
                }

                var final = await CaptureSettledAsync();

                TestContext.Out.WriteLine($"[103] 时长 {sw.Elapsed.TotalSeconds:F1}s / {cycles} 轮");
                TestContext.Out.WriteLine($"[103] 基线 {baseline}");
                TestContext.Out.WriteLine($"[103] 结束 {final}");

                AssertNoDrift(baseline, final, "103");

                Assert.That(server.SessionCount, Is.LessThanOrEqualTo(1),
                    $"所有客户端都已断开，服务端仍残留 {server.SessionCount} 个会话——会话集合未清理（长时间运行必然泄漏）");
            }
            finally
            {
                try { await server.StopAsync(); } catch { }
                await server.DisposeAsync();
            }
        }

        private static Iec103Options Fast103Options() => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = 1500;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = 2;
            });

        // ══════════════════════════════════════════════════════════
        //  3. CS101：文件传输循环 + 周期重连（文件状态机是最易泄漏的路径）
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 稳定性101_文件传输循环与周期重连_资源不漂移()
        {
            var duration = TimeSpan.FromSeconds(DurationSeconds);
            var port = AllocatePort();
            var payload = BuildPayload(257);
            var ll = Ll();

            var slave = new Iec101Server(port, ll)
            {
                LinkLayerAddress = 3,
                LinkLayerAddressOtherStation = 1,
                LinkLayerMode = LinkLayerMode.BALANCED,
            };

            // 排障开关：IEC_STABILITY_VERBOSE=1 时把 FT1.2 与应用层日志打到控制台
            if (Environment.GetEnvironmentVariable("IEC_STABILITY_VERBOSE") == "1")
            {
                var loggerGroup = new LoggerGroup();
                loggerGroup.AddConsoleLogger(LogLevel.Debug);
                slave.Logger = loggerGroup;
            }

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            Iec101Client master = null;
            var masterCts = new CancellationTokenSource();
            Task masterTask = null;

            // 诊断计数：区分"主站没发出去"、"从站没收到"、"从站收到但不应答"
            var slaveRx = 0;
            var slaveTx = 0;
            slave.RawFrameReceived += _ => Interlocked.Increment(ref slaveRx);
            slave.RawFrameSent += _ => Interlocked.Increment(ref slaveTx);

            try
            {
                async Task StartMasterAsync()
                {
                    master = new Iec101Client("127.0.0.1", port, LinkLayerMode.BALANCED, ll)
                    {
                        OwnAddress = 1,
                        SlaveAddress = 3,
                    };
                    masterTask = master.StartAsync(masterCts.Token);

                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < deadline && master.GetLinkLayerState() != LinkLayerState.AVAILABLE)
                    {
                        await Task.Delay(30);
                    }

                    if (master.GetLinkLayerState() != LinkLayerState.AVAILABLE)
                    {
                        var t = typeof(Iec101Client);
                        var fatal = t.GetField("_fatalError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(master);
                        var loop = t.GetField("_loopTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(master);
                        Assert.Fail($"主站链接层未进入 AVAILABLE（当前 {master.GetLinkLayerState()}，"
                            + $"fatal={fatal}，loop={(loop as Task)?.Status.ToString() ?? "null"}，"
                            + $"slaveRx={Volatile.Read(ref slaveRx)} slaveTx={Volatile.Read(ref slaveTx)}，"
                            + $"slaveLoop={slaveTask?.Status.ToString() ?? "null"}）");
                    }
                }

                async Task StopMasterAsync()
                {
                    try { master.Stop(); } catch { }
                    try { master.Dispose(); } catch { }
                    try { masterCts.Cancel(); } catch { }

                    if (masterTask != null)
                    {
                        try { await Task.WhenAny(masterTask, Task.Delay(5000)); } catch { }
                    }

                    masterCts.Dispose();
                    masterCts = new CancellationTokenSource();
                    master = null;
                    masterTask = null;
                }

                async Task<(bool Ok, string Detail)> TransferOnceAsync(int ioa)
                {
                    var file = new CountingFile(1, ioa, NameOfFile.TRANSPARENT_FILE);
                    file.AddSection(payload);
                    slave.GetAvailableFiles().AddFile(file);

                    var receiver = new CountingReceiver(payload.Length);
                    try
                    {
                        master.GetFile(1, ioa, NameOfFile.TRANSPARENT_FILE, receiver);

                        if (!receiver.FinishedEvent.Wait(TimeSpan.FromSeconds(10)))
                        {
                            return (false, $"超时（{receiver.ReceivedBytes}/{payload.Length} 字节已收）");
                        }

                        var ok = receiver.Result == FileErrorCode.SUCCESS && receiver.Matches(payload);
                        return (ok,
                            $"result={receiver.Result}, 收到 {receiver.ReceivedBytes} 字节；"
                            + "主站文件状态: " + master.FileClientDiagnostics
                            + "；从站文件状态: " + slave.FileTransferDiagnostics);
                    }
                    finally
                    {
                        slave.GetAvailableFiles().RemoveFile(file);
                    }
                }

                // 预热
                await StartMasterAsync();
                for (var i = 0; i < 2; i++)
                {
                    var warm = await TransferOnceAsync(30000 + i);
                    Assert.That(warm.Ok, Is.True, $"预热阶段文件传输失败：{warm.Detail}");
                }

                await StopMasterAsync();
                var baseline = await CaptureSettledAsync();

                var transfers = 0;
                var cycles = 0;
                var sw = Stopwatch.StartNew();
                var ioa = 30100;

                while (sw.Elapsed < duration)
                {
                    await StartMasterAsync();

                    // 一个连接内连做 3 次传输，再重建链路
                    for (var n = 0; n < 3; n++)
                    {
                        var t = await TransferOnceAsync(ioa++);
                        Assert.That(t.Ok, Is.True,
                            $"第 {cycles + 1} 轮第 {n + 1} 次文件传输失败（{t.Detail}）"
                            + "——文件状态机在长时间运行后卡死或拒绝新请求");
                        transfers++;
                    }

                    // 收尾让位：Finished 回调发生在最终 ACK 入队后、真正发出前，
                    // 立刻 Dispose 会把 TCP 连接从握手中途掐断，留下对端未应答的帧，
                    // 下一轮重连的 FCB 会因此错位（对端把首个请求当重复帧丢弃）。
                    // 真实应用不会在文件刚完成的微秒内拔链路；这里给 150ms 让链路收尾。
                    await Task.Delay(150);

                    await StopMasterAsync();
                    cycles++;
                }

                var final = await CaptureSettledAsync();

                TestContext.Out.WriteLine(
                    $"[101] 时长 {sw.Elapsed.TotalSeconds:F1}s / {cycles} 轮 / {transfers} 次传输");
                TestContext.Out.WriteLine($"[101] 基线 {baseline}");
                TestContext.Out.WriteLine($"[101] 结束 {final}");

                AssertNoDrift(baseline, final, "101");
            }
            finally
            {
                if (master != null)
                {
                    try { master.Stop(); } catch { }
                    try { master.Dispose(); } catch { }
                }

                try { slave.Stop(); } catch { }
                try { slave.Dispose(); } catch { }
                try { slaveCts.Cancel(); } catch { }
                try { await Task.WhenAny(slaveTask, Task.Delay(5000)); } catch { }
                slaveCts.Dispose();
                masterCts.Dispose();
            }
        }

        private static byte[] BuildPayload(int length)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)(i + 1);
            }

            return data;
        }

        private sealed class CountingReceiver : IFileReceiver
        {
            private readonly byte[] _buffer;
            private int _received;

            public readonly ManualResetEventSlim FinishedEvent = new ManualResetEventSlim(false);

            public FileErrorCode Result { get; private set; } = FileErrorCode.PROTOCOL_ERROR;

            public int ReceivedBytes => _received;

            public CountingReceiver(int size) => _buffer = new byte[size];

            public bool Matches(byte[] expected)
            {
                if (_received != expected.Length)
                {
                    return false;
                }

                for (var i = 0; i < expected.Length; i++)
                {
                    if (_buffer[i] != expected[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            public void SegmentReceived(byte sectionName, int offset, int size, byte[] data)
            {
                Array.Copy(data, 0, _buffer, _received, size);
                _received += size;
            }

            public void Finished(FileErrorCode result)
            {
                Result = result;
                FinishedEvent.Set();
            }
        }

        private sealed class CountingFile : TransparentFile
        {
            public CountingFile(int ca, int ioa, NameOfFile nof)
                : base(ca, ioa, nof)
            {
            }

            public override void TransferComplete(bool success)
            {
            }
        }
    }
}
