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
    private readonly RtpReorderBuffer _reorderBuffer = new();
    private Task? _receiveLoop;

    private readonly byte? _expectedPayloadType;
    private long _packetsReceived;
    private long _payloadTypeMismatches;
    private long _dispatchExceptions;

    /// <summary>Same role/approximation caveats as <see cref="RtpReceiver.PacketsReceived"/> —
    /// deliberately independent code rather than shared, see this class's own doc comment on why.</summary>
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Same role/approximation caveats as <see cref="RtpReceiver.GapEvents"/> — a gap-event
    /// count, not an exact lost-packet count, and indistinguishable from reordering (this class has
    /// no reordering support either).</summary>
    public long GapEvents => _reorderBuffer.GapEvents;
    public long PacketsLost => _reorderBuffer.PacketsLost;
    public long PacketsReordered => _reorderBuffer.PacketsReordered;
    public long DuplicatesOrLate => _reorderBuffer.DuplicatesOrLate;

    /// <summary>Same role as <see cref="RtpReceiver.PayloadTypeMismatches"/> — deliberately
    /// independent code rather than shared, see this class's own doc comment on why.</summary>
    public long PayloadTypeMismatches => Interlocked.Read(ref _payloadTypeMismatches);

    /// <summary>Same role as <see cref="RtpReceiver.DispatchExceptions"/> — deliberately
    /// independent code rather than shared, see this class's own doc comment on why.</summary>
    public long DispatchExceptions => Interlocked.Read(ref _dispatchExceptions);

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.
    /// The <c>uint</c> is the packet's RTP timestamp — for the audio stream this is a wall-clock-
    /// derived value sharing the same epoch as the video stream's timestamps (see
    /// <c>Caster.Casting.LiveCastSession</c>'s doc comment), which is what lets
    /// <c>Terminal.Receiving.CastReceiver</c> pace video against audio at all.</summary>
    public event Action<byte[], uint>? PayloadReceived;

    /// <param name="expectedPayloadType">Same meaning as <see cref="RtpReceiver"/>'s own constructor
    /// parameter of the same name — null (the default) preserves this class's original permissive
    /// behavior.</param>
    public RawRtpReceiver(int listenPort, byte? expectedPayloadType = null)
    {
        _socket = new UdpClient(listenPort);
        _expectedPayloadType = expectedPayloadType;
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
            catch (ObjectDisposedException)
            {
                // Same fix, same reasoning as RtpReceiver.ReceiveLoopAsync's identical catch clause
                // — Dispose() below has the exact same unchecked-timeout Wait()-then-dispose-the-
                // socket-regardless shape, so this loop can observe the socket being torn down out
                // from under an in-flight ReceiveAsync as ObjectDisposedException instead of
                // OperationCanceledException in that narrow window. Treated the same as cancellation.
                return;
            }

            if (!RtpPacket.TryDecode(result.Buffer, out var packet)) continue; // not one of ours — ignore.

            if (_expectedPayloadType.HasValue && packet.PayloadType != _expectedPayloadType.Value)
            {
                Interlocked.Increment(ref _payloadTypeMismatches);
                continue; // same "not one of ours" treatment as a failed decode above.
            }

            Interlocked.Increment(ref _packetsReceived);
            foreach (var delivery in _reorderBuffer.Add(packet, DateTimeOffset.UtcNow))
            {
                try
                {
                    PayloadReceived?.Invoke(delivery.Packet.Payload.ToArray(), delivery.Packet.Timestamp);
                }
                catch (Exception)
                {
                // Defensive hardening, not a fix for a confirmed bug: an audit this session ran
                // looking for the same "unguarded exception kills a whole background receive loop
                // forever" shape found three real instances elsewhere (see this library's README)
                // and flagged this exact call site as "one exception-scope layer thinner than it
                // looks" — no concrete reachable trigger exists today (the real subscriber,
                // Terminal.Receiving.CastReceiver.OnAudioPayloadReceived, already wraps its own
                // risky part in its own try/catch), but nothing stops a future change from adding
                // unguarded logic ahead of that inner try. Swallowing here and moving on to the next
                // packet matches this loop's existing philosophy for a bad datagram (a TryDecode
                // failure or PayloadType mismatch just above are also "skip, don't crash the loop")
                // — the alternative would silently and permanently kill this entire audio stream's
                // receive loop, exactly the shape this session already found and fixed three times
                // elsewhere. Counted, not logged: this class has no logging of its own (it's a
                // shared library used by both Terminal and Caster), matching how GapEvents/
                // PayloadTypeMismatches were also added as bare counters well before either got a
                // real diagnostic consumer.
                    Interlocked.Increment(ref _dispatchExceptions);
                }
            }
        }
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
