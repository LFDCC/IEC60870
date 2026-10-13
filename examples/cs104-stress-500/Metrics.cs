/*
 *  Metrics.cs — lock-light counters + latency reservoir + process resource sampling.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using IEC60870.Core;

namespace cs104_stress_500
{
    public sealed class Metrics
    {
        // ── Directional ASDU / byte counters ───────────────────────────────
        // "Up"   = master -> device (commands, GI requests, ...)
        // "Down" = device -> master (monitor data, confirmations)
        private long _upAsdu;
        private long _upBytes;
        private long _downAsdu;
        private long _downBytes;

        // Spontaneous vs interrogated vs confirmed breakdown of the down direction.
        private long _downSpontaneous;
        private long _downInterrogated;
        private long _downConfirmation;
        private long _downConnectionAsdu;

        // ── Error / stability ──────────────────────────────────────────────
        private long _decodeErrors;
        private long _serverAsduErrors;
        private long _clientConnectionClosed;
        private long _serverConnectionClosed;
        private long _commandTimeouts;
        private long _commandFailures;
        private long _giTimeouts;
        private long _selectMismatches;

        // ── Coverage ───────────────────────────────────────────────────────
        public readonly HashSet<TypeID> MonitorTypesSeen = new HashSet<TypeID>();
        public readonly HashSet<TypeID> CommandTypesConfirmedSoFar = new HashSet<TypeID>();
        private readonly object _coverageGate = new object();

        private readonly Dictionary<TypeID, long> _perTypeDown = new Dictionary<TypeID, long>();
        private readonly Dictionary<TypeID, long> _cmdAttempts = new Dictionary<TypeID, long>();
        private readonly Dictionary<TypeID, long> _cmdTimeouts = new Dictionary<TypeID, long>();
        private readonly Dictionary<TypeID, long> _cmdConfirmed = new Dictionary<TypeID, long>();

        // ── Latency reservoir (command RTT + GI RTT, microseconds) ─────────
        private readonly List<long> _commandLatencyUs = new List<long>(1 << 16);
        private readonly List<long> _giLatencyUs = new List<long>(8192);
        private readonly object _latencyGate = new object();

        public void CountUp(int asduBytes)
        {
            Interlocked.Increment(ref _upAsdu);
            Interlocked.Add(ref _upBytes, asduBytes + ApduOverhead);
        }

        public void CountDown(TypeID typeId, int asduBytes, CauseOfTransmission cot)
        {
            Interlocked.Increment(ref _downAsdu);
            Interlocked.Add(ref _downBytes, asduBytes + ApduOverhead);

            switch (cot)
            {
                case CauseOfTransmission.SPONTANEOUS:
                    Interlocked.Increment(ref _downSpontaneous);
                    break;
                case CauseOfTransmission.INTERROGATED_BY_STATION:
                case CauseOfTransmission.REQUESTED_BY_GENERAL_COUNTER:
                    Interlocked.Increment(ref _downInterrogated);
                    break;
                case CauseOfTransmission.ACTIVATION_CON:
                case CauseOfTransmission.ACTIVATION_TERMINATION:
                case CauseOfTransmission.DEACTIVATION_CON:
                    Interlocked.Increment(ref _downConfirmation);
                    break;
                default:
                    Interlocked.Increment(ref _downConnectionAsdu);
                    break;
            }

            lock (_coverageGate)
            {
                MonitorTypesSeen.Add(typeId);
                _perTypeDown.TryGetValue(typeId, out var n);
                _perTypeDown[typeId] = n + 1;
            }
        }

        public void RecordCommandLatency(double us)
        {
            var v = (long)us;
            lock (_latencyGate)
            {
                if (_commandLatencyUs.Count < 600000)
                {
                    _commandLatencyUs.Add(v);
                }
            }
        }

        public void RecordGiLatency(double us)
        {
            var v = (long)us;
            lock (_latencyGate)
            {
                _giLatencyUs.Add(v);
            }
        }

        public void NoteCommandTypeConfirmed(TypeID t)
        {
            lock (_coverageGate)
            {
                CommandTypesConfirmedSoFar.Add(t);
                _cmdConfirmed.TryGetValue(t, out var n);
                _cmdConfirmed[t] = n + 1;
            }
        }

        public void NoteCommandAttempt(TypeID t)
        {
            lock (_coverageGate)
            {
                _cmdAttempts.TryGetValue(t, out var n);
                _cmdAttempts[t] = n + 1;
            }
        }

        public void NoteCommandTimeout(TypeID t)
        {
            lock (_coverageGate)
            {
                _cmdTimeouts.TryGetValue(t, out var n);
                _cmdTimeouts[t] = n + 1;
            }
        }

        public Dictionary<TypeID, (long attempts, long confirmed, long timeouts)> CommandStats()
        {
            lock (_coverageGate)
            {
                var result = new Dictionary<TypeID, (long, long, long)>();
                var keys = new HashSet<TypeID>();
                foreach (var k in _cmdAttempts.Keys) keys.Add(k);
                foreach (var k in _cmdConfirmed.Keys) keys.Add(k);
                foreach (var k in _cmdTimeouts.Keys) keys.Add(k);
                foreach (var k in keys)
                {
                    _cmdAttempts.TryGetValue(k, out var a);
                    _cmdConfirmed.TryGetValue(k, out var c);
                    _cmdTimeouts.TryGetValue(k, out var to);
                    result[k] = (a, c, to);
                }

                return result;
            }
        }

        public void IncDecodeError() => Interlocked.Increment(ref _decodeErrors);
        public void IncServerAsduError() => Interlocked.Increment(ref _serverAsduErrors);
        public void IncClientClosed() => Interlocked.Increment(ref _clientConnectionClosed);
        public void IncServerClosed() => Interlocked.Increment(ref _serverConnectionClosed);
        public void IncCommandTimeout() => Interlocked.Increment(ref _commandTimeouts);
        public void IncCommandFailure() => Interlocked.Increment(ref _commandFailures);
        public void IncGiTimeout() => Interlocked.Increment(ref _giTimeouts);
        public void IncSelectMismatch() => Interlocked.Increment(ref _selectMismatches);

        private readonly Dictionary<string, long> _closeReasons = new Dictionary<string, long>();
        private readonly object _closeGate = new object();

        public void NoteCloseReason(string reason)
        {
            lock (_closeGate)
            {
                _closeReasons.TryGetValue(reason, out var n);
                _closeReasons[reason] = n + 1;
            }
        }

        public Dictionary<string, long> CloseReasons()
        {
            lock (_closeGate)
            {
                return new Dictionary<string, long>(_closeReasons);
            }
        }

        // IEC 104 APDU overhead: the 0x68 header (2 bytes incl. length) + 4 control bytes.
        private const int ApduOverhead = 6;

        // ── Snapshot / deltas for the live sampler and final report ────────

        public readonly struct Sample
        {
            public readonly long UpAsdu, UpBytes, DownAsdu, DownBytes;
            public Sample(long upAsdu, long upBytes, long downAsdu, long downBytes)
            {
                UpAsdu = upAsdu; UpBytes = upBytes; DownAsdu = downAsdu; DownBytes = downBytes;
            }
        }

        public Sample TakeSample() => new Sample(
            Interlocked.Read(ref _upAsdu), Interlocked.Read(ref _upBytes),
            Interlocked.Read(ref _downAsdu), Interlocked.Read(ref _downBytes));

        public long UpAsdu => Interlocked.Read(ref _upAsdu);
        public long UpBytes => Interlocked.Read(ref _upBytes);
        public long DownAsdu => Interlocked.Read(ref _downAsdu);
        public long DownBytes => Interlocked.Read(ref _downBytes);
        public long DownSpontaneous => Interlocked.Read(ref _downSpontaneous);
        public long DownInterrogated => Interlocked.Read(ref _downInterrogated);
        public long DownConfirmation => Interlocked.Read(ref _downConfirmation);
        public long DecodeErrors => Interlocked.Read(ref _decodeErrors);
        public long ServerAsduErrors => Interlocked.Read(ref _serverAsduErrors);
        public long ClientConnectionClosed => Interlocked.Read(ref _clientConnectionClosed);
        public long ServerConnectionClosed => Interlocked.Read(ref _serverConnectionClosed);
        public long CommandTimeouts => Interlocked.Read(ref _commandTimeouts);
        public long CommandFailures => Interlocked.Read(ref _commandFailures);
        public long GiTimeouts => Interlocked.Read(ref _giTimeouts);
        public long SelectMismatches => Interlocked.Read(ref _selectMismatches);

        public LatencyStats CommandLatency => ComputeStats(_commandLatencyUs, _latencyGate);
        public LatencyStats GiLatency => ComputeStats(_giLatencyUs, _latencyGate);

        public Dictionary<TypeID, long> PerTypeDown()
        {
            lock (_coverageGate)
            {
                return new Dictionary<TypeID, long>(_perTypeDown);
            }
        }

        public List<TypeID> CoverageSnapshot()
        {
            lock (_coverageGate)
            {
                return new List<TypeID>(MonitorTypesSeen);
            }
        }

        public List<TypeID> CommandCoverageSnapshot()
        {
            lock (_coverageGate)
            {
                return new List<TypeID>(CommandTypesConfirmedSoFar);
            }
        }

        private static LatencyStats ComputeStats(List<long> raw, object gate)
        {
            long[] arr;
            lock (gate)
            {
                if (raw.Count == 0)
                {
                    return LatencyStats.Empty;
                }

                arr = raw.ToArray();
            }

            Array.Sort(arr);
            return new LatencyStats
            {
                Count = arr.Length,
                MinUs = arr[0],
                MaxUs = arr[arr.Length - 1],
                P50Us = Percentile(arr, 50),
                P90Us = Percentile(arr, 90),
                P99Us = Percentile(arr, 99),
                P999Us = Percentile(arr, 99.9),
                MeanUs = Mean(arr)
            };
        }

        private static long Percentile(long[] sorted, double p)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            var rank = (p / 100.0) * (sorted.Length - 1);
            var idx = (int)Math.Round(rank, MidpointRounding.AwayFromZero);
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        private static long Mean(long[] a)
        {
            double sum = 0;
            foreach (var v in a)
            {
                sum += v;
            }

            return (long)(sum / a.Length);
        }
    }

    public struct LatencyStats
    {
        public long Count, MinUs, MaxUs, P50Us, P90Us, P99Us, P999Us, MeanUs;
        public static readonly LatencyStats Empty = new LatencyStats();
        public override string ToString() =>
            $"n={Count} min={MinUs / 1000.0:F2}ms p50={P50Us / 1000.0:F2}ms p90={P90Us / 1000.0:F2}ms " +
            $"p99={P99Us / 1000.0:F2}ms p99.9={P999Us / 1000.0:F2}ms max={MaxUs / 1000.0:F2}ms mean={MeanUs / 1000.0:F2}ms";
    }

    /// <summary>Process-level resource sampler (CPU, memory, GC).</summary>
    public sealed class ResourceSampler
    {
        private readonly Process _proc = Process.GetCurrentProcess();
        private TimeSpan _lastCpu;
        private DateTime _lastWall;
        private long _lastAlloc;
        private int _lastGen0, _lastGen1, _lastGen2;

        public double CpuPercentLast;      // over the last sampling interval (process % of one core-set)
        public double CpuPercentTotal;     // average since Start()
        public double CpuSecondsTotal;     // cumulative process CPU time (sum over threads)
        public long WorkingSetBytes;
        public long PrivateBytes;
        public long ManagedHeapBytes;
        public double AllocBytesPerSecLast;
        public long TotalAllocatedBytes;
        public int Gen0, Gen1, Gen2;
        public long Gen0PerSecLast;

        private double _sumCpuWeighted;
        private double _sumSeconds;

        public ResourceSampler()
        {
            Prime();
        }

        private void Prime()
        {
            _proc.Refresh();
            _lastCpu = _proc.TotalProcessorTime;
            _lastWall = DateTime.UtcNow;
            _lastAlloc = GC.GetTotalAllocatedBytes(false);
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
        }

        public void Sample()
        {
            _proc.Refresh();
            var now = DateTime.UtcNow;
            var cpu = _proc.TotalProcessorTime;
            var wall = (now - _lastWall).TotalSeconds;
            if (wall <= 0)
            {
                return;
            }

            var cpuDelta = (cpu - _lastCpu).TotalSeconds;
            CpuPercentLast = 100.0 * cpuDelta / wall;

            var alloc = GC.GetTotalAllocatedBytes(false);
            AllocBytesPerSecLast = (alloc - _lastAlloc) / wall;
            TotalAllocatedBytes = alloc;

            var g0 = GC.CollectionCount(0);
            Gen0PerSecLast = (long)((g0 - _lastGen0) / wall);

            _sumCpuWeighted += CpuPercentLast * wall;
            _sumSeconds += wall;
            CpuPercentTotal = _sumSeconds > 0 ? _sumCpuWeighted / _sumSeconds : 0;
            CpuSecondsTotal = cpu.TotalSeconds;

            WorkingSetBytes = _proc.WorkingSet64;
            PrivateBytes = _proc.PrivateMemorySize64;
            ManagedHeapBytes = GC.GetTotalMemory(false);
            Gen0 = g0;
            Gen1 = GC.CollectionCount(1);
            Gen2 = GC.CollectionCount(2);

            _lastCpu = cpu;
            _lastWall = now;
            _lastAlloc = alloc;
            _lastGen0 = g0;
            _lastGen1 = Gen1;
            _lastGen2 = Gen2;
        }
    }
}
