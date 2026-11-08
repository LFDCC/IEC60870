/*
 *  CS101 主站文件传输示例（FT1.2 over TCP 隧道，平衡链路）。
 *
 *  演示两个方向：
 *    1. 上传（主站 → 从站）：SendFile(...) 声明 FILE READY，等待从站接受后逐段推送，
 *       以从站的文件级肯定确认（FILE ACK）作为结束标志；
 *    2. 下载（从站 → 主站）：GetFile(...) 发起 SELECT/CALL，逐段收到 IFileReceiver。
 *
 *  用法：
 *    dotnet run                          # 用内置的 1025 字节测试数据上传到 CA=1 IOA=30001
 *    dotnet run -- <host> <file> <ca> <ioa>   # 上传指定文件
 *
 *  运行前先启动 examples/101/cs101-slave-file。
 *
 *  注意：文件传输必须两端同为 BALANCED —— 主站推段之后没有对端报文触发下一段，
 *  靠主站自己的收发循环周期驱动（FileClient.HandleFileService），
 *  这也是本示例用 StartAsync 后台循环而不是手动单步驱动的原因。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101;
using TouchSocket.Core;

namespace cs101_master_file
{
    /// <summary>可上传文件：继承 TransparentFile，用完成回调/事件通知调用方。</summary>
    public class SimpleFile : TransparentFile
    {
        private readonly TaskCompletionSource<bool> _complete =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public SimpleFile(int ca, int ioa, NameOfFile nof)
            : base(ca, ioa, nof)
        {
        }

        public override void TransferComplete(bool success)
        {
            Console.WriteLine("[master] 文件上传结束: " + success);
            _complete.TrySetResult(success);
        }

        /// <summary>等待传输结束；超时返回 false（示例不无限阻塞，便于排障）。</summary>
        public async Task<bool> WaitUntilTransferIsCompleteAsync(TimeSpan timeout)
        {
            if (await Task.WhenAny(_complete.Task, Task.Delay(timeout)).ConfigureAwait(false) == _complete.Task)
            {
                return await _complete.Task.ConfigureAwait(false);
            }

            return false;
        }
    }

    /// <summary>接收下发文件的 IFileReceiver：先缓存在内存里，结束后比对/打印。</summary>
    public class ConsoleFileReceiver : IFileReceiver
    {
        private readonly byte[] _buffer;
        private readonly TaskCompletionSource<FileErrorCode> _finished =
            new TaskCompletionSource<FileErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _received;

        public ConsoleFileReceiver(int bufferSize)
        {
            _buffer = new byte[bufferSize];
        }

        public int ReceivedBytes => _received;

        public byte[] GetData()
        {
            var copy = new byte[_received];
            Array.Copy(_buffer, copy, _received);
            return copy;
        }

        public void SegmentReceived(byte sectionName, int offset, int size, byte[] data)
        {
            Array.Copy(data, 0, _buffer, _received, size);
            _received += size;
            Console.WriteLine("[master] 收到分段 NoS={0} offset={1} size={2}（累计 {3} 字节）",
                sectionName, offset, size, _received);
        }

        public void Finished(FileErrorCode result)
        {
            Console.WriteLine("[master] 文件下载结束: " + result);
            _finished.TrySetResult(result);
        }

        public async Task<FileErrorCode> WaitUntilFinishedAsync(TimeSpan timeout)
        {
            if (await Task.WhenAny(_finished.Task, Task.Delay(timeout)).ConfigureAwait(false) == _finished.Task)
            {
                return await _finished.Task.ConfigureAwait(false);
            }

            return FileErrorCode.TIMEOUT;
        }
    }

    class MainClass
    {
        /// <summary>等待链接层进入 AVAILABLE（平衡握手完成）。</summary>
        private static async Task<bool> WaitForLinkAsync(Iec101Client master, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (master.GetLinkLayerState() == LinkLayerState.AVAILABLE)
                {
                    return true;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            return false;
        }

        public static async Task Main(string[] args)
        {
            var hostname = "127.0.0.1";
            string? filename = null;
            var fileCa = 1;
            var fileIoa = 30001;

            if (args.Length >= 1)
            {
                hostname = args[0];
            }

            if (args.Length >= 2)
            {
                filename = args[1];
            }

            if (args.Length >= 3)
            {
                Int32.TryParse(args[2], out fileCa);
            }

            if (args.Length >= 4)
            {
                Int32.TryParse(args[3], out fileIoa);
            }

            Console.WriteLine("Using IEC60870.Core.NET version " + typeof(ASDU).Assembly.GetName().Version?.ToString());

            // CS101 主站文件客户端：平衡链路连到从站的 TCP 隧道
            var master = new Iec101Client(hostname, 2404, LinkLayerMode.BALANCED);

            // 日志级别设为 Debug 可看到逐帧收发（排障用）；正式工程按需调高
            var logger = new LoggerGroup();
            logger.AddConsoleLogger(LogLevel.Debug);
            master.Logger = logger;

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; cts.Cancel(); };

            // 启动后台收发循环：文件传输的段间推进依赖它
            _ = master.StartAsync(cts.Token);

            if (!await WaitForLinkAsync(master, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                Console.WriteLine("[master] 链接层未建立，请确认从站 (cs101-slave-file) 已启动");
                return;
            }

            // ── 1. 上传 ────────────────────────────────────────────
            var file = new SimpleFile(fileCa, fileIoa, NameOfFile.TRANSPARENT_FILE);

            if (filename != null)
            {
                file.AddSection(File.ReadAllBytes(filename));
            }
            else
            {
                var fileData = new byte[1025];

                for (var i = 0; i < 1025; i++)
                {
                    fileData[i] = (byte)(i + 1);
                }

                file.AddSection(fileData);
            }

            Console.WriteLine("[master] 开始上传 CA={0} IOA={1}（{2} 字节）", fileCa, fileIoa, file.GetFileSize());

            master.SendFile(fileCa, fileIoa, NameOfFile.TRANSPARENT_FILE, file);

            var uploaded = await file.WaitUntilTransferIsCompleteAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Console.WriteLine("[master] 上传结果: " + (uploaded ? "成功" : "失败/超时"));

            // ── 2. 下载（读取从站登记的可下发文件）─────────────────
            const int downloadIoa = 30000;
            var receiver = new ConsoleFileReceiver(4096);

            Console.WriteLine("[master] 开始下载 CA={0} IOA={1}", fileCa, downloadIoa);

            master.GetFile(fileCa, downloadIoa, NameOfFile.TRANSPARENT_FILE, receiver);

            var result = await receiver.WaitUntilFinishedAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Console.WriteLine("[master] 下载结果: {0}，共 {1} 字节", result, receiver.ReceivedBytes);

            master.Stop();
            master.Dispose();

            Console.WriteLine("Press any key to terminate...");
            if (!Console.IsInputRedirected) Console.ReadKey();
        }
    }
}
