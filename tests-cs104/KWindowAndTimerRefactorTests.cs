/*
 *  KWindowAndTimerRefactorTests.cs
 *
 *  针对 v5 重构中两项核心改动的专项单元测试：
 *  1. K 窗口背压：SemaphoreSlim + 每帧 LinkedCts → _kLock 保护计数 + 等待者 TCS 队列。
 *     覆盖：逐个确认逐个唤醒（槽位转移语义）、背压等待者取消（留队 + 链式归还，
 *     槽位不泄漏）、Dispose 唤醒全部等待者、发送失败归还槽位。
 *  2. T1/T2/T3 定时器：固定 100ms 轮询 → GetSuggestedDelayMs 动态截止时刻计算。
 *     覆盖：空闲上限钳制、T3 即将到期下限钳制、T1 未确认 I 帧截止、
 *     T2 收到 I 帧后的延迟确认截止、T3 到期发 TESTFR_ACT 并武装 U 帧超时。
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class KWindowAndTimerRefactorTests
    {
        private ApplicationLayerParameters _al;

        [SetUp]
        public void SetUp()
        {
            _al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };
        }

        private static APCIParameters MakeParams(int k = 12, int t1 = 15, int t2 = 2, int t3 = 20)
        {
            return new APCIParameters { K = k, W = k / 2, T0 = 7, T1 = t1, T2 = t2, T3 = t3 };
        }

        /// <summary>立即完成发送的桩 sink（不阻塞，用于制造 k 窗口满窗场景）。</summary>
        private sealed class ImmediateSink : IApduSink
        {
            public bool IsConnected => true;
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => default;
        }

        /// <summary>发送必然失败的桩 sink：验证发送异常时 k 槽位被归还。</summary>
        private sealed class FailingSink : IApduSink
        {
            public bool IsConnected => true;
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => new ValueTask(Task.FromException(new IOException("simulated transport failure")));
        }

        /// <summary>记录发送帧的桩 sink：验证 S/U 帧内容与定时器触发的自动发送。</summary>
        private sealed class RecordingSink : IApduSink
        {
            public ConcurrentQueue<byte[]> Frames { get; } = new ConcurrentQueue<byte[]>();
            public bool IsConnected => true;
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            {
                Frames.Enqueue(apdu.ToArray());
                return default;
            }
        }

        private static async Task SendDummyAsync(ApduConnection conn, ApplicationLayerParameters al,
            CancellationToken ct = default)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
            await conn.SendAsduAsync(asdu, ct).ConfigureAwait(false);
        }

        private static async Task WaitSettledAsync() => await Task.Delay(80).ConfigureAwait(false);

        // ═════════════════════════════════════════════════════════════
        //  K 窗口背压（计数 + 等待者 TCS 队列）
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// 槽位转移语义：逐个 S 确认应逐个唤醒等待者，一次确认只放行一个，
        /// 未确认的等待者保持背压（避免 thundering herd）。
        /// </summary>
        [Test]
        public async Task KWindow_IndividualAcks_WakeWaitersOneByOne()
        {
            using var conn = new ApduConnection(MakeParams(k: 2), _al, new ImmediateSink());

            // 占满窗口（K=2）
            await SendDummyAsync(conn, _al);
            await SendDummyAsync(conn, _al);

            // 3 个背压等待者入队
            var s3 = SendDummyAsync(conn, _al);
            var s4 = SendDummyAsync(conn, _al);
            var s5 = SendDummyAsync(conn, _al);
            await WaitSettledAsync();
            Assert.That(s3.IsCompleted, Is.False, "窗口满时新发送必须背压");
            Assert.That(s4.IsCompleted, Is.False);
            Assert.That(s5.IsCompleted, Is.False);

            // 逐个确认（N(R)=1..3），每个只唤醒队头一个等待者
            Assert.That(conn.OnSFrame(1), Is.True);
            await WaitSettledAsync();
            Assert.That(s3.IsCompleted, Is.True, "第一次确认应恰好唤醒第一个等待者");
            Assert.That(s4.IsCompleted, Is.False, "其余等待者必须继续背压");
            Assert.That(s5.IsCompleted, Is.False);

            Assert.That(conn.OnSFrame(2), Is.True);
            await WaitSettledAsync();
            Assert.That(s4.IsCompleted, Is.True);
            Assert.That(s5.IsCompleted, Is.False);

            Assert.That(conn.OnSFrame(3), Is.True);
            await WaitSettledAsync();
            Assert.That(s5.IsCompleted, Is.True);
        }

        /// <summary>
        /// 背压等待者取消的新语义：取消者留在等待队列（TCS 队列无法 O(1) 任意出队），
        /// 待槽位转移给它时立即归还并链式推进给后续等待者，再抛 OCE——槽位永不泄漏。
        /// </summary>
        [Test]
        public async Task KWindow_BackpressuredWaiter_Cancellation_DoesNotLeakSlots()
        {
            using var conn = new ApduConnection(MakeParams(k: 1), _al, new ImmediateSink());

            // 唯一槽位被占用
            await SendDummyAsync(conn, _al);

            // 背压等待者带 token 入队后取消
            var cts2 = new CancellationTokenSource();
            var s2 = SendDummyAsync(conn, _al, cts2.Token);
            await WaitSettledAsync();
            Assert.That(s2.IsCompleted, Is.False, "窗口满必须背压");

            cts2.Cancel();
            await WaitSettledAsync();
            Assert.That(s2.IsCanceled, Is.False, "取消者按设计留在等待队列，转移时才感知取消");

            // 对端确认 → 槽位转移给已取消的等待者 → 其归还槽位并抛 OCE
            Assert.That(conn.OnSFrame(1), Is.True);
            await WaitSettledAsync();
            Assert.That(s2.IsCanceled, Is.True, "被转移后应感知调用方取消并抛 OCE");

            // 归还的槽位必须可被后续发送推进（不泄漏）
            var s3 = SendDummyAsync(conn, _al);
            await WaitSettledAsync();
            Assert.That(s3.IsCompleted, Is.True, "取消者归还的槽位应可被后续发送获取");
        }

        /// <summary>Dispose 时全部（而非单个）k 窗口等待者被唤醒并以 OCE 失败，不悬挂。</summary>
        [Test]
        public async Task KWindow_Dispose_WakesAllWaiters()
        {
            var conn = new ApduConnection(MakeParams(k: 2), _al, new ImmediateSink());

            await SendDummyAsync(conn, _al);
            await SendDummyAsync(conn, _al);

            var w1 = SendDummyAsync(conn, _al);
            var w2 = SendDummyAsync(conn, _al);
            var w3 = SendDummyAsync(conn, _al);
            await WaitSettledAsync();

            conn.Dispose();

            await WaitSettledAsync();
            foreach (var (name, task) in new[] { ("w1", w1), ("w2", w2), ("w3", w3) })
            {
                Assert.That(task.IsCanceled, Is.True, $"等待者 {name} 应被 Dispose 以 OCE 唤醒");
                Assert.ThrowsAsync<OperationCanceledException>(async () => await task.ConfigureAwait(false));
            }
        }

        /// <summary>发送异常（传输失败）时在途槽位必须归还，后续发送不被饿死。</summary>
        [Test]
        public async Task KWindow_SendFailure_ReleasesSlot()
        {
            using var conn = new ApduConnection(MakeParams(k: 1), _al, new FailingSink());

            Assert.ThrowsAsync<IOException>(async () => await SendDummyAsync(conn, _al).ConfigureAwait(false));

            // 失败释放的槽位应立即可用（K=1）：后续发送直接推进
            var s2 = SendDummyAsync(conn, _al);
            await WaitSettledAsync();
            Assert.That(s2.IsCompleted, Is.True, "发送失败的槽位应被归还，后续发送可推进");
        }

        // ═════════════════════════════════════════════════════════════
        //  T1/T2/T3 动态定时间隔（GetSuggestedDelayMs）
        // ═════════════════════════════════════════════════════════════

        /// <summary>仅 T3 活跃且截止尚远（20s）时，返回值应钳制到上限 250ms（按需唤醒而非空转）。</summary>
        [Test]
        public void GetSuggestedDelayMs_Idle_ReturnsUpperBound()
        {
            using var conn = new ApduConnection(MakeParams(t3: 20), _al, new ImmediateSink());
            Assert.That(conn.GetSuggestedDelayMs(), Is.EqualTo(250));
        }

        /// <summary>T3 即将到期（T3=0）时返回下限 10ms，保证测试帧按期发出。</summary>
        [Test]
        public void GetSuggestedDelayMs_T3Expiring_ReturnsLowerBound()
        {
            using var conn = new ApduConnection(MakeParams(t3: 0), _al, new ImmediateSink());
            Assert.That(conn.GetSuggestedDelayMs(), Is.EqualTo(10));
        }

        /// <summary>存在未确认 I 帧且 T1 截止已到（T1=0）时，T1 deadline 主导返回下限；
        /// 且 CheckTimeoutsAsync 应判定为致命超时（返回 false，传输层关连接）。</summary>
        [Test]
        public async Task GetSuggestedDelayMs_PendingIFrame_T1DeadlineDominates()
        {
            using var conn = new ApduConnection(MakeParams(t1: 0), _al, new ImmediateSink());

            await SendDummyAsync(conn, _al); // 登记最老未确认 I 帧

            Assert.That(conn.GetSuggestedDelayMs(), Is.EqualTo(10),
                "T1 截止时刻应使建议等待收缩到下限");
            Assert.That(await conn.CheckTimeoutsAsync(CancellationToken.None).ConfigureAwait(false), Is.False,
                "T1 到期应报告致命超时");
        }

        /// <summary>收到 I 帧后 T2（延迟确认）开始计时；T2=0 时其截止时刻主导返回下限。</summary>
        [Test]
        public async Task GetSuggestedDelayMs_T2Deadline_AfterIFrameReceipt()
        {
            using var conn = new ApduConnection(MakeParams(t2: 0), _al, new ImmediateSink());

            await SendDummyAsync(conn, _al);                  // 在途 SeqNo=1
            Assert.That(conn.OnIFrame(0, 1, ReadOnlySpan<byte>.Empty), Is.True,
                "对端回显 N(R)=1 应确认在途帧");

            // T2 触发：_t2Triggered=true、_lastConfirmationTime=now、T2=0 → 截止即 now
            Assert.That(conn.GetSuggestedDelayMs(), Is.EqualTo(10),
                "T2 延迟确认截止应主导建议等待");
        }

        /// <summary>T3 到期行为闭环：CheckTimeoutsAsync 发出 TESTFR_ACT（U 帧走 sink），
        /// 并武装 U 帧 T1 超时（T1=0 时其截止主导后续建议等待）。</summary>
        [Test]
        public async Task CheckTimeouts_T3Due_SendsTestFrAct_AndArmsUTimeout()
        {
            var sink = new RecordingSink();
            using var conn = new ApduConnection(MakeParams(t3: 0, t1: 0), _al, sink);

            // 等时钟推进越过 T3 截止（T3 检查为严格大于）
            await Task.Delay(50).ConfigureAwait(false);

            Assert.That(await conn.CheckTimeoutsAsync(CancellationToken.None).ConfigureAwait(false), Is.True);
            Assert.That(sink.Frames.Count, Is.EqualTo(1), "T3 到期应发出 TESTFR_ACT");

            Assert.That(sink.Frames.TryDequeue(out var frame), Is.True);
            Assert.That(frame[0], Is.EqualTo(0x68));
            Assert.That(frame[2] & 0x03, Is.EqualTo(0x03), "U 帧控制域低两位必须为 1");
            Assert.That(frame[2], Is.EqualTo(ApduCodec.TestFrAct), "U 功能码应为 TESTFR_ACT");

            // U 帧超时已武装（T1=0 → 截止即 now）→ 后续建议等待应为下限
            Assert.That(conn.GetSuggestedDelayMs(), Is.EqualTo(10),
                "武装后的 U 帧 T1 截止应主导建议等待");
        }

        // ═════════════════════════════════════════════════════════════
        //  Iec104TimerScheduler（单 Timer 自调度驱动器）
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查返回 false（致命超时）时，调度器恰好触发一次 <c>onFatal</c> 并停止重排。
        /// </summary>
        [Test]
        public async Task Scheduler_FatalCheck_TriggersOnFatalOnce()
        {
            var fatalCount = 0;
            var checkCount = 0;
            using (var scheduler = new Iec104TimerScheduler(
                       token =>
                       {
                           checkCount++;
                           return ValueTask.FromResult(false);
                       },
                       () => 10,
                       () => fatalCount++,
                       CancellationToken.None))
            {
                await WaitForAsync(() => fatalCount > 0).ConfigureAwait(false);
            }

            Assert.That(fatalCount, Is.EqualTo(1), "致命超时只能触发一次 onFatal");
            // 致命路径不重排，检查次数应保持很小（第一次检查即致命）
            await Task.Delay(60).ConfigureAwait(false);
            Assert.That(checkCount, Is.LessThanOrEqualTo(3), "致命后不得继续轮询");
        }

        /// <summary>
        /// 检查抛 OperationCanceledException（连接终止令牌取消）时调度器静默停止，
        /// 不触发 onFatal、不抛异常。
        /// </summary>
        [Test]
        public async Task Scheduler_Cancellation_StopsSilently()
        {
            var cts = new CancellationTokenSource();
            var fatalCount = 0;
            using (var scheduler = new Iec104TimerScheduler(
                       token =>
                       {
                           cts.Cancel(); // 模拟连接终止：下一次检查感知取消
                           token.ThrowIfCancellationRequested();
                           return ValueTask.FromResult(true);
                       },
                       () => 10,
                       () => fatalCount++,
                       cts.Token))
            {
                await Task.Delay(80).ConfigureAwait(false);
            }

            Assert.That(fatalCount, Is.EqualTo(0), "取消不得触发 onFatal");
        }

        /// <summary>
        /// 非致命异常（如 sink 写入噪声）按瞬态处理：不触发 onFatal、不崩溃，
        /// 并按 delayProvider 继续重排。
        /// </summary>
        [Test]
        public async Task Scheduler_TransientError_KeepsScheduling()
        {
            var checkCount = 0;
            var fatalCount = 0;
            using (var scheduler = new Iec104TimerScheduler(
                       token =>
                       {
                           checkCount++;
                           if (checkCount == 1)
                           {
                               throw new InvalidOperationException("writer was completed");
                           }

                           return ValueTask.FromResult(true);
                       },
                       () => 10,
                       () => fatalCount++,
                       CancellationToken.None))
            {
                await WaitForAsync(() => checkCount >= 2).ConfigureAwait(false);
            }

            Assert.That(fatalCount, Is.EqualTo(0), "瞬态异常不得触发 onFatal");
            Assert.That(checkCount, Is.GreaterThanOrEqualTo(2), "瞬态异常后应继续按间隔重排");
        }

        /// <summary>
        /// Dispose 后调度器停止：不再触发检查、不再调用 onFatal（确定性释放，无残留 Timer）。
        /// </summary>
        [Test]
        public async Task Scheduler_Dispose_StopsTimer()
        {
            var checkCount = 0;
            var scheduler = new Iec104TimerScheduler(
                token =>
                {
                    checkCount++;
                    return ValueTask.FromResult(true);
                },
                () => 5,
                () => Assert.Fail("Dispose 后不得触发 onFatal"),
                CancellationToken.None);

            await WaitForAsync(() => checkCount > 0).ConfigureAwait(false);
            var before = checkCount;
            scheduler.Dispose();
            await Task.Delay(80).ConfigureAwait(false);

            Assert.That(checkCount, Is.EqualTo(before), "Dispose 后不得再触发检查");
        }

        // ═════════════════════════════════════════════════════════════
        //  KWindow + SequenceNumbers（提取后的纯数据结构，O(1) 确认校验）
        // ═════════════════════════════════════════════════════════════

        /// <summary>SequenceNumbers.Distance：模 32768 圆上距离基础语义。</summary>
        [Test]
        public void SequenceNumbers_Distance_WrapsModulo()
        {
            Assert.That(SequenceNumbers.Distance(0, 1), Is.EqualTo(1));
            Assert.That(SequenceNumbers.Distance(1, 0), Is.EqualTo(32767), "后退一步距离为 32767");
            Assert.That(SequenceNumbers.Distance(32766, 0), Is.EqualTo(2), "跨零点环绕");
            Assert.That(SequenceNumbers.Distance(32767, 0), Is.EqualTo(1));
            Assert.That(SequenceNumbers.Distance(5, 5), Is.EqualTo(0));
        }

        /// <summary>部分确认：K=4 发送 4 帧（合法区 [0..4]），N(R)=3 确认 seq 0..2 共 3 帧。</summary>
        [Test]
        public void KWindow_PartialAck_ReleasesAckedPrefix()
        {
            var win = new KWindow(4);
            SendN(win, 4);   // 在途 seq 0..3, 登记 SeqNo 1..4

            Assert.That(win.HasUnacked, Is.True);
            Assert.That(win.UnackedCount, Is.EqualTo(4));
            Assert.That(win.Ack(3, true), Is.EqualTo(3), "N(R)=3 应释放 seq 0、1、2 三帧");
            Assert.That(win.UnackedCount, Is.EqualTo(1));
            Assert.That(win.NextSendSeq, Is.EqualTo(4), "N(S) 推进不受确认影响");
        }

        /// <summary>完全确认：N(R)=NextSendSeq 释放全部在途帧，窗口回到空。</summary>
        [Test]
        public void KWindow_FullAck_ClearsWindow()
        {
            var win = new KWindow(4);
            SendN(win, 3);

            Assert.That(win.Ack(3, true), Is.EqualTo(3), "N(R)=下一发送序号 3 应确认全部");
            Assert.That(win.HasUnacked, Is.False);
            Assert.That(win.UnackedCount, Is.EqualTo(0));

            // 窗口空后唯一合法 N(R) = NextSendSeq；再次确认新帧后再全清
            var s = win.TakeSendSeq();
            win.RegisterSent(s, 0);
            Assert.That(win.Ack(4, true), Is.EqualTo(1));
            Assert.That(win.HasUnacked, Is.False);
        }

        /// <summary>冗余确认：N(R)=最旧帧序号（S）表示确认到最旧前一拍，全部在途帧仍未被确认，释放 0。</summary>
        [Test]
        public void KWindow_RedundantAck_ReleasesNothing()
        {
            var win = new KWindow(4);
            SendN(win, 3);   // 在途 seq 0..2, SeqNo 1..3; 最旧帧 N(S)=0

            Assert.That(win.Ack(0, true), Is.EqualTo(0), "N(R)=S（最旧帧自身序号）为冗余确认");
            Assert.That(win.UnackedCount, Is.EqualTo(3));
        }

        /// <summary>越界：N(R) 超出 [S, NextSendSeq] 区间返回 -1（协议错误）。</summary>
        [Test]
        public void KWindow_OutOfRange_ReturnsProtocolError()
        {
            var win = new KWindow(4);
            SendN(win, 3);   // 合法区 [0..3]

            Assert.That(win.Ack(4, true), Is.EqualTo(-1), "超过下一发送序号 → 越界");
            Assert.That(win.Ack(32767, true), Is.EqualTo(-1), "落后于 S 超过区间 → 越界");
        }

        /// <summary>回绕：序列号跨 32767→0 时部分确认仍按模距离正确释放。</summary>
        [Test]
        public void KWindow_Wraparound_PartialAckWorks()
        {
            var win = new KWindow(4);
            // 推进 N(S) 到 32766（不登记条目，仅移动发送游标）
            for (var i = 0; i < 32766; i++)
            {
                win.TakeSendSeq();
            }

            Assert.That(win.NextSendSeq, Is.EqualTo(32766), "推进后 N(S) 应为 32766");

            // 以 32766 起发送 3 帧：N(S)=32766,32767,0（登记 SeqNo 32767,0,1）
            win.RegisterSent(win.TakeSendSeq(), 0);   // seqUsed 32766
            win.RegisterSent(win.TakeSendSeq(), 0);   // seqUsed 32767
            win.RegisterSent(win.TakeSendSeq(), 0);   // seqUsed 0（回绕）
            Assert.That(win.NextSendSeq, Is.EqualTo(1), "三次发送后 N(S) 回绕到 1");
            Assert.That(win.UnackedCount, Is.EqualTo(3));

            // 冗余确认：N(R)=S=32766（最旧帧自身序号）→ 释放 0
            Assert.That(win.Ack(32766, true), Is.EqualTo(0));
            // 部分确认：N(R)=0（确认到 32767 帧）→ 释放 2
            Assert.That(win.Ack(0, true), Is.EqualTo(2));
            Assert.That(win.UnackedCount, Is.EqualTo(1));
            // 完全确认：N(R)=NextSendSeq=1 → 释放剩余 1
            Assert.That(win.Ack(1, true), Is.EqualTo(1));
            Assert.That(win.HasUnacked, Is.False);
        }

        /// <summary>满窗阻塞：K=2 时第 3 帧 TryAcquireSlot 返回 false（需 EnqueueWaiter 背压）。</summary>
        [Test]
        public void KWindow_FullWindow_TryAcquireFails()
        {
            var win = new KWindow(2);
            Assert.That(win.TryAcquireSlot(), Is.True);
            Assert.That(win.TryAcquireSlot(), Is.True);
            Assert.That(win.TryAcquireSlot(), Is.False, "满窗后同步占用必须失败");

            // 释放 1 个（模拟对端确认）后立即可用
            win.ReleaseSlots(1);
            Assert.That(win.TryAcquireSlot(), Is.True);
        }

        /// <summary>Dispose 后：TryAcquireSlot 抛 OCE，排队等待者被取消唤醒。</summary>
        [Test]
        public async Task KWindow_Dispose_WakesWaitersWithCancel()
        {
            var win = new KWindow(1);
            Assert.That(win.TryAcquireSlot(), Is.True);
            var waiter = win.EnqueueWaiter();
            Assert.That(waiter.Task.IsCompleted, Is.False);

            win.Dispose();

            Assert.That(waiter.Task.IsCanceled, Is.True);
            Assert.Throws<OperationCanceledException>(() => win.TryAcquireSlot());
            await Task.Yield();
        }

        /// <summary>槽位转移：ReleaseSlots 优先唤醒队头等待者（占用计数不变），余量才归还计数。</summary>
        [Test]
        public async Task KWindow_ReleaseSlots_TransfersToWaiterFirst()
        {
            var win = new KWindow(1);
            Assert.That(win.TryAcquireSlot(), Is.True);

            var w1 = win.EnqueueWaiter();
            var w2 = win.EnqueueWaiter();
            await Task.Yield();
            Assert.That(w1.Task.IsCompleted, Is.False);
            Assert.That(w2.Task.IsCompleted, Is.False);

            // 释放 2 个：第一个转移给 w1，第二个转移给 w2（占用计数保持 1 不变——
            // 槽位随转移易主，不归还）
            win.ReleaseSlots(2);
            await Task.Yield();
            Assert.That(w1.Task.IsCompleted, Is.True);
            Assert.That(w2.Task.IsCompleted, Is.True);
            Assert.That(win.TryAcquireSlot(), Is.False, "槽位已转移给等待者，计数未归还");
        }

        /// <summary>发送-登记-确认整体闭环：与 ApduConnection 语义一致（条目 SeqNo=N(S)+1）。</summary>
        [Test]
        public void KWindow_SendAckRoundtrip_MatchesApduSemantics()
        {
            var win = new KWindow(8);
            for (var i = 0; i < 5; i++)
            {
                var s = win.TakeSendSeq();
                win.RegisterSent(s, Environment.TickCount64);
            }

            Assert.That(win.NextSendSeq, Is.EqualTo(5));
            Assert.That(win.Ack(3, true), Is.EqualTo(3), "确认 N(R)=3 对应 seq 0..2 三帧");
            Assert.That(win.Ack(5, true), Is.EqualTo(2), "确认 N(R)=5 对应剩余 seq 3..4 两帧");
            Assert.That(win.HasUnacked, Is.False);
        }

        /// <summary>内部辅助：向窗口发送 n 帧（依次 TakeSendSeq + RegisterSent）。</summary>
        private static void SendN(KWindow win, int n)
        {
            for (var i = 0; i < n; i++)
            {
                var s = win.TakeSendSeq();
                win.RegisterSent(s, Environment.TickCount64);
            }
        }

        /// <summary>内部辅助：等待断言条件满足（带超时，避免挂死）。</summary>
        private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!condition())
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("等待调度器状态超时");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }
}
