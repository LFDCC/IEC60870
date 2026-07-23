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
        // 标记本次关闭是否由服务端主动 StopAsync 发起，避免为每个会话重复派发 ConnectionClosed
        private bool _intentionalClose;

        /// <summary>该会话对应的连接层状态机。</summary>
        public ApduConnection Connection => _connection;

        /// <summary>数据传输是否已激活（收到 STARTDT_ACT 后）。</summary>
        public bool IsActivated => _connection?.IsActive ?? false;

        /// <summary>
        /// 最近一次底层连接断开的原因。仅当 <see cref="Iec104Server.ConnectionEvent"/> 收到
        /// <see cref="ApduConnectionEvent.ConnectionClosed"/> 后有意义；未连接时为 <see cref="ConnectionCloseReason.Unknown"/>。
        /// </summary>
        public ConnectionCloseReason LastCloseReason => _connection?.CloseReason ?? ConnectionCloseReason.Unknown;

        // ── IApduSink ─────────────────────────────────────────────────

        ValueTask IApduSink.SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            => new ValueTask(base.SendAsync(apdu));

        bool IApduSink.IsConnected => Online;

        /// <summary>向该会话对端发送一个 ASDU（I 帧）。</summary>
        public async Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default)
        {
            using var writer = new PooledApduWriter();
            asdu.Encode(writer, _server.Parameters);
            using var link = LinkScoped(cancellationToken);
            await _connection.SendAsduAsync(writer, link.Token).ConfigureAwait(false);
        }

        /// <summary>标记本次关闭由服务端主动 StopAsync 发起，避免重复派发 ConnectionClosed。</summary>
        internal void MarkIntentionalClose() => _intentionalClose = true;

        // ── 生命周期 ──────────────────────────────────────────────────

        protected override async Task OnTcpConnected(ConnectedEventArgs e)
        {
            _server = (Iec104Server)this.Service;
            _cts = new CancellationTokenSource();
            _framer = new ApduFramer();
            _intentionalClose = false;
            _connection = new ApduConnection(_server.ApciParameters, _server.Parameters, this, isServerSide: true);
            _connection.AsduReceived += _server.RaiseAsduReceived(this);
            _connection.EventHandler += ev => _server.RaiseConnectionEvent(this, ev);

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
                    _connection.MarkCloseReason(ConnectionCloseReason.ProtocolError);
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

            // 仅在非主动关闭（客户端断开 / 超时 / 协议错误）时派发 ConnectionClosed，
            // 让服务端 ConnectionEvent 订阅者能感知“哪个会话”断开了。服务端主动 StopAsync
            // 已通过 MarkIntentionalClose 标记，主动停止不会刷屏。
            if (!_intentionalClose)
                _connection?.NotifyClosed();

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
                        _connection.MarkCloseReason(ConnectionCloseReason.Timeout);
                        await CloseAsync("timeout").ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception) { /* connection likely closing */ }
        }

        /// <summary>
        /// 将外部 token 与连接生命周期 <c>_cts</c> 链接的轻量句柄（见 <see cref="Iec104Client"/> 同名的 #5 修复）。
        /// 仅当传入可取消 token 时分配 <see cref="CancellationTokenSource"/>，并在 <c>using</c> 结束时释放。
        /// </summary>
        private readonly struct CtsLink : IDisposable
        {
            private readonly CancellationTokenSource _src;
            private readonly bool _owns;
            public CtsLink(CancellationTokenSource src, bool owns) { _src = src; _owns = owns; }
            public CancellationToken Token => _src?.Token ?? default;
            public void Dispose() { if (_owns) _src?.Dispose(); }
        }

        private CtsLink LinkScoped(CancellationToken ct)
            => ct.CanBeCanceled
                ? new CtsLink(CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token), owns: true)
                : new CtsLink(_cts, owns: false);
    }
}
