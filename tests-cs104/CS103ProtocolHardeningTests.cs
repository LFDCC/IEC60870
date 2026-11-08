/*
 *  CS103ProtocolHardeningTests.cs
 *
 *  IEC60870.CS103 协议健壮性回归测试（对应本轮修复的四条缺陷）：
 *  1. 主站因 ACK 丢失原样重发 SEND/CONFIRM 帧时，从站只派发一次 ASDU，
 *     但两次都必须收到链路应答；
 *  2. TI=1 且数据区为空的最小 ASDU 不得让 TryDecode 抛异常；
 *  3. 扰动命令 ASDU 24/25 短帧应抛 FormatException 而非越界；
 *  4. CS103 ByteQueue 单块写入超过环形缓冲容量时保留最后 16KB 且计数不为负。
 *
 *  第 1 条使用真实 TCP 从站 + 裸 Socket 主站：手工发送同一帧两次，
 *  以验证「重复帧只重发应答、不重复执行应用层 ASDU」。
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS103ProtocolHardeningTests
    {
        private const byte Ca = 1;
        private const byte Fun = 160;
        private const byte Inf = 1;
        private const byte LinkAddress = 1;

        /// <summary>分配一个空闲端口（避免与本机其它测试冲突）。</summary>
        private static int AllocatePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static Iec103Options FastOptions() => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = 1500;
                l.CharacterTimeoutMs = 300;
                l.MaxRepeat = 1;
            });

        // ═══════════════════════════════════════════════════════════════
        //  缺陷 1：重复数据帧不得重复派发 ASDU
        // ═══════════════════════════════════════════════════════════════

        [Test]
        public async Task 重复数据帧_只派发一次ASDU且两次都有链路应答()
        {
            var port = AllocatePort();
            var asduCount = 0;

            await using var server = new Iec103Server(FastOptions());
            server.AsduReceived += (session, in view) =>
            {
                if (view.TypeId == TypeIds.GeneralCommand)
                {
                    Interlocked.Increment(ref asduCount);
                }
            };
            await server.StartAsync(port);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 3000;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await WaitUntilAsync(() => server.SessionCount >= 1, TimeSpan.FromSeconds(3));

            // 先复位链路（RESET CU），让会话进入已建链状态并读走固定帧 ACK。
            var reset = Ft12Frame.EncodeFixed(LinkControl.ResetCu().ToByte(), LinkAddress);
            await stream.WriteAsync(reset, 0, reset.Length);
            var resetAck = await ReadExactAsync(stream, Ft12Frame.FixedFrameLength, readCts.Token);
            Assert.That(Ft12Frame.TryDecode(resetAck, out _, out var resetError), Is.True, resetError);

            // 构造一帧 SEND/CONFIRM（FCB=0）承载 TI=20 一般命令（遥控/对时等命令的代表）。
            var payload = Asdu.GeneralCommand(Ca, Fun, Inf, dco: 1, rii: 0).Encode();
            var wire = Ft12Frame.EncodeVariable(LinkControl.SendConfirmed(fcb: false).ToByte(), LinkAddress, payload);

            // 第一次发送：应派发 ASDU，并收到 ACK。
            await stream.WriteAsync(wire, 0, wire.Length);
            var firstAck = await ReadExactAsync(stream, Ft12Frame.FixedFrameLength, readCts.Token);
            AssertIsAck(firstAck);

            // 同一帧原样重发（FCB 未翻转，模拟主站没收到 ACK 后的协议重发）：
            // 从站必须仍回应答，但绝不能再派发一次 ASDU。
            await stream.WriteAsync(wire, 0, wire.Length);
            var secondAck = await ReadExactAsync(stream, Ft12Frame.FixedFrameLength, readCts.Token);
            AssertIsAck(secondAck);

            await WaitUntilAsync(() => Volatile.Read(ref asduCount) >= 1, TimeSpan.FromSeconds(3));

            // 再留一小段时间，确认没有迟到的第二次派发。
            await Task.Delay(300);

            Assert.That(Volatile.Read(ref asduCount), Is.EqualTo(1), "重复帧不得再次派发 ASDU");
        }

        private static void AssertIsAck(byte[] ack)
        {
            Assert.That(Ft12Frame.TryDecode(ack, out var frame, out var error), Is.True, error);
            Assert.That(frame!.Format, Is.EqualTo(Ft12FrameFormat.FixedLength));

            var control = LinkControl.FromByte(frame.Control);
            Assert.That(control.IsPrimary, Is.False, "从站应答 PRM=0");
            Assert.That(control.Function, Is.EqualTo((byte)SecondaryFunction.Ack));
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset, ct);
                Assert.That(read, Is.GreaterThan(0), "连接在应答完整前关闭");
                offset += read;
            }

            return buffer;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !condition())
            {
                await Task.Delay(20);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  缺陷 2：TI=1 空数据区不得抛 IndexOutOfRangeException
        // ═══════════════════════════════════════════════════════════════

        [Test]
        public void TI1_空数据区_TryDecode不抛异常且返回空元素或false()
        {
            // 仅 6 字节 ASDU 头：TI=1、VSQ=1（声明 1 个元素）、数据区为空。
            // 修复前 DecodeMonitor 会退化为 count=1 后读取 data[0]，抛 IndexOutOfRangeException。
            var raw = new[]
            {
                TypeIds.TimeTaggedMessage,
                (byte)0x01,
                CotCodes.Spontaneous,
                Ca,
                Fun,
                Inf,
            };

            var view = new Iec103AsduView(raw);
            Iec103DecodedAsdu decoded = null;
            string error = null;
            var succeeded = false;
            var threw = false;

            try
            {
                succeeded = Iec103AsduDecoder.TryDecode(in view, out decoded, out error);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.That(threw, Is.False, "TryDecode 的契约是不抛异常");

            if (succeeded)
            {
                Assert.That(decoded, Is.InstanceOf<Iec103MonitorMessage>());
                Assert.That(((Iec103MonitorMessage)decoded!).Points, Is.Empty, "空数据区应得到空元素集");
            }
            else
            {
                Assert.That(error, Is.Not.Null.And.Not.Empty, "失败时必须给出原因");
            }
        }

        [Test]
        public void TI1_短数据区_TryDecode不抛异常()
        {
            // 数据区只有 3 字节（不足 8 字节完整元素），同样不得越界。
            var raw = new[]
            {
                TypeIds.TimeTaggedMessage,
                (byte)0x01,
                CotCodes.Spontaneous,
                Ca,
                Fun,
                Inf,
                (byte)0x01,
                (byte)0x00,
                (byte)0x00,
            };

            var view = new Iec103AsduView(raw);
            var threw = false;

            try
            {
                Iec103AsduDecoder.TryDecode(in view, out _, out _);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.That(threw, Is.False, "短数据区的 TryDecode 同样不得抛异常");
        }

        // ═══════════════════════════════════════════════════════════════
        //  缺陷 3：扰动命令解码的长度校验
        // ═══════════════════════════════════════════════════════════════

        [Test]
        public void 扰动命令_短帧_抛FormatException而非越界()
        {
            var shortCommand = new Asdu(
                TypeIds.DisturbanceCommand,
                true,
                1,
                CotCodes.DisturbanceTransfer,
                false,
                Ca,
                Asdu.GlobalFun,
                0,
                new byte[] { TooCodes.RequestDisturbanceData, TovCodes.InstantaneousValues, 0x01 });

            var ex = Assert.Throws<FormatException>(() => DisturbanceAsdu.DecodeCommand(shortCommand));
            Assert.That(ex!.Message, Does.Contain("5"), "错误消息应说明至少需要 5 字节");
        }

        [Test]
        public void 扰动命令_完整5字节_可正常解码()
        {
            var command = DisturbanceAsdu.DisturbanceCommand(
                TypeIds.DisturbanceCommand, Ca, TooCodes.RequestDisturbanceData, fan: 0x1234, acc: 2, cot: CotCodes.DisturbanceTransfer);

            var decoded = DisturbanceAsdu.DecodeCommand(command);

            Assert.That(decoded.Too, Is.EqualTo(TooCodes.RequestDisturbanceData));
            Assert.That(decoded.Tov, Is.EqualTo(TovCodes.InstantaneousValues));
            Assert.That(decoded.Fan, Is.EqualTo(0x1234));
            Assert.That(decoded.Acc, Is.EqualTo(2));
        }

        // ═══════════════════════════════════════════════════════════════
        //  缺陷 4：ByteQueue 单块超容量写入
        // ═══════════════════════════════════════════════════════════════

        [Test]
        public async Task ByteQueue_单块超过容量_保留最后16KB且不抛异常()
        {
            const int capacity = 16 * 1024;
            const int inputLength = 40 * 1024;

            var queue = new ByteQueue(capacity);
            var input = new byte[inputLength];
            for (var i = 0; i < input.Length; i++)
            {
                input[i] = (byte)(i % 251);
            }

            // 修复前：_count 会变成负数，AppendUnsafe 越界或打乱字节顺序。
            queue.Write(input);

            var output = new byte[inputLength];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var read = await queue.ReadAsync(output, cts.Token);

            Assert.That(read, Is.EqualTo(capacity), "溢出后应保留恰好一个缓冲容量的数据");
            Assert.That(
                output.AsSpan(0, read).SequenceEqual(input.AsSpan(inputLength - capacity)),
                Is.True,
                "保留的必须是输入的最后 16KB");
            Assert.That(queue.BufferedBytes, Is.EqualTo(0));
            Assert.That(queue.DroppedBytes, Is.GreaterThanOrEqualTo(inputLength - capacity));
        }
    }
}
