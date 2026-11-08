/*
 *  CS101SerialAndStreamTests.cs
 *
 *  CS101 串口 / 任意字节流路径测试。
 *
 *  串口无法在 CI 挂真硬件，但串口与 TCP 在字节层面完全一致（裸 FT1.2），
 *  因此复用 CS103 系列测试里的内存双工流 PipePair103 就能完整跑主从流程；
 *  真实 SerialPort 只做「构造不崩、不擅自打开」的所有权约定验证。
 *
 *  覆盖两个此前的空白：
 *  1. Stream 构造重载（Iec101Client(Stream) / Iec101Server(Stream)）的端到端流程 ——
 *     此前所有 CS101 端到端测试都只走 TCP；
 *  2. 串口所有权约定：调用方传入未打开的 SerialPort 时，构造阶段不得读取 BaseStream
 *     （SerialPort.BaseStream 在端口未打开时抛 InvalidOperationException），
 *     也不得擅自 Open —— 打开由 StartAsync 负责。
 */

using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS101SerialAndStreamTests
    {
        /// <summary>一个几乎肯定不存在的串口名（本用例不会真的打开它）。</summary>
        private static SerialPort MakeClosedPort()
        {
            return new SerialPort("COM_CS101_TEST_NONEXISTENT", 9600, Parity.None, 8, StopBits.One);
        }

        [Test]
        public void 未打开的SerialPort_构造主站与从站都不应抛异常()
        {
            var port = MakeClosedPort();

            Assert.DoesNotThrow(() =>
            {
                var master = new Iec101Client(port, LinkLayerMode.BALANCED);
                master.Dispose();
            }, "主站构造阶段不应读取 SerialPort.BaseStream（未打开时读取会抛 InvalidOperationException）");

            Assert.DoesNotThrow(() =>
            {
                var slave = new Iec101Server(port);
                slave.Dispose();
            }, "从站构造阶段不应读取 SerialPort.BaseStream");

            Assert.That(port.IsOpen, Is.False,
                "构造阶段不得擅自打开端口 —— 打开时机由 StartAsync 决定（调用方拥有串口）");
        }

        [Test]
        public async Task Stream传输_平衡链路总召唤往返()
        {
            var ll = new LinkLayerParameters
            {
                AddressLength = 1,
                UseSingleCharACK = true,
            };
            var al = new ApplicationLayerParameters();

            using var pipes = new PipePair103();

            // ── 从站（Stream）────────────────────────────────────
            var slave = new Iec101Server(pipes.Slave, ll);
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

            // ── 主站（Stream）────────────────────────────────────
            var master = new Iec101Client(pipes.Master, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;

            var received = 0;
            master.SetASDUReceivedHandler((param, address, asdu) =>
            {
                Interlocked.Increment(ref received);
                return true;
            }, null);

            var masterCts = new CancellationTokenSource();
            // 用 Task.Run 驱动：PipeStream 的读是无 await 的同步实现，
            // 直接调用 StartAsync 会让收发循环占住测试线程（真实串口/TCP 流是真异步，不会这样）
            var masterTask = Task.Run(() => master.StartAsync(masterCts.Token), CancellationToken.None);

            // 等待平衡链路握手完成
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (master.GetLinkLayerState() != LinkLayerState.AVAILABLE && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.That(master.GetLinkLayerState(), Is.EqualTo(LinkLayerState.AVAILABLE),
                "Stream 传输下平衡链路握手应完成");

            master.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);

            deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref received) < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var count = Volatile.Read(ref received);

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
                $"Stream 传输下总召唤应收到 3 个 ASDU (ACT_CON/M_ME_NB_1/ACT_TERM)，实际 {count}");
        }
    }
}
