using Xunit;

namespace Gmc320s.Tests;

/// <summary>
/// Covers locating the log's write pointer by bisection and reading the newest readings back from it.
/// The synthetic logs here deliberately use <em>variable</em> batch lengths - including zero-reading batches,
/// which real hardware produces when the device is restarted twice in quick succession - so that nothing
/// under test can quietly depend on the ~180-reading cadence seen between natural timestamp anchors.
/// </summary>
public class GmcRecentHistoryTests
{
    /// <summary>Builds a log of consecutive [timestamp][marker][readings...] batches, one per requested reading count.</summary>
    private static byte[] BuildLog(params int[] batchReadingCounts)
    {
        var bytes = new List<byte>();
        var clock = new DateTime(2026, 9, 14, 10, 0, 0);

        foreach (var count in batchReadingCounts)
        {
            bytes.AddRange(new byte[]
            {
                0x55, 0xAA, 0x00,
                (byte)(clock.Year - 2000), (byte)clock.Month, (byte)clock.Day,
                (byte)clock.Hour, (byte)clock.Minute, (byte)clock.Second
            });
            bytes.AddRange(new byte[] { 0x55, 0xAA, 0x01 });

            // 0-2 keeps the values in the range real hardware logs, and never produces a false 55 AA pair.
            for (var i = 0; i < count; i++) bytes.Add((byte)(i % 3));

            clock = clock.AddSeconds(count + 1);
        }

        return bytes.ToArray();
    }

    private static Gmc320sClient ClientOver(byte[] flashImage) =>
        new(new Gmc320sConnection(new FakeSerialPort { FlashImage = flashImage }));

    [Fact]
    public async Task FindHistoryEndFastAsync_AgreesWithTheLinearScan()
    {
        var log = BuildLog(180, 141, 37);
        using var client = ClientOver(log);

        var bisected = await client.FindHistoryEndFastAsync(maxAddress: 8192);
        var linear = await client.FindHistoryEndAsync(maxAddress: 8192);

        Assert.Equal(log.Length, bisected);
        Assert.Equal(log.Length, linear);
    }

    [Fact]
    public async Task FindHistoryEndFastAsync_ReturnsNullWhenTailIsNotErased()
    {
        // A fully written range has no erased tail, so the "data then erased" invariant bisection needs
        // doesn't hold - this is the full/wrapped-buffer guard.
        var full = new byte[2048];
        Array.Fill(full, (byte)1);
        using var client = ClientOver(full);

        Assert.Null(await client.FindHistoryEndFastAsync(maxAddress: 2048));
    }

    [Fact]
    public async Task FindHistoryEndFastAsync_HandlesAnEmptyLog()
    {
        using var client = ClientOver(Array.Empty<byte>());

        Assert.Equal(0, await client.FindHistoryEndFastAsync(maxAddress: 8192));
    }

    [Fact]
    public async Task GetRecentHistoryAsync_ReturnsTheNewestReadingsAcrossVariableLengthBatches()
    {
        // 180 natural, then a short 141 batch, then two restarts back-to-back with no readings between
        // them, then 37. Requesting 50 must span the final batch, both empty batches, and part of the 141.
        var log = BuildLog(180, 141, 0, 0, 37);
        using var client = ClientOver(log);

        var recent = await client.GetRecentHistoryAsync(50, maxAddress: 8192);

        Assert.Equal(log.Length, recent.HistoryEndAddress);
        Assert.Equal(50, recent.Entries.Count(e => e is GmcHistoryReading));

        // The slice starts at the 50th-from-last reading and ends at the newest entry.
        Assert.IsType<GmcHistoryReading>(recent.Entries[0]);
        Assert.IsType<GmcHistoryReading>(recent.Entries[^1]);

        // The three anchor pairs bracketing the two restarts fall inside the slice and survive intact.
        Assert.Equal(3, recent.Entries.Count(e => e is GmcHistoryTimestamp));
        Assert.Equal(3, recent.Entries.Count(e => e is GmcHistoryMarker));

        var timestamps = recent.Entries.OfType<GmcHistoryTimestamp>().Select(t => t.Timestamp).ToList();
        Assert.Equal(timestamps.OrderBy(t => t), timestamps);
    }

    [Fact]
    public async Task GetRecentHistoryAsync_ReadingsMatchTheTailOfTheWholeLog()
    {
        var log = BuildLog(200, 0, 90);
        using var client = ClientOver(log);

        var expected = GmcHistoryParser.Parse(log).Entries
            .OfType<GmcHistoryReading>()
            .TakeLast(120)
            .Select(r => r.Count);

        var recent = await client.GetRecentHistoryAsync(120, maxAddress: 8192);

        Assert.Equal(expected, recent.Entries.OfType<GmcHistoryReading>().Select(r => r.Count));
    }

    [Fact]
    public async Task GetRecentHistoryAsync_GrowsTheWindowBeyondOneRead()
    {
        // ~6000 readings is well past the 4096-byte SPIR limit, so satisfying this needs several reads
        // stitched together before the requested count is met.
        var log = BuildLog(2000, 2000, 2000);
        using var client = ClientOver(log);

        var recent = await client.GetRecentHistoryAsync(5000, maxAddress: 0x4000);

        Assert.Equal(5000, recent.Entries.Count(e => e is GmcHistoryReading));
        Assert.True(recent.WindowStartAddress < log.Length - 4096,
            "The window should have been extended backward past a single 4096-byte read.");
    }

    [Fact]
    public async Task GetRecentHistoryAsync_ReturnsWhatExistsWhenTheLogIsShorterThanRequested()
    {
        var log = BuildLog(20);
        using var client = ClientOver(log);

        var recent = await client.GetRecentHistoryAsync(500, maxAddress: 8192);

        Assert.Equal(20, recent.Entries.Count(e => e is GmcHistoryReading));
        Assert.Equal(0, recent.WindowStartAddress);
    }

    [Fact]
    public async Task GetRecentHistoryAsync_ThrowsWhenTheWritePointerCannotBeFound()
    {
        var full = new byte[2048];
        Array.Fill(full, (byte)1);
        using var client = ClientOver(full);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetRecentHistoryAsync(10, maxAddress: 2048));
    }

    [Fact]
    public async Task GetRecentHistoryAsync_AcceptsAKnownWritePointerWithoutProbing()
    {
        var log = BuildLog(100);
        var port = new FakeSerialPort { FlashImage = log };
        using var client = new Gmc320sClient(new Gmc320sConnection(port));

        var recent = await client.GetRecentHistoryAsync(10, historyEnd: log.Length);

        Assert.Equal(10, recent.Entries.Count(e => e is GmcHistoryReading));
        // One SPIR read for the data; no bisection probes, since the write pointer was supplied.
        Assert.Single(port.WrittenFrames, f => f.Length > 5 && f[1] == 'S' && f[2] == 'P');
    }
}
