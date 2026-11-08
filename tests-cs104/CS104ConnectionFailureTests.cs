/*
 *  CS104ConnectionFailureTests.cs
 *
 *  「链路已死但没人上报」缺陷的回归测试。
 *
 *  背景（500 装置压测暴露，见 examples/104/cs104-stress-500/STRESS-TEST-REPORT.md §2.1）：
 *  真实断链后应用层既收不到 ConnectionClosed、IsActivated 也仍为 true，链路变成
 *  「只能收不能发」的僵尸。根因是三段链路叠加：
 *
 *   1. 发送异常时 ApduConnection 回退 N(S)。但「sink 抛异常」不等于「帧没上线」，
 *      对端确认了被回退的序号 → KWindow.Ack 越界 → 自造协议错误。
 *   2. 协议错误在 TouchSocket 收包回调内部同步 CloseAsync，导致适配器被拆解
 *      （之后每帧都抛 "writer completed"），而套接字仍报 Online、Closed 事件不再触发。
 *   3. ConnectionClosed 只在 OnTcpClosed 里派发 → 上述路径永不派发。
 *
 *  本文件的断言把三条都钉住：
 *   - 发送失败（N(S) 已分配）必须判定连接失效，且必须派发 ConnectionClosed / IsActive=false；
 *   - 对端 N(R) 越界（协议错误）同样必须失效 + 派发事件（原先只记原因不派发）；
 *   - 失效后的发送必须给出可诊断的异常，而不是底层 "writer completed"；
 *   - 失效只派发一次事件（幂等），Dispose 后 IsActive 必须为 false。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS104ConnectionFailureTests
    {
        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        /// <summary>发送必然失败的 sink（模拟传输层写失败）。</summary>
        private sealed class FailingSink : IIec104ApduSink
        {
            public bool IsConnected => true;

            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => new ValueTask(Task.FromException(new IOException("simulated transport failure")));
        }

        /// <summary>
        /// 可控闸门的 sink：SendAsync 把帧写入后挂起，直到 <see cref="Release"/>。
        /// 用来复现「序号已分配、发送尚未完成」这一窗口期。
        /// </summary>
        private sealed class GatedSink : IIec104ApduSink
        {
            private readonly TaskCompletionSource _gate =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool IsConnected => true;
            public int SendCount;
            public void Release() => _gate.TrySetResult();

            public async ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref SendCount);
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>立即成功的 sink。</summary>
        private sealed class ImmediateSink : IIec104ApduSink
        {
            public bool IsConnected => true;
            public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
                => default;
        }

        private static async Task SendDummyAsync(ApduConnection conn, CancellationToken ct = default)
        {
            // 协议门禁：未收到 STARTDT_CON 前禁止发送 I 帧。本文件只验证失效/窗口语义，
            // 未激活时先激活（不要重复激活：会重复派发 Activated 事件）。
            if (!conn.IsActive)
            {
                conn.OnUFrame(ApduCodec.StartDtCon);
            }

            var al = Al();
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
            await conn.SendAsduAsync(asdu, ct).ConfigureAwait(false);
        }

        // ══════════════════════════════════════════════════════════
        //  1. 发送失败 → 必须判定失效 + 必须派发 ConnectionClosed
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 发送失败_必须判定连接失效并派发ConnectionClosed()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new FailingSink());

            var events = new List<ApduConnectionEvent>();
            conn.EventHandler += ev => { lock (events) { events.Add(ev); } };

            var fatalReasons = new List<ConnectionCloseReason>();
            conn.SetFatalHandler(reason => { lock (fatalReasons) { fatalReasons.Add(reason); } });

            // 激活（IsActive 为 true 才有区分度）
            conn.OnUFrame(ApduCodec.StartDtCon);
            Assert.That(conn.IsActive, Is.True, "前置条件：连接应处于激活状态");

            lock (events) { events.Clear(); }   // 丢弃激活阶段事件（StartDtConReceived/Activated）

            var thrown = Assert.ThrowsAsync<IOException>(async () => await SendDummyAsync(conn));
            Assert.That(thrown, Is.Not.Null, "发送失败必须向上抛出（调用方需要知道本帧没成功）");

            Assert.That(conn.IsActive, Is.False, "发送失败后 IsActive 必须为 false（否则应用层看到僵尸链路）");
            Assert.That(conn.CloseReason, Is.EqualTo(ConnectionCloseReason.IoError), "断开原因应标记为 IoError");

            lock (events)
            {
                Assert.That(events, Has.Count.EqualTo(1), "必须派发且只派发一次 ConnectionClosed");
                Assert.That(events[0], Is.EqualTo(ApduConnectionEvent.ConnectionClosed));
            }

            lock (fatalReasons)
            {
                Assert.That(fatalReasons, Is.EqualTo(new[] { ConnectionCloseReason.IoError }),
                    "必须请求宿主关闭传输层");
            }
        }

        [Test]
        public async Task 发送失败后_再次发送必须给出可诊断异常且不再派发事件()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new FailingSink());

            var closedCount = 0;
            conn.EventHandler += ev => { if (ev == ApduConnectionEvent.ConnectionClosed) { Interlocked.Increment(ref closedCount); } };

            Assert.ThrowsAsync<IOException>(async () => await SendDummyAsync(conn));

            // 失效后的发送：必须抛「连接已失效」而不是底层 writer 异常
            var ex = Assert.ThrowsAsync<OperationCanceledException>(async () => await SendDummyAsync(conn));
            Assert.That(ex.Message, Does.Contain("connection failed"),
                "失效连接上的发送应给出可诊断原因，而不是底层异常");

            Assert.That(Volatile.Read(ref closedCount), Is.EqualTo(1), "失效事件幂等，不得重复派发");
        }

        // ══════════════════════════════════════════════════════════
        //  1b. 发送尚未完成时，窗口必须已登记该帧（竞态护栏）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 复现 500 装置压测里「NR = nextSendSeq-1、unacked=0」的自造协议错误。
        ///
        /// 旧实现把 <c>RegisterSent</c> 放在 <c>await _sink.SendAsync</c> **之后**：
        /// 帧已分配 N(S)=0 使 _nextSendSeq 变为 1，但窗口仍是空的。这段时间里，
        /// 对端回一个合法的冗余确认 N(R)=0（表示"我还没收到/或已收到上一拍"），
        /// Ack 的合法区间被算成 [1,1]，于是 N(R)=0 被判越界 → 连接被无故关闭。
        ///
        /// 现在 TakeSendSeq 与 RegisterSent 在同一个 _kLock 临界区内完成，
        /// 即使发送还挂在 await 上，窗口也已经登记，N(R)=0 落在合法区间内。
        /// </summary>
        [Test]
        public async Task 发送进行中_窗口必须已登记_冗余确认不得误判为协议错误()
        {
            var sink = new GatedSink();
            using var conn = new ApduConnection(new APCIParameters { K = 4 }, Al(), sink);

            // 启动一次发送，令其停在 sink 的闸门处（N(S) 已分配、帧未发完）
            var sending = SendDummyAsync(conn);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref sink.SendCount) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.That(Volatile.Read(ref sink.SendCount), Is.EqualTo(1), "前置条件：发送已进入 sink 并挂起");

            // 此刻对端回冗余确认 N(R)=0（I 帧：N(S)=0, N(R)=0）
            // 窗口登记了 seqUsed=0 → 条目 SeqNo=1 → 合法区间 [0,1]，N(R)=0 合法。
            var ok = conn.OnIFrame(0, 0, ReadOnlySpan<byte>.Empty);

            sink.Release();
            await sending.ConfigureAwait(false);

            Assert.That(ok, Is.True,
                "发送尚未完成时到期的冗余确认必须合法 —— 旧实现会因 RegisterSent 在 await 之后而误判协议错误");
            Assert.That(conn.LastProtocolErrorDetail, Is.Empty, "不得产生协议错误");
        }

        // ══════════════════════════════════════════════════════════
        //  2. 对端 N(R) 越界 → 协议错误必须失效 + 派发事件
        // ══════════════════════════════════════════════════════════

        [Test]
        public void NR越界_OnIFrame必须拒绝并记录原因()
        {
            var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());

            // 窗口内没有任何在途帧（nextSendSeq=0），对端却确认 N(R)=5 → 越界
            var ok = conn.OnIFrame(0, 5, ReadOnlySpan<byte>.Empty);

            Assert.That(ok, Is.False, "N(R) 越界必须判定为协议错误");
            Assert.That(conn.LastProtocolErrorDetail, Does.Contain("NR out of range"),
                "必须记录具体分支原因（原先协议错误路径没有任何日志）");
            Assert.That(conn.LastProtocolErrorDetail, Does.Contain("nextSendSeq"),
                "诊断信息应包含本端窗口状态");

            conn.Dispose();
        }

        [Test]
        public async Task 对端NR越界_端点必须派发ConnectionClosed并请求关闭传输()
        {
            var apci = new APCIParameters();
            var endpoint = new ApduEndpoint(apci, Al(), new ImmediateSink());

            var transportCloseRequested = 0;
            endpoint.Rebuild(CancellationToken.None, _ => { }, _ =>
            {
                Interlocked.Increment(ref transportCloseRequested);
                return Task.CompletedTask;
            });

            var events = new List<ApduConnectionEvent>();
            endpoint.Connection.EventHandler += ev => { lock (events) { events.Add(ev); } };
            endpoint.Connection.OnUFrame(ApduCodec.StartDtCon);
            lock (events) { events.Clear(); }

            // 构造一个 N(R) 越界的 S 帧（对端确认了本端从未发送的序号）
            var frame = new byte[ApduCodec.ApciLength];
            ApduCodec.WriteSFormatHeader(frame, 5);

            var apdu = new IecApdu();
            Assert.That(apdu.OnParsingHeader(frame.AsSpan(0, 2)), Is.True, "S 帧头应被接受");
            Assert.That(apdu.OnParsingBody(frame.AsSpan(2)), Is.True, "S 帧体应被接受");

            var handled = await endpoint.HandleReceivedAsync(apdu);

            Assert.That(handled, Is.False, "协议错误应向宿主返回 false");

            lock (events)
            {
                Assert.That(events, Has.Count.EqualTo(1), "协议错误必须派发一次 ConnectionClosed（原先不派发）");
                Assert.That(events[0], Is.EqualTo(ApduConnectionEvent.ConnectionClosed));
            }

            Assert.That(endpoint.Connection.IsActive, Is.False, "协议错误后 IsActive 必须为 false");
            Assert.That(endpoint.Connection.CloseReason, Is.EqualTo(ConnectionCloseReason.ProtocolError));

            // 传输关闭是异步的（不在收包回调内同步关闭）
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref transportCloseRequested) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.That(Volatile.Read(ref transportCloseRequested), Is.EqualTo(1),
                "必须请求关闭传输层（异步执行，且只关一次）");
        }

        // ══════════════════════════════════════════════════════════
        //  2b. 宿主清理不得依赖 TouchSocket 的 OnTcpClosed 回调
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 原始僵尸链路的完整成因是两层：①事件/状态不上报；②<b>清理不执行</b>。
        /// 本用例模拟最坏情况：<c>CloseAsync</c> 成功返回，但 TouchSocket 的
        /// <c>OnTcpClosed</c> **完全不触发**（实测存在的路径）。此时宿主的清理回调
        /// （唤醒挂起命令确认 / 注销会话 / 取消生命周期令牌）仍必须执行，
        /// 否则调用方要等满 <c>CommandConfirmationTimeout</c> 才感知断链。
        /// </summary>
        [Test]
        public async Task 底层关闭回调不触发时_失效仍必须执行宿主清理()
        {
            var endpoint = new ApduEndpoint(new APCIParameters(), Al(), new ImmediateSink());

            var transportClosed = 0;
            var cleanupCount = 0;
            var cleanupSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // closeAsync 只返回成功，刻意【不】模拟 OnTcpClosed 回调
            endpoint.Rebuild(CancellationToken.None, _ => { }, _ =>
            {
                Interlocked.Increment(ref transportClosed);
                return Task.CompletedTask;
            }, () =>
            {
                Interlocked.Increment(ref cleanupCount);
                cleanupSignal.TrySetResult();
            });

            endpoint.FailConnection(ConnectionCloseReason.IoError);

            var completed = await Task.WhenAny(cleanupSignal.Task, Task.Delay(3000)).ConfigureAwait(false);

            Assert.That(completed, Is.SameAs(cleanupSignal.Task),
                "OnTcpClosed 未触发时，宿主的失效清理仍必须被执行");
            Assert.That(Volatile.Read(ref transportClosed), Is.EqualTo(1), "传输应被关闭一次");
            Assert.That(Volatile.Read(ref cleanupCount), Is.EqualTo(1), "清理只执行一次（幂等）");
        }

        /// <summary>
        /// 结构护栏：端点失效时必须自行释放共享调度器注册（_liveCount / 物理 Timer）
        /// 与状态机，即使在宿主完全没接 <c>onFailed</c> 的最坏情况下。
        ///
        /// 不能用自定义 <c>onFailed</c> lambda 测这件事（那测的是测试自己的回调，
        /// 不是端点）：本用例故意不传 onFailed，直接观察调度器存活注册数。
        /// 修复前失效路径把资源释放留给 <c>OnTcpClosed</c>，而该回调不触发正是
        /// 本次修复的前提场景 → 泄漏。此测试能抓住「宿主接线漏一半」。
        /// </summary>
        [Test]
        public async Task 端点失效必须自行释放共享调度器注册_即使没有宿主回调()
        {
            SharedTimeoutScheduler.ForceShutdown();
            Assert.That(SharedTimeoutScheduler.LiveCount, Is.EqualTo(0), "前置条件：无存活注册");

            var endpoint = new ApduEndpoint(new APCIParameters(), Al(), new ImmediateSink());
            var transportClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 刻意不传 onFailed：宿主接线缺失也不能造成调度器泄漏
            endpoint.Rebuild(CancellationToken.None, _ => { },
                _ => { transportClosed.TrySetResult(); return Task.CompletedTask; });

            Assert.That(SharedTimeoutScheduler.LiveCount, Is.EqualTo(1), "前置条件：Rebuild 注册了 1 个调度项");

            endpoint.FailConnection(ConnectionCloseReason.IoError);

            await Task.WhenAny(transportClosed.Task, Task.Delay(3000)).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (SharedTimeoutScheduler.LiveCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            Assert.That(SharedTimeoutScheduler.LiveCount, Is.EqualTo(0),
                "失效路径必须自行注销共享调度器，不得依赖 OnTcpClosed（该回调可能不触发）");

            SharedTimeoutScheduler.ForceShutdown();
        }

        /// <summary>
        /// 关闭传输抛异常时也必须继续清理（不能因关闭失败就把等待者挂死）。
        /// </summary>
        [Test]
        public async Task 关闭传输抛异常时_失效仍必须执行宿主清理()
        {
            var endpoint = new ApduEndpoint(new APCIParameters(), Al(), new ImmediateSink());
            var cleanupSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            endpoint.Rebuild(CancellationToken.None, _ => { },
                _ => Task.FromException(new IOException("close failed")),
                () => cleanupSignal.TrySetResult());

            endpoint.FailConnection(ConnectionCloseReason.IoError);

            var completed = await Task.WhenAny(cleanupSignal.Task, Task.Delay(3000)).ConfigureAwait(false);

            Assert.That(completed, Is.SameAs(cleanupSignal.Task),
                "关闭传输失败不得阻断宿主清理（finally 语义）");
        }

        // ══════════════════════════════════════════════════════════
        //  2c. 三条发送路径对称：U 帧 / S 确认帧写失败也必须判定失效
        // ══════════════════════════════════════════════════════════

        /// <summary>U 帧（TESTFR_ACT / STARTDT 等）写失败必须与 I 帧一样判定连接失效。</summary>
        [Test]
        public void U帧发送失败_必须判定连接失效并派发ConnectionClosed()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new FailingSink());
            var closed = 0;
            conn.EventHandler += ev => { if (ev == ApduConnectionEvent.ConnectionClosed) { Interlocked.Increment(ref closed); } };

            Assert.ThrowsAsync<IOException>(async () =>
                await conn.SendTestFrActAsync(CancellationToken.None).ConfigureAwait(false));

            Assert.That(conn.IsActive, Is.False);
            Assert.That(conn.CloseReason, Is.EqualTo(ConnectionCloseReason.IoError));
            Assert.That(Volatile.Read(ref closed), Is.EqualTo(1), "U 帧发送失败也必须派发 ConnectionClosed");
        }

        /// <summary>失效后的 U/S 帧发送必须被立即拒绝（三条路径共用 ThrowIfFailed）。</summary>
        [Test]
        public void 失效后_U帧与S确认帧发送必须被拒绝()
        {
            using var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());
            conn.FailConnection(ConnectionCloseReason.IoError);

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await conn.SendTestFrActAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await conn.StartDataTransferAsync(CancellationToken.None).AsTask().ConfigureAwait(false));
        }

        // ══════════════════════════════════════════════════════════
        //  3. 状态一致性
        // ══════════════════════════════════════════════════════════

        [Test]
        public void Dispose后IsActive必须为false()
        {
            var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());
            conn.OnUFrame(ApduCodec.StartDtCon);
            Assert.That(conn.IsActive, Is.True);

            conn.Dispose();

            Assert.That(conn.IsActive, Is.False, "Dispose 后 IsActive 必须为 false（IsActivated 依赖它）");
        }

        [Test]
        public void 失效后ProcessApdu必须忽略后续帧()
        {
            var conn = new ApduConnection(new APCIParameters(), Al(), new ImmediateSink());
            var events = new List<ApduConnectionEvent>();
            conn.EventHandler += ev => { lock (events) { events.Add(ev); } };

            conn.FailConnection(ConnectionCloseReason.IoError);
            lock (events) { events.Clear(); }

            // 关闭传输是异步的，期间仍可能有帧到达：不得再产生协议错误或事件
            var accepted = conn.ProcessApdu(null);

            Assert.That(accepted, Is.True, "失效连接的后续帧应被静默忽略");
            lock (events)
            {
                Assert.That(events, Is.Empty, "失效后不得再派发任何事件");
            }

            conn.Dispose();
        }
    }
}
