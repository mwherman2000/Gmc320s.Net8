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
    public async Task GetTemperatureCelsiusAsync_DecodesAPositiveReading()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETTEMP", ReplyPayload = new byte[] { 0x1B, 0x06, 0x00, 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal(27.6, await client.GetTemperatureCelsiusAsync(), 3);
    }

    [Fact]
    public async Task GetTemperatureCelsiusAsync_DecodesANegativeReading()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETTEMP", ReplyPayload = new byte[] { 0x05, 0x04, 0x01, 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal(-5.4, await client.GetTemperatureCelsiusAsync(), 3);
    }

    [Fact]
    public async Task GetTemperatureCelsiusAsync_RejectsTheNegativeZeroNotReadyReplyAndReReads()
    {
        // "-0.0 C" - sign flag set over a zero magnitude - is never a real reading. Seen about once in
        // thirty reads on real hardware, with the following read correct.
        var port = new FakeSerialPort
        {
            ReplyTriggerPrefix = "<GETTEMP",
            ReplyPayload = new byte[] { 0x00, 0x00, 0x01, 0xAA },
            SwitchToPayloadAfterMatchingWrites = 1,
            NextReplyPayload = new byte[] { 0x16, 0x00, 0x00, 0xAA }
        };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal(22.0, await client.GetTemperatureCelsiusAsync(), 3);
        Assert.Equal(2, port.WrittenFrames.Count(f => f.Length > 2 && f[1] == 'G' && f[2] == 'E'));
    }

    [Fact]
    public async Task GetTemperatureCelsiusAsync_RejectsTheKnownBad85DegreesAndReReads()
    {
        // 85.0 C is the classic power-on-reset default of digital temperature sensors and is far outside
        // this device's operating range, so it must never reach the caller when a good read is available.
        var port = new FakeSerialPort
        {
            ReplyTriggerPrefix = "<GETTEMP",
            ReplyPayload = new byte[] { 0x55, 0x00, 0x00, 0xAA },
            SwitchToPayloadAfterMatchingWrites = 1,
            NextReplyPayload = new byte[] { 0x16, 0x00, 0x00, 0xAA }
        };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal(22.0, await client.GetTemperatureCelsiusAsync(), 3);
    }

    [Fact]
    public async Task GetTemperatureCelsiusAsync_ThrowsRatherThanReturnAPersistentlyBadReading()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETTEMP", ReplyPayload = new byte[] { 0x55, 0x00, 0x00, 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTemperatureCelsiusAsync());

        Assert.Contains("85.0", ex.Message);
        Assert.Equal(3, port.WrittenFrames.Count(f => f.Length > 2 && f[1] == 'G' && f[2] == 'E'));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0.0)]    // freezing point of nothing in particular, but a valid reading
    [InlineData(0x32, 0x00, 0x00, 50.0)]   // top of the device's specified ambient range
    [InlineData(0x0A, 0x05, 0x01, -10.5)]  // genuine sub-zero reading, sign flag set over a real magnitude
    public async Task GetTemperatureCelsiusAsync_AcceptsPlausibleReadingsIncludingNegatives(byte whole, byte tenths, byte sign, double expected)
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETTEMP", ReplyPayload = new byte[] { whole, tenths, sign, 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        Assert.Equal(expected, await client.GetTemperatureCelsiusAsync(), 3);
        Assert.Single(port.WrittenFrames, f => f.Length > 2 && f[1] == 'G' && f[2] == 'E');
    }

    [Fact]
    public async Task GetTemperatureCelsiusAsync_DoesNotReReadAValidReading()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETTEMP", ReplyPayload = new byte[] { 0x1B, 0x06, 0x00, 0xAA } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await client.GetTemperatureCelsiusAsync();

        Assert.Single(port.WrittenFrames, f => f.Length > 2 && f[1] == 'G' && f[2] == 'E');
    }

    [Fact]
    public async Task GetCpmAsync_ThrottlesBackToBackCallsToTheMinimumGap()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETCPM", ReplyPayload = new byte[] { 0x00, 0x0A } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await client.GetCpmAsync(); // establishes _lastCpmReplyTimestamp; nothing to throttle against yet

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.GetCpmAsync();
        clock.Stop();

        // Generous lower bound: real elapsed time can only overshoot a requested sleep, never undershoot it,
        // so this is not a flakiness risk - only a slow CI machine adding more delay than requested.
        Assert.True(clock.Elapsed.TotalMilliseconds >= Gmc320sClient.MinCpmRequestGapMs * 0.8,
            $"Expected at least ~{Gmc320sClient.MinCpmRequestGapMs} ms, took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task GetCpmAsync_DoesNotThrottleWhenTheGapHasAlreadyElapsed()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETCPM", ReplyPayload = new byte[] { 0x00, 0x0A } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        await client.GetCpmAsync();
        await Task.Delay(Gmc320sClient.MinCpmRequestGapMs + 10);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.GetCpmAsync();
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds < Gmc320sClient.MinCpmRequestGapMs,
            $"Expected a fast call since the gap had already elapsed, took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task GetCpmAsync_DoesNotThrottleTheFirstCallOnAFreshClient()
    {
        var port = new FakeSerialPort { ReplyTriggerPrefix = "<GETCPM", ReplyPayload = new byte[] { 0x00, 0x0A } };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.GetCpmAsync();
        clock.Stop();

        Assert.True(clock.Elapsed.TotalMilliseconds < Gmc320sClient.MinCpmRequestGapMs,
            $"Expected the very first call to be fast, took {clock.Elapsed.TotalMilliseconds:F1} ms");
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
