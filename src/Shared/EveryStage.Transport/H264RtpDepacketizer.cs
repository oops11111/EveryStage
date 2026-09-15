namespace EveryStage.Transport;

/// <summary>
/// Reverses <see cref="H264RtpPacketizer"/>: reassembles RTP payloads back into complete H.264 NAL
/// units. Stateful per stream (an FU-A fragmented NAL spans multiple calls) — one instance per
/// incoming stream, not shared/reused across streams.
///
/// Assumes packets arrive in sequence order. This project has no jitter buffer or reordering logic
/// yet (see this library's README) — an out-of-order or dropped packet mid-fragmentation is
/// detected (the in-progress NAL is discarded rather than silently corrupted) but not recovered;
/// reordering/retransmission is a separate piece of work this class deliberately doesn't attempt.
/// </summary>
public sealed class H264RtpDepacketizer
{
    private const byte NalTypeFuA = 28;

    private MemoryStream? _fragmentBuffer;
    private byte _fragmentedNalHeader;

    /// <summary>Feed one RTP payload (<see cref="RtpPacket.Payload"/>), in sequence-number order.
    /// Returns a complete NAL unit (header byte + RBSP, no start code — ready for
    /// <c>AnnexBNalSplitter</c>'s inverse, prefixing a start code before feeding a decoder) once one
    /// finishes, or null if this payload was a non-final fragment.</summary>
    public byte[]? Process(ReadOnlyMemory<byte> rtpPayload)
    {
        if (rtpPayload.Length == 0) return null;

        byte nalType = (byte)(rtpPayload.Span[0] & 0x1F);
        return nalType == NalTypeFuA
            ? ProcessFragment(rtpPayload)
            : rtpPayload.ToArray(); // Single NAL Unit packet: payload IS the complete NAL unit.
    }

    private byte[]? ProcessFragment(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 2) return null; // malformed — needs at least the 2-byte FU indicator+header.

        byte fuIndicator = payload.Span[0];
        byte fuHeader = payload.Span[1];
        bool start = (fuHeader & 0x80) != 0;
        bool end = (fuHeader & 0x40) != 0;
        byte originalNalType = (byte)(fuHeader & 0x1F);
        byte forbiddenAndNri = (byte)(fuIndicator & 0xE0);
        var chunk = payload.Slice(2);

        if (start)
        {
            _fragmentBuffer?.Dispose(); // an unfinished previous fragmentation (lost end packet) — discard it.
            _fragmentBuffer = new MemoryStream();
            _fragmentedNalHeader = (byte)(forbiddenAndNri | originalNalType);
        }

        if (_fragmentBuffer == null)
        {
            // A continuation/end fragment with no start fragment seen — the start packet (or an
            // earlier middle one) was lost upstream. There is no way to recover the missing bytes;
            // drop this NAL entirely rather than emit a corrupt one.
            return null;
        }

        _fragmentBuffer.Write(chunk.Span);
        if (!end) return null;

        byte[] rbsp = _fragmentBuffer.ToArray();
        _fragmentBuffer.Dispose();
        _fragmentBuffer = null;

        var nalUnit = new byte[1 + rbsp.Length];
        nalUnit[0] = _fragmentedNalHeader;
        rbsp.CopyTo(nalUnit, 1);
        return nalUnit;
    }
}
