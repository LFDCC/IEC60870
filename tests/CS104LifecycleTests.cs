/*
 *  CS104LifecycleTests.cs
 *
 *  CS104 生命周期 / 停止-重启回归护栏：
 *   1. 服务端 Stop → Start 10 轮（每轮 20 个客户端连断）：
 *      每轮 Stop 后会话必须清零，且第 10 轮仍能正常投递自发数据
 *      （缺陷 A：StopAsync 不清理 _sessions/_sessionGroups/_sessionToGroup；
 *        缺陷 H：StartAsync 重入丢弃旧组而不 Cancel）。
 *   2. 重连风暴 30 轮：每轮 new client → Connect → 收一条自发数据 → Disconnect/Dispose，
 *      全部释放后 SharedTimeoutScheduler.LiveCount 必须回落到 0
 *      （缺陷 B：旧连接收尾踩新连接；缺陷 C：调度器 Register/关闭竞态）。
 *   3. 同步 using 释放：`using (var c = ...) { await c.ConnectAsync(); }` 之后
 *      调度器注册必须释放（缺陷 D：Iec104Client 只实现 IAsyncDisposable）。
 *
 *  端口全部动态分配，避免与固定端口的其它用例冲突。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class CS104LifecycleTests
    {
        private const int ClientsPerRound = 20;
        private const int StopStartRounds = 10;
        private const int ReconnectRounds = 30;

        [TearDown]
        public void TearDown()
        {
            // 每个用例后强制关闭单例，避免跨用例共享调度器状态（与 SharedTimeoutSchedulerTests 一致）。
            SharedTimeoutScheduler.ForceShutdown();
        }

        // ══════════════════════════════════════════════════════════
        //  1. Stop → Start 10 轮 + 每轮 20 客户端连断
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 服务端停止重启10轮_会话不泄漏且第10轮仍可收发()
        {
            var port = GetFreePort();
            var al = new ApplicationLayerParameters();
            var server = new Iec104Server(mode: ServerMode.SINGLE_REDUNDANCY_GROUP);

            try
            {
                for (var round = 0; round < StopStartRounds; round++)
                {
                    await server.StartAsync(port);

                    var isLastRound = round == StopStartRounds - 1;
                    var got = 0;
                    var clients = new List<Iec104Client>(ClientsPerRound);

                    try
                    {
                        for (var i = 0; i < ClientsPerRound; i++)
                        {
                            var client = new Iec104Client("127.0.0.1", port, null, al);
                            if (isLastRound)
                            {
                                client.AsduReceived += (in AsduView view) => Interlocked.Increment(ref got);
                            }

                            await client.ConnectAsync();
                            clients.Add(client);
                        }

                        if (isLastRound)
                        {
                            // 第 10 轮必须仍然能收发：证明 Stop→Start 之后冗余组排空循环还活着，
                            // 而不是"能建连、能收报文、但自发事件永不发出"的假在线。
                            await server.EnqueueAsync(Spontaneous(al));
                            await WaitUntil(() => Volatile.Read(ref got) > 0, 5000,
                                "第 10 轮自发数据投递");
                        }
                    }
                    finally
                    {
                        foreach (var client in clients)
                        {
                            try { await client.DisconnectAsync(); } catch { }
                            try { client.Dispose(); } catch { }
                        }
                    }

                    await server.StopAsync();

                    // 缺陷 A 的护栏：StopAsync 必须清空会话表（不依赖 ConnectionClosed 回调）。
                    Assert.That(server.SessionCount, Is.EqualTo(0),
                        $"第 {round + 1} 轮 StopAsync 之后会话必须清零，实际 {server.SessionCount}");
                }
            }
            finally
            {
                try { await server.StopAsync(); } catch { }
                try { server.Dispose(); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  2. 重连风暴 30 轮：调度器注册必须回落为 0
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 重连风暴30轮_全部释放后共享调度器注册回落为0()
        {
            // 从干净的单例开始，LiveCount 才只反映本用例的连接。
            SharedTimeoutScheduler.ForceShutdown();

            var port = GetFreePort();
            var al = new ApplicationLayerParameters();

            // CONNECTION_IS_REDUNDANCY_GROUP：每个会话一个专属组 + 独立排空循环，
            // 同时覆盖「会话专属组随连接结束释放」的路径。
            var server = new Iec104Server(mode: ServerMode.CONNECTION_IS_REDUNDANCY_GROUP);
            await server.StartAsync(port);

            try
            {
                for (var round = 0; round < ReconnectRounds; round++)
                {
                    var client = new Iec104Client("127.0.0.1", port, null, al);
                    var got = 0;
                    client.AsduReceived += (in AsduView view) => Interlocked.Increment(ref got);

                    try
                    {
                        await client.ConnectAsync();

                        // 会话组由 STARTDT 激活事件创建，可能略晚于 STARTDT_CON 到达；
                        // 重试入队直到收到，避免把「组尚未建立」误判成投递失败。
                        var sw = Stopwatch.StartNew();
                        while (Volatile.Read(ref got) == 0 && sw.ElapsedMilliseconds < 3000)
                        {
                            await server.EnqueueAsync(Spontaneous(al));
                            await Task.Delay(20);
                        }

                        Assert.That(Volatile.Read(ref got), Is.GreaterThan(0),
                            $"第 {round + 1} 轮未收到服务端自发数据");
                    }
                    finally
                    {
                        try { await client.DisconnectAsync(); } catch { }
                        try { client.Dispose(); } catch { }
                    }
                }

                // 缺陷 B/C 的护栏：所有客户端与服务端会话都释放后，
                // 共享调度器的存活注册数必须回到 0（旧连接收尾不得把新注册清掉，也不得漏注销）。
                await WaitUntil(() => server.SessionCount == 0, 8000,
                    $"重连风暴结束后服务端会话应清零，实际 {server.SessionCount}");

                await WaitUntil(() => SharedTimeoutScheduler.LiveCount == 0, 5000,
                    $"全部释放后共享调度器 LiveCount 应为 0，实际 {SharedTimeoutScheduler.LiveCount}");
            }
            finally
            {
                try { await server.StopAsync(); } catch { }
                try { server.Dispose(); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  3. 同步 using 释放：必须释放端点与调度器注册
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 同步using释放_必须释放端点与调度器注册()
        {
            SharedTimeoutScheduler.ForceShutdown();

            var port = GetFreePort();
            var al = new ApplicationLayerParameters();
            var server = new Iec104Server();

            await server.StartAsync(port);

            try
            {
                // 关键：同步 using（不是 await using / DisposeAsync）。
                // 若 Iec104Client 只重写 DisposeAsync，此路径只走基类 SafetyDispose，
                // 端点持有的调度器注册会永久泄漏。
                using (var client = new Iec104Client("127.0.0.1", port, null, al))
                {
                    await client.ConnectAsync();
                    Assert.That(SharedTimeoutScheduler.LiveCount, Is.GreaterThan(0),
                        "前置条件：连接建立后应存在共享调度器注册");
                }

                // 停掉服务端，排除服务端会话的调度器注册，只留下客户端侧可观测。
                try { await server.StopAsync(); } catch { }

                await WaitUntil(() => SharedTimeoutScheduler.LiveCount == 0, 5000,
                    $"同步 using 释放后 LiveCount 必须回落为 0，实际 {SharedTimeoutScheduler.LiveCount}");
            }
            finally
            {
                try { await server.StopAsync(); } catch { }
                try { server.Dispose(); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  辅助
        // ══════════════════════════════════════════════════════════

        /// <summary>动态分配一个当前空闲的本地端口（避免固定端口冲突）。</summary>
        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待超时（{timeoutMs}ms）：{what}");
        }

        private static ASDU Spontaneous(ApplicationLayerParameters al)
        {
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject(new MeasuredValueScaled(100, 42, new QualityDescriptor()));
            return asdu;
        }
    }
}
