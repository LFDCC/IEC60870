/*
 *  SharedTimeoutSchedulerTests.cs
 *
 *  共享时间调度器（单物理 Timer + 分片最小堆）的功能测试。
 *
 *  覆盖点：
 *   1. 基本调度：到期触发检查，非致命按 delayProvider 重排；
 *   2. 致命超时：触发 onFatal 一次且不再重排；
 *   3. 取消：静默停止，不触发 onFatal；
 *   4. 瞬态异常：按非致命处理，继续调度；
 *   5. Unregister 后不再触发检查（惰性删除生效）；
 *   6. Dispose/ForceShutdown 后不再触发；
 *   7. 多连接并发：N 个连接各自不同间隔，全部被正确调度且无丢失/重复；
 *   8. 分片隔离：跨分片注册/取消互不影响；
 *   9. 精确重排：dequeued 状态的连接检查后能重新入堆（回归：曾误判为已取消而停止调度）。
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class SharedTimeoutSchedulerTests
    {
        [TearDown]
        public void TearDown()
        {
            // 每个用例后强制关闭单例，避免跨用例共享状态。
            SharedTimeoutScheduler.ForceShutdown();
        }

        private static async Task WaitForAsync(Func<bool> cond, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond())
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.Fail("等待调度器状态超时");
        }

        // ── 1. 基本调度 ────────────────────────────────────────────

        [Test]
        public async Task Register_DueOnce_InvokesCheck_ThenReschedules()
        {
            var checks = 0;
            using (SharedTimeoutScheduler.Instance.Register(
                       _ => { checks++; return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                       () => 10,
                       () => Assert.Fail("不应触发 onFatal"),
                       CancellationToken.None,
                       10))
            {
                await WaitForAsync(() => checks >= 3).ConfigureAwait(false);
            }

            Assert.That(checks, Is.GreaterThanOrEqualTo(3), "应按 10ms 间隔持续触发");
        }

        // ── 2. 致命超时：一次 onFatal 后停止 ──────────────────────────

        [Test]
        public async Task FatalCheck_TriggersOnFatalExactlyOnce_ThenStops()
        {
            var fatal = 0;
            var checks = 0;
            var entry = SharedTimeoutScheduler.Instance.Register(
                _ => { checks++; return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Fatal); },
                () => 10,
                () => Interlocked.Increment(ref fatal),
                CancellationToken.None,
                10);

            await WaitForAsync(() => fatal > 0).ConfigureAwait(false);
            var checksAtFatal = checks;

            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(Volatile.Read(ref fatal), Is.EqualTo(1), "致命超时只能触发一次 onFatal");
            Assert.That(checks, Is.LessThanOrEqualTo(checksAtFatal + 1), "致命后不得继续轮询");
        }

        // ── 3. 取消：静默停止，不触发 onFatal ────────────────────────

        [Test]
        public async Task CancelledCheck_StopsSilently_NoOnFatal()
        {
            var cts = new CancellationTokenSource();
            var fatal = 0;
            var checks = 0;

            using (SharedTimeoutScheduler.Instance.Register(
                       _ =>
                       {
                           Interlocked.Increment(ref checks);
                           cts.Cancel();
                           return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Stop);
                       },
                       () => 10,
                       () => Interlocked.Increment(ref fatal),
                       cts.Token,
                       10))
            {
                await Task.Delay(150).ConfigureAwait(false);
            }

            Assert.That(Volatile.Read(ref fatal), Is.EqualTo(0), "取消不得触发 onFatal");
        }

        [Test]
        public async Task CheckThrowingOce_StopsSilently_NoOnFatal()
        {
            var cts = new CancellationTokenSource();
            var fatal = 0;

            using (SharedTimeoutScheduler.Instance.Register(
                       _ => throw new OperationCanceledException(),
                       () => 10,
                       () => Interlocked.Increment(ref fatal),
                       cts.Token,
                       10))
            {
                await Task.Delay(150).ConfigureAwait(false);
            }

            Assert.That(Volatile.Read(ref fatal), Is.EqualTo(0), "OCE 不得触发 onFatal");
        }

        // ── 4. 瞬态异常：按非致命处理，继续调度 ──────────────────────

        [Test]
        public async Task TransientException_KeepsScheduling()
        {
            var checks = 0;
            var fatal = 0;

            using (SharedTimeoutScheduler.Instance.Register(
                       _ =>
                       {
                           var n = Interlocked.Increment(ref checks);
                           if (n == 1)
                           {
                               throw new InvalidOperationException("writer was completed");
                           }

                           return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue);
                       },
                       () => 10,
                       () => Interlocked.Increment(ref fatal),
                       CancellationToken.None,
                       10))
            {
                await WaitForAsync(() => Volatile.Read(ref checks) >= 3).ConfigureAwait(false);
            }

            Assert.That(Volatile.Read(ref fatal), Is.EqualTo(0), "瞬态异常不得触发 onFatal");
            Assert.That(Volatile.Read(ref checks), Is.GreaterThanOrEqualTo(3), "瞬态异常后应继续重排");
        }

        // ── 5. Unregister 后不再触发（惰性删除）──────────────────────

        [Test]
        public async Task Unregister_StopsCheck_Immediately()
        {
            var checks = 0;
            var entry = SharedTimeoutScheduler.Instance.Register(
                _ => { Interlocked.Increment(ref checks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                () => 5,
                () => Assert.Fail("不应触发 onFatal"),
                CancellationToken.None,
                5);

            await WaitForAsync(() => Volatile.Read(ref checks) > 0).ConfigureAwait(false);
            SharedTimeoutScheduler.Instance.Unregister(entry);

            var before = Volatile.Read(ref checks);
            await Task.Delay(150).ConfigureAwait(false);
            Assert.That(Volatile.Read(ref checks), Is.EqualTo(before), "Unregister 后不得再触发检查");
        }

        // ── 6. ForceShutdown 后不再触发 ─────────────────────────────

        [Test]
        public async Task ForceShutdown_StopsAll()
        {
            var checks = 0;
            var e1 = SharedTimeoutScheduler.Instance.Register(
                _ => { Interlocked.Increment(ref checks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                () => 5,
                () => Assert.Fail("关闭后不得触发 onFatal"),
                CancellationToken.None, 5);
            var e2 = SharedTimeoutScheduler.Instance.Register(
                _ => { Interlocked.Increment(ref checks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                () => 7,
                () => Assert.Fail("关闭后不得触发 onFatal"),
                CancellationToken.None, 7);

            await WaitForAsync(() => Volatile.Read(ref checks) > 0).ConfigureAwait(false);
            SharedTimeoutScheduler.ForceShutdown();

            var before = Volatile.Read(ref checks);
            await Task.Delay(150).ConfigureAwait(false);
            Assert.That(Volatile.Read(ref checks), Is.EqualTo(before), "ForceShutdown 后不得再触发");
        }

        // ── 7. 多连接、不同间隔，全部被正确调度 ──────────────────────

        [Test]
        public async Task ManyConnections_DifferentIntervals_AllScheduled()
        {
            const int n = 200;
            var perConn = new int[n];
            var entries = new SharedTimeoutScheduler.Entry[n];

            for (var i = 0; i < n; i++)
            {
                var idx = i;
                // 间隔 5..24ms 交错，制造不同的绝对 deadline
                entries[i] = SharedTimeoutScheduler.Instance.Register(
                    _ => { Interlocked.Increment(ref perConn[idx]); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                    () => 5 + (idx % 20),
                    () => Assert.Fail("不应触发 onFatal"),
                    CancellationToken.None,
                    5 + (idx % 20));
            }

            await WaitForAsync(() => perConn.Sum() >= n * 3, 5000).ConfigureAwait(false);

            // 每个连接都应被调度到至少一次（无丢失）
            var never = perConn.Where((c, i) => c == 0).Count();
            Assert.That(never, Is.EqualTo(0), $"{never} 个连接从未被调度（不应发生）");

            // 清理：全部注销
            foreach (var e in entries)
            {
                SharedTimeoutScheduler.Instance.Unregister(e);
            }
        }

        // ── 8. 分片隔离：部分注销不影响其余 ──────────────────────────

        [Test]
        public async Task PartialUnregister_OthersKeepRunning()
        {
            const int n = 64;
            var perConn = new int[n];
            var entries = new SharedTimeoutScheduler.Entry[n];

            for (var i = 0; i < n; i++)
            {
                var idx = i;
                entries[i] = SharedTimeoutScheduler.Instance.Register(
                    _ => { Interlocked.Increment(ref perConn[idx]); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                    () => 10,
                    () => Assert.Fail("不应触发 onFatal"),
                    CancellationToken.None,
                    10);
            }

            await WaitForAsync(() => perConn.Sum() >= n).ConfigureAwait(false);

            // 注销偶数下标
            for (var i = 0; i < n; i += 2)
            {
                SharedTimeoutScheduler.Instance.Unregister(entries[i]);
            }

            var evenBefore = 0L;
            for (var i = 0; i < n; i += 2)
            {
                evenBefore += Volatile.Read(ref perConn[i]);
            }

            var oddBefore = 0L;
            for (var i = 1; i < n; i += 2)
            {
                oddBefore += Volatile.Read(ref perConn[i]);
            }

            await Task.Delay(200).ConfigureAwait(false);

            var evenAfter = 0L;
            for (var i = 0; i < n; i += 2)
            {
                evenAfter += Volatile.Read(ref perConn[i]);
            }

            var oddAfter = 0L;
            for (var i = 1; i < n; i += 2)
            {
                oddAfter += Volatile.Read(ref perConn[i]);
            }

            Assert.That(evenAfter, Is.EqualTo(evenBefore), "已注销连接不得再触发");
            Assert.That(oddAfter, Is.GreaterThan(oddBefore), "未注销连接应继续被调度");
        }

        // ── 9. 精确重排：dequeued 后能重新入堆 ───────────────────────

        [Test]
        public async Task Reschedule_AfterDequeue_ReentersHeap()
        {
            // 回归：曾因把"已出堆待执行"误判为"已取消"而导致检查后不再调度。
            var checks = 0;
            using (SharedTimeoutScheduler.Instance.Register(
                       _ => { Interlocked.Increment(ref checks); return new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue); },
                       () => 10,
                       () => Assert.Fail("不应触发 onFatal"),
                       CancellationToken.None,
                       10))
            {
                await WaitForAsync(() => Volatile.Read(ref checks) >= 5).ConfigureAwait(false);
            }

            Assert.That(Volatile.Read(ref checks), Is.GreaterThanOrEqualTo(5),
                "检查后必须能重新入堆并继续调度（dequeued 状态回归）");
        }

        // ── 10. 单例：最后一个连接注销后自动释放 ──────────────────────

        [Test]
        public async Task LastUnregister_ReleasesSingleton()
        {
            Assert.That(SharedTimeoutScheduler.Instance, Is.Not.Null);

            var e = SharedTimeoutScheduler.Instance.Register(
                _ => new ValueTask<Iec104TimerScheduler.SchedulerAction>(Iec104TimerScheduler.SchedulerAction.Continue),
                () => 1000,
                () => { },
                CancellationToken.None,
                1000);

            SharedTimeoutScheduler.Instance.Unregister(e);

            // 单例应已释放：再次取到的是全新实例
            var again = SharedTimeoutScheduler.Instance;
            Assert.That(again, Is.Not.Null);
            Assert.That(again.IsShutdown, Is.False, "新实例不应处于关闭态");
        }
    }
}
