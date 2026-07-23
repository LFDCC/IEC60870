/*
 *  Iec104Session.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;
using IEC60870.Core;



namespace IEC60870.CS104
{
    /// <summary>
    /// IEC 60870-5-104 服务端每连接会话。由 <see cref="Iec104Server"/> 自动创建，
    /// 内含独立的 <see cref="ApduConnection"/> 异步状态机（服务端角色）。
    /// </summary>
    public sealed class Iec104Session : TcpSessionClient, IApduSink
    {
        private ApduConnection _connection;
        private ApduFramer _framer;
        private CancellationTokenSource _cts;
        private Task _timerLoop;
        private Iec104Server _server;

        /// <summary>该会话对应的连接层状态机。</summary>
        public ApduConnection Connection => _connection;

        /// <summary>数据传输是否已激活（收到 STARTDT_ACT 后）。</summary>
        public bool IsActivated => _connection?.IsActive ?? false;

        // ── IApduSink ─────────────────────────────────────────────────

        ValueTask IApduSink.SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            => new ValueTask(base.SendAsync(apdu));

        bool IApduSink.IsConnected => Online;

        /// <summary>向该会话对端发送一个 ASDU（I 帧）。</summary>
        public async Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default)
        {
            using var writer = new PooledApduWriter();
            asdu.Encode(writer, _server.Parameters);
            await _connection.SendAsduAsync(writer, LinkToken(cancellationToken)).ConfigureAwait(false);
        }

        // ── 生命周期 ──────────────────────────────────────────────────

        protected override async Task OnTcpConnected(ConnectedEventArgs e)
        {
            _server = (Iec104Server)this.Service;
            _cts = new CancellationTokenSource();
            _framer = new ApduFramer();
            _connection = new ApduConnection(_server.ApciParameters, _server.Parameters, this, isServerSide: true)
            {
                AsduReceived = _server.RaiseAsduReceived(this),
                EventHandler = ev => _server.RaiseConnectionEvent(this, ev)
            };

            _server.RegisterSession(this);
            _timerLoop = Task.Run(() => TimerLoopAsync(_cts.Token));

            await base.OnTcpConnected(e).ConfigureAwait(false);
        }

        protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
        {
            ByteBlock bb = e.ByteBlock;
            if (bb != null && bb.Length > 0 && _connection != null)
            {
                _framer.Append(bb.TotalMemory.Span.Slice(0, bb.Length));

                if (!_framer.Process(_connection))
                {
                    await CloseAsync("protocol error").ConfigureAwait(false);
                    return;
                }

                await _connection.PumpAsync(_cts.Token).ConfigureAwait(false);
            }

            await base.OnTcpReceived(e).ConfigureAwait(false);
        }

        protected override async Task OnTcpClosed(ClosedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _server?.UnregisterSession(this);
            _connection?.Dispose();
            _framer?.Dispose();
            _framer = null;
            _cts?.Dispose();
            await base.OnTcpClosed(e).ConfigureAwait(false);
        }

        private async Task TimerLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && Online)
                {
                    await Task.Delay(_connection.SuggestedTimerIntervalMs, ct).ConfigureAwait(false);
                    if (!await _connection.CheckTimeoutsAsync(ct).ConfigureAwait(false))
                    {
                        await CloseAsync("timeout").ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception) { /* connection likely closing */ }
        }

        private CancellationToken LinkToken(CancellationToken ct)
            => ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token
                : _cts.Token;
    }
}
