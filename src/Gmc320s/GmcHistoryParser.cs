namespace Gmc320s;

/// <summary>
/// Best-effort decoder for the byte stream returned by <see cref="Gmc320sClient.GetHistoryAsync"/>.
/// </summary>
/// <remarks>
/// GQ has never published a stable spec for the history log format. This decoder is based on a single
/// observed sample from a real GMC-320S at the start of its log (see PROTOCOL-NOTES.md and README.md for the
/// raw bytes): a repeating <c>55 AA</c> sync prefix, where <c>55 AA 00</c> is followed by a 6-byte
/// YY/MM/DD/HH/MM/SS timestamp (the same layout <c>GETDATETIME</c>/<c>SETDATETIME</c> use), and <c>55 AA 01</c>
/// appears to carry no payload. Any other marker type has an unknown payload length, so this parser
/// deliberately stops rather than guess how many bytes to skip - continuing to scan byte-by-byte past an
/// unrecognized marker risks misinterpreting its payload as unrelated entries. Everything from the stopping
/// point onward (including a recognized marker cut short by running out of buffer) is returned as
/// <see cref="GmcHistoryParseResult.UnparsedRemainder"/> rather than guessed at.
/// </remarks>
public static class GmcHistoryParser
{
    private const byte Sync1 = 0x55;
    private const byte Sync2 = 0xAA;

    public static GmcHistoryParseResult Parse(byte[] data)
    {
        var entries = new List<GmcHistoryEntry>();
        var i = 0;

        while (i < data.Length)
        {
            if (data[i] == Sync1 && i + 2 < data.Length && data[i + 1] == Sync2)
            {
                var markerType = data[i + 2];

                if (markerType == 0x00 && i + 8 < data.Length)
                {
                    entries.Add(new GmcHistoryTimestamp(new DateTime(
                        2000 + data[i + 3], data[i + 4], data[i + 5], data[i + 6], data[i + 7], data[i + 8], DateTimeKind.Unspecified)));
                    i += 9;
                    continue;
                }

                if (markerType == 0x01)
                {
                    entries.Add(new GmcHistoryMarker(markerType));
                    i += 3;
                    continue;
                }

                break; // unrecognized marker type, or a known one truncated by the end of the buffer
            }

            entries.Add(new GmcHistoryReading(data[i]));
            i++;
        }

        return new GmcHistoryParseResult(entries, data[i..]);
    }
}
