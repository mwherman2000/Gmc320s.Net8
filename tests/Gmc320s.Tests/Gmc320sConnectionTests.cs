using System.Text;
using Xunit;

namespace Gmc320s.Tests;

public class Gmc320sConnectionTests
{
    [Fact]
    public void Command_RetriesOnTimeout_ThenSucceeds()
    {
        var port = new FakeSerialPort
        {
            ReplyTriggerPrefix = "<GETVER",
            ReplyPayload = Encoding.ASCII.GetBytes("GMC-320Re 5.71"),
            FailMatchingWritesBeforeReply = 2
        };
        var connection = new Gmc320sConnection(port);

        var result = connection.Command("GETVER", 14);

        Assert.Equal("GMC-320Re 5.71", Encoding.ASCII.GetString(result));
        Assert.Equal(3, port.WrittenFrames.Count);
        Assert.All(port.WrittenFrames, f => Assert.Equal("<GETVER>>", Encoding.ASCII.GetString(f)));
    }

    [Fact]
    public void Command_ThrowsAfterExhaustingRetries()
    {
        var port = new FakeSerialPort(); // no reply ever queued
        var connection = new Gmc320sConnection(port);

        Assert.Throws<TimeoutException>(() => connection.Command("GETCPM", 2));
        Assert.Equal(3, port.WrittenFrames.Count);
    }

    [Fact]
    public void Command_ThrowsWhenPortNotOpen()
    {
        var port = new FakeSerialPort();
        port.Close();
        var connection = new Gmc320sConnection(port);

        Assert.Throws<InvalidOperationException>(() => connection.Command("GETVER", 14));
    }
}
