using Xunit;

namespace Gmc320s.Tests;

public class GmcHistoryParserTests
{
    [Fact]
    public void Parse_DecodesRealObservedSample()
    {
        // 55AA001A07150C272355AA0155AA001A, captured from a real GMC-320S at history address 0.
        var data = Convert.FromHexString("55AA001A07150C272355AA0155AA001A");

        var result = GmcHistoryParser.Parse(data);

        Assert.Equal(2, result.Entries.Count);

        var first = Assert.IsType<GmcHistoryTimestamp>(result.Entries[0]);
        Assert.Equal(new DateTime(2026, 7, 21, 12, 39, 35), first.Timestamp);

        var marker = Assert.IsType<GmcHistoryMarker>(result.Entries[1]);
        Assert.Equal((byte)0x01, marker.MarkerType);

        // The trailing "55 AA 00 1A" is a truncated timestamp marker - not enough bytes remain for its
        // 6-byte payload, so it must be left as unparsed remainder rather than guessed at.
        Assert.Equal(new byte[] { 0x55, 0xAA, 0x00, 0x1A }, result.UnparsedRemainder);
    }

    [Fact]
    public void Parse_TreatsPlainBytesAsReadings()
    {
        var result = GmcHistoryParser.Parse(new byte[] { 1, 2, 3 });

        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.IsType<GmcHistoryReading>(e));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Entries.Cast<GmcHistoryReading>().Select(r => r.Cpm));
        Assert.Empty(result.UnparsedRemainder);
    }

    [Fact]
    public void Parse_StopsAtUnrecognizedMarkerType()
    {
        var data = new byte[] { 1, 2, 0x55, 0xAA, 0x02, 9, 9 };

        var result = GmcHistoryParser.Parse(data);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(new byte[] { 0x55, 0xAA, 0x02, 9, 9 }, result.UnparsedRemainder);
    }

    [Fact]
    public void Parse_EmptyBufferYieldsNoEntries()
    {
        var result = GmcHistoryParser.Parse(Array.Empty<byte>());

        Assert.Empty(result.Entries);
        Assert.Empty(result.UnparsedRemainder);
    }
}
