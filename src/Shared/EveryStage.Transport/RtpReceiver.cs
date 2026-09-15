using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Receiver-side counterpart to <see cref="RtpSession"/>: listens on a UDP port, decodes each
/// datagram as an RTP packet, and feeds the payload through an <see cref="H264RtpDepacketizer"/>,
/// raising <see cref="NalUnitReceived"/> whenever a complete NAL unit comes out the other end.
///
/// Assumes packets from only one sender (one SSRC) arrive on this port — there is no per-SSRC
/// demultiplexing here. Runs its receive loop as a background <see cref="Task"/>, the same pattern
/// <c>DiscoveryService</c>/<c>TerminalDiscoveryClient</c> use for their own UDP loops.
/// </summary>
public sealed class RtpReceiver : IDisposable
{
    private readonly UdpClient _socket;
    private readonly H264RtpDepacketizer _depacketizer = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    private ushort? _lastSequenceNumber;
    private long _packetsReceived;
    private long _gapEvents;

    /// <summary>Total RTP packets successfully decoded (<see cref="RtpPacket.TryDecode"/> succeeded)
    /// since this receiver started — read via <see cref="Interlocked"/> so a caller on another
    /// thread (e.g. a periodic diagnostic-logging timer) always sees a consistent snapshot, even
    /// though only this class's own single receive-loop thread ever writes it.</summary>
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>How many times a received packet's sequence number wasn't exactly one more than the
    /// previous packet's — a rough, honestly-approximate loss signal, not an exact lost-packet count:
    /// a single gap that skipped 5 sequence numbers counts as 1 event here, not 5, and a genuinely
    /// reordered-but-not-lost packet (this class has no reordering support at all — see class doc
    /// comment, an out-of-order arrival already breaks NAL reassembly) also counts as an event,
    /// indistinguishable from real loss. Deliberately does NOT compute the signed/wrapped numeric gap
    /// between expected and actual sequence numbers (RFC 3550 sequence numbers wrap at 65536) — doing
    /// that naively would make a reordered packet arriving one slot "early" look like ~65535 lost
    /// packets via <c>ushort</c> wraparound, a far worse mistake than this simpler "did a gap happen,
    /// yes or no" counting. Meant to be combined with <see cref="PacketsReceived"/> into a rough
    /// loss-rate estimate for diagnostic logging (see <c>Terminal.Receiving.CastReceiver</c> and
    /// <c>DeviceConnectionLogger.LogQualityMetric</c>), not treated as an exact percentage.</summary>
    public long GapEvents => Interlocked.Read(ref _gapEvents);

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.
    /// The <c>bool</c> is the completing RTP packet's Marker bit, i.e. RFC 6184 §5.3 "this was the
    /// last NAL unit of its access unit" — passed through as-is rather than making every consumer
    /// re-derive it, since <see cref="H264RtpPacketizer"/>/<see cref="H264RtpDepacketizer"/> already
    /// encode/decode it symmetrically. The <c>uint</c> is that same packet's RTP timestamp — every
    /// NAL unit belonging to one access unit shares the same value (the sender sets it once per
    /// access unit, see <c>RtpSession.SendNalUnitAsync</c>'s caller), so a consumer reassembling an
    /// access unit from multiple NAL units can take the timestamp from any one of them.</summary>
    public event Action<byte[], bool, uint>? NalUnitReceived;

    public RtpReceiver(int listenPort)
    {
        _socket = new UdpClient(listenPort);
    }

    public void Start() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // transient network error on one datagram — keep listening.
            }

            if (!RtpPacket.TryDecode(result.Buffer, out var packet)) continue; // not one of ours — ignore.

            TrackSequenceNumber(packet.SequenceNumber);

            var nalUnit = _depacketizer.Process(packet.Payload);
            if (nalUnit != null) NalUnitReceived?.Invoke(nalUnit, packet.Marker, packet.Timestamp);
        }
    }

    private void TrackSequenceNumber(ushort sequenceNumber)
    {
        Interlocked.Increment(ref _packetsReceived);
        if (_lastSequenceNumber.HasValue && sequenceNumber != unchecked((ushort)(_lastSequenceNumber.Value + 1)))
            Interlocked.Increment(ref _gapEvents);
        _lastSequenceNumber = sequenceNumber;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _receiveLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
        _socket.Dispose();
    }
}
