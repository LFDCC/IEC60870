// Program.cs
//
// 演示 Task.WaitAsync 的两种用法，进程内同时运行从站(Iec104Server)与主站(Iec104Client)，
// 模拟"发送指令 → 等待响应"的完整闭环：
//
//   场景1  手动 WaitAsync：发送总召唤，用 TCS 承接 AsduReceived 事件，
//          task.WaitAsync(timeout) 等待"总召唤应答数据帧"，超时抛 TimeoutException。
//   场景2  内建 WaitAsync：SendControlCommandAndWaitAsync 发送单点控制命令，
//          一次 await 直接拿到 ControlConfirmation（内部即 WaitAsync + 超时保护）。
//   场景3  超时演示：从站对 IOA=9999 故意不回确认，客户端 WaitAsync 超时，
//          捕获 TimeoutException 演示防永久挂起。
//
// WaitAsync 的价值：不改变原 Task 也不取消底层操作，只是"限时拿结果"，
// 超时后调用方立即得到异常继续执行，避免 await 无限挂起。

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;

namespace cs104_waitasync_command
{
    internal class MainClass
    {
        private const int Port = 24104;
        private const int Ca = 1;

        // ── 从站侧：构造应答 ASDU ─────────────────────────────────────────

        // 总召唤三段应答中的 ACT-CON / ACT-TERM（回显请求首元素）。
        private static ASDU BuildEcho(CauseOfTransmission cot, ASDU req, ApplicationLayerParameters al)
        {
            var con = new ASDU(al, cot, false, false, req.Oa, req.Ca, false);
            con.AddInformationObject(req.GetElement(0));
            return con;
        }

        // 总召唤应答数据帧（INTERROGATED_BY_STATION）。
        // 注意：同一 ASDU 内信息对象必须同 TypeID，单点与测量值需分帧发送。
        private static ASDU[] BuildInterrogationData(ASDU req, ApplicationLayerParameters al)
        {
            var points = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION,
                false, false, req.Oa, req.Ca, false);
            points.AddInformationObject(new SinglePointInformation(200, true, new QualityDescriptor()));

            var values = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION,
                false, false, req.Oa, req.Ca, false);
            values.AddInformationObject(new MeasuredValueScaled(100, 1234, new QualityDescriptor()));

            return [points, values];
        }

        // 极简从站：总召唤回三段应答；单点命令回 ACT-CON；IOA=9999 故意不回（演示超时）。
        private static Iec104Server StartDemoServer(ApplicationLayerParameters al)
        {
            var server = new Iec104Server(alParameters: al);

            server.AsduReceived += (Iec104Session session, in AsduView view) =>
            {
                var raw = view.Raw.ToArray();
                var asdu = new ASDU(al, raw, 0, raw.Length);

                if (asdu.TypeId == TypeID.C_IC_NA_1)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.SendAsync(BuildEcho(CauseOfTransmission.ACTIVATION_CON, asdu, al));

                            foreach (var data in BuildInterrogationData(asdu, al))
                            {
                                await session.SendAsync(data);
                            }

                            await session.SendAsync(BuildEcho(CauseOfTransmission.ACTIVATION_TERMINATION, asdu, al));
                            Console.WriteLine("  [从站] 总召唤三段应答已发送 (ACT-CON → 数据×2 → ACT-TERM)");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("  [从站] 应答失败: " + ex.Message);
                        }
                    });
                }
                else if (asdu.TypeId == TypeID.C_SC_NA_1 && asdu.Cot == CauseOfTransmission.ACTIVATION)
                {
                    var io = asdu.GetElement(0);

                    if (io.ObjectAddress == 9999)
                    {
                        Console.WriteLine("  [从站] 收到 IOA=9999 命令 → 故意不响应（演示客户端超时）");
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.SendAsync(BuildEcho(CauseOfTransmission.ACTIVATION_CON, asdu, al));
                            Console.WriteLine($"  [从站] IOA={io.ObjectAddress} 命令 → 已回 ACT-CON");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("  [从站] 回确认失败: " + ex.Message);
                        }
                    });
                }
            };

            return server;
        }

        // ── 场景1：手动 WaitAsync 等待总召唤数据帧 ────────────────────────

        private static async Task ScenarioManualWaitAsync(Iec104Client client, ApplicationLayerParameters al)
        {
            Console.WriteLine("\n=== 场景1  手动 Task.WaitAsync：总召唤 → 等待应答数据帧 ===");

            // 用 TCS 把"事件驱动"的 AsduReceived 桥接成可 await 的 Task
            var tcs = new TaskCompletionSource<AsduSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnAsdu(in AsduView a)
            {
                // 只关心总召唤应答数据（INTERROGATED_BY_STATION），其余帧忽略
                if (a.Cot == CauseOfTransmission.INTERROGATED_BY_STATION)
                {
                    tcs.TrySetResult(AsduSnapshot.Capture(a, al));
                }
            }

            client.AsduReceived += OnAsdu;

            try
            {
                await client.SendInterrogationCommandAsync(CauseOfTransmission.ACTIVATION, Ca, 20);
                Console.WriteLine("  [主站] 总召唤已发送，等待应答数据帧（限时 3s）...");

                // 核心指令：WaitAsync —— 3 秒内拿到结果就返回，超时抛 TimeoutException，
                // 不会把 await 无限挂下去。
                var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

                Console.WriteLine($"  ✅ 收到应答: Type={snapshot.TypeId} COT={snapshot.Cot} " +
                                  $"元素数={snapshot.ElementCount} CA={snapshot.Ca}");
            }
            catch (TimeoutException)
            {
                Console.WriteLine("  ⚠ 3 秒内未收到总召唤数据帧（WaitAsync 超时）");
            }
            finally
            {
                client.AsduReceived -= OnAsdu;
            }
        }

        // ── 场景2：内建 SendControlCommandAndWaitAsync（内部即 WaitAsync）──

        private static async Task ScenarioBuiltInWaitAsync(Iec104Client client)
        {
            Console.WriteLine("\n=== 场景2  SendControlCommandAndWaitAsync：单点命令 → 等待 ACT-CON ===");

            // 一次 await 完成"发送 + 等待确认"，超时由 CommandConfirmationTimeout 控制
            var cmd = new SingleCommand(5001, command: true, selectCommand: false, qu: 0);
            Console.WriteLine("  [主站] 发送单点命令 C_SC_NA_1 IOA=5001（合闸，直接执行）...");

            ControlConfirmation con = await client.SendControlCommandAndWaitAsync(
                CauseOfTransmission.ACTIVATION, Ca, cmd);

            Console.WriteLine($"  ✅ {con}");
        }

        // ── 场景3：超时演示 ───────────────────────────────────────────────

        private static async Task ScenarioTimeout(Iec104Client client)
        {
            Console.WriteLine("\n=== 场景3  超时演示：从站不响应 IOA=9999，WaitAsync 限时返回 ===");

            // 把确认超时临时调短，演示不必干等默认 5 秒
            client.CommandConfirmationTimeout = TimeSpan.FromSeconds(1);

            var cmd = new SingleCommand(9999, command: true, selectCommand: false, qu: 0);
            Console.WriteLine("  [主站] 发送单点命令 IOA=9999（从站故意不回），限时 1s ...");

            try
            {
                await client.SendControlCommandAndWaitAsync(CauseOfTransmission.ACTIVATION, Ca, cmd);
                Console.WriteLine("  ⚠ 意外收到了确认（不应发生）");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"  ✅ 按预期超时: {ex.Message}");
            }
            finally
            {
                client.CommandConfirmationTimeout = TimeSpan.FromSeconds(5);
            }
        }

        // AsduView 是 ref struct 不能出回调，捕获必要字段供打印。
        private readonly record struct AsduSnapshot(TypeID TypeId, CauseOfTransmission Cot,
            int ElementCount, int Ca)
        {
            public static AsduSnapshot Capture(in AsduView a, ApplicationLayerParameters al)
                => new AsduSnapshot(a.TypeId, a.Cot, a.NumberOfElements, a.Ca);
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IEC 60870-5-104 Task.WaitAsync 示例：发送指令并等待响应");
            Console.WriteLine("（进程内从站 + 主站，模拟完整请求-应答闭环）\n");

            var al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };

            Iec104Server server = StartDemoServer(al);
            await server.StartAsync(Port);
            Console.WriteLine($"[从站] 已在 127.0.0.1:{Port} 启动");
            await Task.Delay(300); // 等监听就绪

            await using var client = new Iec104Client("127.0.0.1", Port, alParameters: al);
            await client.ConnectAsync();
            await client.StartDataTransferAsync();
            Console.WriteLine("[主站] 已连接并激活数据传输");

            await ScenarioManualWaitAsync(client, al);
            await ScenarioBuiltInWaitAsync(client);
            await ScenarioTimeout(client);

            Console.WriteLine("\n[主站] 断开连接");
            await client.DisconnectAsync();

            Console.WriteLine("[从站] 停止");
            server.Dispose();
        }
    }
}
