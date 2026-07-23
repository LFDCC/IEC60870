// This example sends a large number of interrogation commands (GIs) and counts
// the received ACTIVATION_CON / ACTIVATION_TERMINATION confirmations, using the
// fully async Iec104Client API.

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;



namespace cs104_client2
{
    class MainClass
    {
        static int interrogationTerminationReceived = 0;
        static int interrogationConfirmationReceived = 0;

        private static ApplicationLayerParameters _al;

        private static void AsduReceivedHandler(in AsduView view)
        {
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_al, raw, 0, raw.Length);

            Console.WriteLine(asdu.ToString());

            if (asdu.TypeId == TypeID.C_IC_NA_1)
            {
                if (asdu.Cot == CauseOfTransmission.ACTIVATION_CON)
                {
                    Console.WriteLine((asdu.IsNegative ? "Negative" : "Positive") + "confirmation for interrogation command");
                    Interlocked.Increment(ref interrogationConfirmationReceived);
                }
                else if (asdu.Cot == CauseOfTransmission.ACTIVATION_TERMINATION)
                {
                    Console.WriteLine("Interrogation command terminated");
                    Interlocked.Increment(ref interrogationTerminationReceived);
                }
            }
            else
            {
                Console.WriteLine("Unknown message type!");
            }

            Console.WriteLine("interrogationConfirmationReceived: " + interrogationConfirmationReceived);
            Console.WriteLine("interrogationTerminationReceived:  " + interrogationTerminationReceived);
        }

        private static void ConnectionHandler(ApduConnectionEvent ev)
        {
            Console.WriteLine("Connection event: " + ev);
        }

        private static async Task SendInterrogation(Iec104Client c, int ca, byte qoi)
        {
            var asdu = new ASDU(c.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(new InterrogationCommand(0, qoi));
            await c.SendAsync(asdu);
        }

        public static async Task Main(string[] args)
        {
            Console.WriteLine("Using IEC60870.Core.NET version " + typeof(ASDU).Assembly.GetName().Version.ToString());

            Iec104Client con = new Iec104Client("127.0.0.1", 2404);
            _al = con.Parameters;

            con.AsduReceived += AsduReceivedHandler;
            con.ConnectionEvent += ConnectionHandler;

            await con.ConnectAsync();
            await con.StartDataTransferAsync();

            int loopRuns = 6000;

            for (int i = 0; i < loopRuns; i++)
            {
                Console.WriteLine("Send GI " + i);
                await SendInterrogation(con, 1, QualifierOfInterrogation.STATION);
            }

            while (interrogationTerminationReceived < loopRuns)
                await Task.Delay(100);

            Console.WriteLine("interrogationTerminationReceived: " + interrogationTerminationReceived);

            await con.DisconnectAsync();
        }
    }
}
