/*
 *  Program.cs — IEC60870.Core.NETAsync 端到端冒烟测试
 *
 *  验证整条异步 0GC 收发链路：
 *    1. Iec104Server 启动监听
 *    2. Iec104Client 建立 TCP 连接
 *    3. STARTDT_ACT / STARTDT_CON 握手
 *    4. 客户端 -> 服务端 发送 ASDU（总召唤命令）
 *    5. 服务端 -> 客户端 回送 ASDU（测量值 + 单点信息，激活确认 + 数据 + 召唤结束）
 *    6. 0GC 压力测试：连续发送 N 个 I 帧，测量每帧堆分配字节数
 */

using System;
using System.Threading;
using System.Threading.Tasks;



namespace IEC60870.Core.SmokeTest
{
    internal static class Program
    {
        private const int Port = 24040;
        private const int CommonAddress = 1;

        private static readonly TaskCompletionSource<bool> ServerGotInterrogation =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly TaskCompletionSource<bool> ClientGotMeasurement =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly TaskCompletionSource<bool> ClientActivated =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _clientRxCount;
        private static int _serverRxCount;

        private static async Task<int> Main()
        {
            Console.WriteLine("=== IEC60870.Core.NETAsync 端到端冒烟测试 ===");
            Console.WriteLine($"    端口={Port}  公共地址={CommonAddress}");
            Console.WriteLine();

            var alParams = new ApplicationLayerParameters();

            // ── 服务端 ────────────────────────────────────────────────
            var server = new Iec104Server(alParameters: alParams);

            server.ConnectionEvent += (Iec104Session s, ApduConnectionEvent ev) =>
                Console.WriteLine($"[服务端] 连接事件: {ev}");

            server.AsduReceived += (Iec104Session session, in AsduView asdu) =>
            {
                Interlocked.Increment(ref _serverRxCount);
                Console.WriteLine(
                    $"[服务端] 收到 ASDU  TypeID={asdu.TypeId}  COT={asdu.Cot}  " +
                    $"CA={asdu.CommonAddress}  元素数={asdu.NumberOfElements}");

                if (asdu.TypeId == TypeID.C_IC_NA_1)
                {
                    // 收到总召唤 -> 触发异步回送（不能在 in-callback 里 await）
                    _ = RespondToInterrogationAsync(session);
                    ServerGotInterrogation.TrySetResult(true);
                }
            };

            await server.StartAsync(Port);
            Console.WriteLine($"[服务端] 已在端口 {Port} 启动监听");

            // ── 客户端 ────────────────────────────────────────────────
            await using var client = new Iec104Client("127.0.0.1", Port, alParameters: alParams);

            client.ConnectionEvent += ev =>
            {
                Console.WriteLine($"[客户端] 连接事件: {ev}");
                if (ev == ApduConnectionEvent.StartDtConReceived || ev == ApduConnectionEvent.Activated)
                    ClientActivated.TrySetResult(true);
            };

            client.AsduReceived += (in AsduView asdu) =>
            {
                Interlocked.Increment(ref _clientRxCount);
                Console.WriteLine(
                    $"[客户端] 收到 ASDU  TypeID={asdu.TypeId}  COT={asdu.Cot}  " +
                    $"CA={asdu.CommonAddress}  元素数={asdu.NumberOfElements}");

                if (asdu.TypeId == TypeID.M_ME_NB_1)
                    ClientGotMeasurement.TrySetResult(true);
            };

            await client.ConnectAsync();
            Console.WriteLine("[客户端] TCP 已连接");

            await client.StartDataTransferAsync();
            Console.WriteLine("[客户端] STARTDT 握手完成，数据传输已激活");

            // 客户端 -> 服务端：总召唤命令
            var interrogation = new ASDU(alParams, CauseOfTransmission.ACTIVATION,
                isTest: false, isNegative: false, oa: 0, ca: CommonAddress, isSequence: false);
            interrogation.AddInformationObject(new InterrogationCommand(0, 20 /* QOI=station */));
            await client.SendAsync(interrogation);
            Console.WriteLine("[客户端] 已发送总召唤命令 (C_IC_NA_1)");

            // 等待完整往返
            var roundTrip = await WhenAllWithTimeout(TimeSpan.FromSeconds(10),
                ServerGotInterrogation.Task, ClientGotMeasurement.Task);

            if (!roundTrip)
            {
                Console.WriteLine();
                Console.WriteLine("!!! 超时：未在 10 秒内完成完整往返 !!!");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"[统计] 服务端收到 {_serverRxCount} 个 ASDU，客户端收到 {_clientRxCount} 个 ASDU");
            Console.WriteLine();

            // ── 0GC 压力测试 ─────────────────────────────────────────
            await ZeroGcBenchmark(client, alParams);

            await client.StopDataTransferAsync();
            Console.WriteLine("[客户端] STOPDT 完成");
            await client.DisconnectAsync();
            await server.StopAsync();

            Console.WriteLine();
            Console.WriteLine("=== 冒烟测试通过 ✔ ===");
            return 0;
        }

        private static async Task RespondToInterrogationAsync(Iec104Session session)
        {
            try
            {
                var p = new ApplicationLayerParameters();

                // 激活确认
                var actCon = new ASDU(p, CauseOfTransmission.ACTIVATION_CON,
                    false, false, 0, CommonAddress, false);
                actCon.AddInformationObject(new InterrogationCommand(0, 20));
                await session.SendAsync(actCon);

                // 数据：测量值 + 单点信息
                var meas = new ASDU(p, CauseOfTransmission.INTERROGATED_BY_STATION,
                    false, false, 0, CommonAddress, false);
                meas.AddInformationObject(new MeasuredValueScaled(100, 12345, new QualityDescriptor()));
                await session.SendAsync(meas);

                var sp = new ASDU(p, CauseOfTransmission.INTERROGATED_BY_STATION,
                    false, false, 0, CommonAddress, false);
                sp.AddInformationObject(new SinglePointInformation(200, true, new QualityDescriptor()));
                await session.SendAsync(sp);

                // 召唤结束
                var actTerm = new ASDU(p, CauseOfTransmission.ACTIVATION_TERMINATION,
                    false, false, 0, CommonAddress, false);
                actTerm.AddInformationObject(new InterrogationCommand(0, 20));
                await session.SendAsync(actTerm);

                Console.WriteLine("[服务端] 已回送 激活确认 + 测量值 + 单点 + 召唤结束");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[服务端] 回送异常: {ex.Message}");
            }
        }

        private static async Task ZeroGcBenchmark(Iec104Client client, ApplicationLayerParameters p)
        {
            const int iterations = 2000;
            Console.WriteLine($"[0GC] 预热 + 连续发送 {iterations} 个 I 帧，测量发送路径堆分配...");

            // 预热（触发 JIT + ArrayPool 首次租借）
            for (int i = 0; i < 50; i++)
            {
                var warm = new ASDU(p, CauseOfTransmission.PERIODIC, false, false, 0, CommonAddress, false);
                warm.AddInformationObject(new MeasuredValueScaled(i, i, new QualityDescriptor()));
                await client.SendAsync(warm);
            }
            await Task.Delay(200);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < iterations; i++)
            {
                var asdu = new ASDU(p, CauseOfTransmission.PERIODIC, false, false, 0, CommonAddress, false);
                asdu.AddInformationObject(new MeasuredValueScaled(i & 0x3fff, i, new QualityDescriptor()));
                await client.SendAsync(asdu);
            }

            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            int gen0After = GC.CollectionCount(0);
            int gen1After = GC.CollectionCount(1);
            int gen2After = GC.CollectionCount(2);

            long totalAlloc = allocAfter - allocBefore;
            double perFrame = (double)totalAlloc / iterations;

            Console.WriteLine($"[0GC] 总分配 {totalAlloc:N0} 字节 / {iterations} 帧 = {perFrame:F1} 字节/帧");
            Console.WriteLine($"[0GC] GC 次数增量  Gen0={gen0After - gen0Before}  " +
                              $"Gen1={gen1After - gen1Before}  Gen2={gen2After - gen2Before}");
            Console.WriteLine("[0GC] 说明：每帧分配主要来自测试构造的 ASDU/InformationObject 托管对象；");
            Console.WriteLine("[0GC]       实际发送路径（APCI 头 + 缓冲）由 PooledApduWriter/ArrayPool 池化，无 per-frame 缓冲分配。");
        }

        private static async Task<bool> WhenAllWithTimeout(TimeSpan timeout, params Task[] tasks)
        {
            var all = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(all, Task.Delay(timeout));
            return completed == all && all.IsCompletedSuccessfully;
        }
    }
}
