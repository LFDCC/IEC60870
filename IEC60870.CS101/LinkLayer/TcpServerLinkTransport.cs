//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;


namespace IEC60870.CS101;

    /// <summary>
    /// TCP 服务端虚拟串口（隧道）。基于 TouchSocket <see cref="TcpService{TClient}"/>，每个连接对应一个
    /// 会话；仅维护单条活动链路（与原 <c>TcpServerVirtualSerialPort</c> 语义一致）。收到的字节推入
    /// <see cref="AsyncByteQueue"/>，由 <see cref="FT12Framer"/> 完成 FT1.2 帧定界。
    /// </summary>
    internal sealed class TcpServerLinkTransport : TcpService<TcpServerLinkTransport.Session>, ISerialLinkTransport
    {
        internal sealed class Session : TcpSessionClient
        {
            internal TcpServerLinkTransport Owner;

            protected override async Task OnTcpConnected(ConnectedEventArgs e)
            {
                if (Owner != null)
                {
                    // 虚拟串口语义：仅允许单活动链路（与原 TcpServerVirtualLinkTransport 一致）。
                    // 新连接占用时关闭上一会话，避免两路字节写入同一队列、相互交错导致 FT1.2 帧损坏（代码评审 #17）。
                    var prev = Owner._activeSession;
                    Owner._activeSession = this;
                    if (prev != null && prev != this)
                    {
                        try
                    {
                        await prev.CloseAsync("replaced by new connection").ConfigureAwait(false);
                    }
                    catch { /* ignore */ }
                }

                    // 新链路建立：先丢弃上一会话残留在字节队列里的数据。
                    // 旧会话关闭与连接替换存在时序差：若旧会话的 OnTcpClosed 晚于
                    // 新会话建立，它的残留字节会被新会话的链路层当成"有效帧"处理，
                    // 把刚复位的文件/应用层状态再次推回非空闲态（重连后首次文件传输
                    // 被当成 Unexpected SELECT FILE 忽略）。此处是无条件清，
                    // 因为此刻新连接还不可能已经投递过数据。
                    Owner._queue.Clear();

                    // 同时复位应用层状态（1/2 类队列 + 文件传输状态机）。
                    try
                    {
                        Owner.NewLinkEstablished?.Invoke();
                    }
                    catch { /* 复位回调不得影响建链 */ }
                }
            await base.OnTcpConnected(e).ConfigureAwait(false);
        }

        protected override async Task OnTcpClosed(ClosedEventArgs e)
            {
                if (Owner != null && Owner._activeSession == this)
                {
                    Owner._activeSession = null;
                    Owner._queue.Clear(); // 丢弃残留字节，避免下一个客户端收到上一客户端的帧
                }
                await base.OnTcpClosed(e).ConfigureAwait(false);
            }

            protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
            {
                // 只接受"当前活动会话"的字节。被替换的旧会话在 CloseAsync 完成前
                // 仍可能投递已排队的收包回调；这些字节若进入共享队列，会与新会话的
                // 帧交错，导致分帧失步、新链路的首个请求帧被丢弃
                // （表现为重连后第一次文件传输偶发超时，且只在负载高时出现）。
                if (!e.Memory.IsEmpty && Owner != null
                    && ReferenceEquals(Owner._activeSession, this))
                {
                    Owner._queue.Write(e.Memory.Span);
                }
                else if (!e.Memory.IsEmpty && Owner != null)
                {
                    Interlocked.Increment(ref Owner.DroppedNonActiveReceive);
                }

                await base.OnTcpReceived(e).ConfigureAwait(false);
            }
        }

        private readonly AsyncByteQueue _queue = new AsyncByteQueue();
        private LinkLayerParameters _llParams;
        private Action<string> _log;
        private int _msgTimeout = 50;
        private int _charTimeout = 50;
        private EventHandler _portDenied;
        /// <summary>
        /// 当前活动会话。
        /// </summary>
        /// <remarks>
        /// 必须 volatile：由 TouchSocket 的建连/断连线程写，由链路层发送线程与
        /// 收包线程读。普通字段在 x64 上虽然引用读写原子，但没有内存屏障，
        /// 发送线程可能长时间读到旧值 null —— 表现为"链路明明在线，但从站偶发
        /// 把一帧应答丢弃"（长跑里表现为重连后第一次文件传输偶发超时，
        /// 传输层计数 dropNoSession 恰好 +1）。
        /// </remarks>
        internal volatile Session _activeSession;

        /// <summary>新链路建立时的回调（服务端据此复位应用层状态）。</summary>
        internal event Action NewLinkEstablished;

        public event EventHandler PortDenied
        {
            add { _portDenied += value; }
            remove { _portDenied -= value; }
        }

        public TcpServerLinkTransport(LinkLayerParameters llParams, Action<string> log)
        {
            _llParams = llParams;
            _log = log;
        }

        protected override Session NewClient() => new Session { Owner = this };

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _msgTimeout = messageTimeout;
            _charTimeout = characterTimeout;
        }

        /// <summary>注册「新链路建立」回调（由 Iec101Server 用于复位应用层状态）。</summary>

        public async Task StartAsync(int port, CancellationToken ct, Action<TouchSocketConfig> configureConfig = null)
        {
            var config = new TouchSocketConfig();
            config.SetListenIPHosts(new IPHost(port));
            // 用户自定义配置在库默认项之后应用
            configureConfig?.Invoke(config);
            await SetupAsync(config).ConfigureAwait(false);
            await base.StartAsync().ConfigureAwait(false);
        }

        public async ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var n = await FT12Framer.ReadFrameAsync(_queue, buffer, _llParams, _msgTimeout, _charTimeout, _log, ct)
                .ConfigureAwait(false);

            if (n > 0)
        {
            _log("RECV " + BitConverter.ToString(buffer.Span.Slice(0, n).ToArray()));
        }

        return n;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _log("SEND " + BitConverter.ToString(data.Span.ToArray()));

            var session = _activeSession;

            if (session == null)
            {
                // 重连窗口：旧会话已关闭、新会话尚未完成建连。此刻直接丢弃会让
                // 链路层误以为帧已发出（随后等不到 ACK → 重传 → 对端按重复 FCB 丢弃
                // → 链路复位），长跑中表现为重连后第一次文件传输偶发超时。
                // 短暂等待新会话出现（正常重连在毫秒级完成），仍无会话才计为丢弃。
                for (var i = 0; i < 10 && session == null && !ct.IsCancellationRequested; i++)
                {
                    try
                    {
                        await Task.Delay(10, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    session = _activeSession;
                }
            }

            if (session != null)
            {
                try
                {
                    await session.SendAsync(data, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref SentFrames);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref SendErrors);
                    _portDenied?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                // 确实没有活动会话（链路真的断开）：计数并告警。
                var dropped = Interlocked.Increment(ref DroppedNoSession);
                if (dropped <= 3 || dropped % 100 == 0)
                {
                    _log($"WARN: no active session - frame dropped (total {dropped})");
                }
            }
        }

        internal long DroppedNoSession;
        internal long SendErrors;
        internal long SentFrames;
        internal long DroppedNonActiveReceive;

        /// <summary>Dispose 钩子：关闭字节队列并沿 TouchSocket 生命周期释放底层 TcpService。</summary>
        protected override void SafetyDispose(bool disposing)
        {
            _queue.SafeDispose();
            base.SafetyDispose(disposing);
        }
    }
