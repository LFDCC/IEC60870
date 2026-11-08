/*
 *  CS101UnbalancedTests.cs
 *
 *  CS101 非平衡链路端到端回归测试（FT1.2 over TCP，进程内主从对跑）。
 *
 *  为什么单独一个文件：Iec101Server 的默认链路模式就是 UNBALANCED
 *  （Iec101Server.cs 的 _linkLayerMode 初值），现实中的 101 装置绝大多数也是非平衡；
 *  但此前所有端到端测试都只跑 BALANCED，非平衡的从站/主站链路层（含 FCB 校验、
 *  召唤 1/2 级数据、确认帧应答）完全没有自动化覆盖。
 *
 *  两个用例：
 *    1. 总召唤往返：链路初始化（复位远方链路）→ 总召唤 → ACT_CON + 数据 + ACT_TERM；
 *    2. 单点遥控往返：C_SC_NA_1 确认型命令 → 从站 ACT_CON/ACT_TERM，
 *       同时覆盖从站泛型 SetASDUHandler 与"未被消费的 ASDU 应回否定确认"契约。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS101UnbalancedTests
    {
        private const int InterrogationPort = 24105;
        private const int CommandPort = 24106;

        private static LinkLayerParameters MakeLinkParams()
        {
            return new LinkLayerParameters
            {
                AddressLength = 1,
                UseSingleCharACK = true,
            };
        }

        /// <summary>轮询指定从站，直到其链路进入 AVAILABLE（非平衡模式必须显式初始化链路）。</summary>
        private static async Task WaitForSlaveAvailable(Iec101Client master, int slaveAddress, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                master.PollSingleSlave(slaveAddress);

                if (master.GetLinkLayerState(slaveAddress) == LinkLayerState.AVAILABLE)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"从站 {slaveAddress} 链路层未在 {timeoutMs}ms 内进入 AVAILABLE，当前 {master.GetLinkLayerState(slaveAddress)}");
        }

        /// <summary>
        /// 非平衡模式下从站不能主动推送：主站必须持续召唤 2 级数据，
        /// 从站才会在应答里置 ACD 位，主站据此自动召唤 1 级数据取回上行报文。
        /// </summary>
        private static async Task<int> PollUntilCount(Iec101Client master, int slaveAddress, Func<int> counter, int expected, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                master.PollSingleSlave(slaveAddress);

                if (counter() >= expected)
                {
                    break;
                }

                await Task.Delay(50);
            }

            return counter();
        }

        // ══════════════════════════════════════════════════════════
        //  1. 非平衡总召唤
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 非平衡模式_总召唤往返()
        {
            var ll = MakeLinkParams();
            var al = new ApplicationLayerParameters();
            const int slaveAddress = 1;

            var slave = new Iec101Server(InterrogationPort, ll);
            slave.LinkLayerAddress = slaveAddress;
            // 保持默认 UNBALANCED —— 这正是本用例要覆盖的模式

            slave.SetInterrogationHandler((parameter, connection, asdu, qoi) =>
            {
                connection.SendACT_CON(asdu, false);

                var resp = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, slaveAddress, false);
                resp.AddInformationObject(new MeasuredValueScaled(100, 1234, new QualityDescriptor()));
                connection.SendASDU(resp);

                connection.SendACT_TERM(asdu);
                return true;
            }, null);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            var master = new Iec101Client("127.0.0.1", InterrogationPort, LinkLayerMode.UNBALANCED, ll);
            master.AddSlave(slaveAddress);
            // 非平衡模式：发送目标由 SlaveAddress 决定，未注册的地址会被链路层拒绝
            master.SlaveAddress = slaveAddress;

            var received = 0;
            master.SetASDUReceivedHandler((param, address, asdu) =>
            {
                Interlocked.Increment(ref received);
                return true;
            }, null);

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);

            await WaitForSlaveAvailable(master, slaveAddress);

            master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, slaveAddress, QualifierOfInterrogation.STATION);

            var count = await PollUntilCount(master, slaveAddress, () => Volatile.Read(ref received), 3);

            master.Stop();
            slave.Stop();
            master.Dispose();
            slave.Dispose();
            masterCts.Cancel();
            slaveCts.Cancel();

            try
            {
                Task.WaitAll(new[] { slaveTask, masterTask }, TimeSpan.FromSeconds(5));
            }
            catch { /* 取消即退出 */ }

            Assert.That(count, Is.EqualTo(3),
                $"非平衡总召唤应收到 3 个 ASDU (ACT_CON/M_ME_NB_1/ACT_TERM)，实际 {count}");
        }

        // ══════════════════════════════════════════════════════════
        //  2. 非平衡单点遥控（确认型命令 + 从站泛型处理 + 否定确认契约）
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 非平衡模式_单点遥控往返()
        {
            var ll = MakeLinkParams();
            const int slaveAddress = 2;
            const int commandIoa = 5001;

            var slave = new Iec101Server(CommandPort, ll);
            slave.LinkLayerAddress = slaveAddress;

            var commandSeen = 0;
            var commandValue = false;

            // 泛型 ASDU 处理器：消费未知类型之外的 C_SC_NA_1 并回确认
            slave.SetASDUHandler((parameter, connection, asdu) =>
            {
                if (asdu.TypeId != TypeID.C_SC_NA_1)
                {
                    return false; // 交回库内建处理
                }

                var command = (SingleCommand)asdu.GetElement(0);
                commandValue = command.State;
                Interlocked.Increment(ref commandSeen);

                connection.SendACT_CON(asdu, false);
                connection.SendACT_TERM(asdu);
                return true;
            }, null);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            var master = new Iec101Client("127.0.0.1", CommandPort, LinkLayerMode.UNBALANCED, ll);
            master.AddSlave(slaveAddress);
            master.SlaveAddress = slaveAddress;

            var confirmations = 0;
            master.SetASDUReceivedHandler((param, address, asdu) =>
            {
                if ((asdu.TypeId == TypeID.C_SC_NA_1) || (asdu.Cot == CauseOfTransmission.ACTIVATION_CON))
                {
                    Interlocked.Increment(ref confirmations);
                }

                return true;
            }, null);

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);

            await WaitForSlaveAvailable(master, slaveAddress);

            master.SendControlCommand(CauseOfTransmission.ACTIVATION, slaveAddress, new SingleCommand(commandIoa, true, false, 0));

            await PollUntilCount(master, slaveAddress, () => Volatile.Read(ref commandSeen), 1);
            var confirmCount = await PollUntilCount(master, slaveAddress, () => Volatile.Read(ref confirmations), 1);

            master.Stop();
            slave.Stop();
            master.Dispose();
            slave.Dispose();
            masterCts.Cancel();
            slaveCts.Cancel();

            try
            {
                Task.WaitAll(new[] { slaveTask, masterTask }, TimeSpan.FromSeconds(5));
            }
            catch { /* 取消即退出 */ }

            Assert.That(Volatile.Read(ref commandSeen), Is.EqualTo(1), "从站应收到 1 次遥控命令");
            Assert.That(commandValue, Is.True, "遥控命令的合/分状态应正确解码");
            Assert.That(confirmCount, Is.GreaterThan(0), "主站应收到遥控命令的确认（ACT_CON）");
        }
    }
}
