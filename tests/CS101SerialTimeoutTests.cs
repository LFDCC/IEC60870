/*
 *  CS101SerialTimeoutTests.cs
 *
 *  CS101 串口读取的「字符间超时」回归测试（不依赖串口硬件）。
 *
 *  被修复的缺陷：
 *      StreamByteSource.ReadAsync 原先用「一个」CancelAfter(timeoutMs) 包住整个
 *      while (total < buffer.Length) 循环，相当于把字符超时当成了整帧总窗口。
 *      FT1.2 变长帧的帧体是一次性读完整的（L 最大 255），默认字符超时 50ms，
 *      9600 波特下每字节约 1.04ms —— 所有 >48 字节的合法帧都会被判成半帧丢弃。
 *      101 现场的总召唤响应、多 IOA 帧几乎都超过这个尺寸。
 *
 *  修复后的语义：
 *      超时是「相邻两个字节之间的最大间隔」（character inter-byte timeout）。
 *      首字节仍受完整 messageTimeout 约束，之后每读到字节就重新计时，
 *      只要字节之间不断流，长帧就能读满。
 *
 *  本文件用自实现的慢速 Stream 模拟串口（每次 ReadAsync 只给 1 字节并按线路
 *  速率延迟），因此可在 CI 上稳定复现「长帧被误杀」与「真半帧超时」两条路径。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.Tests
{
    /// <summary>
    /// 串口字符间超时（character inter-byte timeout）回归。
    /// </summary>
    [TestFixture]
    public class CS101SerialTimeoutTests
    {
        private static LinkLayerParameters Params() => new() { AddressLength = 1 };

        /// <summary>
        /// 构造一个指定总长度的合法 FT1.2 变长帧（地址域 1 字节）。
        /// ASDU 填充 0x20..0x5F，避开 0x68/0x10/0xE5，防止测试里被当成帧起始符。
        /// </summary>
        private static byte[] BuildVariableFrame(int totalLength)
        {
            const int addressLength = 1;
            var asduLength = totalLength - addressLength - 7;
            Assert.That(asduLength, Is.GreaterThan(0));

            var asdu = new byte[asduLength];
            for (var i = 0; i < asdu.Length; i++)
            {
                asdu[i] = (byte)(0x20 + (i % 0x40));
            }

            var frame = new byte[totalLength];
            var len = Ft12Frame.EncodeVariableTo(frame, 0x40, 0x01, addressLength, asdu);

            Assert.That(len, Is.EqualTo(totalLength), "测试帧编码长度与期望不一致");
            return frame;
        }

        // ══════════════════════════════════════════════════════════
        //  1. 长帧不被字符超时误杀
        // ══════════════════════════════════════════════════════════

        [Test]
        [Timeout(30000)]
        public async Task 长帧_字节间隔小于字符超时时必须完整读出()
        {
            var frame = BuildVariableFrame(100);

            const int characterTimeoutMs = 50;
            const int perByteDelayMs = 5;

            using var stream = new SlowSerialStream(frame, perByteDelayMs);
            var source = new StreamByteSource(stream);
            var buffer = new byte[300];
            var logs = new List<string>();

            var sw = Stopwatch.StartNew();
            var n = await FT12Framer.ReadFrameAsync(
                source, buffer, Params(), 1000, characterTimeoutMs, logs.Add, CancellationToken.None);
            sw.Stop();

            Assert.That(n, Is.EqualTo(frame.Length),
                $"100 字节变长帧必须被完整读出（字符间超时不应成为整帧窗口）。实际 {n} 字节，日志：{string.Join(" | ", logs)}");
            Assert.That(buffer.Take(frame.Length).ToArray(), Is.EqualTo(frame), "读出的字节必须与写入的帧完全一致");
            Assert.That(logs.Any(l => l.Contains("Timeout reading variable")), Is.False,
                "字节之间没有超过字符超时的空隙，不应出现帧体超时");

            // 100 字节逐字节按 5ms 间隔到达，总耗时必然远超单次字符超时窗口
            // —— 这正是修复前必然失败的场景（旧实现 ~50ms 后就把半帧丢掉）。
            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300),
                $"本用例依赖慢速逐字节到达来复现缺陷，实际耗时 {sw.ElapsedMilliseconds}ms 过短");
        }

        // ══════════════════════════════════════════════════════════
        //  2. 真半帧仍会超时（不能因为改动而挂死）
        // ══════════════════════════════════════════════════════════

        [Test]
        [Timeout(30000)]
        public async Task 真半帧_在报文超时内返回零且不挂死()
        {
            var frame = BuildVariableFrame(100);
            var partial = frame.Take(10).ToArray(); // 只到帧体前 4 字节，后续线路静默

            using var stream = new SlowSerialStream(partial, perByteDelayMs: 1);
            var source = new StreamByteSource(stream);
            var buffer = new byte[300];
            var logs = new List<string>();

            const int messageTimeoutMs = 60;
            const int characterTimeoutMs = 50;

            var sw = Stopwatch.StartNew();
            var n = await FT12Framer.ReadFrameAsync(
                source, buffer, Params(), messageTimeoutMs, characterTimeoutMs, logs.Add, CancellationToken.None);
            sw.Stop();

            Assert.That(n, Is.Zero, "半帧必须超时返回 0，不能挂死或返回残缺帧");
            Assert.That(logs.Any(l => l.Contains("Timeout reading variable")), Is.True,
                "帧体读取中途断流应记录半帧超时");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000),
                $"超时路径应在 messageTimeout + characterTimeout 量级内结束，实际 {sw.ElapsedMilliseconds}ms");
        }

        // ══════════════════════════════════════════════════════════
        //  3. 单字节间隔超过字符超时 → 仍判超时（不能拼成错帧）
        // ══════════════════════════════════════════════════════════

        [Test]
        [Timeout(30000)]
        public async Task 单字节间隔超过字符超时_仍判为不完整()
        {
            var frame = BuildVariableFrame(100);

            // 前 5 字节按线路速率到达，第 6 字节（索引 5）停顿 150ms：
            // 间隔远超 characterTimeout=50ms，属于真实断流，不能被"重新计时"放过。
            using var stream = new SlowSerialStream(
                frame, perByteDelayMs: 1, pauseAtByteIndex: 5, pauseMs: 150);
            var source = new StreamByteSource(stream);
            var buffer = new byte[300];
            var logs = new List<string>();

            var sw = Stopwatch.StartNew();
            var n = await FT12Framer.ReadFrameAsync(
                source, buffer, Params(), messageTimeout: 30, characterTimeout: 50, logs.Add, CancellationToken.None);
            sw.Stop();

            Assert.That(n, Is.Zero,
                "字节间出现 >characterTimeout 的空隙时必须放弃该半帧（不能把后续字节当成同一帧拼满）");
            Assert.That(logs.Any(l => l.Contains("Timeout reading variable")), Is.True,
                "应记录帧体读取超时");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000),
                $"断流应在字符超时 + 报文超时量级内结束，实际 {sw.ElapsedMilliseconds}ms");
        }

        // ══════════════════════════════════════════════════════════
        //  4. StreamByteSource 的对外契约（已读字节数 / 故障标志）
        // ══════════════════════════════════════════════════════════

        [Test]
        [Timeout(30000)]
        public async Task StreamByteSource_超时返回已读字节数且不标记故障()
        {
            using var stream = new SlowSerialStream(new byte[] { 0x68, 0x03, 0x03 }, perByteDelayMs: 1);
            var source = new StreamByteSource(stream);
            var buffer = new byte[10];

            var n = await source.ReadAsync(buffer, 40, CancellationToken.None);

            Assert.That(n, Is.EqualTo(3), "超时应返回已读到的字节数（既有契约不变），而不是丢掉或返回异常");
            Assert.That(source.IsFaulted, Is.False, "字符间超时属于正常的「本轮无数据」，不是链路故障");
        }

        [Test]
        [Timeout(30000)]
        public async Task StreamByteSource_总时长超过超时但字节间隔未超时_仍读满()
        {
            // 3 字节、每字节间隔 40ms：总耗时 >= 120ms，超过 100ms 的单次窗口；
            // 但每个间隔都 < 100ms。旧实现（整次读取一个窗口）会在第 3 个字节到达前放弃。
            using var stream = new SlowSerialStream(new byte[] { 0x11, 0x22, 0x33 }, perByteDelayMs: 40);
            var source = new StreamByteSource(stream);
            var buffer = new byte[3];

            var sw = Stopwatch.StartNew();
            var n = await source.ReadAsync(buffer, 100, CancellationToken.None);
            sw.Stop();

            Assert.That(n, Is.EqualTo(3), "只要相邻字节间隔未超时，读取就必须继续到填满缓冲");
            Assert.That(buffer, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33 }));
            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(120),
                "用例依赖总耗时超过单次超时窗口来区分新旧语义");
        }

        // ══════════════════════════════════════════════════════════
        //  5. 从站可配置超时（转发到传输层，未启动时也不得抛异常）
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 从站SetTimeouts_未启动时可调用()
        {
            using var stream = new SlowSerialStream(Array.Empty<byte>(), perByteDelayMs: 0);
            using var server = new Iec101Server(stream);

            Assert.DoesNotThrow(() => server.SetTimeouts(1000, 200),
                "SetTimeouts 应在 StartAsync 之前的构造期即可调用");
        }

        /// <summary>
        /// 模拟串口线路：每次 <see cref="ReadAsync"/> 最多返回 1 字节，返回前等待
        /// <c>perByteDelayMs</c>（模拟波特率）。数据耗尽后<b>不返回 0（EOF）</b>，
        /// 而是挂起直到取消 —— 真实串口在线路静默时就是这种语义，只有这样
        /// 超时路径才会被真正走到（返回 0 会被下层当成链路已故障）。
        /// </summary>
        private sealed class SlowSerialStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _perByteDelayMs;
            private readonly int _pauseAtByteIndex;
            private readonly int _pauseMs;
            private int _pos;

            public SlowSerialStream(byte[] data, int perByteDelayMs, int pauseAtByteIndex = -1, int pauseMs = 0)
            {
                _data = data ?? Array.Empty<byte>();
                _perByteDelayMs = perByteDelayMs;
                _pauseAtByteIndex = pauseAtByteIndex;
                _pauseMs = pauseMs;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;

            public override long Position
            {
                get => _pos;
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                var delay = _perByteDelayMs;
                if (_pauseAtByteIndex >= 0 && _pos == _pauseAtByteIndex)
                {
                    delay += _pauseMs;
                }

                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                if (_pos >= _data.Length)
                {
                    // 线路静默（不是 EOF）：挂起，由调用方的超时令牌结束本次读。
                    await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                buffer.Span[0] = _data[_pos++];
                return 1;
            }

            /// <summary>同步读不用于被测路径（StreamByteSource 走 Memory 重载），保留最小实现。</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count <= 0 || _pos >= _data.Length)
                {
                    return 0;
                }

                buffer[offset] = _data[_pos++];
                return 1;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
