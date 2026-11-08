/*
 *  Metrics.cs — 计数器 + 时延蓄水池 + 进程资源采样。
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace cs101_stress_500
{
    public sealed class Metrics
    {
        // 上行 = 主站→装置（命令、总召唤）；下行 = 装置→主站（监视数据、确认）
        private long _upAsdu;
        private long _upBytes;
        private long _downAsdu;
        private long _downBytes;

        private long _decodeErrors;
        private long _sendFailures;

        private long _commandAttempts;
        private long _commandConfirmed;
        private long _commandTimeouts;
        private long _commandFailures;

        private long _giIssued;
        private long _giCompleted;
        private long _giTimeouts;

        private long _winUpAsdu;
        private long _winDownAsdu;
        private long _winUpBytes;
        private long _winDownBytes;
        private long _winSinceTicks = Stopwatch.GetTimestamp();

        public readonly LatencyReservoir CommandLatency = new LatencyReservoir();
        public readonly LatencyReservoir GiLatency = new LatencyReservoir();

        private readonly ConcurrentDictionary<string, long> _sendFailureReasons = new();

        public long UpAsdu => Interlocked.Read(ref _upAsdu);
        public long DownAsdu => Interlocked.Read(ref _downAsdu);
        public long UpBytes => Interlocked.Read(ref _upBytes);
        public long DownBytes => Interlocked.Read(ref _downBytes);
        public long DecodeErrors => Interlocked.Read(ref _decodeErrors);
        public long SendFailures => Interlocked.Read(ref _sendFailures);
        public long CommandAttempts => Interlocked.Read(ref _commandAttempts);
        public long CommandConfirmed => Interlocked.Read(ref _commandConfirmed);
        public long CommandTimeouts => Interlocked.Read(ref _commandTimeouts);
        public long CommandFailures => Interlocked.Read(ref _commandFailures);
        public long GiIssued => Interlocked.Read(ref _giIssued);
        public long GiCompleted => Interlocked.Read(ref _giCompleted);
        public long GiTimeouts => Interlocked.Read(ref _giTimeouts);

        public IEnumerable<(string Reason, long Count)> SendFailureReasons
            => _sendFailureReasons.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value));

        public void CountUp(int bytes)
        {
            Interlocked.Increment(ref _upAsdu);
            Interlocked.Add(ref _upBytes, bytes);
        }

        public void CountDown(int bytes)
        {
            Interlocked.Increment(ref _downAsdu);
            Interlocked.Add(ref _downBytes, bytes);
        }

        public void IncDecodeError() => Interlocked.Increment(ref _decodeErrors);

        public void IncSendFailure(Exception ex)
        {
            Interlocked.Increment(ref _sendFailures);

            var message = ex.Message ?? string.Empty;
            if (message.Length > 80)
            {
                message = message.Substring(0, 80) + "…";
            }

            var key = ex.GetType().Name + ": " + message;
            _sendFailureReasons.AddOrUpdate(key, 1, (_, v) => v + 1);
        }

        public void IncCommandAttempt() => Interlocked.Increment(ref _commandAttempts);
        public void IncCommandConfirmed() => Interlocked.Increment(ref _commandConfirmed);
        public void IncCommandTimeout() => Interlocked.Increment(ref _commandTimeouts);
        public void IncCommandFailure() => Interlocked.Increment(ref _commandFailures);
        public void IncGiIssued() => Interlocked.Increment(ref _giIssued);
        public void IncGiCompleted() => Interlocked.Increment(ref _giCompleted);
        public void IncGiTimeout() => Interlocked.Increment(ref _giTimeouts);

        /// <summary>开始测量窗口：把累计计数快照下来，之后按窗口差算速率。</summary>
        public void ResetWindow()
        {
            Interlocked.Exchange(ref _winUpAsdu, UpAsdu);
            Interlocked.Exchange(ref _winDownAsdu, DownAsdu);
            Interlocked.Exchange(ref _winUpBytes, UpBytes);
            Interlocked.Exchange(ref _winDownBytes, DownBytes);
            Interlocked.Exchange(ref _winSinceTicks, Stopwatch.GetTimestamp());
        }

        public Tick Snapshot(double elapsedSeconds, IEnumerable<DeviceServer101> servers, IEnumerable<MasterClient101> masters,
            ResourceSampler sampler)
        {
            var up = UpAsdu - Interlocked.Read(ref _winUpAsdu);
            var down = DownAsdu - Interlocked.Read(ref _winDownAsdu);
            var upBytes = UpBytes - Interlocked.Read(ref _winUpBytes);
            var downBytes = DownBytes - Interlocked.Read(ref _winDownBytes);
            var secs = elapsedSeconds <= 0 ? 1 : elapsedSeconds;

            var linksUp = 0;
            foreach (var m in masters)
            {
                if (m.IsLinkAvailable)
                {
                    linksUp++;
                }
            }

            return new Tick
            {
                DownPerSec = down / secs,
                UpPerSec = up / secs,
                MbitPerSec = (upBytes + downBytes) * 8 / 1_000_000.0 / secs,
                LinksUp = linksUp,
            };
        }

        public string DescribeLatency(string label, LatencyReservoir r)
        {
            if (r.Count == 0)
            {
                return $"{label}: 无样本";
            }

            var s = r.Snapshot();
            return $"{label}: n={s.Count:N0}  均值={s.MeanUs / 1000.0:F2}ms  "
                 + $"P50={s.P50Us / 1000.0:F2}ms  P90={s.P90Us / 1000.0:F2}ms  "
                 + $"P99={s.P99Us / 1000.0:F2}ms  P99.9={s.P999Us / 1000.0:F2}ms  最大={s.MaxUs / 1000.0:F2}ms";
        }

        public struct Tick
        {
            public double DownPerSec;
            public double UpPerSec;
            public double MbitPerSec;
            public int LinksUp;
        }
    }

    /// <summary>固定容量时延样本（微秒）。超过容量后按固定步长稀释，保持流式可算分位。</summary>
    public sealed class LatencyReservoir
    {
        private const int Capacity = 262_144;
        private readonly long[] _samples = new long[Capacity];
        private int _count;
        private long _seen;
        private long _sum;
        private long _max;
        private readonly object _gate = new object();

        public long Count => Interlocked.Read(ref _seen);
        public long SumUs => Interlocked.Read(ref _sum);
        public long MaxUs => Interlocked.Read(ref _max);

        public void Add(long us)
        {
            Interlocked.Increment(ref _seen);
            Interlocked.Add(ref _sum, us);
            UpdateMax(us);

            lock (_gate)
            {
                if (_count < Capacity)
                {
                    _samples[_count++] = us;
                }
                else
                {
                    // 稀释：每 8 个样本替换 1 个，保证长跑不无限增长内存
                    var idx = (int)(_seen % Capacity);
                    if ((_seen & 7) == 0)
                    {
                        _samples[idx] = us;
                    }
                }
            }
        }

        private void UpdateMax(long us)
        {
            var cur = Interlocked.Read(ref _max);
            while (us > cur)
            {
                var prev = Interlocked.CompareExchange(ref _max, us, cur);
                if (prev == cur)
                {
                    return;
                }

                cur = prev;
            }
        }

        public Stats Snapshot()
        {
            long[] copy;
            lock (_gate)
            {
                copy = new long[_count];
                Array.Copy(_samples, copy, _count);
            }

            Array.Sort(copy);
            var n = copy.Length;

            long At(double q) => n == 0 ? 0 : copy[Math.Min(n - 1, (int)(q * n))];

            return new Stats
            {
                Count = Count,
                MeanUs = n == 0 ? 0 : copy.Sum() / n,
                P50Us = At(0.50),
                P90Us = At(0.90),
                P99Us = At(0.99),
                P999Us = At(0.999),
                MaxUs = MaxUs,
            };
        }

        public struct Stats
        {
            public long Count;
            public long MeanUs;
            public long P50Us;
            public long P90Us;
            public long P99Us;
            public long P999Us;
            public long MaxUs;

            public string ToJson()
                => "{ \"count\": " + Count
                 + ", \"mean\": " + MeanUs
                 + ", \"p50\": " + P50Us
                 + ", \"p90\": " + P90Us
                 + ", \"p99\": " + P99Us
                 + ", \"p999\": " + P999Us
                 + ", \"max\": " + MaxUs + " }";
        }
    }

    /// <summary>进程资源采样（CPU / 内存 / GC / 分配）。</summary>
    public sealed class ResourceSampler
    {
        private readonly Process _proc = Process.GetCurrentProcess();
        private Timer _timer;
        private TimeSpan _lastCpu;
        private long _lastTs;
        private double _peakCpu;
        private double _cpuSum;
        private int _samples;
        private long _allocAtStart;
        private int _gen0, _gen1, _gen2;

        public double CpuPercentLast { get; private set; }
        public double CpuPercentAvg => _samples == 0 ? 0 : _cpuSum / _samples;
        public double CpuPercentPeak => _peakCpu;
        public double CpuSecondsUsed { get; private set; }

        /// <summary>当前工作集（MB）——进度行用，采样器启动后每秒刷新。</summary>
        public double WorkingSetMB { get; private set; }

        public double WorkingSetBeforeMB { get; private set; }
        public double WorkingSetAfterMB { get; private set; }
        public double WorkingSetPeakMB { get; private set; }
        public double PrivateBeforeMB { get; private set; }
        public double PrivateAfterMB { get; private set; }
        public double ManagedHeapBeforeMB { get; private set; }
        public double ManagedHeapAfterMB { get; private set; }
        public double TotalAllocatedDeltaMB { get; private set; }

        // 长跑泄漏判据：内存随 GC 波动，句柄/线程只增不回落基本就是
        // Socket、FileStream 或后台循环没释放 —— 这两个计数比工作集更硬。
        public int HandlesLast { get; private set; }
        public int ThreadsLast { get; private set; }
        public int HandlesBefore { get; private set; }
        public int HandlesAfter { get; private set; }
        public int ThreadsBefore { get; private set; }
        public int ThreadsAfter { get; private set; }

        public int Gen0 => _gen0;
        public int Gen1 => _gen1;
        public int Gen2 => _gen2;

        public void Start()
        {
            _proc.Refresh();
            WorkingSetBeforeMB = _proc.WorkingSet64 / 1024.0 / 1024.0;
            PrivateBeforeMB = _proc.PrivateMemorySize64 / 1024.0 / 1024.0;
            ManagedHeapBeforeMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            HandlesBefore = _proc.HandleCount;
            ThreadsBefore = _proc.Threads.Count;
            WorkingSetPeakMB = WorkingSetBeforeMB;
            _allocAtStart = GC.GetTotalAllocatedBytes(false);
            _lastCpu = _proc.TotalProcessorTime;
            _lastTs = Stopwatch.GetTimestamp();
            _timer = new Timer(_ => Sample(), null, 1000, 1000);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            Finish();
        }

        private void Sample()
        {
            try
            {
                _proc.Refresh();
                var cpu = _proc.TotalProcessorTime;
                var ts = Stopwatch.GetTimestamp();
                var cpuDelta = (cpu - _lastCpu).TotalSeconds;
                var wallDelta = (ts - _lastTs) / (double)Stopwatch.Frequency;
                _lastCpu = cpu;
                _lastTs = ts;

                if (wallDelta > 0)
                {
                    var pct = cpuDelta / wallDelta * 100.0;
                    CpuPercentLast = pct;
                    _cpuSum += pct;
                    _samples++;
                    if (pct > _peakCpu)
                    {
                        _peakCpu = pct;
                    }
                }

                CpuSecondsUsed += cpuDelta;

                var ws = _proc.WorkingSet64 / 1024.0 / 1024.0;
                WorkingSetMB = ws;
                HandlesLast = _proc.HandleCount;
                ThreadsLast = _proc.Threads.Count;
                if (ws > WorkingSetPeakMB)
                {
                    WorkingSetPeakMB = ws;
                }
            }
            catch
            {
                /* 进程退出竞态 */
            }
        }

        private void Finish()
        {
            try
            {
                _proc.Refresh();
                WorkingSetAfterMB = _proc.WorkingSet64 / 1024.0 / 1024.0;
                PrivateAfterMB = _proc.PrivateMemorySize64 / 1024.0 / 1024.0;
                ManagedHeapAfterMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
                HandlesAfter = _proc.HandleCount;
                ThreadsAfter = _proc.Threads.Count;
                _gen0 = GC.CollectionCount(0);
                _gen1 = GC.CollectionCount(1);
                _gen2 = GC.CollectionCount(2);
                TotalAllocatedDeltaMB = (GC.GetTotalAllocatedBytes(false) - _allocAtStart) / 1024.0 / 1024.0;
            }
            catch
            {
            }
        }
    }
}
