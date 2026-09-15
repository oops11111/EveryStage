using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Receiver-side counterpart to <see cref="RtpSession.SendRawPayloadAsync"/>: decodes each incoming
/// datagram as an RTP packet and hands its payload straight through, in arrival order, with no
/// H.264-specific depacketization. Suited to a continuous byte stream like raw PCM audio, where
/// there's no equivalent to <see cref="H264RtpDepacketizer"/>'s "reassemble one fragmented unit"
/// step — every packet's payload already is a complete, independently-usable chunk of the stream.
///
/// Deliberately a separate class from <see cref="RtpReceiver"/> rather than a generalized/shared
/// base — the two loops are nearly identical, but <see cref="RtpReceiver"/>'s whole reason to exist
/// is wiring in <see cref="H264RtpDepacketizer"/>, and forcing that decision to be generic (a
/// pluggable depacketizer, a payload-vs-NAL-unit distinction in the event shape) would only pay off
/// if this project grew a third RTP payload kind. Same reasoning this repo has used elsewhere for
/// small, purpose-specific duplication over a premature shared abstraction (see e.g.
/// <c>EveryStage.Caster.Discovery.TerminalDiscoveryClient</c>'s doc comment on its own
/// pending-request tracking).
/// </summary>
public sealed class RawRtpReceiver : IDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    private ushort? _lastSequenceNumber;
    private long _packetsReceived;
    private long _gapEvents;

    /// <summary>Same role/approximation caveats as <see cref="RtpReceiver.PacketsReceived"/> —
    /// deliberately independent code rather than shared, see this class's own doc comment on why.</summary>
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Same role/approximation caveats as <see cref="RtpReceiver.GapEvents"/> — a gap-event
    /// count, not an exact lost-packet count, and indistinguishable from reordering (this class has
    /// no reordering support either).</summary>
    public long GapEvents => Interlocked.Read(ref _gapEvents);

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.
    /// The <c>uint</c> is the packet's RTP timestamp — for the audio stream this is a wall-clock-
    /// derived value sharing the same epoch as the video stream's timestamps (see
    /// <c>Caster.Casting.LiveCastSession</c>'s doc comment), which is what lets
    /// <c>Terminal.Receiving.CastReceiver</c> pace video against audio at all.</summary>
    public event Action<byte[], uint>? PayloadReceived;

    public RawRtpReceiver(int listenPort)
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

            PayloadReceived?.Invoke(packet.Payload.ToArray(), packet.Timestamp);
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
