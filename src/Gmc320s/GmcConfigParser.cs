namespace Gmc320s;

/// <summary>
/// Best-effort parser for the leading bytes of the GMC-320S's 256-byte <c>GETCFG</c> configuration blob that
/// are consistently documented across GQ RFC1201 references. Everything past what's named here (notably the
/// calibration table) is firmware/model-specific and deliberately left undecoded - see
/// <see cref="GmcConfig.Raw"/> for the full blob. Byte offsets and meaning are protocol-derived and not yet
/// verified against physical hardware; treat the values as raw bytes rather than assuming a specific polarity
/// for the on/off-style fields.
/// </summary>
internal static class GmcConfigParser
{
    public static GmcConfig Parse(byte[] raw)
    {
        var values = new Dictionary<string, object?>
        {
            ["PowerOnOff"] = ByteAt(raw, 0),
            ["AlarmOnOff"] = ByteAt(raw, 1),
            ["SpeakerOnOff"] = ByteAt(raw, 2),
            ["GraphicModeOnOff"] = ByteAt(raw, 3),
            ["BacklightTimeoutSeconds"] = ByteAt(raw, 4),
            ["IdleTitleDisplayMode"] = ByteAt(raw, 5),
        };

        return new GmcConfig(values, raw);
    }

    private static byte? ByteAt(byte[] raw, int index) => index < raw.Length ? raw[index] : null;
}
