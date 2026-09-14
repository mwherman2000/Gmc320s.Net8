using Xunit;

namespace Gmc320s.Tests;

public class GmcConfigParserTests
{
    [Fact]
    public void Parse_DecodesLeadingBytesAndKeepsRaw()
    {
        var raw = new byte[256];
        raw[0] = 0; // PowerOnOff
        raw[1] = 1; // AlarmOnOff
        raw[2] = 0; // SpeakerOnOff
        raw[3] = 1; // GraphicModeOnOff
        raw[4] = 30; // BacklightTimeoutSeconds
        raw[5] = 2; // IdleTitleDisplayMode

        var config = GmcConfigParser.Parse(raw);

        Assert.Equal((byte)0, config.Values["PowerOnOff"]);
        Assert.Equal((byte)1, config.Values["AlarmOnOff"]);
        Assert.Equal((byte)0, config.Values["SpeakerOnOff"]);
        Assert.Equal((byte)1, config.Values["GraphicModeOnOff"]);
        Assert.Equal((byte)30, config.Values["BacklightTimeoutSeconds"]);
        Assert.Equal((byte)2, config.Values["IdleTitleDisplayMode"]);
        Assert.Same(raw, config.Raw);
    }

    [Fact]
    public void Parse_HandlesShorterThanExpectedBuffers()
    {
        var config = GmcConfigParser.Parse(new byte[] { 5 });

        Assert.Equal((byte)5, config.Values["PowerOnOff"]);
        Assert.Null(config.Values["AlarmOnOff"]);
    }
}
