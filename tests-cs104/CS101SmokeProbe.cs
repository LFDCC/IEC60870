/*
 *  CS101SmokeProbe.cs
 *
 *  CS101 FT1.2-over-TCP 冒烟探针：进程内同时运行从站(Iec101Server)与主站(Iec101Client)，
 *  验证链接层 BALANCED 握手 → 主站发送总召唤 → 从站应答数据 → 主站收到 ASDU 的端到端链路。
 *  用于 4c（字节读写层重写）前后的行为基线对比。
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
    public class CS101SmokeProbeTests
    {
        private const int Port = 24101;

        [Test]
        public async Task MasterSlave_TcpBalanced_Interrogation_Roundtrip()
        {
            var ll = new LinkLayerParameters
            {
                AddressLength = 1,
                UseSingleCharACK = true,
            };
            var al = new ApplicationLayerParameters();

            // ── 从站 ─────────────────────────────────────────────
            var slave = new Iec101Server(Port, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;
            slave.SetInterrogationHandler((parameter, connection, asdu, qoi) =>
            {
                connection.SendACT_CON(asdu, false);
                var resp = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);
                resp.AddInformationObject(new MeasuredValueScaled(100, 1234, new QualityDescriptor()));
                connection.SendASDU(resp);
                connection.SendACT_TERM(asdu);
                return true;
            }, null);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(500);

            // ── 主站 ─────────────────────────────────────────────
            var master = new Iec101Client("127.0.0.1", Port, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;
            var received = 0;
            var receivedAsdus = new System.Collections.Concurrent.ConcurrentQueue<string>();
            master.SetASDUReceivedHandler((param, address, asdu) =>
            {
                received++;
                receivedAsdus.Enqueue(asdu.ToString());
                return true;
            }, null);

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);
            await Task.Delay(1500);

            master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);

            // 等待从站应答数据到达主站
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (received < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            // 清理
            master.Stop();
            slave.Stop();
            master.Dispose();
            slave.Dispose();
            slaveCts.Cancel();
            masterCts.Cancel();
            try
            {
                await Task.WhenAll(slaveTask, masterTask);
            }
            catch { /* 取消即退出 */ }

            // 断言：链接层握手 + 总召唤 → ACT_CON → 数据 → ACT_TERM 完整往返
            Assert.That(master.GetLinkLayerState(), Is.EqualTo(LinkLayerState.AVAILABLE),
                $"CS101 主站链接层应可用。收到 {received} ASDU");
            Assert.That(received, Is.EqualTo(3),
                $"CS101 总召唤应收到 3 个 ASDU (ACT_CON/M_ME_NB_1/ACT_TERM)，实际收到 {received}");
            foreach (var s in receivedAsdus)
            {
                Console.WriteLine($"[master] {s}");
            }

            Assert.That(HasAsdu(receivedAsdus, "COT: ACTIVATION_CON"), Is.True,
                "应收到总召唤激活确认");
            Assert.That(HasAsdu(receivedAsdus, "TypeID: M_ME_NB_1 COT: INTERROGATED_BY_STATION"), Is.True,
                "应收到总召唤数据");
        }

        private static bool HasAsdu(System.Collections.Concurrent.ConcurrentQueue<string> queue, string fragment)
        {
            foreach (var s in queue)
            {
                if (s.Contains(fragment))
                {
                    return true;
                }
            }

            return false;
        }

        [Test, Explicit("手动冒烟探针，打印逐帧日志")]
        public async Task Smoke()
        {
            var ll = new LinkLayerParameters
            {
                AddressLength = 1,
                UseSingleCharACK = true,
            };
            var al = new ApplicationLayerParameters();

            // ── 从站 ─────────────────────────────────────────────
            var slave = new Iec101Server(Port, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;
            slave.SetInterrogationHandler((parameter, connection, asdu, qoi) =>
            {
                Console.WriteLine($"[slave] interrogation qoi={qoi}");
                connection.SendACT_CON(asdu, false);
                var resp = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);
                resp.AddInformationObject(new MeasuredValueScaled(100, 1234, new QualityDescriptor()));
                connection.SendASDU(resp);
                connection.SendACT_TERM(asdu);
                return true;
            }, null);
            slave.RawFrameReceived += b => Console.WriteLine($"[slave RX] {BitConverter.ToString(b)}");
            slave.RawFrameSent += b => Console.WriteLine($"[slave TX] {BitConverter.ToString(b)}");

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(500);

            // ── 主站 ─────────────────────────────────────────────
            var master = new Iec101Client("127.0.0.1", Port, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;
            var received = 0;
            master.SetASDUReceivedHandler((param, address, asdu) =>
            {
                received++;
                Console.WriteLine($"[master] ASDU received: {asdu}");
                return true;
            }, null);
            master.SetLinkLayerStateChangedHandler((param, address, state) =>
                Console.WriteLine($"[master] link state: {state}"), null);
            master.RawFrameReceived += b => Console.WriteLine($"[master RX] {BitConverter.ToString(b)}");
            master.RawFrameSent += b => Console.WriteLine($"[master TX] {BitConverter.ToString(b)}");

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);
            await Task.Delay(1500);

            Console.WriteLine($"[master] link state after connect: {master.GetLinkLayerState()}");
            master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);

            // 等待从站应答数据到达主站
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (received == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            Console.WriteLine($"[RESULT] master received {received} ASDU(s); link state={master.GetLinkLayerState()}");

            master.Stop();
            slave.Stop();
            master.Dispose();
            slave.Dispose();
            slaveCts.Cancel();
            masterCts.Cancel();
            try
            {
                await Task.WhenAll(slaveTask, masterTask);
            }
            catch { /* 取消即退出 */ }
        }
    }
}
