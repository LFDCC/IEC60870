// NOTE: ported from CS104 file server to CS101 because the new library has no CS104 file transfer.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101.File;
using IEC60870.CS101;
using IEC60870.Core;
using IEC60870.Core.File;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;



namespace cs104_server_file
{
    /// <summary>
    /// Extend TransparentFile or implement IFileProvider to allow file downloads to the master
    /// </summary>
    public class SimpleFile : TransparentFile
    {
        public SimpleFile(int ca, int ioa, NameOfFile nof)
            : base(ca, ioa, nof)
        {
        }

        public override void TransferComplete(bool success)
        {
            Console.WriteLine("Transfer complete: " + success.ToString());
        }
    }

    /// <summary>
    /// Implement IFileReceiver to allow file uploads from the master
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
            Console.WriteLine("File download finished - code: " + result.ToString());

            // now the valid file data is in the buffer. User can now handle the file data
            // (e.g. store data in local file system)
            if (result == FileErrorCode.SUCCESS)
            {
                File.WriteAllBytes("file_30001.dat", recvBuffer);
            }
        }

        public void SegmentReceived(byte sectionName, int offset, int size, byte[] data)
        {
            Array.Copy(data, 0, recvBuffer, recvdBytes, size);
            recvdBytes += size;
            Console.WriteLine("File segment - sectionName: {0} offset: {1} size: {2}", sectionName, offset, size);
            for (int i = 0; i < size; i++)
            {
                Console.Write(" " + data[i]);
            }
            Console.WriteLine();
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

            // CS101 slave file server listening on a TCP port.
            var slave = new Iec101Server(2404);

            slave.DebugOutput = true;

            slave.SetInterrogationHandler(InterrogationHandler, null);

            // Install a handler to allow file downloads (will be called when the master sends a
            // file ready ASDU to announce a file transfer). Returns a IFileReceiver that receives
            // the uploaded file data.
            slave.SetFileReadyHandler((parameter, ca, ioa, nof, lengthOfFile) =>
            {
                if ((ca == 1) && (ioa == 30001) && (nof == NameOfFile.TRANSPARENT_FILE))
                {
                    // Allow only files with a maximum of 5000 bytes
                    if (lengthOfFile > 5000)
                    {
                        Console.WriteLine("Deny file download. File too large");
                        return null;
                    }
                    else
                    {
                        Console.WriteLine("Accept file download.");
                        return new MyReceiver(lengthOfFile);
                    }
                }
                else
                {
                    Console.WriteLine("Deny file upload. Unknown file type.");
                    return null;
                }
            }, null);

            // Register downloadable files with the auto-created FileServer.
            SimpleFile file = new SimpleFile(1, 30000, NameOfFile.TRANSPARENT_FILE);

            byte[] fileData = new byte[1025];

            for (int i = 0; i < 1025; i++)
                fileData[i] = (byte)(i + 1);

            file.AddSection(fileData);

            SimpleFile file2 = new SimpleFile(1, 30001, NameOfFile.TRANSPARENT_FILE);
            file2.AddSection(fileData);

            slave.GetAvailableFiles().AddFile(file);
            slave.GetAvailableFiles().AddFile(file2);

            // Start the background async loop (fire-and-forget).
            var loop = slave.StartAsync(cts.Token);

            ASDU newAsdu = new ASDU(slave.Parameters, CauseOfTransmission.INITIALIZED, false, false, 0, 1, false);
            EndOfInitialization eoi = new EndOfInitialization(0);
            newAsdu.AddInformationObject(eoi);
            slave.EnqueueUserDataClass1(newAsdu);

            Console.WriteLine("CS101 file server running on TCP port 2404. Press Ctrl+C to stop.");

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            slave.Stop();
            Console.WriteLine("Stop server");
        }
    }
}
