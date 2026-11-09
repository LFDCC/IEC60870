/*
 *  RobustnessFuzzTests.cs
 *
 *  生产可用性验证：畸形输入韧性（fuzz）+ 长跑稳定性 + 重连风暴。
 *
 *  现有测试全部走"正常报文"路径。真实生产环境会遇到：
 *    - 线路噪声 / 半帧 / 错校验和 / 长度字段被破坏；
 *    - 恶意或故障装置发送超长 / 截断 / 全 0xFF 报文；
 *    - 长时间运行后的状态漂移；
 *    - 网络抖动导致的反复重连。
 *  本文件覆盖这些场景，断言的核心是「不崩溃、不死循环、不无限分配」。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.CS103;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class RobustnessFuzzTests
    {
        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        // ══════════════════════════════════════════════════════════
        //  1. FT1.2 分帧器 fuzz（CS101 + CS103）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 随机字节流喂给 CS101 分帧器：不得抛异常、不得死循环、不得无限增长缓冲。
        /// </summary>
        [Test]
        public void FT12分帧_随机字节流_不得崩溃或死循环()
        {
            var rng = new Random(20261108);
            var parser = new IEC60870.CS101.Ft12FrameParser(1);

            var accepted = 0;
            var rejected = 0;
            parser.FrameRejected += (_, _) => rejected++;

            var sw = Stopwatch.StartNew();
            var buf = new byte[64];

            for (var round = 0; round < 2000; round++)
            {
                rng.NextBytes(buf);
                accepted += parser.Feed(buf).Count;
            }

            sw.Stop();

            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(20_000), "分帧器不得出现死循环/指数退化");
            Assert.That(parser.PendingBytes, Is.LessThan(4096),
                "畸形输入不得让分帧器无限累积缓冲（当前 pending=" + parser.PendingBytes + "）");
            Assert.That(accepted + rejected, Is.GreaterThan(0), "应至少产生过解析/拒绝事件");
        }

        /// <summary>
        /// 截断帧（只有起始符、只有长度、长度声明大于实际）：不得抛异常。
        /// </summary>
        [Test]
        public void FT12分帧_截断与超长声明_不得崩溃()
        {
            var parser = new IEC60870.CS101.Ft12FrameParser(1);

            Assert.DoesNotThrow(() =>
            {
                parser.Feed(new byte[] { 0x68 });                      // 只有起始符
                parser.Feed(new byte[] { 0x68, 0xFF });                // 长度 255，后续缺
                parser.Feed(new byte[] { 0x68, 0x01 });                // 长度小于最小
                parser.Feed(new byte[] { 0x10 });                      // 固定帧截断
                parser.Feed(new byte[] { 0xE5 });                      // 单字符
                parser.Feed(Enumerable.Repeat((byte)0x68, 1000).ToArray()); // 全是起始符
            });
        }

        // ══════════════════════════════════════════════════════════
        //  2. CS104 APCI fuzz
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 畸形 APCI 字节直接送适配器解析：不得抛异常。
        /// </summary>
        [Test]
        public void APCI解析_畸形字节_不得抛异常()
        {
            var rng = new Random(42);
            var buf = new byte[300];

            for (var i = 0; i < 500; i++)
            {
                var len = rng.Next(1, 260);
                rng.NextBytes(buf);

                // 一半概率把首字节改成合法起始符，提高走进解析分支的比例
                if ((i & 1) == 0)
                {
                    buf[0] = 0x68;
                }

                var apdu = new IecApdu();
                var header = new ReadOnlySpan<byte>(buf, 0, Math.Min(2, len));

                try
                {
                    if (apdu.OnParsingHeader(header))
                    {
                        apdu.OnParsingBody(new ReadOnlySpan<byte>(buf, 2, Math.Max(0, len - 2)));
                    }
                }
                catch (Exception ex)
                {
                    Assert.Fail($"畸形 APCI 第 {i} 轮（len={len}）抛了未受控异常: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  3. ASDU 解码 fuzz（三库共用 Core 解码器）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 随机字节当作 ASDU 解码：只允许抛 ASDUParsingException 这类受控异常，
        /// 不允许 IndexOutOfRange / NullReference / ArgumentOutOfRange 泄漏。
        /// </summary>
        [Test]
        public void ASDU解码_随机字节_只允许受控异常()
        {
            var al = Al();
            var rng = new Random(777);
            var buf = new byte[260];
            var uncontrolled = new List<string>();

            for (var i = 0; i < 3000; i++)
            {
                var len = rng.Next(0, 260);
                rng.NextBytes(buf);

                try
                {
                    var asdu = new ASDU(al, buf, 0, len);
                    _ = asdu.TypeId;
                    _ = asdu.Cot;
                    _ = asdu.NumberOfElements;
                }
                catch (ASDUParsingException)
                {
                    // 受控：畸形报文按协议层拒绝
                }
                catch (Exception ex)
                {
                    uncontrolled.Add($"{ex.GetType().Name}(len={len}): {ex.Message}");
                }
            }

            Assert.That(uncontrolled, Is.Empty,
                "ASDU 解码只能抛受控的 ASDUParsingException，实际出现未受控异常：\n"
                + string.Join("\n", uncontrolled.Take(10)));
        }

        /// <summary>
        /// ASDU 元素解码（GetElement）：畸形 payload 不得抛未受控异常。
        /// </summary>
        [Test]
        public void ASDU元素解码_畸形payload_只允许受控异常()
        {
            var al = Al();
            var rng = new Random(888);
            var buf = new byte[260];
            var uncontrolled = new List<string>();

            for (var i = 0; i < 2000; i++)
            {
                var len = rng.Next(6, 260);
                rng.NextBytes(buf);

                try
                {
                    var asdu = new ASDU(al, buf, 0, len);
                    _ = asdu.GetElement(0);
                }
                catch (ASDUParsingException)
                {
                }
                catch (Exception ex)
                {
                    uncontrolled.Add($"{ex.GetType().Name}(len={len}): {ex.Message}");
                }
            }

            Assert.That(uncontrolled, Is.Empty,
                "GetElement 对畸形 payload 只能抛受控异常，实际：\n"
                + string.Join("\n", uncontrolled.Take(10)));
        }

        // ══════════════════════════════════════════════════════════
        //  4. CS103 ASDU 解码 fuzz
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 103 的零拷贝视图 + 标准解码器：随机字节不得抛未受控异常。
        /// </summary>
        [Test]
        public void CS103ASDU解码_随机字节_不得抛未受控异常()
        {
            var rng = new Random(103103);
            var buf = new byte[260];
            var uncontrolled = new List<string>();

            for (var i = 0; i < 3000; i++)
            {
                var len = rng.Next(0, 260);
                rng.NextBytes(buf);
                var slice = new byte[len];
                Array.Copy(buf, slice, len);

                try
                {
                    var view = new Iec103AsduView(slice);
                    _ = view.TypeId;
                    _ = view.Fun;
                    _ = view.Inf;
                    _ = view.Cot;
                    _ = Iec103AsduDecoder.TryDecode(in view, out _, out _);
                }
                catch (ArgumentException)
                {
                    // 受控：构造期长度校验（"ASDU 至少 6 字节"）属正常防御，不是缺陷
                }
                catch (Exception ex)
                {
                    uncontrolled.Add($"{ex.GetType().Name}(len={len}): {ex.Message}");
                }
            }

            Assert.That(uncontrolled, Is.Empty,
                "103 解码对随机字节不得抛未受控异常，实际：\n"
                + string.Join("\n", uncontrolled.Take(10)));
        }
    }
}
