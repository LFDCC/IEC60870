using System;
using System.IO.Ports;


using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;
using IEC60870.CS101.LinkLayer;
using IEC60870.CS101.File;
using IEC60870.Core.File;

namespace cs101_slave_balanced
{
    public class CS101TestSlave {

        private static bool myInterrogationHandler(object parameter, IClientConnection connection, ASDU asdu, byte qoi)
        {
            Console.WriteLine("Interrogation for group " + qoi);

            connection.SendACT_CON (asdu, false);

            // send information objects
            ASDU newAsdu = new ASDU(connection.GetApplicationLayerParameters(), CauseOfTransmission.INTERROGATED_BY_STATION, 
                false, false, 2, 1, false);

            newAsdu.AddInformationObject (new MeasuredValueScaled (100, -1, new QualityDescriptor ()));

            newAsdu.AddInformationObject (new MeasuredValueScaled (101, 23, new QualityDescriptor ()));

            newAsdu.AddInformationObject (new MeasuredValueScaled (102, 2300, new QualityDescriptor ()));

            connection.SendASDU (newAsdu);

            // send sequence of information objects
            newAsdu = new ASDU (connection.GetApplicationLayerParameters(), CauseOfTransmission.INTERROGATED_BY_STATION, 
                false, false, 2, 1, true);

            newAsdu.AddInformationObject (new SinglePointInformation (200, true, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (201, false, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (202, true, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (203, false, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (204, true, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (205, false, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (206, true, new QualityDescriptor ()));
            newAsdu.AddInformationObject (new SinglePointInformation (207, false, new QualityDescriptor ()));

            connection.SendASDU (newAsdu);

            connection.SendACT_TERM (asdu);

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

            string portName = "COM2"; // e.g. "COM3" on windows

            if (args.Length > 0)
                portName = args [0];

            SerialPort port = new SerialPort (portName, 9600, Parity.None, 8, StopBits.One);

            LinkLayerParameters llParameters = new LinkLayerParameters ();
            llParameters.AddressLength = 1;
            llParameters.TimeoutForACK = 500;
            llParameters.UseSingleCharACK = false;

            Iec101Server slave = new Iec101Server (port, llParameters);
            slave.DebugOutput = true;
            slave.LinkLayerAddress = 2;
            slave.LinkLayerAddressOtherStation = 1;
            slave.LinkLayerMode = LinkLayerMode.BALANCED;

            slave.SetInterrogationHandler (myInterrogationHandler, null);

            slave.SetUserDataQueueSizes (50, 20);

            ASDU asdu = new ASDU (slave.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
            asdu.AddInformationObject (new StepPositionInformation (301, 1, false, new QualityDescriptor()));
            slave.EnqueueUserDataClass1 (asdu);

            long lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
            Int16 measuredValue = 0;

            TransparentFile file = new TransparentFile (1, 30000, NameOfFile.TRANSPARENT_FILE);

            byte[] fileData = new byte[1025];

            for (int i = 0; i < 1025; i++)
                fileData [i] = (byte)(i + 1);

            file.AddSection (fileData);

            slave.GetAvailableFiles().AddFile (file);

            // Start the async background loop (fire-and-forget, loops until cancelled)
            var cts = new CancellationTokenSource ();
            var loop = slave.StartAsync (cts.Token);

            while (running) {

                if ((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastTimestamp) >= 5000) {

                    lastTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

                    ASDU newAsdu = new ASDU (slave.Parameters, CauseOfTransmission.PERIODIC, false, false, 0, 1, false);
                    newAsdu.AddInformationObject (new MeasuredValueScaled (110, measuredValue, new QualityDescriptor ()));
                    slave.EnqueueUserDataClass2 (newAsdu);

                    measuredValue++;
                }

                if (Console.KeyAvailable) {

                    ConsoleKeyInfo keyInfo = Console.ReadKey ();

                    if (keyInfo.KeyChar == 't') {
                        slave.SendLinkLayerTestFunction ();
                    } 
                    else {
                        Console.WriteLine ("Send spontaneous message");

                        bool value = false;

                        if (keyInfo.KeyChar == 's') {
                            value = true;
                        }

                        ASDU newAsdu = new ASDU (slave.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
                        newAsdu.AddInformationObject (new SinglePointInformation (100, value, new QualityDescriptor ()));

                        slave.EnqueueUserDataClass1 (newAsdu);
                    }
                }

                Thread.Sleep(1);
            }

            slave.Stop ();
            await loop;
        }
    }
}
