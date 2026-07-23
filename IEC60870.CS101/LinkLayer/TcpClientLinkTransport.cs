/*
 *  TcpClientLinkTransport.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
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
    /// TCP 客户端虚拟串口（隧道）。基于 TouchSocket <see cref="TcpClient"/>，将收到的字节推入
    /// <see cref="AsyncByteQueue"/>，由 <see cref="FT12Framer"/> 在其上完成 FT1.2 帧定界。
    /// 无原始 Socket / 工作线程。
    /// </summary>
    internal sealed class TcpClientLinkTransport : TcpClient, ISerialLinkTransport
    {
        private readonly AsyncByteQueue _queue = new AsyncByteQueue();
        private LinkLayerParameters _llParams;
        private Action<string> _log;
        private int _msgTimeout = 50;
        private int _charTimeout = 50;
        private EventHandler _portDenied;
        private readonly string _host;
        private readonly int _port;

        public event EventHandler PortDenied
        {
            add { _portDenied += value; }
            remove { _portDenied -= value; }
        }

        public TcpClientLinkTransport(string host, int port, LinkLayerParameters llParams, Action<string> log)
        {
            _host = host;
            _port = port;
            _llParams = llParams;
            _log = log;
        }

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _msgTimeout = messageTimeout;
            _charTimeout = characterTimeout;
        }

        protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
        {
            var bb = e.ByteBlock;
            if (bb != null && bb.Length > 0)
                _queue.Write(bb.TotalMemory.Span.Slice(0, bb.Length));
            await base.OnTcpReceived(e).ConfigureAwait(false);
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            var config = new TouchSocketConfig();
            config.SetRemoteIPHost(new IPHost($"{_host}:{_port}"));
            await SetupAsync(config).ConfigureAwait(false);
            await base.ConnectAsync(1000, ct).ConfigureAwait(false);
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
            try
            {
                await base.SendAsync(data).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _portDenied?.Invoke(this, EventArgs.Empty);
            }
        }

        public new void Dispose()
        {
            _queue.Close();
            try { base.Dispose(); } catch { }
        }
    }
}
