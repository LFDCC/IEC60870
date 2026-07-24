/*
 *  ApduConnection.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;



namespace IEC60870.CS104
{
    /// <summary>
    /// IEC 60870-5-104 APCI 层异步状态机。传输层无关，通过 <see cref="IApduSink"/> 发送字节。
    /// 纯 <see cref="ValueTask"/> + <see cref="CancellationToken"/>，无独立线程、无阻塞 I/O。
    /// </summary>
    /// <remarks>
    /// 职责：序列号 N(S)/N(R) 维护、k 窗口发送流控（背压）、w 阈值延迟确认、
    /// T1（确认超时）/T2（延迟确认）/T3（空闲测试）、U/S/I 帧处理。
    /// <para>
    /// 线程模型：接收侧由传输层在收到数据时同步调用 <see cref="OnIFrame"/>/<see cref="OnSFrame"/>/
    /// <see cref="OnUFrame"/>（零拷贝、不阻塞）；随后 <c>await</c> <see cref="PumpAsync"/> 冲刷待发控制帧。
    /// 发送侧调用 <see cref="SendAsduAsync"/> 等。k 缓冲索引用短临界区 <c>lock</c> 保护，
    /// 发送到 sink 用异步 <see cref="SemaphoreSlim"/> 串行化——两者均不在持锁时 await。
    /// </para>
    /// </remarks>
    public sealed class ApduConnection : IDisposable
    {
        private readonly APCIParameters _apci;
        private readonly ApplicationLayerParameters _al;
        private readonly IApduSink _sink;
        private readonly bool _isServerSide;

        // ── 发送序列化 ────────────────────────────────────────────────
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // ── 序列号状态 ────────────────────────────────────────────────
        private int _sendSeq;              // N(S)
        private int _receiveSeq;           // N(R)
        private int _unconfirmedReceived;  // 未确认的已收 I 帧计数（对 w 阈值）
        private long _lastConfirmationTime; // 最近一次确认时间 (ms)
        private bool _t2Triggered;

        // ── k 缓冲（已发未确认 I 帧）──────────────────────────────────
        private struct SentApdu
        {
            public long SentTime;
            public int SeqNo;
        }

        private readonly object _kLock = new object();
        private readonly int _maxSent;
        private readonly SentApdu[] _kBuffer;
        private int _oldest = -1;
        private int _newest = -1;

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

        // ── k 窗口背压（信号量）──────────────────────────────────────
        // 信号量计 K 个许可，每个许可 = 一个在途未确认 I 帧槽位。发送前 WaitAsync 占槽，
        // 对端确认（N(R)）时 CheckAndRemoveConfirmed 释放对应数量许可（Release(freedCount)）。
        // 相比 while+TCS 方案：精确计数（无 thundering herd）。
        // 关键：本信号量与下方 _sendLock 在 Dispose 时【绝不 Dispose】——SemaphoreSlim.Dispose()
        // 本身不唤醒 WaitAsync，且其 ct 取消回调异步调度，Dispose 会撕裂内部状态使等待者永久悬挂。
        // 唤醒统一由连接级 _disposeCts.Cancel() 负责（见 Dispose）。
        private readonly SemaphoreSlim _kWindowSem;

        // 连接级取消源：Dispose 时 Cancel，唤醒所有 k 窗口等待者（SemaphoreSlim.Dispose 本身不唤醒 WaitAsync）。
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();

        private bool _active;
        private bool _disposed;
        private bool _closeNotified;                       // 保证 ConnectionClosed 仅派发一次
        private ConnectionCloseReason _closeReason = ConnectionCloseReason.Unknown;

        /// <summary>是否检查序列号（默认 true）。</summary>
        public bool CheckSequenceNumbers { get; set; } = true;

        /// <summary>数据传输是否已激活（收到/发送 STARTDT_CON 后）。</summary>
        public bool IsActive => _active;

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
        /// <param name="isServerSide">是否服务端侧（影响 STARTDT/STOPDT 处理）。</param>
        public ApduConnection(APCIParameters apciParameters, ApplicationLayerParameters alParameters,
            IApduSink sink, bool isServerSide)
        {
            _apci = apciParameters ?? throw new ArgumentNullException(nameof(apciParameters));
            _al = alParameters ?? throw new ArgumentNullException(nameof(alParameters));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _isServerSide = isServerSide;

            _maxSent = _apci.K;
            _kBuffer = new SentApdu[_maxSent];
            _kWindowSem = new SemaphoreSlim(_maxSent, _maxSent);
            ResetT3Timeout(Now);
            _lastConfirmationTime = Now;
        }

        private static long Now => Environment.TickCount64;

        // ═══════════════════════════════════════════════════════════════
        //  接收侧（由传输层在收到数据时同步调用；零拷贝、不阻塞、不 await）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 处理收到的 I 帧。<paramref name="asdu"/> 为零拷贝负载切片。
        /// </summary>
        /// <returns>false 表示序列号错误，传输层应关闭连接。</returns>
        public bool OnIFrame(int frameSendSeq, int frameRecvSeq, ReadOnlySpan<byte> asdu)
        {
            long now = Now;

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
                if (frameSendSeq != _receiveSeq)
                    return false;

                // 校验对端 N(R)，并从 k 缓冲移除已确认帧
                if (!CheckAndRemoveConfirmed(frameRecvSeq))
                    return false;

                _receiveSeq = (_receiveSeq + 1) % 32768;
                _unconfirmedReceived++;
            }

            // 同步分发给用户（零拷贝视图，多订阅者）。用户回调在锁外执行，避免重入死锁。
            if (asdu.Length > 0)
            {
                var view = new AsduView(asdu, _al);
                // 过短的 ASDU（头部不全）属协议错误，直接关闭连接，避免用户回调 IndexOutOfRange
                if (!view.IsValid)
                    return false;
                AsduReceived?.Invoke(in view);
            }

            ResetT3Timeout(now);
            return true;
        }

        /// <summary>处理收到的 S 帧（仅携带对端 N(R)）。</summary>
        /// <returns>false 表示序列号错误。</returns>
        public bool OnSFrame(int frameRecvSeq)
        {
            if (!CheckAndRemoveConfirmed(frameRecvSeq))
                return false;

            ResetT3Timeout(Now);
            return true;
        }

        /// <summary>处理收到的 U 帧（控制帧）。</summary>
        public void OnUFrame(byte uFunction)
        {
            _uMessageTimeout = 0;

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

            // w 阈值：达到未确认上限则立即发 S 确认
            if (_unconfirmedReceived >= _apci.W)
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
        public async ValueTask<int> SendAsduAsync(PooledApduWriter writer, CancellationToken cancellationToken)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            // k 窗口背压：信号量计 K 个许可 = K 个在途未确认槽位。对端确认时 Release。
            // 注意：SemaphoreSlim.Dispose() 本身【不会】唤醒等待中的 WaitAsync（.NET 行为），
            // 故用连接级 _disposeCts 与调用方 token 链接：Dispose 时 Cancel → WaitAsync 抛 OCE，不悬挂。
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            try
            {
                await _kWindowSem.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposed)
            {
                throw new OperationCanceledException("connection has been disposed");
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException("connection has been disposed");
            }

            // 拿到许可后若连接已关，归还许可并失败（避免泄漏槽位）
            if (_disposed)
            {
                _kWindowSem.Release();
                throw new OperationCanceledException("connection has been disposed");
            }

            // 持有 k 槽后，若后续步骤（获取发送锁 / 写入 sink / 登记 k 缓冲）抛异常，必须归还槽位——
            // 该 I 帧并未成功发送/确认，否则许可会永久泄漏（直到 GC），窗口逐步收窄直至死锁。
            // 调用方取消（cancellationToken）与非取消型发送失败（如 sink 抛异常）均需归还。
            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int seqUsed;
                    int nextSeq;
                    ReadOnlyMemory<byte> apdu;

                    // 分配 N(S) 并编码 APDU（短临界区，不 await）
                    lock (_kLock)
                    {
                        seqUsed = _sendSeq;
                        apdu = writer.FinishIFormat(_sendSeq, _receiveSeq);

                        nextSeq = (_sendSeq + 1) % 32768;
                        _sendSeq = nextSeq;

                        _unconfirmedReceived = 0;
                        _t2Triggered = false;
                    }

                    // 实际发送（可能抛 OCE/IO 异常）。只有发送成功才登记 k 缓冲，
                    // 保证异常路径下 _kBuffer 与 k 槽一致（无幻影条目、不会多释放槽位）。
                    await _sink.SendAsync(apdu, cancellationToken).ConfigureAwait(false);

                    // 发送成功：登记到 k 缓冲。SeqNo 存自增后的序号（N(S)+1），
                    // 与对端回送的 N(R)（下一个期望序号）语义一致，
                    // CheckAndRemoveConfirmed 才能正确匹配确认区间（对齐原版语义）。
                    lock (_kLock)
                    {
                        int newIndex = _oldest == -1 ? 0 : (_newest + 1) % _maxSent;
                        _kBuffer[newIndex].SeqNo = nextSeq;
                        _kBuffer[newIndex].SentTime = Now;
                        _newest = newIndex;
                        if (_oldest == -1)
                            _oldest = newIndex;
                    }

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
                _kWindowSem.Release();
                throw;
            }
        }

        /// <summary>发送 STARTDT_ACT 并等待 STARTDT_CON。</summary>
        public async ValueTask StartDataTransferAsync(CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _startDtConWaiter, waiter);
            await SendUAsync(ApduCodec.StartDtActMsg, cancellationToken).ConfigureAwait(false);
            _uMessageTimeout = Now + _apci.T1 * 1000L;
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>发送 STOPDT_ACT 并等待 STOPDT_CON。</summary>
        public async ValueTask StopDataTransferAsync(CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _stopDtConWaiter, waiter);
            await SendUAsync(ApduCodec.StopDtActMsg, cancellationToken).ConfigureAwait(false);
            _uMessageTimeout = Now + _apci.T1 * 1000L;
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>发送 TESTFR_ACT。</summary>
        public ValueTask SendTestFrActAsync(CancellationToken cancellationToken)
            => SendUAsync(ApduCodec.TestFrActMsg, cancellationToken);

        private async ValueTask SendUAsync(byte[] uFrame, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sink.SendAsync(uFrame, cancellationToken).ConfigureAwait(false);
                ResetT3Timeout(Now);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async ValueTask SendSAckAsync(CancellationToken cancellationToken)
        {
            // 从池租借 6 字节缓冲（避免每条 S 确认的热路径堆分配），发送完成后归还。
            byte[] buf = ArrayPool<byte>.Shared.Rent(ApduCodec.ApciLength);
            int nr;
            lock (_kLock)
            {
                nr = _receiveSeq;
                _unconfirmedReceived = 0;
                _t2Triggered = false;
                _lastConfirmationTime = Now;
            }
            ApduCodec.WriteSFormatHeader(buf, nr);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sink.SendAsync(buf.AsMemory(0, ApduCodec.ApciLength), cancellationToken).ConfigureAwait(false);
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
            long now = Now;

            // T3：空闲则发 TESTFR_ACT
            if (now > _nextT3Timeout)
            {
                if (_outstandingTestFrCon > 2)
                    return false; // TESTFR_CON 超时

                await SendUAsync(ApduCodec.TestFrActMsg, cancellationToken).ConfigureAwait(false);
                _uMessageTimeout = now + _apci.T1 * 1000L;
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
                    && (now - _lastConfirmationTime) >= _apci.T2 * 1000L;
            }
            if (t2Due)
            {
                await SendSAckAsync(cancellationToken).ConfigureAwait(false);
            }

            // T1（U 帧）：等待 TESTFR_CON/STARTDT_CON 超时
            if (_uMessageTimeout != 0 && now > _uMessageTimeout)
                return false;

            // T1（I 帧）：最老未确认 I 帧超过 T1 未被确认
            lock (_kLock)
            {
                if (_oldest != -1)
                {
                    if ((now - _kBuffer[_oldest].SentTime) >= _apci.T1 * 1000L)
                        return false;
                }
            }

            return true;
        }

        /// <summary>返回下一次超时检查建议的等待时长（毫秒），供调用方 Task.Delay。</summary>
        public int SuggestedTimerIntervalMs => 100;

        // ═══════════════════════════════════════════════════════════════
        //  内部：序列号窗口
        // ═══════════════════════════════════════════════════════════════

        /// <summary>校验对端 N(R) 合法性并从 k 缓冲移除已确认帧。返回 false 表示序列号越界。</summary>
        private bool CheckAndRemoveConfirmed(int seqNo)
        {
            if (!CheckSequenceNumbers)
                return true;

            int freedCount = 0;

            lock (_kLock)
            {
                bool valid = false;
                bool overflow = false;
                int oldestValid = -1;

                if (_oldest == -1)
                {
                    if (seqNo == _sendSeq)
                        valid = true;
                }
                else
                {
                    if (_kBuffer[_oldest].SeqNo <= _kBuffer[_newest].SeqNo)
                    {
                        if (seqNo >= _kBuffer[_oldest].SeqNo && seqNo <= _kBuffer[_newest].SeqNo)
                            valid = true;
                    }
                    else
                    {
                        if (seqNo >= _kBuffer[_oldest].SeqNo || seqNo <= _kBuffer[_newest].SeqNo)
                            valid = true;
                        overflow = true;
                    }

                    oldestValid = _kBuffer[_oldest].SeqNo == 0 ? 32767 : _kBuffer[_oldest].SeqNo - 1;
                    if (oldestValid == seqNo)
                        valid = true;
                }

                if (!valid)
                    return false;

                if (_oldest != -1)
                {
                    do
                    {
                        if (!overflow && seqNo < _kBuffer[_oldest].SeqNo)
                            break;
                        if (seqNo == oldestValid)
                            break;

                        if (_kBuffer[_oldest].SeqNo == seqNo)
                        {
                            if (_oldest == _newest)
                                _oldest = -1;
                            else
                                _oldest = (_oldest + 1) % _maxSent;
                            freedCount++;
                            break;
                        }

                        _oldest = (_oldest + 1) % _maxSent;
                        freedCount++;

                        int checkIndex = (_newest + 1) % _maxSent;
                        if (_oldest == checkIndex)
                        {
                            _oldest = -1;
                            break;
                        }
                    } while (true);
                }
            }

            if (freedCount > 0)
                _kWindowSem.Release(freedCount);

            return true;
        }

        private void ResetT3Timeout(long now) => _nextT3Timeout = now + _apci.T3 * 1000L;

        /// <summary>最近一次 <see cref="ConnectionClosed"/> 事件的断开原因（只读；连接存活期间为 Unknown）。</summary>
        public ConnectionCloseReason CloseReason => _closeReason;

        private void Raise(ApduConnectionEvent ev) => EventHandler?.Invoke(ev);

        /// <summary>
        /// 通知订阅方连接已断开（非主动）。由传输层在收到远端关闭/超时/协议错误后调用；
        /// 主动 <see cref="Iec104Client.DisconnectAsync"/> / <see cref="Iec104Server.StopAsync"/> 不应调用本方法。
        /// </summary>
        /// <param name="reason">断开原因；若此前已通过 <see cref="MarkCloseReason"/> 标记了更具体的原因则忽略本参数。</param>
        /// <remarks>
        /// 幂等：仅首次调用会派发 <see cref="ApduConnectionEvent.ConnectionClosed"/>（之后即使重复关闭或 Dispose 也不会重复通知）。
        /// 若已 Dispose 则无操作。
        /// </remarks>
        public void NotifyClosed(ConnectionCloseReason reason = ConnectionCloseReason.RemoteClosed)
        {
            if (_disposed || _closeNotified)
                return;

            if (_closeReason == ConnectionCloseReason.Unknown)
                _closeReason = reason;

            _closeNotified = true;
            Raise(ApduConnectionEvent.ConnectionClosed);
        }

        /// <summary>
        /// 预先标记断开原因。由超时 / 协议错误检测路径在底层 socket 关闭回调（<c>OnTcpClosed</c>）之前调用，
        /// 使最终派发的 <see cref="ConnectionClosed"/> 携带准确原因。仅在尚未派发、且尚未标记过时生效。
        /// </summary>
        internal void MarkCloseReason(ConnectionCloseReason reason)
        {
            if (_closeNotified || _closeReason != ConnectionCloseReason.Unknown)
                return;

            _closeReason = reason;
        }

        private static void Signal(ref TaskCompletionSource<bool> field)
        {
            TaskCompletionSource<bool> waiter = Interlocked.Exchange(ref field, null);
            waiter?.TrySetResult(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 唤醒 U 帧握手等待者
            Signal(ref _startDtConWaiter);
            Signal(ref _stopDtConWaiter);
            // 取消连接级 CTS：唤醒所有卡在 _kWindowSem.WaitAsync 的 k 窗口等待者（抛 OCE），不悬挂。
            // 注意：本方法【不 Dispose 任何 SemaphoreSlim】（_kWindowSem / _sendLock）。
            // SemaphoreSlim.Dispose() 本身不唤醒 WaitAsync，且其 ct 取消回调异步调度——
            // 若先 Cancel 再 Dispose，回调可能在 Dispose 之后才执行，从而撕裂内部状态使等待者永久悬挂
            // （实测验证过的坑）。两个信号量均无实质非托管泄漏，交由 GC 终结即可。
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
    }
}
