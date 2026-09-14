using Xunit;

namespace Gmc320s.Tests;

public class Gmc320sClientTests
{
    [Fact]
    public async Task GetVersionAsync_TrimsPaddingFromFixedWidthReply()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETVER", ReplyPayload = "GMC-320Re 5.71"u8.ToArray() };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal("GMC-320Re 5.71", await client.GetVersionAsync());
    }

    [Fact]
    public async Task SetDateTimeAsync_EncodesRawBcdBytesNotAscii()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<SETDATETIME", ReplyPayload = new byte[] { 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await client.SetDateTimeAsync(new DateTime(2026, 9, 13, 14, 5, 30));

        var frame = Assert.Single(port.WrittenFrames, f => f.Length > 2 && f[1] == 'S');
        var expected = new byte[] { (byte)'<' }
            .Concat("SETDATETIME"u8.ToArray())
            .Concat(new byte[] { 26, 9, 13, 14, 5, 30 })
            .Concat(new byte[] { (byte)'>', (byte)'>' })
            .ToArray();
        Assert.Equal(expected, frame);
    }

    [Fact]
    public async Task SetDateTimeAsync_ThrowsWhenDeviceRejectsIt()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<SETDATETIME", ReplyPayload = new byte[] { 0x00 } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await Assert.ThrowsAsync<IOException>(() => client.SetDateTimeAsync(DateTime.Now));
    }

    [Fact]
    public async Task GetHistoryAsync_EncodesAddressAndLengthMinusOne()
    {
        const int length = 10;
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<SPIR", ReplyPayload = new byte[length] };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await client.GetHistoryAsync(0x010203, length);

        var frame = Assert.Single(port.WrittenFrames, f => f.Length > 2 && f[1] == 'S');
        var expected = new byte[] { (byte)'<' }
            .Concat("SPIR"u8.ToArray())
            .Concat(new byte[] { 0x01, 0x02, 0x03, 0x00, length - 1 })
            .Concat(new byte[] { (byte)'>', (byte)'>' })
            .ToArray();
        Assert.Equal(expected, frame);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task GetHistoryAsync_RejectsInvalidLength(int length)
    {
        using var client = new Gmc320sClient(new Gmc320sConnection(new FakeSerialPort()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetHistoryAsync(0, length));
    }

    [Fact]
    public async Task ReadCpsAsync_RecoversFromOneDroppedHeartbeatFrame()
    {
        var port = new FakeSerialPort();
        port.EnqueueTimeout();
        port.EnqueueReadBytes(new byte[] { 0x00, 0x05 });
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var values = new List<int>();
        await foreach (var cps in client.ReadCpsAsync(1))
            values.Add(cps);

        Assert.Equal(new[] { 5 }, values);
    }

    [Fact]
    public async Task ReadCpsAsync_RejectsNegativeCount()
    {
        using var client = new Gmc320sClient(new Gmc320sConnection(new FakeSerialPort()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in client.ReadCpsAsync(-1)) { }
        });
    }
}
