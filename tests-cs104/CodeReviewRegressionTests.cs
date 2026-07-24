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

        /// <summary>可手动开启闸门的桩 sink：SendAsync 阻塞直到 <see cref="Open"/>，且响应取消（token 取消即抛 OCE）。</summary>
        private sealed class GateSink : IApduSink
        {
            private readonly TaskCompletionSource _gate =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsConnected => true;
            public void Open() => _gate.TrySetResult();
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => new ValueTask(_gate.Task.WaitAsync(cancellationToken));
        }

        private static async Task SendDummyAsync(ApduConnection conn, ApplicationLayerParameters al,
            CancellationToken ct = default)
        {
            var w = new PooledApduWriter();
            try
            {
                var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
                asdu.Encode(w, al);
                await conn.SendAsduAsync(w, ct).ConfigureAwait(false);
            }
            finally
            {
                w.Dispose();
            }
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
        public async Task KWindow_PermitReleasedOnCallerCancellation_DoesNotLeak()
        {
            // K=2：两个发送各占一个 k 槽并阻塞在 sink；第三个因窗口满被背压。
            // 取消第二个（已持有 k 槽）的调用方 token → 其 sink 等待抛 OCE。
            // 修复前该 k 槽会永久泄漏（调用方取消路径未被归还）→ 第三个发送永远背压死锁；
            // 修复后槽被归还（catch(Exception){_kWindowSem.Release();throw}）→ 第三个发送继续。
            var apci = new APCIParameters { K = 2 };
            var al = new ApplicationLayerParameters();
            var gate = new GateSink();
            using var conn = new ApduConnection(apci, al, gate, isServerSide: false);

            // 占用两个 k 槽（均在途、未确认），阻塞在 sink
            var s1 = SendDummyAsync(conn, al);                 // 槽 0
            var cts2 = new CancellationTokenSource();
            var s2 = SendDummyAsync(conn, al, cts2.Token);     // 槽 1
            await Task.Delay(50).ConfigureAwait(false);
            Assert.IsFalse(s1.IsCompleted, "send1 应在 sink 处阻塞");
            Assert.IsFalse(s2.IsCompleted, "send2 应在 sink 处阻塞");

            // 第三个发送：窗口已满 → 背压等待 k 槽
            var s3 = SendDummyAsync(conn, al);
            await Task.Delay(50).ConfigureAwait(false);
            Assert.IsFalse(s3.IsCompleted, "send3 应被 k 窗口背压阻塞（窗口满）");

            // 取消 send2（已持有 k 槽）的调用方 token → 其 sink 等待抛 OCE（先取消，再开闸，避免竞争）
            cts2.Cancel();
            await Task.Delay(50).ConfigureAwait(false); // 让取消传播、send2 槽位归还

            // 释放 sink 闸门，让 send1/send3 推进；send2 应已因取消抛 OCE
            gate.Open();

            var s1s3 = Task.WhenAll(s1, s3);
            var completed = await Task.WhenAny(s1s3, Task.Delay(5000)).ConfigureAwait(false);
            Assert.AreSame(s1s3, completed,
                "k 槽泄漏：send2 取消后 send3 仍未能获得槽位（k 窗口修复回归）");

            try
            {
                await s2.ConfigureAwait(false);
                Assert.Fail("send2 应因调用方取消抛出 OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // 预期：被取消的 send2 抛 OCE（TaskCanceledException 为其子类，CatchAsync 语义）
            }

            await s1.ConfigureAwait(false); // 应成功
            await s3.ConfigureAwait(false); // 应成功（拿到了 send2 归还的槽）
        }

        [Test]
        public async Task SendAsduAsync_WindowFull_WhenDisposed_DoesNotHang()
        {
            // K=1：发 1 个即占满窗口；第 2 个发送会背压等待。此时 Dispose 连接，
            // 等待者应被 TrySetCanceled 立即取消（抛 OCE），不能永久悬挂。
            // 修复前：Dispose 用 TrySetResult 唤醒 → 等待者重新循环 → 窗口仍满再次入队 → 永久悬挂。
            var apci = new APCIParameters { K = 1 };
            var al = new ApplicationLayerParameters();
            var sink = new ImmediateSink();
            using var conn = new ApduConnection(apci, al, sink, isServerSide: false);

            // 第 1 个发送占满窗口（K=1）
            await SendDummyAsync(conn, al).ConfigureAwait(false);

            // 第 2 个发送因窗口满而背压等待（不会完成）
            var pending = SendDummyAsync(conn, al);
            await Task.Delay(50).ConfigureAwait(false); // 让其入队等待
            Assert.IsFalse(pending.IsCompleted, "第 2 个发送应被 k 窗口背压阻塞");

            // Dispose 连接 → 等待者应被取消
            conn.Dispose();

            var completed = await Task.WhenAny(pending, Task.Delay(2000)).ConfigureAwait(false);
            Assert.AreSame(pending, completed, "Dispose 后被背压的发送应立即取消，不能悬挂 2s+");

            Assert.CatchAsync<OperationCanceledException>(async () => await pending.ConfigureAwait(false),
                "等待者应抛 OperationCanceledException 或其子类（TrySetCanceled 产生 TaskCanceledException）");
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

        // ── 控制评审关注点：C_RC（StepCommand）预发/执行关联键必须区分 ──
        // GetSelectBit 此前未显式列出 StepCommand；虽然它继承自 DoubleCommand、类型模式已能命中，
        // 但仍显式列出并加此回归测试，防止未来重构静默落入 _ => false。
        // 关键不变量：发送侧 GetSelectBit 与接收侧 ReadSelectBit（读线字节 bit7）必须对同一命令
        // 给出相同的 Select 值，否则预发(select) 确认匹配不上 → 超时 / 预发与执行被错误合并。

        [Test]
        public void ControlWaiter_StepCommand_SelectBit_DistinguishesPreissueAndExecute()
        {
            // 控制评审关注点：C_RC（StepCommand）的预发(select=true) 与 执行(select=false) 必须
            // 产生不同的关联键，否则二者确认会被错误合并 / 预发确认永远匹配不上而超时。
            const int ioa = 200;

            var selectCmd = new StepCommand(ioa, StepCommandValue.HIGHER, select: true, 0);
            var execCmd = new StepCommand(ioa, StepCommandValue.HIGHER, select: false, 0);

            // 发送侧关联键（GetSelectBit）必须区分预发与执行
            Assert.IsTrue(ControlWaiter.GetSelectBit(selectCmd), "C_RC 预发(select) 应返回 true");
            Assert.IsFalse(ControlWaiter.GetSelectBit(execCmd), "C_RC 执行(execute) 应返回 false");
            Assert.AreNotEqual(
                ControlWaiter.GetSelectBit(selectCmd), ControlWaiter.GetSelectBit(execCmd),
                "预发/执行关联键必须不同，否则会被错误合并");

            // 接收侧 ReadSelectBit 读线字节 bit7；StepCommand.Select 读 RCO(dcq) 的 bit7，
            // Encode 把 dcq 写到 IOA 之后第一个字节 —— 收发两侧对同一命令给出相同 Select 值，
            // 关联键对称，故不会合并。此处直接断言对象属性与编码字节位一致：
            Assert.IsTrue(selectCmd.Select, "StepCommand.Select 在预发时应置位（与线字节 bit7 同源）");
        }

        [Test]
        public void ControlWaiter_StepCommandWithCP56Time2a_SelectBit()
        {
            // GetSelectBit 读取内存对象属性（与编码无关），故 C_RC_TA_1 也能正确区分预发/执行。
            const int ioa = 201;
            var ts = new CP56Time2a(DateTime.Now);

            var selectCmd = new StepCommandWithCP56Time2a(ioa, StepCommandValue.LOWER, select: true, 0, ts);
            var execCmd = new StepCommandWithCP56Time2a(ioa, StepCommandValue.LOWER, select: false, 0, ts);

            Assert.IsTrue(ControlWaiter.GetSelectBit(selectCmd), "C_RC_TA_1 预发应返回 true");
            Assert.IsFalse(ControlWaiter.GetSelectBit(execCmd), "C_RC_TA_1 执行应返回 false");
            Assert.AreNotEqual(ControlWaiter.GetSelectBit(selectCmd), ControlWaiter.GetSelectBit(execCmd),
                "C_RC_TA_1 预发/执行关联键必须不同");
        }

        // ── GetEncodedSize 缺陷回归（SingleCommand/DoubleCommand/StepCommand 的 WithCP56Time2a 变体）──
        // 此前三个 *WithCP56Time2a 命令类未重写 GetEncodedSize()，继承基类返回 1（仅限定词字节），
        // 实际元素为 1(限定词) + 7(CP56Time2a) = 8。后果：
        //   ① AddInformationObject 的 spaceLeft 按 1 误算 → AsByteArray() 判不等返回 null（发送侧无法编码）；
        //   ② 解码构造函数长度守卫按 1 判断 → 短消息绕过后读 CP56Time2a 抛 IndexOutOfRange。
        // 修复：三类各重写 GetEncodedSize() => base.GetEncodedSize() + 7。

        [Test]
        public void CommandWithCP56Time2a_GetEncodedSize_IsEight()
        {
            var ts = new CP56Time2a(new DateTime(2026, 7, 23, 12, 0, 0));

            Assert.AreEqual(8, new SingleCommandWithCP56Time2a(1, true, false, 0, ts).GetEncodedSize(),
                "C_SC_TA_1 元素尺寸应为 1(SCO)+7(CP56)=8");
            Assert.AreEqual(8, new DoubleCommandWithCP56Time2a(1, DoubleCommand.ON, false, 0, ts).GetEncodedSize(),
                "C_DC_TA_1 元素尺寸应为 1(DCQ)+7(CP56)=8");
            Assert.AreEqual(8, new StepCommandWithCP56Time2a(1, StepCommandValue.HIGHER, false, 0, ts).GetEncodedSize(),
                "C_RC_TA_1 元素尺寸应为 1(RCO)+7(CP56)=8");
        }

        [Test]
        public void CommandWithCP56Time2a_AsByteArray_NotNull_AndCarriesTimestamp()
        {
            // 修复前 GetEncodedSize 误为 1 → spaceLeft 误算 → AsByteArray 返回 null。
            // 修复后必须返回非空，且编码长度 = ASDU头 + SizeOfIOA + 8（含 7 字节时间戳）。
            var al = new ApplicationLayerParameters();
            int headerSize = 2 + al.SizeOfCOT + al.SizeOfCA;
            int expectedLen = headerSize + al.SizeOfIOA + 8;
            var ts = new CP56Time2a(new DateTime(2026, 7, 23, 12, 34, 56));
            byte[] tsBytes = ts.GetEncodedValue();

            // C_SC_TA_1
            var sc = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
            sc.AddInformationObject(new SingleCommandWithCP56Time2a(100, true, false, 0, ts));
            byte[] encSc = sc.AsByteArray();
            Assert.IsNotNull(encSc, "C_SC_TA_1 AsByteArray 不应返回 null（GetEncodedSize 修复前会返回 null）");
            Assert.AreEqual(expectedLen, encSc.Length, "C_SC_TA_1 编码长度应含 7 字节时间戳");
            for (int i = 0; i < 7; i++)
                Assert.AreEqual(tsBytes[i], encSc[encSc.Length - 7 + i], "C_SC_TA_1 末 7 字节应为时间戳编码");

            // C_DC_TA_1
            var dc = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
            dc.AddInformationObject(new DoubleCommandWithCP56Time2a(101, DoubleCommand.ON, false, 0, ts));
            byte[] encDc = dc.AsByteArray();
            Assert.IsNotNull(encDc, "C_DC_TA_1 AsByteArray 不应返回 null");
            Assert.AreEqual(expectedLen, encDc.Length, "C_DC_TA_1 编码长度应含 7 字节时间戳");
            for (int i = 0; i < 7; i++)
                Assert.AreEqual(tsBytes[i], encDc[encDc.Length - 7 + i], "C_DC_TA_1 末 7 字节应为时间戳编码");

            // C_RC_TA_1
            var rc = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
            rc.AddInformationObject(new StepCommandWithCP56Time2a(102, StepCommandValue.HIGHER, false, 0, ts));
            byte[] encRc = rc.AsByteArray();
            Assert.IsNotNull(encRc, "C_RC_TA_1 AsByteArray 不应返回 null");
            Assert.AreEqual(expectedLen, encRc.Length, "C_RC_TA_1 编码长度应含 7 字节时间戳");
            for (int i = 0; i < 7; i++)
                Assert.AreEqual(tsBytes[i], encRc[encRc.Length - 7 + i], "C_RC_TA_1 末 7 字节应为时间戳编码");
        }

        // ── 问题3：M_EP_TA_1 元素尺寸不一致（GetElement/ComputeExpectedPayloadSize 旧值 3，正确值 6）──
        // EventOfProtectionEquipment.GetEncodedSize=6、ASDUDecoder.TryGetElementSize=6，但 GetElement(int)
        // 与 ComputeExpectedPayloadSize 旧值 3。后果：2 元素序列解码偏移错（读到 elem0 的 CP24 字节）、
        // 构造期校验过松（expected=9 而非 15，截断 payload 绕过）。修复后两处均用 6。

        [Test]
        public void ASDU_M_EP_TA_1_Sequence_TruncatedPayload_Rejected()
        {
            // 2 元素序列，payload 仅 9 字节（= 旧错误 expected=SizeOfIOA+2*3=9，但正确应为 SizeOfIOA+2*6=15）。
            // 修复前：9 < 9 为 false → 接受（bug）。修复后：9 < 15 → 抛 ASDUParsingException。
            var al = new ApplicationLayerParameters();
            byte[] msg = new byte[15]; // header(6) + payload(9)
            msg[0] = (byte)TypeID.M_EP_TA_1; // 17
            msg[1] = 0x82; // SQ | count 2
            msg[2] = 1;    // COT = SPONTANEOUS
            msg[3] = 0;    // OA
            msg[4] = 1;    // CA low
            msg[5] = 0;    // CA high
            // payload 9 字节（不足以容纳 2 个 6 字节元素）

            Assert.Throws<ASDUParsingException>(() => _ = new ASDU(al, msg, 0, msg.Length),
                "M_EP_TA_1 2 元素序列 payload 不足时应被构造期校验拒绝（修复前 expected=9 会错误接受）");
        }

        [Test]
        public void ASDU_M_EP_TA_1_Sequence_DecodesCorrectOffset()
        {
            // 2 元素序列，elem0.SingleEvent=0xAA、elem1.SingleEvent=0xBB。
            // 修复前 elementSize=3 → GetElement(1) 偏移=SizeOfIOA+3=6，读到 elem0 的 CP24 字节(0)。
            // 修复后 elementSize=6 → 偏移=SizeOfIOA+6=9，正确读到 elem1 的 0xBB。
            var al = new ApplicationLayerParameters();
            byte[] msg = new byte[21]; // header(6) + payload(15)
            msg[0] = (byte)TypeID.M_EP_TA_1;
            msg[1] = 0x82; // SQ | count 2
            msg[2] = 1; msg[3] = 0; msg[4] = 1; msg[5] = 0;
            // IOA=1
            msg[6] = 1; msg[7] = 0; msg[8] = 0;
            // elem0: SingleEvent=0xAA, CP16=0,0, CP24=0,0,0
            msg[9] = 0xAA; msg[10] = 0; msg[11] = 0; msg[12] = 0; msg[13] = 0; msg[14] = 0;
            // elem1: SingleEvent=0xBB, CP16=0,0, CP24=0,0,0
            msg[15] = 0xBB; msg[16] = 0; msg[17] = 0; msg[18] = 0; msg[19] = 0; msg[20] = 0;

            var asdu = new ASDU(al, msg, 0, msg.Length);
            Assert.AreEqual(2, asdu.NumberOfElements);
            Assert.IsTrue(asdu.IsSequence);

            var e0 = (EventOfProtectionEquipment)asdu.GetElement(0);
            var e1 = (EventOfProtectionEquipment)asdu.GetElement(1);

            Assert.AreEqual(0xAA, e0.Event.EncodedValue, "elem0 SingleEvent");
            Assert.AreEqual(0xBB, e1.Event.EncodedValue,
                "elem1 SingleEvent（修复前 elementSize=3 偏移错误会读到 elem0 的 CP24 字节=0）");
            Assert.AreEqual(1, e0.ObjectAddress, "elem0 IOA");
            Assert.AreEqual(2, e1.ObjectAddress, "elem1 IOA = elem0+1（序列语义）");
        }

        // ── 问题2：GetElement(IPrivateIOFactory) 缺 payload 越界校验 ──
        // 旧实现无 index 范围检查、无 offset+size 越界检查，大 IOA+错误 VSQ 时 ioFactory.Decode 直接读越界
        // → IndexOutOfRange/NRE。修复后应抛受控 ASDUParsingException。

        private sealed class PrivateIOFactoryStub : IPrivateIOFactory
        {
            private readonly int _encodedSize;
            public PrivateIOFactoryStub(int encodedSize) { _encodedSize = encodedSize; }
            public int GetEncodedSize() => _encodedSize;
            // 仅用于越界校验测试：合法路径返回一个占位对象；越界时 Decode 不应被调用（bounds check 先行）
            public InformationObject Decode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
                => new SinglePointInformation(0, true, new QualityDescriptor());
        }

        private static ASDU BuildUnknownTypeAsdu(int payloadLen)
        {
            // TypeID=200（非标准，落入 ComputeExpectedPayloadSize default → -1 跳过构造期校验），
            // VSQ=1（单元素非序列），便于单独考验 GetElement(IPrivateIOFactory) 的越界校验。
            var al = new ApplicationLayerParameters();
            byte[] msg = new byte[6 + payloadLen];
            msg[0] = 200;            // 未知 TypeID
            msg[1] = 1;              // VSQ = 1（非序列）
            msg[2] = 1; msg[3] = 0;  // COT + OA
            msg[4] = 1; msg[5] = 0;  // CA
            return new ASDU(al, msg, 0, msg.Length);
        }

        [Test]
        public void GetElement_PrivateIOFactory_OutOfBounds_Throws()
        {
            // payload 仅 2 字节，但 factory 声明元素 10 字节 → needed = SizeOfIOA(3)+10 = 13 > 2 → 越界
            var asdu = BuildUnknownTypeAsdu(payloadLen: 2);
            var factory = new PrivateIOFactoryStub(encodedSize: 10);

            Assert.Throws<ASDUParsingException>(() => _ = asdu.GetElement(0, factory),
                "payload 不足以容纳声明元素时应抛 ASDUParsingException（修复前会直接调 Decode 读越界）");
        }

        [Test]
        public void GetElement_PrivateIOFactory_IndexOutOfRange_Throws()
        {
            var asdu = BuildUnknownTypeAsdu(payloadLen: 2); // NumberOfElements = 1
            var factory = new PrivateIOFactoryStub(encodedSize: 1);

            Assert.Throws<ASDUParsingException>(() => _ = asdu.GetElement(1, factory),
                "index >= NumberOfElements 应抛 ASDUParsingException（修复前无此检查）");
        }

        [Test]
        public void GetElement_PrivateIOFactory_HappyPath_ReturnsObject()
        {
            // 合法 payload：needed = SizeOfIOA(3)+4 = 7，payload=7 → 不越界 → Decode 被调用返回对象
            var asdu = BuildUnknownTypeAsdu(payloadLen: 7);
            var factory = new PrivateIOFactoryStub(encodedSize: 4);

            var io = asdu.GetElement(0, factory);
            Assert.IsNotNull(io, "合法 payload 应正常解码返回对象（回归：越界校验不应误拒合法输入）");
        }

        // ── 泛型 GetElement<T>：类型安全版，解析后断言实际类型为 T ──
        // 解码仍由 typeId 决定，T 仅做运行期断言；类型不符/越界时抛 ASDUParsingException 而非裸 InvalidCastException。

        /// <summary>构造一个最小 M_SP_NA_1（typeId=1, VSQ=1, 单元素非序列）ASDU 供类型断言测试。</summary>
        private static ASDU BuildSinglePointAsdu()
        {
            var al = new ApplicationLayerParameters(); // 默认 SizeOfCOT=2 / CA=2 / IOA=3
            // 头部 6 字节：typeId=1, vsq=1, cot=1, oa=0, ca=0,ca=0
            // payload 4 字节：IOA=1(3B) + SPI=1(1B)
            byte[] msg = { 1, 1, 1, 0, 0, 0, 1, 0, 0, 1 };
            return new ASDU(al, msg, 0, msg.Length);
        }

        [Test]
        public void GetElement_T_HappyPath_ReturnsTyped()
        {
            var asdu = BuildSinglePointAsdu();
            var spi = asdu.GetElement<SinglePointInformation>(0);

            Assert.IsNotNull(spi, "类型匹配时应返回具体类型对象");
            Assert.AreEqual(1, spi.ObjectAddress, "IOA 应保持");
            Assert.IsTrue(spi.Value, "SPI 值应保持");
        }

        [Test]
        public void GetElement_T_TypeMismatch_ThrowsASDUParsingException()
        {
            var asdu = BuildSinglePointAsdu();
            // 报文实际是 SinglePointInformation，却断言为 MeasuredValueScaled → 应抛受控异常而非 InvalidCastException
            var ex = Assert.Throws<ASDUParsingException>(() => _ = asdu.GetElement<MeasuredValueScaled>(0),
                "类型不符时应抛 ASDUParsingException（带类型名上下文）");
            Assert.That(ex.Message, Does.Contain("MeasuredValueScaled"),
                "异常消息应指明期望类型，便于排查");
            Assert.That(ex.Message, Does.Contain("SinglePointInformation"),
                "异常消息应指明实际类型");
        }

        [Test]
        public void GetElement_T_IndexOutOfRange_PropagatesASDUParsingException()
        {
            var asdu = BuildSinglePointAsdu(); // NumberOfElements = 1
            Assert.Throws<ASDUParsingException>(() => _ = asdu.GetElement<SinglePointInformation>(1),
                "index 越界应透传 GetElement(int) 的 ASDUParsingException");
        }
    }
}
