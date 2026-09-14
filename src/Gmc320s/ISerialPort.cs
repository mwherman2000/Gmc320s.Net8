namespace Gmc320s;

/// <summary>The subset of serial port behavior <see cref="Gmc320sConnection"/> depends on, so it can be tested without physical hardware.</summary>
internal interface ISerialPort : IDisposable
{
    string PortName { get; }
    int BaudRate { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
    void DiscardInBuffer();
    void DiscardOutBuffer();
}
