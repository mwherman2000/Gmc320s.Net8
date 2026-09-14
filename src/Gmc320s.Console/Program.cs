using Gmc320s;

namespace Gmc320s.Console;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        int? keyToPress = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--key")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var key) || key is < 0 or > 3)
                {
                    System.Console.Error.WriteLine("Error: --key requires a button number (0-3).");
                    return 4;
                }

                keyToPress = key;
                i++;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        var port = positional.Count > 0 ? positional[0] : null;

        using var cts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            System.Console.WriteLine("Stopping...");
            cts.Cancel();
        };

        try
        {
            Gmc320sClient gmc;
            string version;

            if (port is not null)
            {
                System.Console.WriteLine($"Connecting to GMC-320S on {port}...");
                gmc = Gmc320sClient.Connect(port);
                version = await gmc.GetVersionAsync(cts.Token);
            }
            else
            {
                System.Console.WriteLine("No port specified; scanning COM1-COM5 for a GMC device...");
                try
                {
                    (gmc, version) = await Gmc320sClient.FindPortAsync(
                        onStatus: msg => System.Console.WriteLine($"  {msg}"), cancellationToken: cts.Token);
                }
                catch (IOException)
                {
                    System.Console.Error.WriteLine("Error: no GMC device found on COM1-COM5.");
                    return 5;
                }

                port = gmc.PortName;
                System.Console.WriteLine($"Found GMC device on {port}.");
            }

            using (gmc)
            {
                System.Console.WriteLine($"Version: {version}");
                System.Console.WriteLine($"CPM: {await gmc.GetCpmAsync(cts.Token)}");
                System.Console.WriteLine($"Voltage: {await gmc.GetVoltageAsync(cts.Token):F1} V");
                System.Console.WriteLine($"Temperature: {await gmc.GetTemperatureCelsiusAsync(cts.Token):F1} °C");
                System.Console.WriteLine($"Device time: {await gmc.GetDateTimeAsync(cts.Token):yyyy-MM-dd HH:mm:ss}");
                System.Console.WriteLine($"Serial: {await gmc.GetSerialNumberAsync(cts.Token)}");

                var config = await gmc.GetConfigAsync(cts.Token);
                System.Console.WriteLine($"Config: {string.Join(", ", config.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");

                const int historyLength = 4096; // max chunk GetHistoryAsync/SPIR supports per call
                var history = await gmc.GetHistoryAsync(0, historyLength, cts.Token);
                var historyEnd = await gmc.FindHistoryEndAsync(cancellationToken: cts.Token);

                const int hexPreviewBytes = 64;
                var hexPreview = Convert.ToHexString(history, 0, Math.Min(hexPreviewBytes, history.Length));
                System.Console.WriteLine($"History[0..{historyLength}): {hexPreview}{(history.Length > hexPreviewBytes ? "..." : "")}  (log end: {(historyEnd is int e ? $"0x{e:X6} / {e}" : "not found")})");
                System.Console.WriteLine(historyEnd is int usedBytes
                    ? $"Estimated readings available: ~{usedBytes} (best guess, assuming ~1 byte/reading; actual count is somewhat lower due to periodic timestamp/marker overhead)"
                    : "Estimated readings available: unknown (no erased-flash boundary found within the scanned range)");

                foreach (var entry in GmcHistoryParser.Parse(history).Entries.Take(100))
                    System.Console.WriteLine($"  {entry}");

                if (keyToPress.HasValue)
                {
                    await gmc.PressKeyAsync(keyToPress.Value, cts.Token);
                    System.Console.WriteLine($"Pressed key {keyToPress.Value}.");
                }

                System.Console.WriteLine("Live CPS (Ctrl+C to stop):");

                // CPM is the trailing 60-second sum of CPS - the device itself computes it the same way.
                // A separate GETCPM query can't be interleaved here: ReadCpsAsync holds the client's gate for
                // its whole duration, and sending another command while the heartbeat stream is active would
                // also risk corrupting the byte framing.
                var window = new Queue<int>();
                await foreach (var cps in gmc.ReadCpsAsync(null, cts.Token))
                {
                    window.Enqueue(cps);
                    if (window.Count > 60) window.Dequeue();

                    System.Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  {cps,6} CPS  {window.Sum(),6} CPM");
                }
            }

            System.Console.WriteLine("Stopped.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine("Stopped.");
            return 0;
        }
        catch (TimeoutException ex)
        {
            System.Console.Error.WriteLine($"Error: the device did not respond in time ({ex.Message})");
            System.Console.Error.WriteLine("Check that the GMC-320S is powered on, the USB cable is connected, the port/baud rate are correct, and that no other program (e.g. GQ's own software) has the port open.");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            System.Console.Error.WriteLine($"Error: could not open {port} - it may already be in use by another program.");
            return 3;
        }
        catch (IOException)
        {
            var available = System.IO.Ports.SerialPort.GetPortNames();
            System.Console.Error.WriteLine($"Error: {port} could not be opened.");
            System.Console.Error.WriteLine(available.Length > 0
                ? $"Available ports: {string.Join(", ", available)}"
                : "No serial ports were detected on this machine.");
            return 4;
        }
        catch (ArgumentException ex)
        {
            var available = System.IO.Ports.SerialPort.GetPortNames();
            System.Console.Error.WriteLine($"Error: invalid port '{port}' ({ex.Message})");
            System.Console.Error.WriteLine(available.Length > 0
                ? $"Available ports: {string.Join(", ", available)}"
                : "No serial ports were detected on this machine.");
            return 4;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Unexpected error: {ex}");
            return 1;
        }
    }
}
