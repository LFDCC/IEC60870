/*
 *  Iec104Client.cs
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
 *  IEC60870.Core.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;
using IEC60870.Core;
using IEC60870.Core.Time;
using IEC60870.Core.InformationObjects;



namespace IEC60870.CS104
{
    /// <summary>
    /// IEC 60870-5-104 异步客户端（主站）。基于 TouchSocket <see cref="TcpClient"/>，
    /// 对接 <see cref="ApduConnection"/> 异步状态机，热路径零拷贝、零 per-frame 分配。
    /// </summary>
    /// <remarks>
    /// 用法：
    /// <code>
    /// await using var client = new Iec104Client("127.0.0.1", 2404);
    /// client.AsduReceived = (in AsduView a) => { /* 处理 */ };
    /// await client.ConnectAsync();
    /// await client.StartDataTransferAsync();
    /// await client.SendAsync(asdu);
    /// </code>
    /// </remarks>
    public sealed class Iec104Client : TcpClient, IApduSink, IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly APCIParameters _apci;
        private readonly ApplicationLayerParameters _al;
        private readonly ClientSslOption _sslOption;

        private ApduConnection _connection;
        private CancellationTokenSource _cts;
        private Task _timerLoop;
        private bool _dataTransferStarted;

        // 接收侧粘包重组器（单线程访问：TouchSocket 接收回调串行）
        private ApduFramer _framer;

        /// <summary>收到 ASDU 的同步零拷贝回调。</summary>
        public AsduViewHandler AsduReceived
        {
            get => _asduReceived;
            set
            {
                _asduReceived = value;
                if (_connection != null)
                    _connection.AsduReceived = value;
            }
        }
        private AsduViewHandler _asduReceived;

        /// <summary>连接层事件回调（STARTDT_CON 等）。</summary>
        public Action<ApduConnectionEvent> ConnectionEvent
        {
            get => _connEvent;
            set
            {
                _connEvent = value;
                if (_connection != null)
                    _connection.EventHandler = value;
            }
        }
        private Action<ApduConnectionEvent> _connEvent;

        /// <summary>应用层参数（可在连接前调整字段宽度）。</summary>
        public ApplicationLayerParameters Parameters => _al;

        /// <summary>数据传输是否已激活。</summary>
        public bool IsActivated => _connection?.IsActive ?? false;

        /// <summary>
        /// 连接成功后是否自动发送 STARTDT_ACT 激活数据传输。默认 <c>true</c>，与原库
        /// <c>autostart</c> 行为一致；设为 <c>false</c> 则需手动调用 <see cref="StartDataTransferAsync"/>。
        /// </summary>
        public bool Autostart { get; set; } = true;

        public Iec104Client(string host, int port = 2404,
            APCIParameters apciParameters = null,
            ApplicationLayerParameters alParameters = null,
            ClientSslOption sslOption = null,
            bool autostart = true)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _apci = apciParameters ?? new APCIParameters();
            _al = alParameters ?? new ApplicationLayerParameters();
            _sslOption = sslOption;
            Autostart = autostart;
        }

        // ── IApduSink ─────────────────────────────────────────────────

        ValueTask IApduSink.SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
            => new ValueTask(base.SendAsync(apdu));

        bool IApduSink.IsConnected => Online;

        // ── 连接生命周期 ──────────────────────────────────────────────

        /// <summary>建立 TCP 连接（不自动 STARTDT）。</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connection = new ApduConnection(_apci, _al, this, isServerSide: false)
            {
                AsduReceived = _asduReceived,
                EventHandler = _connEvent
            };
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _framer = new ApduFramer();

            var config = new TouchSocketConfig();
            config.SetRemoteIPHost(new IPHost($"{_host}:{_port}"));
            if (_sslOption != null)
                config.SetClientSslOption(_sslOption);

            await SetupAsync(config).ConfigureAwait(false);
            await base.ConnectAsync(_apci.T0 * 1000, cancellationToken).ConfigureAwait(false);

            if (Autostart)
                await StartDataTransferAsync(cancellationToken).ConfigureAwait(false);

            _timerLoop = Task.Run(() => TimerLoopAsync(_cts.Token));
        }

        /// <summary>发送 STARTDT_ACT 并等待 STARTDT_CON，激活数据传输。</summary>
        /// <remarks>幂等：已激活或 <see cref="Autostart"/> 已自动触发时，重复调用为无操作。</remarks>
        public async Task StartDataTransferAsync(CancellationToken cancellationToken = default)
        {
            if (_dataTransferStarted)
                return;

            _dataTransferStarted = true;
            await _connection.StartDataTransferAsync(Link(cancellationToken)).AsTask().ConfigureAwait(false);
        }

        /// <summary>发送 STOPDT_ACT 并等待 STOPDT_CON。</summary>
        public Task StopDataTransferAsync(CancellationToken cancellationToken = default)
            => _connection.StopDataTransferAsync(Link(cancellationToken)).AsTask();

        /// <summary>发送一个 ASDU（I 帧）。k 窗口满时异步背压等待，不阻塞线程。</summary>
        public async Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default)
        {
            using var writer = new PooledApduWriter();
            asdu.Encode(writer, _al);
            await _connection.SendAsduAsync(writer, Link(cancellationToken)).ConfigureAwait(false);
        }

        // ── 标准命令便捷方法（异步）─────────────────────────────────
        // 下列方法构造对应的标准 C_* 命令 ASDU 并通过 SendAsync 发送（I 帧）。SendAsync 在 k
        // 窗口满时内部异步背压，调用方无需阻塞；连接断开等异常由 SendAsync 原样抛出。

        /// <summary>发送总召唤命令（C_IC_NA_1，typeID 100）。</summary>
        /// <param name="cot">Cause of transmission</param>
        /// <param name="ca">Common address</param>
        /// <param name="qoi">Qualifier of interrogation（20 = 站召唤）</param>
        public async Task SendInterrogationCommandAsync(CauseOfTransmission cot, int ca, byte qoi, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.Interrogation(_al, cot, ca, qoi), cancellationToken).ConfigureAwait(false);

        /// <summary>发送计数量总召唤命令（C_CI_NA_1，typeID 101）。</summary>
        public async Task SendCounterInterrogationCommandAsync(CauseOfTransmission cot, int ca, byte qcc, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.CounterInterrogation(_al, cot, ca, qcc), cancellationToken).ConfigureAwait(false);

        /// <summary>发送读命令（C_RD_NA_1，typeID 102）。COT 固定 REQUEST，用于循环读取单个点。</summary>
        public async Task SendReadCommandAsync(int ca, int ioa, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.Read(_al, ca, ioa), cancellationToken).ConfigureAwait(false);

        /// <summary>发送时钟同步命令（C_CS_NA_1，typeID 103）。</summary>
        public async Task SendClockSyncCommandAsync(int ca, CP56Time2a time, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.ClockSync(_al, ca, time), cancellationToken).ConfigureAwait(false);

        /// <summary>发送测试命令（C_TS_NA_1，typeID 104）。</summary>
        public async Task SendTestCommandAsync(int ca, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.Test(_al, ca), cancellationToken).ConfigureAwait(false);

        /// <summary>发送带时标的测试命令（C_TS_TA_1，typeID 107）。</summary>
        public async Task SendTestCommandWithCP56Time2aAsync(int ca, ushort tsc, CP56Time2a time, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.TestWithCP56Time2a(_al, ca, tsc, time), cancellationToken).ConfigureAwait(false);

        /// <summary>发送复位进程命令（C_RP_NA_1，typeID 105）。</summary>
        public async Task SendResetProcessCommandAsync(CauseOfTransmission cot, int ca, byte qrp, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.ResetProcess(_al, cot, ca, qrp), cancellationToken).ConfigureAwait(false);

        /// <summary>发送延时获取命令（C_CD_NA_1，typeID 106）。</summary>
        public async Task SendDelayAcquisitionCommandAsync(CauseOfTransmission cot, int ca, CP16Time2a delay, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.DelayAcquisition(_al, cot, ca, delay), cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// 发送通用控制命令。typeId 须与 sc 的类型匹配：
        /// C_SC_NA_1→SingleCommand、C_DC_NA_1→DoubleCommand、C_RC_NA_1→StepCommand、
        /// C_SC_TA_1→SingleCommandWithCP56Time2a、C_SE_NA_1→SetpointCommandNormalized、
        /// C_SE_NB_1→SetpointCommandScaled、C_SE_NC_1→SetpointCommandShort、C_BO_NA_1→Bitstring32Command。
        /// </summary>
        /// <param name="cot">Cause of transmission（发起控制序列用 ACTIVATION）</param>
        /// <param name="ca">Common address</param>
        /// <param name="sc">控制命令 InformationObject</param>
        public async Task SendControlCommandAsync(CauseOfTransmission cot, int ca, InformationObject sc, CancellationToken cancellationToken = default)
            => await SendAsync(CommandBuilder.Control(_al, cot, ca, sc), cancellationToken).ConfigureAwait(false);

        /// <summary>主动断开连接。</summary>
        public async Task DisconnectAsync()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { await CloseAsync("client disconnect").ConfigureAwait(false); } catch { /* ignore */ }
            await CleanupAsync().ConfigureAwait(false);
        }

        private CancellationToken Link(CancellationToken ct)
            => ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token : _cts.Token;

        // ── 接收（TouchSocket 回调，串行）─────────────────────────────

        protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
        {
            ByteBlock bb = e.ByteBlock;
            if (bb != null && bb.Length > 0)
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

        // ── 定时器循环（T1/T2/T3）─────────────────────────────────────

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

        protected override async Task OnTcpClosed(ClosedEventArgs e)
        {
            await CleanupAsync().ConfigureAwait(false);
            await base.OnTcpClosed(e).ConfigureAwait(false);
        }

        private ValueTask CleanupAsync()
        {
            _connection?.Dispose();
            _framer?.Dispose();
            _framer = null;
            return default;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _cts?.Dispose();
            base.Dispose();
        }
    }
}
