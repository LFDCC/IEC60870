/*
 *  CS101FileFailureTests.cs
 *
 *  CS101 文件传输「失败路径」回归测试。
 *
 *  背景：本会话修好文件传输卡死后，深度审计发现修复只覆盖了「成功路径」：
 *  SetFileReadyHandler 转发修好后下载方向才可用，但 FileServer 的超时/中止/校验和
 *  以及 FileClient 的失败清理都只在一侧实现。本文件锁死这些失败分支。
 *
 *  用进程内的 IClientConnection 桩驱动 FileServer，不需要起 TCP。
 */

using System;
using System.Collections.Generic;
using IEC60870.Core;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS101FileFailureTests
    {
        private static ApplicationLayerParameters Al() => new ApplicationLayerParameters();

        /// <summary>记录所有发出的 ASDU 的 IClientConnection 桩。</summary>
        /// <summary>把桩连接记录到的 ASDU 转成可 GetElement 的线字节往返对象。</summary>
        private static ASDU Materialize(ASDU asdu)
        {
            var al = Al();
            var frame = new BufferFrame(new byte[256], 0);
            asdu.Encode(frame, al);
            return new ASDU(al, frame.GetBuffer(), 0, frame.GetMsgSize());
        }

        private sealed class RecordingConnection : IClientConnection
        {
            public readonly List<ASDU> Sent = new List<ASDU>();
            private readonly ApplicationLayerParameters _al = Al();

            public void SendASDU(ASDU asdu) { lock (Sent) { Sent.Add(asdu); } }
            public void SendACT_CON(ASDU asdu, bool negative) { }
            public void SendACT_TERM(ASDU asdu) { }
            public ApplicationLayerParameters GetApplicationLayerParameters() => _al;
        }

        /// <summary>记录 Finished 结果的 IFileReceiver 桩。</summary>
        private sealed class RecordingReceiver : IFileReceiver
        {
            public FileErrorCode? Result;
            public int Segments;

            public void Finished(FileErrorCode result) { Result = result; }
            public void SegmentReceived(byte sectionName, int offset, int size, byte[] data) { Segments++; }
        }

        private static ASDU FileReady(int ca, int ioa, int length)
        {
            // 必须经「编码→线字节→解码」往返：ASDU 对象的 GetElement 从编码后的
            // payload 解码，直接构造的对象没有线字节，GetElement(0) 会抛
            // ASDUParsingException（信息对象地址不完整）。
            var al = Al();
            var src = new ASDU(al, CauseOfTransmission.FILE_TRANSFER, false, false, 0, ca, false);
            src.AddInformationObject(new FileReady(ioa, NameOfFile.TRANSPARENT_FILE, length, true));

            var frame = new BufferFrame(new byte[256], 0);
            src.Encode(frame, al);
            return new ASDU(al, frame.GetBuffer(), 0, frame.GetMsgSize());
        }

        // ══════════════════════════════════════════════════════════
        //  1. 下载方向超时必须通知 receiver 并复位状态
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 下载方向超时_必须通知接收者TIMEOUT并复位状态()
        {
            var conn = new RecordingConnection();
            var receiver = new RecordingReceiver();
            var server = new FileServer(conn, new FilesAvailable(), _ => { });
            server.SetFileReadyHandler((_, _, _, _, _) => receiver, null);
            server.Timeout = 1;

            // 主站声明文件 → 从站进入 WAITING_FOR_SECTION_READY（下载方向），此后不再来帧
            Assert.That(server.HandleFileAsdu(FileReady(1, 30001, 8)), Is.True);
            Assert.That(receiver.Result, Is.Null, "前置条件：接收者尚未结束");

            System.Threading.Thread.Sleep(20);
            server.HandleFileTransmission();

            Assert.That(receiver.Result, Is.EqualTo(FileErrorCode.TIMEOUT),
                "下载方向超时必须通知 IFileReceiver.Finished(TIMEOUT) —— 原实现只认 _selectedFile，"
                + "把下载状态完全排除在超时处理外，主站中途停发即永久卡死");

            // 复位后不应再重复通知（幂等）
            var before = conn.Sent.Count;
            server.HandleFileTransmission();
            Assert.That(conn.Sent.Count, Is.EqualTo(before), "复位到 IDLE 后不得再产生任何报文");
        }

        // ══════════════════════════════════════════════════════════
        //  2. SEND_ABORT 必须发出 DEACTIVATE_FILE
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 中止态_必须发出DEACTIVATE_FILE而不是只等超时()
        {
            var conn = new RecordingConnection();
            var server = new FileServer(conn, new FilesAvailable(), _ => { });

            // 通过反射把状态置为 SEND_ABORT（该状态原为死状态，4 处赋值、0 处处理）
            var field = typeof(FileServer).GetField("_transferState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(server, FileServerState.SEND_ABORT);

            // 让超时门槛也能进入（否则 return 前就被挡住）
            var lastSent = typeof(FileServer).GetField("_lastSentTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            lastSent.SetValue(server, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            server.HandleFileTransmission();

            bool sentDeactivate = false;
            foreach (var sent in conn.Sent)
            {
                var asdu = Materialize(sent);
                if (asdu.TypeId == TypeID.F_SC_NA_1)
                {
                    var io = asdu.GetElement(0);
                    if (io is FileCallOrSelect fcs
                        && fcs.SCQ == SelectAndCallQualifier.DEACTIVATE_FILE)
                    {
                        sentDeactivate = true;
                    }
                }
            }

            Assert.That(sentDeactivate, Is.True,
                "SEND_ABORT 原是死状态：4 处赋值却无处理分支，DEACTIVATE_FILE 从不发出。"
                + "修复后必须主动发中止帧，与 FileClient.AbortFileTransfer 对称");
        }

        // ══════════════════════════════════════════════════════════
        //  3. 服务端构造前访问 DIR 不得空引用
        // ══════════════════════════════════════════════════════════

        [Test]
        public void 服务端启动前访问链路属性不得空引用()
        {
            using var stream = new System.IO.MemoryStream();
            var server = new Iec101Server(stream);

            Assert.DoesNotThrow(() => { server.DIR = true; _ = server.DIR; },
                "Iec101Server 原在 RunAsync 里懒建 _linkLayer，启动前访问 DIR 会 NRE");
            Assert.DoesNotThrow(() => server.SetReceivedRawMessageHandler((_, _, _) => true, null),
                "启动前订阅原始报文事件不得 NRE");
            Assert.DoesNotThrow(() => server.SendLinkLayerTestFunction(),
                "启动前调用链路测试不得 NRE");
        }
    }
}
