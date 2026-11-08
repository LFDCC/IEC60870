/*
 *  CS101FileTransferTests.cs
 *
 *  CS101 文件传输端到端回归测试（平衡链路 + TCP，进程内主从对跑）。
 *
 *  背景：文件传输在原实现里是"卡死"重灾区，且过去没有任何端到端覆盖
 *  （只有 F_FR_NA_1 等单个 ASDU 的编解码往返测试）。本文件锁死两条完整路径：
 *
 *  1. 下载（监视方向）：从站持有文件 → 主站 GetFile 拉取 → 逐段接收 → 校验内容。
 *     这条路径会验证从站在平衡链路上"主动推帧"的能力（PRM=1 的 USER_DATA_CONFIRMED），
 *     以及主站对这帧的链路层应答。
 *
 *  2. 上传（控制方向）：主站 SendFile 推送 → 从站 FileReadyHandler 接收 → 校验内容。
 *     这条路径会验证主站的周期驱动（FileClient.HandleFileService）——
 *     段与段之间没有对端来帧触发，必须由本端循环推进，否则第一段之后就永久停住。
 *
 *  两个用例分别覆盖 UseSingleCharACK=true（E5 单字符应答）与 false（固定帧 ACK）两种链路应答形态。
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class CS101FileTransferTests
    {
        private const int DownloadPort = 24102;
        private const int UploadPort = 24103;
        private const int UnsolicitedPort = 24104;

        /// <summary>构造一段确定性测试数据（每个字节 = 序号+1，跨 256 回绕）。</summary>
        private static byte[] BuildPayload(int length)
        {
            var data = new byte[length];

            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)(i + 1);
            }

            return data;
        }

        /// <summary>把收到的分段拼成一个缓冲，并用信号量通知"传输结束"。</summary>
        private sealed class CollectingReceiver : IFileReceiver
        {
            private readonly byte[] _buffer;
            private int _received;

            public readonly ManualResetEventSlim FinishedEvent = new ManualResetEventSlim(false);

            public FileErrorCode Result { get; private set; } = FileErrorCode.PROTOCOL_ERROR;

            public int ReceivedBytes => _received;

            public CollectingReceiver(int bufferSize)
            {
                _buffer = new byte[bufferSize];
            }

            public byte[] GetData()
            {
                var copy = new byte[_received];
                Array.Copy(_buffer, copy, _received);
                return copy;
            }

            public void SegmentReceived(byte sectionName, int offset, int size, byte[] data)
            {
                // offset 是主站侧的段偏移；直接追加即可（主站的 _currentSectionOffset 单调递增）
                Array.Copy(data, 0, _buffer, _received, size);
                _received += size;
            }

            public void Finished(FileErrorCode result)
            {
                Result = result;
                FinishedEvent.Set();
            }
        }

        /// <summary>记录 TransferComplete 结果的 IFileProvider（从站下发文件用）。</summary>
        private sealed class RecordingFile : TransparentFile
        {
            public readonly ManualResetEventSlim CompletedEvent = new ManualResetEventSlim(false);

            public bool? Success { get; private set; }

            public RecordingFile(int ca, int ioa, NameOfFile nof)
                : base(ca, ioa, nof)
            {
            }

            public override void TransferComplete(bool success)
            {
                Success = success;
                CompletedEvent.Set();
            }
        }

        private static LinkLayerParameters MakeLinkParams(bool singleCharAck)
        {
            return new LinkLayerParameters
            {
                AddressLength = 1,
                UseSingleCharACK = singleCharAck,
            };
        }

        /// <summary>等待主站链接层进入 AVAILABLE（平衡握手完成），超时抛断言失败。</summary>
        private static async Task WaitForLinkAvailable(Iec101Client master, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (master.GetLinkLayerState() == LinkLayerState.AVAILABLE)
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"主站链接层未在 {timeoutMs}ms 内进入 AVAILABLE，当前状态 {master.GetLinkLayerState()}");
        }

        private static void DisposePair(Iec101Server slave, Iec101Client master,
            CancellationTokenSource slaveCts, CancellationTokenSource masterCts,
            Task slaveTask, Task masterTask)
        {
            master.Stop();
            slave.Stop();
            master.Dispose();
            slave.Dispose();
            slaveCts.Cancel();
            masterCts.Cancel();

            try
            {
                Task.WaitAll(new[] { slaveTask, masterTask }, TimeSpan.FromSeconds(5));
            }
            catch
            {
                /* 取消即退出 */
            }
        }

        // ══════════════════════════════════════════════════════════
        //  1. 下载（从站 → 主站）：从站主动推帧 + 主站应答
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 文件下载_从站下发到主站_内容一致()
        {
            const int fileIoa = 30000;
            var payload = BuildPayload(1025);

            var ll = MakeLinkParams(singleCharAck: true);
            var al = new ApplicationLayerParameters();

            // ── 从站：注册一个可下发的透明文件 ────────────────────────
            var slave = new Iec101Server(DownloadPort, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;

            var offeredFile = new RecordingFile(1, fileIoa, NameOfFile.TRANSPARENT_FILE);
            offeredFile.AddSection(payload);
            slave.GetAvailableFiles().AddFile(offeredFile);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            // ── 主站 ──────────────────────────────────────────────
            var master = new Iec101Client("127.0.0.1", DownloadPort, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);

            await WaitForLinkAvailable(master);

            var receiver = new CollectingReceiver(payload.Length);
            master.GetFile(1, fileIoa, NameOfFile.TRANSPARENT_FILE, receiver);

            Assert.That(receiver.FinishedEvent.Wait(TimeSpan.FromSeconds(15)), Is.True,
                $"文件下载未在超时内完成，已收 {receiver.ReceivedBytes}/{payload.Length} 字节");
            Assert.That(receiver.Result, Is.EqualTo(FileErrorCode.SUCCESS), "下载应成功结束");
            Assert.That(receiver.GetData(), Is.EqualTo(payload), "下载内容应与从站提供的文件一致");

            DisposePair(slave, master, slaveCts, masterCts, slaveTask, masterTask);
        }

        // ══════════════════════════════════════════════════════════
        //  2. 上传（主站 → 从站）：主站周期驱动 + 从站收齐
        // ══════════════════════════════════════════════════════════

        [Test]
        public async Task 文件上传_主站推送到从站_内容一致()
        {
            const int fileIoa = 30001;
            var payload = BuildPayload(1025);

            var ll = MakeLinkParams(singleCharAck: false);
            var al = new ApplicationLayerParameters();

            CollectingReceiver receiver = null;

            // ── 从站：接受文件下载 ────────────────────────────────
            var slave = new Iec101Server(UploadPort, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;

            slave.SetFileReadyHandler((parameter, ca, ioa, nof, lengthOfFile) =>
            {
                if ((ca == 1) && (ioa == fileIoa) && (nof == NameOfFile.TRANSPARENT_FILE) && (lengthOfFile == payload.Length))
                {
                    receiver = new CollectingReceiver(lengthOfFile);
                    return receiver;
                }

                return null;
            }, null);

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            // ── 主站 ──────────────────────────────────────────────
            var master = new Iec101Client("127.0.0.1", UploadPort, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);

            await WaitForLinkAvailable(master);

            var provider = new RecordingFile(1, fileIoa, NameOfFile.TRANSPARENT_FILE);
            provider.AddSection(payload);

            master.SendFile(1, fileIoa, NameOfFile.TRANSPARENT_FILE, provider);

            Assert.That(provider.CompletedEvent.Wait(TimeSpan.FromSeconds(15)), Is.True,
                "主站侧文件上传未在超时内完成（应在收到 FILE ACK 后置位）");
            Assert.That(provider.Success, Is.True, "主站应收到从站的文件级肯定确认");

            Assert.That(receiver, Is.Not.Null, "从站应已接受文件下发请求");
            Assert.That(receiver.FinishedEvent.Wait(TimeSpan.FromSeconds(5)), Is.True,
                $"从站未在超时内收完文件，已收 {receiver.ReceivedBytes}/{payload.Length} 字节");
            Assert.That(receiver.Result, Is.EqualTo(FileErrorCode.SUCCESS), "从站应成功收完文件");
            Assert.That(receiver.GetData(), Is.EqualTo(payload), "从站收到内容应与主站推送的文件一致");

            DisposePair(slave, master, slaveCts, masterCts, slaveTask, masterTask);
        }

        // ══════════════════════════════════════════════════════════
        //  3. 链路层应答与"应用层是否处理"无关
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 从站在平衡链路上主动推一个主站"不关心"的 ASDU（这里用启动时入队的 M_EI_NA_1 初始化结束报文），
        /// 主站没有注册任何 ASDU 处理回调。
        ///
        /// 链路层必须照样回 ACK —— 确认型用户数据帧的应答是链路层职责，与上层是否消费该报文无关。
        /// 若把应答绑定在"应用层返回值"上，主站将保持沉默，从站的 PRIMARY 状态机在 ACK 超时后
        /// 复位链路并反复重连（实测表现为：文件上传最终 Transfer complete: False，
        /// 因为从站的链路在文件协商过程中被反复打断）。
        /// </summary>
        [Test]
        public async Task 未被应用层消费的ASDU_链路层仍应应答()
        {
            var ll = MakeLinkParams(singleCharAck: true);

            var slave = new Iec101Server(UnsolicitedPort, ll);
            slave.LinkLayerAddress = 3;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;

            var slaveCts = new CancellationTokenSource();
            var slaveTask = Task.Run(() => slave.StartAsync(slaveCts.Token), CancellationToken.None);
            await Task.Delay(300);

            var master = new Iec101Client("127.0.0.1", UnsolicitedPort, LinkLayerMode.BALANCED, ll);
            master.OwnAddress = 1;
            master.SlaveAddress = 3;
            // 关键前提：不注册任何 ASDU 处理回调（"应用层不消费该报文"）

            var masterSentFrames = 0;
            master.RawFrameSent += _ => Interlocked.Increment(ref masterSentFrames);

            var masterCts = new CancellationTokenSource();
            var masterTask = master.StartAsync(masterCts.Token);

            await WaitForLinkAvailable(master);

            // 主站自己的 PLL 达到 AVAILABLE 不代表从站的 PLL 握手也走完了
            // （从站会独立发起 REQUEST_LINK_STATUS → 主站 SLL 应答 STATUS_OF_LINK → RESET_REMOTE_LINK → ACK）。
            // 先等链路彻底静默，才能把"主站发帧"唯一归因于对 EOI 的链路层应答。
            await Task.Delay(1500);
            Interlocked.Exchange(ref masterSentFrames, 0);
            await Task.Delay(500);

            Assert.That(Volatile.Read(ref masterSentFrames), Is.EqualTo(0),
                "静默期主站不应主动发送任何帧（测试前提被破坏：链路仍在握手）");

            var eoi = new ASDU(slave.Parameters, CauseOfTransmission.INITIALIZED, false, false, 0, 1, false);
            eoi.AddInformationObject(new EndOfInitialization(0));
            slave.EnqueueUserDataClass1(eoi);

            // 等待主站的链路层应答（超时窗口取 ACK 超时 1s 的 3 倍）
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref masterSentFrames) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var sent = Volatile.Read(ref masterSentFrames);

            DisposePair(slave, master, slaveCts, masterCts, slaveTask, masterTask);

            Assert.That(sent, Is.GreaterThan(0),
                "主站未对未被应用层消费的确认型用户数据帧作出链路层应答 —— 从站将 ACK 超时并复位链路");
            Assert.That(sent, Is.EqualTo(1),
                $"主站应答次数应为 1（仅一次链路层 ACK），实际发出 {sent} 帧");
        }
    }
}
