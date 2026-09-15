using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Sender-side RTP session: owns the per-stream state (SSRC, sequence number) RFC 3550 requires
/// and that <see cref="H264RtpPacketizer"/> deliberately doesn't hold itself, and sends the
/// resulting packets over UDP. One instance per outgoing stream — SSRC/sequence numbers are not
/// meant to be shared across streams.
/// </summary>
public sealed class RtpSession : IDisposable
{
    private readonly UdpClient _socket;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly byte _payloadType;
    private readonly int _maxPayloadSize;
    private readonly uint _ssrc;
    private ushort _sequenceNumber;

    /// <param name="payloadType">RTP payload type number (0-127) — a value both ends must already
    /// agree on; this project has no SDP-style negotiation, see this library's README.</param>
    /// <param name="maxPayloadSize">Passed straight through to <see cref="H264RtpPacketizer"/> —
    /// see its own doc comment on picking this for the actual network path.</param>
    public RtpSession(IPEndPoint remoteEndPoint, byte payloadType, int maxPayloadSize = 1400)
    {
        _remoteEndPoint = remoteEndPoint;
        _payloadType = payloadType;
        _maxPayloadSize = maxPayloadSize;
        _socket = new UdpClient();

        // RFC 3550 §5.1: SSRC and the initial sequence number SHOULD be chosen randomly per
        // session — makes colliding with another session on the same port pair vanishingly
        // unlikely and (per the RFC) makes known-plaintext attacks on any later-added encryption
        // harder. Random.Shared is thread-safe should this ever need to be called concurrently.
        _ssrc = unchecked((uint)Random.Shared.Next());
        _sequenceNumber = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
    }

    /// <summary>Packetizes one NAL unit and sends the resulting RTP packet(s).</summary>
    /// <param name="rtpTimestamp">Must be identical for every NAL unit belonging to the same
    /// encoded frame — see <see cref="RtpVideoClock"/>.</param>
    /// <param name="isLastNalOfAccessUnit">See <see cref="H264RtpPacketizer.Packetize"/>.</param>
    public async Task SendNalUnitAsync(ReadOnlyMemory<byte> nalUnit, uint rtpTimestamp, bool isLastNalOfAccessUnit)
    {
        foreach (var payload in H264RtpPacketizer.Packetize(nalUnit, isLastNalOfAccessUnit, _maxPayloadSize))
        {
            var packet = new RtpPacket
            {
                Marker = payload.Marker,
                PayloadType = _payloadType,
                SequenceNumber = _sequenceNumber++, // wraps at ushort.MaxValue by design, per RFC 3550.
                Timestamp = rtpTimestamp,
                Ssrc = _ssrc,
                Payload = payload.Bytes,
            };

            byte[] datagram = packet.Encode();
            await _socket.SendAsync(datagram, datagram.Length, _remoteEndPoint);
        }
    }

    public void Dispose() => _socket.Dispose();
}
