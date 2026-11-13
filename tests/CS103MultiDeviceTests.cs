/*
 *  CS103MultiDeviceTests.cs
 *
 *  一条通道挂多台装置（各自用链路地址/CA 区分）的回归测试。
 *
 *  现场形态：一个串口或一路 TCP 网关下接入多台保护装置，主站靠链路地址
 *  区分目标。103 非平衡链路上这是「串行轮询」而不是并发会话，但必须保证：
 *  1. 每台装置的 1/2 级上送队列互相隔离（向 A 召唤不能拿到 B 的数据）；
 *  2. FCB 按装置独立（IEC 60870-5-1 要求）；
 *  3. 装置 A 超时后的迟到应答不得被装置 B 的事务收下（跨装置串答）；
 *  4. 主站 API 被并发调用时不串答（事务锁串行化后结果各自正确）；
 *  5. 从站可配置多个本站地址，未配置的地址静默忽略。
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CS103MultiDeviceTests
    {
        private static Iec103Options MultiAddressOptions(
            byte primary,
            byte secondary,
            int timeoutMs = 1200,
            int maxRepeat = 2) => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = timeoutMs;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = maxRepeat;
                l.LocalLinkAddress = primary;
                l.AddLinkAddress(secondary);
            });

        private static Iec103Options ClientOptions(int timeoutMs, int maxRepeat) => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = timeoutMs;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = maxRepeat;
            });

        /// <summary>
        /// 主站向地址 1 / 地址 2 分别召唤 2 级数据：每台只应收到自己的测量值。
        /// </summary>
        /// <remarks>
        /// 修复前从站只有一个上送队列，地址 2 的数据会先被地址 1 的召唤取走
        /// （或反之），两台装置的数据混在同一个结果里。
        /// </remarks>
        [Test]
        public async Task 多装置共通道_从站队列按地址隔离()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, ClientOptions(1200, 2));
            await client.ConnectAsync();

            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            // 两台装置各上送一条 2 级测量值（103 中 CA == 链路地址）
            await server.SendMeasurementAsync(1, 160, 1, 1111);
            await server.SendMeasurementAsync(2, 160, 1, 2222);

            var device1 = await client.RequestClass2Async(1);
            var device2 = await client.RequestClass2Async(2);

            Assert.That(device1, Is.Not.Empty, "地址 1 应取到自己的测量值");
            Assert.That(device2, Is.Not.Empty, "地址 2 应取到自己的测量值");
            Assert.That(device1.All(a => a.Ca == 1), Is.True,
                "地址 1 的召唤结果混入了其它装置的数据：" + Describe(device1));
            Assert.That(device2.All(a => a.Ca == 2), Is.True,
                "地址 2 的召唤结果混入了其它装置的数据：" + Describe(device2));

            var values1 = device1.Where(a => a.TypeId == TypeIds.Measurand)
                .Select(Asdu.DecodeMeasurement).ToArray();
            var values2 = device2.Where(a => a.TypeId == TypeIds.Measurand)
                .Select(Asdu.DecodeMeasurement).ToArray();

            Assert.That(values1, Does.Contain((short)1111), "地址 1 应收到 1111");
            Assert.That(values1, Does.Not.Contain((short)2222), "地址 1 不得收到地址 2 的 2222");
            Assert.That(values2, Does.Contain((short)2222), "地址 2 应收到 2222");
            Assert.That(values2, Does.Not.Contain((short)1111), "地址 2 不得收到地址 1 的 1111");
        }

        /// <summary>
        /// 主站交替访问两台装置时，每台的 FCB 序列必须各自从 0 开始独立翻转。
        /// </summary>
        /// <remarks>
        /// IEC 60870-5-1：帧计数位是每个从站独立的。修复前主站只有一个 _nextFcb，
        /// 交替访问会得到 1:[0,0] / 2:[1]，严格校验 FCB 的装置会把第二次请求
        /// 当成重复帧并回陈旧应答。
        /// </remarks>
        [Test]
        public async Task 多装置共通道_FCB按装置独立()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(pipes.Slave);

            var fcbByAddress = new Dictionary<byte, List<bool>>();
            var gate = new object();
            server.FrameTraced += (session, direction, frame) =>
            {
                if (direction != "RX")
                {
                    return;
                }

                var control = LinkControl.FromByte(frame.Control);
                if (!control.IsPrimary || (PrimaryFunction)control.Function != PrimaryFunction.RequestClass2)
                {
                    return;
                }

                lock (gate)
                {
                    if (!fcbByAddress.TryGetValue(frame.Address, out var list))
                    {
                        list = new List<bool>();
                        fcbByAddress[frame.Address] = list;
                    }

                    list.Add(control.FcbOrAcd);
                }
            };

            await using var client = new Iec103Client(pipes.Master, ClientOptions(1200, 2));
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            // 两台装置的队列都为空，每次召唤只产生一个请求帧
            await client.RequestClass2Async(1);
            await client.RequestClass2Async(2);
            await client.RequestClass2Async(1);

            lock (gate)
            {
                Assert.That(fcbByAddress.ContainsKey(1), Is.True, "地址 1 应收到两次请求");
                Assert.That(fcbByAddress.ContainsKey(2), Is.True, "地址 2 应收到一次请求");

                Assert.That(fcbByAddress[1], Is.EqualTo(new[] { false, true }),
                    "地址 1 的 FCB 应独立按 0→1 翻转；实际 " + string.Join(",", fcbByAddress[1].Select(b => b ? 1 : 0)));
                Assert.That(fcbByAddress[2], Is.EqualTo(new[] { false }),
                    "地址 2 的首次请求必须用 FCB=0（不能继承地址 1 的状态）；实际 "
                    + string.Join(",", fcbByAddress[2].Select(b => b ? 1 : 0)));
            }
        }

        /// <summary>
        /// 装置 1 超时后的迟到应答，不能被装置 2 的事务当成本轮答案。
        /// </summary>
        /// <remarks>
        /// 这是多装置共通道最危险的缺陷：主站等 A 超时、切到 B，A 的应答此刻才到，
        /// 若事务不校验地址就会被 B 收下，表现为「B 的召唤结果里混入 A 的数据」。
        /// </remarks>
        [Test]
        public async Task 多装置共通道_装置1的迟到应答不得污染装置2的事务()
        {
            using var pipes = new PipePair103();
            using var slave = new ManualSlave(pipes.Slave);

            // 超时要短：让装置 1「无应答」尽快超时，把迟到帧窗口压缩到测试可控范围
            await using var client = new Iec103Client(pipes.Master, ClientOptions(200, 0));
            await client.ConnectAsync();

            // 1) 向地址 1 请求：手工从站收到但不回，主站超时
            var device1Request = client.RequestClass2Async(1);
            var frame1 = await slave.ReadFrameAsync();
            Assert.That(frame1.Address, Is.EqualTo(1), "前置条件：主站先向地址 1 发请求");
            Assert.ThrowsAsync<TimeoutException>(async () => await device1Request);


            // 2) 向地址 2 请求
            var device2Request = client.RequestClass2Async(2);
            var frame2 = await slave.ReadFrameAsync();
            Assert.That(frame2.Address, Is.EqualTo(2), "前置条件：主站已切到地址 2");

            // 3) 先补发地址 1 的迟到数据帧（模拟 A 的慢应答此刻才到线）
            await slave.WriteAsync(RespondData(address: 1, Asdu.Measurement(1, 160, 1, 9999).Encode()));

            // 4) 再回地址 2 的正确数据
            await slave.WriteAsync(RespondData(address: 2, Asdu.Measurement(2, 160, 1, 2222).Encode()));

            // 5) 主站收到数据后会继续召唤，手工从站回「无数据」结束本轮
            var frame3 = await slave.ReadFrameAsync();
            Assert.That(frame3.Address, Is.EqualTo(2));
            await slave.WriteAsync(NoData(address: 2));

            var result = await device2Request;

            Assert.That(result.All(a => a.Ca == 2), Is.True,
                "地址 1 的迟到帧串进了地址 2 的事务：" + Describe(result));
            Assert.That(result.Select(Asdu.DecodeMeasurement), Does.Contain((short)2222),
                "地址 2 必须拿到自己的数据");
            Assert.That(result.Select(Asdu.DecodeMeasurement), Does.Not.Contain((short)9999),
                "地址 1 的 9999 不得出现在地址 2 的结果里");
        }

        /// <summary>
        /// 主站 API 被并发调用（Task.WhenAll）时必须串行化且结果各自正确。
        /// </summary>
        /// <remarks>
        /// 103 非平衡链路上本来就不能真并发；这里验证并发调用不会串答、不会抛异常，
        /// 而是排队执行并各取各的数据。
        /// </remarks>
        [Test]
        public async Task 多装置共通道_并发调用串行化且结果各自正确()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, ClientOptions(1500, 2));
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            await server.SendMeasurementAsync(1, 160, 1, 1111);
            await server.SendMeasurementAsync(2, 160, 1, 2222);

            var task1 = client.RequestClass2Async(1);
            var task2 = client.RequestClass2Async(2);
            var results = await Task.WhenAll(task1, task2);

            Assert.That(results[0].All(a => a.Ca == 1), Is.True,
                "并发调用后地址 1 的结果被污染：" + Describe(results[0]));
            Assert.That(results[0].Select(Asdu.DecodeMeasurement), Does.Contain((short)1111));

            Assert.That(results[1].All(a => a.Ca == 2), Is.True,
                "并发调用后地址 2 的结果被污染：" + Describe(results[1]));
            Assert.That(results[1].Select(Asdu.DecodeMeasurement), Does.Contain((short)2222));
        }

        /// <summary>
        /// 从站配置多个本站地址：配置过的都应答，未配置的静默忽略。
        /// </summary>
        [Test]
        public async Task 多地址从站_只应答已配置地址()
        {
            using var pipes = new PipePair103();

            var options = MultiAddressOptions(1, 2, timeoutMs: 120, maxRepeat: 0);
            await using var server = new Iec103Server(options);
            await server.StartAsync(pipes.Slave);

            var tracedAddresses = new ConcurrentBag<byte>();
            server.FrameTraced += (session, direction, frame) =>
            {
                if (direction == "RX")
                {
                    tracedAddresses.Add(frame.Address);
                }
            };

            await using var client = new Iec103Client(pipes.Master, ClientOptions(120, 0));
            await client.ConnectAsync();

            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            Assert.ThrowsAsync<TimeoutException>(() => client.ResetLinkAsync(3));

            Assert.That(tracedAddresses, Does.Not.Contain((byte)3),
                "未配置的地址 3 必须被静默忽略（不追踪、不应答）");
            Assert.That(server.Sessions[0].LinkEstablished, Is.True);
        }

        /// <summary>
        /// 链路地址集合的默认值、覆盖语义与非法地址校验。
        /// </summary>
        [Test]
        public void 链路地址集合_默认值与配置语义()
        {
            var parameters = new Iec103LinkParameters();
            Assert.That(parameters.LinkAddresses, Is.EquivalentTo(new byte[] { 1 }),
                "默认只含 LocalLinkAddress=1");

            parameters.LocalLinkAddress = 7;
            Assert.That(parameters.LinkAddresses, Is.EquivalentTo(new byte[] { 7 }),
                "设置 LocalLinkAddress 应重置集合为单地址");

            parameters.AddLinkAddress(8);
            Assert.That(parameters.LinkAddresses, Is.EquivalentTo(new byte[] { 7, 8 }));

            parameters.LocalLinkAddress = 9;
            Assert.That(parameters.LinkAddresses, Is.EquivalentTo(new byte[] { 9 }),
                "再次设置 LocalLinkAddress 应丢弃之前 AddLinkAddress 的地址");

            Assert.Throws<ArgumentOutOfRangeException>(() => parameters.AddLinkAddress(0),
                "0 不是合法链路地址");
            Assert.Throws<ArgumentOutOfRangeException>(() => parameters.AddLinkAddress(255),
                "255 是广播地址，不能作为本站地址");
        }

        /// <summary>
        /// 组件构造期的配置快照必须保留全部地址（<see cref="Iec103LinkParameters.Clone"/> 的陷阱）。
        /// </summary>
        /// <remarks>
        /// <see cref="Iec103LinkParameters.LocalLinkAddress"/> 的 setter 会把集合重置为单地址，
        /// 若 Clone 逐字段走 setter，AddLinkAddress 添加的地址会静默丢失。
        /// </remarks>
        [Test]
        public void Clone_保留全部链路地址()
        {
            var options = new Iec103Options().UseLinkParameters(l =>
            {
                l.LocalLinkAddress = 3;
                l.AddLinkAddress(4);
                l.AddLinkAddress(5);
            });

            var clone = options.Clone();

            Assert.That(clone.Link.LinkAddresses, Is.EquivalentTo(new byte[] { 3, 4, 5 }),
                "Clone 后必须保留全部地址");
            Assert.That(clone.Link.LocalLinkAddress, Is.EqualTo((byte)3));
        }

        /// <summary>
        /// 上送队列上界按地址独立计算：一台装置积压不影响另一台。
        /// </summary>
        [Test]
        public async Task 队列上界_按地址独立裁剪()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(pipes.Slave);

            var core = ((Iec103StreamSession)server.Sessions[0]).Core;
            core.MaxQueueLength = 2;

            // 地址 1 入 3 条（应丢 1 条），地址 2 入 2 条（不丢）
            for (var i = 0; i < 3; i++)
            {
                core.EnqueueRaw(1, Asdu.Measurement(1, 160, (byte)(i + 1), (short)i).Encode(), isClass1: false);
            }

            for (var i = 0; i < 2; i++)
            {
                core.EnqueueRaw(2, Asdu.Measurement(2, 160, (byte)(i + 1), (short)(100 + i)).Encode(), isClass1: false);
            }

            Assert.That(core.DroppedAsduCount, Is.EqualTo(1),
                "只有地址 1 的第 3 条应被丢弃；若共享上界会丢 3 条");

            await using var client = new Iec103Client(pipes.Master, ClientOptions(1200, 2));
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            var device2 = await client.RequestClass2Async(2);
            Assert.That(device2.Count(a => a.TypeId == TypeIds.Measurand), Is.EqualTo(2),
                "地址 2 的两条都必须还在（不受地址 1 裁剪影响）");
        }

        /// <summary>
        /// 多装置交替轮询 50 轮：结果不串答、FCB 每台独立交替、无累积错误。
        /// </summary>
        [Test]
        public async Task 多装置共通道_交替轮询50轮稳定()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(pipes.Slave);

            var fcbSequences = new Dictionary<byte, List<bool>> { [1] = new(), [2] = new() };
            var gate = new object();
            server.FrameTraced += (session, direction, frame) =>
            {
                if (direction != "RX")
                {
                    return;
                }

                var control = LinkControl.FromByte(frame.Control);
                if (control.IsPrimary && (PrimaryFunction)control.Function == PrimaryFunction.RequestClass2)
                {
                    lock (gate)
                    {
                        if (fcbSequences.TryGetValue(frame.Address, out var list))
                        {
                            list.Add(control.FcbOrAcd);
                        }
                    }
                }
            };

            await using var client = new Iec103Client(pipes.Master, ClientOptions(1200, 2));
            await client.ConnectAsync();
            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            const int rounds = 50;
            for (var round = 0; round < rounds; round++)
            {
                await server.SendMeasurementAsync(1, 160, 1, (short)round);
                await server.SendMeasurementAsync(2, 160, 1, (short)(1000 + round));

                var device1 = await client.RequestClass2Async(1);
                var device2 = await client.RequestClass2Async(2);

                Assert.That(device1.All(a => a.Ca == 1), Is.True,
                    $"第 {round} 轮地址 1 结果被污染：" + Describe(device1));
                Assert.That(device2.All(a => a.Ca == 2), Is.True,
                    $"第 {round} 轮地址 2 结果被污染：" + Describe(device2));

                Assert.That(device1.Select(Asdu.DecodeMeasurement), Does.Contain((short)round),
                    $"第 {round} 轮地址 1 应收到 {round}");
                Assert.That(device2.Select(Asdu.DecodeMeasurement), Does.Contain((short)(1000 + round)),
                    $"第 {round} 轮地址 2 应收到 {1000 + round}");
            }

            lock (gate)
            {
                AssertFcbAlternates(fcbSequences[1], address: 1);
                AssertFcbAlternates(fcbSequences[2], address: 2);
            }
        }

        /// <summary>FCB 序列必须是 0,1,0,1…（每轮每台都独立翻转）。</summary>
        private static void AssertFcbAlternates(List<bool> sequence, byte address)
        {
            Assert.That(sequence.Count, Is.GreaterThan(0), $"地址 {address} 未捕获到请求帧");

            for (var i = 0; i < sequence.Count; i++)
            {
                var expected = i % 2 == 0 ? false : true;
                Assert.That(sequence[i], Is.EqualTo(expected),
                    $"地址 {address} 第 {i + 1} 帧 FCB 应为 {(expected ? 1 : 0)}；"
                    + "实际序列 " + string.Join(",", sequence.Select(b => b ? 1 : 0)));
            }
        }

        /// <summary>
        /// 真实 TCP 通道上的多装置隔离（网关后挂多台装置的典型现场形态）。
        /// </summary>
        /// <remarks>
        /// TCP 模式下从站不做链路地址过滤（一条连接对应一个主站），
        /// 但「按地址取队列」与「按地址维护 FCB」必须同样成立。
        /// </remarks>
        [Test]
        public async Task 多装置共通道_TCP上同样按地址隔离()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(MultiAddressOptions(1, 2));
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, ClientOptions(1500, 2));
            await client.ConnectAsync();

            await client.ResetLinkAsync(1);
            await client.ResetLinkAsync(2);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(1), "前置条件：一个 TCP 连接一个会话");

            await server.SendMeasurementAsync(1, 160, 1, 1111);
            await server.SendMeasurementAsync(2, 160, 1, 2222);

            var device1 = await client.RequestClass2Async(1);
            var device2 = await client.RequestClass2Async(2);

            Assert.That(device1.All(a => a.Ca == 1), Is.True,
                "TCP 通道上地址 1 的结果被污染：" + Describe(device1));
            Assert.That(device2.All(a => a.Ca == 2), Is.True,
                "TCP 通道上地址 2 的结果被污染：" + Describe(device2));
            Assert.That(device1.Select(Asdu.DecodeMeasurement), Does.Contain((short)1111));
            Assert.That(device2.Select(Asdu.DecodeMeasurement), Does.Contain((short)2222));
            Assert.That(device1.Select(Asdu.DecodeMeasurement), Does.Not.Contain((short)2222));
            Assert.That(device2.Select(Asdu.DecodeMeasurement), Does.Not.Contain((short)1111));
        }

        // ── 手工从站辅助 ────────────────────────────────────────────

        /// <summary>分配一个空闲端口（避免与本机其它测试冲突）。</summary>
        private static int AllocatePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static Ft12Frame RespondData(byte address, byte[] asdu) => new(
            Ft12FrameFormat.VariableLength,
            LinkControl.Secondary(SecondaryFunction.RespondData).ToByte(),
            address,
            asdu);

        private static Ft12Frame NoData(byte address) => new(
            Ft12FrameFormat.FixedLength,
            LinkControl.Secondary(SecondaryFunction.RespondNoData).ToByte(),
            address,
            Ft12Frame.EmptyAsdu);

        private static string Describe(IEnumerable<Asdu> asdus)
            => string.Join(" | ", asdus.Select(a => $"TI={a.TypeId} CA={a.Ca} FUN={a.Fun} INF={a.Inf}"));

        /// <summary>
        /// 手工从站：逐帧读主站请求、按测试编排写应答（用于制造迟到帧）。
        /// </summary>
        private sealed class ManualSlave : IDisposable
        {
            private readonly Stream _stream;
            private readonly Ft12FrameParser _parser = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly Queue<Ft12Frame> _pending = new();

            public ManualSlave(Stream stream) => _stream = stream;

            public async Task<Ft12Frame> ReadFrameAsync()
            {
                if (_pending.Count > 0)
                {
                    return _pending.Dequeue();
                }

                var buffer = new byte[512];
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    var read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("主站侧已关闭");
                    }

                    foreach (var frame in _parser.Feed(buffer.AsSpan(0, read)))
                    {
                        _pending.Enqueue(frame);
                    }

                    if (_pending.Count > 0)
                    {
                        return _pending.Dequeue();
                    }
                }

                throw new TimeoutException("手工从站等待主站帧超时");
            }

            public Task WriteAsync(Ft12Frame frame)
                => _stream.WriteAsync(frame.ToBytes().AsMemory(0, frame.GetEncodedLength()), _cts.Token)
                    .AsTask();

            public void Dispose()
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}
