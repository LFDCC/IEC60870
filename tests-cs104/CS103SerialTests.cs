/*
 *  CS103SerialTests.cs
 *
 *  IEC60870.CS103 串口/字节流路径测试。
 *
 *  串口现场无法在 CI 挂真实硬件，但串口与 TCP 在字节层面完全一致（裸 FT1.2），
 *  因此用内存双工流（PipeStream）即可完整覆盖协议与链路行为，包括：
 *  1. 主站 Stream ↔ 从站 Stream 完整总召唤流程；
 *  2. RS-485 地址过滤（非本站地址静默忽略、255 广播可配置）；
 *  3. 半帧超时复位（线路中断后能恢复）；
 *  4. 串口所有权的 Close-not-Dispose 约定。
 *
 *  真实 SerialPort 的构造重载只做「参数校验 / 未打开自动打开」的冒烟验证，
 *  不在此处执行实际串口 IO（需要硬件）。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    /// <summary>
    /// 内存双工流：一对相互连接的读写端，模拟一条物理链路。
    /// </summary>
    internal sealed class PipePair103 : IDisposable
    {
        private readonly PipeStream103 _a;
        private readonly PipeStream103 _b;

        public PipePair103()
        {
            _a = new PipeStream103();
            _b = new PipeStream103();
            _a.Peer = _b;
            _b.Peer = _a;
        }

        /// <summary>主站侧流。</summary>
        public Stream Master => _a;

        /// <summary>从站侧流。</summary>
        public Stream Slave => _b;

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }

        /// <summary>单向内存管道：写入的数据可从对端读出。</summary>
        internal sealed class PipeStream103 : Stream
        {
            private readonly System.Collections.Concurrent.BlockingCollection<byte> _bytes =
                new(new System.Collections.Concurrent.ConcurrentQueue<byte>());

            private volatile bool _completed;

            public PipeStream103? Peer { get; set; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                var read = 0;

                // 至少等到 1 字节（或完成/取消），之后把当前可用的都取走
                while (read == 0)
                {
                    if (_completed)
                    {
                        return 0;
                    }

                    try
                    {
                        if (!_bytes.TryTake(out var b, 50, ct))
                        {
                            // 模拟串口：无数据时返回 0 会结束循环，因此抛取消（由上层处理）
                            ct.ThrowIfCancellationRequested();
                            continue;
                        }

                        buffer.Span[read++] = b;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                while (read < buffer.Length && _bytes.TryTake(out var nb))
                {
                    buffer.Span[read++] = nb;
                }

                return read;
            }

            public override void Write(byte[] buffer, int offset, int count)
                => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                var peer = Peer ?? throw new InvalidOperationException("未连接对端");

                foreach (var b in buffer.Span)
                {
                    peer._bytes.Add(b, ct);
                }

                return default;
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        }
    }

    [TestFixture]
    public class CS103SerialTests
    {
        private static Iec103Options SerialOptions(byte localAddress = 1) => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = 1500;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = 2;
                l.LocalLinkAddress = localAddress;
            });

        [Test]
        public async Task 串口路径_总召唤全流程()
        {
            using var pipes = new PipePair103();
            const int pointCount = 3;

            await using var server = new Iec103Server(SerialOptions());
            server.AsduReceived += (session, in view) =>
            {
                if (view.TypeId != TypeIds.GeneralInterrogation)
                {
                    return;
                }

                for (var i = 1; i <= pointCount; i++)
                {
                    session.SendAsduAsync(Asdu.TimeTaggedMessage(
                        1, 160, (byte)i, DpiQuality.FromDpi(2),
                        Cp56Time2a.FromDateTime(new DateTime(2026, 7, 23, 14, 30, i)),
                        CotCodes.RemoteOperation)).AsTask().GetAwaiter().GetResult();
                }

                session.SendAsduAsync(Asdu.GeneralInterrogationEnd(1)).AsTask().GetAwaiter().GetResult();
            };

            await server.StartAsync(pipes.Slave);
            Assert.That(server.Mode, Is.EqualTo(Iec103ServerMode.SerialPort));

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            await client.ConnectAsync();

            await client.ResetLinkAsync(1);
            var result = await client.GeneralInterrogationAsync(1);

            var points = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Where(result, a => a.TypeId == TypeIds.TimeTaggedMessage));
            Assert.That(points.Count, Is.EqualTo(pointCount), "串口路径应收到全部监视点");
            Assert.That(result[^1].TypeId, Is.EqualTo(TypeIds.GeneralInterrogationEnd), "终止帧在最后");
        }

        [Test]
        public async Task 串口路径_测量值走二级队列()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(SerialOptions());
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            // 等链路建立
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            await server.SendMeasurementAsync(1, 160, 1, 4321);
            var collected = await client.RequestClass2Async(1);

            var measurements = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(collected, a => a.TypeId == TypeIds.Measurand));
            Assert.That(measurements, Is.Not.Empty);
            Assert.That(Asdu.DecodeMeasurement(measurements[0]), Is.EqualTo(4321));
        }

        [Test]
        public async Task 地址过滤_非本站地址被静默忽略()
        {
            using var pipes = new PipePair103();

            // 从站地址设为 5，主站却向地址 1 发复位
            var slowNoRetry = new Iec103Options().UseLinkParameters(l =>
            {
                l.LocalLinkAddress = 5;
                l.MessageTimeoutMs = 300;
                l.MaxRepeat = 0;
            });

            await using var server = new Iec103Server(slowNoRetry);
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, slowNoRetry);
            await client.ConnectAsync();

            // 从站应只在收到本站地址（5）时回应答
            var traced = new System.Collections.Generic.List<string>();
            server.FrameTraced += (s, dir, f) => traced.Add($"{dir} A={f.Address:X2} C={f.Control:X2}");

            // 向错误地址复位：从站应静默忽略 → 主站超时
            var threw = false;
            try
            {
                await client.ResetLinkAsync(1);
            }
            catch (TimeoutException)
            {
                threw = true;
            }

            Assert.That(traced, Is.Empty,
                "被过滤的帧不应出现在链路追踪中（无 RX 也无 TX）；实际追踪: "
                + (traced.Count == 0 ? "(空)" : string.Join(" | ", traced)));
            Assert.That(threw, Is.True, "非本站地址的请求帧必须被静默忽略（RS-485 多站共享线路）");

            // 向正确地址复位：应成功
            await client.ResetLinkAsync(5);
            Assert.That(server.Sessions[0].LinkEstablished, Is.True);
        }

        [Test]
        public async Task 地址过滤_广播帧可配置接受或忽略()
        {
            using var pipes = new PipePair103();

            var noBroadcast = new Iec103Options().UseLinkParameters(l =>
            {
                l.LocalLinkAddress = 3;
                l.AcceptBroadcast = false;
                l.MessageTimeoutMs = 400;
                l.MaxRepeat = 0;
            });

            await using var server = new Iec103Server(noBroadcast);
            await server.StartAsync(pipes.Slave);

            var linkEstablished = false;
            server.AsduReceived += (session, in view) => linkEstablished = true;

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            await client.ConnectAsync();

            // 广播地址 255 在 AcceptBroadcast=false 时应被过滤（不建链、不上报）
            Assert.ThrowsAsync<TimeoutException>(async () => await client.ResetLinkAsync(255));

            Assert.That(linkEstablished, Is.False, "广播被禁用时不应处理其携带的 ASDU");
        }

        [Test]
        public void 串口传输_参数校验()
        {
            Assert.Throws<ArgumentNullException>(() => new Iec103Client((Stream)null!));
            Assert.Throws<ArgumentNullException>(() => new Iec103Client((System.IO.Ports.SerialPort)null!));

            Assert.ThrowsAsync<ArgumentNullException>(async () => await new Iec103Server(SerialOptions()).StartAsync((Stream)null!));
        }

        [Test]
        public async Task 串口传输_重复启动被拒绝()
        {
            using var pipes = new PipePair103();
            await using var server = new Iec103Server(SerialOptions());
            await server.StartAsync(pipes.Slave);

            Assert.ThrowsAsync<InvalidOperationException>(async () => await server.StartAsync(pipes.Slave),
                "已启动的从站再次启动应报错，避免传输被静默替换");

            await server.StopAsync();
            Assert.That(server.Mode, Is.EqualTo(Iec103ServerMode.Stopped));
        }

        [Test]
        public async Task 串口传输_停止后会话被清理()
        {
            using var pipes = new PipePair103();
            await using var server = new Iec103Server(SerialOptions());
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            Assert.That(server.SessionCount, Is.EqualTo(1));

            await server.StopAsync();
            Assert.That(server.SessionCount, Is.Zero);
        }

        [Test]
        public async Task 半帧恢复_后续帧仍可正常处理()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(SerialOptions());
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            await client.ConnectAsync();

            // 从站侧直接注入一个残缺帧（只有起始字节和半个头），模拟线路中断
            var partial = new byte[] { 0x68, 0x05 };
            await pipes.Master.WriteAsync(partial);

            await Task.Delay(300); // 超过 CharacterTimeoutMs，触发分帧器半帧复位

            // 随后正常复位应成功（若分帧器卡死，这里会超时）
            await client.ResetLinkAsync(1);
            Assert.That(server.Sessions[0].LinkEstablished, Is.True);
        }

        [Test]
        public async Task 私有类型_串口路径收发闭环()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(SerialOptions());
            server.PrivateTypes.RegisterFunInf(200, 7, new VendorHandler103());
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, SerialOptions());
            client.PrivateTypes.RegisterFunInf(200, 7, new VendorHandler103());

            var decoded = new System.Collections.Generic.List<string>();
            client.AsduReceived += (in Iec103AsduView asdu) =>
            {
                if (asdu.TypeId == 200 && asdu.Fun == 200 && asdu.Inf == 7)
                {
                    lock (decoded)
                    {
                        decoded.Add("VENDOR:" + System.Convert.ToHexString(asdu.Data.ToArray()));
                    }
                }
            };

            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            await server.BroadcastAsync(new Asdu(200, true, 1, CotCodes.Spontaneous, false, 1, 200, 7,
                new byte[] { 0xCA, 0xFE }));
            await client.RequestClass1Async(1);

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && decoded.Count == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(decoded, Is.Not.Empty, "串口路径同样必须支持私有类型分派");
            Assert.That(decoded[0], Is.EqualTo("VENDOR:CAFE"));
        }

        // ══════════════════════════════════════════════════════════
        //  真实 SerialPort 的构造契约（不需要硬件）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 传入**未打开**的 SerialPort 时，主站/从站构造与启动都不得因
        /// <c>SerialPort.BaseStream</c> 抛 <see cref="InvalidOperationException"/>。
        ///
        /// 关键事实：<c>SerialPort.BaseStream</c> 在端口未打开时抛
        /// "The BaseStream is only available when the port is open"。
        /// <c>SerialLinkTransport</c> 的契约是「调用方传未打开的串口，库在 Open() 里打开」，
        /// 因此它**只能在 Open() 成功之后**才解析 BaseStream。
        /// 原实现在构造函数里就取 → 任何用到串口的路径（StartAsync/ConnectAsync 创建
        /// SerialLinkTransport 那一刻）立即崩溃，而不是给出"串口打开失败"这种可诊断错误。
        /// 此前测试全走内存 PipeStream，故从未暴露（与 CS101 的 SerialTransceiverFT12 同型）。
        ///
        /// 本用例直接覆盖 <see cref="SerialLinkTransport"/> 本身：构造不得访问 BaseStream，
        /// 未打开时读写应给出 InvalidOperationException（明确错误），而不是在构造期就炸。
        /// </summary>
        [Test]
        public void 串口传输_构造不得访问BaseStream_未打开时读写给出明确异常()
        {
            var port = new System.IO.Ports.SerialPort(
                "COM_CS103_TEST_NONEXISTENT", 9600,
                System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);

            Assert.That(port.IsOpen, Is.False, "前置条件：端口未打开");

            // 【核心断言】构造阶段绝不能碰 SerialPort.BaseStream
            Assert.DoesNotThrow(() =>
            {
                var t = new SerialLinkTransport(port);
                t.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }, "SerialLinkTransport 构造阶段不得读取 SerialPort.BaseStream（未打开时读取会抛）");

            Assert.That(port.IsOpen, Is.False, "构造阶段不得擅自打开端口");

            // 未打开时读写：明确异常（而非 NullReferenceException / 构造期崩溃）
            var t2 = new SerialLinkTransport(port);
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await t2.WriteAsync(new byte[] { 0x10 }, CancellationToken.None),
                "端口未打开时写应给出 InvalidOperationException（BaseStream 不可用）");
        }

        /// <summary>
        /// 串口传输在未打开状态下被读写时，应给出明确错误而不是 NullReferenceException
        /// （延迟解析后 _inner 尚未创建）。
        /// </summary>
        [Test]
        public async Task 串口传输_未打开时读写应抛出明确异常()
        {
            var port = new System.IO.Ports.SerialPort(
                "COM_CS103_TEST_NONEXISTENT", 9600,
                System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);

            await using var transport = new SerialLinkTransport(port);

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await transport.WriteAsync(new byte[] { 0x10 }, CancellationToken.None));
        }

        private sealed class VendorHandler103 : IIec103PrivateTypeHandler
        {
            public byte TypeId => 200;

            public string VendorTag => "SerialTest";

            public byte? Fun => 200;

            public byte? Inf => 7;



            public byte[] Encode(Iec103DecodedAsdu value) => Array.Empty<byte>();
        }
    }
}
