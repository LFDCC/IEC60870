/*
 *  CS103LifecycleTests.cs
 *
 *  IEC60870.CS103 生命周期/稳定性回归测试。
 *
 *  覆盖（对应代码审查缺陷编号）：
 *  A/B/G —— 主站反复断开/重连：旧传输必须退订并释放，读循环退出必须收尾；
 *  C/E   —— 会话关闭后核心必须释放、服务端会话表必须归零；
 *  F     —— SessionClosed 订阅者抛异常不能打断 StopAsync。
 *  D     —— 会话 1/2 级队列上界与丢弃计数。
 *
 *  端口一律用 TcpListener(0) 动态分配，避免与其它测试/本机服务冲突。
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS103LifecycleTests
    {
        private const byte Ca = 1;
        private const byte Fun = 160;

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
                l.CharacterTimeoutMs = 150;
                l.MaxRepeat = 1;
            });

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            return condition();
        }

        /// <summary>
        /// A/B/G 护栏：50 轮「新建客户端 → 连接 → 复位 → 1 级召唤 → 断开 → 释放」。
        /// 每轮都必须成功；结束后服务端 SessionCount 必须回落到 0。
        /// </summary>
        [Test]
        public async Task 五十次断开重连_服务端会话最终归零()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            for (var i = 0; i < 50; i++)
            {
                var client = new Iec103Client("127.0.0.1", port, FastOptions());
                try
                {
                    await client.ConnectAsync();
                    Assert.That(client.IsConnected, Is.True, $"第 {i + 1} 轮连接失败");

                    await client.ResetLinkAsync(Ca);

                    var collected = await client.RequestClass1Async(Ca);
                    Assert.That(collected, Is.Not.Null, $"第 {i + 1} 轮 1 级召唤失败");
                }
                finally
                {
                    await client.DisconnectAsync();
                    await client.DisposeAsync();
                }
            }

            Assert.That(
                await WaitUntilAsync(() => server.SessionCount == 0, TimeSpan.FromSeconds(10)),
                Is.True,
                "50 轮断开后服务端会话必须归零（旧传输/旧会话泄漏时这里会残留）");
        }

        /// <summary>
        /// A/B/G 护栏：同一个 client 反复 Connect/Disconnect 10 次。
        /// 第二轮起仍必须能收到应答，验证旧传输已释放且旧读循环未抢新连接的数据。
        /// </summary>
        [Test]
        public async Task 同一客户端反复连接断开十次仍能收发()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());

            for (var i = 0; i < 10; i++)
            {
                await client.ConnectAsync();
                Assert.That(client.IsConnected, Is.True, $"第 {i + 1} 轮连接失败");

                await client.ResetLinkAsync(Ca);

                var expected = (short)(i + 1);
                await server.SendMeasurementAsync(Ca, Fun, 1, expected);

                var collected = await client.RequestClass2Async(Ca);
                Assert.That(collected, Is.Not.Null, $"第 {i + 1} 轮 2 级召唤失败");

                var measurement = collected.FirstOrDefault(a => a.TypeId == TypeIds.Measurand);
                Assert.That(measurement, Is.Not.Null, $"第 {i + 1} 轮未收到测量值应答");
                Assert.That(Asdu.DecodeMeasurement(measurement), Is.EqualTo(expected),
                    $"第 {i + 1} 轮收到的测量值不正确（旧传输/旧读循环可能仍在工作）");

                await client.DisconnectAsync();
                Assert.That(client.IsConnected, Is.False, $"第 {i + 1} 轮断开后 IsConnected 应为 false");
            }
        }

        /// <summary>
        /// F 护栏：SessionClosed 订阅者抛异常不能打断 StopAsync。
        /// </summary>
        [Test]
        public async Task SessionClosed订阅者抛异常_StopAsync仍能在时限内完成()
        {
            var port = AllocatePort();

            await using var server = new Iec103Server(FastOptions());
            await server.StartAsync(port);

            await using var client = new Iec103Client("127.0.0.1", port, FastOptions());
            await client.ConnectAsync();
            await client.ResetLinkAsync(Ca);

            Assert.That(
                await WaitUntilAsync(() => server.SessionCount == 1, TimeSpan.FromSeconds(5)),
                Is.True,
                "应先建立 1 个会话，才能触发 SessionClosed");

            server.SessionClosed += (session, reason) =>
                throw new InvalidOperationException("boom");

            var stopTask = server.StopAsync();
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.That(completed, Is.SameAs(stopTask), "StopAsync 必须在 5 秒内返回");
            Assert.DoesNotThrowAsync(async () => await stopTask, "订阅者异常不得从 StopAsync 冒出");
            Assert.That(server.SessionCount, Is.Zero, "停止后会话数应为 0");
        }

        /// <summary>
        /// D 护栏：队列上界生效，超出部分丢弃最旧数据并累计 DroppedAsduCount。
        /// </summary>
        [Test]
        public async Task 会话队列超过上界时丢弃最旧数据()
        {
            await using var server = new Iec103Server(FastOptions());
            var transport = new NullLinkTransport();

            var core = new SessionCore(
                server,
                transport,
                new Iec103LinkParameters(),
                sessionId: 7,
                logger: null,
                filterByAddress: false)
            {
                MaxQueueLength = 3,
            };

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    core.EnqueueRaw(new[] { (byte)i }, isClass1: true);
                }

                Assert.That(core.DroppedAsduCount, Is.EqualTo(7), "上界 3、入队 10 条应丢弃 7 条");
            }
            finally
            {
                await core.DisposeAsync();
            }
        }

#pragma warning disable CS0067 // 测试桩不触发 TransportClosed
        /// <summary>不读不写的传输桩，仅用于直接构造 <see cref="SessionCore"/>。</summary>
        private sealed class NullLinkTransport : IIec103LinkTransport
        {
            public bool IsOpen => true;

            public string EndPoint => "null";

            public event EventHandler<Iec103TransportClosedEventArgs> TransportClosed;

            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
                => new ValueTask<int>(0);

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
                => default;

            public ValueTask DisposeAsync() => default;
        }
#pragma warning restore CS0067
    }
}
