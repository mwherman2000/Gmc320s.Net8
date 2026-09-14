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
    public async Task FindHistoryEndAsync_FindsFirstLongRunOfErasedBytes()
    {
        var flash = new byte[2048];
        for (var i = 0; i < 100; i++) flash[i] = (byte)(i % 50 + 1); // "real" log data, never 0xFF
        for (var i = 100; i < flash.Length; i++) flash[i] = 0xFF; // erased flash

        var port = new FakeSerialPort { FlashImage = flash };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var end = await client.FindHistoryEndAsync(maxAddress: flash.Length, chunkSize: 256, minErasedRunLength: 64);

        Assert.Equal(100, end);
    }

    [Fact]
    public async Task FindHistoryEndAsync_ReturnsNullWhenNoGapFound()
    {
        var flash = new byte[512];
        for (var i = 0; i < flash.Length; i++) flash[i] = (byte)(i % 50 + 1);

        var port = new FakeSerialPort { FlashImage = flash };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var end = await client.FindHistoryEndAsync(maxAddress: flash.Length, chunkSize: 128, minErasedRunLength: 64);

        Assert.Null(end);
    }

    [Fact]
    public async Task FindHistoryEndAsync_IgnoresMarkerBytesShorterThanThreshold()
    {
        var flash = new byte[512];
        for (var i = 0; i < flash.Length; i++) flash[i] = 1;
        flash[50] = 0xFF; flash[51] = 0xFF; flash[52] = 0xFF; // short marker-like run, below threshold
        for (var i = 200; i < flash.Length; i++) flash[i] = 0xFF; // genuine gap

        var port = new FakeSerialPort { FlashImage = flash };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var end = await client.FindHistoryEndAsync(maxAddress: flash.Length, chunkSize: 128, minErasedRunLength: 10);

        Assert.Equal(200, end);
    }

    [Fact]
    public async Task FindHistoryEndAsync_CountsErasedRunAcrossChunkBoundary()
    {
        var flash = new byte[256];
        for (var i = 0; i < flash.Length; i++) flash[i] = 1;
        for (var i = 110; i < 150; i++) flash[i] = 0xFF; // straddles the 128-byte chunk boundary

        var port = new FakeSerialPort { FlashImage = flash };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var end = await client.FindHistoryEndAsync(maxAddress: flash.Length, chunkSize: 128, minErasedRunLength: 40);

        Assert.Equal(110, end);
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
