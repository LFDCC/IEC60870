// Minimal CS104 client example using the fully async Iec104Client API.
// Connects, starts data transfer, sends an interrogation command and prints
// all received ASDUs (decoded from the zero-copy AsduView).

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;



namespace cs104_client3
{
    class MainClass
    {
        private static Iec104Client _client;

        private static void AsduReceivedHandler(in AsduView view)
        {
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_client.Parameters, raw, 0, raw.Length);

            Console.WriteLine("ASDU: Type=" + asdu.TypeId + " COT=" + asdu.Cot + " CA=" + asdu.Ca + " Elements=" + asdu.NumberOfElements);

            if (asdu.TypeId == TypeID.M_SP_NA_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    SinglePointInformation spi = (SinglePointInformation)asdu.GetElement(i);
                    Console.WriteLine("  IOA=" + spi.ObjectAddress + " SP=" + spi.Value);
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_NB_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    MeasuredValueScaled mvs = (MeasuredValueScaled)asdu.GetElement(i);
                    Console.WriteLine("  IOA=" + mvs.ObjectAddress + " scaled=" + mvs.ScaledValue);
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_TE_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    MeasuredValueScaledWithCP56Time2a mvs = (MeasuredValueScaledWithCP56Time2a)asdu.GetElement(i);
                    Console.WriteLine("  IOA=" + mvs.ObjectAddress + " scaled=" + mvs.ScaledValue + " t=" + mvs.Timestamp);
                }
            }
            else if (asdu.TypeId == TypeID.M_SP_TB_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    SinglePointWithCP56Time2a spi = (SinglePointWithCP56Time2a)asdu.GetElement(i);
                    Console.WriteLine("  IOA=" + spi.ObjectAddress + " SP=" + spi.Value + " t=" + spi.Timestamp);
                }
            }
            else if (asdu.TypeId == TypeID.M_ME_ND_1)
            {
                for (int i = 0; i < asdu.NumberOfElements; i++)
                {
                    MeasuredValueNormalizedWithoutQuality mvn = (MeasuredValueNormalizedWithoutQuality)asdu.GetElement(i);
                    Console.WriteLine("  IOA=" + mvn.ObjectAddress + " norm=" + mvn.NormalizedValue);
                }
            }
        }

        private static void ConnectionHandler(ApduConnectionEvent ev)
        {
            Console.WriteLine("Connection event: " + ev);
        }

        private static async Task SendInterrogation(Iec104Client client, int ca, byte qoi)
        {
            var asdu = new ASDU(client.Parameters, CauseOfTransmission.ACTIVATION, false, false, 0, ca, false);
            asdu.AddInformationObject(new InterrogationCommand(0, qoi));
            await client.SendAsync(asdu);
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

            Console.WriteLine("Sending interrogation command...");
            await SendInterrogation(con, 1, QualifierOfInterrogation.STATION);

            await Task.Delay(2000);

            Console.WriteLine("Closing connection");
            await con.DisconnectAsync();
        }
    }
}
