/*
 *  DeviceServer.cs
 *
 *  One simulated IEC 60870-5-104 "device" (a slave/substation RTU). Each device
 *  runs its own Iec104Server on a dedicated port and holds a single expected
 *  master session. On the device side we:
 *    - answer ACTIVATION commands (control commands, clock sync, GI, counter GI,
 *      reset process, delay acquisition, test commands) with ACTIVATION_CON,
 *    - answer general interrogation with ACTIVATION_CON + a full monitor snapshot
 *      (all M_* types) + ACTIVATION_TERMINATION,
 *    - stream spontaneous monitor data at a configured rate.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;

namespace cs104_stress_500
{
    public sealed class DeviceServer : IAsyncDisposable
    {
        private readonly int _index;
        private readonly int _port;
        private readonly StressConfig _cfg;
        private readonly Metrics _metrics;
        private readonly ApplicationLayerParameters _al;

        private Iec104Server _server;
        private Iec104Session _session;
        private CancellationTokenSource _loopCts;

        private long _spontSequence;

        public DeviceServer(int index, int port, StressConfig cfg, Metrics metrics)
        {
            _index = index;
            _port = port;
            _cfg = cfg;
            _metrics = metrics;
            _al = new ApplicationLayerParameters();
        }

        public int Index => _index;
        public int Ca => _index + 1; // unique common address per device (1..N)

        /// <summary>Whether the device's single expected session has completed STARTDT (data transfer active).</summary>
        public bool IsActivated => _session != null && _session.IsActivated;

        /// <summary>Number of live TCP sessions currently held by this device's server.</summary>
        public int SessionCount => _server?.SessionCount ?? 0;

        public async Task StartAsync()
        {
            _server = new Iec104Server(null, _al);
            _server.ConnectionEvent += OnConnectionEvent;
            _server.AsduReceived += OnAsduReceived;
            await _server.StartAsync(_port).ConfigureAwait(false);
        }

        private void OnConnectionEvent(Iec104Session session, ApduConnectionEvent ev)
        {
            switch (ev)
            {
                case ApduConnectionEvent.Activated:
                    _session = session;
                    StartSpontaneousLoop();
                    break;
                case ApduConnectionEvent.ConnectionClosed:
                    _metrics.IncServerClosed();
                    _metrics.NoteCloseReason(session.LastCloseReason.ToString());
                    StopSpontaneousLoop();
                    break;
            }
        }

        private void OnAsduReceived(Iec104Session session, in AsduView view)
        {
            // Copy out of the zero-copy view immediately.
            var raw = view.Raw.ToArray();
            ASDU asdu;
            try
            {
                asdu = new ASDU(session is null ? _al : _al, raw, 0, raw.Length);
            }
            catch (Exception)
            {
                _metrics.IncDecodeError();
                return;
            }

            try
            {
                HandleCommand(session, asdu);
            }
            catch (Exception)
            {
                _metrics.IncServerAsduError();
            }
        }

        private void HandleCommand(Iec104Session session, ASDU req)
        {
            switch (req.TypeId)
            {
                case TypeID.C_IC_NA_1:
                    _ = RespondInterrogationAsync(session, req);
                    break;

                case TypeID.C_CI_NA_1:
                    _ = RespondSimpleConAsync(session, req);
                    break;

                case TypeID.C_RD_NA_1:
                    _ = RespondReadAsync(session, req);
                    break;

                case TypeID.C_CS_NA_1:
                    _ = RespondSimpleConAsync(session, req);
                    break;

                case TypeID.C_TS_NA_1:
                case TypeID.C_TS_TA_1:
                    _ = RespondSimpleConAsync(session, req);
                    break;

                case TypeID.C_RP_NA_1:
                    _ = RespondSimpleConAsync(session, req);
                    break;

                case TypeID.C_CD_NA_1:
                    _ = RespondSimpleConAsync(session, req);
                    break;

                default:
                    // Control commands (C_SC/DC/RC/SE/BO, NA and TA variants):
                    // echo the command back as an ACTIVATION_CON.
                    if (IsControlCommand(req.TypeId))
                    {
                        _ = RespondSimpleConAsync(session, req);
                    }
                    break;
            }
        }

        private static bool IsControlCommand(TypeID t) => t switch
        {
            TypeID.C_SC_NA_1 or TypeID.C_DC_NA_1 or TypeID.C_RC_NA_1 =>
                true,
            TypeID.C_SE_NA_1 or TypeID.C_SE_NB_1 or TypeID.C_SE_NC_1 or TypeID.C_BO_NA_1 =>
                true,
            TypeID.C_SC_TA_1 or TypeID.C_DC_TA_1 or TypeID.C_RC_TA_1 =>
                true,
            TypeID.C_SE_TA_1 or TypeID.C_SE_TB_1 or TypeID.C_SE_TC_1 or TypeID.C_BO_TA_1 =>
                true,
            _ => false
        };

        // Device -> master: ACTIVATION_CON echoing the request's first IO.
        private async Task RespondSimpleConAsync(Iec104Session session, ASDU req)
        {
            try
            {
                var con = BuildActCon(req);
                await session.SendAsync(con).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _metrics.IncServerAsduError();
            }
        }

        // Device -> master: read response for a single point.
        private async Task RespondReadAsync(Iec104Session session, ASDU req)
        {
            try
            {
                var resp = new ASDU(_al, CauseOfTransmission.REQUEST, false, false, 0, req.Ca, false);
                resp.AddInformationObject(new MeasuredValueScaled(4000, _index * 3, QualityDescriptor.VALID()));
                await session.SendAsync(resp).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _metrics.IncServerAsduError();
            }
        }

        // Device -> master: ACTIVATION_CON + full monitor snapshot + ACTIVATION_TERMINATION.
        private async Task RespondInterrogationAsync(Iec104Session session, ASDU req)
        {
            try
            {
                await session.SendAsync(BuildActCon(req)).ConfigureAwait(false);

                var ioaBase = 1000;
                var slot = 0;
                foreach (var typeId in TypeCoverage.MonitorTypes)
                {
                    if (typeId == TypeID.M_EI_NA_1)
                    {
                        continue; // end-of-initialization is not part of GI
                    }

                    var asdu = new ASDU(_al, CauseOfTransmission.INTERROGATED_BY_STATION,
                        false, false, 0, req.Ca, false);
                    for (var p = 0; p < _cfg.PointsPerMonitorAsdu; p++)
                    {
                        var io = TypeCoverage.MakeMonitorSample(typeId, ioaBase + slot++, _index + slot);
                        asdu.AddInformationObject(io);
                    }

                    await session.SendAsync(asdu).ConfigureAwait(false);
                }

                var term = new ASDU(_al, CauseOfTransmission.ACTIVATION_TERMINATION,
                    false, false, 0, req.Ca, false);
                term.AddInformationObject(req.GetElement(0));
                await session.SendAsync(term).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _metrics.IncServerAsduError();
            }
        }

        private ASDU BuildActCon(ASDU req)
        {
            var con = new ASDU(_al, CauseOfTransmission.ACTIVATION_CON, false, false, req.Oa, req.Ca, false);
            con.AddInformationObject(req.GetElement(0));
            return con;
        }

        private void StartSpontaneousLoop()
        {
            if (_loopCts != null)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            var periodMs = _cfg.SpontaneousPerSecondPerDevice > 0
                ? (int)Math.Max(10, 1000.0 / _cfg.SpontaneousPerSecondPerDevice)
                : int.MaxValue;

            if (_cfg.SpontaneousPerSecondPerDevice <= 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var types = TypeCoverage.MonitorTypes;
                var idx = _index;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));
                try
                {
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        var session = _session;
                        if (session == null || !session.IsActivated)
                        {
                            continue;
                        }

                        var typeId = types[(int)(Interlocked.Increment(ref _spontSequence) % types.Length)];
                        var asdu = new ASDU(_al, CauseOfTransmission.SPONTANEOUS, false, false, 0, Ca, false);
                        for (var p = 0; p < _cfg.PointsPerMonitorAsdu; p++)
                        {
                            var io = TypeCoverage.MakeMonitorSample(typeId, 2000 + idx * 100 + p, idx + idx + p);
                            asdu.AddInformationObject(io);
                        }

                        try
                        {
                            await session.SendAsync(asdu, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            _metrics.IncServerAsduError();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal shutdown
                }
            }, token);
        }

        private void StopSpontaneousLoop()
        {
            try
            {
                _loopCts?.Cancel();
            }
            catch { /* ignore */ }

            _loopCts?.Dispose();
            _loopCts = null;
        }

        public async ValueTask DisposeAsync()
        {
            StopSpontaneousLoop();
            if (_server != null)
            {
                try
                {
                    await _server.StopAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }

                try
                {
                    _server.Dispose();
                }
                catch { /* ignore */ }
            }
        }
    }
}
