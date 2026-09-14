namespace Gmc320s;

public sealed record GmcReading(DateTimeOffset Timestamp, int CountsPerMinute, double? MicroSievertsPerHour = null);
public sealed record GmcDeviceInfo(string? Version, string? SerialNumber);
/// <summary>
/// A <c>GETGYRO</c> reading. Named for what it measures rather than for the command: despite that name these
/// are accelerometer axes, not rotation rates, so a stationary device reports the gravity vector (1 g total)
/// rather than zero - which makes it an orientation sensor in practice. Scale is <b>16384 counts per g</b>;
/// divide by 16384.0 for g. The sensor is 12-bit left-shifted into 16 bits, so every value is a multiple of
/// 16. See <see cref="Gmc320sClient.GetOrientationAsync"/> and PROTOCOL-NOTES.md.
/// </summary>
public sealed record GmcOrientation(short X, short Y, short Z);
/// <summary>One CPM-to-µSv/h calibration point read out of the device configuration.</summary>
public sealed record GmcCalibrationPoint(int Cpm, double MicroSievertsPerHour);

/// <param name="Values">Named leading configuration bytes, exposed raw. See <see cref="GmcConfigParser"/>.</param>
/// <param name="Calibration">CPM-to-µSv/h calibration points, empty if the table could not be read.</param>
/// <param name="Raw">The full 256-byte configuration blob.</param>
public sealed record GmcConfig(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<GmcCalibrationPoint> Calibration,
    byte[] Raw);

/// <summary>A decoded entry from a <c>SPIR</c> history read. See <see cref="GmcHistoryParser"/>.</summary>
public abstract record GmcHistoryEntry;

/// <summary>
/// A plain one-byte log sample. Named <c>Count</c> rather than <c>Cpm</c>: real-hardware observation shows
/// these recur about once per second between periodic timestamp anchors (see <see cref="GmcHistoryTimestamp"/>
/// and PROTOCOL-NOTES.md), which is far more consistent with a per-second CPS value than a per-minute CPM
/// value - but that's an inference from timing, not a documented fact.
/// </summary>
public sealed record GmcHistoryReading(byte Count) : GmcHistoryEntry;

/// <summary>A <c>55 AA 00</c> marker followed by a YY/MM/DD/HH/MM/SS timestamp.</summary>
public sealed record GmcHistoryTimestamp(DateTime Timestamp) : GmcHistoryEntry;

/// <summary>A recognized <c>55 AA</c> marker with no known payload (currently only type <c>0x01</c>).</summary>
public sealed record GmcHistoryMarker(byte MarkerType) : GmcHistoryEntry;

/// <summary>Result of parsing a history byte buffer: whatever was confidently decoded, plus everything after the point parsing had to stop.</summary>
public sealed record GmcHistoryParseResult(IReadOnlyList<GmcHistoryEntry> Entries, byte[] UnparsedRemainder);

/// <summary>
/// Result of parsing a window read from an arbitrary flash address. <paramref name="AnchorOffset"/> is the
/// offset within the window where the first recognizable <c>55 AA</c> anchor was found and parsing began, or
/// -1 if none was found (in which case <paramref name="Entries"/> is empty).
/// </summary>
public sealed record GmcHistoryResyncResult(IReadOnlyList<GmcHistoryEntry> Entries, int AnchorOffset, byte[] UnparsedRemainder);

/// <summary>
/// The tail of the history log. <paramref name="Entries"/> ends at the newest entry written, and any
/// timestamps/markers the device wrote within that span are included alongside the readings.
/// </summary>
/// <param name="Entries">Chronological entries, ending at the most recent one.</param>
/// <param name="WindowStartAddress">Flash address the containing window was read from - pass a lower end address to page further back.</param>
/// <param name="HistoryEndAddress">The log's write pointer: the first erased-flash address after the newest entry.</param>
public sealed record GmcRecentHistory(IReadOnlyList<GmcHistoryEntry> Entries, int WindowStartAddress, int HistoryEndAddress);
