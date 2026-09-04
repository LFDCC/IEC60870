/*
 *  RedundancyGroupTests.cs
 *
 *  事件队列 / 冗余组端到端测试（通过公开服务端 API）：
 *  - SINGLE_REDUNDANCY_GROUP：EnqueueAsync 的自发 ASDU 按序投递给已激活客户端。
 *  - CONNECTION_IS_REDUNDANCY_GROUP：每个客户端拥有独立队列，各自收到事件副本。
 *  - 队列计数：入队后、排空前的 GetNumberOfQueueEntries 反映缓冲深度。
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class RedundancyGroupTests
    {
        private const int SinglePort = 24301;
        private const int ConnPort = 24302;
        private const int QueueCountPort = 24303;
        private const int Ca = 1;

        private static ASDU Spontaneous(ApplicationLayerParameters al, int ioa)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, Ca, false);
            asdu.AddInformationObject(new SinglePointInformation(ioa, true, new QualityDescriptor()));
            return asdu;
        }

        private static async Task<T> Within<T>(Task<T> task, int seconds)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds));
            var completed = await Task.WhenAny(task, delay);
            if (ReferenceEquals(completed, delay))
            {
                throw new TimeoutException($"Timed out after {seconds}s.");
            }

            return await task;
        }

        [Test]
        public async Task SingleMode_Enqueue_DeliversInOrder()
        {
            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(SinglePort);

            var client = new Iec104Client("127.0.0.1", SinglePort);
            var received = new ConcurrentQueue<int>();
            var tcs = new TaskCompletionSource<bool>();
            client.AsduReceived += (in AsduView a) =>
            {
                var decoded = new ASDU(server.Parameters, a.Raw.ToArray(), 0, a.Raw.Length);
                if (decoded.GetElement(0) is SinglePointInformation spi)
                {
                    received.Enqueue(spi.ObjectAddress);
                    if (received.Count >= 3)
                    {
                        tcs.TrySetResult(true);
                    }
                }
            };
            await client.ConnectAsync();

            await server.EnqueueAsync(Spontaneous(server.Parameters, 10));
            await server.EnqueueAsync(Spontaneous(server.Parameters, 11));
            await server.EnqueueAsync(Spontaneous(server.Parameters, 12));

            await Within(tcs.Task, 5);

            var got = received.ToArray();
            Assert.AreEqual(new[] { 10, 11, 12 }, got, "自发事件应按入队顺序投递");

            await client.DisconnectAsync();
            server.Dispose();
        }

        [Test]
        public async Task ConnectionMode_EachClientGetsOwnCopy()
        {
            var server = new Iec104Server(mode: ServerMode.CONNECTION_IS_REDUNDANCY_GROUP);
            await server.StartAsync(ConnPort);

            var c1 = new Iec104Client("127.0.0.1", ConnPort);
            var c2 = new Iec104Client("127.0.0.1", ConnPort);
            int n1 = 0, n2 = 0;
            var both = new TaskCompletionSource<bool>();
            c1.AsduReceived += (in AsduView a) =>
            {
                if (Interlocked.Increment(ref n1) == 1 && Volatile.Read(ref n2) == 1)
                {
                    both.TrySetResult(true);
                }
            };
            c2.AsduReceived += (in AsduView a) =>
            {
                if (Interlocked.Increment(ref n2) == 1 && Volatile.Read(ref n1) == 1)
                {
                    both.TrySetResult(true);
                }
            };

            await c1.ConnectAsync();
            await c2.ConnectAsync();
            Assert.AreEqual(2, server.SessionCount);

            await server.EnqueueAsync(Spontaneous(server.Parameters, 55));

            await Within(both.Task, 5);
            Assert.GreaterOrEqual(Volatile.Read(ref n1), 1);
            Assert.GreaterOrEqual(Volatile.Read(ref n2), 1);

            await c1.DisconnectAsync();
            await c2.DisconnectAsync();
            server.Dispose();
        }

        [Test]
        public async Task SingleMode_QueueCount_ReflectsBuffering()
        {
            // 不连接任何客户端：入队的事件应滞留在共享队列中，计数可见。
            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);
            await server.StartAsync(QueueCountPort);

            await server.EnqueueAsync(Spontaneous(server.Parameters, 1));
            await server.EnqueueAsync(Spontaneous(server.Parameters, 2));

            // 无活动连接，排空循环挂起等待，队列保持 2 条。
            Assert.AreEqual(2, server.GetNumberOfQueueEntries());

            server.Dispose();
        }
    }
}
