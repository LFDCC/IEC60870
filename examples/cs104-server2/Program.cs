// This example shows how to send larger numbers of periodic messages using the
// fully async Iec104Server API (TouchSocket-based).

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS104;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;
using IEC60870.Core.Time;



namespace cs104_server2
{
    /// <summary>
    /// This example shows how to handle a large number of information objects.
    /// </summary>
    class MainClass
    {
        static SinglePointInformation[] spiObjects = new SinglePointInformation[400];
        static StepPositionWithCP56Time2a[] stepPositionObjects = new StepPositionWithCP56Time2a[100];

        private static void AsduReceivedHandler(Iec104Session session, in AsduView view)
        {
            // This server only pushes periodic data; just log incoming ASDUs.
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(server.Parameters, raw, 0, raw.Length);
            Console.WriteLine("ASDU received: " + asdu.ToString());
        }

        private static Iec104Server server;

        public static async Task Main(string[] args)
        {
            /* Initialize data objects */
            for (int i = 0; i < 400; i++)
                spiObjects[i] = new SinglePointInformation(1000 + i, true, new QualityDescriptor());

            for (int i = 0; i < 100; i++)
                stepPositionObjects[i] = new StepPositionWithCP56Time2a(10000 + i, 0, false,
                    new QualityDescriptor(), new CP56Time2a());

            bool running = true;

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                running = false;
            };

            server = new Iec104Server();

            server.AsduReceived = AsduReceivedHandler;
            server.ConnectionEvent = (session, ev) =>
            {
                Console.WriteLine("Connection event: " + ev);
            };

            await server.StartAsync(2404);

            // send an initial message (end of initialization)
            ASDU initial = new ASDU(server.Parameters, CauseOfTransmission.INITIALIZED, false, false, 0, 1, false);
            initial.AddInformationObject(new EndOfInitialization(0));
            await server.BroadcastAsync(initial);

            Console.WriteLine("Server started on port 2404. Press Ctrl+C to stop.");

            int waitTime = 2000;

            while (running)
            {
                await Task.Delay(100);

                if (waitTime > 0)
                    waitTime -= 100;
                else
                {
                    /* send SPI objects */
                    ASDU newAsdu = null;

                    for (int i = 0; i < 400; i++)
                    {
                        spiObjects[i].Value = !(spiObjects[i].Value);

                        if (newAsdu == null)
                            newAsdu = new ASDU(server.Parameters, CauseOfTransmission.PERIODIC, false, false, 1, 1, false);

                        if (newAsdu.AddInformationObject(spiObjects[i]) == false)
                        {
                            await server.BroadcastAsync(newAsdu);
                            newAsdu = null;
                            i--;
                        }
                    }

                    if (newAsdu != null)
                        await server.BroadcastAsync(newAsdu);

                    /* send step position objects */
                    newAsdu = null;

                    for (int i = 0; i < 100; i++)
                    {
                        stepPositionObjects[i].Value = (stepPositionObjects[i].Value + 1) % 63;

                        if (newAsdu == null)
                            newAsdu = new ASDU(server.Parameters, CauseOfTransmission.PERIODIC, false, false, 1, 1, false);

                        if (newAsdu.AddInformationObject(stepPositionObjects[i]) == false)
                        {
                            await server.BroadcastAsync(newAsdu);
                            newAsdu = null;
                            i--;
                        }
                    }

                    if (newAsdu != null)
                        await server.BroadcastAsync(newAsdu);

                    waitTime = 1000;
                }
            }

            Console.WriteLine("Stop server");
        }
    }
}
