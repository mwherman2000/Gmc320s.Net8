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
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Entries.Cast<GmcHistoryReading>().Select(r => r.Count));
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

    [Theory]
    [InlineData(0)]   // month 0
    [InlineData(13)]  // month 13
    public void Parse_StopsRatherThanThrowingOnAnImpossibleTimestamp(byte month)
    {
        // Resyncing mid-log means garbage will be fed to the timestamp decoder, so it must reject the
        // marker instead of letting DateTime's constructor throw.
        var data = new byte[] { 1, 0x55, 0xAA, 0x00, 26, month, 21, 12, 39, 35 };

        var result = GmcHistoryParser.Parse(data);

        Assert.Single(result.Entries);
        Assert.Equal(data[1..], result.UnparsedRemainder);
    }

    [Fact]
    public void Parse_LeavesATrailing0x55Unparsed()
    {
        // Ambiguous: a reading of 85, or the first byte of an anchor the read window cut off.
        var result = GmcHistoryParser.Parse(new byte[] { 7, 0x55 });

        Assert.Single(result.Entries);
        Assert.Equal(new byte[] { 0x55 }, result.UnparsedRemainder);
    }

    [Fact]
    public void Parse_Treats0x55NotFollowedBySyncAsAReading()
    {
        var result = GmcHistoryParser.Parse(new byte[] { 0x55, 0x01, 9 });

        Assert.Equal(new byte[] { 0x55, 0x01, 9 }, result.Entries.Cast<GmcHistoryReading>().Select(r => r.Count));
        Assert.Empty(result.UnparsedRemainder);
    }

    [Fact]
    public void ParseResynced_DiscardsThePartialEntryAtTheStartOfTheWindow()
    {
        // A window opening mid-timestamp: the leading 4 bytes are that timestamp's tail, which parsing from
        // offset 0 would misreport as readings of 21, 12, 39, 35.
        var data = Convert.FromHexString("15" + "0C2723" + "55AA01" + "010002" + "55AA001A07150C2724" + "0102");

        var result = GmcHistoryParser.ParseResynced(data);

        Assert.Equal(4, result.AnchorOffset);
        Assert.IsType<GmcHistoryMarker>(result.Entries[0]);
        Assert.Equal(new byte[] { 1, 0, 2 }, result.Entries.Skip(1).Take(3).Cast<GmcHistoryReading>().Select(r => r.Count));
        Assert.Equal(new DateTime(2026, 7, 21, 12, 39, 36), Assert.IsType<GmcHistoryTimestamp>(result.Entries[4]).Timestamp);
    }

    [Fact]
    public void ParseResynced_ReportsNoAnchorWhenTheWindowHasNone()
    {
        var result = GmcHistoryParser.ParseResynced(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(-1, result.AnchorOffset);
        Assert.Empty(result.Entries);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.UnparsedRemainder);
    }
}
