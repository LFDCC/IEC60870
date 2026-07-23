/*
 *  Iec104SendReceiveTests.cs
 *
 *  Automated round-trip tests for common IEC 60870-5-104 (CS104) send/receive
 *  commands. Each test starts a local Iec104Server + Iec104Client pair, then
 *  verifies that a command issued by the client is delivered to the server
 *  with the expected TypeID / CauseOfTransmission / CommonAddress, and that
 *  spontaneous data pushed by the server is delivered back to the client.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Time;
using IEC60870.CS104;
using NUnit.Framework;
using IEC60870.Core.Quality;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class Iec104SendReceiveTests
    {
        // Distinctive local port to avoid clashing with example servers.
        private const int Port = 24099;
        private const int DefaultCa = 1;

        private Iec104Server _server;
        private Iec104Client _client;

        // Captured ASDU views (copied out of the zero-copy ref struct immediately).
        private sealed class CapturedAsdu
        {
            public TypeID TypeId;
            public CauseOfTransmission Cot;
            public int CommonAddress;
            public bool IsTest;
            public bool IsNegative;
            public int NumberOfElements;
            public byte[] Raw;
        }

        private TaskCompletionSource<CapturedAsdu> _serverTcs;
        private TaskCompletionSource<CapturedAsdu> _clientTcs;

        private static CapturedAsdu Capture(in AsduView a) => new CapturedAsdu
        {
            TypeId = a.TypeId,
            Cot = a.Cot,
            CommonAddress = a.CommonAddress,
            IsTest = a.IsTest,
            IsNegative = a.IsNegative,
            NumberOfElements = a.NumberOfElements,
            Raw = a.Raw.ToArray()
        };

        private void ResetCaptures()
        {
            _serverTcs = new TaskCompletionSource<CapturedAsdu>();
            _clientTcs = new TaskCompletionSource<CapturedAsdu>();
        }

        private static async Task<T> Within<T>(Task<T> task, int seconds)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds));
            var completed = await Task.WhenAny(task, delay);
            if (ReferenceEquals(completed, delay))
                throw new TimeoutException($"Timed out after {seconds}s waiting for ASDU.");
            return await task;
        }

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _server = new Iec104Server();
            _server.AsduReceived += (Iec104Session session, in AsduView a) =>
            {
                _serverTcs?.TrySetResult(Capture(a));
            };
            await _server.StartAsync(Port);

            _client = new Iec104Client("127.0.0.1", Port);
            _client.AsduReceived += (in AsduView a) =>
            {
                _clientTcs?.TrySetResult(Capture(a));
            };
            // Autostart = true (default) -> sends STARTDT_ACT and waits for STARTDT_CON.
            await _client.ConnectAsync();
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            try { await _client.DisconnectAsync(); } catch { /* ignore */ }
            try { _server.Dispose(); } catch { /* ignore */ }
        }

        // ── Connectivity sanity ────────────────────────────────────────

        [Test]
        public void Autostart_ActivatesDataTransfer()
        {
            Assert.IsTrue(_client.IsActivated, "client should be activated after ConnectAsync (autostart)");
            Assert.AreEqual(1, _server.SessionCount, "server should hold exactly one session");
        }

        // ── Client -> Server commands (send direction) ─────────────────

        [Test]
        public async Task InterrogationCommand_SentAndReceived()
        {
            ResetCaptures();
            await _client.SendInterrogationCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, 20);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_IC_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.ACTIVATION, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task CounterInterrogationCommand_SentAndReceived()
        {
            ResetCaptures();
            await _client.SendCounterInterrogationCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, 5);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_CI_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.ACTIVATION, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task ReadCommand_SentAndReceived()
        {
            ResetCaptures();
            await _client.SendReadCommandAsync(DefaultCa, 100);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_RD_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.REQUEST, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task ClockSyncCommand_SentAndReceived()
        {
            ResetCaptures();
            var now = new CP56Time2a(DateTime.Now);
            await _client.SendClockSyncCommandAsync(DefaultCa, now);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_CS_NA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task TestCommand_SentAndReceived()
        {
            ResetCaptures();
            await _client.SendTestCommandAsync(DefaultCa);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_TS_NA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task TestCommandWithCP56Time2a_SentAndReceived()
        {
            ResetCaptures();
            var now = new CP56Time2a(DateTime.Now);
            await _client.SendTestCommandWithCP56Time2aAsync(DefaultCa, 0x1234, now);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_TS_TA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task ResetProcessCommand_SentAndReceived()
        {
            ResetCaptures();
            await _client.SendResetProcessCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, 1);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_RP_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.ACTIVATION, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task DelayAcquisitionCommand_SentAndReceived()
        {
            ResetCaptures();
            var delay = new CP16Time2a(100);
            await _client.SendDelayAcquisitionCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, delay);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_CD_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.ACTIVATION, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);
        }

        [Test]
        public async Task SingleControlCommand_SentAndReceived()
        {
            ResetCaptures();
            const int ioa = 100;
            var cmd = new SingleCommand(ioa, true, false, 0);
            await _client.SendControlCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, cmd);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_SC_NA_1, r.TypeId);
            Assert.AreEqual(CauseOfTransmission.ACTIVATION, r.Cot);
            Assert.AreEqual(DefaultCa, r.CommonAddress);

            // Decode the received ASDU and verify the command payload.
            var asdu = new ASDU(_server.Parameters, r.Raw, 0, r.Raw.Length);
            var received = asdu.GetElement(0) as SingleCommand;
            Assert.IsNotNull(received, "expected a SingleCommand element");
            Assert.AreEqual(ioa, received.ObjectAddress);
            Assert.IsTrue(received.State, "expected ON state");
            Assert.IsFalse(received.Select, "expected direct command (not select)");
        }

        [Test]
        public async Task DoubleControlCommand_SentAndReceived()
        {
            ResetCaptures();
            const int ioa = 101;
            var cmd = new DoubleCommand(ioa, DoubleCommand.ON, false, 0);
            await _client.SendControlCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, cmd);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_DC_NA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);

            var asdu = new ASDU(_server.Parameters, r.Raw, 0, r.Raw.Length);
            var received = asdu.GetElement(0) as DoubleCommand;
            Assert.IsNotNull(received);
            Assert.AreEqual(ioa, received.ObjectAddress);
            Assert.AreEqual(DoubleCommand.ON, received.State);
        }

        [Test]
        public async Task SetpointNormalizedControl_SentAndReceived()
        {
            ResetCaptures();
            const int ioa = 102;
            var cmd = new SetpointCommandNormalized(ioa, -0.5f, new SetpointCommandQualifier(false, 0));
            await _client.SendControlCommandAsync(CauseOfTransmission.ACTIVATION, DefaultCa, cmd);
            var r = await Within(_serverTcs.Task, 5);
            Assert.AreEqual(TypeID.C_SE_NA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);

            var asdu = new ASDU(_server.Parameters, r.Raw, 0, r.Raw.Length);
            var received = asdu.GetElement(0) as SetpointCommandNormalized;
            Assert.IsNotNull(received);
            Assert.AreEqual(ioa, received.ObjectAddress);
        }

        // ── Server -> Client spontaneous data (receive direction) ──────

        [Test]
        public async Task SpontaneousData_ServerToClient()
        {
            ResetCaptures();
            const int ioa = 200;
            var asdu = new ASDU(_server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, DefaultCa, false);
            asdu.AddInformationObject(new SinglePointInformation(ioa, true, new QualityDescriptor()));
            await _server.BroadcastAsync(asdu);

            var r = await Within(_clientTcs.Task, 5);
            Assert.AreEqual(TypeID.M_SP_NA_1, r.TypeId);
            Assert.AreEqual(DefaultCa, r.CommonAddress);

            var decoded = new ASDU(_server.Parameters, r.Raw, 0, r.Raw.Length);
            var spi = decoded.GetElement(0) as SinglePointInformation;
            Assert.IsNotNull(spi, "expected a SinglePointInformation element");
            Assert.AreEqual(ioa, spi.ObjectAddress);
            Assert.IsTrue(spi.Value, "expected ON value");
        }

        [Test]
        public async Task SpontaneousData_MultiplePoints_ServerToClient()
        {
            ResetCaptures();
            var asdu = new ASDU(_server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, DefaultCa, false);
            asdu.AddInformationObject(new SinglePointInformation(1, true, new QualityDescriptor()));
            asdu.AddInformationObject(new SinglePointInformation(2, false, new QualityDescriptor()));
            asdu.AddInformationObject(new SinglePointInformation(3, true, new QualityDescriptor()));
            await _server.BroadcastAsync(asdu);

            var r = await Within(_clientTcs.Task, 5);
            Assert.AreEqual(TypeID.M_SP_NA_1, r.TypeId);
            Assert.AreEqual(3, r.NumberOfElements);
        }
    }

    /// <summary>
    /// 验证连接断开时客户端能收到 ConnectionEvent（ConnectionClosed）。
    /// 模拟“手动关闭服务端窗口”= 服务端停止监听并断开所有会话，客户端应被通知。
    /// </summary>
    [TestFixture]
    public class Iec104ConnectionClosedTests
    {
        private const int Port = 24199;
        private Iec104Server _server;
        private Iec104Client _client;
        private readonly System.Collections.Generic.List<ApduConnectionEvent> _events = new();

        [SetUp]
        public async Task SetUp()
        {
            _events.Clear();
            _server = new Iec104Server();
            await _server.StartAsync(Port);

            _client = new Iec104Client("127.0.0.1", Port);
            _client.ConnectionEvent += ev =>
            {
                lock (_events) { _events.Add(ev); }
            };
            // Autostart = true -> 发送 STARTDT_ACT 并等待 STARTDT_CON，激活数据传输
            await _client.ConnectAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            try { await _client.DisconnectAsync(); } catch { /* ignore */ }
            try { _server.Dispose(); } catch { /* ignore */ }
        }

        [Test]
        public async Task ServerStop_RaisesConnectionClosedOnClient()
        {
            Assert.IsTrue(_client.IsActivated, "client should be activated before server stop");

            // 模拟“手动关闭服务端运行窗口”：停止服务端监听并断开所有会话
            await _server.StopAsync();

            // 等待客户端收到 ConnectionClosed（远端关闭 -> OnTcpClosed -> NotifyClosed）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool gotClosed = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (_events)
                {
                    if (_events.Contains(ApduConnectionEvent.ConnectionClosed))
                    {
                        gotClosed = true;
                        break;
                    }
                }
                await Task.Delay(50);
            }

            Assert.IsTrue(gotClosed,
                "client ConnectionEvent should contain ConnectionClosed after server stop. Received: " +
                string.Join(",", _events));
        }
    }

    /// <summary>
    /// 验证：客户端断开连接后，服务端 ConnectionEvent 能收到 ConnectionClosed（并带断连的会话）。
    /// 这是服务端感知“哪个会话断开”的核心能力——此前缺失（OnTcpClosed 未派发），是个缺陷。
    /// </summary>
    [TestFixture]
    public class ServerSideConnectionClosedTests
    {
        private const int Port = 24201;
        private Iec104Server _server;
        private Iec104Client _client;
        private readonly System.Collections.Generic.List<(Iec104Session, ApduConnectionEvent)> _events = new();

        [SetUp]
        public async Task SetUp()
        {
            _events.Clear();
            _server = new Iec104Server();
            _server.ConnectionEvent += (session, ev) =>
            {
                lock (_events) { _events.Add((session, ev)); }
            };
            await _server.StartAsync(Port);

            _client = new Iec104Client("127.0.0.1", Port);
            await _client.ConnectAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            try { await _client.DisconnectAsync(); } catch { /* ignore */ }
            try { _server.Dispose(); } catch { /* ignore */ }
        }

        [Test]
        public async Task ClientDisconnect_RaisesConnectionClosedOnServer()
        {
            Assert.IsTrue(_client.IsActivated, "client should be activated before disconnect");
            Assert.GreaterOrEqual(_server.SessionCount, 1, "server should have the client session");

            // 客户端主动断开（模拟客户端进程退出 / 网络中断）
            await _client.DisconnectAsync();

            // 等待服务端收到 ConnectionClosed（带来源会话）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool gotClosed = false;
            Iec104Session closedSession = null;
            while (sw.Elapsed < System.TimeSpan.FromSeconds(5))
            {
                lock (_events)
                {
                    foreach (var (s, ev) in _events)
                    {
                        if (ev == ApduConnectionEvent.ConnectionClosed)
                        {
                            gotClosed = true;
                            closedSession = s;
                            break;
                        }
                    }
                }
                if (gotClosed) break;
                await Task.Delay(50);
            }

            Assert.IsTrue(gotClosed,
                "server ConnectionEvent should contain ConnectionClosed after client disconnect. Received: " +
                string.Join(",", _events.ConvertAll(e => e.Item2.ToString())));
            Assert.IsNotNull(closedSession, "ConnectionClosed should carry the disconnected session");
            Assert.AreEqual(0, _server.SessionCount, "session should be unregistered after disconnect");
        }
    }
}
