// TLS secured CS104 server using the fully async Iec104Server API and
// TouchSocket's ServiceSslOption. Listens on port 19998.
//
// NOTE: The old TlsSecurityInformation (AddAllowedCertificate/AddCA/
// ChainValidation/AllowOnlySpecificCertificates) is replaced by ServiceSslOption.
// Per-certificate acceptance is handled by CertificateValidationCallback = true.
// The example certs (server.pfx, client1.cer, root.cer) live in this folder.

using System;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Sockets;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;
using IEC60870.Core.Time;



namespace cs104_tls_server
{
    class MainClass
    {
        private static Iec104Server _server;

        private static ASDU BuildActCon(ASDU req, ApplicationLayerParameters al)
        {
            ASDU con = new ASDU(al, CauseOfTransmission.ACTIVATION_CON, false, false, req.Oa, req.Ca, false);
            con.AddInformationObject(req.GetElement(0));
            return con;
        }

        private static ASDU BuildActTerm(ASDU req, ApplicationLayerParameters al)
        {
            ASDU term = new ASDU(al, CauseOfTransmission.ACTIVATION_TERMINATION, false, false, req.Oa, req.Ca, false);
            term.AddInformationObject(req.GetElement(0));
            return term;
        }

        private static void HandleInterrogation(Iec104Session session, ASDU asdu)
        {
            ApplicationLayerParameters al = _server.Parameters;

            _ = Task.Run(async () =>
            {
                try
                {
                    await session.SendAsync(BuildActCon(asdu, al));

                    ASDU newAsdu = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);
                    newAsdu.AddInformationObject(new MeasuredValueScaled(100, -1, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new MeasuredValueScaled(101, 23, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new MeasuredValueScaled(102, 2300, new QualityDescriptor()));
                    await session.SendAsync(newAsdu);

                    newAsdu = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 3, 1, false);
                    newAsdu.AddInformationObject(new MeasuredValueScaledWithCP56Time2a(103, 3456, new QualityDescriptor(), new CP56Time2a(DateTime.Now)));
                    await session.SendAsync(newAsdu);

                    newAsdu = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, false);
                    newAsdu.AddInformationObject(new SinglePointWithCP56Time2a(104, true, new QualityDescriptor(), new CP56Time2a(DateTime.Now)));
                    await session.SendAsync(newAsdu);

                    newAsdu = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, true);
                    newAsdu.AddInformationObject(new SinglePointInformation(200, true, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(201, false, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(202, true, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(203, false, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(204, true, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(205, false, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(206, true, new QualityDescriptor()));
                    newAsdu.AddInformationObject(new SinglePointInformation(207, false, new QualityDescriptor()));
                    await session.SendAsync(newAsdu);

                    newAsdu = new ASDU(al, CauseOfTransmission.INTERROGATED_BY_STATION, false, false, 2, 1, true);
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(300, -1.0f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(301, -0.5f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(302, -0.1f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(303, .0f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(304, 0.1f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(305, 0.2f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(306, 0.5f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(307, 0.7f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(308, 0.99f));
                    newAsdu.AddInformationObject(new MeasuredValueNormalizedWithoutQuality(309, 1f));
                    await session.SendAsync(newAsdu);

                    await session.SendAsync(BuildActTerm(asdu, al));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Interrogation response error: " + ex.Message);
                }
            });
        }

        private static void HandleAsdu(Iec104Session session, in AsduView view)
        {
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_server.Parameters, raw, 0, raw.Length);

            if (asdu.TypeId == TypeID.C_IC_NA_1)
            {
                HandleInterrogation(session, asdu);
            }
            else if (asdu.TypeId == TypeID.C_SC_NA_1)
            {
                Console.WriteLine("Single command");
                SingleCommand sc = (SingleCommand)asdu.GetElement(0);
                Console.WriteLine(sc.ToString());

                _ = Task.Run(async () =>
                {
                    try { await session.SendAsync(BuildActCon(asdu, _server.Parameters)); }
                    catch (Exception ex) { Console.WriteLine("Command response error: " + ex.Message); }
                });
            }
            else if (asdu.TypeId == TypeID.C_CS_NA_1)
            {
                ClockSynchronizationCommand qsc = (ClockSynchronizationCommand)asdu.GetElement(0);
                Console.WriteLine("Received clock sync command with time " + qsc.NewTime.ToString());

                _ = Task.Run(async () =>
                {
                    try { await session.SendAsync(BuildActCon(asdu, _server.Parameters)); }
                    catch (Exception ex) { Console.WriteLine("Command response error: " + ex.Message); }
                });
            }
            else
            {
                Console.WriteLine("ASDU received: " + asdu.ToString());
            }
        }

        private static void OnAsduReceived(Iec104Session session, in AsduView view)
            => HandleAsdu(session, view);

        public static async Task Main(string[] args)
        {
            bool running = true;

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                running = false;
            };

            // Own certificate has to be a pfx file that contains the private key.
            X509Certificate2 ownCertificate = new X509Certificate2("server.pfx");

            var ssl = new ServiceSslOption
            {
                Certificate = ownCertificate,
                ClientCertificateRequired = false,
                CheckCertificateRevocation = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // NOTE: accept the example client certificates unconditionally.
                CertificateValidationCallback = (s, c, ch, err) => true
            };

            Iec104Server server = new Iec104Server(null, null, ssl);
            _server = server;

            server.ConnectionEvent += (session, ev) =>
            {
                Console.WriteLine("Connection event: " + ev);
            };

            server.AsduReceived += OnAsduReceived;

            await server.StartAsync(19998);

            ASDU initial = new ASDU(server.Parameters, CauseOfTransmission.INITIALIZED, false, false, 0, 1, false);
            initial.AddInformationObject(new EndOfInitialization(0));
            await server.BroadcastAsync(initial);

            Console.WriteLine("TLS server started on port 19998. Press Ctrl+C to stop.");

            int waitTime = 1000;

            while (running)
            {
                await Task.Delay(100);

                if (waitTime > 0)
                    waitTime -= 100;
                else
                {
                    ASDU newAsdu = new ASDU(server.Parameters, CauseOfTransmission.PERIODIC, false, false, 2, 1, false);
                    newAsdu.AddInformationObject(new MeasuredValueScaled(110, -1, new QualityDescriptor()));
                    await server.BroadcastAsync(newAsdu);

                    waitTime = 1000;
                }
            }

            Console.WriteLine("Stop server");
        }
    }
}
