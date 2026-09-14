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

    public async Task<int> GetCpmAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => BinaryPrimitives.ReadUInt16BigEndian(_connection.Command("GETCPM", 2)), cancellationToken);

    public async Task<double> GetVoltageAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Command("GETVOLT", 1)[0] / 10.0, cancellationToken);

    /// <remarks>
    /// A reading of exactly 85.0 °C is almost certainly not real: 85 °C is the classic power-on-reset default
    /// of common digital temperature sensors, returned when the sensor is read before it has completed a
    /// conversion. It shows up on this device shortly after a reset or power cycle and gives way to plausible
    /// values once it has settled, so treat 85.0 as "not ready yet" rather than as a measurement.
    /// </remarks>
    public async Task<double> GetTemperatureCelsiusAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() =>
        {
            var b = _connection.Command("GETTEMP", 4);
            var sign = b[2] == 0 ? 1 : -1;
            return sign * (b[0] + b[1] / 10.0);
        }, cancellationToken);

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
    /// Despite the command name, the values behave like an accelerometer rather than a rate gyroscope: on a
    /// device lying flat, Z sits near -16384 while X and Y stay near zero, which reads as -1g on Z (gravity)
    /// at a scale of roughly 16384 counts per g. That is why Z looks like a suspiciously round constant.
    /// </remarks>
    public async Task<GmcGyro> GetGyroAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() =>
        {
            var b = _connection.Command("GETGYRO", 7);
            return new GmcGyro(BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(0, 2)), BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(2, 2)), BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(4, 2)));
        }, cancellationToken);

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

    public async Task<GmcReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var cpm = await GetCpmAsync(cancellationToken);
        return new GmcReading(DateTimeOffset.UtcNow, cpm);
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

    /// <summary>Converts CPM to µSv/h using three calibration points from the device configuration.</summary>
    /// <remarks>This is best-effort because the device firmware/configuration format is not officially stable.</remarks>
    public async Task<double> GetMicroSievertsPerHourAsync(int? cpm = null, CancellationToken cancellationToken = default)
    {
        // The GMC configuration calibration bytes are firmware-sensitive. This method is deliberately not
        // implemented as a hidden formula; callers should supply their calibration model once verified.
        throw new NotSupportedException("µSv/h conversion is firmware/configuration dependent. Use CPM directly or supply a verified calibration model.");
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
