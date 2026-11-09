/*
 *  ProductionHardeningTests.cs
 *
 *  针对"生产可用性深度审查"发现并已修复的问题的回归护栏。
 *  每条都对应一个真实的、会影响现场的问题，且都验证过"回退修复即失败"。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class ProductionHardeningTests
    {
        // ══════════════════════════════════════════════════════════
        //  1. 共享调度器：最后一个连接注销不得掐掉新注册连接
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 复现原竞态：连接 A 注销（计数归零）与连接 B 注册并发。
        /// 原实现无条件 ForceShutdown 会清空所有分片堆，B 的 entry 被清成
        /// HeapIndex=-1 且不再 arm → B 的 T1/T2/T3 永久不运行（僵尸链路）。
        /// </summary>
        [Test]
        public async Task 共享调度器_并发注册与注销_不得静默停摆()
        {
            SharedTimeoutScheduler.ForceShutdown();

            for (var round = 0; round < 50; round++)
            {
                var aTicks = 0;
                var bTicks = 0;

                var a = SharedTimeoutScheduler.Instance.Register(
                    _ => { Interlocked.Increment(ref aTicks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                    () => 10,
                    () => { },
                    CancellationToken.None,
                    10);

                // 并发：A 注销 与 B 注册 同时发生
                var unregister = Task.Run(() => SharedTimeoutScheduler.Instance.Unregister(a));

                var b = SharedTimeoutScheduler.Instance.Register(
                    _ => { Interlocked.Increment(ref bTicks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                    () => 10,
                    () => { },
                    CancellationToken.None,
                    10);

                await unregister.ConfigureAwait(false);

                await Task.Delay(120).ConfigureAwait(false);

                // B 必须仍在被调度（原实现这里 bTicks 恒为 0）
                var bAlive = Volatile.Read(ref bTicks) > 0;

                SharedTimeoutScheduler.Instance.Unregister(b);

                Assert.That(bAlive, Is.True,
                    $"第 {round} 轮：注销 A 之后，并发注册的 B 必须继续被调度（实际 bTicks=0，"
                    + "原实现会 ForceShutdown 掐掉所有定时器 → 链路僵尸）");
            }

            SharedTimeoutScheduler.ForceShutdown();
        }

        /// <summary>
        /// 在已关闭的实例上注册：必须**透明转交**到活动实例并真的被调度，
        /// 既不能返回永不触发的 entry（僵尸链路），也不能抛异常拖垮调用方。
        /// </summary>
        /// <remarks>
        /// 真实竞态（CI 上已复现）：一个连接断开把 _liveCount 减到 0 关闭实例，
        /// 同时另一个连接正在 <c>Instance.Register</c>。此前实现直接抛
        /// ObjectDisposedException，而 Iec104TimerScheduler 的构造函数并不捕获它，
        /// 表现为"偶发重连失败"。现在 Instance 不再返回已关闭实例，
        /// Register 在竞态命中时转交活动实例。
        /// </remarks>
        [Test]
        public async Task 共享调度器_已关闭实例_注册必须转交活动实例()
        {
            SharedTimeoutScheduler.ForceShutdown();

            var closed = SharedTimeoutScheduler.Instance;
            var first = closed.Register(
                _ => new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Stop),
                () => 10, () => { }, CancellationToken.None, 10);
            closed.Unregister(first);      // 计数归零 → 关闭该实例

            Assert.That(closed.IsShutdown, Is.True, "前置条件：该实例已被关闭");

            var ticks = 0;
            var entry = closed.Register(
                _ =>
                {
                    Interlocked.Increment(ref ticks);
                    return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue);
                },
                () => 10, () => { }, CancellationToken.None, 10);

            await Task.Delay(150).ConfigureAwait(false);

            var alive = Volatile.Read(ref ticks) > 0;
            entry.Owner.Unregister(entry);

            SharedTimeoutScheduler.ForceShutdown();

            Assert.That(alive, Is.True,
                "在已关闭实例上注册必须转交到活动实例并真的被调度："
                + "既不能静默返回 arm=false 的僵尸 entry，也不能抛异常让调用方失败");
        }

        // ══════════════════════════════════════════════════════════
        //  2. Dispose 期间等待 STARTDT_CON：必须取消，不能"假成功"
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 连接在等 STARTDT_CON 期间被 Dispose：等待方必须收到取消。
        /// 原实现用 TrySetResult(true) 唤醒 → StartDataTransferAsync 正常返回 →
        /// Iec104Client 置 _dataTransferStarted=true → 之后 StartDataTransferAsync
        /// 变 no-op，应用以为链路已激活，实际 STARTDT 从未完成。
        /// </summary>
        [Test]
        public async Task 等待STARTDT期间Dispose_必须取消而不是假成功()
        {
            var sink = new SilentSink();
            var conn = new ApduConnection(new APCIParameters(), new ApplicationLayerParameters(), sink);

            var startTask = conn.StartDataTransferAsync(CancellationToken.None).AsTask();

            await Task.Delay(50).ConfigureAwait(false);

            conn.Dispose();

            var completed = await Task.WhenAny(startTask, Task.Delay(2000)).ConfigureAwait(false);
            Assert.That(completed, Is.SameAs(startTask), "Dispose 后等待方必须立即被唤醒（不能悬挂）");

            Assert.That(startTask.IsCanceled || startTask.IsFaulted, Is.True,
                "Dispose 唤醒等待者必须走「取消」语义；原实现 TrySetResult(true) 会让 "
                + "StartDataTransferAsync 假成功，导致 _dataTransferStarted 被错误置位");
        }

        /// <summary>发送成功但永不回包的 sink（用于制造"等 STARTDT_CON"场景）。</summary>
        private sealed class SilentSink : IIec104ApduSink
        {
            public bool IsConnected => true;

            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => default;
        }

        // ══════════════════════════════════════════════════════════
        //  3. CS104 StopAsync → StartAsync 后自发事件必须仍能投递
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 服务端 Stop → Start 之后，EnqueueAsync 的事件必须仍能到达客户端。
        /// 原实现 StopAsync 停掉冗余组排空循环且不可恢复（Init 幂等早退），
        /// 重启后服务端"能建连能收报文，但自发事件永不发出"——最危险的假在线。
        /// </summary>
        [Test]
        public async Task CS104服务端停止后重启_自发事件必须仍能投递()
        {
            const int port = 24501;
            var al = new ApplicationLayerParameters();

            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(port);

            // 第一轮：确认能投递
            var c1 = new Iec104Client("127.0.0.1", port, null, al);
            var got1 = 0;
            c1.AsduReceived += (in AsduView v) => Interlocked.Increment(ref got1);
            await c1.ConnectAsync();
            await server.EnqueueAsync(Spontaneous(al));
            await Task.Delay(500);
            await c1.DisconnectAsync();
            c1.Dispose();
            Assert.That(Volatile.Read(ref got1), Is.GreaterThan(0), "前置条件：首轮投递正常");

            // 停止 → 重启
            await server.StopAsync();
            await server.StartAsync(port);

            var c2 = new Iec104Client("127.0.0.1", port, null, al);
            var got2 = 0;
            c2.AsduReceived += (in AsduView v) => Interlocked.Increment(ref got2);
            await c2.ConnectAsync();

            // 必须等服务端把该会话认成"主用"，否则 Enqueue 时 _active 仍为 null，
            // 事件会留在队列里等，测试就测不到"排空循环是否活着"。
            await Task.Delay(500);

            await server.EnqueueAsync(Spontaneous(al));
            await Task.Delay(1000);

            var afterRestart = Volatile.Read(ref got2);
            var queued = server.GetNumberOfQueueEntries();

            try { await c2.DisconnectAsync(); } catch { }
            c2.Dispose();
            try { await server.StopAsync(); } catch { }
            server.Dispose();

            Assert.That(afterRestart, Is.GreaterThan(0),
                "StopAsync → StartAsync 之后自发事件必须仍能投递；原实现重启后排空循环已死，"
                + "服务端能建连能收但永不发送（无异常无日志的假在线）");
        }

        private static ASDU Spontaneous(ApplicationLayerParameters al)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new MeasuredValueScaled(100, 42, new QualityDescriptor()));
            return asdu;
        }

        // ══════════════════════════════════════════════════════════
        //  4. TypeHandlers 必须真正生效（私有类型可解码）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 客户端配置的 TypeHandlers 必须作用于接收路径。
        /// 原实现接收侧固定 new AsduView(asdu, _al)，从不注入注册表 →
        /// 用户注册的私有类型（如许继 166/168）解码时仍走 Default 抛 Unknown type id。
        ///
        /// 这里直接断言 ApduConnection 暴露的注册表与客户端配置的是同一个
        /// （注册表在构造时透传给端点，ConnectAsync 的 Rebuild 再传给连接）。
        /// </summary>
        [Test]
        public void 客户端TypeHandlers必须传入连接()
        {
            var al = new ApplicationLayerParameters();
            var registry = AsduTypeHandlerRegistry.Default;
            var client = new Iec104Client("127.0.0.1", 1, null, al);
            client.TypeHandlers = registry;

            var endpointField = typeof(Iec104Client).GetField("_endpoint",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var endpoint = endpointField?.GetValue(client);
            Assert.That(endpoint, Is.Not.Null);

            // 端点持有注册表（构造时注入），Rebuild 时透传给 ApduConnection
            var handlersField = endpoint.GetType().GetField("_typeHandlers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var actual = handlersField?.GetValue(endpoint);

            Assert.That(actual, Is.SameAs(registry),
                "Iec104Client.TypeHandlers 必须在构造时注入 ApduEndpoint，"
                + "并在 Rebuild 时透传给 ApduConnection —— 否则接收路径的 GetElement 永远用 Default，"
                + "用户注册的私有类型无法解码（现场极难定位）");
        }
    }
}
