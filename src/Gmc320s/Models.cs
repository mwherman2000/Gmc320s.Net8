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
public sealed record GmcOrientation(short X, short Y, short Z)
{
    /// <summary>Raw counts per g. The sensor is 12-bit left-shifted into 16 bits, so ±2 g spans the full range.</summary>
    public const double CountsPerG = 16384.0;

    /// <summary>
    /// Default tolerance for <see cref="IsStable"/>. Deliberately loose: the axes are unevenly trimmed - Y's
    /// gain is ~4% low - so a genuinely motionless device reads as little as 0.96 g when Y points down.
    /// Anything tighter would reject real readings depending only on which way up the device happens to be.
    /// </summary>
    public const double DefaultStabilityTolerance = 0.10;

    /// <summary>Acceleration along X in g.</summary>
    public double XG => X / CountsPerG;

    /// <summary>Acceleration along Y in g.</summary>
    public double YG => Y / CountsPerG;

    /// <summary>Acceleration along Z in g.</summary>
    public double ZG => Z / CountsPerG;

    /// <summary>
    /// Length of the acceleration vector in g. A motionless device reads 1.0 whatever its orientation, since
    /// tilting redistributes gravity between the axes without changing its total. Departures measure motion:
    /// below 1.0 the device is accelerating downward (0.0 would be free fall), above 1.0 it is being
    /// accelerated or arrested.
    /// </summary>
    public double Magnitude => Math.Sqrt(XG * XG + YG * YG + ZG * ZG);

    /// <summary>
    /// Whether <see cref="Magnitude"/> is within <see cref="DefaultStabilityTolerance"/> of 1 g. An
    /// accelerometer cannot distinguish gravity from acceleration, so this is a prerequisite for reading the
    /// axes as an orientation.
    /// </summary>
    /// <remarks>
    /// <b>Necessary but not sufficient, and the failure rate is not small.</b> A moving device sweeps through
    /// 1 g magnitude twice per oscillation, so samples taken at those crossings look perfectly still. In a
    /// 100-sample capture of a device being shaken hard enough to saturate the sensor, <b>10 samples</b>
    /// satisfied this property. Polling once and trusting the result would therefore have had roughly a one
    /// in ten chance of reading violent motion as a valid orientation. This property reliably rejects obvious
    /// motion but cannot confirm stillness on its own; to trust the axes, require several consecutive samples
    /// to agree in direction as well as magnitude, which is what
    /// <see cref="Gmc320sClient.GetStableOrientationAsync"/> does.
    /// </remarks>
    public bool IsStable => IsStableWithin(DefaultStabilityTolerance);

    /// <summary>As <see cref="IsStable"/>, with a caller-supplied tolerance in g.</summary>
    public bool IsStableWithin(double toleranceG) => Math.Abs(Magnitude - 1.0) <= toleranceG;
}
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
