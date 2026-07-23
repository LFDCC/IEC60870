// This example demonstrates a CS104 client "auto-reconnect on link drop" strategy.
//
// Key points:
//  * The client subscribes to ConnectionEvent and reacts to ApduConnectionEvent.ConnectionClosed
//    (the event raised when the peer closes the socket / RST / t1-t3 timeout -- fixed so the
//    client actually gets notified; an active DisconnectAsync does NOT raise it).
//  * On a passive drop it runs a reconnect loop with exponential back-off
//    (1s -> 2s -> 4s -> ... capped at MaxDelay), retrying until success or MaxRetries.
//  * After a (re)connection it re-runs StartDataTransferAsync + an interrogation so the link
//    is fully usable again, exactly like the first connect.
//  * An active shutdown (_manualShutdown) suppresses the reconnect loop so we don't fight a
//    user-initiated close.
//  * The single Iec104Client is reused across reconnects -- no new object, no leaked handlers
//    (we never unsubscribe/subscribe in the loop; the event is subscribed once at startup).
//
// A built-in Iec104Server is started only so this sample is self-contained: it crashes after a
// few seconds (StopAsync) and restarts later (StartAsync), letting you watch the client detect
// the drop and recover on its own. In a real deployment you would run an external station instead.

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.CS104;

namespace cs104_client_reconnect
{
    class MainClass
    {
        // ---- reconnect policy (exponential back-off) ----
        private const int MaxRetries = 12;
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

        private static Iec104Client _client = null!;
        private static volatile bool _manualShutdown;          // set on active shutdown -> no reconnect
        private static readonly object _reconnectGuard = new();
        private static bool _reconnecting;                      // ensures only one loop runs

        // ---- simulation timings (so the demo fits inside `timeout`) ----
        private static readonly TimeSpan CrashAfter = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(5); // after the crash

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IEC 60870-5-104 client auto-reconnect demo (LFDCC)");

            // Self-contained peer: a tiny server so the example runs standalone.
            var server = new Iec104Server();
            await server.StartAsync(2404);
            Console.WriteLine("[server] listening on :2404");

            _client = new Iec104Client("127.0.0.1", 2404);
            _client.AsduReceived += OnAsduReceived;
            _client.ConnectionEvent += OnConnectionEvent;

            await ConnectAndResume();   // first connect

            // Simulate an operator event: peer crashes, then comes back.
            _ = Task.Run(async () =>
            {
                await Task.Delay(CrashAfter);
                Console.WriteLine("[sim] PEER CRASH -> server.StopAsync()");
                await server.StopAsync();

                await Task.Delay(RestartAfter);
                Console.WriteLine("[sim] PEER BACK -> server.StartAsync(2404)");
                await server.StartAsync(2404);
            });

            Console.WriteLine("Press any key to shut down cleanly...");
            try
            {
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                // stdin is not a console (e.g. piped / CI). Keep the process alive long
                // enough for the reconnect demo to play out, then exit.
                await Task.Delay(TimeSpan.FromSeconds(25));
            }

            // Active shutdown: mark flag first so the drop is NOT treated as a reconnect trigger.
            _manualShutdown = true;
            await _client.DisconnectAsync();
            await server.StopAsync();
            Console.WriteLine("bye.");
        }

        // Connect (or reconnect) and bring the application layer back online.
        private static async Task ConnectAndResume()
        {
            await _client.ConnectAsync();
            await _client.StartDataTransferAsync();
            await SendInterrogation(1);
            Console.WriteLine("[client] connected + data-transfer started + interrogation sent");
        }

        private static void OnConnectionEvent(ApduConnectionEvent ev)
        {
            Console.WriteLine($"[client] connection event: {ev}");
            if (ev == ApduConnectionEvent.ConnectionClosed && !_manualShutdown)
            {
                // Fire-and-forget; the loop guards itself against re-entrancy.
                _ = Task.Run(ReconnectLoop);
            }
        }

        private static async Task ReconnectLoop()
        {
            lock (_reconnectGuard)
            {
                if (_reconnecting) return;   // an earlier loop is still alive
                _reconnecting = true;
            }

            try
            {
                var delay = InitialDelay;
                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    if (_manualShutdown) return;

                    Console.WriteLine($"[reconnect] attempt {attempt} in {delay.TotalSeconds:0}s...");
                    await Task.Delay(delay, CancellationToken.None);

                    try
                    {
                        await ConnectAndResume();
                        Console.WriteLine($"[reconnect] SUCCESS on attempt {attempt}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[reconnect] failed ({ex.GetType().Name}: {ex.Message})");
                        // exponential back-off, capped
                        delay = TimeSpan.FromMilliseconds(
                            Math.Min(delay.TotalMilliseconds * 2, MaxDelay.TotalMilliseconds));
                    }
                }
                Console.WriteLine($"[reconnect] gave up after {MaxRetries} attempts");
            }
            finally
            {
                lock (_reconnectGuard) { _reconnecting = false; }
            }
        }

        private static void OnAsduReceived(in AsduView view)
        {
            // Zero-copy view is only valid inside this callback; materialize what we need.
            Console.WriteLine($"[asdu] Type={view.TypeId} COT={view.Cot} CA={view.CommonAddress} " +
                              $"elements={view.NumberOfElements}");
        }

        private static async Task SendInterrogation(int ca)
        {
            var asdu = new ASDU(_client.Parameters, CauseOfTransmission.ACTIVATION,
                false, false, 0, ca, false);
            asdu.AddInformationObject(new InterrogationCommand(0, QualifierOfInterrogation.STATION));
            await _client.SendAsync(asdu);
        }
    }
}
