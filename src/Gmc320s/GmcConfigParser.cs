using System.Buffers.Binary;

namespace Gmc320s;

/// <summary>
/// Best-effort parser for the leading bytes of the GMC-320S's 256-byte <c>GETCFG</c> configuration blob.
/// </summary>
/// <remarks>
/// The named single-byte fields (offsets 0-5) come from GQ RFC1201 references rather than observation, so they
/// are exposed as raw bytes without assuming a polarity for the on/off-style ones. A device-menu factory reset
/// was seen to change several of them, which at least confirms they carry real state.
/// <para>
/// The calibration table at offsets 8-25 <em>was</em> confirmed against hardware - see PROTOCOL-NOTES.md for
/// the raw bytes and the reasoning. Everything past offset 25 is still undecoded; the full blob is always
/// available via <see cref="GmcConfig.Raw"/>.
/// </para>
/// </remarks>
internal static class GmcConfigParser
{
    // Three consecutive 6-byte entries: big-endian uint16 CPM, then little-endian float µSv/h.
    private const int CalibrationOffset = 8;
    private const int CalibrationPointCount = 3;
    private const int CalibrationEntrySize = 6;

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

        return new GmcConfig(values, ReadCalibration(raw), raw);
    }

    private static IReadOnlyList<GmcCalibrationPoint> ReadCalibration(byte[] raw)
    {
        var points = new List<GmcCalibrationPoint>();

        for (var i = 0; i < CalibrationPointCount; i++)
        {
            var offset = CalibrationOffset + i * CalibrationEntrySize;
            if (offset + CalibrationEntrySize > raw.Length) break;

            var cpm = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(offset, 2));
            var microSieverts = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(offset + 2, 4));

            // Skip unwritten or nonsensical entries rather than letting them distort the curve.
            if (cpm == 0 || !float.IsFinite(microSieverts) || microSieverts <= 0) continue;

            points.Add(new GmcCalibrationPoint(cpm, microSieverts));
        }

        return points;
    }

    private static byte? ByteAt(byte[] raw, int index) => index < raw.Length ? raw[index] : null;
}
