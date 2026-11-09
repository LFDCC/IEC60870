/*
 *  CS101ByteQueueTests.cs
 *
 *  锁死 AsyncByteQueue 的「写入批次」与「读取」许可配平。
 *
 *  背景缺陷：Write 原实现按字节 Release(N)，而 ReadAsync 每次只消费一个许可
 *  并一次取走缓冲区内全部字节。写 100 字节后残留 99 个许可 → 后续 99 次
 *  ReadAsync 在 _count == 0 时立即返回 0，LinkLayerEngine 把 0 当作"暂无数据"
 *  立即重入，形成每批 N 字节 N 次空转的忙循环（每次还 new 一个 linked CTS）。
 *  长跑下就是持续的 CPU 尖峰与分配风暴。
 *
 *  判据用「耗时」而不是内部计数：无残留许可时，超时读必须真的等满超时；
 *  有残留许可时会在微秒级返回 0。
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CS101ByteQueueTests
    {
        private const int ReadTimeoutMs = 300;

        private static async Task<long> TimeReadAsync(AsyncByteQueue queue, int timeoutMs = ReadTimeoutMs)
        {
            var buffer = new byte[256];
            var sw = Stopwatch.StartNew();
            await queue.ReadAsync(buffer, timeoutMs, CancellationToken.None);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// 一次写入一批字节后只能产生一个许可：第一次读取走全部字节，
        /// 第二次读必须老老实实等超时，不能立刻返回 0。
        /// </summary>
        [Test]
        public async Task 单批写入_只放行一个许可_后续读必须等待()
        {
            using var queue = new AsyncByteQueue();

            var payload = new byte[64];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)i;
            }

            queue.Write(payload);

            var buffer = new byte[256];
            var first = await queue.ReadAsync(buffer, ReadTimeoutMs, CancellationToken.None);

            Assert.That(first, Is.EqualTo(payload.Length), "第一次读必须取走整批字节");

            var elapsed = await TimeReadAsync(queue);

            Assert.That(elapsed, Is.GreaterThanOrEqualTo(ReadTimeoutMs / 2),
                $"第二批读耗时 {elapsed}ms —— 说明 Write 按字节放行了许可，残留许可让读立即返回 0。"
                + "这正是忙循环的根因（原实现 Release(accepted)）");
        }

        /// <summary>Clear 必须把上一会话残留的许可一起抽干，否则重连后仍会空转。</summary>
        [Test]
        public async Task Clear后不得残留许可()
        {
            using var queue = new AsyncByteQueue();

            queue.Write(new byte[32]);
            queue.Clear();

            var buffer = new byte[256];
            var read = await queue.ReadAsync(buffer, 1, CancellationToken.None);
            Assert.That(read, Is.EqualTo(0), "清空后没有数据可读");

            var elapsed = await TimeReadAsync(queue);

            Assert.That(elapsed, Is.GreaterThanOrEqualTo(ReadTimeoutMs / 2),
                $"Clear 后读耗时 {elapsed}ms —— 残留许可未被抽干，重连后读循环会连续空转");
        }

        /// <summary>连续多次写入仍逐次可读，不会丢批次。</summary>
        [Test]
        public async Task 多批写入_逐批可读()
        {
            using var queue = new AsyncByteQueue();
            var buffer = new byte[256];

            for (var i = 1; i <= 5; i++)
            {
                queue.Write(new byte[i]);

                var read = await queue.ReadAsync(buffer, 1000, CancellationToken.None);
                Assert.That(read, Is.EqualTo(i), $"第 {i} 批应读出 {i} 字节");
            }
        }
    }
}
