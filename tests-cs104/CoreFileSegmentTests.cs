/*
 *  CoreFileSegmentTests.cs
 *
 *  锁死 FileSegment 的编码尺寸核算。
 *
 *  背景缺陷：GetEncodedSize() 只返回固定前缀（4 字节），不含段数据长度，
 *  而 EncodeBody 实际写入 4 + data.Length 字节。ASDU.AsByteArray() 按
 *  「MaxAsduLength - _spaceLeft」分配缓冲区 → 一个 200 字节的段只分到 ~13 字节，
 *  编码时直接越界。104 冗余组入队（RedundancyGroup.Enqueue）与抓包导出都走这条路径。
 */

using System;
using IEC60870.Core;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CoreFileSegmentTests
    {
        private static byte[] Payload(int length)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)(i * 7 + 1);
            }

            return data;
        }

        [Test]
        public void FileSegment_编码尺寸必须包含段数据()
        {
            var al = new ApplicationLayerParameters();
            var data = Payload(200);
            var segment = new FileSegment(30000, NameOfFile.TRANSPARENT_FILE, 1, data);

            Assert.That(segment.GetEncodedSize(), Is.EqualTo(4 + data.Length),
                "段尺寸必须 = 固定前缀(4) + 段数据长度；原实现只算前缀，编码时会越界");

            var asdu = new ASDU(al, CauseOfTransmission.FILE_TRANSFER, false, false, 0, 1, false);
            asdu.AddInformationObject(segment);

            byte[] encoded = null;
            Assert.DoesNotThrow(() => encoded = asdu.AsByteArray(),
                "含 200 字节段的 ASDU 必须能完整编码（原实现按 13 字节分配缓冲区）");

            Assert.That(encoded, Is.Not.Null);
            Assert.That(encoded.Length, Is.GreaterThan(data.Length),
                "编码结果必须容纳整段数据");
        }

        [Test]
        public void FileSegment_接近上限的段仍可编码()
        {
            var al = new ApplicationLayerParameters();
            var maxData = FileSegment.GetMaxDataSize(al);
            var data = Payload(maxData);

            var asdu = new ASDU(al, CauseOfTransmission.FILE_TRANSFER, false, false, 0, 1, false);
            asdu.AddInformationObject(new FileSegment(30001, NameOfFile.TRANSPARENT_FILE, 1, data));

            byte[] encoded = null;
            Assert.DoesNotThrow(() => encoded = asdu.AsByteArray());
            Assert.That(encoded.Length, Is.GreaterThan(0));
        }
    }
}
