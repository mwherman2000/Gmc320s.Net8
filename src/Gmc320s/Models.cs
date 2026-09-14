namespace Gmc320s;

public sealed record GmcReading(DateTimeOffset Timestamp, int CountsPerMinute, double? MicroSievertsPerHour = null);
public sealed record GmcDeviceInfo(string? Version, string? SerialNumber);
public sealed record GmcGyro(short X, short Y, short Z);
public sealed record GmcConfig(IReadOnlyDictionary<string, object?> Values, byte[] Raw);

/// <summary>A decoded entry from a <c>SPIR</c> history read. See <see cref="GmcHistoryParser"/>.</summary>
public abstract record GmcHistoryEntry;

/// <summary>A plain one-byte CPM sample.</summary>
public sealed record GmcHistoryReading(byte Cpm) : GmcHistoryEntry;

/// <summary>A <c>55 AA 00</c> marker followed by a YY/MM/DD/HH/MM/SS timestamp.</summary>
public sealed record GmcHistoryTimestamp(DateTime Timestamp) : GmcHistoryEntry;

/// <summary>A recognized <c>55 AA</c> marker with no known payload (currently only type <c>0x01</c>).</summary>
public sealed record GmcHistoryMarker(byte MarkerType) : GmcHistoryEntry;

/// <summary>Result of parsing a history byte buffer: whatever was confidently decoded, plus everything after the point parsing had to stop.</summary>
public sealed record GmcHistoryParseResult(IReadOnlyList<GmcHistoryEntry> Entries, byte[] UnparsedRemainder);
