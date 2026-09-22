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

    // RFC 6184 section 5.2 packet type ranges: 1-23 are Single NAL Unit packets (the payload IS
    // the NAL unit), 24-27 are aggregation packets (STAP-A/STAP-B/MTAP16/MTAP24), 28-29 are
    // fragmentation units (FU-A/FU-B), and 0 plus 30-31 are reserved or undefined.
    private const byte NalTypeSingleNalMin = 1;
    private const byte NalTypeSingleNalMax = 23;

    private MemoryStream? _fragmentBuffer;
    private byte _fragmentedNalHeader;
    private long _unsupportedPacketTypes;
    private long _malformedFragments;

    /// <summary>Packets discarded because their RTP payload format type is one this depacketizer
    /// does not implement (aggregation packets, FU-B, reserved types). A bare counter with no
    /// logging, matching how RtpReceiver exposes GapEvents/PayloadTypeMismatches.</summary>
    public long UnsupportedPacketTypes => Interlocked.Read(ref _unsupportedPacketTypes);

    /// <summary>FU-A packets too short to contain even their own 2-byte header.</summary>
    public long MalformedFragments => Interlocked.Read(ref _malformedFragments);

    public void Reset()
    {
        _fragmentBuffer?.Dispose();
        _fragmentBuffer = null;
    }

    /// <summary>Feed one RTP payload (<see cref="RtpPacket.Payload"/>), in sequence-number order.
    /// Returns a complete NAL unit (header byte + RBSP, no start code — ready for
    /// <c>AnnexBNalSplitter</c>'s inverse, prefixing a start code before feeding a decoder) once one
    /// finishes, or null if this payload was a non-final fragment.</summary>
    public byte[]? Process(ReadOnlyMemory<byte> rtpPayload)
    {
        if (rtpPayload.Length == 0) return null;

        byte nalType = (byte)(rtpPayload.Span[0] & 0x1F);
        if (nalType == NalTypeFuA) return ProcessFragment(rtpPayload);
        if (nalType >= NalTypeSingleNalMin && nalType <= NalTypeSingleNalMax)
            return rtpPayload.ToArray(); // Single NAL Unit packet: payload IS the complete NAL unit.

        // Aggregation packets (24-27), FU-B (29) and the reserved types (0, 30, 31). This project's
        // H264RtpPacketizer never emits any of them, but a conforming sender may - STAP-A carrying
        // SPS/PPS in a single packet is the most common such optimization. Their first byte is an
        // aggregation or fragmentation header, NOT a NAL unit header, so the previous "anything that
        // is not FU-A must already be a complete NAL unit" fallthrough handed the decoder a
        // fabricated NAL unit built out of a header it had misread. RFC 6184 section 5.2 requires a
        // receiver to discard packet types it does not support. Reset() too: arriving mid-
        // fragmentation, such a packet means the peer is not speaking the format this class assumed,
        // which makes the half-built NAL unit suspect as well.
        Interlocked.Increment(ref _unsupportedPacketTypes);
        Reset();
        return null;
    }

    private byte[]? ProcessFragment(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 2)
        {
            // Malformed: an FU-A needs at least its 2-byte FU indicator+header. Discarding the
            // in-progress NAL unit is the point here - this used to just return null and leave
            // _fragmentBuffer intact, so the following middle/end fragments concatenated straight
            // across the hole and emitted a silently corrupt NAL unit. RtpReceiver's loss path
            // (Reset() when PacketsLostBefore > 0) never covers this case, because a truncated
            // packet is a packet that *arrived*: the reorder buffer sees no sequence-number gap.
            Interlocked.Increment(ref _malformedFragments);
            Reset();
            return null;
        }

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
