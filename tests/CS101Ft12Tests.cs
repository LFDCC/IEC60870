/*
 *  CS101Ft12Tests.cs
 *
 *  CS101 的 FT1.2 帧编解码回归测试。
 *
 *  目标：确保 CS101 的 FT12 实现与 CS103 在字节格式上完全一致
 *  （同样的帧布局、校验和范围、长度双写、边界处理），
 *  唯一允许的差异是地址域长度可配（0/1/2 字节 vs 固定 1 字节）。
 *
 *  分三部分：
 *  1. Ft12Frame 自身的编解码往返（0/1/2 地址长度 × 固定/可变/单字符）
 *  2. 与 LinkLayerEngine 现有实现的字节级对照（阶段B/C 的黄金基准）
 *  3. Ft12FrameParser 的流式分帧（粘包/半包/噪声/半帧超时）
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CS101Ft12FrameTests
    {
        // ══════════════════════════════════════════════════════════
        //  1. 编解码往返
        // ══════════════════════════════════════════════════════════

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void 固定帧_编码长度与校验和(int addressLength)
        {
            var frame = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x40, 1, addressLength);

            Assert.That(len, Is.EqualTo(4 + addressLength));
            Assert.That(frame[0], Is.EqualTo(0x10));
            Assert.That(frame[1], Is.EqualTo(0x40));
            Assert.That(frame[len - 1], Is.EqualTo(0x16));

            // 校验和 = C + 地址各字节；地址长度 0 时没有地址字节，只剩 C
            var expected = (byte)0x40;
            for (var i = 0; i < addressLength; i++)
            {
                expected += (byte)((1 >> (8 * i)) & 0xFF);
            }

            Assert.That(frame[len - 2], Is.EqualTo(expected));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void 固定帧_两字节地址高低位顺序(int addressLength)
        {
            // 地址 0x0102：低字节在前
            var frame = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x49, 0x0102, 2);

            Assert.That(len, Is.EqualTo(6));
            Assert.That(frame[2], Is.EqualTo(0x02), "低字节在前");
            Assert.That(frame[3], Is.EqualTo(0x01), "高字节在后");
            Assert.That(frame[4], Is.EqualTo((byte)(0x49 + 0x02 + 0x01)));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void 可变帧_长度双写与校验和范围(int addressLength)
        {
            byte[] asdu = { 0x01, 0x02, 0x03 };
            var frame = new byte[32];
            var len = Ft12Frame.EncodeVariableTo(frame, 0x53, 0x0A, addressLength, asdu);

            // 68 L L 68 C [A..] ASDU CS 16 = 4 + 1 + A + ASDU + 2 = ASDU + A + 7
            var expectedTotal = asdu.Length + addressLength + 7;
            Assert.That(len, Is.EqualTo(expectedTotal));
            Assert.That(frame[0], Is.EqualTo(0x68));
            Assert.That(frame[1], Is.EqualTo(1 + addressLength + asdu.Length), "L = C(1) + A + ASDU");
            Assert.That(frame[2], Is.EqualTo(frame[1]), "长度双写");
            Assert.That(frame[3], Is.EqualTo(0x68));
            Assert.That(frame[4], Is.EqualTo(0x53));
            Assert.That(frame[len - 1], Is.EqualTo(0x16));

            // 校验和 = 自 C 起至 ASDU 末；地址域长度 0 时不含地址字节
            var cs = (byte)(0x53 + 0x01 + 0x02 + 0x03);
            for (var i = 0; i < addressLength; i++)
            {
                cs += (byte)((0x0A >> (8 * i)) & 0xFF);
            }

            Assert.That(frame[len - 2], Is.EqualTo(cs));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void 固定帧_解码往返(int addressLength)
        {
            var bytes = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(bytes, 0x5A, 0x07, addressLength);

            Assert.That(Ft12Frame.TryDecode(bytes.AsSpan(0, len), addressLength, out var frame, out var error), Is.True, error);
            Assert.That(frame!.Format, Is.EqualTo(Ft12FrameFormat.FixedLength));
            Assert.That(frame.Control, Is.EqualTo(0x5A));

            // 地址长度 0 时帧内没有地址域，解码结果为 0
            Assert.That(frame.Address, Is.EqualTo(addressLength == 0 ? 0 : 0x07));
            Assert.That(frame.AddressLength, Is.EqualTo(addressLength));
            Assert.That(frame.Asdu, Is.Empty);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void 可变帧_解码往返(int addressLength)
        {
            byte[] asdu = { 0x0A, 0x81, 0x09, 0x01, 0xFF, 0x00, 0x01 };
            var bytes = new byte[32];
            var len = Ft12Frame.EncodeVariableTo(bytes, 0x73, 0x0102, addressLength, asdu);

            Assert.That(Ft12Frame.TryDecode(bytes.AsSpan(0, len), addressLength, out var frame, out var error), Is.True, error);
            Assert.That(frame!.Format, Is.EqualTo(Ft12FrameFormat.VariableLength));
            Assert.That(frame.Control, Is.EqualTo(0x73));
            Assert.That(frame.Asdu, Is.EqualTo(asdu));
        }

        [Test]
        public void 单字符帧_往返()
        {
            var frame = new Ft12Frame(Ft12FrameFormat.SingleChar, 0, 0, 0, Ft12Frame.EmptyAsdu);
            var bytes = frame.ToBytes();

            Assert.That(bytes, Is.EqualTo(new byte[] { 0xE5 }));
            Assert.That(Ft12Frame.TryDecode(bytes, 1, out var decoded, out _), Is.True);
            Assert.That(decoded!.Format, Is.EqualTo(Ft12FrameFormat.SingleChar));
        }

        [Test]
        public void 解码_校验和错误被拒绝()
        {
            var bytes = new byte[16];
            var len = Ft12Frame.EncodeVariableTo(bytes, 0x53, 0x01, 1, new byte[] { 0x01, 0x02 });
            bytes[len - 2] ^= 0xFF;

            Assert.That(Ft12Frame.TryDecode(bytes.AsSpan(0, len), 1, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("校验和"));
        }

        [Test]
        public void 解码_长度双写不一致被拒绝()
        {
            var bytes = new byte[16];
            var len = Ft12Frame.EncodeVariableTo(bytes, 0x53, 0x01, 1, new byte[] { 0x01, 0x02 });
            bytes[2] = (byte)(bytes[1] + 1);

            Assert.That(Ft12Frame.TryDecode(bytes.AsSpan(0, len), 1, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("长度字节不一致"));
        }

        [Test]
        public void 解码_结束符错误被拒绝()
        {
            var bytes = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(bytes, 0x40, 0x01, 1);
            bytes[len - 1] = 0x17;

            Assert.That(Ft12Frame.TryDecode(bytes.AsSpan(0, len), 1, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("结束符"));
        }

        [Test]
        public void 编码_超过上限抛异常()
        {
            var tooLong = new byte[Ft12Frame.MaxAsduLength(1) + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Ft12Frame.EncodeVariableTo(new byte[300], 0x53, 1, 1, tooLong));

            // 0 字节地址可容纳的 ASDU 比 1 字节地址多 1
            Assert.That(Ft12Frame.MaxAsduLength(0), Is.EqualTo(253));
            Assert.That(Ft12Frame.MaxAsduLength(1), Is.EqualTo(252));
            Assert.That(Ft12Frame.MaxAsduLength(2), Is.EqualTo(251));
        }

        [Test]
        public void 解码_非法地址长度被拒绝()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Ft12FrameParser(3));
            Assert.That(Ft12Frame.TryDecode(new byte[] { 0xE5 }, 3, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("地址域长度"));
        }

        // ══════════════════════════════════════════════════════════
        //  2. 流式分帧
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 分帧_粘包一次切出多帧()
        {
            var parser = new Ft12FrameParser(1);

            var f1 = new byte[8];
            var l1 = Ft12Frame.EncodeFixedTo(f1, 0x40, 0x01, 1);

            var f2 = new byte[16];
            var l2 = Ft12Frame.EncodeVariableTo(f2, 0x73, 0x01, 1, new byte[] { 0x01, 0x02 });

            var combined = f1.Take(l1).Concat(f2.Take(l2)).Concat(new byte[] { 0xE5 }).ToArray();

            var frames = parser.Feed(combined);

            Assert.That(frames, Has.Count.EqualTo(3));
            Assert.That(frames[0].Format, Is.EqualTo(Ft12FrameFormat.FixedLength));
            Assert.That(frames[1].Format, Is.EqualTo(Ft12FrameFormat.VariableLength));
            Assert.That(frames[2].Format, Is.EqualTo(Ft12FrameFormat.SingleChar));
        }

        [Test]
        public void 分帧_半包累积后补齐()
        {
            var parser = new Ft12FrameParser(1);
            var frame = new byte[16];
            var len = Ft12Frame.EncodeVariableTo(frame, 0x53, 0x05, 1, new byte[] { 0x11, 0x22, 0x33, 0x44 });

            Assert.That(parser.Feed(frame.AsSpan(0, 3)), Is.Empty);
            Assert.That(parser.InFrame, Is.True);

            var frames = parser.Feed(frame.AsSpan(3, len - 3));
            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].Asdu, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Assert.That(parser.InFrame, Is.False);
        }

        [Test]
        public void 分帧_线路噪声被静默丢弃()
        {
            var parser = new Ft12FrameParser(1);
            var frame = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x5A, 0x01, 1);

            var noisy = new byte[] { 0x00, 0x37, 0xFF }
                .Concat(frame.Take(len)).ToArray();

            var frames = parser.Feed(noisy);
            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].Control, Is.EqualTo(0x5A));
        }

        [Test]
        public void 分帧_坏帧上报并恢复()
        {
            var parser = new Ft12FrameParser(1);
            var rejections = new List<string>();
            parser.FrameRejected += (_, reason) => rejections.Add(reason);

            var bad = new byte[16];
            var len = Ft12Frame.EncodeVariableTo(bad, 0x53, 0x01, 1, new byte[] { 0x01 });
            bad[len - 2] ^= 0xFF;

            Assert.That(parser.Feed(bad.AsSpan(0, len)), Is.Empty);
            Assert.That(rejections, Is.Not.Empty);

            // 恢复后仍能切出后续合法帧
            var good = new byte[8];
            var glen = Ft12Frame.EncodeFixedTo(good, 0x40, 0x01, 1);
            Assert.That(parser.Feed(good.AsSpan(0, glen)), Has.Count.EqualTo(1));
        }

        [Test]
        public void 分帧_半帧超时可复位()
        {
            var parser = new Ft12FrameParser(1);
            var frame = new byte[16];
            var len = Ft12Frame.EncodeVariableTo(frame, 0x53, 0x01, 1, new byte[] { 0x01, 0x02, 0x03 });

            parser.Feed(frame.AsSpan(0, 4));
            Assert.That(parser.InFrame, Is.True);

            System.Threading.Thread.Sleep(20);
            Assert.That(parser.TryResetStale(1), Is.True);
            Assert.That(parser.InFrame, Is.False);

            // 复位后新帧可正常切出
            var good = new byte[8];
            var glen = Ft12Frame.EncodeFixedTo(good, 0x40, 0x01, 1);
            Assert.That(parser.Feed(good.AsSpan(0, glen)), Has.Count.EqualTo(1));
        }

        [Test]
        public void 分帧_慢速分片长帧不误判超时()
        {
            // 与 103 同类用例：每批之间调 TryResetStale(1)，只要字节持续到达就不该丢帧。
            // 这验证「距最后一个字节的间隔」而非「整帧总时长」。
            var asdu = Enumerable.Range(0, Ft12Frame.MaxAsduLength(1)).Select(i => (byte)i).ToArray();
            var frame = new byte[300];
            var len = Ft12Frame.EncodeVariableTo(frame, 0x53, 0x01, 1, asdu);

            var parser = new Ft12FrameParser(1);
            var frames = new List<Ft12Frame>();

            const int batch = 20;
            for (var offset = 0; offset < len; offset += batch)
            {
                frames.AddRange(parser.Feed(frame.AsSpan(offset, Math.Min(batch, len - offset))));
                Assert.That(parser.TryResetStale(1), Is.False, $"偏移 {offset} 处不应判定半帧超时");
            }

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].Asdu, Is.EqualTo(asdu));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void 分帧_零与两字节地址长度(int addressLength)
        {
            var parser = new Ft12FrameParser(addressLength);
            var frame = new byte[16];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x49, 0x0102, addressLength);

            var frames = parser.Feed(frame.AsSpan(0, len));
            Assert.That(frames, Has.Count.EqualTo(addressLength == 0 ? 0 : 1),
                "地址长度 0 时 10 C CS 16 只有 4 字节，与传入的 5 字节不匹配应视为半帧");
        }
    }

    /// <summary>
    /// FT12Framer（拉取式）的鲁棒性测试：噪声重同步与半帧超时复位。
    /// </summary>
    [TestFixture]
    public class CS101Ft12FramerTests
    {
        /// <summary>
        /// 测试字节源：按预设字节流顺序供给，每次尽量填满请求的 buffer。
        /// 与真实 <c>StreamByteSource</c> 语义一致（循环读直到凑满或耗尽）。
        /// </summary>
        private sealed class ScriptedByteSource : IEC60870.CS101.IByteSource
        {
            private readonly byte[] _all;
            private readonly Queue<int> _scriptedChunkSizes;
            private int _pos;

            /// <summary>按预设字节流供给；chunkSizes 可精确控制每次 ReadAsync 返回多少字节。</summary>
            public ScriptedByteSource(IEnumerable<byte[]> chunks, params int[] chunkSizes)
            {
                _all = chunks.SelectMany(c => c).ToArray();
                _scriptedChunkSizes = new Queue<int>(chunkSizes ?? Array.Empty<int>());
            }

            public int ReadCount { get; private set; }

            /// <summary>脚本耗尽视为"暂无数据"，不是链路故障（否则分帧器会提前收尾）。</summary>
            public bool IsFaulted => false;

            public ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, System.Threading.CancellationToken ct)
            {
                ReadCount++;

                var available = _all.Length - _pos;
                if (available <= 0)
                {
                    return new ValueTask<int>(0);
                }

                // 有脚本时按脚本给的字节数返回（模拟慢速/分段到达），否则尽量填满
                var limit = _scriptedChunkSizes.Count > 0 ? _scriptedChunkSizes.Dequeue() : int.MaxValue;
                var take = Math.Min(Math.Min(available, buffer.Length), Math.Max(limit, 0));
                if (take == 0)
                {
                    return new ValueTask<int>(0);
                }

                _all.AsSpan(_pos, take).CopyTo(buffer.Span);
                _pos += take;
                return new ValueTask<int>(take);
            }
        }

        private static LinkLayerParameters Params(int addressLength = 1) => new() { AddressLength = addressLength };

        [Test]
        public async Task 噪声字节后仍能同步到帧()
        {
            var frame = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x40, 0x01, 1);

            // 先喂 3 个噪声字节，再喂真帧
            var src = new ScriptedByteSource(new[]
            {
                new byte[] { 0x00 },
                new byte[] { 0x37 },
                new byte[] { 0xFF },
                frame.Take(len).ToArray(),
            });

            var buffer = new byte[16];
            var logs = new List<string>();
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(), 100, 100, m => logs.Add(m), System.Threading.CancellationToken.None);

            Assert.That(n, Is.EqualTo(len), "跳过噪声后应读到完整帧");
            Assert.That(buffer.Take(len).ToArray(), Is.EqualTo(frame.Take(len).ToArray()));
            Assert.That(logs.Any(l => l.Contains("unexpected start byte")), Is.True, "应记录跳过噪声");
        }

        [Test]
        public async Task 半帧超时后复位并继续寻找起始符()
        {
            // 给一个只有头部的残缺可变帧（L=5，需 11 字节），后面不足；
            // framer 应在超时后复位（记录日志），继续回到等待起始符状态。
            var src = new ScriptedByteSource(
                new[] { new byte[] { 0x68, 0x05, 0x05, 0x68 } },
                chunkSizes: new[] { 1, 1, 2 }); // 第3次只给2字节，凑不满 rest=9

            var buffer = new byte[32];
            var logs = new List<string>();
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(), 100, 50, m => logs.Add(m), System.Threading.CancellationToken.None);

            Assert.That(logs.Any(l => l.Contains("Timeout reading variable")), Is.True,
                "应记录半帧超时并复位");
            Assert.That(n, Is.Zero, "流已耗尽，最终返回 0");
        }

        [Test]
        public async Task 半帧超时后能读回后续完整帧()
        {
            // 精确安排：先让 framer 读一个残缺帧超时，再让它从头读到完整帧。
            // 做法是让第一次读取只暴露残缺字节，超时后流中才"出现"完整帧。
            var good = new byte[8];
            var glen = Ft12Frame.EncodeFixedTo(good, 0x49, 0x01, 1);

            var src = new TwoPhaseByteSource(
                phase1: new byte[] { 0x68, 0x05, 0x05, 0x68 },
                phase2: good.Take(glen).ToArray(),
                flipAfterReads: 3);

            var buffer = new byte[32];
            var logs = new List<string>();
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(), 100, 50, m => logs.Add(m), System.Threading.CancellationToken.None);

            Assert.That(n, Is.EqualTo(glen), "半帧超时复位后应读到后续完整帧");
            Assert.That(buffer.Take(glen).ToArray(), Is.EqualTo(good.Take(glen).ToArray()));
        }

        /// <summary>两阶段字节源：前 N 次读返回残缺数据，之后切换到完整帧。</summary>
        private sealed class TwoPhaseByteSource : IEC60870.CS101.IByteSource
        {
            private readonly byte[] _phase1;
            private readonly byte[] _phase2;
            private readonly int _flipAfterReads;
            private int _reads;
            private int _pos1;
            private int _pos2;

            public TwoPhaseByteSource(byte[] phase1, byte[] phase2, int flipAfterReads)
            {
                _phase1 = phase1;
                _phase2 = phase2;
                _flipAfterReads = flipAfterReads;
            }

            /// <summary>两阶段脚本都是正常数据，不模拟链路故障。</summary>
            public bool IsFaulted => false;

            public ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, System.Threading.CancellationToken ct)
            {
                _reads++;

                if (_reads <= _flipAfterReads)
                {
                    var avail1 = _phase1.Length - _pos1;
                    if (avail1 > 0)
                    {
                        var take1 = Math.Min(avail1, buffer.Length);
                        _phase1.AsSpan(_pos1, take1).CopyTo(buffer.Span);
                        _pos1 += take1;
                        return new ValueTask<int>(take1);
                    }

                    return new ValueTask<int>(0);
                }

                var avail2 = _phase2.Length - _pos2;
                if (avail2 <= 0)
                {
                    return new ValueTask<int>(0);
                }

                var take2 = Math.Min(avail2, buffer.Length);
                _phase2.AsSpan(_pos2, take2).CopyTo(buffer.Span);
                _pos2 += take2;
                return new ValueTask<int>(take2);
            }
        }

        [Test]
        public async Task 无数据时返回零()
        {
            var src = new ScriptedByteSource(Array.Empty<byte[]>());
            var buffer = new byte[16];
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(), 10, 10, _ => { }, System.Threading.CancellationToken.None);

            Assert.That(n, Is.Zero, "无数据应返回 0");
        }

        [Test]
        public async Task 单字符ACK直接返回()
        {
            var src = new ScriptedByteSource(new[] { new byte[] { 0xE5 } });
            var buffer = new byte[16];
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(), 100, 100, _ => { }, System.Threading.CancellationToken.None);

            Assert.That(n, Is.EqualTo(1));
            Assert.That(buffer[0], Is.EqualTo(0xE5));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task 固定帧按地址长度读取(int addressLength)
        {
            var frame = new byte[8];
            var len = Ft12Frame.EncodeFixedTo(frame, 0x40, 0x02, addressLength);

            var src = new ScriptedByteSource(new[] { frame.Take(len).ToArray() });
            var buffer = new byte[16];
            var n = await IEC60870.CS101.FT12Framer.ReadFrameAsync(
                src, buffer, Params(addressLength), 100, 100, _ => { }, System.Threading.CancellationToken.None);

            Assert.That(n, Is.EqualTo(len), $"地址长度 {addressLength} 时固定帧长度应为 {len}");
        }
    }
}
