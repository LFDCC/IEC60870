// 本示例演示由 TouchSocket 内置 ReconnectionPlugin（UseReconnection）驱动的 CS104 客户端
// 自动重连（与 cs104-client-reconnect 的手写循环对照，生产环境推荐插件方式）。
//
// ═══════════════════════════════════════════════════════════════════
//  特别注意：使用重连插件时，客户端必须显式调用 DisposeAsync 才能停止重连！
//  插件主循环只随插件自身被 Dispose 而退出，且 Dispose 会级联释放插件：
//      client.DisposeAsync() → ClearConfig → PluginManager.Dispose → 插件 CTS 取消 → 主循环退出
//  只调 DisconnectAsync（不断开 Dispose）的后果：
//    1. 插件把主动关闭当断线，继续无限重连（默认 MaxRetryCount=-1）；
//    2. 轮询 Task + 插件 + client 整棵对象树（闭包强引用）全部泄漏，永不回收。
//  多装置动态管理的场景（移除某个装置连接）必须走 DisconnectAsync + DisposeAsync 两步。
// ═══════════════════════════════════════════════════════════════════
//
// 其他要点：ConnectAction 必须委托到 Iec104Client 类型的 ConnectAsync（重建 104 层，
// 插件默认只做纯 TCP 握手）；主动关闭前先置 PauseReconnectionProperty，否则插件会
// 把主动关闭当断线重连回去；MaxRetryCount 保持 -1（有限次数用尽后插件永久放弃，
// 装置恢复也不再连）。

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace cs104_client_use_reconnection
{
    class MainClass
    {
        // ---- simulation timings (so the demo fits inside `timeout`) ----
        private static readonly TimeSpan CrashAfter = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(5); // after the crash

        private static Iec104Client _client = null!;
        private static volatile bool _manualShutdown;

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IEC 60870-5-104 client auto-reconnect demo (TouchSocket UseReconnection plugin)");

            // Self-contained peer: a tiny server so the example runs standalone.
            var server = new Iec104Server();
            await server.StartAsync(2404);
            Console.WriteLine("[server] listening on :2404");

            _client = new Iec104Client("127.0.0.1", 2404);
            _client.AsduReceived += (in AsduView view) =>
            {
                Console.WriteLine($"[asdu] Type={view.TypeId} COT={view.Cot} CA={view.CommonAddress} " +
                                  $"elements={view.NumberOfElements}");
            };
            try
            {

            await _client.ConnectAsync(cancellationToken: default, configureConfig: config =>
            {
                config.ConfigurePlugins(a =>
                {
                    a.UseReconnection<Iec104Client>(o =>
                    {
                        // 关键：委托到 Iec104Client 类型的 ConnectAsync，重连时才完整重建 104 层
                        //（插件默认走基类纯 TCP 握手重载，不会重建 ApduConnection/APCI 状态机）
                        o.ConnectAction = static (client, ct) => client.ConnectAsync(ct);

                        // 指数退避：1s 起、x2、15s 封顶、无限重试（MaxRetryCount 保持 -1，
                        // 有限次数用尽后插件会永久放弃，装置恢复也不再连）
                        o.UseExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), 2.0, -1);

                        // 注意：OnSuccessed 可能双触发（断开检测与重试成功两条路径），恢复动作需幂等
                        o.OnSuccessed += client =>
                        {
                            Console.WriteLine("[reconnect] TCP + STARTDT re-activated by plugin");
                            _ = SendInterrogationAsync(1);   // 应用层恢复：重发总召
                        };
                        o.OnFailed += (client, attempt, ex) =>
                            Console.WriteLine($"[reconnect] attempt {attempt} failed: {ex?.GetType().Name}: {ex?.Message}");
                        o.OnGiveUp += (client, attempts) =>
                            Console.WriteLine($"[reconnect] gave up after {attempts} attempts (max retries reached)");
                    });
                });
            });
            }
            catch (Exception)
            {

            }
            Console.WriteLine("[client] connected + STARTDT activated (Autostart)");

            // 应用层周期业务：每 2 秒发一次总召，直观展示断链期间发送失败、恢复后自动继续
            //_ = Task.Run(async () =>
            //{
            //    while (!_manualShutdown)
            //    {
            //        try
            //        {
            //            await SendInterrogationAsync(1);
            //        }
            //        catch (Exception ex)
            //        {
            //            Console.WriteLine($"[client] send failed (expected while offline): {ex.Message}");
            //        }
            //        await Task.Delay(2000);
            //    }
            //});

            // Simulate an operator event: peer crashes, then comes back.
            _ = Task.Run(async () =>
            {
                await Task.Delay(CrashAfter);
                Console.WriteLine("[sim] PEER CRASH -> server.StopAsync()");
                await server.StopAsync();

                await Task.Delay(RestartAfter);
                Console.WriteLine("[sim] PEER BACK -> server.StartAsync(2404)");
                //await server.StartAsync(2404);
                await _client.DisposeAsync();
                await Task.Delay(10000);
            });

            Console.WriteLine("Press any key to shut down cleanly...");
            try
            {
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                // stdin is not a console (e.g. piped / CI). Keep the process alive long
                // enough for the reconnect demo to play out, then exit.
                await Task.Delay(TimeSpan.FromSeconds(25));
            }

            // ── 主动退出：必须先 Pause（否则插件把主动关闭当断线，又重连回去），
            //    且必须 DisposeAsync（否则插件轮询 Task、插件、client 整棵对象树泄漏，
            //    重连循环永不停止）——见文件头"特别注意"。
            _manualShutdown = true;
            _client.SetValue(ClientExtension.PauseReconnectionProperty, true);
            await _client.DisconnectAsync();
            await _client.DisposeAsync();
            await server.StopAsync();
            server.Dispose();
            Console.WriteLine("bye.");
        }

        private static async Task SendInterrogationAsync(int ca)
        {
            var asdu = new ASDU(_client.Parameters, CauseOfTransmission.ACTIVATION,
                false, false, 0, ca, false);
            asdu.AddInformationObject(new InterrogationCommand(0, QualifierOfInterrogation.STATION));
            await _client.SendAsync(asdu);
        }
    }
}
