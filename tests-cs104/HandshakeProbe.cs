using System;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class HandshakeProbeTests
    {
        // 纯手动诊断探针：只打印握手过程、无断言、固定等待 2 秒。
        // 标 Explicit 避免它「永远通过」给 CI 的全绿结果带来虚假安全感。
        [Test]
        [Explicit("手动诊断探针，需人工观察控制台输出；不参与常规测试")]
        public async Task Handshake()
        {
            var server = new Iec104Server();
            server.ConnectionEvent += (session, ev) =>
                Console.WriteLine($"[server] session event: {ev}");
            server.AsduReceived += (Iec104Session session, in AsduView a) =>
                Console.WriteLine($"[server] ASDU type={a.TypeId} cot={a.Cot}");
            server.ConnectionEvent += (Iec104Session session, ApduConnectionEvent ev) =>
            {
                session.RawFrameReceived += b =>
                    Console.WriteLine($"[server RX] {BitConverter.ToString(b)}");
                session.RawFrameSent += b =>
                    Console.WriteLine($"[server TX] {BitConverter.ToString(b)}");
            };
            await server.StartAsync(24098);

            var client = new Iec104Client("127.0.0.1", 24098);
            client.ConnectionEvent += ev => Console.WriteLine($"[client] event: {ev}");
            client.RawFrameSent += b => Console.WriteLine($"[client TX] {BitConverter.ToString(b)}");
            client.RawFrameReceived += b => Console.WriteLine($"[client RX] {BitConverter.ToString(b)}");
            try
            {
                await client.ConnectAsync();
                Console.WriteLine($"client.IsActivated={client.IsActivated}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ConnectAsync threw: {ex.GetType().Name}: {ex.Message}");
            }
            await Task.Delay(2000);
            Console.WriteLine($"after 2s: client.IsActivated={client.IsActivated} online={client.Online} serverSessions={server.SessionCount}");
            try
            {
                await client.DisconnectAsync();
            }
            catch { }
            server.Dispose();
        }
    }
}
