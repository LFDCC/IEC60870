// NOTE: ported from CS104 file upload client to CS101 because the new library has no CS104 file transfer.
// Client example to demonstrate file upload to a CS101 slave file server over TCP.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101.File;
using IEC60870.Core.File;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.CS101.LinkLayer;



namespace cs104_client_file_upload
{
    public class SimpleFile : TransparentFile
    {
        private AutoResetEvent ready = new AutoResetEvent(false);

        public SimpleFile(int ca, int ioa, NameOfFile nof)
            : base(ca, ioa, nof)
        {
        }

        public override void TransferComplete(bool success)
        {
            Console.WriteLine("Transfer complete: " + success.ToString());
            ready.Set();
        }

        public void WaitUntilTransferIsComplete()
        {
            ready.WaitOne();
        }
    }

    class MainClass
    {
        public static async Task Main(string[] args)
        {
            string hostname = "127.0.0.1";
            string? filename = null;
            int fileCa = 1;
            int fileIoa = 30001;

            if (args.Length >= 1)
                hostname = args[0];
            if (args.Length >= 2)
                filename = args[1];
            if (args.Length >= 3)
                Int32.TryParse(args[2], out fileCa);
            if (args.Length >= 4)
                Int32.TryParse(args[3], out fileIoa);

            Console.WriteLine("Using IEC60870.Core.NET version " + typeof(ASDU).Assembly.GetName().Version.ToString());

            // CS101 master file client connecting to the slave over TCP (balanced link layer).
            var master = new Iec101Client(hostname, 2404, LinkLayerMode.BALANCED);

            var cts = new CancellationTokenSource();

            // Start the background async loop (fire-and-forget).
            _ = master.StartAsync(cts.Token);

            // Give the link layer time to connect and establish the balanced link.
            await Task.Delay(500).ConfigureAwait(false);

            SimpleFile file = new SimpleFile(fileCa, fileIoa, NameOfFile.TRANSPARENT_FILE);

            if (filename != null)
            {
                file.AddSection(File.ReadAllBytes(filename));
            }
            else
            {
                byte[] fileData = new byte[1025];

                for (int i = 0; i < 1025; i++)
                    fileData[i] = (byte)(i + 1);

                file.AddSection(fileData);
            }

            master.SendFile(fileCa, fileIoa, NameOfFile.TRANSPARENT_FILE, file);

            // Block until the slave acknowledged the file transfer.
            await Task.Run(() => file.WaitUntilTransferIsComplete()).ConfigureAwait(false);

            master.Stop();

            Console.WriteLine("Press any key to terminate...");
            Console.ReadKey();
        }
    }
}
