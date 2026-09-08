// Example: subscribing to RAW protocol frames (IEC 104 client and IEC 101 client).
//
// Demonstrates the raw-message events available on both protocols:
//   - Iec104Client.RawFrameReceived / RawFrameSent   (complete APDU, includes 0x68 APCI header)
//   - Iec101Client.RawFrameReceived / RawFrameSent   (complete FT1.2 frame, incl. start/control/checksum)
//
// The payload of each event is a freshly-copied byte[] that is safe to keep, queue or write to disk.
// These events fire ONLY while a subscriber is attached (zero overhead otherwise).
//
// Usage:
//   cs104-client-raw             -> IEC 104 client, connects to 127.0.0.1:2404, sends station interrogation
//   cs104-client-raw --101       -> IEC 101 client (balanced, TCP transport), connects to 127.0.0.1:2404

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.CS101;

namespace cs104_client_raw
{
    class MainClass
    {
        // ── IEC 104 raw frame subscribers ─────────────────────────────
        // frame is a COMPLETE APDU: 0x68 | LEN | 4-byte APCI | ASDU...

        private static void PrintApdu(string direction, byte[] frame)
            => Console.WriteLine($"[104 {direction}] {BitConverter.ToString(frame)}");

        // ── IEC 101 raw frame subscribers ─────────────────────────────
        // frame is a COMPLETE FT1.2 frame: 0x68|0x10|0xE5 start, control, address, checksum, 0x16 end

        private static void PrintFrame101(string direction, byte[] frame)
            => Console.WriteLine($"[101 {direction}] {BitConverter.ToString(frame)}");

        // ── IEC 104 example ───────────────────────────────────────────

        private static async Task Run104ClientAsync()
        {
            await using var client = new Iec104Client("127.0.0.1", 2404);

            // Subscribe BEFORE connecting so every frame (STARTDT, I/S/U) is captured.
            client.RawFrameReceived += f => PrintApdu("RX", f);
            client.RawFrameSent += f => PrintApdu("TX", f);

            // The normal decoded ASDU event still works alongside the raw event.
            client.AsduReceived += (in AsduView view) =>
            {
                Console.WriteLine($"  [decoded] Type={view.TypeId} COT={view.Cot} CA={view.Ca} N={view.NumberOfElements}");
            };

            Console.WriteLine("IEC 104: connecting to 127.0.0.1:2404 (autostart STARTDT)...");
            await client.ConnectAsync();          // Autostart=true -> STARTDT_ACT appears in RawFrameSent

            Console.WriteLine("IEC 104: sending station interrogation (C_IC_NA_1)...");
            await client.SendInterrogationCommandAsync(CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);

            await Task.Delay(2000);
            Console.WriteLine("IEC 104: disconnecting...");
            await client.DisconnectAsync();
        }

        // ── IEC 101 example ───────────────────────────────────────────

        private static async Task Run101ClientAsync()
        {
            var llParams = new LinkLayerParameters { AddressLength = 1, UseSingleCharACK = true };
            var client = new Iec101Client("127.0.0.1", 2404, LinkLayerMode.BALANCED, llParams);
            client.OwnAddress = 1;
            client.SlaveAddress = 3;

            // New raw-frame events (like the IEC 104 ones): complete FT1.2 frames.
            client.RawFrameReceived += f => PrintFrame101("RX", f);
            client.RawFrameSent += f => PrintFrame101("TX", f);

            Console.WriteLine("IEC 101: connecting (TCP transport, balanced mode)...");
            var cts = new CancellationTokenSource();
            var loop = client.StartAsync(cts.Token);

            Console.WriteLine("IEC 101: sending station interrogation (C_IC_NA_1)...");
            client.SendInterrogationCommand(CauseOfTransmission.ACTIVATION, 1, QualifierOfInterrogation.STATION);

            await Task.Delay(2000);
            Console.WriteLine("IEC 101: disconnecting...");
            cts.Cancel();
            client.Stop();
            client.Dispose();
        }

        public static async Task Main(string[] args)
        {
            var use101 = args.Length > 0 && args[0] == "--101";

            if (use101)
            {
                await Run101ClientAsync();
            }
            else
            {
                await Run104ClientAsync();
            }

            Console.WriteLine("Done. (run with --101 to exercise the IEC 101 raw frame events)");
        }
    }
}
