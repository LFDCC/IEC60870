// This example shows a CS104 client that connects, sends a test command,
// an interrogation command, control commands and a clock sync command using
// the fully async Iec104Client API. All received ASDUs are decoded from the
// zero-copy AsduView.
//
// NOTE: The old example used GetDirectory/GetFile (file transfer). The new CS104
// API has NO file-transfer support (it only exists for CS101), so those calls
// are intentionally omitted here.

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Time;



namespace cs104_client1
{
    class MainClass
    {
        private static Iec104Client _client;

        private static void AsduReceivedHandler(in AsduView view)
        {
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_client.Parameters, raw, 0, raw.Length);

            Console.WriteLine(asdu.ToString());

            if (asdu.TypeId == TypeID.M_SP_NA_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var val = (SinglePointInformation)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + val.ObjectAddress + " SP value: " + val.Value);
                    Console.WriteLine("   " + val.Quality.ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_TE_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var msv = (MeasuredValueScaledWithCP56Time2a)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + msv.ObjectAddress + " scaled value: " + msv.ScaledValue);
                    Console.WriteLine("   " + msv.Quality.ToString());
                    Console.WriteLine("   " + msv.Timestamp.ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_TF_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var mfv = (MeasuredValueShortWithCP56Time2a)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + mfv.ObjectAddress + " float value: " + mfv.Value);
                    Console.WriteLine("   " + mfv.Quality.ToString());
                    Console.WriteLine("   " + mfv.Timestamp.ToString());
                    Console.WriteLine("   " + mfv.Timestamp.GetDateTime().ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_SP_TB_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var val = (SinglePointWithCP56Time2a)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + val.ObjectAddress + " SP value: " + val.Value);
                    Console.WriteLine("   " + val.Quality.ToString());
                    Console.WriteLine("   " + val.Timestamp.ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_NC_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var mfv = (MeasuredValueShort)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + mfv.ObjectAddress + " float value: " + mfv.Value);
                    Console.WriteLine("   " + mfv.Quality.ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_NB_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var msv = (MeasuredValueScaled)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + msv.ObjectAddress + " scaled value: " + msv.ScaledValue);
                    Console.WriteLine("   " + msv.Quality.ToString());
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_ND_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    var msv = (MeasuredValueNormalizedWithoutQuality)asdu.GetElement(i);
                    Console.WriteLine("  IOA: " + msv.ObjectAddress + " scaled value: " + msv.NormalizedValue);
                }
            }
            else if (asdu.TypeId == TypeID.C_IC_NA_1)
            {
                if (asdu.Cot == CauseOfTransmission.ACTIVATION_CON)
                    Console.WriteLine((asdu.IsNegative ? "Negative" : "Positive") + "confirmation for interrogation command");
                else if (asdu.Cot == CauseOfTransmission.ACTIVATION_TERMINATION)
                    Console.WriteLine("Interrogation command terminated");
            }
            else
            {
                Console.WriteLine("Unknown message type!");
            }
        }

        private static void ConnectionHandler(ApduConnectionEvent ev)
        {
            Console.WriteLine("Connection event: " + ev);
        }

        private static async Task SendTestCommand(Iec104Client c, int ca)
        {
            var asdu = new ASDU(c.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(new TestCommand());
            await c.SendAsync(asdu);
        }

        private static async Task SendInterrogation(Iec104Client c, int ca, byte qoi)
        {
            var asdu = new ASDU(c.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(new InterrogationCommand(0, qoi));
            await c.SendAsync(asdu);
        }

        private static async Task SendControlCommand(Iec104Client c, int ca, InformationObject io)
        {
            var asdu = new ASDU(c.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(io);
            await c.SendAsync(asdu);
        }

        private static async Task SendClockSync(Iec104Client c, int ca, CP56Time2a time)
        {
            var asdu = new ASDU(c.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(new ClockSynchronizationCommand(0, time));
            await c.SendAsync(asdu);
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine("Using IEC60870.Core.NET version " + LibraryCommon.GetLibraryVersionString());

            Iec104Client con = new Iec104Client("127.0.0.1", 2404);
            _client = con;

            con.AsduReceived += AsduReceivedHandler;
            con.ConnectionEvent += ConnectionHandler;

            await con.ConnectAsync();
            await con.StartDataTransferAsync();

            // NOTE: file transfer (GetDirectory/GetFile) is not available in the new CS104
            // API; it only exists for CS101. Skipping it here.

            await SendTestCommand(con, 1);

            await SendInterrogation(con, 1, QualifierOfInterrogation.STATION);

            await Task.Delay(5000);

            await SendControlCommand(con, 1, new SingleCommand(5000, true, false, 0));
            await SendControlCommand(con, 1, new DoubleCommand(5001, DoubleCommand.ON, false, 0));
            await SendControlCommand(con, 1, new StepCommand(5002, StepCommandValue.HIGHER, false, 0));
            await SendControlCommand(con, 1, new SingleCommandWithCP56Time2a(5000, false, false, 0, new CP56Time2a(DateTime.Now)));

            await SendClockSync(con, 1, new CP56Time2a(DateTime.Now));

            Console.WriteLine("CLOSE");
            await con.DisconnectAsync();

            Console.WriteLine("RECONNECT");
            await con.ConnectAsync();
            await con.StartDataTransferAsync();

            await Task.Delay(5000);

            Console.WriteLine("CLOSE 2");
            await con.DisconnectAsync();

            Console.WriteLine("Press any key to terminate...");
            Console.ReadKey();
        }
    }
}
