/*
 *  Iec104ReadWaitTests.cs
 *
 *  「发送读命令并等待应答」的串行 / 并发 / 干扰用例。
 *  重点验证并发等待不会串数据：不同 IOA 并发、同一 IOA 并发、
 *  以及等待期间同 IOA ／ 相邻 IOA 的突发上送不会顶替读应答。
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using NUnit.Framework;

namespace IEC60870.Tests
{
    [TestFixture]
    public class Iec104ReadWaitTests
    {
        private const int Port = 24601;
        private const int Ca = 1;

        // IOA 分区约定（服务端据此决定怎么应答，测试之间互不影响）：
        //   100…799  正常回值
        //   800…899  否定应答（回 C_RD_NA_1 + P/N）
        //   900…999  沉默（不回应答，用于超时用例）
        private const int NegativeIoaBase = 800;
        private const int SilentIoaBase = 900;

        // 500…519 的读请求：先喷 SpamPerRead 帧突发上送（值恒为 -1.5），再回真正的读应答。
        private const int SpamIoaBase = 500;
        private const int SpamIoaCount = 20;
        private const int SpamPerRead = 20;
        private const float SpontaneousValue = -1.5f;

        private Iec104Server _server;
        private Iec104Client _client;
        private int _spontaneousReceived;

        /// <summary>读应答的期望值：由 IOA 唯一决定，便于并发用例判定是否串台。</summary>
        private static float ReadValue(int ioa) => ioa + 0.25f;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _server = new Iec104Server();
            _server.AsduReceived += OnServerAsdu;
            await _server.StartAsync(Port);

            _client = new Iec104Client("127.0.0.1", Port)
            {
                CommandConfirmationTimeout = TimeSpan.FromSeconds(10)
            };
            _client.AsduReceived += (in AsduView a) =>
            {
                if (a.Cot == CauseOfTransmission.SPONTANEOUS)
                {
                    Interlocked.Increment(ref _spontaneousReceived);
                }
            };
            await _client.ConnectAsync();
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch { /* ignore */ }

            try
            {
                _server.Dispose();
            }
            catch { /* ignore */ }
        }

        /// <summary>假从站：按 IOA 分区回读应答 / 否定 / 沉默 / 突发干扰。</summary>
        private void OnServerAsdu(Iec104Session session, in AsduView a)
        {
            if (a.TypeId != TypeID.C_RD_NA_1)
            {
                return;
            }

            ReadCommand request;
            try
            {
                request = a.GetElement(0) as ReadCommand;
            }
            catch (Exception)
            {
                return;
            }

            if (request == null)
            {
                return;
            }

            var ca = a.Ca;
            var ioa = request.ObjectAddress;

            if (ioa >= SilentIoaBase)
            {
                return; // 沉默：不回任何东西
            }

            if (ioa >= NegativeIoaBase)
            {
                // 装置拒绝读：回 C_RD_NA_1 的否定应答（不带数据）。
                var negative = new ASDU(_server.Parameters, CauseOfTransmission.REQUEST,
                    isTest: false, isNegative: true, oa: 0, ca: ca, isSequence: false);
                negative.AddInformationObject(new ReadCommand(ioa));
                _ = Task.Run(() => SendSafeAsync(session, negative));
                return;
            }

            var firstFrame = ioa >= SpamIoaBase && ioa < SpamIoaBase + SpamIoaCount;

            _ = Task.Run(async () =>
            {
                // 干扰：同 IOA 及相邻 IOA 的突发上送先到，读应答后到。
                if (firstFrame)
                {
                    for (var i = 0; i < SpamPerRead; i++)
                    {
                        var spamIoa = SpamIoaBase + (i % SpamIoaCount);
                        var spam = new ASDU(_server.Parameters, CauseOfTransmission.SPONTANEOUS,
                            isTest: false, isNegative: false, oa: 0, ca: ca, isSequence: false);
                        spam.AddInformationObject(
                            new MeasuredValueShort(spamIoa, SpontaneousValue, QualityDescriptor.VALID()));

                        if (!await SendSafeAsync(session, spam))
                        {
                            return;
                        }
                    }
                }

                var response = new ASDU(_server.Parameters, CauseOfTransmission.REQUEST,
                    isTest: false, isNegative: false, oa: 0, ca: ca, isSequence: false);
                response.AddInformationObject(
                    new MeasuredValueShort(ioa, ReadValue(ioa), QualityDescriptor.VALID()));

                await SendSafeAsync(session, response);
            });
        }

        /// <summary>会话已断开时不抛异常（干扰用例的 Task 可能晚于断连）。</summary>
        private static async Task<bool> SendSafeAsync(Iec104Session session, ASDU asdu)
        {
            try
            {
                await session.SendAsync(asdu);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── 串行 ───────────────────────────────────────────────────────

        [Test]
        public async Task Read_ReturnsDecodedValueAndType()
        {
            var result = await _client.SendReadCommandAndWaitAsync(Ca, 42);

            Assert.AreEqual(Ca, result.CommonAddress);
            Assert.AreEqual(42, result.Ioa);
            Assert.IsFalse(result.IsNegative);
            Assert.AreEqual(TypeID.M_ME_NC_1, result.TypeId);
            Assert.IsInstanceOf<MeasuredValueShort>(result.Value);
            Assert.AreEqual(ReadValue(42), ((MeasuredValueShort)result.Value).Value);
        }

        [Test]
        public async Task Read_NegativeReply_ReturnsNegativeWithoutData()
        {
            var result = await _client.SendReadCommandAndWaitAsync(Ca, NegativeIoaBase + 7);

            Assert.IsTrue(result.IsNegative);
            Assert.IsNull(result.Value);
            Assert.AreEqual(TypeID.C_RD_NA_1, result.TypeId);
            Assert.AreEqual(NegativeIoaBase + 7, result.Ioa);
        }

        // ── 并发：不同 IOA ─────────────────────────────────────────────

        [Test]
        public async Task Read_ConcurrentDifferentIoa_NoCrossTalk()
        {
            const int count = 200;

            var tasks = new Task<ReadResponse>[count];
            for (var i = 0; i < count; i++)
            {
                // int 局部变量：避免闭包捕获循环变量
                var ioa = 100 + i;
                tasks[i] = _client.SendReadCommandAndWaitAsync(Ca, ioa);
            }

            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

            for (var i = 0; i < count; i++)
            {
                var expectedIoa = 100 + i;
                Assert.AreEqual(expectedIoa, results[i].Ioa, $"第 {i} 个读应答的 IOA 串台");
                Assert.IsFalse(results[i].IsNegative);
                Assert.IsInstanceOf<MeasuredValueShort>(results[i].Value);

                var actual = ((MeasuredValueShort)results[i].Value).Value;
                Assert.AreEqual(ReadValue(expectedIoa), actual,
                    $"IOA={expectedIoa} 的读值错乱（收到 {actual}）");
            }
        }

        // ── 并发：同一 IOA（应答不可区分，只能先进先出逐个交付）────────

        [Test]
        public async Task Read_ConcurrentSameIoa_AllCompleteInOrder()
        {
            const int count = 60;
            const int ioa = 777;

            var tasks = new Task<ReadResponse>[count];
            for (var i = 0; i < count; i++)
            {
                tasks[i] = _client.SendReadCommandAndWaitAsync(Ca, ioa);
            }

            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.AreEqual(count, results.Length);
            foreach (var result in results)
            {
                Assert.AreEqual(ioa, result.Ioa);
                Assert.IsFalse(result.IsNegative);
                Assert.AreEqual(ReadValue(ioa), ((MeasuredValueShort)result.Value).Value);
            }
        }

        // ── 并发 + 突发上送干扰：同 IOA / 相邻 IOA 的自发帧不得顶替读应答 ──

        [Test]
        public async Task Read_ConcurrentWithSpontaneousTraffic_DoesNotCrossTalk()
        {
            const int count = SpamIoaCount;
            var before = Volatile.Read(ref _spontaneousReceived);

            var tasks = new Task<ReadResponse>[count];
            for (var i = 0; i < count; i++)
            {
                var ioa = SpamIoaBase + i;
                tasks[i] = _client.SendReadCommandAndWaitAsync(Ca, ioa);
            }

            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

            for (var i = 0; i < count; i++)
            {
                var expectedIoa = SpamIoaBase + i;
                Assert.IsFalse(results[i].IsNegative);
                Assert.AreEqual(expectedIoa, results[i].Ioa);

                var actual = ((MeasuredValueShort)results[i].Value).Value;
                Assert.AreEqual(ReadValue(expectedIoa), actual,
                    $"IOA={expectedIoa} 的读应答被突发帧顶替（收到 {actual}）");
            }

            // 突发帧仍要正常投递到 AsduReceived，不能被读匹配器吞掉。
            var received = Volatile.Read(ref _spontaneousReceived) - before;
            Assert.GreaterOrEqual(received, count * SpamPerRead,
                "突发上送帧应当全部到达 AsduReceived（读匹配器不得吞帧）");
        }

        // ── 超时 ───────────────────────────────────────────────────────

        [Test]
        public async Task Read_NoReply_TimesOutAndConnectionStaysUsable()
        {
            await using var client = new Iec104Client("127.0.0.1", Port)
            {
                CommandConfirmationTimeout = TimeSpan.FromMilliseconds(300)
            };
            await client.ConnectAsync();

            Assert.ThrowsAsync<TimeoutException>(async () =>
                await client.SendReadCommandAndWaitAsync(Ca, SilentIoaBase + 1));

            // 超时不关连接，后续读仍正常（挂起槽位已清理）。
            var ok = await client.SendReadCommandAndWaitAsync(Ca, 123);
            Assert.IsFalse(ok.IsNegative);
            Assert.AreEqual(ReadValue(123), ((MeasuredValueShort)ok.Value).Value);

            await client.DisconnectAsync();
        }

        // ── 断开唤醒 ───────────────────────────────────────────────────

        [Test]
        public async Task Read_DisconnectWhileWaiting_IsWokenUp()
        {
            await using var client = new Iec104Client("127.0.0.1", Port)
            {
                CommandConfirmationTimeout = TimeSpan.FromSeconds(30)
            };
            await client.ConnectAsync();

            var pending = client.SendReadCommandAndWaitAsync(Ca, SilentIoaBase + 2);
            await Task.Delay(100);

            var started = Environment.TickCount64;
            await client.DisconnectAsync();

            Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            Assert.Less(Environment.TickCount64 - started, 10_000,
                "连接断开后读等待者应立即被唤醒，而不是等满 30 秒超时");
        }
    }
}
