using System.Buffers.Binary;

namespace EveryStage.Transport;

/// <summary>Single-loss XOR FEC over RTP payloads. Headers are carried as compact metadata so the
/// parity datagram remains close to one MTU instead of XORing eight complete RTP datagrams.</summary>
public static class XorFecCodec
{
    private static readonly byte[] Magic = "ESF1"u8.ToArray();
    public const int BlockSize = 8;
    public const int MetadataSize = 4 + 1 + 2 + 1 + 2 + 1 + 4 + BlockSize * 2 + BlockSize * 4 + 1;

    public static byte[] CreateParity(IReadOnlyList<RtpPacket> packets, ushort baseSequence)
    {
        if (packets.Count != BlockSize) throw new ArgumentException("FEC block must contain eight packets.");
        int maxLength = packets.Max(packet => packet.Payload.Length);
        var result = new byte[MetadataSize + maxLength];
        Magic.CopyTo(result, 0); result[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(5, 2), baseSequence);
        result[7] = BlockSize;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8, 2), checked((ushort)maxLength));
        result[10] = packets[0].PayloadType;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(11, 4), packets[0].Ssrc);
        int offset = 15;
        foreach (var packet in packets)
        {
            if (packet.PayloadType != packets[0].PayloadType || packet.Ssrc != packets[0].Ssrc)
                throw new ArgumentException("FEC block must use one RTP stream.");
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), checked((ushort)packet.Payload.Length));
            offset += 2;
        }
        foreach (var packet in packets)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), packet.Timestamp);
            offset += 4;
        }
        byte markers = 0;
        for (int i = 0; i < packets.Count; i++) if (packets[i].Marker) markers |= (byte)(1 << i);
        result[offset++] = markers;
        var parity = result.AsSpan(offset);
        foreach (var packet in packets)
            for (int i = 0; i < packet.Payload.Length; i++) parity[i] ^= packet.Payload.Span[i];
        return result;
    }

    public static bool TryRecover(byte[] parityPayload, IReadOnlyDictionary<ushort, RtpPacket> received,
        out RtpPacket recovered)
    {
        recovered = default;
        const int fixedMetadata = 15;
        if (parityPayload.Length < fixedMetadata || !parityPayload.AsSpan(0, 4).SequenceEqual(Magic) || parityPayload[4] != 1)
            return false;
        ushort baseSequence = BinaryPrimitives.ReadUInt16BigEndian(parityPayload.AsSpan(5, 2));
        int count = parityPayload[7];
        int maxLength = BinaryPrimitives.ReadUInt16BigEndian(parityPayload.AsSpan(8, 2));
        if (count != BlockSize) return false;
        int lengthsOffset = fixedMetadata;
        int timestampsOffset = lengthsOffset + count * 2;
        int markerOffset = timestampsOffset + count * 4;
        int payloadOffset = markerOffset + 1;
        if (parityPayload.Length != payloadOffset + maxLength) return false;
        int missingIndex = -1;
        for (int i = 0; i < count; i++)
        {
            ushort sequence = unchecked((ushort)(baseSequence + i));
            if (!received.ContainsKey(sequence))
            {
                if (missingIndex >= 0) return false;
                missingIndex = i;
            }
        }
        if (missingIndex < 0) return false;
        var payload = parityPayload.AsSpan(payloadOffset, maxLength).ToArray();
        for (int i = 0; i < count; i++)
        {
            ushort sequence = unchecked((ushort)(baseSequence + i));
            if (!received.TryGetValue(sequence, out var packet)) continue;
            int length = BinaryPrimitives.ReadUInt16BigEndian(parityPayload.AsSpan(lengthsOffset + i * 2, 2));
            if (packet.PayloadType != parityPayload[10] || packet.Ssrc != BinaryPrimitives.ReadUInt32BigEndian(parityPayload.AsSpan(11, 4))
                || packet.Payload.Length != length) return false;
            for (int p = 0; p < length; p++) payload[p] ^= packet.Payload.Span[p];
        }
        int recoveredLength = BinaryPrimitives.ReadUInt16BigEndian(parityPayload.AsSpan(lengthsOffset + missingIndex * 2, 2));
        if (recoveredLength <= 0 || recoveredLength > payload.Length) return false;
        recovered = new RtpPacket
        {
            Marker = (parityPayload[markerOffset] & (1 << missingIndex)) != 0,
            PayloadType = parityPayload[10],
            SequenceNumber = unchecked((ushort)(baseSequence + missingIndex)),
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(parityPayload.AsSpan(timestampsOffset + missingIndex * 4, 4)),
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(parityPayload.AsSpan(11, 4)),
            Payload = payload.AsMemory(0, recoveredLength),
        };
        return true;
    }
}
