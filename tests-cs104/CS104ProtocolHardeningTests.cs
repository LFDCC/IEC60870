/*
 *  CS104ProtocolHardeningTests.cs
 *
 *  CS104 协议健壮性缺陷的回归测试（只针对以下四条）：
 *
 *  1. CheckSequenceNumbers=false 时 k 窗口永不释放：
 *     发送路径必须旁路 k 流控（不占槽位、不登记窗口），否则发满 K 帧后
 *     AcquireSendSlotAsync 会永久挂起（无超时、无异常、无日志）。
 *
 *  2. STOPDT / 未 STARTDT 之后仍派发 I 帧：
 *     - 未激活时 SendAsduAsync 必须抛 InvalidOperationException；
 *     - 未激活时收到的 I 帧必须按协议丢弃（不派发 AsduReceived），
 *       但仍推进 N(R) 并按 w/T2 发送 S 确认，避免对端重传风暴。
 *
 *  3. RawFrameReceived 订阅者异常未隔离：
 *     抓包订阅者抛异常不得跳过 ProcessApdu/PumpAsync，客户端收发必须仍然正常。
 *
 *  4. APCIParameters 无校验：
 *     K/W 1..32767、T0/T1/T2/T3 > 0 必须在 setter 拒绝；
 *     T0<T1<T3（以及显式配置的 W<K）在 ApduConnection 构造时校验。
 */

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS104ProtocolHardeningTests
    {
        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        /// <summary>记录所有已发送 APDU 的桩 sink（不阻塞，立即完成）。</summary>
        private sealed class RecordingSink : IIec104ApduSink
        {
            public ConcurrentQueue<byte[]> Frames { get; } = new ConcurrentQueue<byte[]>();

            public bool IsConnected => true;

            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            {
                Frames.Enqueue(apdu.ToArray());
                return default;
            }
        }

        /// <summary>立即完成发送的桩 sink。</summary>
        private sealed class ImmediateSink : IIec104ApduSink
        {
            public bool IsConnected => true;

            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => default;
        }

        private static async Task SendDummyAsync(ApduConnection conn, CancellationToken ct = default)
        {
            var al = Al();
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
            await conn.SendAsduAsync(asdu, ct).ConfigureAwait(false);
        }

        private static async Task SendManyAsync(ApduConnection conn, int count, CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                await SendDummyAsync(conn, ct).ConfigureAwait(false);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  缺陷 1：CheckSequenceNumbers=false 时 k 窗口必须旁路
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// K=2 且关闭序列号校验时连续发送 10 个 ASDU：必须全部成功且不阻塞。
        /// 修复前窗口只增不减，第 3 个发送就会永久挂在 k 槽位等待上。
        /// </summary>
        [Test]
        public async Task 关闭序列号校验_K2连续发10帧_必须全部成功且不阻塞()
        {
            var sink = new RecordingSink();
            var apci = new APCIParameters { K = 2, W = 1 };
            using var conn = new ApduConnection(apci, Al(), sink);

            conn.CheckSequenceNumbers = false;
            conn.OnUFrame(ApduCodec.StartDtCon); // 激活（否则 SendAsduAsync 会拒绝）

            // WaitAsync(5s) 保护：修复前此处会因 k 槽位永久等待而超时。
            await SendManyAsync(conn, 10).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(sink.Frames.Count, Is.EqualTo(10), "10 个 I 帧必须全部发送成功");
            Assert.That(conn.IsActive, Is.True, "旁路 k 流控不应影响连接激活状态");
        }

        /// <summary>
        /// 旁路 k 流控不能破坏接收侧 S 确认：即使不校验序列号，
        /// 收到 I 帧仍必须按 w 阈值回 S 确认（N(R) 由接收计数推进）。
        /// </summary>
        [Test]
        public async Task 关闭序列号校验_收到I帧仍须按W回S确认()
        {
            var sink = new RecordingSink();
            using var conn = new ApduConnection(new APCIParameters { W = 1 }, Al(), sink);

            conn.CheckSequenceNumbers = false;

            // 未激活时也按协议维护 N(R)：N(S)=0 被接收，计数 +1
            Assert.That(conn.OnIFrame(0, 0, ReadOnlySpan<byte>.Empty), Is.True,
                "关闭序列号校验时 I 帧仍应被接收");

            await conn.PumpAsync(CancellationToken.None);

            Assert.That(sink.Frames.Count, Is.EqualTo(1), "w=1 时收到 1 个未确认 I 帧必须立即回 S 确认");
            Assert.That(sink.Frames.TryPeek(out var frame), Is.True);
            Assert.That(frame.Length, Is.EqualTo(ApduCodec.ApciLength), "S 帧为固定 6 字节");
            Assert.That(frame[2] & 0x03, Is.EqualTo(0x01), "控制域低 2 位必须为 01（S 格式）");
        }

        // ══════════════════════════════════════════════════════════
        //  缺陷 2：STOPDT / 未 STARTDT 后发送必须被拒绝，I 帧必须丢弃
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 未STARTDT发送_必须抛InvalidOperationException()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await SendDummyAsync(conn).ConfigureAwait(false));

            Assert.That(ex, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("STARTDT_CON"), "异常信息应可诊断");
        }

        [Test]
        public async Task STARTDT后STOPDT再发送_必须抛InvalidOperationException()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());

            conn.OnUFrame(ApduCodec.StartDtCon);
            await SendDummyAsync(conn).ConfigureAwait(false); // 激活期间可发送

            conn.OnUFrame(ApduCodec.StopDtCon);
            Assert.That(conn.IsActive, Is.False, "STOPDT_CON 后必须处于非激活状态");

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await SendDummyAsync(conn).ConfigureAwait(false));
        }

        /// <summary>
        /// 未激活时收到的 I 帧必须丢弃（不派发 AsduReceived），但 N(S)/N(R) 必须继续维护，
        /// 且 w 阈值到达时仍要发 S 确认。
        /// </summary>
        [Test]
        public async Task 未激活时收到I帧_必须丢弃但仍维护NR并回S确认()
        {
            var sink = new RecordingSink();
            using var conn = new ApduConnection(new APCIParameters { W = 1 }, Al(), sink);

            var received = 0;
            conn.AsduReceived += (in AsduView _) => Interlocked.Increment(ref received);

            // 有效长度的 ASDU 头部（AsduView 只要求覆盖头部）
            var asdu = new byte[7];

            Assert.That(conn.IsActive, Is.False);
            Assert.That(conn.OnIFrame(0, 0, asdu), Is.True,
                "未激活期间 I 帧应被接收（用于维护 N(R)），而不是当作协议错误");
            Assert.That(Volatile.Read(ref received), Is.EqualTo(0),
                "未激活期间绝不能把 I 帧派发给业务（停机脏数据）");

            await conn.PumpAsync(CancellationToken.None);
            Assert.That(sink.Frames.Count, Is.EqualTo(1), "必须回 S 确认，避免对端重传风暴");

            // 重新激活后，后续 I 帧必须正常派发
            conn.OnUFrame(ApduCodec.StartDtCon);
            Assert.That(conn.OnIFrame(1, 0, asdu), Is.True);
            Assert.That(Volatile.Read(ref received), Is.EqualTo(1), "激活后 I 帧必须派发");
        }

        // ══════════════════════════════════════════════════════════
        //  缺陷 3：RawFrameReceived 订阅者异常必须隔离
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 客户端订阅 RawFrameReceived 并让回调每次抛异常；连接、收、发仍必须正常。
        /// 修复前异常会跳过 ProcessApdu/PumpAsync 并冒泡进适配器，握手/收数据即失败。
        /// </summary>
        [Test]
        public async Task RawFrame订阅者抛异常_客户端收发必须仍然正常()
        {
            var port = GetFreeTcpPort();
            var al = Al();
            var received = 0;

            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            var client = new Iec104Client("127.0.0.1", port, null, al);
            try
            {
                await server.StartAsync(port).ConfigureAwait(false);

                client.AsduReceived += (in AsduView _) => Interlocked.Increment(ref received);
                client.RawFrameReceived += _ => throw new InvalidOperationException("simulated raw-frame subscriber failure");

                await client.ConnectAsync().ConfigureAwait(false);
                Assert.That(client.IsActivated, Is.True, "STARTDT 握手不应被订阅者异常破坏");

                await server.EnqueueAsync(Spontaneous(al)).ConfigureAwait(false);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Volatile.Read(ref received) == 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20).ConfigureAwait(false);
                }

                Assert.That(Volatile.Read(ref received), Is.GreaterThan(0),
                    "RawFrameReceived 订阅者抛异常时必须被隔离：ProcessApdu/PumpAsync 仍须执行，"
                    + "业务数据必须继续到达");
            }
            finally
            {
                try { await client.DisconnectAsync().ConfigureAwait(false); } catch { /* best effort */ }
                client.Dispose();
                try { await server.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
                server.Dispose();
            }
        }

        /// <summary>
        /// 端点级确定性验证：RawFrameReceived 订阅者抛异常后，ProcessApdu 与 PumpAsync
        /// 仍必须执行（用 w=1 时必发的 S 确认作为 PumpAsync 已运行的证据）。
        /// </summary>
        [Test]
        public async Task RawFrame订阅者抛异常_端点仍须处理APDU并冲刷S确认()
        {
            var sink = new RecordingSink();
            var endpoint = new ApduEndpoint(new APCIParameters { W = 1 }, Al(), sink);
            endpoint.Rebuild(CancellationToken.None, _ => { }, _ => Task.CompletedTask);

            try
            {
                endpoint.RawFrameReceived += _ => throw new InvalidOperationException("boom");
                endpoint.Connection.OnUFrame(ApduCodec.StartDtCon); // 激活，I 帧才会派发

                // 完整 I 帧（N(S)=0），且带 RawFrame 拷贝以触发订阅者
                var apdu = BuildIFrame(0, 0, new byte[7]);

                var handled = await endpoint.HandleReceivedAsync(apdu);

                Assert.That(handled, Is.True, "订阅者异常不得被当作协议错误");
                Assert.That(sink.Frames.Count, Is.EqualTo(1),
                    "PumpAsync 必须继续执行：w=1 时应发出 S 确认");
                Assert.That(endpoint.Connection.LastProtocolErrorDetail,
                    Does.Contain("RawFrameReceived callback error"),
                    "订阅者异常应记录到既有诊断字段");
            }
            finally
            {
                endpoint.FinalizeSession();
            }
        }

        // ══════════════════════════════════════════════════════════
        //  缺陷 4：APCIParameters 参数校验
        // ══════════════════════════════════════════════════════════

        [Test]
        public void APCIParameters_单字段范围校验()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { K = 0 },
                "K=0 原会在 new KWindow 处抛且信息不足");
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { K = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { K = 1_000_000 },
                "K=1e6 原会分配 16MB 窗口");

            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { W = 0 },
                "W=0 会导致每帧都发 S 确认");
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { W = 32768 });

            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { T0 = 0 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { T1 = -5 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { T2 = -1 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new APCIParameters { T3 = -1 });

            // 0 = 不启用该定时器（既有合法用法：单元测试与兼容不规范装置的现场）
            Assert.DoesNotThrow(() => new APCIParameters { T1 = 0, T2 = 0, T3 = 0 });
        }

        [Test]
        public void APCIParameters_跨字段关系在使用处校验()
        {
            var al = Al();
            var sink = new ImmediateSink();

            // T0 < T1
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { using var c = new ApduConnection(new APCIParameters { T0 = 20 }, al, sink); },
                "T0 >= T1 必须在连接构造时拒绝");

            // T1 < T3
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { using var c = new ApduConnection(new APCIParameters { T3 = 10 }, al, sink); },
                "T3 <= T1 必须在连接构造时拒绝");

            // 显式配置 W >= K
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { using var c = new ApduConnection(new APCIParameters { K = 4, W = 8 }, al, sink); },
                "显式配置 W >= K 必须在连接构造时拒绝");

            // 合法配置（包括只调小 K、保留默认 W 的既有用法）不得误伤
            Assert.DoesNotThrow(
                () => { using var c = new ApduConnection(new APCIParameters { K = 4, W = 1 }, al, sink); });
            Assert.DoesNotThrow(
                () => { using var c = new ApduConnection(new APCIParameters { K = 2 }, al, sink); });
        }

        // ══════════════════════════════════════════════════════════
        //  辅助
        // ══════════════════════════════════════════════════════════

        private static ASDU Spontaneous(ApplicationLayerParameters al)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new MeasuredValueScaled(100, 42, new QualityDescriptor()));
            return asdu;
        }

        /// <summary>构造一个带完整原始帧拷贝的 I 帧 APDU（供端点接收路径测试）。</summary>
        private static IecApdu BuildIFrame(int ns, int nr, byte[] asdu)
        {
            var frame = new byte[ApduCodec.ApciLength + asdu.Length];
            ApduCodec.WriteIFormatHeader(frame.AsSpan(0, ApduCodec.ApciLength), ns, nr, asdu.Length);
            asdu.CopyTo(frame.AsSpan(ApduCodec.ApciLength));

            // CaptureRawFrames=true → OnParsingBody 会拷贝完整帧到 RawFrame，触发 RawFrameReceived
            var apdu = new IecApdu { CaptureRawFrames = true };
            if (!apdu.OnParsingHeader(frame.AsSpan(0, 2)) || !apdu.OnParsingBody(frame.AsSpan(2)))
            {
                throw new InvalidOperationException("failed to build test I-frame");
            }

            return apdu;
        }

        /// <summary>动态分配一个空闲 TCP 端口（避免测试间端口冲突）。</summary>
        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
