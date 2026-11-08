/*
 *  CS101 从站文件服务示例（FT1.2 over TCP 隧道，平衡链路）。
 *
 *  同时演示两个方向：
 *    1. 文件下发（从站 → 主站）：用 GetAvailableFiles().AddFile(...) 注册可下发文件，
 *       主站发起 SELECT/CALL 即可逐段拉取；
 *    2. 文件上传（主站 → 从站）：用 SetFileReadyHandler(...) 接受主站的 FILE READY，
 *       返回的 IFileReceiver 会逐段收到上传数据，收完后写入 file_30001.dat。
 *
 *  注意：文件传输必须两端同为 BALANCED —— 从站要主动推帧（调文件、段就绪、
 *  分段数据），非平衡模式下的从站只会回 ACK 等主站召唤 1 级数据，而文件主站不会召唤，
 *  双方会互等。
 *
 *  运行：先启动本示例，再运行 examples/101/cs101-master-file。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101;
using IEC60870.Core;
using TouchSocket.Core;

namespace cs101_slave_file
{
    /// <summary>
    /// 可下发文件：继承 TransparentFile 并实现 TransferComplete 即可（也可自行实现 IFileProvider）。
    /// </summary>
    public class SimpleFile : TransparentFile
    {
        public SimpleFile(int ca, int ioa, NameOfFile nof)
            : base(ca, ioa, nof)
        {
        }

        public override void TransferComplete(bool success)
        {
            Console.WriteLine("[slave] 文件下发结束: " + success.ToString());
        }
    }

    /// <summary>
    /// Implement IFileReceiver to allow file uploads from the master
    /// </summary>
    /// <summary>
    /// 接收主站上传的文件：逐段收齐后落盘。
    /// </summary>
    public class MyReceiver : IFileReceiver
    {
        public byte[] recvBuffer;
        public int recvdBytes = 0;

        public MyReceiver(int bufferSize)
        {
            recvBuffer = new byte[bufferSize];
        }

        public void Finished(FileErrorCode result)
        {
            Console.WriteLine("[slave] 文件上传结束: " + result.ToString());

            // 收完后有效数据都在 recvBuffer 里，可按业务需要落盘/入库
            if (result == FileErrorCode.SUCCESS)
            {
                var path = Path.GetFullPath("file_30001.dat");
                File.WriteAllBytes(path, recvBuffer);
                Console.WriteLine("[slave] 已写入 " + path + " (" + recvBuffer.Length + " 字节)");
            }
        }

        public void SegmentReceived(byte sectionName, int offset, int size, byte[] data)
        {
            Array.Copy(data, 0, recvBuffer, recvdBytes, size);
            recvdBytes += size;
            Console.WriteLine("[slave] 收到分段 NoS={0} offset={1} size={2}（累计 {3} 字节）",
                sectionName, offset, size, recvdBytes);
        }
    }

    class MainClass
    {
        private static bool InterrogationHandler(object parameter, IClientConnection connection, ASDU asdu, byte qoi)
        {
            Console.WriteLine("Interrogation for group " + qoi);

            ApplicationLayerParameters cp = connection.GetApplicationLayerParameters();

            connection.SendACT_CON(asdu, false);

            ASDU newAsdu = new ASDU(cp, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);

            newAsdu.AddInformationObject(new MeasuredValueScaled(100, -1, new QualityDescriptor()));

            connection.SendASDU(newAsdu);

            connection.SendACT_TERM(asdu);

            return true;
        }

        public static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // CS101 从站文件服务：TCP 端口 2404 上的 FT1.2 隧道
            var slave = new Iec101Server(2404);

            // 必须与主站同模式：文件传输在平衡链路上由从站主动推帧（调文件、段就绪、分段数据），
            // 非平衡模式下从站只回 ACK、等主站召唤 1 级数据，而文件主站不会召唤 —— 双方会互等。
            slave.LinkLayerMode = LinkLayerMode.BALANCED;

            var logger = new LoggerGroup();
            logger.AddConsoleLogger(LogLevel.Debug);
            slave.Logger = logger;

            slave.SetInterrogationHandler(InterrogationHandler, null);

            // 注册"接受文件上传"的回调：主站发来 FILE READY 声明要上传时调用，
            // 返回的 IFileReceiver 负责逐段接收。返回 null 表示拒收。
            slave.SetFileReadyHandler((parameter, ca, ioa, nof, lengthOfFile) =>
            {
                if ((ca == 1) && (ioa == 30001) && (nof == NameOfFile.TRANSPARENT_FILE))
                {
                    // 限制最大 5000 字节
                    if (lengthOfFile > 5000)
                    {
                        Console.WriteLine("[slave] 拒收：文件过大");
                        return null;
                    }
                    else
                    {
                        Console.WriteLine("[slave] 接受文件上传（" + lengthOfFile + " 字节）");
                        return new MyReceiver(lengthOfFile);
                    }
                }
                else
                {
                    Console.WriteLine("[slave] 拒收：未知的文件类型/地址");
                    return null;
                }
            }, null);

            // 注册"可下发"的文件（主站可 SELECT/CALL 后逐段拉取）
            SimpleFile file = new SimpleFile(1, 30000, NameOfFile.TRANSPARENT_FILE);

            var fileData = new byte[1025];

            for (var i = 0; i < 1025; i++)
            {
                fileData[i] = (byte)(i + 1);
            }

            file.AddSection(fileData);

            SimpleFile file2 = new SimpleFile(1, 30001, NameOfFile.TRANSPARENT_FILE);
            file2.AddSection(fileData);

            slave.GetAvailableFiles().AddFile(file);
            slave.GetAvailableFiles().AddFile(file2);

            // 启动后台收发循环
            var loop = slave.StartAsync(cts.Token);

            ASDU newAsdu = new ASDU(slave.Parameters, CauseOfTransmission.INITIALIZED, false, false, 0, 1, false);
            EndOfInitialization eoi = new EndOfInitialization(0);
            newAsdu.AddInformationObject(eoi);
            slave.EnqueueUserDataClass1(newAsdu);

            Console.WriteLine("CS101 从站文件服务已启动（TCP 2404）。Ctrl+C 停止。");
            Console.WriteLine("  可下发文件: CA=1 IOA=30000 / 30001");
            Console.WriteLine("  可接受上传: CA=1 IOA=30001（最大 5000 字节）");

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            slave.Stop();
            slave.Dispose();
            Console.WriteLine("Stop server");
        }
    }
}
