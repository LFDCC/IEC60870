/*
 *  EventQueueTests.cs
 *
 *  AsduEventQueue 单元测试：FIFO 顺序、计数、三种 EnqueueMode 满队策略、
 *  RequeueFront 队头回插保序、DequeueAsync 异步唤醒。
 *  队列是 internal 类型，经 InternalsVisibleTo 直接测试，无需套接字。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class EventQueueTests
    {
        private static byte[] Snap(int v) => new byte[] { (byte)v };

        [Test]
        public async Task Fifo_Order_Preserved()
        {
            var q = new AsduEventQueue(10, EnqueueMode.REMOVE_OLDEST);

            for (var i = 0; i < 5; i++)
            {
                Assert.IsTrue(q.TryEnqueue(Snap(i)));
            }

            Assert.AreEqual(5, q.Count);

            for (var i = 0; i < 5; i++)
            {
                var got = await q.DequeueAsync(CancellationToken.None);
                Assert.AreEqual(i, got[0], "出队顺序应与入队一致");
            }

            Assert.AreEqual(0, q.Count);
        }

        [Test]
        public async Task RemoveOldest_EvictsHead_WhenFull()
        {
            var q = new AsduEventQueue(3, EnqueueMode.REMOVE_OLDEST);

            q.TryEnqueue(Snap(1));
            q.TryEnqueue(Snap(2));
            q.TryEnqueue(Snap(3));
            q.TryEnqueue(Snap(4)); // 满 → 挤掉 1

            Assert.AreEqual(3, q.Count);
            Assert.AreEqual(2, (await q.DequeueAsync(CancellationToken.None))[0]);
            Assert.AreEqual(3, (await q.DequeueAsync(CancellationToken.None))[0]);
            Assert.AreEqual(4, (await q.DequeueAsync(CancellationToken.None))[0]);
        }

        [Test]
        public async Task Ignore_DropsNew_WhenFull()
        {
            var q = new AsduEventQueue(2, EnqueueMode.IGNORE);

            Assert.IsTrue(q.TryEnqueue(Snap(1)));
            Assert.IsTrue(q.TryEnqueue(Snap(2)));
            Assert.IsFalse(q.TryEnqueue(Snap(3)), "IGNORE 策略下满队应拒绝新快照");

            Assert.AreEqual(2, q.Count);
            Assert.AreEqual(1, (await q.DequeueAsync(CancellationToken.None))[0]);
            Assert.AreEqual(2, (await q.DequeueAsync(CancellationToken.None))[0]);
        }

        [Test]
        public void ThrowException_Raises_WhenFull()
        {
            var q = new AsduEventQueue(1, EnqueueMode.THROW_EXCEPTION);

            q.TryEnqueue(Snap(1));
            Assert.Throws<ASDUQueueException>(() => q.TryEnqueue(Snap(2)));
        }

        [Test]
        public async Task RequeueFront_PreservesOrder_AfterFailure()
        {
            var q = new AsduEventQueue(5, EnqueueMode.REMOVE_OLDEST);

            q.TryEnqueue(Snap(1));
            q.TryEnqueue(Snap(2));

            var head = await q.DequeueAsync(CancellationToken.None); // 取走 1
            Assert.AreEqual(1, head[0]);

            q.RequeueFront(head); // 模拟发送失败回插队头

            Assert.AreEqual(2, q.Count);
            Assert.AreEqual(1, (await q.DequeueAsync(CancellationToken.None))[0], "回插的 1 应重新排在最前");
            Assert.AreEqual(2, (await q.DequeueAsync(CancellationToken.None))[0]);
        }

        [Test]
        public async Task DequeueAsync_WaitsUntilEnqueue()
        {
            var q = new AsduEventQueue(5, EnqueueMode.REMOVE_OLDEST);

            var dequeueTask = q.DequeueAsync(CancellationToken.None);
            Assert.IsFalse(dequeueTask.IsCompleted, "空队列时 DequeueAsync 应挂起");

            q.TryEnqueue(Snap(42));

            var got = await dequeueTask;
            Assert.AreEqual(42, got[0]);
        }

        [Test]
        public async Task DequeueAsync_HonorsCancellation()
        {
            var q = new AsduEventQueue(5, EnqueueMode.REMOVE_OLDEST);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await q.DequeueAsync(cts.Token));
        }
    }
}
