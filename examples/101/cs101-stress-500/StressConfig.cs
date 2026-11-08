/*
 *  StressConfig.cs — 命令行配置。
 */

using System;
using System.Globalization;

namespace cs101_stress_500
{
    public sealed class StressConfig
    {
        /// <summary>模拟装置数（= 装置服务端数 = 主站数）。</summary>
        public int DeviceCount = 500;

        /// <summary>起始 TCP 端口；装置 i 监听 BasePort + i。</summary>
        public int BasePort = 26000;

        /// <summary>全部链路建立后的测量时长（秒）。</summary>
        public int DurationSeconds = 60;

        /// <summary>预热秒数（不进测量窗口）。</summary>
        public int WarmupSeconds = 10;

        /// <summary>每秒建立多少条链路。</summary>
        public int RampUpPerSecond = 25;

        /// <summary>每台装置每秒主动上送多少个 ASDU。</summary>
        public double SpontaneousPerSecondPerDevice = 4.0;

        /// <summary>主站命令周期（ms）。</summary>
        public int CommandIntervalMs = 250;

        /// <summary>主站总召唤周期（ms）。</summary>
        public int GiIntervalMs = 10000;

        /// <summary>每个监视 ASDU 打包的测点数。</summary>
        public int PointsPerMonitorAsdu = 8;

        /// <summary>是否每秒打印进度。</summary>
        public bool Verbose = true;

        /// <summary>JSON 指标输出路径。</summary>
        public string JsonOut;

        public static StressConfig Parse(string[] args)
        {
            var cfg = new StressConfig();

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : null;

                switch (a)
                {
                    case "--devices": cfg.DeviceCount = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--port": cfg.BasePort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--duration": cfg.DurationSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--warmup": cfg.WarmupSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--ramp": cfg.RampUpPerSecond = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--spont": cfg.SpontaneousPerSecondPerDevice = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cmd-interval": cfg.CommandIntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--gi-interval": cfg.GiIntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--points": cfg.PointsPerMonitorAsdu = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--json": cfg.JsonOut = Next(); break;
                    case "--quiet": cfg.Verbose = false; break;
                    case "-h":
                    case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                }
            }

            return cfg;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(
                "cs101-stress-500 — IEC 60870-5-101 装置压力测试\n" +
                "Usage: cs101-stress-500 [options]\n" +
                "  --devices N      装置数（默认 500）\n" +
                "  --port P         起始端口（默认 26000）\n" +
                "  --duration S     测量秒数（默认 60）\n" +
                "  --warmup S       预热秒数（默认 10）\n" +
                "  --ramp N         每秒建链数（默认 25）\n" +
                "  --spont X        每装置每秒自发 ASDU 数（默认 4）\n" +
                "  --cmd-interval M 主站命令周期 ms（默认 250）\n" +
                "  --gi-interval M  总召唤周期 ms（默认 10000）\n" +
                "  --points N       每监视 ASDU 测点数（默认 8）\n" +
                "  --json PATH      输出 JSON 指标\n" +
                "  --quiet          不打印每秒进度\n");
        }
    }
}
