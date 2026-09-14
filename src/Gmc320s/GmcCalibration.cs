namespace Gmc320s;

/// <summary>
/// Converts CPM to µSv/h using the calibration points stored in the device's own configuration, rather than a
/// hardcoded sensitivity figure.
/// </summary>
/// <remarks>
/// The device stores three CPM-to-µSv/h points. This interpolates linearly between them, treating (0 CPM,
/// 0 µSv/h) as an implicit origin so that low counts can't extrapolate to a negative dose rate, and
/// extrapolating along the final segment above the highest point. On a GMC-320S the three stored points are
/// collinear through the origin, so every reasonable scheme agrees there - the piecewise handling exists for
/// firmwares whose points are not collinear.
/// </remarks>
public static class GmcCalibration
{
    /// <summary>Converts a CPM value to µSv/h using <paramref name="calibration"/>.</summary>
    /// <exception cref="NotSupportedException">The calibration table holds no usable points.</exception>
    public static double ToMicroSievertsPerHour(int countsPerMinute, IReadOnlyList<GmcCalibrationPoint> calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (countsPerMinute < 0) throw new ArgumentOutOfRangeException(nameof(countsPerMinute), "Must be non-negative.");

        var points = calibration
            .Where(p => p.Cpm > 0 && double.IsFinite(p.MicroSievertsPerHour) && p.MicroSievertsPerHour > 0)
            .OrderBy(p => p.Cpm)
            .ToList();

        if (points.Count == 0)
            throw new NotSupportedException(
                "The device configuration holds no usable CPM-to-µSv/h calibration points, so CPM cannot be converted. " +
                "Use CPM directly, or supply a verified calibration model.");

        // Zero counts means zero dose rate, which also keeps extrapolation below the lowest point sane.
        if (points[0].Cpm > 0) points.Insert(0, new GmcCalibrationPoint(0, 0));

        for (var i = 1; i < points.Count; i++)
            if (countsPerMinute <= points[i].Cpm)
                return Interpolate(points[i - 1], points[i], countsPerMinute);

        return Interpolate(points[^2], points[^1], countsPerMinute);
    }

    /// <summary>
    /// Converts a CPM value to µSv/h, returning <see langword="false"/> instead of throwing when the
    /// calibration table holds no usable points - for callers where a missing calibration is an expected
    /// condition rather than an error.
    /// </summary>
    public static bool TryToMicroSievertsPerHour(int countsPerMinute, IReadOnlyList<GmcCalibrationPoint> calibration, out double microSievertsPerHour)
    {
        try
        {
            microSievertsPerHour = ToMicroSievertsPerHour(countsPerMinute, calibration);
            return true;
        }
        catch (NotSupportedException)
        {
            microSievertsPerHour = 0;
            return false;
        }
    }

    private static double Interpolate(GmcCalibrationPoint low, GmcCalibrationPoint high, int countsPerMinute)
    {
        if (high.Cpm == low.Cpm) return high.MicroSievertsPerHour;

        var slope = (high.MicroSievertsPerHour - low.MicroSievertsPerHour) / (high.Cpm - low.Cpm);
        return Math.Max(0, low.MicroSievertsPerHour + (countsPerMinute - low.Cpm) * slope);
    }
}
