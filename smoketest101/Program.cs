/*
 *  Program.cs — IEC60870.Core.NETAsync CS101 端到端冒烟测试（基于 TouchSocket TCP 隧道）
 *
 *  验证整条 CS101 异步链路（FT1.2 帧 + 链路层状态机 + 应用层），底层走
 *  TcpClientLinkTransport / TcpServerLinkTransport（TouchSocket）：
 *    1. Iec101Server 在端口监听（TCP 隧道）
 *    2. Iec101Client 经 TCP 连接（TouchSocket）
 *    3. 平衡模式链路层自动建链（REQUEST_LINK_STATUS -> RESET_REMOTE_LINK -> AVAILABLE）
 *    4. 主站 -> 从站 发送总召唤命令（C_IC_NA_1）
 *    5. 从站 -> 主站 回送（ACT_CON + 测量值 + 单点 + ACT_TERM）
 *    6. 验证完整往返与收发计数
 */

using System;
using System.Threading;
using System.Threading.Tasks;



namespace IEC60870.Core.SmokeTest
{
    internal static class Program
    {
        private const int Port = 24041;
        private const int CommonAddress = 1;
        private const int MasterLinkAddress = 1;
        private const int SlaveLinkAddress = 3;

        private static readonly TaskCompletionSource<bool> SlaveGotInterrogation =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly TaskCompletionSource<bool> MasterGotActTerm =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _masterRxCount;
        private static int _slaveRxCount;

        private static async Task<int> Main()
        {
            Console.WriteLine("=== IEC60870.Core.NETAsync CS101 端到端冒烟测试（TouchSocket TCP 隧道）===");
            Console.WriteLine($"    端口={Port}  主站链路地址={MasterLinkAddress}  从站链路地址={SlaveLinkAddress}");
            Console.WriteLine();

            var llParams = new LinkLayerParameters();
            llParams.AddressLength = 1;
            llParams.UseSingleCharACK = true;
            llParams.TimeoutForACK = 500;

            var alParams = new ApplicationLayerParameters();

            using var cts = new CancellationTokenSource();

            // ── 从站 ────────────────────────────────────────────────
            var slave = new Iec101Server(Port, llParams);
            slave.DebugOutput = false;
            slave.LinkLayerAddress = SlaveLinkAddress;
            slave.LinkLayerAddressOtherStation = MasterLinkAddress;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;
            slave.SetInterrogationHandler((parameter, connection, asdu, qoi) =>
            {
                Interlocked.Increment(ref _slaveRxCount);
                Console.WriteLine($"[从站] 收到总召唤 C_IC_NA_1  QOI={qoi}");

                connection.SendACT_CON(asdu, false);

                var data = new ASDU(connection.GetApplicationLayerParameters(),
                    CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, CommonAddress, false);
                data.AddInformationObject(new MeasuredValueScaled(100, 12345, new QualityDescriptor()));
                data.AddInformationObject(new MeasuredValueScaled(101, 2300, new QualityDescriptor()));
                connection.SendASDU(data);

                var sp = new ASDU(connection.GetApplicationLayerParameters(),
                    CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, CommonAddress, false);
                sp.AddInformationObject(new SinglePointInformation(200, true, new QualityDescriptor()));
                connection.SendASDU(sp);

                connection.SendACT_TERM(asdu);
                Console.WriteLine("[从站] 已回送 ACT_CON + 测量值 + 单点 + ACT_TERM");

                SlaveGotInterrogation.TrySetResult(true);
                return true;
            }, null);

            var slaveTask = slave.StartAsync(cts.Token);
            Console.WriteLine($"[从站] 已在端口 {Port} 启动监听（TCP 隧道）");

            // 等待从站进入监听态，再启动主站
            await Task.Delay(500, cts.Token);

            // ── 主站 ────────────────────────────────────────────────
            var master = new Iec101Client("127.0.0.1", Port, LinkLayerMode.BALANCED, llParams, alParams);
            master.DebugOutput = false;
            master.OwnAddress = MasterLinkAddress;
            master.SlaveAddress = SlaveLinkAddress;
            master.SetASDUReceivedHandler((parameter, address, asdu) =>
            {
                Interlocked.Increment(ref _masterRxCount);
                Console.WriteLine(
                    $"[主站] 收到 ASDU  TypeID={asdu.TypeId}  COT={asdu.Cot}  " +
                    $"CA={asdu.Ca}  元素数={asdu.NumberOfElements}");

                if (asdu.TypeId == TypeID.C_IC_NA_1 &&
                    asdu.Cot == CauseOfTransmission.ACTIVATION_TERMINATION)
                {
                    MasterGotActTerm.TrySetResult(true);
                }

                return true;
            }, null);

            var masterTask = master.StartAsync(cts.Token);
            Console.WriteLine("[主站] 已启动并连接（TouchSocket）");

            // ── 等待链路建立 ────────────────────────────────────────
            Console.WriteLine("[主站] 等待链路层建立...");
            bool linkUp = await WaitForLinkAvailable(master, TimeSpan.FromSeconds(10));
            if (!linkUp)
            {
                Console.WriteLine();
                Console.WriteLine("!!! 超时：10 秒内链路层未进入 AVAILABLE !!!");
                await Shutdown(slave, master, cts, slaveTask, masterTask);
                return 1;
            }
            Console.WriteLine($"[主站] 链路层状态 = {master.GetLinkLayerState()}");

            // ── 主站发送总召唤 ──────────────────────────────────────
            master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, CommonAddress, 20);
            Console.WriteLine("[主站] 已发送总召唤命令 (C_IC_NA_1)");

            // ── 等待完整往返 ────────────────────────────────────────
            var roundTrip = await WhenAllWithTimeout(TimeSpan.FromSeconds(10),
                SlaveGotInterrogation.Task, MasterGotActTerm.Task);

            if (!roundTrip)
            {
                Console.WriteLine();
                Console.WriteLine("!!! 超时：未在 10 秒内完成完整往返 !!!");
                await Shutdown(slave, master, cts, slaveTask, masterTask);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"[统计] 从站收到 {_slaveRxCount} 个 ASDU，主站收到 {_masterRxCount} 个 ASDU");

            await Shutdown(slave, master, cts, slaveTask, masterTask);

            Console.WriteLine();
            Console.WriteLine("=== CS101 冒烟测试通过 ✔ ===");
            return 0;
        }

        private static async Task<bool> WaitForLinkAvailable(Iec101Client master, TimeSpan timeout)
        {
            var deadline = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds;
            while (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
            {
                if (master.GetLinkLayerState() == LinkLayerState.AVAILABLE)
                    return true;
                await Task.Delay(100);
            }
            return false;
        }

        private static async Task Shutdown(Iec101Server slave, Iec101Client master,
            CancellationTokenSource cts, Task slaveTask, Task masterTask)
        {
            try { master.StopAsync(); } catch { }
            try { slave.Stop(); } catch { }
            cts.Cancel();

            try { await slaveTask.ConfigureAwait(false); } catch { }
            try { await masterTask.ConfigureAwait(false); } catch { }
        }

        private static async Task<bool> WhenAllWithTimeout(TimeSpan timeout, params Task[] tasks)
        {
            var all = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(all, Task.Delay(timeout));
            return completed == all && all.IsCompletedSuccessfully;
        }
    }
}
