/*
 *  MasterClient.cs
 *
 *  One simulated master that connects to a single device. Each master:
 *    - counts every ASDU it receives (and validates it decodes),
 *    - cycles control commands across all C_* types and measures command RTT
 *      via the built-in "send and wait for ACTIVATION_CON" API,
 *    - issues a periodic general interrogation and measures the round-trip
 *      from the GI command to ACTIVATION_TERMINATION.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;

namespace cs104_stress_500
{
    public sealed class MasterClient : IAsyncDisposable
    {
        private readonly int _index;
        private readonly int _port;
        private readonly StressConfig _cfg;
        private readonly Metrics _metrics;
        private readonly ApplicationLayerParameters _al;

        private Iec104Client _client;
        private CancellationTokenSource _loopCts;

        private int _commandTypeCursor;
        private long _giPendingTicks;   // Stopwatch ticks of the in-flight GI command, 0 = none
        private long _giIssued;
        private long _giCompleted;

        public MasterClient(int index, int port, StressConfig cfg, Metrics metrics)
        {
            _index = index;
            _port = port;
            _cfg = cfg;
            _metrics = metrics;
            _al = new ApplicationLayerParameters();
        }

        public int Ca => _index + 1;

        public bool IsActivated => _client?.IsActivated ?? false;

        public async Task ConnectAsync(CancellationToken token)
        {
            _client = new Iec104Client("127.0.0.1", _port, null, _al);
            _client.CommandConfirmationTimeout = TimeSpan.FromSeconds(5);
            _client.AsduReceived += OnAsduReceived;
            _client.ConnectionEvent += OnConnectionEvent;

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            // autostart=true -> STARTDT_ACT/CON handshake completes before this returns.
            await _client.ConnectAsync(_loopCts.Token).ConfigureAwait(false);
        }

        public void StartTraffic()
        {
            _ = Task.Run(CommandLoopAsync);
            _ = Task.Run(InterrogationLoopAsync);
        }

        /// <summary>Stop issuing new traffic (cancels the command/GI loops) without tearing down the link.</summary>
        public void StopTraffic()
        {
            try
            {
                _loopCts?.Cancel();
            }
            catch { /* ignore */ }
        }

        private void OnConnectionEvent(ApduConnectionEvent ev)
        {
            if (ev == ApduConnectionEvent.ConnectionClosed)
            {
                _metrics.IncClientClosed();
                _metrics.NoteCloseReason((_client.LastCloseReason ?? ConnectionCloseReason.Unknown).ToString());
            }
        }

        private void OnAsduReceived(in AsduView view)
        {
            var typeId = view.TypeId;
            var cot = view.Cot;
            var asduBytes = view.Raw.Length;
            _metrics.CountDown(typeId, asduBytes, cot);

            // GI completion detection: ACTIVATION_TERMINATION closes the round trip.
            if (cot == CauseOfTransmission.ACTIVATION_TERMINATION)
            {
                var issued = Interlocked.Exchange(ref _giPendingTicks, 0);
                if (issued != 0)
                {
                    var us = (Stopwatch.GetTimestamp() - issued) * 1_000_000.0 / Stopwatch.Frequency;
                    _metrics.RecordGiLatency(us);
                    Interlocked.Increment(ref _giCompleted);
                }
            }

            // Force a decode of the first element to exercise the receive path and
            // catch any type whose payload shape is rejected by the decoder.
            try
            {
                _ = view.GetElement(0, null);
            }
            catch (ASDUParsingException)
            {
                _metrics.IncDecodeError();
            }
            catch (Exception)
            {
                _metrics.IncDecodeError();
            }
        }

        private async Task CommandLoopAsync()
        {
            var token = _loopCts.Token;
            var types = TypeCoverage.CommandTypes;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_cfg.CommandIntervalMs));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var client = _client;
                    if (client == null || !client.IsActivated)
                    {
                        continue;
                    }

                    var typeId = types[Interlocked.Increment(ref _commandTypeCursor) % types.Length];
                    var ioa = 3000 + _index;

                    // Select-and-execute: issue the SELECT (预发) confirmation first, then the
                    // EXECUTE confirmation, both on the same IOA. The two must be distinguished
                    // purely by the Select bit — the exact path the ControlConfirmation matcher
                    // relies on (regression of the C_SE_* offset fix). Bypass SELECT for
                    // commands without a Select bit.
                    var hasSelect = TypeCoverage.HasSelectBit(typeId) && _cfg.SelectThenExecute;
                    if (hasSelect)
                    {
                        await SendOneCommandAsync(client, typeId, ioa, select: true, token).ConfigureAwait(false);
                    }

                    await SendOneCommandAsync(client, typeId, ioa, select: false, token).ConfigureAwait(false);

                    // If the link dropped mid-flight, stop the loop instead of hammering a dead
                    // connection (each further send would just fail and inflate the counters).
                    if (!client.IsActivated)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        private async Task SendOneCommandAsync(Iec104Client client, TypeID typeId, int ioa, bool select, CancellationToken token)
        {
            var io = TypeCoverage.MakeCommandSample(typeId, ioa, select);
            var startTicks = Stopwatch.GetTimestamp();
            _metrics.CountUp(EstimateUpBytes(typeId));
            _metrics.NoteCommandAttempt(typeId);

            try
            {
                var confirmation = await client
                    .SendControlCommandAndWaitAsync(CauseOfTransmission.ACTIVATION, Ca, io, token)
                    .ConfigureAwait(false);

                var us = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000.0 / Stopwatch.Frequency;
                _metrics.RecordCommandLatency(us);
                _metrics.NoteCommandTypeConfirmed(typeId);

                // The confirmation's IsSelect must equal what we sent — proving the receive-side
                // matcher resolved the correct Select bit (guards against the C_SE_* offset bug).
                if (confirmation.IsSelect != select)
                {
                    _metrics.IncSelectMismatch();
                }
            }
            catch (TimeoutException)
            {
                _metrics.IncCommandTimeout();
                _metrics.NoteCommandTimeout(typeId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception)
            {
                _metrics.IncCommandFailure();
            }
        }

        private async Task InterrogationLoopAsync()
        {
            var token = _loopCts.Token;
            // Spread GI over masters: each master runs its first GI after a small
            // offset and then every 10s, so GI load is not a single thundering herd.
            var firstDelay = TimeSpan.FromMilliseconds(500 + (_index % 20) * 50);
            try
            {
                await Task.Delay(firstDelay, token).ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (!token.IsCancellationRequested)
                {
                    var client = _client;
                    if (client != null && client.IsActivated)
                    {
                        Interlocked.Increment(ref _giIssued);
                        Interlocked.Exchange(ref _giPendingTicks, Stopwatch.GetTimestamp());
                        _metrics.CountUp(6);
                        try
                        {
                            await client.SendInterrogationCommandAsync(
                                CauseOfTransmission.ACTIVATION, Ca, 20, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            Interlocked.Exchange(ref _giPendingTicks, 0);
                            _metrics.IncGiTimeout();
                        }
                    }

                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        private static int EstimateUpBytes(TypeID t) => 16; // ASDU header + single command element

        public async ValueTask DisposeAsync()
        {
            try
            {
                _loopCts?.Cancel();
            }
            catch { /* ignore */ }

            if (_client != null)
            {
                // Iec104Client.DisposeAsync() already performs a graceful DisconnectAsync()
                // internally, so a separate DisconnectAsync() here is redundant.
                try
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }

            _loopCts?.Dispose();
        }

        public long GiIssued => Interlocked.Read(ref _giIssued);
        public long GiCompleted => Interlocked.Read(ref _giCompleted);
    }
}
