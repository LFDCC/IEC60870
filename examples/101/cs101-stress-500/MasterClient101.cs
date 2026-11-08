/*
 *  MasterClient101.cs — 一个模拟主站，对应一台装置。
 *
 *  平衡链路 + TCP 隧道。主站行为：
 *    - 周期下发单点遥控（C_SC_NA_1），等装置回 ACTIVATION_CON 并测 RTT；
 *    - 周期发起总召唤（C_IC_NA_1），等 ACTIVATION_TERMINATION 并测 RTT；
 *    - 统计收到的所有 ASDU（装置主动上送 + 确认）。
 *
 *  注意 101 的节流特性：链路层是 FT1.2 的「一帧一确认」（FCB 交替），
 *  平衡模式下本端 PLL 每确认一帧才发下一帧，所以命令/总召唤的实际节拍
 *  受 ACK 往返支配 —— 这正是要量化的东西。
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;

namespace cs101_stress_500
{
    public sealed class MasterClient101
    {
        private readonly int _index;
        private readonly int _port;
        private readonly StressConfig _cfg;
        private readonly Metrics _metrics;
        private readonly ApplicationLayerParameters _al = new ApplicationLayerParameters();

        private Iec101Client _client;
        private CancellationTokenSource _loopCts;

        private long _cmdPendingTicks;      // 在途命令的起始时间戳，0 = 无
        private long _cmdPendingStartMs;
        private long _giPendingTicks;
        private long _giPendingStartMs;

        private const int DeviceAddress = 3;
        private const int MasterAddress = 1;
        private const int CommandTimeoutMs = 5000;
        private const int GiTimeoutMs = 15000;

        public MasterClient101(int index, int port, StressConfig cfg, Metrics metrics)
        {
            _index = index;
            _port = port;
            _cfg = cfg;
            _metrics = metrics;
        }

        public int Ca => 1;

        public bool IsLinkAvailable
            => _client != null && _client.GetLinkLayerState() == LinkLayerState.AVAILABLE;

        public async Task ConnectAsync(CancellationToken token)
        {
            _client = new Iec101Client("127.0.0.1", _port, LinkLayerMode.BALANCED);
            _client.OwnAddress = MasterAddress;
            _client.SlaveAddress = DeviceAddress;
            _client.SetASDUReceivedHandler(OnAsdu, null);

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            _ = _client.StartAsync(_loopCts.Token);

            // 平衡链路握手（双方各自的 PLL 各自建链）需要一点时间
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (IsLinkAvailable)
                {
                    return;
                }

                await Task.Delay(20, token).ConfigureAwait(false);
            }

            throw new TimeoutException($"装置 #{_index} 链路未在 10s 内进入 AVAILABLE");
        }

        public void StartTraffic()
        {
            _ = Task.Run(CommandLoopAsync);
            _ = Task.Run(InterrogationLoopAsync);
            _ = Task.Run(TimeoutWatchAsync);
        }

        public void StopTraffic()
        {
            try
            {
                _loopCts?.Cancel();
            }
            catch
            {
            }
        }

        /// <summary>关闭并释放；返回是否在超时内完成。</summary>
        public async Task<bool> CloseAsync()
        {
            try
            {
                _client?.Stop();
                _client?.Dispose();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool OnAsdu(object parameter, int address, ASDU asdu)
        {
            _metrics.CountDown(EstimateSize(asdu));

            switch (asdu.Cot)
            {
                case CauseOfTransmission.ACTIVATION_TERMINATION:
                    {
                        var issued = Interlocked.Exchange(ref _giPendingTicks, 0);
                        if (issued != 0)
                        {
                            _metrics.GiLatency.Add(ElapsedUs(issued));
                            _metrics.IncGiCompleted();
                        }

                        break;
                    }

                case CauseOfTransmission.ACTIVATION_CON:
                    if (asdu.TypeId == TypeID.C_SC_NA_1)
                    {
                        var issued = Interlocked.Exchange(ref _cmdPendingTicks, 0);
                        if (issued != 0)
                        {
                            _metrics.CommandLatency.Add(ElapsedUs(issued));
                            _metrics.IncCommandConfirmed();
                        }
                    }

                    break;
            }

            return true;
        }

        private static int EstimateSizeForObjects(int objects)
        {
            return 6 + Math.Max(1, objects) * 4;
        }

        private static int EstimateSize(ASDU asdu)
        {
            // 粗估线字节：ASDU 头 6 + 每信息对象 ~4 —— 只用于带宽口径，不做精确编码
            return 6 + Math.Max(1, asdu.NumberOfElements) * 4;
        }

        private static long ElapsedUs(long startTicks)
            => (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;

        private async Task CommandLoopAsync()
        {
            var token = _loopCts.Token;
            var interval = TimeSpan.FromMilliseconds(_cfg.CommandIntervalMs);
            var ioa = 5000 + _index;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (IsLinkAvailable && Interlocked.Read(ref _cmdPendingTicks) == 0)
                    {
                        Interlocked.Exchange(ref _cmdPendingTicks, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _cmdPendingStartMs, NowMs());
                        _metrics.IncCommandAttempt();

                        try
                        {
                            _client.SendControlCommand(CauseOfTransmission.ACTIVATION, Ca,
                                new SingleCommand(ioa, true, false, 0));
                            _metrics.CountUp(EstimateSizeForObjects(1));
                        }
                        catch (LinkLayerBusyException)
                        {
                            Interlocked.Exchange(ref _cmdPendingTicks, 0);
                            _metrics.IncCommandFailure();
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Exchange(ref _cmdPendingTicks, 0);
                            _metrics.IncSendFailure(ex);
                            _metrics.IncCommandFailure();
                        }
                    }

                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task InterrogationLoopAsync()
        {
            var token = _loopCts.Token;
            var interval = TimeSpan.FromMilliseconds(_cfg.GiIntervalMs);

            // 错开首轮，避免 500 个主站同一瞬间发起总召唤形成惊群
            try
            {
                await Task.Delay(500 + (_index % 20) * 50, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (IsLinkAvailable && Interlocked.Read(ref _giPendingTicks) == 0)
                    {
                        Interlocked.Exchange(ref _giPendingTicks, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _giPendingStartMs, NowMs());
                        _metrics.IncGiIssued();

                        try
                        {
                            _client.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, Ca, QualifierOfInterrogation.STATION);
                            _metrics.CountUp(EstimateSizeForObjects(1));
                        }
                        catch (LinkLayerBusyException)
                        {
                            Interlocked.Exchange(ref _giPendingTicks, 0);
                            _metrics.IncGiTimeout();
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Exchange(ref _giPendingTicks, 0);
                            _metrics.IncSendFailure(ex);
                            _metrics.IncGiTimeout();
                        }
                    }

                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>在途命令/总召唤超时判定（命令 5s、总召唤 15s）。</summary>
        private async Task TimeoutWatchAsync()
        {
            var token = _loopCts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);

                    if (Interlocked.Read(ref _cmdPendingTicks) != 0 && NowMs() - Interlocked.Read(ref _cmdPendingStartMs) > CommandTimeoutMs)
                    {
                        if (Interlocked.Exchange(ref _cmdPendingTicks, 0) != 0)
                        {
                            _metrics.IncCommandTimeout();
                        }
                    }

                    if (Interlocked.Read(ref _giPendingTicks) != 0 && NowMs() - Interlocked.Read(ref _giPendingStartMs) > GiTimeoutMs)
                    {
                        if (Interlocked.Exchange(ref _giPendingTicks, 0) != 0)
                        {
                            _metrics.IncGiTimeout();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
