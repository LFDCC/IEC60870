/*
 *  MicroBench.cs — pure protocol-core microbenchmarks.
 *
 *  Measures the library's per-operation cost independent of the network: ASDU
 *  encode (ASDU -> wire bytes) and decode (wire bytes -> InformationObject) for
 *  representative types. These numbers are the fairest cross-library comparison
 *  point because they exercise exactly the protocol core (no socket, no OS).
 *
 *  Run: cs104-stress-500 --bench [--bench-iterations N]
 */

using System;
using System.Diagnostics;
using IEC60870.Core;

namespace cs104_stress_500
{
    public static class MicroBench
    {
        public static void Run(int iterations)
        {
            Console.WriteLine("======================================================================");
            Console.WriteLine(" 协议核心微基准 — 编码/解码 单操作成本 (MicroBench)");
            Console.WriteLine("======================================================================");
            Console.WriteLine($" 迭代次数: {iterations:N0} / 项   运行时: {Environment.Version} / {Environment.ProcessorCount} 逻辑核");
            Console.WriteLine("----------------------------------------------------------------------");

            var al = new ApplicationLayerParameters();
            var buf = new byte[al.MaxAsduLength];

            // ── Encode: one 8-point M_ME_NC_1 (float measured value) ASDU ──
            Bench("编码 M_ME_NC_1 ×8 点", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                for (var p = 0; p < 8; p++)
                {
                    asdu.AddInformationObject(new MeasuredValueShort(1000 + p, p * 0.5f, new QualityDescriptor()));
                }

                var w = new AsduWriter(buf);
                asdu.Encode(ref w, al);
                return w.Position;
            });

            // ── Encode: one single-point ASDU (smallest monitor type) ──
            Bench("编码 M_SP_NA_1 ×1 点", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                asdu.AddInformationObject(new SinglePointInformation(1000, true, new QualityDescriptor()));
                var w = new AsduWriter(buf);
                asdu.Encode(ref w, al);
                return w.Position;
            });

            // ── Encode: C_SE_NC_1 setpoint command ──
            Bench("编码 C_SE_NC_1 命令", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
                asdu.AddInformationObject(new SetpointCommandShort(3000, 123.456f, new SetpointCommandQualifier(false, 0)));
                var w = new AsduWriter(buf);
                asdu.Encode(ref w, al);
                return w.Position;
            });

            // ── Decode: materialize an 8-point M_ME_NC_1 ASDU then decode all 8 elements ──
            var wireMonitor = BuildWireMonitor(al, TypeID.M_ME_NC_1, 8, out var wireLenMonitor);
            Bench("解码 M_ME_NC_1 ×8 点", iterations, () =>
            {
                var asdu = new ASDU(al, wireMonitor, 0, wireLenMonitor);
                var sum = 0;
                for (var i = 0; i < asdu.NumberOfElements; i++)
                {
                    sum += (int)asdu.GetElement<MeasuredValueShort>(i).Value;
                }

                return sum;
            });

            // ── Decode (zero-copy view): decode one element via AsduView ──
            Bench("解码 AsduView 单元素 (M_ME_TF_1)", iterations, () =>
            {
                var view = new AsduView(new ReadOnlySpan<byte>(wireMonitor, 0, wireLenMonitor), al);
                var io = view.GetElement(0, null);
                return io.ObjectAddress;
            });

            Console.WriteLine("----------------------------------------------------------------------");
            Console.WriteLine(" 说明: 编码=ASDU→线字节 (含 I 帧负载), 解码=线字节→信息对象 (含对象分配)。");
            Console.WriteLine("      这是网络无关的协议核心成本, 可与其它 104 库的编码/解码做同口径对比。");
            Console.WriteLine("======================================================================");
        }

        private static ASDU BuildMonitorAsdu(ApplicationLayerParameters al, TypeID typeId, int points)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            for (var p = 0; p < points; p++)
            {
                asdu.AddInformationObject(TypeCoverage.MakeMonitorSample(typeId, 1000 + p, p));
            }

            return asdu;
        }

        /// <summary>Extra rows that use the exact same API surface as the reference lib60870.NET, for a like-for-like comparison.</summary>
        public static void RunComparable(int iterations)
        {
            Console.WriteLine("======================================================================");
            Console.WriteLine(" 同口径对比基准 (与 lib60870.NET 2.3.0 相同的 API 路径)");
            Console.WriteLine("======================================================================");
            var al = new ApplicationLayerParameters();

            // encode -> byte[] (AsByteArray), same as reference's AsByteArray()
            Bench("编码→byte[] M_ME_NC_1 ×8", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                for (var p = 0; p < 8; p++)
                {
                    asdu.AddInformationObject(new MeasuredValueShort(1000 + p, p * 0.5f, new QualityDescriptor()));
                }

                return asdu.AsByteArray().Length;
            });

            Bench("编码→byte[] M_SP_NA_1 ×1", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                asdu.AddInformationObject(new SinglePointInformation(1000, true, new QualityDescriptor()));
                return asdu.AsByteArray().Length;
            });

            Bench("编码→byte[] C_SE_NC_1", iterations, () =>
            {
                var asdu = new ASDU(al, CauseOfTransmission.ACTIVATION, false, false, 0, 1, false);
                asdu.AddInformationObject(new SetpointCommandShort(3000, 123.456f, new SetpointCommandQualifier(false, 0)));
                return asdu.AsByteArray().Length;
            });

            // decode via ASDU(msg) + non-generic GetElement (same as reference)
            var asduEnc = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            for (var p = 0; p < 8; p++)
            {
                asduEnc.AddInformationObject(new MeasuredValueShort(1000 + p, p * 0.5f, new QualityDescriptor()));
            }

            var wire = asduEnc.AsByteArray();
            Bench("解码(非泛型) M_ME_NC_1 ×8", iterations, () =>
            {
                var asdu = new ASDU(al, wire, 0, wire.Length);
                var sum = 0;
                for (var i = 0; i < asdu.NumberOfElements; i++)
                {
                    sum += (int)((MeasuredValueShort)asdu.GetElement(i)).Value;
                }

                return sum;
            });

            Console.WriteLine("======================================================================");
        }

        private static byte[] BuildWireMonitor(ApplicationLayerParameters al, TypeID typeId, int points, out int len)
        {
            var asdu = BuildMonitorAsdu(al, typeId, points);
            var bytes = asdu.AsByteArray();
            len = bytes.Length;
            return bytes;
        }

        private static void Bench(string name, int iterations, Func<int> op)
        {
            // Warm up (JIT + pool).
            var warm = Math.Min(iterations / 10 + 1, 50000);
            for (var i = 0; i < warm; i++)
            {
                op();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 跑两轮取最优：第一轮常落在分层 JIT 晋升（tier-2/PGO）完成之前，会把 .NET 抬高
            // 约 1.8×（与 C 的编译期全优化代码对比时会系统性高估差距）。
            var bestUsPerOp = double.MaxValue;
            var bestOpsPerSec = 0.0;
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 2; round++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < iterations; i++)
                {
                    op();
                }

                sw.Stop();
                var usPerOp = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;
                if (usPerOp < bestUsPerOp)
                {
                    bestUsPerOp = usPerOp;
                    bestOpsPerSec = iterations / sw.Elapsed.TotalSeconds;
                }
            }

            var allocAfter = GC.GetAllocatedBytesForCurrentThread();

            var bytesPerOp = (allocAfter - allocBefore) / (double)iterations / 2;

            Console.WriteLine($" {name,-30} : {bestUsPerOp,8:F3} µs/op   {bestOpsPerSec,14:N0} op/s   分配 {bytesPerOp,7:F0} B/op");
        }
    }
}
