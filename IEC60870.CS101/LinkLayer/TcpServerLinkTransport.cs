/*
 *  TcpServerLinkTransport.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket;
using TouchSocket.Core;
using TouchSocket.Sockets;


namespace IEC60870.CS101.LinkLayer
{
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
                    // 虚拟串口语义：仅允许单活动链路（与原 TcpServerVirtualSerialPort 一致）。
                    // 新连接占用时关闭上一会话，避免两路字节写入同一队列、相互交错导致 FT1.2 帧损坏（代码评审 #17）。
                    Session prev = Owner._activeSession;
                    Owner._activeSession = this;
                    if (prev != null && prev != this)
                    {
                        try { await prev.CloseAsync("replaced by new connection").ConfigureAwait(false); }
                        catch { /* ignore */ }
                    }
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
                var bb = e.ByteBlock;
                if (bb != null && bb.Length > 0 && Owner != null)
                    Owner._queue.Write(bb.TotalMemory.Span.Slice(0, bb.Length));
                await base.OnTcpReceived(e).ConfigureAwait(false);
            }
        }

        private readonly AsyncByteQueue _queue = new AsyncByteQueue();
        private LinkLayerParameters _llParams;
        private Action<string> _log;
        private int _msgTimeout = 50;
        private int _charTimeout = 50;
        private EventHandler _portDenied;
        internal Session _activeSession;

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

        public async Task StartAsync(int port, CancellationToken ct)
        {
            var config = new TouchSocketConfig();
            config.SetListenIPHosts(new IPHost(port));
            await SetupAsync(config).ConfigureAwait(false);
            await base.StartAsync().ConfigureAwait(false);
        }

        public async ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int n = await FT12Framer.ReadFrameAsync(_queue, buffer, _llParams, _msgTimeout, _charTimeout, _log, ct)
                .ConfigureAwait(false);

            if (n > 0)
                _log("RECV " + BitConverter.ToString(buffer.Span.Slice(0, n).ToArray()));

            return n;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _log("SEND " + BitConverter.ToString(data.Span.ToArray()));
            if (_activeSession != null)
            {
                try
                {
                    await _activeSession.SendAsync(data).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _portDenied?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public new void Dispose()
        {
            _queue.Close();
            try { base.Dispose(); } catch { }
        }
    }
}
