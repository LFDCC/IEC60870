/*
 *  CS103SlaveIsolationTests.cs
 *
 *  CS103 **从站**侧回调异常隔离的回归测试。
 *
 *  背景（深度审计发现）：主站 `Iec103Client` 用 SafeInvoke/DispatchAsdu 把
 *  `AsduReceived` / `UnknownAsduReceived` / `FrameTraced` / `RawFrameReceived` 全部隔离，
 *  但从站 `Iec103Server.Raise*` 系列全是裸 `?.Invoke`。任一订阅者抛异常会冒泡到
 *  `SessionCore` 的读循环外层 catch，**该会话静默死亡**且不通知任何一方。
 *  这属于"只修一半"的对称缺口（主站修了、从站没修）。
 *
 *  这些用例用内存 PipeStream 跑完整主从流程，不需要硬件。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS103SlaveIsolationTests
    {
        private const byte LocalAddress = 1;

        private static Iec103Options Options() => new Iec103Options()
            .UseLinkParameters(l =>
            {
                l.MessageTimeoutMs = 1500;
                l.CharacterTimeoutMs = 200;
                l.MaxRepeat = 2;
                l.LocalLinkAddress = LocalAddress;
            });

        /// <summary>
        /// 从站 AsduReceived 订阅者抛异常时，会话必须继续工作（不能静默死亡）。
        /// </summary>
        [Test]
        public async Task 从站AsduReceived订阅者抛异常_会话必须继续工作()
        {
            using var pipes = new PipePair103();

            var threw = 0;
            await using var server = new Iec103Server(Options());
            server.AsduReceived += (session, in view) =>
            {
                Interlocked.Increment(ref threw);
                throw new InvalidOperationException("订阅者故意抛异常");
            };

            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, Options());
            await client.ConnectAsync();
            await client.ResetLinkAsync(LocalAddress);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(1), "前置条件：会话已建立");

            // 触发从站的 AsduReceived：主站发一条用户数据给从站
            await client.SendAsduAsync(Asdu.Measurement(1, 160, 1, 1234));

            // 关键断言：回调抛异常后，会话仍能完成常规链路交互
            Assert.DoesNotThrowAsync(async () => await client.RequestClass2Async(LocalAddress),
                "从站订阅者抛异常必须被隔离 —— 否则读循环退出、会话静默死亡（主站早已隔离，这是对称缺口）");

            Assert.That(Volatile.Read(ref threw), Is.GreaterThan(0), "前置条件：订阅者确实被调到过");
            Assert.That(server.SessionCount, Is.EqualTo(1), "隔离后会话不应被注销");
        }

        /// <summary>
        /// 从站 UnknownAsduReceived 订阅者抛异常同样必须被隔离。
        /// </summary>
        [Test]
        public async Task 从站UnknownAsdu订阅者抛异常_会话必须继续工作()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(Options());
            server.UnknownAsduReceived += (session, unknown) =>
                throw new InvalidOperationException("未知类型订阅者抛异常");

            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, Options());
            await client.ConnectAsync();
            await client.ResetLinkAsync(LocalAddress);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            // 发一个标准范围外、且未登记的 TI（54），触发从站 UnknownAsduReceived
            await server.BroadcastAsync(new Asdu(54, true, 1, CotCodes.Spontaneous, false, 1, 1, 1,
                new byte[] { 0x01 }));

            Assert.DoesNotThrowAsync(async () => await client.RequestClass2Async(LocalAddress),
                "UnknownAsduReceived 抛异常必须被隔离");

            Assert.That(server.SessionCount, Is.EqualTo(1), "隔离后会话不应被注销");
        }

        /// <summary>
        /// 从站 FrameTraced 订阅者抛异常必须被隔离（该回调在收发路径内同步执行）。
        /// </summary>
        [Test]
        public async Task 从站FrameTraced订阅者抛异常_会话必须继续工作()
        {
            using var pipes = new PipePair103();

            await using var server = new Iec103Server(Options());
            server.FrameTraced += (session, direction, frame) =>
                throw new InvalidOperationException("帧追踪订阅者抛异常");

            await server.StartAsync(pipes.Slave);

            await using var client = new Iec103Client(pipes.Master, Options());
            await client.ConnectAsync();

            // 复位链路本身就要收发多帧，全程会触发 FrameTraced
            Assert.DoesNotThrowAsync(async () => await client.ResetLinkAsync(LocalAddress),
                "FrameTraced 抛异常必须被隔离 —— 它在收发路径内同步执行，否则整个链路被一个订阅者打死");

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && server.SessionCount == 0)
            {
                await Task.Delay(20);
            }

            Assert.That(server.SessionCount, Is.EqualTo(1), "隔离后会话不应被注销");
        }
    }
}
