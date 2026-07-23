/*
 *  SerialTransceiverFT12.cs
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
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;


namespace IEC60870.CS101.LinkLayer
{
    /// <summary>
    /// 串口 FT1.2 收发器（异步）。基于 <see cref="SerialPort.BaseStream"/> 的异步读写，
    /// 通过 <see cref="FT12Framer"/> 完成帧定界。实现 <see cref="ISerialLinkTransport"/>。
    /// </summary>
    internal sealed class SerialTransceiverFT12 : ISerialLinkTransport
    {
        private Stream _serialStream = null;
        private SerialPort _port = null;

        private Action<string> _debugLog;
        private LinkLayerParameters _linkLayerParameters;
        private int _messageTimeout = 50;
        private int _characterTimeout = 50;

        private readonly StreamByteSource _source;
        private EventHandler _portDenied;

        public event EventHandler PortDenied
        {
            add { _portDenied += value; }
            remove { _portDenied -= value; }
        }

        public SerialTransceiverFT12(SerialPort port, LinkLayerParameters linkLayerParameters, Action<string> debugLog)
        {
            _port = port;
            _serialStream = port.BaseStream;
            _debugLog = debugLog;
            _linkLayerParameters = linkLayerParameters;
            _source = new StreamByteSource(_serialStream);
        }

        public SerialTransceiverFT12(Stream serialStream, LinkLayerParameters linkLayerParameters, Action<string> debugLog)
        {
            _port = null;
            _serialStream = serialStream;
            _debugLog = debugLog;
            _linkLayerParameters = linkLayerParameters;
            _source = new StreamByteSource(_serialStream);
        }

        public int BaudRate
        {
            get
            {
                if (_port != null)
                    return _port.BaudRate;
                else
                    return 10000000;
            }
        }

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _messageTimeout = messageTimeout;
            _characterTimeout = characterTimeout;
        }

        public async ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int n = await FT12Framer.ReadFrameAsync(_source, buffer, _linkLayerParameters,
                _messageTimeout, _characterTimeout, _debugLog, ct).ConfigureAwait(false);

            if (n > 0)
                _debugLog("RECV " + BitConverter.ToString(buffer.Span.Slice(0, n).ToArray()));

            return n;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _debugLog("SEND " + BitConverter.ToString(data.Span.ToArray()));

            try
            {
                await _serialStream.WriteAsync(data, ct).ConfigureAwait(false);
                await _serialStream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                OnPortDenied();
            }
            catch (IOException)
            {
                OnPortDenied();
            }
            catch (ObjectDisposedException)
            {
                OnPortDenied();
            }
        }

        private void OnPortDenied()
        {
            if (_portDenied != null)
            {
                try { _portDenied(this, EventArgs.Empty); } catch { }
            }
        }

        public void Dispose()
        {
            // 不在此关闭 _serialStream：串口由 CS101Master/ServerBase 拥有；TCP 隧道由各自传输层管理。
        }
    }
}
