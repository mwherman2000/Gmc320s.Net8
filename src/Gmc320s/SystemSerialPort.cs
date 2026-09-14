using System.IO.Ports;

namespace Gmc320s;

/// <summary>Adapts <see cref="System.IO.Ports.SerialPort"/> to <see cref="ISerialPort"/> for production use.</summary>
internal sealed class SystemSerialPort : ISerialPort
{
    private readonly SerialPort _port;

    public SystemSerialPort(string portName, int baudRate, int readTimeoutMs, int writeTimeoutMs)
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

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();
    public void Close() { if (_port.IsOpen) _port.Close(); }
    public void Dispose() => _port.Dispose();

    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void DiscardInBuffer() => _port.DiscardInBuffer();
    public void DiscardOutBuffer() => _port.DiscardOutBuffer();
}
