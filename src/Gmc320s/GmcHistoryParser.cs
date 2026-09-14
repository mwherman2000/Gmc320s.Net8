namespace Gmc320s;

/// <summary>
/// Best-effort decoder for the byte stream returned by <see cref="Gmc320sClient.GetHistoryAsync"/>.
/// </summary>
/// <remarks>
/// GQ has never published a stable spec for the history log format. This decoder is based on observation of
/// real GMC-320S hardware (see PROTOCOL-NOTES.md and README.md): a repeating <c>55 AA</c> sync prefix, where
/// <c>55 AA 00</c> is followed by a 6-byte YY/MM/DD/HH/MM/SS timestamp (the same layout
/// <c>GETDATETIME</c>/<c>SETDATETIME</c> use), and <c>55 AA 01</c> carries no payload. Any other marker type
/// has an unknown payload length, so parsing deliberately stops rather than guess how many bytes to skip -
/// continuing past an unrecognized marker risks misinterpreting its payload as unrelated entries. Everything
/// from the stopping point onward (including a recognized marker cut short by running out of buffer) is
/// returned as <see cref="GmcHistoryParseResult.UnparsedRemainder"/> rather than guessed at.
/// <para>
/// Note that the format is self-synchronizing: readings are single bytes, so a wrong starting phase cannot
/// mis-split them and resolves itself at the next <c>55 AA</c> anchor. That is what makes
/// <see cref="ParseResynced"/> possible, and it means the number of readings between anchors is NOT a
/// reliable constant - a device restart writes an extra anchor, and an interrupted session ends a batch early.
/// Never infer entry positions from an assumed batch length.
/// </para>
/// </remarks>
public static class GmcHistoryParser
{
    private const byte Sync1 = 0x55;
    private const byte Sync2 = 0xAA;

    /// <summary>Parses from the start of <paramref name="data"/>, which must begin on an entry boundary (e.g. flash address 0).</summary>
    public static GmcHistoryParseResult Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var entries = new List<GmcHistoryEntry>();
        var offset = 0;
        while (TryReadEntry(data, offset, out var entry, out var size))
        {
            entries.Add(entry);
            offset += size;
        }

        return new GmcHistoryParseResult(entries, data[offset..]);
    }

    /// <summary>
    /// Parses a window read from an arbitrary flash address by discarding the leading bytes before the first
    /// recognizable <c>55 AA</c> anchor, since a window that starts mid-log may begin partway through an entry
    /// whose remaining payload bytes would otherwise be misread as readings.
    /// </summary>
    /// <remarks>
    /// A plain reading of 85 (<c>0x55</c>) followed by one of 170 (<c>0xAA</c>) can imitate an anchor. The
    /// marker-type and timestamp-range validation here rejects most such pairs, and because the format is
    /// self-synchronizing a false lock costs at most a few misread bytes before the next real anchor corrects
    /// it - but it is not impossible, so treat the first few entries of a resynced window as the least certain.
    /// </remarks>
    public static GmcHistoryResyncResult ParseResynced(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        for (var start = 0; start < data.Length; start++)
        {
            if (data[start] != Sync1) continue;
            if (start + 1 >= data.Length || data[start + 1] != Sync2) continue;
            if (!TryReadEntry(data, start, out _, out _)) continue;

            var parsed = Parse(data[start..]);
            return new GmcHistoryResyncResult(parsed.Entries, start, parsed.UnparsedRemainder);
        }

        return new GmcHistoryResyncResult(Array.Empty<GmcHistoryEntry>(), -1, data);
    }

    private static bool TryReadEntry(byte[] data, int offset, out GmcHistoryEntry entry, out int size)
    {
        entry = null!;
        size = 0;
        if (offset >= data.Length) return false;

        if (data[offset] == Sync1)
        {
            // A trailing 0x55 is ambiguous - a reading of 85, or the first byte of an anchor the window cut
            // off - so leave it unparsed rather than emit a possibly-bogus reading.
            if (offset + 1 >= data.Length) return false;

            if (data[offset + 1] == Sync2)
            {
                if (offset + 2 >= data.Length) return false;

                switch (data[offset + 2])
                {
                    case 0x00:
                        if (offset + 8 >= data.Length) return false;
                        if (!TryReadTimestamp(data, offset + 3, out var timestamp)) return false;
                        entry = new GmcHistoryTimestamp(timestamp);
                        size = 9;
                        return true;

                    case 0x01:
                        entry = new GmcHistoryMarker(0x01);
                        size = 3;
                        return true;

                    default:
                        return false;
                }
            }
        }

        entry = new GmcHistoryReading(data[offset]);
        size = 1;
        return true;
    }

    private static bool TryReadTimestamp(byte[] data, int offset, out DateTime timestamp)
    {
        timestamp = default;

        int year = data[offset], month = data[offset + 1], day = data[offset + 2];
        int hour = data[offset + 3], minute = data[offset + 4], second = data[offset + 5];

        if (year > 99 || month is < 1 or > 12 || day < 1 || hour > 23 || minute > 59 || second > 59) return false;
        if (day > DateTime.DaysInMonth(2000 + year, month)) return false;

        timestamp = new DateTime(2000 + year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return true;
    }
}
