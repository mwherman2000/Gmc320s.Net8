namespace Gmc320s;

public sealed class Gmc320sConnection : IDisposable
{
    private readonly ISerialPort _port;
    private readonly object _sync = new();

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    public Gmc320sConnection(string portName, int baudRate = 115200, int readTimeoutMs = 5000, int writeTimeoutMs = 5000)
        : this(new SystemSerialPort(portName, baudRate, readTimeoutMs, writeTimeoutMs))
    {
    }

    /// <summary>Test seam: build a connection over a fake <see cref="ISerialPort"/> instead of a real one.</summary>
    internal Gmc320sConnection(ISerialPort port) => _port = port;

    public void Open() => _port.Open();
    public void Close() { if (_port.IsOpen) _port.Close(); }
    public void Dispose() { Close(); _port.Dispose(); }

    private const int MaxAttempts = 3;
    private const int RetryDelayMs = 100;

    internal byte[] Command(string command, int expectedBytes, bool clearBefore = true)
        => Execute(System.Text.Encoding.ASCII.GetBytes($"<{command}>>"), command, expectedBytes, clearBefore);

    /// <summary>Sends a command with a raw binary payload embedded before the closing "&gt;&gt;", e.g. SETDATETIME's YY/MM/DD/HH/MM/SS bytes.</summary>
    internal byte[] CommandWithPayload(string command, byte[] payload, int expectedBytes, bool clearBefore = true)
    {
        var prefix = System.Text.Encoding.ASCII.GetBytes($"<{command}");
        var suffix = System.Text.Encoding.ASCII.GetBytes(">>");
        var bytes = new byte[prefix.Length + payload.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, bytes, prefix.Length, payload.Length);
        Buffer.BlockCopy(suffix, 0, bytes, prefix.Length + payload.Length, suffix.Length);
        return Execute(bytes, command, expectedBytes, clearBefore);
    }

    private byte[] Execute(byte[] bytes, string command, int expectedBytes, bool clearBefore)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("GMC-320S serial port is not open.");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                lock (_sync)
                {
                    if (clearBefore) _port.DiscardInBuffer();
                    _port.Write(bytes, 0, bytes.Length);
                    return ReadExactly(expectedBytes);
                }
            }
            catch (TimeoutException) when (attempt < MaxAttempts)
            {
                lock (_sync) { _port.DiscardInBuffer(); }
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new TimeoutException($"Timed out executing '{command}' after {MaxAttempts} attempts.");
    }

    internal void Send(string command)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("GMC-320S serial port is not open.");
        var bytes = System.Text.Encoding.ASCII.GetBytes($"<{command}>>");
        lock (_sync) { _port.Write(bytes, 0, bytes.Length); }
    }

    internal void Clear() { lock (_sync) { _port.DiscardInBuffer(); _port.DiscardOutBuffer(); } }

    internal byte[] ReadExactly(int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = _port.Read(result, offset, count - offset);
            if (n <= 0) throw new TimeoutException($"Timed out reading {count} bytes from GMC-320S; received {offset}.");
            offset += n;
        }
        return result;
    }

    /// <summary>
    /// Reads a fixed-size frame from the live heartbeat stream, retrying (with an input-buffer flush between
    /// attempts) if a read times out, the same way <see cref="Command"/> smooths over occasional non-responses.
    /// </summary>
    internal byte[] ReadHeartbeatFrame(int count)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                lock (_sync) { return ReadExactly(count); }
            }
            catch (TimeoutException) when (attempt < MaxAttempts)
            {
                lock (_sync) { _port.DiscardInBuffer(); }
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new TimeoutException($"Timed out reading {count} live CPS bytes after {MaxAttempts} attempts.");
    }
}
