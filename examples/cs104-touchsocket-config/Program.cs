// Program.cs
//
// 演示通过 ConnectAsync / StartAsync 的 TouchSocketConfig 配置回调,
// 向 IEC 104 客户端/服务端注入 TouchSocket 原生配置(TCP KeepAlive、NoDelay、
// 监听选项、插件等),采用用户回调追加式配置合并。
//
// 合并顺序:库必配项(远程地址/监听端口/SSL) → 用户回调追加 → SetupAsync。
// 用户回调中指定的配置不会覆盖库的地址/端口,但可自由追加 TouchSocket 能力。

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace cs104_touchsocket_config
{
    class MainClass
    {
        private const int Port = 2404;
        private const int Ca = 1;

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IEC 60870-5-104 TouchSocketConfig 自定义配置 示例\n");

            // ── 服务端:通过配置回调追加监听选项 / 最大连接数 / 插件 ──
            var server = new Iec104Server();
            server.AsduReceived += (Iec104Session session, in AsduView view) =>
            {
                var raw = view.Raw.ToArray();
                ASDU asdu = new ASDU(server.Parameters, raw, 0, raw.Length);
                Console.WriteLine($"[从站] 收到 {asdu.TypeId} CA={asdu.Ca} 元素数={asdu.NumberOfElements}");

                // 收到总召激活即回一帧遥信
                if (asdu.TypeId == TypeID.C_IC_NA_1 && asdu.Cot == CauseOfTransmission.ACTIVATION)
                {
                    var resp = new ASDU(server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, Ca, false);
                    resp.AddInformationObject(new SinglePointInformation(100, true, QualityDescriptor.VALID()));
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.SendAsync(resp);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("  从站回送失败: " + ex.Message);
                        }
                    });
                }
            };

            await server.StartAsync(Port, config =>
            {
                // 监听套接字选项:复用地址、等待队列
                config.SetReuseAddress(true);
                config.SetBacklog(50);
                // 服务端最大连接数
                config.SetMaxCount(10);
                // 附加服务端插件(如自定义日志)
                config.ConfigurePlugins(a =>
                {
                    a.AddTcpConnectingPlugin((service, e) =>
                    {
                        Console.WriteLine("[从站] 新客户端接入: " + e.Id);
                        return e.InvokeNext();
                    });
                });
            });
            Console.WriteLine($"[从站] 已在 127.0.0.1:{Port} 启动(配置回调已注入监听选项/插件)\n");
            await Task.Delay(300); // 等监听就绪

            // ── 客户端:通过配置回调追加 KeepAlive / NoDelay / 插件 ──
            await using var client = new Iec104Client("127.0.0.1", Port);
            client.AsduReceived += (in AsduView view) =>
            {
                var raw = view.Raw.ToArray();
                ASDU asdu = new ASDU(client.Parameters, raw, 0, raw.Length);
                Console.WriteLine($"[主站] 收到 {asdu.TypeId} CA={asdu.Ca} 元素数={asdu.NumberOfElements}");
                if (asdu.TypeId == TypeID.M_SP_NA_1)
                {
                    var sp = (SinglePointInformation)asdu.GetElement(0);
                    Console.WriteLine($"  IOA={sp.ObjectAddress} 值={sp.Value} 品质={sp.Quality}");
                }
            };
            client.ConnectionEvent += ev => Console.WriteLine("[主站] 连接事件: " + ev);

            await client.ConnectAsync(cancellationToken: default, configureConfig: config =>
            {
                // TCP KeepAlive:每 5 秒探测一次(在 T3 心跳之外增加链路保活)
                config.SetKeepAliveValue(o =>
                {
                    o.Interval = 5000;
                    o.AckInterval = 5000;
                });
                // 禁用 Nagle,降低小报文(如 S 帧确认)发送延迟
                config.SetNoDelay(true);
                // 附加客户端插件(如收发报文观测)
                config.ConfigurePlugins(a =>
                {
                    a.AddTcpSendingPlugin((c, e) =>
                    {
                        Console.WriteLine("[主站 TX] " + e.Memory.Span.ToHexString(" "));
                        return e.InvokeNext();
                    });
                });
            });

            await client.StartDataTransferAsync();
            Console.WriteLine("[主站] 已连接并激活数据传输\n");

            // 总召,触发从站回送遥信
            var interrogation = new ASDU(client.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, Ca, false);
            interrogation.AddInformationObject(new InterrogationCommand(0, QualifierOfInterrogation.STATION));
            await client.SendAsync(interrogation);

            await Task.Delay(2000);

            Console.WriteLine("\n[主站] 断开连接");
            await client.DisconnectAsync();

            Console.WriteLine("[从站] 停止");
            server.Dispose();

            Console.WriteLine("Press any key to terminate...");
            Console.ReadKey();
        }
    }
}
