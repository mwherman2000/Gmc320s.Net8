using System.Text;

namespace Gmc320s.Tests;

/// <summary>
/// In-memory stand-in for a real serial port, so <see cref="Gmc320sConnection"/>'s framing and retry logic can be
/// tested without physical (or virtual) hardware.
/// </summary>
/// <remarks>
/// Two independent ways to script a reply, matching the two call patterns the production code uses:
/// - <see cref="ReplyTriggerPrefix"/>/<see cref="ReplyPayload"/>/<see cref="FailMatchingWritesBeforeReply"/> queue
///   a reply when a matching command is written - mirrors <c>Gmc320sConnection.Execute</c>, which resends the
///   command on every retry.
/// - <see cref="EnqueueTimeout"/>/<see cref="EnqueueReadBytes"/> script raw per-<see cref="Read"/> outcomes
///   independent of any write - mirrors the live heartbeat stream, which reads repeatedly without resending
///   anything.
/// </remarks>
internal sealed class FakeSerialPort : ISerialPort
{
    private readonly Queue<byte> _incoming = new();
    private readonly Queue<Func<byte[], int, int, int>> _scriptedReads = new();
    private int _matchingWriteCount;

    public List<byte[]> WrittenFrames { get; } = new();
    public string? ReplyTriggerPrefix { get; set; }
    public byte[]? ReplyPayload { get; set; }
    public int FailMatchingWritesBeforeReply { get; set; }

    /// <summary>
    /// When set, simulates a real flash chip for SPIR reads: decodes the address/length out of each written
    /// SPIR frame and replies with the corresponding slice, padded with 0xFF (erased flash) past the image's
    /// end. Independent of <see cref="ReplyTriggerPrefix"/>, so it doesn't affect other commands.
    /// </summary>
    public byte[]? FlashImage { get; set; }

    public string PortName => "FAKE";
    public int BaudRate => 115200;
    public bool IsOpen { get; private set; } = true;

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void Dispose() { }

    public void EnqueueTimeout() => _scriptedReads.Enqueue((_, _, _) => throw new TimeoutException("simulated timeout"));

    public void EnqueueReadBytes(byte[] bytes) => _scriptedReads.Enqueue((buffer, offset, count) =>
    {
        var n = Math.Min(count, bytes.Length);
        Array.Copy(bytes, 0, buffer, offset, n);
        return n;
    });

    public void Write(byte[] buffer, int offset, int count)
    {
        var frame = new byte[count];
        Array.Copy(buffer, offset, frame, 0, count);
        WrittenFrames.Add(frame);

        if (FlashImage is not null && frame.Length >= 10 && frame[0] == '<' && frame[1] == 'S' && frame[2] == 'P' && frame[3] == 'I' && frame[4] == 'R')
        {
            var address = (frame[5] << 16) | (frame[6] << 8) | frame[7];
            var length = ((frame[8] << 8) | frame[9]) + 1;
            for (var i = 0; i < length; i++)
            {
                var srcIndex = address + i;
                _incoming.Enqueue(srcIndex < FlashImage.Length ? FlashImage[srcIndex] : (byte)0xFF);
            }
            return;
        }

        if (ReplyTriggerPrefix is null || ReplyPayload is null) return;
        if (!Encoding.ASCII.GetString(frame).StartsWith(ReplyTriggerPrefix, StringComparison.Ordinal)) return;

        _matchingWriteCount++;
        if (_matchingWriteCount > FailMatchingWritesBeforeReply)
            foreach (var b in ReplyPayload) _incoming.Enqueue(b);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_scriptedReads.Count > 0) return _scriptedReads.Dequeue()(buffer, offset, count);
        if (_incoming.Count == 0) throw new TimeoutException("simulated timeout: no data queued");

        var n = 0;
        while (n < count && _incoming.Count > 0)
        {
            buffer[offset + n] = _incoming.Dequeue();
            n++;
        }

        return n;
    }

    public void DiscardInBuffer() => _incoming.Clear();
    public void DiscardOutBuffer() { }
}
