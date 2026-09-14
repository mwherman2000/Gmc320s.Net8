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
        var orientationSamples = 100;
        var fullScan = false;
        var rawCommands = new List<(string Command, int Length)>();
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
            else if (args[i] == "--orientation")
            {
                // Defaults to 100; 0 suppresses the sample table, leaving just the single reading above it.
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out orientationSamples) || orientationSamples < 0)
                {
                    System.Console.Error.WriteLine("Error: --orientation requires a sample count of 0 or more.");
                    return 4;
                }

                i++;
            }
            else if (args[i] == "--raw")
            {
                // <COMMAND>:<expected reply bytes>, e.g. --raw GETTEMP:4 - repeatable.
                var spec = i + 1 < args.Length ? args[i + 1].Split(':') : [];
                if (spec.Length != 2 || string.IsNullOrWhiteSpace(spec[0]) || !int.TryParse(spec[1], out var rawLength) || rawLength is <= 0 or > 4096)
                {
                    System.Console.Error.WriteLine("Error: --raw requires COMMAND:BYTES, e.g. --raw GETTEMP:4 (BYTES is 1-4096).");
                    return 4;
                }

                rawCommands.Add((spec[0], rawLength));
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
                System.Console.WriteLine();
                System.Console.WriteLine("--- Identity ---");
                System.Console.WriteLine($"Version: {version}");
                System.Console.WriteLine($"Serial: {await gmc.GetSerialNumberAsync(cts.Token)}");
                System.Console.WriteLine($"DeviceInfo: {await gmc.GetDeviceInfoAsync(cts.Token)}");

                System.Console.WriteLine();
                System.Console.WriteLine("--- Measurements ---   values marked (calculated) are derived by this library, not reported by the device");
                System.Console.WriteLine($"CPM: {await gmc.GetCpmAsync(cts.Token)}");
                System.Console.WriteLine($"Reading: {await gmc.ReadAsync(cts.Token)}  (MicroSievertsPerHour is calculated)");
                System.Console.WriteLine($"Voltage: {await gmc.GetVoltageAsync(cts.Token):F1} V");
                System.Console.WriteLine($"Temperature: {await gmc.GetTemperatureCelsiusAsync(cts.Token):F1} °C  (runs ~5-6 °C above ambient; self-heating)");

                var orientation = await gmc.GetOrientationAsync(cts.Token);
                System.Console.WriteLine(
                    $"Orientation: X={orientation.X} Y={orientation.Y} Z={orientation.Z}  =  " +
                    $"X={orientation.XG:F3}g Y={orientation.YG:F3}g Z={orientation.ZG:F3}g, |g|={orientation.Magnitude:F3} (calculated)  " +
                    $"[{(orientation.IsStable ? "still - axes are a valid orientation" : "MOVING - axes are motion, not orientation")}]");

                if (orientationSamples > 0)
                {
                    System.Console.WriteLine();
                    System.Console.WriteLine($"--- Orientation, {orientationSamples} samples ---   g and |g| are (calculated); a still device reads |g| = 1.000 whatever its orientation");
                    System.Console.WriteLine("   #        X       Y       Z         Xg       Yg       Zg      |g|  state");

                    var samples = new List<GmcOrientation>(orientationSamples);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (var sample = 1; sample <= orientationSamples; sample++)
                    {
                        var o = await gmc.GetOrientationAsync(cts.Token);
                        samples.Add(o);
                        System.Console.WriteLine(
                            $"  {sample,2}   {o.X,6}  {o.Y,6}  {o.Z,6}   {o.XG,8:F4} {o.YG,8:F4} {o.ZG,8:F4} {o.Magnitude,8:F4}" +
                            $"  {(o.IsStable ? "still" : o.Magnitude < 1 ? "FALLING" : "ACCEL")}");
                    }
                    clock.Stop();

                    // A run of byte-identical consecutive samples means we are polling faster than the sensor
                    // refreshes, so the extra reads carry no new information.
                    var repeats = samples.Zip(samples.Skip(1)).Count(pair => pair.First == pair.Second);
                    System.Console.WriteLine(
                        $"  {orientationSamples} samples in {clock.Elapsed.TotalSeconds:F2}s = " +
                        $"{orientationSamples / clock.Elapsed.TotalSeconds:F1} Hz, {clock.Elapsed.TotalMilliseconds / orientationSamples:F1} ms/read; " +
                        $"{repeats} identical to previous (calculated)");

                    // A single sample reading ~1 g proves nothing - a moving device crosses 1 g twice per
                    // oscillation - so this waits for consecutive samples to agree in direction too.
                    try
                    {
                        var settled = await gmc.GetStableOrientationAsync(cancellationToken: cts.Token);
                        System.Console.WriteLine(
                            $"  settled: X={settled.X} Y={settled.Y} Z={settled.Z}  =  " +
                            $"X={settled.XG:F4} Y={settled.YG:F4} Z={settled.ZG:F4}, |g|={settled.Magnitude:F4} (calculated, averaged)");
                    }
                    catch (TimeoutException ex)
                    {
                        System.Console.WriteLine($"  settled: unavailable - {ex.Message}");
                    }
                }

                // Converted using the calibration points the device itself stores, not a hardcoded
                // sensitivity - so it still refuses rather than guessing if that table is unreadable.
                try
                {
                    System.Console.WriteLine($"uSv/h: {await gmc.GetMicroSievertsPerHourAsync(cancellationToken: cts.Token):F4}  (calculated from CPM and the device's stored calibration)");
                }
                catch (NotSupportedException ex)
                {
                    System.Console.WriteLine($"uSv/h: not available - {ex.Message}");
                }

                System.Console.WriteLine();
                System.Console.WriteLine("--- Clock and configuration ---");
                System.Console.WriteLine($"Device time: {await gmc.GetDateTimeAsync(cts.Token):yyyy-MM-dd HH:mm:ss}");

                var config = await gmc.GetConfigAsync(cts.Token);
                System.Console.WriteLine($"Config: {string.Join(", ", config.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
                System.Console.WriteLine($"Config raw: {config.Raw.Length} bytes, first 32: {Convert.ToHexString(config.Raw, 0, Math.Min(32, config.Raw.Length))}");
                System.Console.WriteLine(config.Calibration.Count > 0
                    ? $"Calibration: {string.Join(", ", config.Calibration.Select(p => $"{p.Cpm} CPM = {p.MicroSievertsPerHour:G} uSv/h"))}"
                      + $"  [ratios: {string.Join(", ", config.Calibration.Select(p => $"{p.Cpm / p.MicroSievertsPerHour:F1}"))} CPM per uSv/h (calculated)]"
                    : "Calibration: no usable points found in the configuration.");

                foreach (var (rawCommand, rawLength) in rawCommands)
                {
                    var reply = await gmc.SendRawAsync(rawCommand, rawLength, cts.Token);
                    System.Console.WriteLine($"Raw <{rawCommand}>> -> {Convert.ToHexString(reply)}");
                }

                System.Console.WriteLine();
                System.Console.WriteLine("--- History ---");

                const int historyLength = 4096; // max chunk GetHistoryAsync/SPIR supports per call
                var history = await gmc.GetHistoryAsync(0, historyLength, cts.Token);

                const int hexPreviewBytes = 64;
                var hexPreview = Convert.ToHexString(history, 0, Math.Min(hexPreviewBytes, history.Length));
                System.Console.WriteLine($"History[0..{historyLength}): {hexPreview}{(history.Length > hexPreviewBytes ? "..." : "")}");

                // Bisect to the write pointer rather than walking the log: ~13 reads instead of one per chunk.
                var historyEnd = await gmc.FindHistoryEndFastAsync(cancellationToken: cts.Token);
                System.Console.WriteLine(historyEnd is int end
                    ? $"Log write pointer: 0x{end:X6} / {end}  (calculated - probed by bisection; the protocol never reports it)"
                    : "Log write pointer: not found - the tail of the scanned range isn't erased, so the log may be full or wrapped.");
                if (historyEnd is int usedBytes)
                    System.Console.WriteLine($"Readings stored: ~{usedBytes}  (calculated - assumes ~1 byte each, so it overcounts by the timestamp/marker overhead)");

                if (historyEnd is int writePointer)
                {
                    var recent = await gmc.GetRecentHistoryAsync(recentCount, writePointer, cancellationToken: cts.Token);
                    System.Console.WriteLine($"Most recent {recentCount} readings (window from 0x{recent.WindowStartAddress:X6}, {recent.Entries.Count} entries incl. anchors):");

                    var position = 0;
                    foreach (var entry in recent.Entries)
                        System.Console.WriteLine($"  [{position++,4}] {entry}");
                }

                // The linear scan reads one chunk per 4096 bytes of log, so it's opt-in alongside the full
                // walk. It reports the FIRST erased run it meets, where the bisecting probe above reports the
                // LAST written byte - they agree on a simple log and can differ if the log contains an
                // interior run of 0xFF, which is exactly why both exist.
                if (fullScan)
                {
                    var linearEnd = await gmc.FindHistoryEndAsync(cancellationToken: cts.Token);
                    System.Console.WriteLine(linearEnd is int first
                        ? $"Linear scan boundary: 0x{first:X6} / {first}{(linearEnd == historyEnd ? " (agrees with the bisecting probe)" : " (DIFFERS from the bisecting probe - interior erased run?)")}"
                        : "Linear scan boundary: not found.");
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

                System.Console.WriteLine();
                System.Console.WriteLine("--- Live CPS (Ctrl+C to stop) ---");
                System.Console.WriteLine("time         CPS     CPM (calculated)");

                // CPM here is a trailing 60-second sum of CPS computed on this side, NOT the device's own
                // GETCPM: ReadCpsAsync holds the client's gate for its whole duration, so a GETCPM query
                // can't be interleaved, and issuing one mid-heartbeat would risk corrupting the framing.
                // It also reads low for the first minute, until the window fills.
                var window = new Queue<int>();
                await foreach (var cps in gmc.ReadCpsAsync(null, cts.Token))
                {
                    window.Enqueue(cps);
                    if (window.Count > 60) window.Dequeue();

                    System.Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  {cps,6}  {window.Sum(),6}{(window.Count < 60 ? "  (window filling)" : "")}");
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
