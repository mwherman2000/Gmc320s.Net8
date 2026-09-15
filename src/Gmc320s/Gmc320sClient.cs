using System.Buffers.Binary;
using System.Text;

namespace Gmc320s;

/// <summary>GQ Electronics GMC-320S client for the RFC1201 serial protocol.</summary>
public sealed class Gmc320sClient : IDisposable
{
    private readonly Gmc320sConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Gmc320sClient(Gmc320sConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (!_connection.IsOpen) _connection.Open();
        TryDisableHeartbeat();
    }

    public static Gmc320sClient Connect(string portName, int baudRate = 115200, int timeoutMs = 5000)
        => new(new Gmc320sConnection(portName, baudRate, timeoutMs, timeoutMs));

    /// <summary>Default set of ports probed by <see cref="FindPortAsync"/> when no candidate list is supplied.</summary>
    public static readonly string[] DefaultScanPorts = { "COM1", "COM2", "COM3", "COM4", "COM5" };

    /// <summary>
    /// Tries each candidate port in turn, connecting and issuing <c>GETVER</c>, until one replies with a
    /// version string starting with "GMC". Ports that time out, are already in use, or don't exist are
    /// skipped. Throws <see cref="IOException"/> if none of the candidates yield a GMC device.
    /// </summary>
    public static async Task<(Gmc320sClient Client, string Version)> FindPortAsync(
        IEnumerable<string>? candidatePorts = null, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in candidatePorts ?? DefaultScanPorts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStatus?.Invoke($"Trying {candidate}...");

            Gmc320sClient? client = null;
            var found = false;
            try
            {
                client = Connect(candidate);
                var version = await client.GetVersionAsync(cancellationToken);
                if (version.StartsWith("GMC", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    return (client, version);
                }

                onStatus?.Invoke($"{candidate}: unexpected response '{version}'.");
            }
            catch (TimeoutException)
            {
                onStatus?.Invoke($"{candidate}: timed out waiting for a response.");
            }
            catch (UnauthorizedAccessException)
            {
                onStatus?.Invoke($"{candidate}: port is in use by another program.");
            }
            catch (IOException)
            {
                onStatus?.Invoke($"{candidate}: port does not exist.");
            }
            catch (ArgumentException)
            {
                onStatus?.Invoke($"{candidate}: invalid port.");
            }
            finally
            {
                // Dispose on every exit path except the successful match above (including exception types
                // not caught here), so a partially-opened port is never left dangling.
                if (!found) client?.Dispose();
            }
        }

        throw new IOException("No GMC device found on any of the candidate ports.");
    }

    public string PortName => _connection.PortName;
    public int BaudRate => _connection.BaudRate;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => Encoding.ASCII.GetString(_connection.Command("GETVER", 14)).Trim('\0', ' ', '\r', '\n'), cancellationToken);

    /// <remarks>
    /// Do not poll this in a tight loop. The device silently ignores a <c>GETCPM</c> that arrives too soon
    /// after the previous one - measured at roughly a one-second minimum interval - so a rapid caller pays a
    /// full read timeout on every reading. The automatic retry then succeeds, which hides the stall: 20
    /// back-to-back calls cost about 5.1 seconds each. Space CPM reads about a second apart. See
    /// PROTOCOL-NOTES.md.
    /// </remarks>
    public async Task<int> GetCpmAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => BinaryPrimitives.ReadUInt16BigEndian(_connection.Command("GETCPM", 2)), cancellationToken);

    public async Task<double> GetVoltageAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Command("GETVOLT", 1)[0] / 10.0, cancellationToken);

    /// <remarks>
    /// Two things to know about this reading, both established against real hardware (see PROTOCOL-NOTES.md):
    /// <list type="bullet">
    /// <item>Values run roughly 5-6 °C above room temperature. That is self-heating inside a USB-powered
    /// enclosure, not a decoding error - the encoding is plain binary and was verified as such.</item>
    /// <item>The sensor occasionally answers with a known-bad value - an impossible "negative zero" (sign
    /// flag set over zero magnitude), or 85.0 °C, the classic power-on-reset default of digital temperature
    /// sensors. Both are rejected and re-read, up to three attempts, so callers never silently receive one.
    /// If every attempt is implausible an <see cref="InvalidDataException"/> is thrown rather than a wrong
    /// number being returned.</item>
    /// <item>Beyond those, consecutive reads are byte-identical, so no general double-read is needed; a value
    /// that disagrees with a moments-earlier one reflects genuine thermal lag, which re-reading cannot fix.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidDataException">Every attempt returned an implausible reading.</exception>
    public async Task<double> GetTemperatureCelsiusAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() =>
        {
            byte[] reply = [];
            for (var attempt = 1; attempt <= TemperatureAttempts; attempt++)
            {
                reply = _connection.Command("GETTEMP", 4);
                var celsius = DecodeTemperature(reply);
                if (IsPlausible(reply, celsius)) return celsius;
            }

            throw new InvalidDataException(
                $"GETTEMP returned an implausible reading on all {TemperatureAttempts} attempts (last reply: " +
                $"{Convert.ToHexString(reply)}, decoded as {DecodeTemperature(reply):F1} °C). The sensor reports " +
                "known-bad values occasionally; a persistent one suggests the sensor is faulty or this firmware " +
                "encodes temperature differently. Use SendRawAsync(\"GETTEMP\", 4) to inspect the raw reply.");
        }, cancellationToken);

    private const int TemperatureAttempts = 3;

    // The device is specified for roughly 0-50 °C ambient and reads ~5-6 °C high from self-heating, so this
    // range is deliberately far wider than any genuine reading while still excluding the known-bad 85.0 °C.
    private const double MinPlausibleCelsius = -20.0;
    private const double MaxPlausibleCelsius = 70.0;

    private static double DecodeTemperature(byte[] reply) => (reply[2] == 0 ? 1 : -1) * (reply[0] + reply[1] / 10.0);

    private static bool IsPlausible(byte[] reply, double celsius)
    {
        // "-0.0 °C": the sign flag set over a zero magnitude, which is never a real reading. This one is
        // recognised structurally rather than by value, having been captured raw as 00 00 01 AA.
        if (reply[0] == 0 && reply[1] == 0 && reply[2] != 0) return false;

        return celsius is >= MinPlausibleCelsius and <= MaxPlausibleCelsius;
    }

    public async Task<DateTime> GetDateTimeAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() =>
        {
            var b = _connection.Command("GETDATETIME", 7);
            return new DateTime(2000 + b[0], b[1], b[2], b[3], b[4], b[5], DateTimeKind.Unspecified);
        }, cancellationToken);

    /// <summary>Sets the device's internal clock. The device stores no time zone, so pass whatever wall-clock time you want it to read back.</summary>
    public async Task SetDateTimeAsync(DateTime dateTime, CancellationToken cancellationToken = default)
    {
        if (dateTime.Year is < 2000 or > 2099)
            throw new ArgumentOutOfRangeException(nameof(dateTime), "GMC-320S only supports years 2000-2099.");

        var payload = new byte[]
        {
            (byte)(dateTime.Year - 2000), (byte)dateTime.Month, (byte)dateTime.Day,
            (byte)dateTime.Hour, (byte)dateTime.Minute, (byte)dateTime.Second
        };

        await ExecuteAsync(() =>
        {
            var b = _connection.CommandWithPayload("SETDATETIME", payload, 1);
            if (b[0] != 0xAA) throw new IOException($"GMC-320S rejected SETDATETIME (reply byte 0x{b[0]:X2}).");
        }, cancellationToken);
    }

    /// <remarks>
    /// Despite the command name these are accelerometer readings, not rotation rates: the values are the
    /// static gravity vector, so a stationary device reads 1 g total rather than zero.
    /// <para>
    /// Scale is <b>16384 counts per g</b>. The underlying sensor is 12-bit left-shifted into a 16-bit field -
    /// every value is a multiple of 16 - so there are 4096 effective steps over a ±2 g range. Lying flat, Z
    /// reads about -16384 (-1 g) while X and Y sit within a few hundred counts of zero, which is a tilt of
    /// roughly a degree. Divide by 16384.0 to get g. See PROTOCOL-NOTES.md for the measurements.
    /// </para>
    /// <para>
    /// That scale is exact only for Z. A six-position calibration gave gain/offset of 0.995 and +0.011 g for
    /// X, 0.960 and -0.002 g for Y, and 0.999 and -0.003 g for Z: X's error is almost pure bias, Y's almost
    /// pure gain. Fine for coarse orientation; apply per-axis gain and offset for quantitative tilt work, and
    /// let the device settle first - readings taken while it is being handled drift substantially.
    /// </para>
    /// </remarks>
    public async Task<GmcOrientation> GetOrientationAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() =>
        {
            var b = _connection.Command("GETGYRO", 7);
            return new GmcOrientation(BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(0, 2)), BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(2, 2)), BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(4, 2)));
        }, cancellationToken);

    /// <summary>
    /// Samples the accelerometer until several consecutive readings agree, and returns their average - a
    /// reading whose axes can actually be trusted as an orientation.
    /// </summary>
    /// <remarks>
    /// <see cref="GmcOrientation.IsStable"/> on a single sample is necessary but <b>not sufficient</b>. A
    /// moving device passes through 1 g magnitude twice per oscillation, so an unlucky single sample looks
    /// perfectly still: during a hand-shake capture, readings of 0.968 g and 1.036 g were taken while the
    /// device was being violently rotated. Only agreement across consecutive samples - in direction, not just
    /// magnitude - separates genuinely stationary from momentarily-passing-through.
    /// <para>
    /// The returned value is the mean of the qualifying window, which also averages out the ~1% per-sample
    /// noise. It is therefore not necessarily a multiple of 16, unlike a raw <see cref="GetOrientationAsync"/>
    /// reading.
    /// </para>
    /// </remarks>
    /// <param name="consecutiveSamples">How many consecutive agreeing samples are required.</param>
    /// <param name="toleranceG">How far each sample's magnitude may sit from 1 g. Defaults to <see cref="GmcOrientation.DefaultStabilityTolerance"/>, which is loose because the axes are unevenly trimmed.</param>
    /// <param name="agreementG">How far the samples may spread on any one axis. The default of 0.05 g sits well above the ~0.02 g spread of a motionless device and well below the ~0.16 g swing between consecutive samples of a moving one.</param>
    /// <param name="maxSamples">Give up after this many reads.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="TimeoutException">The device did not hold still within <paramref name="maxSamples"/> reads.</exception>
    public async Task<GmcOrientation> GetStableOrientationAsync(
        int consecutiveSamples = 4,
        double toleranceG = GmcOrientation.DefaultStabilityTolerance,
        double agreementG = 0.05,
        int maxSamples = 40,
        CancellationToken cancellationToken = default)
    {
        if (consecutiveSamples < 2) throw new ArgumentOutOfRangeException(nameof(consecutiveSamples), "At least two samples are needed to establish agreement.");
        if (toleranceG <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceG), "Must be positive.");
        if (agreementG <= 0) throw new ArgumentOutOfRangeException(nameof(agreementG), "Must be positive.");
        if (maxSamples < consecutiveSamples) throw new ArgumentOutOfRangeException(nameof(maxSamples), "Must allow at least consecutiveSamples reads.");

        var window = new List<GmcOrientation>(consecutiveSamples);

        for (var taken = 0; taken < maxSamples; taken++)
        {
            var sample = await GetOrientationAsync(cancellationToken);

            // A sample nowhere near 1 g cannot belong to a stationary run at all, so the run restarts.
            if (!sample.IsStableWithin(toleranceG))
            {
                window.Clear();
                continue;
            }

            window.Add(sample);
            if (window.Count < consecutiveSamples) continue;
            if (AxesAgree(window, agreementG)) return Average(window);

            window.RemoveAt(0);
        }

        throw new TimeoutException(
            $"The device did not hold still: no {consecutiveSamples} consecutive readings agreed within " +
            $"{agreementG:F3} g per axis across {maxSamples} samples. Let it come to rest, or relax the thresholds.");
    }

    private static bool AxesAgree(List<GmcOrientation> window, double agreementG) =>
        Spread(window.Select(o => o.XG)) <= agreementG &&
        Spread(window.Select(o => o.YG)) <= agreementG &&
        Spread(window.Select(o => o.ZG)) <= agreementG;

    private static double Spread(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Max() - list.Min();
    }

    private static GmcOrientation Average(List<GmcOrientation> window) => new(
        (short)Math.Round(window.Average(o => (double)o.X)),
        (short)Math.Round(window.Average(o => (double)o.Y)),
        (short)Math.Round(window.Average(o => (double)o.Z)));

    public async Task<GmcDeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(cancellationToken);
        var serial = await GetSerialNumberAsync(cancellationToken);
        return new GmcDeviceInfo(version, serial);
    }

    /// <summary>Returns the device's serial number as a 14-character hex string.</summary>
    public async Task<string> GetSerialNumberAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => Convert.ToHexString(_connection.Command("GETSERIAL", 7)), cancellationToken);

    /// <summary>Powers the device off. The device does not acknowledge this command.</summary>
    /// <remarks>
    /// Observed on real hardware: the device cannot actually be powered off while USB is connected - bus power
    /// keeps it running - so over a USB connection this command has no visible effect. Since the same
    /// connection is what carries the command, there is no way to confirm it either.
    /// </remarks>
    public async Task PowerOffAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Send("POWEROFF"), cancellationToken);

    /// <summary>Powers the device on (firmware 5.71+). The device does not acknowledge this command.</summary>
    /// <remarks>
    /// Largely moot over USB: plugging the cable in already powers the device on, so by the time a connection
    /// exists to send this over, the device is on.
    /// </remarks>
    public async Task PowerOnAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Send("POWERON"), cancellationToken);

    /// <summary>Reboots the device. The device does not acknowledge this command and the connection will be lost.</summary>
    public async Task RebootAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Send("REBOOT"), cancellationToken);

    /// <summary>Resets the device to factory defaults. The device does not acknowledge this command.</summary>
    public async Task FactoryResetAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Send("FACTORYRESET"), cancellationToken);

    /// <summary>Simulates pressing one of the device's four physical buttons (keys 0-3, i.e. S1-S4).</summary>
    public async Task PressKeyAsync(int key, CancellationToken cancellationToken = default)
    {
        if (key is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(key), "Key must be 0-3.");
        await ExecuteAsync(() => _connection.Send($"KEY{key}"), cancellationToken);
    }

    /// <summary>Reads raw history data out of the device's onboard flash storage.</summary>
    /// <param name="address">Zero-based flash address (0-0xFFFFFF).</param>
    /// <param name="length">Number of bytes to read (1-4096).</param>
    /// <param name="cancellationToken"></param>
    /// <remarks>The SPIR length encoding (actual length minus one) is protocol-derived and not yet verified against physical hardware.</remarks>
    public async Task<byte[]> GetHistoryAsync(int address, int length, CancellationToken cancellationToken = default)
    {
        if (address is < 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(address), "Address must fit in 24 bits.");
        if (length is <= 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(length), "Length must be 1-4096.");

        var encodedLength = length - 1;
        var payload = new byte[]
        {
            (byte)(address >> 16), (byte)(address >> 8), (byte)address,
            (byte)(encodedLength >> 8), (byte)encodedLength
        };

        return await ExecuteAsync(() => _connection.CommandWithPayload("SPIR", payload, length), cancellationToken);
    }

    /// <summary>
    /// Reads flash forward in chunks from <paramref name="startAddress"/>, looking for where logged history
    /// ends and erased (unwritten) flash begins, and returns the address of the first byte of that gap.
    /// </summary>
    /// <param name="startAddress">Address to start scanning from (0-0xFFFFFF).</param>
    /// <param name="maxAddress">
    /// Upper bound of the scan, exclusive. Defaults to 0x100000 (1MB), the commonly cited GMC-320 flash size -
    /// unverified against physical hardware, so pass a larger value if your device holds more.
    /// </param>
    /// <param name="chunkSize">Bytes read per <see cref="GetHistoryAsync"/> call (1-4096).</param>
    /// <param name="minErasedRunLength">
    /// How many consecutive 0xFF bytes count as "erased flash" rather than coincidental 0xFF bytes inside a real
    /// log entry (0xFF is also used as a marker byte for special entries - see <c>PROTOCOL-NOTES.md</c>). Longer
    /// reduces false positives at the risk of overshooting past a genuine gap into more written data.
    /// </param>
    /// <returns>The address where the erased-flash run starts, or <see langword="null"/> if none was found before <paramref name="maxAddress"/>.</returns>
    public async Task<int?> FindHistoryEndAsync(
        int startAddress = 0, int maxAddress = 0x100000, int chunkSize = 4096, int minErasedRunLength = 64,
        CancellationToken cancellationToken = default)
    {
        if (startAddress is < 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(startAddress), "Address must fit in 24 bits.");
        if (maxAddress <= startAddress) throw new ArgumentOutOfRangeException(nameof(maxAddress), "Must be greater than startAddress.");
        if (chunkSize is <= 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be 1-4096.");
        if (minErasedRunLength <= 0) throw new ArgumentOutOfRangeException(nameof(minErasedRunLength), "Must be positive.");

        var address = startAddress;
        var runStart = -1;
        var runLength = 0;

        while (address < maxAddress)
        {
            var length = Math.Min(chunkSize, maxAddress - address);
            var chunk = await GetHistoryAsync(address, length, cancellationToken);

            for (var i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] == 0xFF)
                {
                    if (runLength == 0) runStart = address + i;
                    if (++runLength >= minErasedRunLength) return runStart;
                }
                else
                {
                    runLength = 0;
                }
            }

            address += length;
        }

        return null;
    }

    /// <summary>
    /// Locates the log's write pointer by binary search instead of the forward scan
    /// <see cref="FindHistoryEndAsync"/> does, costing roughly log2(<paramref name="maxAddress"/> /
    /// <paramref name="probeSize"/>) reads rather than one per chunk of the whole log.
    /// </summary>
    /// <remarks>
    /// This relies on the log being written sequentially with erased flash after it, so that "is this region
    /// erased?" is monotonic in the address. Two consequences worth knowing:
    /// <list type="bullet">
    /// <item>Where <see cref="FindHistoryEndAsync"/> reports the <em>first</em> erased run it meets (useful
    /// when you want the earliest boundary), this reports the <em>last</em> written byte - which is what you
    /// want for reading recent data.</item>
    /// <item>If the log has ever wrapped, the invariant fails and bisection would mislead. The final
    /// <paramref name="probeSize"/> bytes are checked first, and <see langword="null"/> is returned without
    /// bisecting if they are not erased - so a full or wrapped buffer yields no answer rather than a
    /// confidently wrong one. See PROTOCOL-NOTES.md; wrap behavior is still untested.</item>
    /// </list>
    /// </remarks>
    /// <param name="maxAddress">Upper bound of the search, exclusive. Defaults to 0x100000 (1MB), the commonly cited - but unverified - GMC-320 flash size.</param>
    /// <param name="probeSize">Bytes read per probe, and the number of consecutive 0xFF bytes required to call a region erased. Larger is more resistant to a genuine run of 255-valued readings being mistaken for erased flash.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The first erased address after the newest entry, or <see langword="null"/> if the tail of the searched range is not erased.</returns>
    public async Task<int?> FindHistoryEndFastAsync(
        int maxAddress = 0x100000, int probeSize = 256, CancellationToken cancellationToken = default)
    {
        if (maxAddress is <= 0 or > 0x1000000) throw new ArgumentOutOfRangeException(nameof(maxAddress), "Must be 1-0x1000000.");
        if (probeSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(probeSize), "Probe size must be 1-2048 (two probes must fit in one 4096-byte read).");

        var tailProbe = Math.Min(probeSize, maxAddress);
        if (!IsErased(await GetHistoryAsync(maxAddress - tailProbe, tailProbe, cancellationToken))) return null;

        // Invariant: everything at or below lo is treated as written, hi is known erased.
        var lo = 0;
        var hi = maxAddress - tailProbe;
        while (hi - lo > probeSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mid = lo + (hi - lo) / 2;
            var probe = await GetHistoryAsync(mid, Math.Min(probeSize, maxAddress - mid), cancellationToken);
            if (IsErased(probe)) hi = mid; else lo = mid;
        }

        // The boundary lies within [lo, hi + probeSize); read that span and take the last written byte.
        var spanLength = Math.Min(hi + probeSize - lo, maxAddress - lo);
        var span = await GetHistoryAsync(lo, spanLength, cancellationToken);
        for (var i = span.Length - 1; i >= 0; i--)
            if (span[i] != 0xFF) return lo + i + 1;

        return lo;
    }

    private static bool IsErased(byte[] chunk) => chunk.All(b => b == 0xFF);

    /// <summary>
    /// Reads the most recent <paramref name="readingCount"/> readings without paging through the whole log:
    /// it locates the write pointer by binary search, then reads backward from it and resynchronizes on a
    /// <c>55 AA</c> anchor.
    /// </summary>
    /// <remarks>
    /// The number of readings between anchors is deliberately not assumed: a device restart writes an extra
    /// anchor and an interrupted session ends a batch early (both seen on real hardware), so the bytes needed
    /// for a given number of readings cannot be computed up front. The window is grown backward until enough
    /// readings are found, the start of flash is reached, or <paramref name="maxLookbackBytes"/> is exhausted.
    /// </remarks>
    /// <param name="readingCount">How many of the newest readings to return.</param>
    /// <param name="historyEnd">A known write pointer, to skip re-probing for it. Omit to locate it via <see cref="FindHistoryEndFastAsync"/>.</param>
    /// <param name="maxAddress">Passed through to <see cref="FindHistoryEndFastAsync"/> when <paramref name="historyEnd"/> is omitted.</param>
    /// <param name="maxLookbackBytes">Safety cap on how far back to read before giving up and returning what was found.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="InvalidOperationException">The write pointer could not be located - see <see cref="FindHistoryEndFastAsync"/>.</exception>
    public async Task<GmcRecentHistory> GetRecentHistoryAsync(
        int readingCount, int? historyEnd = null, int maxAddress = 0x100000, int maxLookbackBytes = 65536,
        CancellationToken cancellationToken = default)
    {
        if (readingCount <= 0) throw new ArgumentOutOfRangeException(nameof(readingCount), "Must be positive.");
        if (maxLookbackBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLookbackBytes), "Must be positive.");

        var end = historyEnd ?? await FindHistoryEndFastAsync(maxAddress, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Could not locate the end of the history log: the tail of the searched range is not erased flash, " +
                "so the log may be full or may have wrapped. Pass a larger maxAddress, or a known historyEnd.");

        const int maxChunk = 4096;
        var collected = Array.Empty<byte>();
        var windowStart = end;
        var entries = (IReadOnlyList<GmcHistoryEntry>)Array.Empty<GmcHistoryEntry>();

        while (windowStart > 0 && end - windowStart < maxLookbackBytes)
        {
            var chunk = Math.Min(maxChunk, windowStart);
            windowStart -= chunk;
            collected = [.. await GetHistoryAsync(windowStart, chunk, cancellationToken), .. collected];

            // Address 0 is a known entry boundary; anywhere else the window may start mid-entry.
            entries = windowStart == 0
                ? GmcHistoryParser.Parse(collected).Entries
                : GmcHistoryParser.ParseResynced(collected).Entries;

            if (entries.Count(e => e is GmcHistoryReading) >= readingCount) break;
        }

        return new GmcRecentHistory(TrimToLastReadings(entries, readingCount), windowStart, end);
    }

    private static IReadOnlyList<GmcHistoryEntry> TrimToLastReadings(IReadOnlyList<GmcHistoryEntry> entries, int readingCount)
    {
        var seen = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is GmcHistoryReading && ++seen == readingCount)
                return entries.Skip(i).ToList();
        }

        return entries;
    }

    /// <summary>Reads the current CPM, converted to µSv/h where the device's calibration allows it.</summary>
    /// <remarks>
    /// Costs two round trips (<c>GETCPM</c> then <c>GETCFG</c>). <see cref="GmcReading.MicroSievertsPerHour"/>
    /// is left <see langword="null"/> rather than throwing when the device holds no usable calibration.
    /// </remarks>
    public async Task<GmcReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var cpm = await GetCpmAsync(cancellationToken);
        var config = await GetConfigAsync(cancellationToken);

        return new GmcReading(
            DateTimeOffset.UtcNow,
            cpm,
            GmcCalibration.TryToMicroSievertsPerHour(cpm, config.Calibration, out var microSieverts) ? microSieverts : null);
    }

    /// <summary>Enables the device heartbeat and yields one CPS value approximately each second.</summary>
    /// <remarks>
    /// A dropped or delayed heartbeat frame is retried the same way commands retry transient timeouts (see
    /// <see cref="Gmc320sConnection.ReadHeartbeatFrame"/>). Each read runs on a background thread
    /// so it never blocks the caller, but cancellation still can't interrupt a read already in flight - in the
    /// worst case (the device stops responding entirely) stopping takes up to the connection's read timeout.
    /// </remarks>
    public async IAsyncEnumerable<int> ReadCpsAsync(int? count = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (count is < 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _connection.Clear();
            _connection.Send("HEARTBEAT1");
            var i = 0;
            while (!count.HasValue || i++ < count.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var b = await Task.Run(() => _connection.ReadHeartbeatFrame(2), cancellationToken);
                yield return BinaryPrimitives.ReadUInt16BigEndian(b) & 0x3FFF;
            }
        }
        finally
        {
            TryDisableHeartbeat();
            _gate.Release();
        }
    }

    /// <summary>Converts CPM to µSv/h using the calibration points stored in the device's own configuration.</summary>
    /// <param name="cpm">The CPM value to convert. Omit to read the current CPM from the device first.</param>
    /// <param name="cancellationToken"></param>
    /// <remarks>
    /// This reads <c>GETCFG</c> on every call. To convert many values, read the configuration once and call
    /// <see cref="GmcCalibration.ToMicroSievertsPerHour"/> directly with <see cref="GmcConfig.Calibration"/>.
    /// The conversion uses the device's stored points rather than a hardcoded sensitivity, so it follows
    /// whatever calibration the unit actually holds.
    /// </remarks>
    /// <exception cref="NotSupportedException">The device configuration holds no usable calibration points.</exception>
    public async Task<double> GetMicroSievertsPerHourAsync(int? cpm = null, CancellationToken cancellationToken = default)
    {
        var countsPerMinute = cpm ?? await GetCpmAsync(cancellationToken);
        var config = await GetConfigAsync(cancellationToken);
        return GmcCalibration.ToMicroSievertsPerHour(countsPerMinute, config.Calibration);
    }

    /// <summary>Reads and parses the device's 256-byte configuration blob. See <see cref="GmcConfigParser"/> for what's decoded.</summary>
    public async Task<GmcConfig> GetConfigAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => GmcConfigParser.Parse(_connection.Command("GETCFG", 256)), cancellationToken);

    public async Task<byte[]> SendRawAsync(string command, int expectedBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
        return await ExecuteAsync(() => _connection.Command(command.Trim('<', '>'), expectedBytes), cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await Task.Run(action, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task ExecuteAsync(Action action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await Task.Run(action, cancellationToken); }
        finally { _gate.Release(); }
    }

    private void TryDisableHeartbeat()
    {
        if (!_connection.IsOpen) return;
        // Best-effort cleanup: only swallow the failure modes we expect from writing to a port that may be
        // mid-close or unresponsive. Anything else (a real bug) should still surface.
        try { _connection.Send("HEARTBEAT0"); _connection.Clear(); }
        catch (TimeoutException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        TryDisableHeartbeat();
        _gate.Dispose();
        _connection.Dispose();
    }
}
