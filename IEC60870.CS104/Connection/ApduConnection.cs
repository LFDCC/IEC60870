//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using TouchSocket.Core;



namespace IEC60870.CS104;
    /// <summary>
    /// IEC 60870-5-104 APCI 层异步状态机。传输层无关，通过 <see cref="IIec104ApduSink"/> 发送字节。
    /// 纯 <see cref="ValueTask"/> + <see cref="CancellationToken"/>，无独立线程、无阻塞 I/O。
    /// </summary>
    /// <remarks>
    /// 本库（IEC60870.NETAsync）基于原生 .NET 异步与 TouchSocket 通信栈构建（.NET 6+），
    /// APCI 状态机与传输实现解耦：本类只维护协议语义，字节收发与内存管理交由
    /// TouchSocket 管线完成。
    /// <para>
    /// 职责：序列号 N(S)/N(R) 维护、k 窗口发送流控（背压）、w 阈值延迟确认、
    /// T1（确认超时）/T2（延迟确认）/T3（空闲测试）、U/S/I 帧处理。
    /// </para>
    /// <para>
    /// 线程模型：接收侧由传输层在收到数据时同步调用 <see cref="ProcessApdu"/>（零拷贝、不阻塞）；
    /// 随后 <c>await</c> <see cref="PumpAsync"/> 冲刷待发控制帧。发送侧调用 <see cref="SendAsduAsync"/> 等。
    /// 锁风格统一约定：纯内存短临界区（无 await）用同步 <c>lock</c>（纳秒级，无内核切换）；
    /// 跨 await 的发送串行化用 <see cref="SemaphoreSlim"/>（异步等待，不占线程）——
    /// 两者均不在持锁时 await，符合 .NET 异步锁使用规范。
    /// </para>
    /// </remarks>
    public sealed class ApduConnection : IDisposable
    {
        private readonly APCIParameters _apci;
        private readonly ApplicationLayerParameters _al;
        private readonly IIec104ApduSink _sink;

        /// <summary>
        /// ASDU 类型处理器注册表（可为 null = 使用全局 <c>Default</c>）。
        /// </summary>
        /// <remarks>
        /// 必须由连接持有并暴露给上层：<see cref="AsduView.GetElement(int, AsduTypeHandlerRegistry)"/>
        /// 的注册表是**调用方传入**的，接收路径原先从不注入，导致
        /// <c>Iec104Client.TypeHandlers</c> / <c>Iec104Server.TypeHandlers</c>
        /// 成了死配置 —— 用户注册了私有类型（如许继 166/168），
        /// 解码时仍走 Default 抛 "Unknown ASDU type id"，现场极难定位。
        /// </remarks>
        private readonly AsduTypeHandlerRegistry _typeHandlers;

        /// <summary>本连接使用的类型处理器注册表（null 表示用全局默认）。</summary>
        public AsduTypeHandlerRegistry TypeHandlers => _typeHandlers;

        // ── 定时器常量（构造时由 APCIParameters 秒值预计算，消除散落的 *1000 乘法）──
        internal readonly int T1Ms;
        internal readonly int T2Ms;
        internal readonly int T3Ms;

        // ── 发送序列化 ────────────────────────────────────────────────
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // ── 序列号状态 ────────────────────────────────────────────────
        private int _unconfirmedReceived;  // 未确认的已收 I 帧计数（对 w 阈值）
        private long _lastConfirmationTime; // 最近一次确认时间 (ms)
        private bool _t2Triggered;

        // ── k 窗口背压 + 序列号缓冲（提取至 KWindow，锁内调用）──────────
        private readonly KWindow _window;

        // ── 定时器状态 ────────────────────────────────────────────────
        private long _nextT3Timeout;
        private long _uMessageTimeout;     // 0 = 关闭
        private int _outstandingTestFrCon;

        // ── 待发控制帧标志（接收侧置位，PumpAsync 冲刷）────────────────
        private volatile bool _pendingTestFrCon;
        private volatile bool _pendingStartDtCon;   // 服务端收到 STARTDT_ACT
        private volatile bool _pendingStopDtCon;    // 服务端收到 STOPDT_ACT

        // ── 握手等待（客户端）─────────────────────────────────────────
        private TaskCompletionSource<bool> _startDtConWaiter;
        private TaskCompletionSource<bool> _stopDtConWaiter;

        // ── k 窗口槽位等待者（由 KWindow 管理，_kLock 保护）────────────────
        // 发送前 TryAcquireSlot：有空槽同步通过（零分配）；窗口满则 TaskCompletionSource
        // 入队 await，对端确认时 ReleaseSlots 将槽位【直接转移】给队头等待者
        // （占用计数不变，逐个唤醒、无 thundering herd）。Dispose 时统一 TrySetCanceled
        // 唤醒所有等待者（抛 OCE，不悬挂）。详见 KWindow 备注。

        private readonly object _kLock = new object();

        private volatile bool _active;
        private bool _disposed;
        private bool _closeNotified;                       // 保证 ConnectionClosed 仅派发一次
        private ConnectionCloseReason _closeReason = ConnectionCloseReason.Unknown;

        /// <summary>连接已被判定失效（协议错误 / 发送失败 / 超时）。失效后不再收发，仅等宿主关闭传输。</summary>
        private volatile bool _failed;

        /// <summary>失效通知宿主（由 <see cref="ApduEndpoint"/> 注入）：请其关闭传输层。</summary>
        private Action<ConnectionCloseReason> _fatalHandler;

        /// <summary>
        /// 是否检查序列号（默认 true）。
        /// 设为 false 时旁路 k 窗口流控（不占用发送槽位、不登记/释放窗口条目）——
        /// 此时没有可信的对端 N(R) 来释放窗口，若仍占槽位会在发满 K 帧后永久阻塞；
        /// 接收侧仍按收到的 I 帧计数发送 S 确认，不影响 w/T2 语义。
        /// </summary>
        public bool CheckSequenceNumbers { get; set; } = true;

        /// <summary>
        /// 由宿主（<see cref="ApduEndpoint"/>）注入的失效回调：连接判定失效时请宿主关闭传输层。
        /// </summary>
        internal void SetFatalHandler(Action<ConnectionCloseReason> handler) => _fatalHandler = handler;

        /// <summary>
        /// 判定本连接已无可挽回地失效：置非激活、记录原因、派发一次
        /// <see cref="ApduConnectionEvent.ConnectionClosed"/>，并请宿主关闭传输层。
        /// </summary>
        /// <param name="reason">失效原因（协议错误 / IO 错误 / 超时）。</param>
        /// <remarks>
        /// <para>
        /// <b>为什么不能只依赖传输层的关闭回调</b>：<see cref="ApduConnectionEvent.ConnectionClosed"/>
        /// 原先只在宿主的 socket 关闭回调（<c>OnTcpClosed</c>）里派发。实测在「协议错误 →
        /// 收包回调内关闭连接」这条路径上，TouchSocket 的 Closed 事件**不会触发**，
        /// 于是原因记了、事件没人派发、<see cref="IsActive"/> 也仍为 true —— 应用层看到一条
        /// "在线"但所有下发都失败的僵尸链路（500 装置压测下 7% 的链路中招，见
        /// examples/104/cs104-stress-500/STRESS-TEST-REPORT.md §2.1）。
        /// </para>
        /// <para>
        /// 本方法把「状态 + 事件」与「传输关闭」解耦：前者同步完成（应用层立刻可感知），
        /// 后者交给宿主异步执行，因此可从发送失败等任意上下文安全调用。
        /// 幂等：重复调用只派发一次事件，但每次都确保传输关闭被请求。
        /// </para>
        /// </remarks>
        internal void FailConnection(ConnectionCloseReason reason)
        {
            if (_disposed)
            {
                return;
            }

            _failed = true;
            _active = false;
            NotifyClosed(reason);

            try
            {
                _fatalHandler?.Invoke(reason);
            }
            catch (Exception ex)
            {
                // 宿主关闭动作失败不应掩盖原始故障（调用方随后仍会收到原始发送异常）
                System.Diagnostics.Debug.WriteLine("FailConnection fatal handler error: " + ex);
            }
        }


        /// <summary>数据传输是否已激活（收到/发送 STARTDT_CON 后，且连接未失效/未释放）。</summary>
        public bool IsActive => _active && !_disposed;

        /// <summary>
        /// 最近一次协议错误的细节（如 <c>NR out of range: 6</c>）。仅用于日志与排障，
        /// 不参与协议判定；空字符串表示尚无协议错误。
        /// </summary>
        /// <remarks>
        /// 此前协议错误路径没有任何日志，现场只能看到 <see cref="CloseReason"/> 变成一个枚举值，
        /// 无法判断是 N(S) 不匹配、N(R) 越界还是 ASDU 过短 —— 压测定位时只能靠临时插桩。
        /// </remarks>
        internal string LastProtocolErrorDetail { get; private set; } = string.Empty;

        /// <summary>
        /// 记录一条诊断细节。供宿主在订阅者回调异常等「非协议错误」但需要复用的排障信息场景使用。
        /// </summary>
        internal void RecordProtocolErrorDetail(string detail) => LastProtocolErrorDetail = detail;

        /// <summary>收到 ASDU 时的同步事件（零拷贝视图，仅在回调期间有效，支持多订阅者）。</summary>
        public event AsduViewHandler AsduReceived;

        /// <summary>连接层事件（支持多订阅者）。</summary>
        public event Action<ApduConnectionEvent> EventHandler;

        /// <summary>
        /// 构造 APCI 状态机。
        /// </summary>
        /// <param name="apciParameters">APCI 参数（<see cref="K"/> 在构造时被捕获为窗口容量；构造后修改本对象无效/不一致，详见 <see cref="APCIParameters"/> 备注）。</param>
        /// <param name="alParameters">应用层参数。</param>
        /// <param name="sink">传输层发送出口。</param>
        public ApduConnection(APCIParameters apciParameters, ApplicationLayerParameters alParameters,
            IIec104ApduSink sink, AsduTypeHandlerRegistry typeHandlers = null)
        {
            _apci = apciParameters ?? throw new ArgumentNullException(nameof(apciParameters));
            _al = alParameters ?? throw new ArgumentNullException(nameof(alParameters));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _typeHandlers = typeHandlers;

            // 跨字段关系（T0<T1<T3、显式 W<K）在使用处校验：setter 无法可靠校验，
            // 且 K 的错误取值若留到 new KWindow 处只会抛不带信息的异常（K=1e6 更会先分配 16MB）。
            _apci.Validate();

            _window = new KWindow(_apci.K);
            T1Ms = _apci.T1 * 1000;
            T2Ms = _apci.T2 * 1000;
            T3Ms = _apci.T3 * 1000;
            ResetT3Timeout(Now);
            _lastConfirmationTime = Now;        }

        private static long Now => Environment.TickCount64;

        // ═══════════════════════════════════════════════════════════════
        //  接收侧（由传输层在收到数据时同步调用；零拷贝、不阻塞、不 await）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 处理适配器解析出的一个完整 APDU(按帧型路由到 I/S/U 处理)。
        /// </summary>
        /// <param name="apdu">解析产物(帧数据归 apdu 所有,同步消费)。</param>
        /// <returns>false 表示序列号/协议错误,传输层应关闭连接。</returns>
        internal bool ProcessApdu(IecApdu apdu)
        {
            // 已判定失效的连接不再解析后续帧：宿主关闭传输是异步的，期间仍可能有帧到达，
            // 继续按协议解释只会再报一次协议错误、并误导排障。
            if (_failed)
            {
                return true;
            }

            switch (apdu.Kind)
            {
                case ApduKind.Information:
                    return OnIFrame(apdu.SendSeq, apdu.RecvSeq, apdu.AsduSpan);
                case ApduKind.Supervisory:
                    return OnSFrame(apdu.RecvSeq);
                default:
                    OnUFrame(apdu.UFunction);
                    return true;
            }
        }

        /// <summary>
        /// 处理收到的 I 帧。<paramref name="asdu"/> 为零拷贝负载切片。
        /// </summary>
        /// <returns>false 表示序列号错误,传输层应关闭连接。</returns>
        public bool OnIFrame(int frameSendSeq, int frameRecvSeq, ReadOnlySpan<byte> asdu)
        {
            var now = Now;

            // 接收序号/确认计数等字段会被接收线程（本方法）与定时器线程
            // （CheckTimeoutsAsync / SendSAckAsync）跨线程读写，统一在 _kLock 下操作，
            // 既保证原子性也建立内存可见性（happens-before），避免 w 阈值/T2 时序偏差。
            lock (_kLock)
            {
                if (!_t2Triggered)
                {
                    _t2Triggered = true;
                    _lastConfirmationTime = now; // 启动 T2
                }

                // 校验 N(S)：必须等于我方期望的接收序列号
                if (frameSendSeq != _window.NextRecvSeq)
            {
                LastProtocolErrorDetail = $"NS mismatch: got={frameSendSeq} expect={_window.NextRecvSeq}";
                return false;
            }

            // 校验对端 N(R)，并从 k 缓冲移除已确认帧
            if (!CheckAndRemoveConfirmed(frameRecvSeq))
            {
                LastProtocolErrorDetail = $"NR out of range: NR={frameRecvSeq} nextSendSeq={_window.NextSendSeq} unacked={_window.UnackedCount}";
                return false;
            }

            _window.AdvanceRecvSeq(frameSendSeq);
                _unconfirmedReceived++;
            }

            // ASDU 完整性校验先于激活判定：过短的 ASDU（头部不全）即使在非激活状态
            // 也属于协议错误，必须报告，避免对端持续发送损坏帧。
            if (asdu.Length > 0)
            {
                var view = new AsduView(asdu, _al);                // 过短的 ASDU（头部不全）属协议错误，直接关闭连接，避免用户回调 IndexOutOfRange
                if (!view.IsValid)
                {
                    LastProtocolErrorDetail = $"ASDU too short: len={asdu.Length}";
                    return false;
                }

                // 数据传输未激活（STARTDT_CON 之前 / STOPDT_CON 之后）时按协议丢弃 I 帧：
                // 不向业务派发 AsduReceived，避免停机期间把在途/重发的报文当作新数据入库。
                // 注意 N(S) 已在锁内推进、_unconfirmedReceived 已计数，PumpAsync 仍会
                // 按 w/T2 发送 S 确认，防止对端因未获确认而重传风暴。
                if (_active)
                {
                    // 同步分发给用户（零拷贝视图，多订阅者）。用户回调在锁外执行，避免重入死锁。
                    // 必须隔离订阅者异常：本方法在 TouchSocket 收包管线内同步调用，
                    // 异常会跳过后续的 PumpAsync（S 确认没发出去）并冒泡到传输层，
                    // 行为不可预期；CS103 早已隔离，属对称缺口。
                    try
                    {
                        AsduReceived?.Invoke(in view);
                    }
                    catch (Exception ex)
                    {
                        // 记录到诊断字段（宿主在协议错误日志里已经会读它），
                        // 且绝不再向外传播 —— 订阅者故障不能杀死链路。
                        LastProtocolErrorDetail = $"ASDU callback error (TI={view.TypeId} CA={view.Ca}): {ex.Message}";
                    }
                }
            }

            ResetT3Timeout(now);
            return true;
        }

        /// <summary>处理收到的 S 帧（仅携带对端 N(R)）。</summary>
        /// <returns>false 表示序列号错误。</returns>
        public bool OnSFrame(int frameRecvSeq)
        {
            if (!CheckAndRemoveConfirmed(frameRecvSeq))
        {
            LastProtocolErrorDetail = $"S-frame NR out of range: NR={frameRecvSeq} nextSendSeq={_window.NextSendSeq} unacked={_window.UnackedCount}";
            return false;
        }

        ResetT3Timeout(Now);
            return true;
        }

        /// <summary>处理收到的 U 帧（控制帧）。</summary>
        public void OnUFrame(byte uFunction)
        {
            // 只在收到「期待的确认」时清零 U 帧 T1 超时。
            // 原实现第一句无条件 _uMessageTimeout = 0：对端持续发任意 U 帧
            // （TESTFR_ACT / 重复 STARTDT_ACT）即可把 STARTDT_CON 的 T1 截止
            // 时刻反复抹掉，握手失败判定被无限推迟，与 T0/T1 语义不符。
            if (uFunction is ApduCodec.TestFrCon or ApduCodec.StartDtCon or ApduCodec.StopDtCon)
            {
                _uMessageTimeout = 0;
            }

            if (uFunction == ApduCodec.TestFrAct)
            {
                _pendingTestFrCon = true;
            }
            else if (uFunction == ApduCodec.TestFrCon)
            {
                _outstandingTestFrCon = 0;
            }
            else if (uFunction == ApduCodec.StartDtAct)
            {
                _pendingStartDtCon = true;      // 服务端：将回 STARTDT_CON
            }
            else if (uFunction == ApduCodec.StartDtCon)
            {
                _active = true;                 // 客户端：激活
                Raise(ApduConnectionEvent.StartDtConReceived);
                Raise(ApduConnectionEvent.Activated);
                Signal(ref _startDtConWaiter);
            }
            else if (uFunction == ApduCodec.StopDtAct)
            {
                _pendingStopDtCon = true;
            }
            else if (uFunction == ApduCodec.StopDtCon)
            {
                _active = false;
                Raise(ApduConnectionEvent.StopDtConReceived);
                Raise(ApduConnectionEvent.Deactivated);
                Signal(ref _stopDtConWaiter);
            }

            ResetT3Timeout(Now);
        }

        // ═══════════════════════════════════════════════════════════════
        //  冲刷待发控制帧（接收批处理结束后 await 调用）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 冲刷因接收而积累的待发控制帧：TESTFR_CON、STARTDT/STOPDT_CON、w 阈值 S 确认。
        /// </summary>
        public async ValueTask PumpAsync(CancellationToken cancellationToken)
        {
            if (_pendingTestFrCon)
            {
                _pendingTestFrCon = false;
                await SendUAsync(ApduCodec.TestFrConMsg, cancellationToken).ConfigureAwait(false);
            }

            if (_pendingStartDtCon)
            {
                _pendingStartDtCon = false;
                _active = true;
                Raise(ApduConnectionEvent.StartDtActReceived);
                await SendUAsync(ApduCodec.StartDtConMsg, cancellationToken).ConfigureAwait(false);
                Raise(ApduConnectionEvent.Activated);
            }

            if (_pendingStopDtCon)
            {
                _pendingStopDtCon = false;
                _active = false;
                Raise(ApduConnectionEvent.StopDtActReceived);
                await SendUAsync(ApduCodec.StopDtConMsg, cancellationToken).ConfigureAwait(false);
                Raise(ApduConnectionEvent.Deactivated);
            }

            // w 阈值：达到未确认上限则立即发 S 确认。
            // _unconfirmedReceived 由接收线程在 _kLock 下写入，此处也需在
            // _kLock 下取快照（CheckTimeoutsAsync 已按此约定），避免可见性问题
            // 让 S 确认被推迟到 T2 才补发。
            bool shouldAck;
            lock (_kLock)
            {
                shouldAck = _unconfirmedReceived >= _apci.W;
            }

            if (shouldAck)
            {
                await SendSAckAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  发送侧
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 发送一个 I 帧（携带 ASDU）。若 k 窗口已满则异步等待对端确认（背压），
        /// 不阻塞线程。返回所用的发送序列号 N(S)。
        /// </summary>
        /// <remarks>
        /// 构建路径采用「预留-回填」模式：<see cref="ValueByteBlock"/>（池化，零堆分配）
        /// 预留整帧内存，ASDU 经 <c>Encode(ref AsduWriter,...)</c> 直写负载区
        /// （span 直写、无中间 Frame 对象、每 IO 一次虚调用）；6 字节 APCI 头先占位，
        /// 在分配 N(S) 的短临界区内回填（序列号与帧字节原子确定），随后经 IIec104ApduSink 发送。
        /// </remarks>
        public async ValueTask<int> SendAsduAsync(ASDU asdu, CancellationToken cancellationToken)
        {
            if (asdu == null)
            {
                throw new ArgumentNullException(nameof(asdu));
            }

            // 已失效连接优先给出可诊断的 OCE（保持既有语义：FailConnection/Dispose 后
            // 调用方按 OperationCanceledException 处理），再区分「未激活」。
            ThrowIfFailed();

            // 数据传输未激活（未收到 STARTDT_CON，或已收到 STOPDT_CON）时拒绝发送：
            // 否则 STOPDT 之后仍会把 I 帧推给对端，违反协议状态机。
            if (!_active)
            {
                throw new InvalidOperationException("数据传输未启动（未收到 STARTDT_CON）");
            }

            // 关闭序列号校验时旁路 k 流控（见 AcquireSendSlotAsync）：不占槽位、不登记窗口。
            var trackWindow = CheckSequenceNumbers;
            var slotHeld = await AcquireSendSlotAsync(trackWindow, cancellationToken).ConfigureAwait(false);

            // 持有 k 槽后，若后续步骤抛异常，必须归还槽位；序号歧义由 FailedConnection 处理（见下方 catch）。
            bool seqTaken = false;
            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int seqUsed;

                    // ── 构建 I 帧体（池化预留-回填，一次成型零中间拷贝）──
                    var byteBlock = new ValueByteBlock(ApduCodec.MaxApduLength);
                    try
                    {
                        var memory = byteBlock.GetMemory(ApduCodec.MaxApduLength);
                        // 负载区直写：跳过 6 字节 APCI 预留区
                        var writer = new AsduWriter(memory.Span.Slice(ApduCodec.ApciLength));
                        asdu.Encode(ref writer, _al);
                        var total = ApduCodec.ApciLength + writer.Position;
                        byteBlock.Advance(total);

                        // 分配 N(S) + 登记 k 缓冲 + 回填 APCI 头，必须在同一个短临界区里完成：
                        // 登记【不能】推迟到 await _sink.SendAsync 之后。否则在「序号已分配、
                        // 帧未登记」这个窗口里，窗口看似为空（unacked=0）而 _nextSendSeq 已推进，
                        // 此时对端回一个合法的冗余确认 N(R)=已分配序号-1 就会被 Ack 判为越界
                        // → 自造协议错误 → 高负载下大批链路被无故关闭（500 装置压测下 9%）。
                        // 两者同锁，接收线程的 Ack 就不会看到这个中间态。
                        lock (_kLock)
                        {
                            seqUsed = _window.TakeSendSeq();
                            seqTaken = true;
                            // 关闭序列号校验时不登记窗口：窗口永远不会有 Ack 来释放条目，
                            // 登记只会让 UnackedCount/HasUnacked 失真并触发无意义的 T1 判定。
                            if (trackWindow)
                            {
                                _window.RegisterSent(seqUsed, Now);
                            }
                            ApduCodec.WriteIFormatHeader(memory.Span.Slice(0, ApduCodec.ApciLength),
                                seqUsed, _window.NextRecvSeq, total - ApduCodec.ApciLength);

                            _unconfirmedReceived = 0;
                            _t2Triggered = false;
                        }

                        // 实际发送（可能抛 OCE/IO 异常）。帧已在窗口登记；异常路径不回退
                        // 序号（无法确知帧是否上线），而是判定连接失效，见下方 catch。
                        await _sink.SendAsync(byteBlock.Memory.Slice(0, total), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        byteBlock.Dispose();
                    }

                    // 帧已成功上线（发送前已登记窗口），后续异常不再回退序号
                    seqTaken = false;
                    ResetT3Timeout(Now);
                    return seqUsed;
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception)
            {
                // N(S) 已分配但发送抛异常：「抛异常」并不等于「帧没上线」——底层
                // （TouchSocket ProtectedSendAsync）完全可能在字节已写入发送管道之后才失败。
                // 此时对端会确认一个我方未登记的序号，本地任意猜测（回退或保留 N(S)）
                // 都会与对端失步，104 规范下序号歧义不可恢复 ⇒ 只能判定连接失效。
                // 原实现在此处回退 N(S)，正是压测中 7% 链路进入「僵尸」状态的触发源
                // （见 examples/104/cs104-stress-500/STRESS-TEST-REPORT.md §2.1）。
                if (seqTaken)
                {
                    FailConnection(ConnectionCloseReason.IoError);
                }

                // 仅在确实占用过槽位时归还（关闭序列号校验时旁路流控，未占槽位）。
                if (slotHeld)
                {
                    ReleaseSlots(1);
                }

                throw;
            }
        }

        /// <summary>
        /// 发送一个已编码的 ASDU 体（不含 APCI 头）作为 I 帧。供事件队列排空循环使用：
        /// 快照在入队时已编码，此处直接写入负载区，避免持有 <see cref="ASDU"/> 对象引用。
        /// k 窗口背压、序列号分配与 <see cref="SendAsduAsync"/> 完全一致。
        /// </summary>
        public async ValueTask<int> SendEncodedAsduAsync(ReadOnlyMemory<byte> asduBody, CancellationToken cancellationToken)
        {
            // 与 SendAsduAsync 对称：关闭序列号校验时旁路 k 流控（不占槽位、不登记窗口）。
            var trackWindow = CheckSequenceNumbers;
            var slotHeld = await AcquireSendSlotAsync(trackWindow, cancellationToken).ConfigureAwait(false);

            bool seqTaken = false;
            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int seqUsed;

                    var byteBlock = new ValueByteBlock(ApduCodec.MaxApduLength);
                    try
                    {
                        var memory = byteBlock.GetMemory(ApduCodec.MaxApduLength);
                        asduBody.Span.CopyTo(memory.Span.Slice(ApduCodec.ApciLength));
                        var total = ApduCodec.ApciLength + asduBody.Length;
                        byteBlock.Advance(total);

                        lock (_kLock)
                        {
                            seqUsed = _window.TakeSendSeq();
                            seqTaken = true;
                            // 关闭序列号校验时不登记窗口：窗口永远不会有 Ack 来释放条目，
                            // 登记只会让 UnackedCount/HasUnacked 失真并触发无意义的 T1 判定。
                            if (trackWindow)
                            {
                                _window.RegisterSent(seqUsed, Now);
                            }
                            ApduCodec.WriteIFormatHeader(memory.Span.Slice(0, ApduCodec.ApciLength),
                                seqUsed, _window.NextRecvSeq, total - ApduCodec.ApciLength);

                            _unconfirmedReceived = 0;
                            _t2Triggered = false;
                        }

                        // 同 SendAsduAsync：窗口已在发送前登记，异常路径不回退序号而是判定连接失效。
                        await _sink.SendAsync(byteBlock.Memory.Slice(0, total), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        byteBlock.Dispose();
                    }

                    seqTaken = false;
                    ResetT3Timeout(Now);
                    return seqUsed;
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception)
            {
                // 同 SendAsduAsync：序号已分配即存在歧义，只能判定连接失效（不回退 N(S)）
                if (seqTaken)
                {
                    FailConnection(ConnectionCloseReason.IoError);
                }

                if (slotHeld)
                {
                    ReleaseSlots(1);
                }

                throw;
            }
        }

        /// <summary>
        /// 占用一个 k 窗口槽位。有空槽同步通过（零分配热路径）；
        /// 窗口满则以 TaskCompletionSource 入队 <see cref="_kLock"/> 保护的等待队列并异步挂起，
        /// 由 <see cref="KWindow.ReleaseSlots"/> 在对端确认时将槽位直接转移给队头等待者。
        /// </summary>
        /// <remarks>
        /// 取消语义：调用方 token 取消后等待者【留在队列中】（队列无法 O(1) 任意出队），
        /// 待槽位转移给它时立即归还（<see cref="KWindow.ReleaseSlots"/> 链式推进给后续等待者），
        /// 再抛出 <see cref="OperationCanceledException"/>——槽位永不泄漏，代价仅一次转移跳。
        /// </remarks>
        /// <summary>
        /// 已失效连接拒绝新的发送。三条发送路径（I 帧 / U 帧 / S 确认帧）共用，
        /// 保证语义一致：给出可诊断原因，而不是让底层抛 "writer completed" 之类异常。
        /// </summary>
        private void ThrowIfFailed()
        {
            if (_failed)
            {
                throw new OperationCanceledException("connection failed: " + _closeReason);
            }
        }

        /// <returns>
        /// true 表示已占用一个 k 窗口槽位（调用方负责在失败路径归还）；
        /// false 表示关闭了序列号校验（<see cref="CheckSequenceNumbers"/> == false）而旁路了流控，
        /// 未占槽位，调用方<b>不得</b>归还。
        /// </returns>
        private async ValueTask<bool> AcquireSendSlotAsync(bool trackWindow, CancellationToken cancellationToken)
        {
            ThrowIfFailed();

            // 关闭序列号校验时旁路 k 流控：窗口永远不会被对端 N(R) 推进（也不登记发送条目），
            // 若仍占槽位，发满 K 帧后这里会永久等待 —— 无超时、无异常、无日志。
            if (!trackWindow)
            {
                return false;
            }

            TaskCompletionSource<bool> waiter;
            lock (_kLock)
            {
                if (_window.TryAcquireSlot())
                {
                    return true;
                }

                waiter = _window.EnqueueWaiter();
            }

            try
            {
                // true = 槽位已转移给本等待者；canceled = 连接已 Dispose
                await waiter.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException("connection has been disposed");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                ReleaseSlots(1);
                throw new OperationCanceledException(cancellationToken);
            }

            return true;
        }

        /// <summary>
        /// 释放 <paramref name="count"/> 个 k 窗口槽位（发送失败归还 / 对端 N(R) 确认释放）。
        /// 委托 <see cref="KWindow.ReleaseSlots"/>（须在 _kLock 内调用）。
        /// </summary>
        private void ReleaseSlots(int count)
        {
            if (count <= 0)
        {
            return;
        }

        lock (_kLock)
            {
                _window.ReleaseSlots(count);
            }
        }

        /// <summary>发送 STARTDT_ACT 并等待 STARTDT_CON。</summary>
        public async ValueTask StartDataTransferAsync(CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _startDtConWaiter, waiter);
            await SendUAsync(ApduCodec.StartDtActMsg, cancellationToken).ConfigureAwait(false);
            _uMessageTimeout = Now + T1Ms;
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>发送 STOPDT_ACT 并等待 STOPDT_CON。</summary>
        public async ValueTask StopDataTransferAsync(CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _stopDtConWaiter, waiter);
            await SendUAsync(ApduCodec.StopDtActMsg, cancellationToken).ConfigureAwait(false);
            _uMessageTimeout = Now + T1Ms;
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>发送 TESTFR_ACT。</summary>
        public ValueTask SendTestFrActAsync(CancellationToken cancellationToken)
            => SendUAsync(ApduCodec.TestFrActMsg, cancellationToken);

        private async ValueTask SendUAsync(byte[] uFrame, CancellationToken cancellationToken)
        {
            // 与两条 I 帧发送路径对称：已失效连接直接拒绝，传输写失败必须判定连接失效。
            // 否则 STARTDT_CON/TESTFR_CON 写失败时异常会冒进 OnTcpReceived（无 catch），
            // 而超时循环里的 TESTFR_ACT 写失败会被调度器当「瞬态异常」吞成 Continue，
            // 形成无事件、无状态变化、无清理的死链轮询。
            ThrowIfFailed();

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    await _sink.SendAsync(uFrame, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    FailConnection(ConnectionCloseReason.IoError);
                    throw;
                }

                ResetT3Timeout(Now);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async ValueTask SendSAckAsync(CancellationToken cancellationToken)
        {
            ThrowIfFailed();

            // 从池租借 6 字节缓冲（避免每条 S 确认的热路径堆分配），发送完成后归还。
            var buf = ArrayPool<byte>.Shared.Rent(ApduCodec.ApciLength);
            int nr;
            lock (_kLock)
            {
                nr = _window.NextRecvSeq;
                _unconfirmedReceived = 0;
                _t2Triggered = false;
                _lastConfirmationTime = Now;
            }
            ApduCodec.WriteSFormatHeader(buf, nr);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    await _sink.SendAsync(buf.AsMemory(0, ApduCodec.ApciLength), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    FailConnection(ConnectionCloseReason.IoError);
                    throw;
                }
            }
            finally
            {
                _sendLock.Release();
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  超时驱动（由传输层定时循环 await 调用）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查并处理 T1/T2/T3 超时。
        /// </summary>
        /// <returns>false 表示发生致命超时（T1/TESTFR_CON 超时），传输层应关闭连接。</returns>
        public async ValueTask<bool> CheckTimeoutsAsync(CancellationToken cancellationToken)
        {
            var now = Now;

            // T3：空闲则发 TESTFR_ACT
            if (now > _nextT3Timeout)
            {
                if (_outstandingTestFrCon > 2)
            {
                return false; // TESTFR_CON 超时
            }

            await SendUAsync(ApduCodec.TestFrActMsg, cancellationToken).ConfigureAwait(false);
                _uMessageTimeout = now + T1Ms;
                _outstandingTestFrCon++;
                ResetT3Timeout(now);
            }

            // T2：有未确认的已收 I 帧且超过 T2 则发 S 确认
            // 这些字段由接收线程在 _kLock 下写入，此处也需在 _kLock 下读取以保证可见性。
            bool t2Due;
            lock (_kLock)
            {
                t2Due = _unconfirmedReceived > 0
                    && _t2Triggered
                    && (now - _lastConfirmationTime) >= T2Ms;
            }
            if (t2Due)
            {
                await SendSAckAsync(cancellationToken).ConfigureAwait(false);
            }

            // T1（U 帧）：等待 TESTFR_CON/STARTDT_CON 超时
            if (_uMessageTimeout != 0 && now > _uMessageTimeout)
        {
            return false;
        }

        // T1（I 帧）：最老未确认 I 帧超过 T1 未被确认
        lock (_kLock)
            {
                if (_window.HasUnacked)
                {
                    if ((now - _window.OldestSentTime) >= T1Ms)
                {
                    return false;
                }
            }
            }

            return true;
        }

        /// <summary>返回下一次超时检查的建议等待时长（毫秒），供调用方 Task.Delay。</summary>
        /// <remarks>
        /// 动态计算：取全部活动定时器（T1 最老未确认 I 帧、T2 延迟确认、U 帧握手超时、T3 空闲测试）
        /// 中最近的截止时刻，使定时器循环按需唤醒而非固定 100ms 空转。
        /// 下限 <see cref="MinPollIntervalMs"/> 防忙转；上限 <see cref="MaxPollIntervalMs"/>
        /// 为「计算后新产生的更早截止时刻」（如收到 I 帧触发的 T2）限定最大感知延迟。
        /// </remarks>
        public int GetSuggestedDelayMs()
        {
            var now = Now;
            var minDeadline = Volatile.Read(ref _nextT3Timeout);

            lock (_kLock)
            {
                if (_window.HasUnacked)
            {
                minDeadline = Math.Min(minDeadline, _window.OldestSentTime + T1Ms);
            }

            if (_t2Triggered && _unconfirmedReceived > 0)
            {
                minDeadline = Math.Min(minDeadline, _lastConfirmationTime + T2Ms);
            }

            if (_uMessageTimeout != 0)
            {
                minDeadline = Math.Min(minDeadline, _uMessageTimeout);
            }
        }

            var delta = minDeadline - now;
            return (int)Math.Clamp(delta, MinPollIntervalMs, MaxPollIntervalMs);
        }

        private const int MinPollIntervalMs = 10;   // 最小轮询间隔（防忙转）
        private const int MaxPollIntervalMs = 250;  // 最大轮询间隔（限定新截止时刻的感知延迟）

        // ═══════════════════════════════════════════════════════════════
        //  内部：序列号窗口
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 校验对端 N(R) 合法性并从 k 缓冲移除已确认帧（委托 <see cref="KWindow.Ack"/>，
        /// O(1) 模距离公式，替代旧版 do/while+溢出标志逐条扫描）。返回 false 表示序列号越界。
        /// </summary>
        private bool CheckAndRemoveConfirmed(int seqNo)
        {
            // 关闭序列号校验时直接返回且不触碰窗口：发送路径也不登记（RegisterSent），
            // 因此这里若调用 KWindow.Ack 会基于空窗口误判 N(R) 越界或误释放槽位。
            if (!CheckSequenceNumbers)
            {
                return true;
            }

        int freedCount;
            lock (_kLock)
            {
                freedCount = _window.Ack(seqNo, checkEnabled: true);
            }

            if (freedCount < 0)
        {
            return false;
        }

        if (freedCount > 0)
        {
            ReleaseSlots(freedCount);
        }

        return true;
        }

        private void ResetT3Timeout(long now) => _nextT3Timeout = now + T3Ms;

        /// <summary>最近一次 <see cref="ConnectionClosed"/> 事件的断开原因（只读；连接存活期间为 Unknown）。</summary>
        public ConnectionCloseReason CloseReason => _closeReason;

        private void Raise(ApduConnectionEvent ev) => EventHandler?.Invoke(ev);

        /// <summary>
        /// 通知订阅方连接已断开（非主动）。由传输层在收到远端关闭/超时/协议错误后调用；
        /// 主动 <see cref="Iec104Client.DisconnectAsync"/> / <see cref="Iec104Server.StopAsync"/> 不应调用本方法。
        /// </summary>
        /// <param name="reason">断开原因；已由 <see cref="FailConnection"/> 记录时忽略本参数。</param>
        /// <remarks>
        /// 幂等：仅首次调用会派发 <see cref="ApduConnectionEvent.ConnectionClosed"/>（之后即使重复关闭或 Dispose 也不会重复通知）。
        /// 若已 Dispose 则无操作。
        /// </remarks>
        public void NotifyClosed(ConnectionCloseReason reason = ConnectionCloseReason.RemoteClosed)
        {
            if (_disposed || _closeNotified)
        {
            return;
        }

        if (_closeReason == ConnectionCloseReason.Unknown)
        {
            _closeReason = reason;
        }

        _closeNotified = true;
            // 断开即非激活：IsActive 必须与 ConnectionClosed 事件一致，否则应用层
            // 会看到「已收到断开事件但 IsActivated 仍为 true」的矛盾状态。
            _active = false;
            Raise(ApduConnectionEvent.ConnectionClosed);
        }

        // 已删除 MarkCloseReason（旧「先记原因、等 OnTcpClosed 再派发」机制的残留）。
        // 该机制正是僵尸链路的成因：底层关闭回调不触发时事件永远不派发。
        // 现在统一由 FailConnection → NotifyClosed(reason) 一次性完成。

        /// <summary>U 帧握手成功（收到 STARTDT_CON / STOPDT_CON）：唤醒等待者为成功。</summary>
        private static void Signal(ref TaskCompletionSource<bool> field)
        {
            var waiter = Interlocked.Exchange(ref field, null);
            waiter?.TrySetResult(true);
        }

        /// <summary>连接终止（Dispose）：唤醒等待者为取消，绝不能让调用方误以为握手成功。</summary>
        private static void CancelWaiter(ref TaskCompletionSource<bool> field)
        {
            var waiter = Interlocked.Exchange(ref field, null);
            waiter?.TrySetCanceled();
        }

        public void Dispose()
        {
            lock (_kLock)
            {
                if (_disposed)
            {
                return;
            }

            _disposed = true;
                _active = false;
                _failed = true;

                // 窗口终结：置 disposed（后续 TryAcquireSlot 抛 OCE）并取出全部 k 窗口
                // 等待者，锁外统一取消唤醒（抛 OCE，不悬挂）。
                _window.Dispose();
            }

            // 唤醒 U 帧握手等待者：必须以「取消」而非「成功」唤醒。
            // 原实现用 TrySetResult(true)，于是连接在等 STARTDT_CON 期间被 Dispose
            // （协议错误 / 对端断开 / DisconnectAsync / 重连）时，
            // StartDataTransferAsync 会正常返回成功 → Iec104Client 置
            // _dataTransferStarted=true → 之后 StartDataTransferAsync 变成 no-op，
            // 应用以为链路已激活，实际 STARTDT 从未完成（与 296-297 行修的
            // "标志卡住"是同一类故障，方向相反）。
            CancelWaiter(ref _startDtConWaiter);
            CancelWaiter(ref _stopDtConWaiter);

            // 发送串行化锁：不 Dispose。
            // CleanupConnection() 由 RequestTransportClose 的 Task.Run、OnTcpClosed、
            // Rebuild、SafetyDispose 从任意线程调用，此时应用线程可能正卡在
            // await _sink.SendAsync(...) 且持有本锁。SemaphoreSlim.Dispose() 之后
            // finally 里的 Release() 必抛 ObjectDisposedException，**覆盖原始发送异常**；
            // WaitAsync 打到已 Dispose 的信号量抛 ODE 而非 OCE，按 OCE 写的重连逻辑全部走错分支。
            // SemaphoreSlim 未触碰 WaitHandle 时无未托管资源，不 Dispose 无泄漏。
        }
    }
