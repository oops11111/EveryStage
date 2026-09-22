namespace EveryStage.Transport;

/// <summary>
/// Packs H.264 NAL units into RTP payloads per RFC 6184 ("RTP Payload Format for H.264 Video"):
/// a NAL unit that fits in one packet goes out as a Single NAL Unit packet (the NAL unit
/// unchanged); a larger one is split into a Fragmentation Unit (FU-A) sequence. This only produces
/// payload bytes — wrapping them in <see cref="RtpPacket"/> (sequence numbers, timestamp, SSRC) is
/// the caller's job, since that requires per-stream state this static packetizer doesn't hold.
///
/// Every produced payload is tagged with whether the RTP <c>Marker</c> bit should be set on it —
/// per RFC 6184 §5.3, that means "the last packet of an access unit" (i.e. the final NAL, or its
/// final fragment, of one encoded video frame) — so the caller only needs to pass through whichever
/// flag this returns rather than re-deriving that rule itself.
/// </summary>
public static class H264RtpPacketizer
{
    // NAL unit types (H.264 §7.4.1) relevant to the FU-A header this packetizer writes.
    private const byte NalTypeFuA = 28;

    public readonly record struct Payload(ReadOnlyMemory<byte> Bytes, bool Marker);

    /// <param name="nalUnit">One NAL unit (header byte + RBSP), as produced by
    /// <see cref="AnnexBNalSplitter"/> — no start code.</param>
    /// <param name="isLastNalOfAccessUnit">True for the last (or only) NAL unit of one encoded
    /// frame — controls the RTP Marker bit per RFC 6184 §5.3.</param>
    /// <param name="maxPayloadSize">RTP payload budget per packet (typically MTU minus IP/UDP/RTP
    /// header overhead — a caller targeting a 1500-byte Ethernet MTU would pass something like
    /// 1400 to leave headroom; this packetizer doesn't guess a default since the right number
    /// depends on the actual network path).</param>
    public static IEnumerable<Payload> Packetize(ReadOnlyMemory<byte> nalUnit, bool isLastNalOfAccessUnit, int maxPayloadSize)
    {
        if (nalUnit.Length == 0)
            // An empty NAL unit is not representable in this payload format - there is no header
            // byte to carry its type - and H264RtpDepacketizer drops a zero-length payload on
            // arrival anyway, so emitting one would only burn a sequence number. AnnexBNalSplitter
            // should never produce one; this guards a future caller rather than fixing an observed
            // input.
            yield break;

        if (nalUnit.Length <= maxPayloadSize)
        {
            // Single NAL Unit packet (RFC 6184 §5.6): the NAL unit goes out unmodified as the
            // entire RTP payload.
            yield return new Payload(nalUnit, isLastNalOfAccessUnit);
            yield break;
        }

        // Fragmentation Unit (FU-A, RFC 6184 §5.8): split the NAL unit's payload (everything after
        // its 1-byte header) across multiple packets, each carrying a 2-byte FU header
        // (FU indicator + FU header) ahead of a chunk of the original payload.
        byte nalHeader = nalUnit.Span[0];
        byte forbiddenAndNri = (byte)(nalHeader & 0xE0); // forbidden_zero_bit(1) + nal_ref_idc(2)
        byte originalNalType = (byte)(nalHeader & 0x1F);

        var remaining = nalUnit.Slice(1); // NAL payload without its own header byte.
        int chunkSize = maxPayloadSize - 2; // 2 bytes of FU indicator+header per packet.
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadSize), "maxPayloadSize too small to fit even one FU-A byte of payload.");

        bool isFirst = true;
        while (remaining.Length > 0)
        {
            int take = Math.Min(chunkSize, remaining.Length);
            bool isLastFragment = take == remaining.Length;

            var packetBuffer = new byte[2 + take];
            packetBuffer[0] = (byte)(forbiddenAndNri | NalTypeFuA); // FU indicator
            packetBuffer[1] = (byte)((isFirst ? 0x80 : 0) | (isLastFragment ? 0x40 : 0) | originalNalType); // FU header: S | E | R=0 | Type
            remaining.Slice(0, take).Span.CopyTo(packetBuffer.AsSpan(2));

            yield return new Payload(packetBuffer, isLastFragment && isLastNalOfAccessUnit);

            remaining = remaining.Slice(take);
            isFirst = false;
        }
    }
}
