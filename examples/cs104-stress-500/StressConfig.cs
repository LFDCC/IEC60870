/*
 *  StressConfig.cs — run configuration parsed from the command line.
 */

using System;
using System.Globalization;

namespace cs104_stress_500
{
    public sealed class StressConfig
    {
        /// <summary>Number of simulated devices (= number of device servers = number of master clients).</summary>
        public int DeviceCount = 500;

        /// <summary>First TCP port; device i listens on BasePort + i.</summary>
        public int BasePort = 25000;

        /// <summary>Total measurement duration (seconds) after all links are up.</summary>
        public int DurationSeconds = 60;

        /// <summary>How many links to establish per second during ramp-up.</summary>
        public int RampUpPerSecond = 25;

        /// <summary>Spontaneous monitor ASDUs pushed by each device per second.</summary>
        public double SpontaneousPerSecondPerDevice = 4.0;

        /// <summary>Master command round interval (ms) — how often each master issues a command cycle.</summary>
        public int CommandIntervalMs = 250;

        /// <summary>Number of monitor points bundled per monitored ASDU.</summary>
        public int PointsPerMonitorAsdu = 8;

        /// <summary>Print live progress each second.</summary>
        public bool Verbose = true;

        /// <summary>Path to write the JSON metrics summary (optional).</summary>
        public string JsonOut;

        /// <summary>Issue a SELECT (预发) confirmation before each EXECUTE, doubling command traffic.</summary>
        public bool SelectThenExecute = true;

        /// <summary>Run the protocol-core microbenchmark instead of the network stress test.</summary>
        public bool Bench;

        /// <summary>Microbenchmark iterations per operation.</summary>
        public int BenchIterations = 2_000_000;

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
                    case "--ramp": cfg.RampUpPerSecond = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--spont": cfg.SpontaneousPerSecondPerDevice = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cmd-interval": cfg.CommandIntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--points": cfg.PointsPerMonitorAsdu = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--no-select": cfg.SelectThenExecute = false; break;
                    case "--bench": cfg.Bench = true; break;
                    case "--bench-iterations": cfg.BenchIterations = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--quiet": cfg.Verbose = false; break;
                    case "--json": cfg.JsonOut = Next(); break;
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
                "cs104-stress-500 — IEC 60870-5-104 500-device stress harness\n" +
                "Usage: cs104-stress-500 [options]\n" +
                "  --devices N      number of devices (default 500)\n" +
                "  --port P         first TCP port (default 25000)\n" +
                "  --duration S     measurement seconds (default 60)\n" +
                "  --ramp N         links established per second during ramp-up (default 25)\n" +
                "  --spont X        spontaneous monitor ASDUs per device per second (default 4)\n" +
                "  --cmd-interval M master command cycle interval in ms (default 250)\n" +
                "  --points N       monitor points per ASDU (default 8)\n" +
                "  --no-select      do not issue SELECT before EXECUTE (halves command traffic)\n" +
                "  --bench          run the protocol-core microbenchmark instead of the stress test\n" +
                "  --bench-iterations N  microbenchmark iterations per op (default 2000000)\n" +
                "  --quiet          suppress per-second progress\n" +
                "  --json PATH      write JSON metrics summary to PATH");
        }
    }
}
