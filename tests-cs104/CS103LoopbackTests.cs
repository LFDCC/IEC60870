/*
 *  CS103LoopbackTests.cs
 *
 *  IEC60870.CS103 端到端自环测试：真实 TCP（TouchSocket 客户端 + 服务端）。
 *
 *  覆盖：
 *  1. 链路复位（RESET CU）与链路状态查询；
 *  2. 时间同步往返；
 *  3. 总召唤全流程（TI=7 → 1 级数据 → TI=8 终止）；
 *  4. 1 级突发数据召唤（ACD 位驱动排空）；
 *  5. 测量值（2 级队列）；
 *  6. 多主站并发（103 现场核心需求）；
 *  7. 私有类型 TI/FUN/INF 收发闭环；
 *  8. 未识别 ASDU 兜底（UnknownAsduReceived 收到完整 Raw）。
 *
 *  说明：103 现场常见 2404 端口，但语义与 104 完全不同——这里承载裸 FT1.2 帧。
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS103LoopbackTests
    {
        private const byte Ca = 1;
        private const byte Fun = 160;
        private const byte Inf = 1;

        /// <summary>分配一个空闲端口（避免与本机其它测试冲突）。</summary>
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
                l.MessageTimeoutMs = 1500;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = 2;
            });

        [Test]
        public async Task 链路复位与链路状态查询()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();

            Assert.That(client.IsConnected, Is.True);

            await client.ResetLinkAsync(Ca);
            var status = await client.RequestLinkStatusAsync(Ca);

            Assert.That(status.Format, Is.EqualTo(Ft12FrameFormat.FixedLength));
            var control = LinkControl.FromByte(status.Control);
            Assert.That(control.IsPrimary, Is.False, "从站应答 PRM=0");
            Assert.That(control.Function, Is.EqualTo((byte)SecondaryFunction.LinkStatus));
        }

        [Test]
        public async Task 从站记录会话且链路地址正确()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(0x0A);

            // 等待会话建链
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(1));
            var session = server.Sessions[0];
            Assert.That(session.LinkEstablished, Is.True);
            Assert.That(session.LinkAddress, Is.EqualTo(0x0A));
        }

        [Test]
        public async Task 时间同步命令可达从站()
        {
            var port = AllocatePort();
            var received = new List<Asdu>();

            await using var server = new Iec103Server(FastOptions());
            server.AsduReceived += (session, in view) =>
            {
                lock (received)
                {
                    received.Add(Asdu.Materialize(view));
                }
            };
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var time = new DateTime(2026, 7, 23, 14, 30, 5, 678);
            await client.SendClockSyncAsync(Ca, time);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && received.Count == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(received, Is.Not.Empty);
            var sync = received.First(a => a.TypeId == TypeIds.ClockSync);
            Assert.That(sync.Cot, Is.EqualTo(CotCodes.ClockSync));

            var decoded = Cp56Time2a.Decode(sync.Data);
            Assert.That(decoded.Hour, Is.EqualTo(14));
            Assert.That(decoded.Minute, Is.EqualTo(30));
            Assert.That(decoded.Year, Is.EqualTo(26));
        }

        [Test]
        public async Task 总召唤全流程_含终止帧()
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

                // 注意：AsduReceived 是同步委托（in 参数 + ref struct 视图），无法用 async lambda。
                // 实际业务中应在此入队，由独立发送循环上送；测试里直接同步入队即可
                // （会话的队列是 ConcurrentQueue，入队不阻塞）。
                for (var i = 1; i <= pointCount; i++)
                {
                    session.SendAsduAsync(Asdu.TimeTaggedMessage(
                        Ca, Fun, (byte)i, DpiQuality.FromDpi(2),
                        Cp56Time2a.FromDateTime(new DateTime(2026, 7, 23, 14, 30, i)), CotCodes.RemoteOperation))
                        .AsTask().GetAwaiter().GetResult();
                }

                session.SendAsduAsync(Asdu.GeneralInterrogationEnd(Ca))
                    .AsTask().GetAwaiter().GetResult();
            };
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var result = await client.GeneralInterrogationAsync(Ca);

            var points = result.Where(a => a.TypeId == TypeIds.TimeTaggedMessage).ToList();
            var end = result.Where(a => a.TypeId == TypeIds.GeneralInterrogationEnd).ToList();

            Assert.That(points.Count, Is.EqualTo(pointCount), "应收到全部监视点");
            Assert.That(end.Count, Is.EqualTo(1), "应收到唯一终止帧");
            Assert.That(result.Last().TypeId, Is.EqualTo(TypeIds.GeneralInterrogationEnd), "终止帧在最后");

            // INF 序列应为 1..3，且 COT=11（总召唤回送）
            Assert.That(points.Select(p => (int)p.Inf).ToArray(), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(points.All(p => p.Cot == CotCodes.RemoteOperation), Is.True);
        }

        [Test]
        public async Task 一级突发数据召唤_排空队列()
        {
            var port = AllocatePort();
            const int eventCount = 5;

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            // 等会话建链后推 5 条突发
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            for (var i = 1; i <= eventCount; i++)
            {
                await server.BroadcastAsync(Asdu.ProtectionEvent(
                    Ca, Fun, (byte)i, DpiQuality.FromDpi(2), relativeTimeMs: i * 100, fan: i,
                    time: Cp56Time2a.FromDateTime(new DateTime(2026, 7, 23, 14, 30, 0)), cot: CotCodes.Spontaneous));
            }

            var collected = await client.RequestClass1Async(Ca, stop: null);

            Assert.That(collected.Count, Is.GreaterThanOrEqualTo(eventCount));
            Assert.That(collected.All(a => a.TypeId == TypeIds.TimeTaggedMessagePrivate || a.TypeId == TypeIds.TimeTaggedMessage), Is.True);
        }

        [Test]
        public async Task 测量值走二级队列()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            await server.SendMeasurementAsync(Ca, Fun, 1, 12345);
            await server.SendMeasurementAsync(Ca, Fun, 2, -6789);

            var collected = await client.RequestClass2Async(Ca);

            var measurements = collected.Where(a => a.TypeId == TypeIds.Measurand).ToList();
            Assert.That(measurements.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(Asdu.DecodeMeasurement(measurements[0]), Is.EqualTo(12345));
            Assert.That(Asdu.DecodeMeasurement(measurements[1]), Is.EqualTo(-6789));
        }

        [Test]
        public async Task 多主站并发_各自独立会话()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var clientA = new Iec103Client("127.0.0.1", port, FastOptions());
            await using var clientB = new Iec103Client("127.0.0.1", port, FastOptions());
            await using var clientC = new Iec103Client("127.0.0.1", port, FastOptions());

            await clientA.ConnectAsync();
            await clientB.ConnectAsync();
            await clientC.ConnectAsync();

            await clientA.ResetLinkAsync(1);
            await clientB.ResetLinkAsync(2);
            await clientC.ResetLinkAsync(3);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && server.SessionCount < 3)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(3), "103 必须支持多主站并发（与 CS101 单链路语义不同）");

            var addresses = server.Sessions.Select(s => s.LinkAddress).OrderBy(a => a).ToArray();
            Assert.That(addresses, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public async Task 私有类型_FUN_INF注册后收发闭环()
        {
            const byte vendorTi = 200;
            const byte vendorFun = 200;
            const byte vendorInf = 7;

            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            server.PrivateTypes.RegisterFunInf(vendorFun, vendorInf, new VendorTestHandler());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            client.PrivateTypes.RegisterFunInf(vendorFun, vendorInf, new VendorTestHandler());

            var decoded = new List<string>();

            // 私有类型由业务代码自己 switch/knowledge 解析，库只提供原始视图。
            client.AsduReceived += (in Iec103AsduView asdu) =>
            {
                if (asdu.TypeId == vendorTi && asdu.Fun == vendorFun && asdu.Inf == vendorInf)
                {
                    lock (decoded)
                    {
                        decoded.Add("VENDOR:" + Convert.ToHexString(asdu.Data.ToArray()));
                    }
                }
            };

            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            // 从站上送私有 FUN/INF 报文
            var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var asdu = new Asdu(vendorTi, true, 1, CotCodes.Spontaneous, false, Ca, vendorFun, vendorInf, payload);
            await server.BroadcastAsync(asdu);

            // 主站召唤取回
            var collected = await client.RequestClass1Async(Ca);

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && decoded.Count == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(collected.Any(a => a.TypeId == vendorTi && a.Fun == vendorFun && a.Inf == vendorInf), Is.True,
                "私有 TI/FUN/INF 应原样到达");
            Assert.That(decoded, Is.Not.Empty, "私有处理器应被调用");
            Assert.That(decoded[0], Is.EqualTo("VENDOR:DEADBEEF"));
        }

        [Test]
        public async Task 未识别ASDU_兜底事件收到完整原始字节()
        {
            const byte unknownTi = 250;
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions()
                .UseUnknownTypePolicy(Iec103UnknownTypePolicy.Ignore));

            var unknowns = new List<Iec103UnknownAsdu>();
            client.UnknownAsduReceived += u =>
            {
                lock (unknowns)
                {
                    unknowns.Add(u);
                }
            };

            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            var payload = new byte[] { 0x11, 0x22, 0x33 };
            await server.BroadcastAsync(new Asdu(unknownTi, true, 1, CotCodes.Spontaneous, false, Ca, 200, 5, payload));

            await client.RequestClass1Async(Ca);

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && unknowns.Count == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(unknowns, Is.Not.Empty, "未识别 TI 应触发兜底事件");
            var u = unknowns[0];
            Assert.That(u.TypeId, Is.EqualTo(unknownTi));
            Assert.That(u.Fun, Is.EqualTo(200));
            Assert.That(u.Inf, Is.EqualTo(5));
            Assert.That(u.Raw, Is.EqualTo(new[] { unknownTi, 0x81, 0x01, Ca, 200, 5, 0x11, 0x22, 0x33 }),
                "必须保留完整原始字节（含头部），供现场逆向");
        }

        [Test]
        public async Task 原始帧事件_收发均可抓包()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());

            var received = new List<byte[]>();
            client.RawFrameReceived += raw =>
            {
                lock (received)
                {
                    received.Add(raw);
                }
            };

            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            await server.SendMeasurementAsync(Ca, Fun, 1, 100);
            await client.RequestClass2Async(Ca);

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && received.Count == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(received, Is.Not.Empty);

            // 抓包应同时覆盖链路帧（10 ... 16 / E5）与用户数据帧（68 L L 68 ... 16）。
            // 这里挑出可变帧验证 FT1.2 结构。
            var variableFrames = received.Where(f => f.Length >= 6 && f[0] == 0x68).ToList();
            Assert.That(variableFrames, Is.Not.Empty, "抓包必须包含可变帧（ASDU 数据）");

            var frame = variableFrames[0];
            Assert.That(frame[0], Is.EqualTo(0x68));
            Assert.That(frame[1], Is.EqualTo(frame[2]), "长度双写");
            Assert.That(frame[3], Is.EqualTo(0x68));
            Assert.That(frame[^1], Is.EqualTo(0x16));

            // 固定帧也应在抓包中出现（复位/ACK 等链路交互同样需要可观测）
            var fixedFrames = received.Where(f => f.Length == 5 && f[0] == 0x10).ToList();
            Assert.That(fixedFrames, Is.Not.Empty, "抓包必须包含固定帧（链路层交互）");
        }

        [Test]
        public async Task 客户端断开后服务端会话应清理()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(1));

            await client.DisconnectAsync();
            await client.DisposeAsync();

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && server.SessionCount > 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.Zero, "断开后会话应被移除");
        }

        /// <summary>测试用私有类型处理器：解码为 <c>VENDOR:HEX</c>。</summary>
        private sealed class VendorTestHandler : IIec103PrivateTypeHandler
        {
            public byte TypeId => 200;

            public string VendorTag => "TestVendor";

            public byte? Fun => 200;

            public byte? Inf => 7;



            public byte[] Encode(Iec103DecodedAsdu value) => Array.Empty<byte>();
        }
    }
}
