using System.Buffers.Binary;

namespace EveryStage.Transport;

/// <summary>
/// An RTP packet header + payload, per RFC 3550 §5.1. Deliberately covers only what this project's
/// own sender ever produces — fixed 12-byte header, no CSRC list, no header extension, no padding.
/// Encoding always writes exactly that shape; decoding is correspondingly lenient about CSRC
/// (skipped correctly, since that's a fixed-size, easy-to-parse list) but does NOT parse the RTP
/// header extension mechanism — if a peer sets the extension bit, <see cref="TryDecode"/> returns
/// the (wrong) payload starting right after the CSRC list rather than skipping the extension
/// header too. That's fine for this project talking to itself; it is not a general-purpose RTP
/// parser and would need extending before trusting arbitrary third-party RTP input.
///
/// PLANNING.md §4.2: "传输：UDP + RTP...不使用 WebRTC 的 NAT穿透部分（内网不需要）" — this is the RTP
/// framing that transport layer rides on; NACK/FEC recovery is implemented by the surrounding
/// session/receiver classes, while congestion control remains a higher-level concern. See this
/// library's README.
/// </summary>
public readonly struct RtpPacket
{
    public const int FixedHeaderSize = 12;
    private const byte RtpVersion = 2;

    public bool Marker { get; init; }

    /// <summary>0-127. The payload type number itself is a separate negotiation this project
    /// hasn't defined yet (no SDP/RTP profile) — pick and document one constant value for H.264
    /// when the sender/receiver are actually wired together.</summary>
    public byte PayloadType { get; init; }

    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public uint Ssrc { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public byte[] Encode()
    {
        var buffer = new byte[FixedHeaderSize + Payload.Length];

        buffer[0] = (byte)(RtpVersion << 6); // padding=0, extension=0, CSRC count=0
        buffer[1] = (byte)((Marker ? 0x80 : 0x00) | (PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), Ssrc);
        Payload.Span.CopyTo(buffer.AsSpan(FixedHeaderSize));

        return buffer;
    }

    /// <summary>Returns false (rather than throwing) for anything too short to be a valid RTP
    /// packet or with an unrecognized version — a malformed/foreign UDP datagram on the transport
    /// port should be dropped, not crash whatever's reading the socket.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> data, out RtpPacket packet)
    {
        packet = default;
        if (data.Length < FixedHeaderSize) return false;

        var span = data.Span;
        byte versionAndFlags = span[0];
        byte version = (byte)(versionAndFlags >> 6);
        if (version != RtpVersion) return false;

        int csrcCount = versionAndFlags & 0x0F;
        int headerSize = FixedHeaderSize + csrcCount * 4;
        if (data.Length < headerSize) return false;

        byte secondByte = span[1];
        bool marker = (secondByte & 0x80) != 0;
        byte payloadType = (byte)(secondByte & 0x7F);
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));

        packet = new RtpPacket
        {
            Marker = marker,
            PayloadType = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
            Payload = data.Slice(headerSize),
        };
        return true;
    }
}
