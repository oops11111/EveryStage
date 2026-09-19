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

    private readonly byte? _expectedPayloadType;
    private ushort? _lastSequenceNumber;
    private long _packetsReceived;
    private long _gapEvents;
    private long _payloadTypeMismatches;
    private long _dispatchExceptions;

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

    /// <summary>How many otherwise-valid RTP packets were dropped because their PayloadType didn't
    /// match <see cref="_expectedPayloadType"/> — 0 always if this receiver was constructed without
    /// one (the pre-existing, permissive default). Not expected to ever be non-zero in this project's
    /// own Caster-to-Terminal traffic (both sides use the same hardcoded constant, see
    /// <c>DiscoveryProtocol.CastStartMessage.PayloadType</c>'s own doc comment) — this exists as a
    /// defensive check against a stray/foreign RTP-shaped datagram on the same port, or a future
    /// protocol-version mismatch, not as a currently-expected occurrence.</summary>
    public long PayloadTypeMismatches => Interlocked.Read(ref _payloadTypeMismatches);

    /// <summary>How many times dispatching a decoded/depacketized NAL unit to
    /// <see cref="NalUnitReceived"/> subscribers threw an exception, caught here rather than left to
    /// kill this receive loop — see <see cref="ReceiveLoopAsync"/>'s own comment on why this exists
    /// as defensive hardening against a not-currently-known bug, not a fix for a confirmed one. Same
    /// bare-counter treatment as <see cref="GapEvents"/>/<see cref="PayloadTypeMismatches"/> before
    /// either had a real diagnostic consumer — expected to stay 0.</summary>
    public long DispatchExceptions => Interlocked.Read(ref _dispatchExceptions);

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.
    /// The <c>bool</c> is the completing RTP packet's Marker bit, i.e. RFC 6184 §5.3 "this was the
    /// last NAL unit of its access unit" — passed through as-is rather than making every consumer
    /// re-derive it, since <see cref="H264RtpPacketizer"/>/<see cref="H264RtpDepacketizer"/> already
    /// encode/decode it symmetrically. The <c>uint</c> is that same packet's RTP timestamp — every
    /// NAL unit belonging to one access unit shares the same value (the sender sets it once per
    /// access unit, see <c>RtpSession.SendNalUnitAsync</c>'s caller), so a consumer reassembling an
    /// access unit from multiple NAL units can take the timestamp from any one of them.</summary>
    public event Action<byte[], bool, uint>? NalUnitReceived;

    /// <param name="expectedPayloadType">When set, a decoded packet whose <see cref="RtpPacket.PayloadType"/>
    /// doesn't match this value is dropped (counted in <see cref="PayloadTypeMismatches"/>) exactly
    /// like a packet that failed to decode at all, rather than being processed — see
    /// <see cref="EveryStage.Transport"/>'s README on why this previously-unused
    /// <c>CastStartMessage.PayloadType</c> field now has a real consumer. Null (the default)
    /// preserves this class's original behavior: accept any successfully-decoded packet regardless
    /// of its PayloadType.</param>
    public RtpReceiver(int listenPort, byte? expectedPayloadType = null)
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
                // Bug found (self-review, same audit that found LiveCastSession's own
                // ObjectDisposedException race on the Caster side) and fixed here: Dispose() below
                // calls _cts.Cancel() then _receiveLoop?.Wait(TimeSpan.FromSeconds(2)) WITHOUT
                // checking whether that wait actually succeeded or merely timed out, then disposes
                // _socket regardless. Under normal conditions ReceiveAsync(token) reacts to
                // cancellation almost immediately (caught as OperationCanceledException above), but
                // if it's ever slower than Dispose()'s 2-second patience — this loop is still
                // between iterations, an unusually slow network stack, whatever — _socket.Dispose()
                // can run while this exact await is still pending, and a UdpClient whose socket gets
                // disposed out from under an in-flight ReceiveAsync throws ObjectDisposedException,
                // not OperationCanceledException. Left uncaught, that would propagate out of this
                // loop's Task.Run as an unobserved exception. Treated the same as cancellation
                // (return, not continue) since by the time the socket is disposed there is nothing
                // left to keep receiving on.
                return;
            }

            if (!RtpPacket.TryDecode(result.Buffer, out var packet)) continue; // not one of ours — ignore.

            if (_expectedPayloadType.HasValue && packet.PayloadType != _expectedPayloadType.Value)
            {
                // Same "not one of ours" treatment as a failed decode above — excluded before
                // TrackSequenceNumber so a foreign/stray packet can't pollute this stream's own
                // sequence-number-based PacketsReceived/GapEvents tracking.
                Interlocked.Increment(ref _payloadTypeMismatches);
                continue;
            }

            TrackSequenceNumber(packet.SequenceNumber);

            try
            {
                var nalUnit = _depacketizer.Process(packet.Payload);
                if (nalUnit != null) NalUnitReceived?.Invoke(nalUnit, packet.Marker, packet.Timestamp);
            }
            catch (Exception)
            {
                // Defensive hardening, not a fix for a confirmed bug: an audit this session ran
                // looking for the same "unguarded exception kills a whole background receive loop
                // forever" shape found three real instances elsewhere (see this library's README)
                // and flagged this exact call site as "one exception-scope layer thinner than it
                // looks" — no concrete reachable trigger exists today (H264RtpDepacketizer.Process
                // itself is defensively bounds-checked, and the real subscriber,
                // Terminal.Receiving.CastReceiver.OnNalUnitReceived, already wraps its own risky part
                // in its own try/catch), but nothing stops a future change from adding unguarded
                // logic ahead of that inner try, or a bug in the depacketizer itself. Swallowing here
                // and moving on to the next packet matches this loop's existing philosophy for a bad
                // datagram (a TryDecode failure or PayloadType mismatch just above are also "skip,
                // don't crash the loop") — the alternative would silently and permanently kill this
                // entire video stream's receive loop, exactly the shape this session already found
                // and fixed three times elsewhere. Counted, not logged: this class has no logging of
                // its own (it's a shared library used by both Terminal and Caster), matching how
                // GapEvents/PayloadTypeMismatches were also added as bare counters well before either
                // got a real diagnostic consumer.
                Interlocked.Increment(ref _dispatchExceptions);
            }
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
