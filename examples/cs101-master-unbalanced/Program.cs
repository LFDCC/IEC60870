using System;
using System.IO.Ports;


using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS101.LinkLayer;
using IEC60870.Core.InformationObjects;
using IEC60870.CS101;

namespace cs101_master_unbalanced
{
    class MainClass
    {
        private static bool asduReceivedHandler(object parameter, int slaveAddress, ASDU asdu)
        {
            Console.WriteLine ("Slave: {0} - {1}", slaveAddress, asdu.ToString ());

            if (asdu.TypeId == TypeID.M_SP_NA_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {

                    var val = (SinglePointInformation)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + val.ObjectAddress + " SP value: " + val.Value);
                    Console.WriteLine ("   " + val.Quality.ToString ());
                }
            } 
            else if (asdu.TypeId == TypeID.M_ME_TE_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {

                    var msv = (MeasuredValueScaledWithCP56Time2a)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + msv.ObjectAddress + " scaled value: " + msv.ScaledValue);
                    Console.WriteLine ("   " + msv.Quality.ToString ());
                    Console.WriteLine ("   " + msv.Timestamp.ToString ());
                }

            } else if (asdu.TypeId == TypeID.M_ME_TF_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {
                    var mfv = (MeasuredValueShortWithCP56Time2a)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + mfv.ObjectAddress + " float value: " + mfv.Value);
                    Console.WriteLine ("   " + mfv.Quality.ToString ());
                    Console.WriteLine ("   " + mfv.Timestamp.ToString ());
                    Console.WriteLine ("   " + mfv.Timestamp.GetDateTime ().ToString ());
                }
            } else if (asdu.TypeId == TypeID.M_SP_TB_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {

                    var val = (SinglePointWithCP56Time2a)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + val.ObjectAddress + " SP value: " + val.Value);
                    Console.WriteLine ("   " + val.Quality.ToString ());
                    Console.WriteLine ("   " + val.Timestamp.ToString ());
                }
            } else if (asdu.TypeId == TypeID.M_ME_NC_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {
                    var mfv = (MeasuredValueShort)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + mfv.ObjectAddress + " float value: " + mfv.Value);
                    Console.WriteLine ("   " + mfv.Quality.ToString ());
                }
            } else if (asdu.TypeId == TypeID.M_ME_NB_1) {

                for (int i = 0; i < asdu.NumberOfElements; i++) {

                    var msv = (MeasuredValueScaled)asdu.GetElement (i);

                    Console.WriteLine ("  IOA: " + msv.ObjectAddress + " scaled value: " + msv.ScaledValue);
                    Console.WriteLine ("   " + msv.Quality.ToString ());
                }

            }

            return true;
        }

        private static void linkLayerStateChanged (object parameter, int address, LinkLayerState newState)
        {
            Console.WriteLine ("LL state event {0} for slave {1}", newState.ToString (), address);
        }

        public static async Task Main (string[] args)
        {
            bool running = true;

            // use Ctrl-C to stop the programm
            Console.CancelKeyPress += delegate(object? sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                running = false;
            };

            string portName = "COM1";

            if (args.Length > 0)
                portName = args [0];

            SerialPort port = new SerialPort (portName, 9600, Parity.None, 8, StopBits.One);

            /* set link layer address length */
            LinkLayerParameters llParameters = new LinkLayerParameters ();
            llParameters.AddressLength = 1;

            /* unbalanced mode allows multiple slaves on a single serial line */
            Iec101Client master = new Iec101Client(port, LinkLayerMode.UNBALANCED, llParameters);
            master.DebugOutput = false;
            master.SetASDUReceivedHandler (asduReceivedHandler, null);
            master.SetLinkLayerStateChangedHandler (linkLayerStateChanged, null);

            master.AddSlave (1);
            master.AddSlave (2);
            master.AddSlave (3);

            var cts = new CancellationTokenSource ();
            var loop = master.StartAsync (cts.Token);

            long lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

            while (running) {

                // NOTE: the async loop drives RunAsync(); we only trigger the polls here.
                master.PollSingleSlave(1);
                master.PollSingleSlave(2);
                master.PollSingleSlave(3);

                if ((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastTimestamp) >= 20000) {

                    lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

                    try {
                        master.SlaveAddress = 1;
                        master.SendInterrogationCommand (CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);
                    }
                    catch (LinkLayerBusyException) {
                        Console.WriteLine ("Slave 1: Link layer busy or not ready");
                    }

                    try {
                        master.SlaveAddress = 2;
                        master.SendInterrogationCommand (CauseOfTransmission.ACTIVATION, 2, QualifierOfInterrogation.STATION);
                    }
                    catch (LinkLayerBusyException) {
                        Console.WriteLine ("Slave 2: Link layer busy or not ready");
                    }
                        
                    try {
                        master.SlaveAddress = 3;
                        master.SendInterrogationCommand (CauseOfTransmission.ACTIVATION, 3, QualifierOfInterrogation.STATION);
                    }
                    catch (LinkLayerBusyException) {
                        Console.WriteLine ("Slave 3: Link layer busy or not ready");
                    }
                }
                    
                Thread.Sleep (100);
            }

            master.StopAsync ();
            await loop;
        }
    }
}
