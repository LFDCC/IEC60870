/*
 *  CS103ProtocolCoreTests.cs
 *
 *  IEC60870.CS103 协议核单元测试（不含网络层）。
 *
 *  覆盖：
 *  1. FT1.2 帧编码/解码（固定帧、可变帧、单字符）、校验和、长度双写、错误拒绝；
 *  2. 流式分帧器：粘包、半包、噪声字节、坏帧上报、半帧超时复位；
 *  3. 链路控制域位布局（PRM/FCB/ACV/ACD/DFC，bit7 恒 0）；
 *  4. ASDU 头部编解码、FUN/INF 两级寻址、COT P/N 位；
 *  5. CP56/CP32 时标、DPI 品质往返；
 *  6. 通用分类 GIN/KOD/GDD/NGD 编解码、GB18030 截断；
 *  7. 私有类型注册表 5 条轴的分派优先级与实例隔离；
 *  8. 扰动录波 ASDU 编解码。
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IEC60870.CS103;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS103FrameTests
    {
        [Test]
        public void 固定帧编码_5字节且校验和正确()
        {
            var frame = Ft12Frame.EncodeFixed(0x40, 0x01);
            Assert.That(frame.Length, Is.EqualTo(5));
            Assert.That(frame[0], Is.EqualTo(0x10));
            Assert.That(frame[1], Is.EqualTo(0x40));
            Assert.That(frame[2], Is.EqualTo(0x01));
            Assert.That(frame[3], Is.EqualTo(0x41), "CS=(C+A) mod 256");
            Assert.That(frame[4], Is.EqualTo(0x16));
        }

        [Test]
        public void 可变帧编码_长度双写且校验和覆盖控制域至ASDU末()
        {
            byte[] asdu = { 0x01, 0x02, 0x03 };
            var frame = Ft12Frame.EncodeVariable(0x53, 0x0A, asdu);

            Assert.That(frame.Length, Is.EqualTo(asdu.Length + 8));
            Assert.That(frame[0], Is.EqualTo(0x68));
            Assert.That(frame[1], Is.EqualTo(asdu.Length + 2), "L = C + A + ASDU");
            Assert.That(frame[2], Is.EqualTo(frame[1]), "长度双写");
            Assert.That(frame[3], Is.EqualTo(0x68));
            Assert.That(frame[4], Is.EqualTo(0x53));
            Assert.That(frame[5], Is.EqualTo(0x0A));

            var expectedCs = (byte)(0x53 + 0x0A + 0x01 + 0x02 + 0x03);
            Assert.That(frame[^2], Is.EqualTo(expectedCs));
            Assert.That(frame[^1], Is.EqualTo(0x16));
        }

        [Test]
        public void 单字符帧_仅E5()
        {
            var frame = Ft12Frame.EncodeSingleChar();
            Assert.That(frame, Is.EqualTo(new byte[] { 0xE5 }));
        }

        [Test]
        public void 帧解码_固定帧往返()
        {
            var bytes = Ft12Frame.EncodeFixed(0x5A, 0x07);
            Assert.That(Ft12Frame.TryDecode(bytes, out var frame, out var error), Is.True, error);
            Assert.That(frame!.Format, Is.EqualTo(Ft12FrameFormat.FixedLength));
            Assert.That(frame.Control, Is.EqualTo(0x5A));
            Assert.That(frame.Address, Is.EqualTo(0x07));
            Assert.That(frame.Asdu, Is.Empty);
        }

        [Test]
        public void 帧解码_可变帧往返()
        {
            byte[] asdu = { 0x0A, 0x81, 0x09, 0x01, 0xFF, 0x00, 0x01 };
            var bytes = Ft12Frame.EncodeVariable(0x73, 0x01, asdu);
            Assert.That(Ft12Frame.TryDecode(bytes, out var frame, out var error), Is.True, error);
            Assert.That(frame!.Format, Is.EqualTo(Ft12FrameFormat.VariableLength));
            Assert.That(frame.Asdu, Is.EqualTo(asdu));
        }

        [Test]
        public void 帧解码_校验和错误被拒绝()
        {
            var bytes = Ft12Frame.EncodeVariable(0x53, 0x01, new byte[] { 0x01, 0x02 });
            bytes[^2] ^= 0xFF;
            Assert.That(Ft12Frame.TryDecode(bytes, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("校验和"));
        }

        [Test]
        public void 帧解码_长度双写不一致被拒绝()
        {
            var bytes = Ft12Frame.EncodeVariable(0x53, 0x01, new byte[] { 0x01, 0x02 });
            bytes[2] = (byte)(bytes[1] + 1);
            Assert.That(Ft12Frame.TryDecode(bytes, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("长度字节不一致"));
        }

        [Test]
        public void 帧解码_结束符错误被拒绝()
        {
            var bytes = Ft12Frame.EncodeFixed(0x40, 0x01);
            bytes[4] = 0x17;
            Assert.That(Ft12Frame.TryDecode(bytes, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("结束符"));
        }

        [Test]
        public void 可变帧编码_超过253字节抛异常()
        {
            var tooLong = new byte[Ft12Frame.MaxAsduLength + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() => Ft12Frame.EncodeVariable(0x53, 0x01, tooLong));
        }

        [Test]
        public void 帧编码_写入目标span与分配版本字节一致()
        {
            byte[] asdu = { 0xAA, 0xBB, 0xCC };
            var allocated = Ft12Frame.EncodeVariable(0x53, 0x02, asdu);

            var buffer = new byte[32];
            var written = Ft12Frame.EncodeVariableTo(buffer, 0x53, 0x02, asdu);

            Assert.That(written, Is.EqualTo(allocated.Length));
            Assert.That(buffer.AsSpan(0, written).ToArray(), Is.EqualTo(allocated));
        }
    }

    [TestFixture]
    public class CS103FrameParserTests
    {
        private static byte[] Concat(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var p in parts)
            {
                all.AddRange(p);
            }

            return all.ToArray();
        }

        [Test]
        public void 分帧_粘包一次切出多帧()
        {
            var parser = new Ft12FrameParser();
            var f1 = Ft12Frame.EncodeFixed(0x40, 0x01);
            var f2 = Ft12Frame.EncodeVariable(0x73, 0x01, new byte[] { 0x01, 0x02, 0x03 });
            var f3 = Ft12Frame.EncodeSingleChar();

            var frames = parser.Feed(Concat(f1, f2, f3));

            Assert.That(frames.Count, Is.EqualTo(3));
            Assert.That(frames[0].Format, Is.EqualTo(Ft12FrameFormat.FixedLength));
            Assert.That(frames[1].Format, Is.EqualTo(Ft12FrameFormat.VariableLength));
            Assert.That(frames[2].Format, Is.EqualTo(Ft12FrameFormat.SingleChar));
        }

        [Test]
        public void 分帧_半包累积后补齐()
        {
            var parser = new Ft12FrameParser();
            var frame = Ft12Frame.EncodeVariable(0x53, 0x05, new byte[] { 0x11, 0x22, 0x33, 0x44 });

            var first = parser.Feed(frame.AsSpan(0, 3));
            Assert.That(first, Is.Empty);
            Assert.That(parser.InFrame, Is.True);

            var second = parser.Feed(frame.AsSpan(3));
            Assert.That(second.Count, Is.EqualTo(1));
            Assert.That(second[0].Asdu, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
            Assert.That(parser.InFrame, Is.False);
        }

        [Test]
        public void 分帧_线路噪声被静默丢弃()
        {
            var parser = new Ft12FrameParser();
            var frame = Ft12Frame.EncodeFixed(0x5A, 0x01);
            var noisy = Concat(new byte[] { 0x00, 0x37, 0xFF }, frame);

            var frames = parser.Feed(noisy);
            Assert.That(frames.Count, Is.EqualTo(1));
            Assert.That(frames[0].Control, Is.EqualTo(0x5A));
        }

        [Test]
        public void 分帧_坏帧上报完整字节与原因()
        {
            var parser = new Ft12FrameParser();
            string rejectedHex = null;
            string rejectedReason = null;
            parser.FrameRejected += (hex, reason) =>
            {
                rejectedHex = hex;
                rejectedReason = reason;
            };

            var bad = Ft12Frame.EncodeVariable(0x53, 0x01, new byte[] { 0x01 });
            bad[^2] ^= 0xFF;
            var frames = parser.Feed(bad);

            Assert.That(frames, Is.Empty);
            Assert.That(rejectedHex, Is.Not.Null.And.Length.GreaterThan(0));
            Assert.That(rejectedReason, Does.Contain("校验和"));
        }

        [Test]
        public void 分帧_半帧超时可复位()
        {
            var parser = new Ft12FrameParser();
            var frame = Ft12Frame.EncodeVariable(0x53, 0x01, new byte[] { 0x01, 0x02, 0x03 });
            parser.Feed(frame.AsSpan(0, 4));
            Assert.That(parser.InFrame, Is.True);

            // 字符超时 1ms，等待片刻后应能复位
            System.Threading.Thread.Sleep(20);
            Assert.That(parser.TryResetStale(1), Is.True, "半帧应被判定超时");
            Assert.That(parser.InFrame, Is.False);

            // 复位后新帧可正常切出（原实现会吞掉重连后的首帧）
            var frames = parser.Feed(Ft12Frame.EncodeFixed(0x40, 0x01));
            Assert.That(frames.Count, Is.EqualTo(1));
        }

        [Test]
        public void 分帧_未超时不应复位()
        {
            var parser = new Ft12FrameParser();
            var frame = Ft12Frame.EncodeVariable(0x53, 0x01, new byte[] { 0x01 });
            parser.Feed(frame.AsSpan(0, 3));

            Assert.That(parser.TryResetStale(60000), Is.False);
            Assert.That(parser.InFrame, Is.True);
        }
    }

    [TestFixture]
    public class CS103LinkControlTests
    {
        [Test]
        public void 控制域_主站复位CU为0x40()
        {
            Assert.That(LinkControl.ResetCu().ToByte(), Is.EqualTo(0x40));
        }

        [Test]
        public void 控制域_FCB翻转产生0x53与0x73()
        {
            Assert.That(LinkControl.SendConfirmed(false).ToByte(), Is.EqualTo(0x53));
            Assert.That(LinkControl.SendConfirmed(true).ToByte(), Is.EqualTo(0x73));
        }

        [Test]
        public void 控制域_召唤1级为0x5A与0x7A()
        {
            Assert.That(LinkControl.RequestClass1(false).ToByte(), Is.EqualTo(0x5A));
            Assert.That(LinkControl.RequestClass1(true).ToByte(), Is.EqualTo(0x7A));
        }

        [Test]
        public void 控制域_bit7恒为零_无DIR位()
        {
            // 103 非平衡传输不使用 DIR；所有主站功能码的最高位必须为 0
            foreach (PrimaryFunction fn in Enum.GetValues(typeof(PrimaryFunction)))
            {
                var b = LinkControl.Primary(fn, true, true).ToByte();
                Assert.That((b & 0x80), Is.EqualTo(0), $"{fn} 不应设置 bit7");
            }
        }

        [Test]
        public void 控制域_从站应答ACD位正确()
        {
            var noData = LinkControl.NoData(acd: true).ToByte();
            Assert.That(noData, Is.EqualTo(0x29), "FC=9 + ACD(bit5)");

            var ack = LinkControl.Ack(acd: false, dfc: true).ToByte();
            Assert.That(ack, Is.EqualTo(0x10), "FC=0 + DFC(bit4)");
        }

        [Test]
        public void 控制域_字节往返保持全部位()
        {
            var original = new LinkControl(true, 10, true, true);
            var roundtrip = LinkControl.FromByte(original.ToByte());
            Assert.That(roundtrip.IsPrimary, Is.True);
            Assert.That(roundtrip.Function, Is.EqualTo(10));
            Assert.That(roundtrip.FcbOrAcd, Is.True);
            Assert.That(roundtrip.FcvOrDfc, Is.True);
        }

        [Test]
        public void 控制域_功能码描述可读()
        {
            Assert.That(LinkControl.ResetCu().DescribeFunction(), Is.EqualTo("RESET CU"));
            Assert.That(LinkControl.RequestClass1(false).DescribeFunction(), Is.EqualTo("REQ CLASS 1"));
            Assert.That(LinkControl.NoData().DescribeFunction(), Is.EqualTo("NO DATA"));
        }
    }

    [TestFixture]
    public class CS103AsduTests
    {
        [Test]
        public void ASDU_头部编码布局正确()
        {
            var asdu = new Asdu(1, true, 1, CotCodes.Spontaneous, false, 0x01, 160, 2, new byte[] { 0x02 });
            var bytes = asdu.Encode();

            Assert.That(bytes.Length, Is.EqualTo(7));
            Assert.That(bytes[0], Is.EqualTo(1));
            Assert.That(bytes[1], Is.EqualTo(0x81), "SQ=1 + count=1");
            Assert.That(bytes[2], Is.EqualTo(0x01), "COT=1 无 P/N");
            Assert.That(bytes[3], Is.EqualTo(0x01));
            Assert.That(bytes[4], Is.EqualTo(160), "FUN 专用范围");
            Assert.That(bytes[5], Is.EqualTo(2), "INF");
        }

        [Test]
        public void ASDU_否定位设置在COT最高位()
        {
            var asdu = new Asdu(10, true, 1, CotCodes.GenericReadInvalid, true, 1, Asdu.GenericFun, 0, Array.Empty<byte>());
            Assert.That(asdu.Encode()[2], Is.EqualTo((byte)(CotCodes.GenericReadInvalid | 0x80)));
        }

        [Test]
        public void ASDU_解码往返()
        {
            var original = new Asdu(1, true, 1, CotCodes.Spontaneous, false, 0x05, 161, 3, new byte[] { 0xAA, 0xBB });
            var bytes = original.Encode();

            Assert.That(Asdu.TryDecode(bytes, out var decoded, out var error), Is.True, error);
            Assert.That(decoded!.TypeId, Is.EqualTo(original.TypeId));
            Assert.That(decoded.Sq, Is.True);
            Assert.That(decoded.Count, Is.EqualTo(1));
            Assert.That(decoded.Cot, Is.EqualTo(CotCodes.Spontaneous));
            Assert.That(decoded.Negative, Is.False);
            Assert.That(decoded.Ca, Is.EqualTo(0x05));
            Assert.That(decoded.Fun, Is.EqualTo(161));
            Assert.That(decoded.Inf, Is.EqualTo(3));
            Assert.That(decoded.Data, Is.EqualTo(new byte[] { 0xAA, 0xBB }));
        }

        [Test]
        public void ASDU_短于6字节被拒绝()
        {
            Assert.That(Asdu.TryDecode(new byte[] { 1, 2, 3 }, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("至少 6 字节"));
        }

        [Test]
        public void ASDU_视图与物化模型字节一致()
        {
            var original = new Asdu(9, true, 1, CotCodes.Cyclic, false, 1, 160, 1, new byte[] { 0x10, 0x27 });
            var bytes = original.Encode();

            var view = new Iec103AsduView(bytes);
            Assert.That(view.TypeId, Is.EqualTo(9));
            Assert.That(view.Fun, Is.EqualTo(160));
            Assert.That(view.Inf, Is.EqualTo(1));
            Assert.That(view.IsPrivateFun, Is.True);
            Assert.That(Iec103AsduDecoder.Decode(in view) is Iec103MeasurandMessage { Values: [10000] }, Is.True);

            var materialized = Asdu.Materialize(view);
            Assert.That(materialized.Encode(), Is.EqualTo(bytes));
        }

        [Test]
        public void ASDU_视图读取TI1的DPI与CP56时标()
        {
            var time = new Cp56Time2a(1234, 5, 6, 7, 8, 26);
            var asdu = Asdu.TimeTaggedMessage(1, 160, 2, DpiQuality.FromDpi(2), time, CotCodes.Spontaneous);
            var view = new Iec103AsduView(asdu.Encode());

            Assert.That(view.TryGetDpi(out var dpi), Is.True);
            Assert.That(dpi.Dpi, Is.EqualTo(2));
            Assert.That(dpi.State, Is.EqualTo("合（ON）"));

            Assert.That(view.TryGetCp56Time2a(5, out var decoded), Is.True);
            Assert.That(decoded.Millisecond, Is.EqualTo(1234));
            Assert.That(decoded.Year, Is.EqualTo(26));
        }

        [Test]
        public void ASDU_总召唤命令布局()
        {
            var asdu = Asdu.GeneralInterrogation(1, 0);
            Assert.That(asdu.TypeId, Is.EqualTo(TypeIds.GeneralInterrogation));
            Assert.That(asdu.Cot, Is.EqualTo(CotCodes.GiStart));
            Assert.That(asdu.Fun, Is.EqualTo(Asdu.GlobalFun));
            Assert.That(asdu.Inf, Is.Zero);
            Assert.That(asdu.Data, Is.EqualTo(new byte[] { 0 }));
        }

        [Test]
        public void ASDU_标识报文为8字节ASCII()
        {
            var asdu = Asdu.Identification(1, false, "TEST");
            Assert.That(asdu.Data.Length, Is.EqualTo(8));
            Assert.That(Encoding.ASCII.GetString(asdu.Data), Is.EqualTo("TEST    "));
            Assert.That(asdu.Inf, Is.EqualTo(Asdu.InfCompatIdent));
            Assert.That(asdu.Cot, Is.EqualTo(CotCodes.ResetCu));
        }

        [Test]
        public void ASDU_一般命令DCO解码()
        {
            var asdu = Asdu.GeneralCommand(1, 160, 1, 0x02, 7);
            var (dct, qu, rii) = Asdu.DecodeGeneralCommand(asdu);
            Assert.That(dct, Is.EqualTo(2), "合");
            Assert.That(qu, Is.Zero);
            Assert.That(rii, Is.EqualTo(7));
        }

        [Test]
        public void ASDU_保护事件相对时标与故障序号()
        {
            var time = new Cp56Time2a(0, 0, 0, 1, 1, 26);
            var asdu = Asdu.ProtectionEvent(1, 160, 3, DpiQuality.FromDpi(2), 1234, 42, time, CotCodes.Spontaneous);
            var (relMs, fan) = Asdu.DecodeProtectionEvent(asdu);
            Assert.That(relMs, Is.EqualTo(1234));
            Assert.That(fan, Is.EqualTo(42));
        }
    }

    [TestFixture]
    public class CS103TimeQualityTests
    {
        [Test]
        public void CP56_往返()
        {
            var original = new Cp56Time2a(3599, 30, 14, 23, 7, 26);
            var decoded = Cp56Time2a.Decode(original.Encode());

            Assert.That(decoded.Millisecond, Is.EqualTo(3599));
            Assert.That(decoded.Minute, Is.EqualTo(30));
            Assert.That(decoded.Hour, Is.EqualTo(14));
            Assert.That(decoded.Day, Is.EqualTo(23));
            Assert.That(decoded.Month, Is.EqualTo(7));
            Assert.That(decoded.Year, Is.EqualTo(26));
        }

        [Test]
        public void CP56_毫秒域含秒()
        {
            // CP56 的毫秒域是 0-59999（含秒），不是 0-999
            var time = Cp56Time2a.FromDateTime(new DateTime(2026, 7, 23, 14, 30, 5, 678));
            Assert.That(time.Millisecond, Is.EqualTo(5678));
            var back = time.ToDateTime();
            Assert.That(back.Second, Is.EqualTo(5));
            Assert.That(back.Millisecond, Is.EqualTo(678));
        }

        [Test]
        public void CP56_无效位在分钟字节最高位()
        {
            var time = new Cp56Time2a(0, 10, 12, 1, 1, 26, Invalid: true);
            var bytes = time.Encode();
            Assert.That((bytes[2] & 0x80), Is.EqualTo(0x80));
            Assert.That(Cp56Time2a.Decode(bytes).Invalid, Is.True);
        }

        [Test]
        public void CP32_往返与长度()
        {
            var original = new Cp32Time2a(1234, 30, 14);
            var bytes = original.Encode();
            Assert.That(bytes.Length, Is.EqualTo(4));

            var decoded = Cp32Time2a.Decode(bytes);
            Assert.That(decoded.Millisecond, Is.EqualTo(1234));
            Assert.That(decoded.Minute, Is.EqualTo(30));
            Assert.That(decoded.Hour, Is.EqualTo(14));
        }

        [Test]
        public void DPI_品质位按标准位序编码()
        {
            // IEC 60870-5-101/104 DIQ：bit4 BL、bit5 SB、bit6 NT、bit7 IV、bit2-3 保留。
            var original = new DpiQuality(2, Invalid: true, NonTopical: true, Substituted: true, Blocked: true);
            var encoded = original.Encode();

            Assert.That(encoded & 0x03, Is.EqualTo(2), "DPI bit0-1");
            Assert.That(encoded & 0x0C, Is.EqualTo(0), "bit2-3 保留恒 0");
            Assert.That(encoded & 0x10, Is.EqualTo(0x10), "BL bit4");
            Assert.That(encoded & 0x20, Is.EqualTo(0x20), "SB bit5");
            Assert.That(encoded & 0x40, Is.EqualTo(0x40), "NT bit6");
            Assert.That(encoded & 0x80, Is.EqualTo(0x80), "IV bit7");
            Assert.That(encoded, Is.EqualTo(0xF2), "全品质位 + DPI=2");

            var decoded = DpiQuality.Decode(encoded);
            Assert.That(decoded, Is.EqualTo(original));
        }

        [Test]
        public void DPI_按标准解码真实装置字节()
        {
            // 0x81 = IV + DPI=1（分）：装置传来的「无效的分位」必须保留 IV。
            var invalid = DpiQuality.Decode(0x81);
            Assert.That(invalid.Dpi, Is.EqualTo(1));
            Assert.That(invalid.Invalid, Is.True, "0x81 的 bit7 必须解成 IV");
            Assert.That(invalid.NonTopical || invalid.Substituted || invalid.Blocked, Is.False);

            // 0x41 = NT + DPI=1：bit6 是 NT，不是任何「事件有效」位。
            var stale = DpiQuality.Decode(0x41);
            Assert.That(stale.Dpi, Is.EqualTo(1));
            Assert.That(stale.NonTopical, Is.True);
            Assert.That(stale.Invalid, Is.False);

            // 0x04/0x08 是保留位，不得被解释成 IV/NT。
            var reserved = DpiQuality.Decode(0x0C);
            Assert.That(reserved.Invalid || reserved.NonTopical || reserved.Substituted || reserved.Blocked, Is.False);
            Assert.That(reserved.Dpi, Is.EqualTo(0));
        }

        [Test]
        public void DPI_状态文本()
        {
            Assert.That(DpiQuality.FromDpi(1).State, Is.EqualTo("分（OFF）"));
            Assert.That(DpiQuality.FromDpi(2).State, Is.EqualTo("合（ON）"));
            Assert.That(DpiQuality.FromDpi(0).State, Is.EqualTo("不确定"));
        }
    }

    [TestFixture]
    public class CS103GenericTests
    {
        [Test]
        public void GIN_编解码()
        {
            var gin = new Gin(0x90, 0x01);
            Assert.That(Gin.Decode(gin.Encode()), Is.EqualTo(gin));
            Assert.That(gin.ToString(), Is.EqualTo("144.1"));
        }

        [Test]
        public void GDD_位串宽度按位数转字节()
        {
            var gdd = new Gdd(GddTypes.Bitstring, 12, 2, false);
            Assert.That(gdd.ElementSize, Is.EqualTo(2), "12 位 → 2 字节");
            Assert.That(gdd.GidSize, Is.EqualTo(4));
        }

        [Test]
        public void GDD_非位串宽度即字节数()
        {
            var gdd = new Gdd(GddTypes.ShortReal, 4, 3, false);
            Assert.That(gdd.ElementSize, Is.EqualTo(4));
            Assert.That(gdd.GidSize, Is.EqualTo(12));
        }

        [Test]
        public void 数据集_ASCII往返()
        {
            var set = GenericDataSet.Ascii(new Gin(1, 2), KodCodes.Description, "保护装置描述");
            var bytes = set.Encode();

            Assert.That(GenericDataSet.TryDecode(bytes, out var decoded, out var consumed), Is.True);
            Assert.That(consumed, Is.EqualTo(bytes.Length));
            Assert.That(decoded!.Gin, Is.EqualTo(new Gin(1, 2)));
            Assert.That(decoded.Kod, Is.EqualTo(KodCodes.Description));
            Assert.That((string)decoded.DecodeValue(), Is.EqualTo("保护装置描述"));
        }

        [Test]
        public void 数据集_GB18030截断落在字符边界()
        {
            // 400 个汉字远超 239 字节预算，截断后不应产生半个多字节字符
            var text = new string('保', 400);
            var set = GenericDataSet.Ascii(new Gin(1, 1), KodCodes.Description, text);

            Assert.That(set.Gid.Length, Is.LessThanOrEqualTo(GenericDataSet.MaxAsciiBytes));
            Assert.That(set.Gdd.DataWidth, Is.EqualTo(set.Gid.Length), "宽度必须等于实际 GID 长度");

            var decoded = GenericDataSet.DecodeGb18030(set.Gid);
            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded.All(c => c == '保'), Is.True, "不应出现乱码字符");
        }

        [Test]
        public void 数据集_无符号整数往返()
        {
            var set = GenericDataSet.Unsigned(new Gin(2, 3), KodCodes.ActualValue, 123456, 4);
            var decoded = (long)set.DecodeValue();
            Assert.That(decoded, Is.EqualTo(123456));
        }

        [Test]
        public void 数据集_有符号负数往返()
        {
            var set = GenericDataSet.Signed(new Gin(2, 4), KodCodes.ActualValue, -98765, 4);
            var decoded = (long)set.DecodeValue();
            Assert.That(decoded, Is.EqualTo(-98765));
        }

        [Test]
        public void 数据集_短实数往返()
        {
            var set = GenericDataSet.Float(new Gin(2, 5), KodCodes.ActualValue, 3.14159f);
            var decoded = (float)set.DecodeValue();
            Assert.That(decoded, Is.EqualTo(3.14159f).Within(1e-6));
        }

        [Test]
        public void 数据集_量程为三个短实数()
        {
            var set = GenericDataSet.Range(new Gin(3, 1), 0f, 100f, 0.1f);
            Assert.That(set.Kod, Is.EqualTo(KodCodes.Range));
            Assert.That(set.Gid.Length, Is.EqualTo(12));
            Assert.That(set.Gdd.Number, Is.EqualTo(3));
        }

        [Test]
        public void 数据集_含后续标志时逐条解码()
        {
            var s1 = GenericDataSet.Unsigned(new Gin(1, 1), KodCodes.ActualValue, 10, 2);
            var s2 = GenericDataSet.Unsigned(new Gin(1, 2), KodCodes.ActualValue, 20, 2);
            var all = s1.Encode().Concat(s2.Encode()).ToArray();

            Assert.That(GenericDataSet.TryDecode(all, out var first, out var used1), Is.True);
            Assert.That(GenericDataSet.TryDecode(all.AsSpan(used1), out var second, out var used2), Is.True);

            Assert.That((long)first!.DecodeValue(), Is.EqualTo(10));
            Assert.That((long)second!.DecodeValue(), Is.EqualTo(20));
            Assert.That(used1 + used2, Is.EqualTo(all.Length));
        }

        [Test]
        public void NGD_数目与后续标志()
        {
            var encoded = NgdCodes.Encode(5, true);
            Assert.That((encoded & NgdCodes.ContinuationMask), Is.EqualTo(0x80));

            var (count, cont) = NgdCodes.Decode(encoded);
            Assert.That(count, Is.EqualTo(5));
            Assert.That(cont, Is.True);
        }

        [Test]
        public void 通用分类读全目录_命令布局()
        {
            var asdu = Asdu.GenericReadAll(1, 9);
            Assert.That(asdu.TypeId, Is.EqualTo(TypeIds.GenericCommand));
            Assert.That(asdu.Cot, Is.EqualTo(CotCodes.GenericRead));
            Assert.That(asdu.Fun, Is.EqualTo(Asdu.GenericFun));
            Assert.That(asdu.Data[0], Is.EqualTo(9), "RII");
            Assert.That(asdu.Data[1], Is.EqualTo(1), "NOG");
            Assert.That(asdu.Data[4], Is.EqualTo(KodCodes.Description), "KOD=描述");
        }
    }

    [TestFixture]
    public class CS103PrivateRegistryTests
    {
        private sealed class FakeHandler : IIec103PrivateTypeHandler
        {
            public FakeHandler(byte typeId, string vendor, byte? fun = null, byte? inf = null)
            {
                TypeId = typeId;
                VendorTag = vendor;
                Fun = fun;
                Inf = inf;
            }

            public byte TypeId { get; }

            public string VendorTag { get; }

            public byte? Fun { get; }

            public byte? Inf { get; }

            public byte[] Encode(Iec103DecodedAsdu value) => BitConverter.GetBytes(value.Header.TypeId);
        }

        [Test]
        public void 注册表_按TI登记可解析()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var handler = new FakeHandler(200, "TestVendor");
            reg.RegisterType(handler);

            Assert.That(reg.TryResolve(200, 0, 0, CotCodes.Spontaneous, out var found), Is.True);
            Assert.That(found, Is.SameAs(handler));
        }

        [Test]
        public void 注册表_FUN_INF精确匹配优先于TI()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var byType = new FakeHandler(200, "ByType");
            var byFunInf = new FakeHandler(200, "ByFunInf");
            reg.RegisterType(byType);
            reg.RegisterFunInf(200, 7, byFunInf);

            Assert.That(reg.TryResolve(200, 200, 7, CotCodes.Spontaneous, out var found1), Is.True);
            Assert.That(found1, Is.SameAs(byFunInf), "FUN+INF 精确匹配应优先");

            Assert.That(reg.TryResolve(200, 200, 8, CotCodes.Spontaneous, out var found2), Is.True);
            Assert.That(found2, Is.SameAs(byType), "未命中 FUN+INF 时回退 TI");
        }

        [Test]
        public void 注册表_FUN区间匹配覆盖专用范围()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var rangeHandler = new FakeHandler(0, "RangeVendor");
            reg.RegisterFunRange(160, 255, rangeHandler);

            Assert.That(reg.TryResolve(1, 160, 1, CotCodes.Spontaneous, out var hit1), Is.True);
            Assert.That(hit1, Is.SameAs(rangeHandler));

            Assert.That(reg.TryResolve(1, 255, 99, CotCodes.Spontaneous, out var hit2), Is.True);
            Assert.That(hit2, Is.SameAs(rangeHandler));

            Assert.That(reg.TryResolve(1, 159, 1, CotCodes.Spontaneous, out _), Is.False, "区间外不应命中");
        }

        [Test]
        public void 注册表_注销后不再命中()
        {
            var reg = new Iec103PrivateTypeRegistry();
            reg.RegisterType(new FakeHandler(201, "V"));

            Assert.That(reg.UnregisterType(201), Is.True);
            Assert.That(reg.TryResolve(201, 0, 0, 1, out _), Is.False);
            Assert.That(reg.UnregisterType(201), Is.False, "重复注销返回 false");
        }

        [Test]
        public void 注册表_实例隔离_不同实例互不影响()
        {
            var regA = new Iec103PrivateTypeRegistry();
            var regB = new Iec103PrivateTypeRegistry();

            regA.RegisterType(new FakeHandler(202, "VendorA"));

            Assert.That(regA.TryResolve(202, 0, 0, 1, out _), Is.True);
            Assert.That(regB.TryResolve(202, 0, 0, 1, out _), Is.False, "B 实例不应看到 A 的登记");
        }

        [Test]
        public void 注册表_私有KOD与GDD名称覆盖标准描述()
        {
            var reg = new Iec103PrivateTypeRegistry();
            reg.RegisterKod(200, "厂商私有定值类型");
            reg.RegisterGddDataType(99, "厂商私有数据结构");

            Assert.That(reg.DescribeKod(200), Is.EqualTo("厂商私有定值类型"));
            Assert.That(reg.DescribeKod(KodCodes.Description), Is.EqualTo("描述"), "标准值回退内置表");
            Assert.That(reg.DescribeGddDataType(99), Is.EqualTo("厂商私有数据结构"));
        }

        [Test]
        public void 注册表_按GIN解析_精确优先于组区间()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var byRange = new FakeGenericHandler("Range");
            var byExact = new FakeGenericHandler("Exact");

            reg.RegisterGinRange(0x90, 0x9F, byRange);
            reg.RegisterGin(0x90, 0x01, byExact);

            Assert.That(reg.TryResolveGin(new Gin(0x90, 0x01), out var hit1), Is.True);
            Assert.That(hit1, Is.SameAs(byExact));

            Assert.That(reg.TryResolveGin(new Gin(0x90, 0x02), out var hit2), Is.True);
            Assert.That(hit2, Is.SameAs(byRange));
        }

        [Test]
        public void 注册表_私有COT名称()
        {
            var reg = new Iec103PrivateTypeRegistry();
            reg.RegisterCot(50, "厂商私有传送原因");
            Assert.That(reg.DescribeCot(50), Is.EqualTo("厂商私有传送原因"));
            Assert.That(reg.DescribeCot(1), Is.EqualTo("突发"), "标准值回退");
        }

        [Test]
        public void 注册表_并发登记与读取不撕裂()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var errors = new List<Exception>();

            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 200; i++)
                    {
                        reg.RegisterType((byte)(100 + (i % 50)), new FakeHandler((byte)(100 + (i % 50)), $"V{i}"));
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            });

            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 2000; i++)
                    {
                        reg.TryResolve((byte)(100 + (i % 50)), 0, 0, 1, out _);
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            });

            System.Threading.Tasks.Task.WaitAll(writer, reader);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void 注册表_批量登记FUN_INF()
        {
            var reg = new Iec103PrivateTypeRegistry();
            var handler = new FakeHandler(210, "Batch");
            reg.RegisterFunInfMany(handler, (200, 1), (200, 2), (201, 3));

            Assert.That(reg.TryResolve(210, 200, 2, 1, out var h1), Is.True);
            Assert.That(h1, Is.SameAs(handler));
            Assert.That(reg.TryResolve(210, 201, 3, 1, out var h2), Is.True);
            Assert.That(h2, Is.SameAs(handler));
        }

        private sealed class FakeGenericHandler : IIec103GenericDataHandler
        {
            public FakeGenericHandler(string tag)
            {
                VendorTag = tag;
            }

            public string VendorTag { get; }

            public string Describe(in Gin gin, byte kod, in Gdd gdd, ReadOnlySpan<byte> gid) => VendorTag;

            public object Decode(in Gin gin, byte kod, in Gdd gdd, ReadOnlySpan<byte> gid) => VendorTag;
        }
    }

    [TestFixture]
    public class CS103UnknownAsduTests
    {
        [Test]
        public void 未识别_保留完整原始字节与头部()
        {
            var asdu = new Asdu(250, true, 1, CotCodes.Spontaneous, false, 3, 200, 9, new byte[] { 0xDE, 0xAD });
            var raw = asdu.Encode();

            var unknown = new Iec103UnknownAsdu(raw, reason: "未登记 TI=250");

            Assert.That(unknown.HasHeader, Is.True);
            Assert.That(unknown.TypeId, Is.EqualTo(250));
            Assert.That(unknown.Cot, Is.EqualTo(CotCodes.Spontaneous));
            Assert.That(unknown.Ca, Is.EqualTo(3));
            Assert.That(unknown.Fun, Is.EqualTo(200));
            Assert.That(unknown.Inf, Is.EqualTo(9));
            Assert.That(unknown.Raw, Is.EqualTo(raw), "原始字节必须完整保留");
            Assert.That(unknown.ToString(), Does.Contain("DEAD"));
        }

        [Test]
        public void 未识别_超短报文也不抛异常()
        {
            var unknown = new Iec103UnknownAsdu(new byte[] { 0x01 }, reason: "过短");
            Assert.That(unknown.HasHeader, Is.False);
            Assert.That(unknown.Raw.Length, Is.EqualTo(1));
        }
    }

    [TestFixture]
    public class CS103DisturbanceTests
    {
        [Test]
        public void 扰动表_编解码往返()
        {
            var records = new List<DisturbanceRecord>
            {
                new()
                {
                    Fan = 7,
                    Status = new Sof(TripProtection: true, TripCommand: true, Test: false, EquipmentStart: true),
                    FaultTime = new DateTime(2026, 7, 23, 14, 30, 12, 345),
                },
            };

            var asdu = DisturbanceAsdu.DisturbanceList(1, records);
            Assert.That(asdu.TypeId, Is.EqualTo(TypeIds.DisturbanceList));
            Assert.That(asdu.Cot, Is.EqualTo(CotCodes.DisturbanceTransfer));
            Assert.That(asdu.Count, Is.EqualTo(1));

            var decoded = DisturbanceAsdu.DecodeDisturbanceList(asdu);
            Assert.That(decoded.Count, Is.EqualTo(1));
            Assert.That(decoded[0].Fan, Is.EqualTo(7));
            Assert.That(decoded[0].Status.TripProtection, Is.True);
            Assert.That(decoded[0].Status.Test, Is.False);
            Assert.That(decoded[0].FaultTime.Second, Is.EqualTo(12));
        }

        [Test]
        public void 通道数据段_编解码往返()
        {
            short[] samples = { 100, -200, 300, -400, 500 };
            var asdu = DisturbanceAsdu.ChannelData(1, TooCodes.RequestChannel, 7, 1, samples, 0, samples.Length);

            var segment = DisturbanceAsdu.DecodeChannelData(asdu);
            Assert.That(segment.Fan, Is.EqualTo(7));
            Assert.That(segment.Acc, Is.EqualTo(1));
            Assert.That(segment.FirstElement, Is.EqualTo(1), "元素序号从 1 起");
            Assert.That(segment.Values, Is.EqualTo(samples));
        }

        [Test]
        public void 通道数据段_短帧报错而非越界()
        {
            var bad = new Asdu(TypeIds.ChannelData, true, 1, CotCodes.DisturbanceTransfer, false, 1, Asdu.GlobalFun, 0,
                new byte[] { 1, 1, 0, 0, 1 });
            Assert.Throws<FormatException>(() => DisturbanceAsdu.DecodeChannelData(bad));
        }

        [Test]
        public void 通道数据段_声明点数超过实长报错()
        {
            var bad = new Asdu(TypeIds.ChannelData, true, 1, CotCodes.DisturbanceTransfer, false, 1, Asdu.GlobalFun, 0,
                new byte[] { 1, 1, 0, 0, 1, 10, 1, 0, 0x01, 0x00 });
            Assert.Throws<FormatException>(() => DisturbanceAsdu.DecodeChannelData(bad));
        }

        [Test]
        public void 就绪帧_字段解码()
        {
            var record = new DisturbanceRecord
            {
                Fan = 9,
                SampleIntervalUs = 1000,
                FaultTime = new DateTime(2026, 7, 23, 14, 30, 12, 345),
            };
            record.Channels.Add(new DisturbanceChannel { Acc = 1, SampleCount = 200 });

            var asdu = DisturbanceAsdu.TransferReady(1, TooCodes.SelectFault, record, 3);
            var (fan, recorded, channels, samples, interval, time) = DisturbanceAsdu.DecodeTransferReady(asdu);

            Assert.That(fan, Is.EqualTo(9));
            Assert.That(recorded, Is.EqualTo(3));
            Assert.That(channels, Is.EqualTo(1));
            Assert.That(samples, Is.EqualTo(200));
            Assert.That(interval, Is.EqualTo(1000));
            Assert.That(time.Hour, Is.EqualTo(14));
        }

        [Test]
        public void TOO语义描述()
        {
            Assert.That(TooCodes.Describe(TooCodes.RequestDisturbanceList), Is.EqualTo("请求被记录的扰动表"));
            Assert.That(TooCodes.Describe(TooCodes.EndChannel + 2), Is.EqualTo("通道传输结束（装置中止）"));
            Assert.That(TooCodes.Describe(200), Is.EqualTo("TOO 200"));
        }

        [Test]
        public void SOF位布局()
        {
            var sof = new Sof(true, true, false, false);
            Assert.That(sof.Encode(), Is.EqualTo(0xC0));

            var decoded = Sof.Decode(0xC0);
            Assert.That(decoded.TripProtection, Is.True);
            Assert.That(decoded.TripCommand, Is.True);
            Assert.That(decoded.Test, Is.False);
        }
    }

    [TestFixture]
    public class CS103OptionsTests
    {
        [Test]
        public void 选项_默认值合理()
        {
            var options = new Iec103Options();
            Assert.That(options.Link.MessageTimeoutMs, Is.EqualTo(2000));
            Assert.That(options.Link.CharacterTimeoutMs, Is.EqualTo(50));
            Assert.That(options.Link.MaxRepeat, Is.EqualTo(3));
            Assert.That(options.UnknownTypePolicy, Is.EqualTo(Iec103UnknownTypePolicy.Log));
            Assert.That(options.Link.AddressLength, Is.EqualTo(1), "103 固定 1 字节链路地址");
        }

        [Test]
        public void 选项_Fluent配置生效()
        {
            var options = new Iec103Options()
                .UseLinkParameters(l => { l.MessageTimeoutMs = 500; l.MaxRepeat = 5; })
                .UseUnknownTypePolicy(Iec103UnknownTypePolicy.Ignore);

            Assert.That(options.Link.MessageTimeoutMs, Is.EqualTo(500));
            Assert.That(options.Link.MaxRepeat, Is.EqualTo(5));
            Assert.That(options.UnknownTypePolicy, Is.EqualTo(Iec103UnknownTypePolicy.Ignore));
        }

        [Test]
        public void 选项_克隆为深拷贝()
        {
            var original = new Iec103Options().UseLinkParameters(l => l.MessageTimeoutMs = 777);
            var clone = original.Clone();

            clone.Link.MessageTimeoutMs = 999;

            Assert.That(clone.Link.MessageTimeoutMs, Is.EqualTo(999));
            Assert.That(original.Link.MessageTimeoutMs, Is.EqualTo(777), "克隆不应影响原对象");
        }
    }
}
