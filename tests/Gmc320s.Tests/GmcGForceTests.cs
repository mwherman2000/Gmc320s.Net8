using Xunit;

namespace Gmc320s.Tests;

public class GmcGForceTests
{
    [Fact]
    public void ConvertsCountsToG()
    {
        var o = new GmcGForce(16384, -8192, 0);

        Assert.Equal(1.0, o.XG, 6);
        Assert.Equal(-0.5, o.YG, 6);
        Assert.Equal(0.0, o.ZG, 6);
    }

    [Theory]
    // Real readings from a motionless device in each of the six calibration positions. Every one must be
    // stable, including Y-down at 0.96 g, where the axis's ~4% gain shortfall is at its most visible.
    [InlineData(288, 48, -16288)]      // flat, Z down
    [InlineData(224, -128, 16288)]     // Z up
    [InlineData(-16048, -32, -224)]    // X down
    [InlineData(16480, -240, -304)]    // X up
    [InlineData(256, 15664, 1168)]     // Y up
    [InlineData(-976, -15760, -160)]   // Y down
    public void MotionlessReadingsAreStableInEveryOrientation(short x, short y, short z)
    {
        var o = new GmcGForce(x, y, z);

        Assert.True(o.IsStable, $"|g| = {o.Magnitude:F4} should count as still");
    }

    [Theory]
    // Captured while the device was being moved and dropped: near free fall through to a hard catch.
    [InlineData(-1568, -3568, 4368)]     // 0.36 g - two thirds of free fall
    [InlineData(2512, -448, -4928)]      // 0.34 g
    [InlineData(-16352, -11408, 31200)]  // 2.26 g - the catch at the bottom
    [InlineData(-18976, -5216, 23808)]   // 1.89 g
    [InlineData(-3056, -2848, 13792)]    // 0.88 g - only just outside tolerance
    public void MovingReadingsAreNotStable(short x, short y, short z)
    {
        var o = new GmcGForce(x, y, z);

        Assert.False(o.IsStable, $"|g| = {o.Magnitude:F4} should count as moving");
    }

    [Fact]
    public void MagnitudeIsOrientationIndependentForAnIdealSensor()
    {
        // Same 1 g vector pointed three different ways; magnitude must not care which.
        var down = new GmcGForce(0, 0, -16384);
        var edge = new GmcGForce(0, 16384, 0);
        var tilted = new GmcGForce(11586, 11586, 0); // 1 g split evenly across two axes

        Assert.Equal(1.0, down.Magnitude, 3);
        Assert.Equal(1.0, edge.Magnitude, 3);
        Assert.Equal(1.0, tilted.Magnitude, 3);
    }

    [Fact]
    public void FreeFallReadsZero()
    {
        var o = new GmcGForce(0, 0, 0);

        Assert.Equal(0.0, o.Magnitude, 6);
        Assert.False(o.IsStable);
    }

    private static byte[] Reply(short x, short y, short z) =>
    [
        (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)(z >> 8), (byte)z, 0xAA
    ];

    private static Gmc320sClient ClientReplying(params byte[][] replies) =>
        new(new Gmc320sConnection(new FakeSerialPort
        {
            ReplyTriggerPrefix = "<GETGYRO",
            ReplySequence = new Queue<byte[]>(replies)
        }));

    [Fact]
    public async Task GetStableGForceAsync_AveragesAgreeingSamples()
    {
        using var client = ClientReplying(
            Reply(288, 48, -16288),
            Reply(272, 32, -16352),
            Reply(304, 48, -16320),
            Reply(288, 64, -16304));

        var o = await client.GetStableGForceAsync();

        Assert.True(o.IsStable);
        Assert.Equal(288, o.X);   // mean of 288, 272, 304, 288
        Assert.Equal(48, o.Y);
        Assert.Equal(-16316, o.Z);
    }

    [Fact]
    public async Task GetStableGForceAsync_WaitsOutMotionThenReturnsTheSettledReading()
    {
        // The first three are real shake-capture samples, including one at 0.968 g that a single-sample
        // IsStable check would wrongly accept. Only the settled run afterwards should be returned.
        using var client = ClientReplying(
            Reply(3168, -12608, -144),    // 0.79 g - obvious motion
            Reply(-4112, -15312, 160),    // 0.968 g - passes IsStable, but the device was being rotated
            Reply(2512, -11360, -2720),   // 0.73 g - obvious motion again
            Reply(288, 48, -16288),
            Reply(272, 32, -16352),
            Reply(304, 48, -16320),
            Reply(288, 64, -16304));

        var o = await client.GetStableGForceAsync();

        Assert.Equal(-16316, o.Z);
        Assert.True(o.IsStable);
    }

    [Fact]
    public async Task GetStableGForceAsync_RejectsSamplesThatAreNearOneGButDisagree()
    {
        // Every sample sits close to 1 g, so each passes IsStable individually - but they point in wildly
        // different directions, which is exactly the case a single-sample check cannot catch.
        using var client = ClientReplying(
            Reply(16384, 0, 0),
            Reply(0, 16384, 0),
            Reply(0, 0, 16384),
            Reply(-16384, 0, 0));

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetStableGForceAsync(maxSamples: 8));
    }

    [Fact]
    public async Task GetStableGForceAsync_ThrowsWhenTheDeviceNeverSettles()
    {
        using var client = ClientReplying(Reply(-16352, -11408, 31200)); // 2.26 g, repeated

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetStableGForceAsync(maxSamples: 6));
    }

    [Fact]
    public void IsStableWithinHonoursACallerSuppliedTolerance()
    {
        var yDown = new GmcGForce(-976, -15760, -160); // ~0.96 g, still, but 4% off from Y's gain error

        Assert.True(yDown.IsStableWithin(0.10));
        Assert.False(yDown.IsStableWithin(0.01)); // too tight for this device's per-axis trim
    }
}
