using Xunit;

namespace Gmc320s.Tests;

public class GmcCalibrationTests
{
    /// <summary>The first 32 configuration bytes read from a real GMC-320S (firmware GMC-320SRe 1.1).</summary>
    private const string RealConfigHex =
        "00010103010000640602000020413C140000C842782800004843000000003F00";

    private static byte[] RealConfig()
    {
        var raw = new byte[256];
        Convert.FromHexString(RealConfigHex).CopyTo(raw, 0);
        return raw;
    }

    [Fact]
    public void Parse_ReadsTheCalibrationTableFromRealHardwareBytes()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        Assert.Collection(config.Calibration,
            p => { Assert.Equal(1538, p.Cpm); Assert.Equal(10.0, p.MicroSievertsPerHour, 3); },
            p => { Assert.Equal(15380, p.Cpm); Assert.Equal(100.0, p.MicroSievertsPerHour, 3); },
            p => { Assert.Equal(30760, p.Cpm); Assert.Equal(200.0, p.MicroSievertsPerHour, 3); });
    }

    [Fact]
    public void RealHardwarePointsAgreeOnTheDocumentedTubeSensitivity()
    {
        // All three points must sit on one line through the origin at 153.8 CPM per uSv/h, i.e. the
        // 0.0065 uSv/h per CPM figure documented for this tube. If a future firmware breaks that, the
        // offsets or endianness assumed by the parser are probably wrong.
        var config = GmcConfigParser.Parse(RealConfig());

        Assert.All(config.Calibration, p => Assert.Equal(153.8, p.Cpm / p.MicroSievertsPerHour, 1));
    }

    [Fact]
    public void ToMicroSievertsPerHour_ConvertsABackgroundReading()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        // 19 CPM / 153.8 = a plausible ~0.12 uSv/h background.
        Assert.Equal(0.1235, GmcCalibration.ToMicroSievertsPerHour(19, config.Calibration), 4);
    }

    [Fact]
    public void ToMicroSievertsPerHour_IsZeroAtZeroCounts()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        Assert.Equal(0, GmcCalibration.ToMicroSievertsPerHour(0, config.Calibration));
    }

    [Fact]
    public void ToMicroSievertsPerHour_MatchesTheStoredPointsExactly()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        foreach (var point in config.Calibration)
            Assert.Equal(point.MicroSievertsPerHour, GmcCalibration.ToMicroSievertsPerHour(point.Cpm, config.Calibration), 3);
    }

    [Fact]
    public void ToMicroSievertsPerHour_ExtrapolatesAboveTheHighestPoint()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        // Beyond 30760 CPM the final segment's slope continues, so the line through the origin holds.
        Assert.Equal(400.0, GmcCalibration.ToMicroSievertsPerHour(61520, config.Calibration), 2);
    }

    [Fact]
    public void ToMicroSievertsPerHour_InterpolatesPiecewiseWhenPointsAreNotCollinear()
    {
        // A deliberately bent curve, to prove the segments are honoured rather than a single slope assumed.
        var calibration = new[]
        {
            new GmcCalibrationPoint(100, 1.0),
            new GmcCalibrationPoint(200, 10.0),
        };

        Assert.Equal(0.5, GmcCalibration.ToMicroSievertsPerHour(50, calibration), 4);   // origin -> first point
        Assert.Equal(5.5, GmcCalibration.ToMicroSievertsPerHour(150, calibration), 4);  // between the points
        Assert.Equal(19.0, GmcCalibration.ToMicroSievertsPerHour(300, calibration), 4); // past the last point
    }

    [Fact]
    public void Parse_SkipsUnwrittenCalibrationEntries()
    {
        var config = GmcConfigParser.Parse(new byte[256]); // all zeroes

        Assert.Empty(config.Calibration);
    }

    [Fact]
    public void ToMicroSievertsPerHour_ThrowsWhenNoUsablePointsExist()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => GmcCalibration.ToMicroSievertsPerHour(19, Array.Empty<GmcCalibrationPoint>()));

        Assert.Contains("calibration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryToMicroSievertsPerHour_ReportsFailureInsteadOfThrowing()
    {
        Assert.False(GmcCalibration.TryToMicroSievertsPerHour(19, Array.Empty<GmcCalibrationPoint>(), out var usv));
        Assert.Equal(0, usv);
    }

    [Fact]
    public void TryToMicroSievertsPerHour_ConvertsWhenCalibrationExists()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        Assert.True(GmcCalibration.TryToMicroSievertsPerHour(19, config.Calibration, out var usv));
        Assert.Equal(0.1235, usv, 4);
    }

    [Fact]
    public void ToMicroSievertsPerHour_RejectsNegativeCounts()
    {
        var config = GmcConfigParser.Parse(RealConfig());

        Assert.Throws<ArgumentOutOfRangeException>(() => GmcCalibration.ToMicroSievertsPerHour(-1, config.Calibration));
    }
}
