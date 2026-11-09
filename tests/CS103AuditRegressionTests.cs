/*
 *  CS103AuditRegressionTests.cs
 *
 *  全功能审查回归测试：针对审查发现并修复的缺陷逐一加护栏，
 *  防止「测试全绿但线上会出错」的情况再次出现。
 *
 *  覆盖：
 *  1. ASDU 30 编码长度（8+2N，不多发 1 字节）
 *  2. GeneralCommand 保留 QU 限定词（bit6-7）
 *  3. 慢速分片长帧不被误判半帧（TryResetStale 按字符间隔计时）
 *  4. 通用分类多元素 GID / 符号扩展 / 无符号定点 / 显式小端
 *  5. GB18030 非 BMP 字符截断
 *  6. 解码长度守卫（短数据不抛 IndexOutOfRange）
 *  7. 连续两次总召唤（FCB 主从同步）
 *  8. 订阅者回调抛异常不杀死接收循环
 *  9. CLASS1/CLASS2 队列分派
 * 10. 广播地址默认配置不回应答
 * 11. SendPrivateAsync 按 TI 注册可解析
 * 12. VSQ count 越界 / GenericRead 条目超限
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CS103AuditRegressionTests
    {
        private static int AllocatePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static Iec103Options FastOptions() => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = 1200;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = 1;
            });

        // ═══════════════════════════════════════════════════════════
        //  协议核
        // ═══════════════════════════════════════════════════════════

        [Test]
        public void ASDU30_编码长度恰为8加2N()
        {
            short[] samples = { 100, -200, 300, -400, 500 };
            var asdu = DisturbanceAsdu.ChannelData(1, TooCodes.RequestChannel, 7, 1, samples, 0, samples.Length);

            // 头部 8 字节 + NDV×2；曾误写为 9+2N 导致线路帧尾部多一个 0x00
            Assert.That(asdu.Data.Length, Is.EqualTo(8 + samples.Length * 2),
                "ASDU 30 数据区必须恰为 8+2N，多发 1 字节会被装置判为坏帧");
            Assert.That(asdu.Data[^1], Is.EqualTo((byte)((samples[^1] >> 8) & 0xFF)),
                "最后一个字节应是最后一个采样值的高字节，而不是多余的 0x00");

            var segment = DisturbanceAsdu.DecodeChannelData(asdu);
            Assert.That(segment.Values, Is.EqualTo(samples));
        }

        [Test]
        public void GeneralCommand_保留QU限定词()
        {
            // 0x82 = QU=2(bit7) + DCT=2(合)。原实现 dco & 0x03 会把 QU 抹掉。
            var asdu = Asdu.GeneralCommand(1, 160, 1, dco: 0x82, rii: 7);
            var (dct, qu, rii) = Asdu.DecodeGeneralCommand(asdu);

            Assert.That(dct, Is.EqualTo(2), "合");
            Assert.That(qu, Is.EqualTo(2), "QU 必须原样保留");
            Assert.That(rii, Is.EqualTo(7));
            Assert.That(asdu.Data[0], Is.EqualTo(0x82));
        }

        [Test]
        public void 慢速分片长帧_不应被误判半帧()
        {
            // 构造一个 L=253 的最大可变帧，分 10 批喂入，每批之间只推进虚拟时间。
            // TryResetStale 现在按「距最后一个字节的间隔」计时，只要每批内持续
            // 收到字节，整帧耗时远超 CharacterTimeoutMs 也不该被丢弃。
            var asdu = new byte[253];
            for (var i = 0; i < asdu.Length; i++)
            {
                asdu[i] = (byte)(i & 0xFF);
            }

            var frame = Ft12Frame.EncodeVariable(0x53, 1, asdu);
            var parser = new Ft12FrameParser();
            var rejected = 0;
            parser.FrameRejected += (_, _) => rejected++;
            var frames = new List<Ft12Frame>();

            const int batchSize = 27;
            for (var offset = 0; offset < frame.Length; offset += batchSize)
            {
                var take = Math.Min(batchSize, frame.Length - offset);
                frames.AddRange(parser.Feed(frame.AsSpan(offset, take)));

                // 每批之后都问一次是否超时（模拟读循环）；只要还有字节在到达，
                // 状态机就不该认为「半个帧卡死了」。此前时间戳只在帧开始时刷新，
                // 这里会在第 2 批就把整帧 Reject 掉。
                Assert.That(parser.TryResetStale(1), Is.False,
                    $"偏移 {offset} 处不应判定半帧超时（仍在持续收字节）");
            }

            Assert.That(rejected, Is.Zero, "合法长帧不得被丢弃");
            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].Asdu, Is.EqualTo(asdu));
        }

        [Test]
        public void 半帧真超时_仍应复位()
        {
            var parser = new Ft12FrameParser();
            parser.Feed(new byte[] { 0x68, 0x05 });
            Assert.That(parser.InFrame, Is.True);

            // 没有后续字节到达时，超时必须复位。用 100ms 保证跨过阈值。
            System.Threading.Thread.Sleep(120);
            Assert.That(parser.TryResetStale(50), Is.True);
            Assert.That(parser.InFrame, Is.False);
        }

        [Test]
        public void 通用分类_多元素GID全部解码()
        {
            var set = GenericDataSet.Range(new Gin(1, 1), 0.5f, 100.25f, 0.1f);

            Assert.That(set.Gdd.Number, Is.EqualTo(3));
            var values = set.DecodeValues();
            Assert.That(values.Count, Is.EqualTo(3), "min/max/step 三个元素都要取回");
            Assert.That((float)values[0], Is.EqualTo(0.5f).Within(1e-6));
            Assert.That((float)values[1], Is.EqualTo(100.25f).Within(1e-6));
            Assert.That((float)values[2], Is.EqualTo(0.1f).Within(1e-6));

            // 兼容的单值入口仍返回首个元素
            Assert.That((float)set.DecodeValue(), Is.EqualTo(0.5f).Within(1e-6));
        }

        [Test]
        public void 通用分类_有符号多元素符号扩展按单元素宽度()
        {
            // Gdd(SignedInt, 1, 2) + Gid=[0xFF, 0x01]：元素1=-1，元素2=+1。
            // 原实现用整个 GID 最后一字节判符号、按总长算 bits，会解出 511。
            var set = new GenericDataSet(
                new Gin(2, 1), KodCodes.ActualValue,
                new Gdd(GddTypes.SignedInt, 1, 2, false),
                new byte[] { 0xFF, 0x01 });

            var values = set.DecodeValues();
            Assert.That(values.Count, Is.EqualTo(2));
            Assert.That((long)values[0], Is.EqualTo(-1));
            Assert.That((long)values[1], Is.EqualTo(1));
        }

        [Test]
        public void 通用分类_无符号定点按全宽度读取()
        {
            // 0x0100 LE = 256 -> 1.0。原实现只读 Gid[0]=0x00，得到 0.0。
            var set = new GenericDataSet(
                new Gin(2, 2), KodCodes.ActualValue,
                new Gdd(GddTypes.UnsignedFixed, 2, 1, false),
                new byte[] { 0x00, 0x01 });

            Assert.That((float)set.DecodeValue(), Is.EqualTo(1.0f).Within(1e-6));
        }

        [Test]
        public void 通用分类_GB18030非BMP字符截断不产生孤立代理()
        {
            // U+20000 是 4 字节 GB18030 字符（UTF-16 代理对）。用 400 个强制触发截断。
            var scalar = char.ConvertFromUtf32(0x20000);
            var text = string.Concat(Enumerable.Repeat(scalar, 400));
            var set = GenericDataSet.Ascii(new Gin(1, 1), KodCodes.Description, text);

            var decoded = GenericDataSet.DecodeGb18030(set.Gid);
            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded.Any(c => c == '\uFFFD' || c == '?'), Is.False,
                "非 BMP 字符不应被拆成孤代理产生替换符");
            Assert.That(set.Gdd.DataWidth, Is.EqualTo(set.Gid.Length));
        }

        [Test]
        public void 解码方法_短数据抛FormatException而非越界()
        {
            var empty = new Asdu(TypeIds.Measurand, true, 1, CotCodes.Cyclic, false, 1, 160, 1, Array.Empty<byte>());

            Assert.Throws<FormatException>(() => Asdu.DecodeMeasurement(empty));
            Assert.Throws<FormatException>(() => Asdu.DecodeGeneralCommand(empty));
            Assert.Throws<FormatException>(() => Asdu.DecodeProtectionEvent(empty));
            Assert.Throws<FormatException>(() => Gin.Decode(ReadOnlySpan<byte>.Empty));
            Assert.Throws<FormatException>(() => Gdd.Decode(ReadOnlySpan<byte>.Empty));
        }

        [Test]
        public void TimeTaggedMessage_extraInfo不足4字节不抛异常()
        {
            var time = new Cp56Time2a(0, 0, 0, 1, 1, 26);
            Assert.DoesNotThrow(() =>
                Asdu.TimeTaggedMessage(1, 160, 1, DpiQuality.FromDpi(2), time, CotCodes.Spontaneous, new byte[] { 0xAA }));
        }

        [Test]
        public void VSQ数目越界应抛异常()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Asdu.VsQBits(true, 200));
            Assert.Throws<ArgumentOutOfRangeException>(() => Asdu.VsQBits(false, -1));
        }

        [Test]
        public void GenericRead条目超过255应抛异常()
        {
            var entries = new (Gin, byte)[256];
            Assert.Throws<ArgumentOutOfRangeException>(() => Asdu.GenericRead(1, 1, entries));
        }

        // ═══════════════════════════════════════════════════════════
        //  运行时 / 端到端
        // ═══════════════════════════════════════════════════════════

        [Test]
        public async Task 连续两次总召唤_结果一致()
        {
            var port = AllocatePort();
            const int pointCount = 3;

            await using var server = new Iec103Server(FastOptions());
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
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            var first = await client.GeneralInterrogationAsync(1);
            var second = await client.GeneralInterrogationAsync(1);

            // 旧实现第二次会因主从 FCB 失步被判为重复帧，返回 0 个真实点 + 陈旧终止帧
            Assert.That(first.Count(a => a.TypeId == TypeIds.TimeTaggedMessage), Is.EqualTo(pointCount));
            Assert.That(second.Count(a => a.TypeId == TypeIds.TimeTaggedMessage), Is.EqualTo(pointCount),
                "连续两次总召唤必须都拿到完整数据（FCB 主从同步）");
            Assert.That(second[^1].TypeId, Is.EqualTo(TypeIds.GeneralInterrogationEnd));
        }

        [Test]
        public async Task 订阅者回调抛异常_链路不中断()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());

            var throwCount = 0;
            client.AsduReceived += (in Iec103AsduView view) =>
            {
                throwCount++;
                throw new InvalidOperationException("用户回调故意抛异常");
            };

            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            // 等会话建链后推一条数据
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            await server.BroadcastAsync(Asdu.Measurement(1, 160, 1, 1234));

            // 关键：即使回调抛异常，客户端仍应能完成一次正常召唤
            Assert.DoesNotThrowAsync(async () => await client.RequestClass2Async(1),
                "回调异常必须被隔离，接收循环不能退出");

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && throwCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(client.IsConnected, Is.True, "IsConnected 不能仍为 true 而链路已死");
            Assert.That(throwCount, Is.GreaterThan(0), "回调确实被调用过");
        }

        [Test]
        public async Task CLASS1与CLASS2队列严格分离()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            // 同时放一条 1 级事件和一条 2 级测量值
            await server.SendProtectionEventAsync(1, 160, 1, DpiQuality.FromDpi(2),
                100, 7, new DateTime(2026, 7, 23, 14, 30, 0));
            await server.SendMeasurementAsync(1, 160, 2, 5678);

            var class2 = await client.RequestClass2Async(1);

            // 旧实现 CLASS2 会先取到 CLASS1 的事件 ASDU
            Assert.That(class2.Any(a => a.TypeId == TypeIds.TimeTaggedMessagePrivate), Is.False,
                "2 级召唤不得返回 1 级事件数据");
            Assert.That(class2.Any(a => a.TypeId == TypeIds.Measurand), Is.True,
                "2 级召唤应返回测量值");
        }

        [Test]
        public async Task 默认配置下广播帧不回应答()
        {
            // RS-485 上广播请求若每站都回必然总线冲突。
            // 用内存管道测单站：默认 AcceptBroadcast=true 时也不该回 ACK。
            using var pipes = new PipePair103();
            var options = new Iec103Options().UseLinkParameters(l =>
            {
                l.LocalLinkAddress = 1;
                l.AcceptBroadcast = true; // 默认
                l.MessageTimeoutMs = 300;
                l.MaxRepeat = 0;
            });

            await using var server = new Iec103Server(options);
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, options);
            await client.ConnectAsync();

            Assert.ThrowsAsync<TimeoutException>(async () => await client.ResetLinkAsync(255),
                "广播 RESET CU 不应得到应答（避免 RS-485 多站抢答）");
        }

        [Test]
        public void SendPrivateAsync_按TI注册可解析()
        {
            // 用构造后不连线的客户端验证解析分支（失败会抛 InvalidOperationException，
            // 成功则因未连接抛 InvalidOperationException「链路未连接」——区分消息即可）
            var client = new Iec103Client("127.0.0.1", 2404, FastOptions());
            client.PrivateTypes.RegisterType(new ByTypeHandler());

            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.SendPrivateAsync(1, 200, 7, typeId: 200, value: new Iec103ClockSyncMessage(default)));

            Assert.That(ex!.Message, Does.Contain("链路未连接"),
                "按 TI 注册的处理器必须能解析到，不应报『未登记』");
        }

        private sealed class ByTypeHandler : IIec103PrivateTypeHandler
        {
            public byte TypeId => 200;
            public string VendorTag => "AuditTest";
            public byte? Fun => null;
            public byte? Inf => null;
            public byte[] Encode(Iec103DecodedAsdu value) => BitConverter.GetBytes(value.Header.TypeId);
        }

        /// <summary>内存双工流（与 CS103SerialTests 同款，供本文件独立使用）。</summary>
        private sealed class PipePair103 : IDisposable
        {
            private readonly PipeStream103 _a = new();
            private readonly PipeStream103 _b = new();

            public PipePair103()
            {
                _a.Peer = _b;
                _b.Peer = _a;
            }

            public Stream Master => _a;
            public Stream Slave => _b;

            public void Dispose()
            {
                _a.Dispose();
                _b.Dispose();
            }

            internal sealed class PipeStream103 : Stream
            {
                private readonly System.Collections.Concurrent.BlockingCollection<byte> _bytes =
                    new(new System.Collections.Concurrent.ConcurrentQueue<byte>());

                public PipeStream103? Peer { get; set; }

                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override void Flush() { }
                public override Task FlushAsync(System.Threading.CancellationToken ct) => Task.CompletedTask;

                public override int Read(byte[] buffer, int offset, int count)
                    => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

                public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken ct = default)
                {
                    int read = 0;
                    while (read == 0)
                    {
                        if (!_bytes.TryTake(out var b, 50, ct))
                        {
                            ct.ThrowIfCancellationRequested();
                            continue;
                        }

                        buffer.Span[read++] = b;
                    }

                    while (read < buffer.Length && _bytes.TryTake(out var nb))
                    {
                        buffer.Span[read++] = nb;
                    }

                    return read;
                }

                public override void Write(byte[] buffer, int offset, int count)
                    => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

                public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken ct = default)
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
    }
}
