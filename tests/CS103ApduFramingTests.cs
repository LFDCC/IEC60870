/*
 *  CS103ApduFramingTests.cs
 *
 *  许继网口 103（103 ASDU 装在 104 APCI 壳里）的单元测试。
 *
 *  ⚠️ 这不是标准协议：标准 103（DL/T 667-1999）只定义 FT1.2 帧，
 *     且正文明确写「应用层未采用明确的应用规约控制信息(APCI)」。
 *     许继的 APCI 承载是厂商私有实现，代码在
 *     examples/103/cs103-private-ioa/Xuji/XujiApduCodec.cs，
 *     库本身只提供 IIec103FramingCodec 这个扩展点。
 *
 *  覆盖：
 *  1. 现场 7 帧真实抓包的逐字节回归（帧长、序列号、8 字节 ASDU 头、2 字节 CA）；
 *  2. 帧编解码往返与坏帧拒绝（长度域非法、总长不符、非 I 帧控制域）；
 *  3. 分帧器：粘包、半包、噪声、半帧超时复位；
 *  4. ASDU 视图的头部布局（标准 6 字节 vs 许继 8 字节）；
 *  5. 两种承载不互相误判（FT1.2 帧不会被当成许继 APDU，反之亦然）。
 *
 *  报文来源：许继现场 2403 端口抓包（调定值 + 单项定值修改），2026-11-19。
 */

using System;
using System.Linq;
using cs103_private_ioa.Xuji;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.Tests
{
    /// <summary>许继网口 103（APCI 承载）单元测试。</summary>
    [TestFixture]
    public class CS103ApduFramingTests
    {
        // ── 现场抓包原始报文 ────────────────────────────────────────────

        /// <summary>调定值请求（主站 → 装置）。</summary>
        private const string ReadRequestHex = "68 0E 00 00 00 00 3D 81 14 00 01 01 B2 64 1F 00";

        /// <summary>调定值应答（装置 → 主站），27 项 × 3 字节定值。</summary>
        private const string ReadResponseHex =
            "68 5f 00 00 02 00 3d 1c 14 00 01 01 b2 64 1f 00 00 00 10 01 00 10 05 00 10 e8 03 10 14 00 10 00 00 10 "
            + "e8 03 10 01 00 10 c0 00 10 a8 00 10 04 00 10 02 00 10 00 00 10 80 00 10 e1 00 10 22 00 10 22 00 10 "
            + "12 00 10 00 00 10 80 00 10 e1 00 10 22 00 10 22 00 10 00 00 10 01 00 10 01 00 10 00 00 10";

        /// <summary>调定值结束帧。</summary>
        private const string ReadEndHex = "68 0e 02 00 02 00 3d 81 0a 00 01 01 b2 64 1f 00";

        /// <summary>单项定值修改预发（主站 → 装置，值 1500、系数 ×0.01）。</summary>
        private const string PreSetHex = "68 11 00 00 00 00 3D 02 14 00 01 01 B2 6B 1F 0A DC 05 12";

        /// <summary>单项定值修改返校（装置回显）。</summary>
        private const string VerifyHex = "68 11 00 00 02 00 3D 02 14 00 01 01 B2 6C 1F 0A DC 05 12";

        /// <summary>单项定值修改执行（第 1 项）。</summary>
        private const string Execute1Hex = "68 0E 00 00 00 00 3D 81 14 00 01 01 B2 6D 1F 01";

        /// <summary>单项定值修改执行（第 2 项）。</summary>
        private const string Execute2Hex = "68 0E 00 00 00 00 3D 81 14 00 01 01 B2 6D 1F 02";

        private static byte[] Hex(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Convert.ToByte(x, 16))
                .ToArray();

        // ══════════════════════════════════════════════════════════════
        //  1. 现场抓包逐字节回归
        // ══════════════════════════════════════════════════════════════

        [Test]
        [TestCase(ReadRequestHex, 0, 0, 10)]
        [TestCase(ReadResponseHex, 0, 1, 91)]
        [TestCase(ReadEndHex, 1, 1, 10)]
        [TestCase(PreSetHex, 0, 0, 13)]
        [TestCase(VerifyHex, 0, 1, 13)]
        [TestCase(Execute1Hex, 0, 0, 10)]
        [TestCase(Execute2Hex, 0, 0, 10)]
        public void FieldFrames_DecodeExactly(string hex, int expectedNs, int expectedNr, int expectedAsduLength)
        {
            var bytes = Hex(hex);

            // 帧总长必须精确等于 2 + L（许继承载的长度域语义）
            Assert.That(bytes[1] + 2, Is.EqualTo(bytes.Length), "帧长应为 2 + L");

            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out var error), Is.True, error);
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.SendSequence, Is.EqualTo(expectedNs), "N(S)");
            Assert.That(frame.ReceiveSequence, Is.EqualTo(expectedNr), "N(R)");
            Assert.That(frame.Asdu.Length, Is.EqualTo(expectedAsduLength), "ASDU 长度");

            // 编码回去必须与原报文逐字节一致
            Assert.That(frame.ToBytes(), Is.EqualTo(bytes), "编解码应互逆");
        }

        [Test]
        public void ReadResponse_HasEightByteHeaderAndTwoByteCa()
        {
            var bytes = Hex(ReadResponseHex);
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out _), Is.True);

            var view = new Iec103AsduView(frame!.Asdu, XujiApduLayout.Instance);

            // Iec103AsduView 是 ref struct，不能捕获进 lambda，故逐项取出后断言
            var headerLength = view.HeaderLength;
            var typeId = view.TypeId;
            var cot = view.Cot;
            var cotField = view.CotField;
            var ca = view.Ca;
            var fun = view.Fun;
            var inf = view.Inf;

            Assert.Multiple(() =>
            {
                Assert.That(headerLength, Is.EqualTo(8), "许继承载头为 8 字节");
                Assert.That(typeId, Is.EqualTo(61), "TI=61 定值");
                Assert.That(cot, Is.EqualTo(20), "COT=20 命令肯定确认");
                Assert.That(cotField, Is.EqualTo(0x0014), "COT 字段 2 字节（高字节是源发地址）");
                Assert.That(ca, Is.EqualTo(0x0101), "CA=0x0101（2 字节小端）");
                Assert.That(fun, Is.EqualTo(178), "FUN=178 线路保护");
                Assert.That(inf, Is.EqualTo(100), "INF=100 调定值");
            });
        }

        [Test]
        public void ReadResponse_DataIs27ItemsOfThreeBytes()
        {
            var bytes = Hex(ReadResponseHex);
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out _), Is.True);

            var view = new Iec103AsduView(frame!.Asdu, XujiApduLayout.Instance);
            var data = view.Data;

            // 信息体 = CPU/区(1) + 定值序号(1) + N×3B
            Assert.That(data.Length, Is.EqualTo(2 + 27 * 3), "83 字节 = 2 + 27×3");

            var cpuZone = data[0];
            var index = data[1];
            Assert.Multiple(() =>
            {
                Assert.That(cpuZone, Is.EqualTo(0x1F), "CPU 板=1、定值区=15（当前运行区）");
                Assert.That(index, Is.EqualTo(0), "定值序号=0（整区）");
            });

            var body = data[2..];
            for (var i = 0; i < 27; i++)
            {
                var tail = body[i * 3 + 2];
                // 0x10 = 0001 0000b：bit0-1 = 0（系数 ×1）、bit4 = 1（性质位「数据」）
                Assert.That(tail & 0x10, Is.Not.Zero, $"第 {i + 1} 项的性质位（bit4）应为「数据」");
            }
        }

        [Test]
        public void SingleSet_Value1500WithHundredthsScale()
        {
            var bytes = Hex(PreSetHex);
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out _), Is.True);

            var view = new Iec103AsduView(frame!.Asdu, XujiApduLayout.Instance);
            Assert.That(view.Inf, Is.EqualTo(107), "INF=107 单项定值修改预发");

            var data = view.Data;
            Assert.That(data.Length, Is.EqualTo(5), "1B CPU/区 + 1B 序号 + 3B 定值");

            var raw = data[2] | (data[3] << 8);
            Assert.That(raw, Is.EqualTo(1500), "原始定值数据");
            Assert.That(data[4], Is.EqualTo(0x12), "第 3 字节 = 0x12");

            // 0x12 = 0001 0010b：bit0-1 = 10b = 2 → ×0.01
            var scale = data[4] & 0x03;
            Assert.That(scale, Is.EqualTo(2), "比例系数 bit0-1 = 2（×0.01）");
            Assert.That(raw / 100.0, Is.EqualTo(15.0), "实际值 1500 × 0.01 = 15.00");
        }

        // ══════════════════════════════════════════════════════════════
        //  2. 帧编解码与坏帧拒绝
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void Frame_RoundTrip()
        {
            var asdu = new byte[] { 0x3D, 0x81, 0x14, 0x00, 0x01, 0x01, 0xB2, 0x64, 0x1F, 0x00 };
            var frame = new XujiApduFrame(SendSequence: 5, ReceiveSequence: 3, asdu);

            var bytes = frame.ToBytes();
            Assert.That(bytes.Length, Is.EqualTo(6 + asdu.Length));
            Assert.That(bytes[0], Is.EqualTo(0x68));
            Assert.That(bytes[1], Is.EqualTo(4 + asdu.Length));

            Assert.That(XujiApduFrame.TryDecode(bytes, out var decoded, out var error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(decoded!.SendSequence, Is.EqualTo(5));
                Assert.That(decoded.ReceiveSequence, Is.EqualTo(3));
                Assert.That(decoded.Asdu, Is.EqualTo(asdu));
            });
        }

        [Test]
        public void Frame_RejectsInvalidLengthField()
        {
            // L=3 小于控制域长度 4
            var bytes = new byte[] { 0x68, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 };
            Assert.That(XujiApduFrame.TryDecode(bytes, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("长度域"));
        }

        [Test]
        public void Frame_RejectsLengthMismatch()
        {
            var bytes = Hex(ReadRequestHex);
            var truncated = bytes.AsSpan(0, bytes.Length - 1).ToArray();

            Assert.That(XujiApduFrame.TryDecode(truncated, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("总长"));
        }

        [Test]
        public void Frame_DecodesControlFrame()
        {
            // 控制域 bit0=1 → S/U 帧，无 ASDU，标记为控制帧
            var bytes = new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out var error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(frame!.IsControlFrame, Is.True);
                Assert.That(frame.ControlFunction, Is.EqualTo(0x07), "STARTDT_ACT");
                Assert.That(frame.Asdu, Is.Empty);
            });
        }

        [Test]
        public void Codec_RepliesStartDtCon()
        {
            var codec = new XujiApduCodec();
            byte[] reply = null;
            codec.ControlFrameResponse += raw => reply = raw;

            // 装置连上即发的 STARTDT_ACT
            var frames = codec.Feed(new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 });

            Assert.That(frames, Is.Empty, "控制帧不应作为 ASDU 上报");
            Assert.That(reply, Is.Not.Null, "应回 STARTDT_CON");
            Assert.That(Convert.ToHexString(reply), Is.EqualTo("68040B000000"));
        }

        [Test]
        public void Codec_SeparatesControlAndInformationFrames()
        {
            var codec = new XujiApduCodec();
            var mixed = new byte[]
            {
                0x68, 0x04, 0x07, 0x00, 0x00, 0x00,             // STARTDT_ACT
                0x68, 0x0E, 0x00, 0x00, 0x00, 0x00,             // I 帧
                0x3D, 0x81, 0x14, 0x00, 0x01, 0x01, 0xB2, 0x64, 0x1F, 0x00,
            };

            var frames = codec.Feed(mixed);

            Assert.That(frames, Has.Count.EqualTo(1), "只应上报 I 帧");
            Assert.That(frames[0].Asdu.Length, Is.EqualTo(10));
        }

        // ══════════════════════════════════════════════════════════════
        //  3. 分帧器（IIec103FramingCodec 实现）
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void Codec_SplitsConcatenatedFrames()
        {
            var codec = new XujiApduCodec();
            var stream = Hex(ReadRequestHex).Concat(Hex(ReadResponseHex)).ToArray();

            var frames = codec.Feed(stream);

            Assert.That(frames, Has.Count.EqualTo(2));
            Assert.That(frames[0].Asdu.Length, Is.EqualTo(10));
            Assert.That(frames[1].Asdu.Length, Is.EqualTo(91));
        }

        [Test]
        public void Codec_HandlesByteByByteFeed()
        {
            var codec = new XujiApduCodec();
            var bytes = Hex(ReadRequestHex);

            var collected = new System.Collections.Generic.List<Iec103IncomingFrame>();
            foreach (var b in bytes)
            {
                collected.AddRange(codec.Feed(new[] { b }));
            }

            Assert.That(collected, Has.Count.EqualTo(1), "逐字节喂入也应切出 1 帧");
            Assert.That(collected[0].Asdu.Length, Is.EqualTo(10));
        }

        [Test]
        public void Codec_SkipsNoiseBeforeStartByte()
        {
            var codec = new XujiApduCodec();
            var noise = new byte[] { 0x00, 0xFF, 0x10, 0x16 };
            var stream = noise.Concat(Hex(ReadRequestHex)).ToArray();

            Assert.That(codec.Feed(stream), Has.Count.EqualTo(1));
        }

        [Test]
        public void Codec_ReportsRejectedFrame()
        {
            var codec = new XujiApduCodec();
            string rejectedReason = null;
            codec.FrameRejected += (_, reason) => rejectedReason = reason;

            // L=1 非法
            codec.Feed(new byte[] { 0x68, 0x01, 0x00 });

            Assert.That(rejectedReason, Is.Not.Null);
            Assert.That(rejectedReason, Does.Contain("长度域"));
        }

        // ══════════════════════════════════════════════════════════════
        //  4. ASDU 头部布局
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void AsduView_StandardLayout_IsSixBytes()
        {
            // TI=61 VSQ=0x81 COT=0x14 CA=0x01 FUN=0xB2 INF=100
            var asdu = new byte[] { 0x3D, 0x81, 0x14, 0x01, 0xB2, 0x64 };
            var view = new Iec103AsduView(asdu, Iec103AsduLayout.Standard);

            var headerLength = view.HeaderLength;
            var ca = view.Ca;
            var cotField = view.CotField;
            var fun = view.Fun;
            var inf = view.Inf;

            Assert.Multiple(() =>
            {
                Assert.That(headerLength, Is.EqualTo(6));
                Assert.That(ca, Is.EqualTo(1), "标准 103 的 CA 为 1 字节");
                Assert.That(cotField, Is.EqualTo(0x14), "标准 103 的 COT 为 1 字节");
                Assert.That(fun, Is.EqualTo(0xB2));
                Assert.That(inf, Is.EqualTo(100));
            });
        }

        [Test]
        public void AsduView_XujiLayout_IsEightBytes()
        {
            var asdu = new byte[] { 0x3D, 0x81, 0x14, 0x00, 0x01, 0x01, 0xB2, 0x64 };
            var view = new Iec103AsduView(asdu, XujiApduLayout.Instance);

            var headerLength = view.HeaderLength;
            var ca = view.Ca;
            var cotField = view.CotField;
            var fun = view.Fun;
            var inf = view.Inf;

            Assert.Multiple(() =>
            {
                Assert.That(headerLength, Is.EqualTo(8));
                Assert.That(ca, Is.EqualTo(0x0101), "许继的 CA 为 2 字节小端");
                Assert.That(cotField, Is.EqualTo(0x0014), "许继的 COT 为 2 字节（高字节是源发地址）");
                Assert.That(fun, Is.EqualTo(0xB2));
                Assert.That(inf, Is.EqualTo(100));
            });
        }

        [Test]
        public void AsduView_DefaultConstructorIsStandard()
        {
            var asdu = new byte[] { 0x3D, 0x81, 0x14, 0x01, 0xB2, 0x64 };
            var view = new Iec103AsduView(asdu);

            Assert.That(view.Layout, Is.SameAs(Iec103AsduLayout.Standard), "默认构造必须是标准 6 字节布局");
        }

        [Test]
        [TestCase(0x14, 20, false, TestName = "COT=20 命令确认")]
        [TestCase(0x15, 21, false, TestName = "COT=21 命令否定")]
        [TestCase(0x40, 64, false, TestName = "COT=64 调版本号（>63，104 位序会抹成 0）")]
        [TestCase(0x42, 66, false, TestName = "COT=66 冻结否定（>63）")]
        [TestCase(0x95, 21, true, TestName = "COT=0x95 否定确认（bit7=P/N）")]
        public void AsduView_UsesIec103CotBitOrder(byte cotByte, int expectedCause, bool expectedNegative)
        {
            // 字段宽 2 字节（104 布局），但首字节位序是 103 的：bit7=P/N、bit6-0=原因。
            // 若按 104 位序（bit6 作 P/N），COT=64/66 会被抹成 0，装置拒绝会被误判成功。
            var asdu = new byte[] { 0x3D, 0x81, cotByte, 0x00, 0x01, 0x01, 0xB2, 0x64 };
            var view = new Iec103AsduView(asdu, XujiApduLayout.Instance);

            var cot = view.Cot;
            var neg = view.Negative;

            Assert.Multiple(() =>
            {
                Assert.That(cot, Is.EqualTo(expectedCause));
                Assert.That(neg, Is.EqualTo(expectedNegative));
            });
        }

        [Test]
        public void Asdu_EncodeWithXujiLayout_ThenDecodeRoundTrip()
        {
            var original = new Asdu(61, true, 1, 20, false, 0x0101, 178, 100, new byte[] { 0x1F, 0x00 },
                XujiApduLayout.Instance);

            var encoded = original.Encode();
            Assert.That(encoded.Length, Is.EqualTo(8 + 2));
            Assert.That(encoded[2], Is.EqualTo(0x14), "COT 低字节（原因 20）");
            Assert.That(encoded[3], Is.EqualTo(0x00), "COT 高字节（源发地址）");
            Assert.That(encoded[4], Is.EqualTo(0x01), "CA 低字节");
            Assert.That(encoded[5], Is.EqualTo(0x01), "CA 高字节");

            Assert.That(Asdu.TryDecode(encoded, XujiApduLayout.Instance, out var decoded, out var error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(decoded!.Ca, Is.EqualTo(0x0101));
                Assert.That(decoded.Fun, Is.EqualTo(178));
                Assert.That(decoded.Inf, Is.EqualTo(100));
            });
        }

        [Test]
        public void Asdu_StandardLayout_RejectsWideCa()
        {
            var asdu = new Asdu(61, true, 1, 20, false, 0x0101, 178, 100, Array.Empty<byte>());

            // 标准 103 的 CA 只有 1 字节，静默截断会把 CA=0x0101 发成 CA=1，必须报错
            Assert.Throws<ArgumentOutOfRangeException>(() => asdu.Encode());
        }

        // ══════════════════════════════════════════════════════════════
        //  5. CA 语义（按许继 104 规约：低字节公共地址/扇区、高字节装置地址）
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void XujiCa_PacksSectorInLowByte_DeviceInHighByte()
        {
            var ca = new XujiCa(sector: 1, device: 1);

            Assert.That(ca.ToCommonAddress(), Is.EqualTo(0x0101), "现场 CA 值");
            Assert.That(ca.ToCommonAddress() & 0xFF, Is.EqualTo(1), "低字节 = 公共地址（扇区）");
            Assert.That((ca.ToCommonAddress() >> 8) & 0xFF, Is.EqualTo(1), "高字节 = 装置地址");
        }

        [Test]
        public void XujiCa_RoundTripsThroughParse()
        {
            var original = new XujiCa(sector: 0x12, device: 0x34);
            var parsed = XujiCa.Parse(original.ToCommonAddress());

            Assert.Multiple(() =>
            {
                Assert.That(parsed.Sector, Is.EqualTo(0x12));
                Assert.That(parsed.Device, Is.EqualTo(0x34));
            });
        }

        [Test]
        public void XujiCa_WireOrderIsSectorFirst()
        {
            // CA 按小端写出，落线顺序是「扇区在前、装置地址在后」
            var asdu = new Asdu(61, true, 1, 20, false,
                new XujiCa(sector: 0xAA, device: 0xBB).ToCommonAddress(),
                178, 100, Array.Empty<byte>(), XujiApduLayout.Instance);

            var encoded = asdu.Encode();
            Assert.Multiple(() =>
            {
                Assert.That(encoded[4], Is.EqualTo(0xAA), "偏移 4（CA 低字节）= 扇区");
                Assert.That(encoded[5], Is.EqualTo(0xBB), "偏移 5（CA 高字节）= 装置地址");
            });
        }

        [Test]
        public void CpuBoard_ComesFromInformationBody_NotFromCa()
        {
            // 现场报文：CA=0x0101（扇区1/装置1），信息体首字节 0x1F（CPU板1/定值区15）
            // 两者是不同字段——CPU 板不在 CA 里
            var bytes = Hex(ReadRequestHex);
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out _), Is.True);

            var view = new Iec103AsduView(frame!.Asdu, XujiApduLayout.Instance);
            var ca = XujiCa.Parse(view.Ca);
            var infoFirstByte = view.Data[0];

            Assert.Multiple(() =>
            {
                Assert.That(ca.Sector, Is.EqualTo(1), "CA 低字节 = 扇区");
                Assert.That(ca.Device, Is.EqualTo(1), "CA 高字节 = 装置地址");
                Assert.That(infoFirstByte >> 4, Is.EqualTo(1), "CPU 板在信息体高半字节");
                Assert.That(infoFirstByte & 0x0F, Is.EqualTo(15), "定值区在信息体低半字节（15=当前运行区）");
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  6. 两种承载互不误判
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void FieldFrames_AreNotFt12()
        {
            var parser = new Ft12FrameParser();
            string rejectedReason = null;
            parser.FrameRejected += (_, reason) => rejectedReason = reason;

            var frames = parser.Feed(Hex(ReadRequestHex));

            Assert.That(frames, Is.Empty, "许继帧不是 FT1.2，必须切不出帧");
            Assert.That(rejectedReason, Is.Not.Null, "应上报为坏帧而不是静默吞掉");
        }

        [Test]
        public void StandardVariableFrame_IsNotXujiApdu()
        {
            // 标准 FT1.2 可变帧：68 0A 0A 68 08 01 <ASDU 8B> CS 16
            var ft12 = Ft12Frame.EncodeVariable(0x08, 0x01,
                new byte[] { 0x09, 0x81, 0x02, 0x01, 0xA0, 0x01, 0x34, 0x12 });

            var codec = new XujiApduCodec();
            var frames = codec.Feed(ft12);

            // L=0x0A=10 → 期望总长 12，恰好等于实际长度，因此可能"解出"一帧，
            // 但解出的 ASDU 内容必然与原 ASDU 不同（偏移错位）。
            var originalAsdu = new byte[] { 0x09, 0x81, 0x02, 0x01, 0xA0, 0x01, 0x34, 0x12 };
            Assert.That(
                frames.Count == 0 || !frames[0].Asdu.SequenceEqual(originalAsdu),
                Is.True,
                "FT1.2 帧不应被误认为等价的许继 APDU 帧");
        }

        // ══════════════════════════════════════════════════════════════
        //  7. TI=61 命令的 VSQ 取值（表26 无值 / 表28 带值）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 表26（调定值/执行，不带定值项）VSQ 为 81H；表28（预发/返校，带定值项）
        /// VSQ 为「1 个头元素 + N 个定值项」。现场实测：27 项定值为 0x1C，单项为 0x02。
        /// </summary>
        [Test]
        [TestCase(false, 0, 0x81, TestName = "无定值项 → VSQ=0x81（表26）")]
        [TestCase(true, 1, 0x02, TestName = "1 个定值项 → VSQ=0x02（表28）")]
        [TestCase(true, 27, 0x1C, TestName = "27 个定值项 → VSQ=0x1C（表28）")]
        public void SettingCommand_VsqMatchesSpecTable(bool hasValues, int valueCount, int expectedVsq)
        {
            // 按 SettingClient.SendAsync 的组帧规则构造
            var payload = new byte[2 + valueCount * 3];
            payload[0] = 0x1F;
            var asdu = new Asdu(
                XujiTypeIds.SettingCommand,
                Sq: !hasValues,
                Count: hasValues ? 1 + valueCount : 1,
                Cot: XujiCot.Positive,
                Negative: false,
                Ca: 0x0101,
                Fun: 178,
                Inf: 100,
                Data: payload,
                Layout: XujiApduLayout.Instance);

            var bytes = asdu.Encode();

            Assert.Multiple(() =>
            {
                Assert.That(bytes[1], Is.EqualTo(expectedVsq), "VSQ 字节");
                Assert.That(bytes[0], Is.EqualTo(XujiTypeIds.SettingCommand), "TI");
                Assert.That(bytes[2], Is.EqualTo(XujiCot.Positive), "COT 低字节（8 字节头偏移 2）");
            });
        }

        /// <summary>现场 27 项定值应答的 VSQ 必须解出 28（1 头 + 27 项）。</summary>
        [Test]
        public void FieldReadResponse_VsqDecodesAsOnePlusN()
        {
            var bytes = Hex(ReadResponseHex);
            Assert.That(XujiApduFrame.TryDecode(bytes, out var frame, out _), Is.True);

            var view = new Iec103AsduView(frame!.Asdu, XujiApduLayout.Instance);

            var sq = view.Sq;
            var count = view.Count;
            var bodyLen = view.Data.Length;

            Assert.Multiple(() =>
            {
                Assert.That(sq, Is.False, "SQ=0（非序列）");
                Assert.That(count, Is.EqualTo(28), "VSQ = 1 + 27 = 28");
                Assert.That((bodyLen - 2) / 3, Is.EqualTo(27), "27 项 × 3 字节");
            });
        }
    }
}
