namespace EveryStage.Transport;

/// <summary>
/// Collects the NAL units of one encoded video frame into a complete Annex B access unit, ready to
/// hand to a decoder. The inverse of <see cref="AnnexBNalSplitter"/> plus the access-unit boundary
/// rule: the Caster side strips start codes off when packetizing per RFC 6184, so they have to be
/// put back before a decoder MFT that expects an Annex B bytestream will take the bytes.
///
/// Two boundary signals, not one. The RTP marker bit is the primary one (RFC 6184 section 5.3: the
/// last packet of an access unit), but relying on it alone means that losing the single packet that
/// carries it silently merges two frames into one submitted access unit, because the NAL units
/// pending from the unterminated frame are never cleared and the next frame just piles on top of
/// them. The RTP timestamp is the second: RFC 6184 section 5.1 requires every packet of one access
/// unit to carry the same timestamp, so a timestamp change proves the previous frame ended whether
/// or not its marker ever arrived. Whatever is still pending at that point belongs to a frame that
/// is incomplete by definition, and is dropped (and counted) rather than glued onto the next one -
/// dropping one frame beats handing the decoder a two-frame access unit.
///
/// This deliberately lives here rather than inside Terminal's CastReceiver, where it started: as a
/// plain byte-in/byte-out class with no decoder, D3D11 or Media Foundation dependency it can be
/// covered by the core self-tests, which run on any .NET runtime without Windows or a GPU.
///
/// Not thread-safe; one instance per incoming stream, called from that stream's own thread.
/// </summary>
public sealed class AccessUnitAssembler
{
    private readonly List<byte[]> _pendingNals = new();
    private uint? _pendingTimestamp;
    private long _incompleteAccessUnitsDropped;

    /// <summary>Access units dropped because the packet carrying their marker bit never arrived,
    /// detected by the RTP timestamp changing while NAL units were still pending. A bare counter
    /// with no logging, matching how RtpReceiver exposes GapEvents/PayloadTypeMismatches.</summary>
    public long IncompleteAccessUnitsDropped => Interlocked.Read(ref _incompleteAccessUnitsDropped);

    /// <summary>Feed one NAL unit. Returns the finished Annex B access unit when this NAL unit
    /// completes one, or null while the frame is still being collected.</summary>
    /// <param name="nalUnit">A complete NAL unit (header byte + RBSP, no start code), as produced
    /// by <see cref="H264RtpDepacketizer"/>.</param>
    /// <param name="isLastNalOfAccessUnit">The RTP marker bit of the packet this NAL unit came
    /// from.</param>
    /// <param name="timestamp">The RTP timestamp of that same packet.</param>
    public byte[]? Add(byte[] nalUnit, bool isLastNalOfAccessUnit, uint timestamp)
    {
        if (_pendingNals.Count > 0 && _pendingTimestamp != timestamp)
        {
            _pendingNals.Clear();
            Interlocked.Increment(ref _incompleteAccessUnitsDropped);
        }

        _pendingTimestamp = timestamp;
        _pendingNals.Add(nalUnit);
        if (!isLastNalOfAccessUnit) return null;

        byte[] accessUnit = BuildAnnexB(_pendingNals);
        _pendingNals.Clear();
        return accessUnit;
    }

    /// <summary>Discard whatever frame is half-collected. For a stream restart - not for packet
    /// loss, which the timestamp rule in <see cref="Add"/> already covers.</summary>
    public void Reset()
    {
        _pendingNals.Clear();
        _pendingTimestamp = null;
    }

    private static byte[] BuildAnnexB(List<byte[]> nalUnits)
    {
        int total = 0;
        foreach (var nal in nalUnits) total += nal.Length + 4;

        var buffer = new byte[total];
        int offset = 0;
        foreach (var nal in nalUnits)
        {
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 1;
            nal.CopyTo(buffer, offset);
            offset += nal.Length;
        }
        return buffer;
    }
}
