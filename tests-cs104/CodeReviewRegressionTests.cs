/*
 *  CodeReviewRegressionTests.cs
 *
 *  针对代码评审中修复项的最小回归测试：
 *  #4  CS104 k 窗口背压：并发满窗等待者必须全部被唤醒（旧单字段 _windowWaiter 会孤立除最后一个外的等待者）。
 *  #13 Core CP56Time2a.Equals：按值相等，而非哈希相等。
 *  #14 Core/104 过短 ASDU（头部不全）应被 OnIFrame 拒绝，且不向用户回调派发。
 *  #15 Core ASDU 构造期校验 payload 长度，声明元素数超出实际 payload 时抛异常。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;
using IEC60870.Core.Time;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CodeReviewRegressionTests
    {
        /// <summary>立即完成发送的桩 sink（不阻塞，用于制造 k 窗口满窗场景）。</summary>
        private sealed class ImmediateSink : IApduSink
        {
            public bool IsConnected => true;
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => default;
        }

        private static async Task SendDummyAsync(ApduConnection conn, ApplicationLayerParameters al)
        {
            using var w = new PooledApduWriter();
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
            asdu.Encode(w, al);
            await conn.SendAsduAsync(w, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task KWindowBackpressure_ConcurrentWaiters_AllComplete()
        {
            // 小窗口 K=4，并发发送 K+4=8 个；前 4 个在途后窗口满，其余被背压阻塞。
            // 模拟对端一次确认（N(R)=K 即 SeqNo=4）腾出窗口，必须唤醒全部等待者。
            var apci = new APCIParameters { K = 4 };
            var al = new ApplicationLayerParameters();
            var sink = new ImmediateSink();
            using var conn = new ApduConnection(apci, al, sink, isServerSide: false);

            var tasks = new List<Task>();
            for (int i = 0; i < apci.K + 4; i++)
                tasks.Add(SendDummyAsync(conn, al));

            // 等待首批 K 个进入“在途”缓冲、其余进入背压等待
            await Task.Delay(100).ConfigureAwait(false);

            // 对端确认 N(R)=K（SeqNo=4，确认 N(S)=0..K-1），腾出窗口
            Assert.IsTrue(conn.OnSFrame(apci.K), "对端确认应被接受");

            var all = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(all, Task.Delay(5000)).ConfigureAwait(false);
            if (completed != all)
                Assert.Fail("存在发送者被 k 窗口背压永久孤立（代码评审 #4 回归）");

            await all.ConfigureAwait(false);
        }

        [Test]
        public void CP56Time2a_Equals_UsesValueNotHash()
        {
            var a = new CP56Time2a(new DateTime(2026, 7, 23, 9, 0, 0));
            var b = new CP56Time2a(new DateTime(2026, 7, 23, 9, 0, 0));
            var c = new CP56Time2a(new DateTime(2026, 7, 23, 9, 0, 1));

            Assert.IsTrue(a.Equals(b), "相同时间应相等");
            Assert.IsFalse(a.Equals(c), "相差 1 秒应不相等");
            Assert.IsTrue(a.Equals((object)b), "装箱相等应一致");
            Assert.IsFalse(a.Equals((object)c));
            Assert.IsFalse(a.Equals(null));
            Assert.IsFalse(a.Equals(new object()));
        }

        [Test]
        public void ApduConnection_OnIFrame_ShortAsdu_ReturnsFalse()
        {
            var apci = new APCIParameters();
            var al = new ApplicationLayerParameters();
            var sink = new ImmediateSink();
            using var conn = new ApduConnection(apci, al, sink, isServerSide: true);

            // 头部不全的 ASDU（仅 1 字节），应被拒绝且不向用户回调派发（代码评审 #14）
            bool ok = conn.OnIFrame(0, 0, new byte[] { 1 });
            Assert.IsFalse(ok, "过短 ASDU 应视为协议错误返回 false");
        }

        [Test]
        public void ASDU_MalformedPayload_Throws()
        {
            var al = new ApplicationLayerParameters(); // SizeOfCOT=1, SizeOfCA=1, SizeOfIOA=3

            // TypeID=M_SP_NA_1(1), VSQ=5（声明 5 个元素）, COT=1, CA=1, 之后仅 3 字节 payload
            // 实际 payload 远不足以容纳 5 个 SinglePointInformation（每个需 SizeOfIOA+1=4 字节）。
            byte[] msg = { (byte)TypeID.M_SP_NA_1, 5, (byte)CauseOfTransmission.SPONTANEOUS, 1, 1, 0x01, 0x00, 0x00 };

            Assert.Throws<ASDUParsingException>(() => _ = new ASDU(al, msg, 0, msg.Length),
                "声明元素数超出实际 payload 长度应抛 ASDUParsingException（代码评审 #15）");
        }
    }
}
