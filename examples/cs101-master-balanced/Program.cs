using System;
using System.IO.Ports;


using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101.LinkLayer;
using IEC60870.Core;
using IEC60870.CS101;
using IEC60870.Core.InformationObjects;

namespace cs101_master_balanced
{
    class MainClass
    {
        private static void linkLayerStateChanged (object parameter, int address, LinkLayerState newState)
        {
            Console.WriteLine ("LL state event: " + newState.ToString ());
        }

        private static bool asduReceivedHandler (object parameter, int address, ASDU asdu)
        {
            Console.WriteLine (asdu.ToString ());

            return true;
        }

        public static async Task Main (string [] args)
        {
            bool running = true;

            // use Ctrl-C to stop the programm
            Console.CancelKeyPress += delegate (object? sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                running = false;
            };

            string portName = "COM1";

            if (args.Length > 0)
                portName = args [0];

            // Setup serial port (NOTE: StartAsync opens it; must compile without hardware attached)
            SerialPort port = new SerialPort (portName, 9600, Parity.None, 8, StopBits.One);

            // Setup balanced CS101 master
            LinkLayerParameters llParameters = new LinkLayerParameters ();
            llParameters.AddressLength = 1;
            llParameters.UseSingleCharACK = false;

            Iec101Client master = new Iec101Client (port, LinkLayerMode.BALANCED, llParameters);
            master.DebugOutput = false;
            master.OwnAddress = 3;
            master.SlaveAddress = 2;
            master.SetASDUReceivedHandler (asduReceivedHandler, null);
            master.SetLinkLayerStateChangedHandler (linkLayerStateChanged, null);
            master.SetReceivedRawMessageHandler ((object parameter, byte [] message, int messageSize) => {
                Console.WriteLine ("RECV " + BitConverter.ToString (message, 0, messageSize));
                return true;
            }, null);

            master.SetSentRawMessageHandler ((object parameter, byte [] message, int messageSize) => {
                Console.WriteLine ("SEND " + BitConverter.ToString (message, 0, messageSize));
                return true;
            }, null);

            // Start the async background loop (fire-and-forget, loops until cancelled)
            var cts = new CancellationTokenSource ();
            var loop = master.StartAsync (cts.Token);

            long lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

            while (running) {

                if ((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds () - lastTimestamp) >= 5000) {

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
