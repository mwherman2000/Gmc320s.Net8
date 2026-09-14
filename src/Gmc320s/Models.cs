namespace Gmc320s;

public sealed record GmcReading(DateTimeOffset Timestamp, int CountsPerMinute, double? MicroSievertsPerHour = null);
public sealed record GmcDeviceInfo(string? Version, string? SerialNumber);
public sealed record GmcGyro(short X, short Y, short Z);
public sealed record GmcConfig(IReadOnlyDictionary<string, object?> Values, byte[] Raw);
