// Program.cs
//
// 演示 IEC 60870-5-104 控制命令的"预发 → 预发结束 → 执行 → 执行完成"四阶段同步等待。
//
// 关键点：Iec104Client 本身是"发完即返"（fire-and-forget），真正的确认是服务端
// 异步回送的 ACTIVATION_CON。本例用 ControlWaiter 把"发命令"和"等确认"串成
// 一个 await，从而写出直观的顺序代码：
//
//     var sel = await waiter.SendControlCommandAndWaitAsync(..., select:true);   // 预发 → 预发结束
//     var exe = await waiter.SendControlCommandAndWaitAsync(..., select:false);  // 执行 → 执行完成
//
// 进程内同时起一个简单的从站，收到控制命令即回 ACT-CON（含否定确认演示）。

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;

namespace cs104_control_select_execute
{
    class MainClass
    {
        private const int Port = 2404;
        private const int Ca = 1;

        // 收到控制命令即回一个 ACT-CON；negative=true 时回否定确认（演示被拒场景）。
        private static ASDU BuildActCon(ASDU req, ApplicationLayerParameters al, bool negative)
        {
            var con = new ASDU(al, CauseOfTransmission.ACTIVATION_CON, false, negative, req.Oa, req.Ca, false);
            con.AddInformationObject(req.GetElement(0));
            return con;
        }

        // 极简从站：拦截控制命令并回确认。
        private static Iec104Server StartDemoServer(ApplicationLayerParameters al, APCIParameters apci)
        {
            var server = new Iec104Server(apci, al);
            server.AsduReceived += (Iec104Session session, in AsduView view) =>
            {
                byte[] raw = view.Raw.ToArray();
                ASDU asdu = new ASDU(al, raw, 0, raw.Length);

                // 本演示从站只处理"激活(ACTIVATION)"的控制命令
                if (asdu.Cot == CauseOfTransmission.ACTIVATION)
                {
                    var io = asdu.GetElement(0);
                    // IOA=9999 故意演示"否定确认"（预发被拒）
                    bool negative = io.ObjectAddress == 9999;

                    Console.WriteLine($"  [从站] 收到控制命令 {asdu.TypeId} IOA={io.ObjectAddress} " +
                                      $"{(negative ? "→ 回复否定确认" : "→ 回复 ACT-CON")}");

                    ASDU con = BuildActCon(asdu, al, negative);
                    _ = Task.Run(async () =>
                    {
                        try { await session.SendAsync(con); }
                        catch (Exception ex) { Console.WriteLine("  从站回确认失败: " + ex.Message); }
                    });
                }
            };
            return server;
        }

        private static async Task RunControlSequence(ControlWaiter waiter, string label,
            CauseOfTransmission cot, int ca, InformationObject selectCmd, InformationObject executeCmd)
        {
            Console.WriteLine($"\n=== {label} ===");

            // ① 预发（select=true）
            Console.WriteLine($"① 预发  : 发送 {selectCmd.Type} IOA={selectCmd.ObjectAddress} select=true ...");
            ControlConfirmation sel = await waiter.SendControlCommandAndWaitAsync(cot, ca, selectCmd);
            if (sel.IsNegative)
            {
                Console.WriteLine($"   ⚠ 预发被服务端拒绝，终止该点控制序列: {sel}");
                return;
            }
            Console.WriteLine($"   ✅ 预发结束: {sel}");

            // ② 执行（select=false）
            Console.WriteLine($"② 执行  : 发送 {executeCmd.Type} IOA={executeCmd.ObjectAddress} select=false ...");
            ControlConfirmation exe = await waiter.SendControlCommandAndWaitAsync(cot, ca, executeCmd);
            if (exe.IsNegative)
            {
                Console.WriteLine($"   ⚠ 执行被服务端拒绝（预发已成功但执行未通过）: {exe}");
                return;
            }
            Console.WriteLine($"   ✅ 执行完成: {exe}");
        }

        // 透传打印非控制命令类 ASDU（AsduView 是 ref struct，使用 in 参数）
        private static void LogOtherAsdu(in AsduView view) =>
            Console.WriteLine($"  [其它 ASDU] Type={view.TypeId} COT={view.Cot} CA={view.CommonAddress}");

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IEC 60870-5-104 控制命令 预发/执行 同步等待 示例");
            Console.WriteLine("（使用 IEC60870.Core.NET 异步 API）\n");

            // 客户端/服务端使用同一套应用层参数，确保 Select 位解析偏移一致
            var al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };
            var apci = new APCIParameters();

            // 进程内从站
            Iec104Server server = StartDemoServer(al, apci);
            await server.StartAsync(Port);
            Console.WriteLine($"[从站] 已在 127.0.0.1:{Port} 启动\n");
            await Task.Delay(300); // 等监听就绪

            await using var client = new Iec104Client("127.0.0.1", Port, apci, al);
            // 其它 ASDU（如有自发上送）透传打印
            var waiter = new ControlWaiter(client, LogOtherAsdu);

            await client.ConnectAsync();
            await client.StartDataTransferAsync();
            Console.WriteLine("[主站] 已连接并激活数据传输\n");

            // ── 单命令 C_SC_NA_1：预发 → 预发结束 → 执行 → 执行完成 ──
            int ioaSwitch = 5001;
            await RunControlSequence(
                waiter, "单命令 C_SC_NA_1（合分闸）",
                CauseOfTransmission.ACTIVATION, Ca,
                selectCmd: new SingleCommand(ioaSwitch, command: true, selectCommand: true, qu: 0),
                executeCmd: new SingleCommand(ioaSwitch, command: true, selectCommand: false, qu: 0));

            // ── 设点命令 C_SE_NA_1：同样支持预发/执行（通用模式）──
            int ioaSetpoint = 6001;
            await RunControlSequence(
                waiter, "归一化设点 C_SE_NA_1（设定值）",
                CauseOfTransmission.ACTIVATION, Ca,
                selectCmd: new SetpointCommandNormalized(ioaSetpoint, -0.5f,
                    new SetpointCommandQualifier(select: true, ql: 0)),
                executeCmd: new SetpointCommandNormalized(ioaSetpoint, -0.5f,
                    new SetpointCommandQualifier(select: false, ql: 0)));

            // ── 否定确认演示：预发即被拒 ──
            int ioaReject = 9999;
            await RunControlSequence(
                waiter, "否定确认演示（从站对 IOA=9999 拒绝）",
                CauseOfTransmission.ACTIVATION, Ca,
                selectCmd: new SingleCommand(ioaReject, command: true, selectCommand: true, qu: 0),
                executeCmd: new SingleCommand(ioaReject, command: true, selectCommand: false, qu: 0));

            Console.WriteLine("\n[主站] 断开连接");
            await client.DisconnectAsync();
            waiter.Dispose();

            Console.WriteLine("[从站] 停止");
            server.Dispose(); // TouchSocket TcpService 实现 IDisposable，进程退出前释放监听
        }
    }
}
