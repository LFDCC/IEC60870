/*
 *  CommonAddressWireFormatTests.cs
 *
 *  钉死「公共地址 CA 的线格式」这一库契约：CA 按 ApplicationLayerParameters.SizeOfCA
 *  以**小端**写出（低字节在前）。许继 104 的接入规则
 *      ca = (装置地址 << 8) | 扇区
 *  （规约 §3.14 表18：低字节=公共地址/扇区，高字节=装置地址）
 *  完全建立在这条契约上，因此这里既验证通用契约，也按许继语义把
 *  「扇区在前、装置地址在后」的字节顺序写死，防止将来有人把高低字节写反。
 *
 *  防回归点：
 *  1. SizeOfCA=2 时线字节 = [ca & 0xFF, ca >> 8]（低字节在前）；
 *  2. 许继语义：线字节 = [扇区, 装置地址]；
 *  3. 解码往返：Ca 原样还原（含 0xFFFF 全 1 边界）；
 *  4. SizeOfCA=1 时不写高字节，且不影响其后信息体（IOA）的解码。
 */

using IEC60870.Core;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CommonAddressWireFormatTests
    {
        private static readonly QualityDescriptor Qd = QualityDescriptor.VALID();

        /// <summary>CA 在 ASDU 体中的偏移：TI(1) + VSQ(1) + COT(SizeOfCOT, 含 Oa)。</summary>
        private static int CaOffset(ApplicationLayerParameters al) => 1 + 1 + al.SizeOfCOT;

        private static byte[] EncodeAsdu(ApplicationLayerParameters al, int ca)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, ca, false);
            asdu.AddInformationObject(new SinglePointInformation(0x010203, true, Qd));
            byte[] body = asdu.AsByteArray();
            Assert.That(body, Is.Not.Null, "ASDU.AsByteArray() 不应返回 null");
            return body;
        }

        [TestCase(0x0001)]
        [TestCase(0x0102)]
        [TestCase(0x0502)]
        [TestCase(0x00FF)]
        [TestCase(0xFF00)]
        [TestCase(0x1234)]
        [TestCase(0xFFFF)]
        public void CA_按小端两字节写出_并解码往返(int ca)
        {
            var al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };
            byte[] body = EncodeAsdu(al, ca);
            int off = CaOffset(al);

            Assert.That(body[off], Is.EqualTo((byte)(ca & 0xFF)), $"CA=0x{ca:X4} 首字节应为低字节");
            Assert.That(body[off + 1], Is.EqualTo((byte)(ca >> 8)), $"CA=0x{ca:X4} 次字节应为高字节");

            var decoded = new ASDU(al, body, 0, body.Length);
            Assert.That(decoded.Ca, Is.EqualTo(ca), $"CA=0x{ca:X4} 解码往返");
        }

        /// <summary>
        /// 许继 104 语义：ca = (装置地址 &lt;&lt; 8) | 扇区 ⇒ 线字节必须是 [扇区, 装置地址]。
        /// 这正是 examples/104/cs104-private-ioa 里 XujiCa 所依赖的契约。
        /// </summary>
        [TestCase(1, 0)]
        [TestCase(1, 3)]
        [TestCase(2, 5)]
        [TestCase(255, 0)]
        [TestCase(0, 255)]
        [TestCase(255, 255)]
        public void 许继语义_扇区在低字节_装置地址在高字节(byte sector, byte device)
        {
            var al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };
            int ca = (device << 8) | sector;          // 许继的拼装式
            byte[] body = EncodeAsdu(al, ca);
            int off = CaOffset(al);

            Assert.That(body[off], Is.EqualTo(sector), "CA 首字节应为 扇区");
            Assert.That(body[off + 1], Is.EqualTo(device), "CA 次字节应为 装置地址");

            var decoded = new ASDU(al, body, 0, body.Length);
            Assert.That(decoded.Ca, Is.EqualTo(ca));
        }

        [Test]
        public void SizeOfCA为1时_不写高字节_且不影响信息体地址解码()
        {
            var al = new ApplicationLayerParameters { SizeOfCA = 1, SizeOfIOA = 3, SizeOfCOT = 2 };
            byte[] body = EncodeAsdu(al, 0x0502);

            // 只写低字节
            Assert.That(body[CaOffset(al)], Is.EqualTo(0x02), "SizeOfCA=1 只写低字节");

            var decoded = new ASDU(al, body, 0, body.Length);
            Assert.That(decoded.Ca, Is.EqualTo(0x02), "高字节丢失，CA 截断为低字节");
            Assert.That(decoded.GetElement(0).ObjectAddress, Is.EqualTo(0x010203),
                "CA 宽度变化不得影响其后 IOA 的定位与解码");
        }
    }
}
