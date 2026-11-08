/*
 *  DeviceServer101.cs — 一台被模拟的 101 装置（从站）。
 *
 *  平衡链路 + TCP 隧道，每个装置独占一个端口（与 104 压测工具同口径）。
 *  装置行为：
 *    - 按 --spont 速率主动上送监视数据（平衡链路上从站自行推帧，无需主站召唤）；
 *    - 响应总召唤：ACT_CON + 全量快照 + ACT_TERM；
 *    - 响应遥控：ACT_CON + ACT_TERM。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;

namespace cs101_stress_500
{
    public sealed class DeviceServer101 : IDisposable
    {
        private readonly int _index;
        private readonly int _port;
        private readonly StressConfig _cfg;
        private readonly Metrics _metrics;

        private readonly Iec101Server _server;
        private readonly ApplicationLayerParameters _al = new ApplicationLayerParameters();

        private CancellationTokenSource _cts;
        private Task _spontTask;

        /// <summary>本装置自己的链路地址（平衡模式下由工具约定：装置=3，主站=1）。</summary>
        private const int DeviceAddress = 3;
        private const int MasterAddress = 1;

        public int SessionCount { get; private set; }

        public DeviceServer101(int index, int port, StressConfig cfg, Metrics metrics)
        {
            _index = index;
            _port = port;
            _cfg = cfg;
            _metrics = metrics;

            _server = new Iec101Server(port);
            _server.LinkLayerMode = LinkLayerMode.BALANCED;
            _server.LinkLayerAddress = DeviceAddress;
            _server.LinkLayerAddressOtherStation = MasterAddress;

            _server.SetInterrogationHandler(OnInterrogation, null);
            _server.SetASDUHandler(OnAsdu, null);
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _ = _server.StartAsync(_cts.Token);

            // 让监听套接字就绪
            await Task.Delay(1).ConfigureAwait(false);

            _spontTask = Task.Run(SpontaneousLoopAsync);
        }

        private bool OnInterrogation(object parameter, IClientConnection connection, ASDU asdu, byte qoi)
        {
            _metrics.CountDown(0); // 记一次下行报文（确认，用于口径对齐）

            connection.SendACT_CON(asdu, false);

            var resp = new ASDU(_al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 0, 1, false);

            for (var i = 0; i < _cfg.PointsPerMonitorAsdu; i++)
            {
                resp.AddInformationObject(new MeasuredValueScaled(100 + i, _index * 3 + i, new QualityDescriptor()));
            }

            connection.SendASDU(resp);
            connection.SendACT_TERM(asdu);
            return true;
        }

        private bool OnAsdu(object parameter, IClientConnection connection, ASDU asdu)
        {
            switch (asdu.TypeId)
            {
                case TypeID.C_SC_NA_1:
                    connection.SendACT_CON(asdu, false);
                    connection.SendACT_TERM(asdu);
                    return true;

                case TypeID.C_IC_NA_1:
                case TypeID.C_CI_NA_1:
                case TypeID.C_CS_NA_1:
                case TypeID.C_TS_NA_1:
                    // 由库内建处理器负责
                    return false;

                default:
                    return false;
            }
        }

        private async Task SpontaneousLoopAsync()
        {
            var token = _cts.Token;

            if (_cfg.SpontaneousPerSecondPerDevice <= 0)
            {
                return;
            }

            var intervalMs = Math.Max(1, (int)(1000.0 / _cfg.SpontaneousPerSecondPerDevice));
            var cyc = new ASDU(_al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);

            for (var i = 0; i < Math.Max(1, _cfg.PointsPerMonitorAsdu); i++)
            {
                cyc.AddInformationObject(new SinglePointInformation(200 + i, true, new QualityDescriptor()));
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 每个装置都用 class1，保证「从站主动推帧」这条平衡链路路径被压到
                    _server.EnqueueUserDataClass1(cyc);
                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _metrics.IncSendFailure(ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _server.Stop();
            }
            catch
            {
            }

            try
            {
                _server.Dispose();
            }
            catch
            {
            }

            try
            {
                _cts?.Dispose();
            }
            catch
            {
            }
        }
    }
}
