using System;
using System.IO.Ports;


using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101.LinkLayer;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.Core.InformationObjects;

namespace cs101_master_tcp
{
    class MainClass
    {

        private static bool rcvdRawMessageHandler (object parameter, byte[] message, int messageSize)
        {
            Console.WriteLine ("RECV " + BitConverter.ToString (message, 0, messageSize));

            return true;
        }

        private static void linkLayerStateChanged (object parameter, int address, LinkLayerState newState)
        {
            Console.WriteLine ("LL state event: " + newState.ToString ());
        }

        private static bool asduReceivedHandler(object parameter, int address, ASDU asdu)
        {
            Console.WriteLine (asdu.ToString ());

            return true;
        }


        public static async Task Main (string[] args)
        {
            bool running = true;

            // use Ctrl-C to stop the programm
            Console.CancelKeyPress += delegate(object? sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                running = false;
            };

            string hostname = "127.0.0.1";
            int tcpPort = 2404;

            if (args.Length > 0)
                hostname = args [0];

            if (args.Length > 1)
                int.TryParse (args [1], out tcpPort);

            // NOTE: new API uses a TouchSocket TCP tunnel ctor (hostname, port, mode) instead of
            // the old TcpClientVirtualSerialPort wrapper.
            LinkLayerParameters llParameters = new LinkLayerParameters();
            llParameters.AddressLength = 1;
            llParameters.UseSingleCharACK = true;

            Iec101Client master = new Iec101Client (hostname, tcpPort, LinkLayerMode.BALANCED, llParameters);
            master.DebugOutput = false;
            master.OwnAddress = 1;
            master.SlaveAddress = 3;
            master.SetASDUReceivedHandler (asduReceivedHandler, null);
            master.SetLinkLayerStateChangedHandler (linkLayerStateChanged, null);
            master.SetReceivedRawMessageHandler (rcvdRawMessageHandler, null);

            var cts = new CancellationTokenSource ();
            var loop = master.StartAsync (cts.Token);

            long lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

            while (running) {

                if ((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastTimestamp) >= 5000) {

                    lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

                    if (master.GetLinkLayerState () == LinkLayerState.AVAILABLE) {
                        master.SendInterrogationCommand (CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);
                    } else {
                        Console.WriteLine ("Link layer: " + master.GetLinkLayerState ().ToString ());
                    }
                }

                Thread.Sleep (100);
            }

            master.StopAsync ();
            await loop;
        }
    }
}
