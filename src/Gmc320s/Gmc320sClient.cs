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
    public async Task PowerOffAsync(CancellationToken cancellationToken = default)
        => await ExecuteAsync(() => _connection.Send("POWEROFF"), cancellationToken);

    /// <summary>Powers the device on (firmware 5.71+). The device does not acknowledge this command.</summary>
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
