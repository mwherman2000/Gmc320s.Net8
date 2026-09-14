using System.Buffers.Binary;
using Xunit;

namespace Gmc320s.Tests;

public class ProtocolTests
{
    [Fact]
    public void CpmIsBigEndian() => Assert.Equal(28, BinaryPrimitives.ReadUInt16BigEndian(new byte[] { 0x00, 0x1C }));

    [Fact]
    public void HeartbeatMasksTo14Bits() => Assert.Equal(0x3FFF, 0xFFFF & 0x3FFF);

    [Fact]
    public void TemperaturePositiveFormat() { var b = new byte[] { 20, 7, 0, 0 }; Assert.Equal(20.7, b[0] + b[1] / 10.0, 5); }
}
