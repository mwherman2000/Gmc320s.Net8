using Gmc320s;

namespace Gmc320s.Console;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var exitCode = await RunAsync(args);
        Pause();
        return exitCode;
    }

    /// <summary>Keeps the window open when the app is launched from a debugger or by double-clicking the exe.</summary>
    private static void Pause()
    {
        // Redirected input means there's no interactive console to wait on (a pipe, CI, or a test harness),
        // and ReadKey would throw rather than block.
        if (System.Console.IsInputRedirected) return;

        System.Console.WriteLine();
        System.Console.Write("Press any key to exit...");
        try
        {
            System.Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console attached at all - nothing to pause for.
        }

        System.Console.WriteLine();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        int? keyToPress = null;
        var recentCount = 100;
        var fullScan = false;
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
            else if (args[i] == "--recent")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out recentCount) || recentCount <= 0)
                {
                    System.Console.Error.WriteLine("Error: --recent requires a positive number of readings.");
                    return 4;
                }

                i++;
            }
            else if (args[i] == "--full-scan")
            {
                fullScan = true;
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

                var gyro = await gmc.GetGyroAsync(cts.Token);
                System.Console.WriteLine($"Gyro: X={gyro.X} Y={gyro.Y} Z={gyro.Z}");

                var config = await gmc.GetConfigAsync(cts.Token);
                System.Console.WriteLine($"Config: {string.Join(", ", config.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");

                const int historyLength = 4096; // max chunk GetHistoryAsync/SPIR supports per call
                var history = await gmc.GetHistoryAsync(0, historyLength, cts.Token);

                const int hexPreviewBytes = 64;
                var hexPreview = Convert.ToHexString(history, 0, Math.Min(hexPreviewBytes, history.Length));
                System.Console.WriteLine($"History[0..{historyLength}): {hexPreview}{(history.Length > hexPreviewBytes ? "..." : "")}");

                // Bisect to the write pointer rather than walking the log: ~13 reads instead of one per chunk.
                var historyEnd = await gmc.FindHistoryEndFastAsync(cancellationToken: cts.Token);
                System.Console.WriteLine(historyEnd is int end
                    ? $"Log write pointer: 0x{end:X6} / {end} (~{end} readings, assuming ~1 byte each minus timestamp/marker overhead)"
                    : "Log write pointer: not found - the tail of the scanned range isn't erased, so the log may be full or wrapped.");

                if (historyEnd is int writePointer)
                {
                    var recent = await gmc.GetRecentHistoryAsync(recentCount, writePointer, cancellationToken: cts.Token);
                    System.Console.WriteLine($"Most recent {recentCount} readings (window from 0x{recent.WindowStartAddress:X6}):");

                    var position = 0;
                    foreach (var entry in recent.Entries)
                        System.Console.WriteLine($"  [{position++,4}] {entry}");
                }

                // The forward walk costs one read per 4096 bytes of log, so it's opt-in; it's the way to see
                // every timestamp across the whole history rather than just the tail. A marker can straddle a
                // chunk boundary, so unparsed trailing bytes carry over into the next chunk.
                if (fullScan && historyEnd is int scanEnd)
                {
                    System.Console.WriteLine($"Scanning the whole log (0x000000..0x{scanEnd:X6}) for timestamps...");

                    var remainder = Array.Empty<byte>();
                    var address = 0;
                    var index = 0;
                    while (address < scanEnd)
                    {
                        var length = Math.Min(historyLength, scanEnd - address);
                        var chunkBytes = await gmc.GetHistoryAsync(address, length, cts.Token);
                        byte[] combined = [.. remainder, .. chunkBytes];

                        var parsed = GmcHistoryParser.Parse(combined);
                        foreach (var entry in parsed.Entries)
                        {
                            if (entry is GmcHistoryTimestamp) System.Console.WriteLine($"  [{index,6}] {entry}");
                            index++;
                        }

                        remainder = parsed.UnparsedRemainder;
                        address += length;
                    }

                    System.Console.WriteLine($"Done scanning: {index} entries.");
                }

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
