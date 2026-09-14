using System.IO.Ports;
using System.Threading;

namespace Gmc320s;

public sealed class Gmc320sConnection : IDisposable
{
    private readonly SerialPort _port;
    private readonly object _sync = new();

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    public Gmc320sConnection(string portName, int baudRate = 115200, int readTimeoutMs = 5000, int writeTimeoutMs = 5000)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false
        };
    }

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
}
