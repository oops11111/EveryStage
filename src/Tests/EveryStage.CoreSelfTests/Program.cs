using EveryStage.Discovery;
using EveryStage.Transport;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.Logging;
using System.IO.Compression;

var failures = new List<string>();

await RunAsync("RTP/H.264 transport", async () =>
{
    var result = await TransportSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Raw audio RTP transport", async () =>
{
    var result = await RawTransportSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Discovery protocol", async () =>
{
    var result = await DiscoveryProtocolSelfTest.RunAsync();
    return (result.Success, result.FailureReason);
});

await RunAsync("Device identity persistence", () =>
{
    var result = DeviceIdentitySelfTest.Run();
    return Task.FromResult((result.Success, result.FailureReason));
});

await RunAsync("Output state machine", () => Task.FromResult(TestOutputStateMachine()));
await RunAsync("Media queue navigation", () => Task.FromResult(TestMediaQueue()));
await RunAsync("Settings migration", () => Task.FromResult(TestSettingsMigration()));
await RunAsync("Log export", () => Task.FromResult(TestLogExport()));
await RunAsync("Cast audio frames carry their own capture time (A-3)", () => Task.FromResult(TestPcmTimeline()));
await RunAsync("Log writer: daily retention, write failures counted, folder recreated, export while writing", () => Task.FromResult(TestDailyRollingLogWriter()));
await RunAsync("Unreadable stores preserve original data", () => Task.FromResult(TestUnreadableStores()));
await RunAsync("PDF viewport layout (resize keeps content position)", () => Task.FromResult(TestPdfViewport()));
await RunAsync("Document playback settings persist and clone", () => Task.FromResult(TestDocumentSettings()));
await RunAsync("Multichannel PCM folds to stereo without clipping (5.1 / 7.1 / quad)", () => Task.FromResult(TestPcmDownmix()));
await RunAsync("PCM converts a mid-stream format change back to the clock format (A-25)", () => Task.FromResult(TestPcmConverter()));
await RunAsync("RTP idle gap timeout", TestIdleGapTimeout);
await RunAsync("H.264 payload format robustness", () => Task.FromResult(TestPayloadFormatRobustness()));
await RunAsync("Access unit assembly", () => Task.FromResult(TestAccessUnitAssembly()));
await RunAsync("Replay freshness bound", () => Task.FromResult(TestReplayFreshness()));
await RunAsync("Queue position survives list edits", () => Task.FromResult(TestQueuePositionAfterListEdits()));
await RunAsync("Media authentication and replay protection", () => Task.FromResult(TestMediaAuthentication()));
await RunAsync("Authenticated RTP delivery", TestAuthenticatedDelivery);
await RunAsync("Authenticated cast-start acknowledgement", () =>
{
    string key = PairingSecurity.GenerateKey();
    Guid session = Guid.NewGuid();
    Guid terminal = Guid.NewGuid();
    foreach (bool ready in new[] { true, false })
    {
        bool pending = !ready;
        var ack = new DiscoveryProtocol.CastStartAckMessage
        { MediaSessionId = session, Ready = ready, Pending = pending, Error = ready ? null : "display unavailable" };
        PairingSecurity.Sign(ack, terminal, key);
        var decoded = DiscoveryProtocol.Decode(DiscoveryProtocol.Encode(ack)) as DiscoveryProtocol.CastStartAckMessage;
        if (decoded == null || decoded.MediaSessionId != session || decoded.SenderDeviceId != terminal
            || decoded.Ready != ready || decoded.Pending != pending || decoded.Error != ack.Error || !PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Start acknowledgement lost fields or authentication."));
        decoded.Ready = !ready;
        if (PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Tampered readiness was accepted."));
        decoded.Ready = ready;
        decoded.Pending = !pending;
        if (PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Tampered pending state was accepted."));
        decoded.Pending = pending;
        decoded.MediaSessionId = Guid.NewGuid();
        if (PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Acknowledgement could be moved to another session."));
    }
    return Task.FromResult<(bool, string?)>((true, null));
});
await RunAsync("NACK protocol authentication and bounded sequence list", () =>
{
    string key = PairingSecurity.GenerateKey();
    var nack = new DiscoveryProtocol.CastNackMessage
    {
        DeviceId = Guid.NewGuid(), MediaSessionId = Guid.NewGuid(),
        MissingVideoSequences = Enumerable.Range(0, 4).Select(i => (ushort)(100 + i)).ToArray(),
    };
    PairingSecurity.Sign(nack, Guid.NewGuid(), key);
    var decoded = DiscoveryProtocol.Decode(DiscoveryProtocol.Encode(nack)) as DiscoveryProtocol.CastNackMessage;
    if (decoded == null || decoded.MissingVideoSequences.Length != 4 || !PairingSecurity.Verify(decoded, key))
        return Task.FromResult<(bool, string?)>((false, "NACK fields or authentication were lost."));
    decoded.MissingVideoSequences[0]++;
    if (PairingSecurity.Verify(decoded, key)) return Task.FromResult<(bool, string?)>((false, "Tampered NACK was accepted."));
    return Task.FromResult<(bool, string?)>((true, null));
});
await RunAsync("XOR FEC single-packet recovery", () =>
{
    var packets = Enumerable.Range(0, XorFecCodec.BlockSize)
        .Select(i => new RtpPacket { PayloadType = 96, Ssrc = 7, SequenceNumber = (ushort)(400 + i),
            Timestamp = (uint)(900 + i), Marker = i == 7,
            Payload = Enumerable.Range(0, 20 + i).Select(value => (byte)(value + i)).ToArray() }).ToArray();
    byte[] parity = XorFecCodec.CreateParity(packets, 400);
    var received = packets.Select((packet, index) => (packet, index))
        .Where(entry => entry.index != 3)
        .ToDictionary(entry => (ushort)(400 + entry.index), entry => entry.packet);
    if (!XorFecCodec.TryRecover(parity, received, out var recovered)
        || recovered.SequenceNumber != 403 || !packets[3].Payload.Span.SequenceEqual(recovered.Payload.Span))
        return Task.FromResult<(bool, string?)>((false, "FEC did not recover the single missing packet."));
    received.Remove(402);
    if (XorFecCodec.TryRecover(parity, received, out _))
        return Task.FromResult<(bool, string?)>((false, "FEC incorrectly recovered with multiple missing packets."));
    received[400] = new RtpPacket
    {
        PayloadType = 96, Ssrc = 999, SequenceNumber = 400, Timestamp = packets[0].Timestamp,
        Marker = packets[0].Marker, Payload = packets[0].Payload,
    };
    received[402] = packets[2];
    received.Remove(403);
    if (XorFecCodec.TryRecover(parity, received, out _))
        return Task.FromResult<(bool, string?)>((false, "FEC accepted a packet from a different SSRC."));
    var mtuPackets = packets.Select((packet, index) => new RtpPacket
    {
        PayloadType = packet.PayloadType, Ssrc = packet.Ssrc,
        SequenceNumber = packet.SequenceNumber, Timestamp = packet.Timestamp,
        Marker = index == XorFecCodec.BlockSize - 1, Payload = new byte[1268]
    }).ToArray();
    if (XorFecCodec.CreateParity(mtuPackets, 400).Length + RtpPacket.FixedHeaderSize + 56 > 1400)
        return Task.FromResult<(bool, string?)>((false, "FEC parity exceeded the RTP MTU budget."));
    return Task.FromResult<(bool, string?)>((true, null));
});
await RunAsync("Media protocol capability negotiation", () =>
{
    var beacon = new DiscoveryProtocol.BeaconMessage { DeviceId = Guid.NewGuid(), DeviceName = "test", MediaAuthenticationVersion = 1 };
    var decoded = DiscoveryProtocol.Decode(DiscoveryProtocol.Encode(beacon)) as DiscoveryProtocol.BeaconMessage;
    if (decoded?.MediaAuthenticationVersion != 1) return Task.FromResult<(bool, string?)>((false, "Media capability lost in beacon round trip."));
    var json = System.Text.Json.Nodes.JsonNode.Parse(DiscoveryProtocol.Encode(beacon))!.AsObject();
    json.Remove(nameof(DiscoveryProtocol.BeaconMessage.MediaAuthenticationVersion));
    var legacy = DiscoveryProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())) as DiscoveryProtocol.BeaconMessage;
    return Task.FromResult<(bool, string?)>((legacy?.MediaAuthenticationVersion == 0, "Legacy beacon was not recognized as unsupported."));
});
await RunAsync("Adaptive capture congestion policy", () =>
{
    var policy = new EveryStage.Caster.Casting.CaptureRateController();
    if (policy.CurrentStride != 1) return Task.FromResult<(bool, string?)>((false, "Policy did not start at full cadence."));
    policy.Update(6, TimeSpan.FromMilliseconds(50), null, 0, 60, 0);
    if (policy.CurrentStride != 2) return Task.FromResult<(bool, string?)>((false, "Moderate pressure did not reduce capture cadence."));
    policy.Update(0, TimeSpan.Zero, null, 50, 60, 0);
    if (policy.CurrentStride != 3) return Task.FromResult<(bool, string?)>((false, "Queue pressure did not trigger severe throttling."));
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    if (policy.CurrentStride != 3) return Task.FromResult<(bool, string?)>((false, "Policy recovered before hysteresis threshold."));
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    if (policy.CurrentStride != 2) return Task.FromResult<(bool, string?)>((false, "Policy did not recover one step after three healthy reports."));
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 0);
    if (policy.CurrentStride != 1) return Task.FromResult<(bool, string?)>((false, "Policy did not return to full cadence after sustained health."));
    policy.Update(0, TimeSpan.Zero, null, 0, 60, 1);
    if (policy.CurrentStride != 3) return Task.FromResult<(bool, string?)>((false, "New encoder backpressure was not treated as severe pressure."));
    return Task.FromResult<(bool, string?)>((true, null));
});

async Task<(bool, string?)> TestAuthenticatedDelivery()
{
    using var probe = new System.Net.Sockets.UdpClient(0);
    int port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    probe.Dispose();
    byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    Guid session = Guid.NewGuid();
    using var authentication = new MediaPacketAuthentication(key, session);
    using var receiver = new RawRtpReceiver(port, 96, authentication, System.Net.IPAddress.Loopback);
    var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);
    using var sender = new RtpSession(endpoint, 96, authentication: new MediaPacketAuthentication(key, session));
    using var foreign = new RtpSession(endpoint, 96);
    int rejectedDelivered = 0;
    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.PayloadReceived += (bytes, _) =>
    {
        if (bytes.SequenceEqual(new byte[] { 42 })) received.TrySetResult(true);
        else Interlocked.Increment(ref rejectedDelivered);
    };
    receiver.Start();
    await foreign.SendRawPayloadAsync(new byte[] { 99 }, 1);
    await sender.SendRawPayloadAsync(new byte[] { 42 }, 2);
    try { await received.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (TimeoutException) { return (false, "Authenticated media was not delivered."); }
    await Task.Delay(100);
    return (rejectedDelivered == 0 && receiver.PacketsReceived == 1, "Unauthenticated media reached the receiver.");
}

(bool, string?) TestMediaAuthentication()
{
    byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    Guid session = Guid.NewGuid();
    using var sender = new MediaPacketAuthentication(key, session);
    using var receiver = new MediaPacketAuthentication(key, session);
    using var otherSession = new MediaPacketAuthentication(key, Guid.NewGuid());
    using var wrongKey = new MediaPacketAuthentication(new byte[32], session);
    byte[] first = sender.Protect(new byte[] { 1, 2, 3 });
    byte[] second = sender.Protect(new byte[] { 4 });
    byte[] tampered = first.ToArray();
    tampered[24] ^= 1;
    if (receiver.TryUnprotect(tampered, out _) || otherSession.TryUnprotect(first, out _) || wrongKey.TryUnprotect(first, out _))
        return (false, "Accepted tampering, a foreign session, or an incorrect key.");
    if (!receiver.TryUnprotect(second, out _) || !receiver.TryUnprotect(first, out var payload) || !payload.SequenceEqual(new byte[] { 1, 2, 3 }))
        return (false, "Valid out-of-order packets did not round trip.");
    if (receiver.TryUnprotect(first, out _)) return (false, "Replay accepted.");
    byte[] old = sender.Protect(new byte[] { 5 });
    for (int i = 0; i < 1025; i++)
        if (!receiver.TryUnprotect(sender.Protect(new byte[] { 6 }), out _)) return (false, "Valid packet rejected.");
    if (receiver.TryUnprotect(old, out _) || receiver.TryUnprotect(new byte[8], out _))
        return (false, "Expired or truncated packet accepted.");
    return (true, null);
}

// Regression cases for the four RFC 6184 audit findings fixed alongside them. None of these
// were reachable through TransportSelfTest, which only sends well-formed packets over a clean
// loopback socket: every one needs a packet that is malformed or a type this project never
// emits itself, which only a peer implementation or a hostile sender would produce.
(bool, string?) TestPayloadFormatRobustness()
{
    // Aggregation packets (24-27), FU-B (29) and reserved types (0, 30-31) must be discarded.
    // Their first byte is an aggregation/fragmentation header, not a NAL unit header, so the
    // old "anything that is not FU-A is already a complete NAL unit" fallthrough handed the
    // decoder a NAL unit fabricated from a misread header.
    var depacketizer = new H264RtpDepacketizer();
    byte[] unsupportedTypes = { 0, 24, 25, 26, 27, 29, 30, 31 };
    foreach (byte type in unsupportedTypes)
    {
        byte[] packet = { (byte)(0x60 | type), 0x11, 0x22, 0x33 };
        if (depacketizer.Process(packet) != null)
            return (false, $"RTP payload type {type} was passed through as a complete NAL unit instead of discarded.");
    }
    if (depacketizer.UnsupportedPacketTypes != unsupportedTypes.Length)
        return (false, $"Expected {unsupportedTypes.Length} unsupported packet types counted, got {depacketizer.UnsupportedPacketTypes}.");

    // Single NAL Unit packets (types 1-23) must still pass through byte-for-byte.
    byte[] single = { 0x65, 0xAA, 0xBB };
    byte[]? passedThrough = depacketizer.Process(single);
    if (passedThrough == null || !passedThrough.SequenceEqual(single))
        return (false, "A Single NAL Unit packet was not passed through unchanged.");

    // A truncated FU-A must discard the in-progress NAL unit. Previously it was skipped with the
    // fragment buffer left intact, so the following fragments concatenated across the hole and
    // produced a silently corrupt NAL unit. The loss path never covers this: a truncated packet
    // is a packet that arrived, so the reorder buffer reports no sequence-number gap.
    var nal = new byte[300];
    nal[0] = 0x65;
    for (int i = 1; i < nal.Length; i++) nal[i] = (byte)i;
    var fragments = H264RtpPacketizer.Packetize(nal, isLastNalOfAccessUnit: true, maxPayloadSize: 100).ToList();
    if (fragments.Count < 3)
        return (false, "Test setup: expected a 300-byte NAL unit to fragment into at least 3 FU-A packets.");

    var spliced = new H264RtpDepacketizer();
    spliced.Process(fragments[0].Bytes);
    if (spliced.Process(new[] { fragments[1].Bytes.Span[0] }) != null)
        return (false, "A truncated FU-A packet produced a NAL unit.");
    byte[]? corrupt = null;
    for (int i = 2; i < fragments.Count; i++) corrupt = spliced.Process(fragments[i].Bytes) ?? corrupt;
    if (corrupt != null)
        return (false, "A truncated FU-A packet was skipped and the remaining fragments were spliced into a corrupt NAL unit.");
    if (spliced.MalformedFragments != 1)
        return (false, $"Expected 1 malformed fragment counted, got {spliced.MalformedFragments}.");

    // An empty NAL unit is not representable in this payload format and must not go on the wire.
    if (H264RtpPacketizer.Packetize(ReadOnlyMemory<byte>.Empty, isLastNalOfAccessUnit: true, maxPayloadSize: 100).Any())
        return (false, "An empty NAL unit produced an RTP payload.");

    // The undamaged FU-A round trip must still reproduce the original NAL unit exactly.
    var roundTrip = new H264RtpDepacketizer();
    byte[]? rebuilt = null;
    foreach (var fragment in fragments) rebuilt = roundTrip.Process(fragment.Bytes) ?? rebuilt;
    if (rebuilt == null || !rebuilt.SequenceEqual(nal))
        return (false, "An intact FU-A round trip did not reproduce the original NAL unit.");

    return (true, null);
}

// Regression cases for the access-unit boundary rule. The marker bit alone used to be the only
// boundary, so losing the one packet carrying it merged two frames into a single access unit.
(bool, string?) TestAccessUnitAssembly()
{
    static byte[] Nal(byte type, byte fill, int length)
    {
        var nal = new byte[length];
        nal[0] = (byte)(0x60 | type);
        for (int i = 1; i < length; i++) nal[i] = fill;
        return nal;
    }
    static byte[] AnnexB(params byte[][] nalUnits) =>
        nalUnits.SelectMany(nal => new byte[] { 0, 0, 0, 1 }.Concat(nal)).ToArray();

    var assembler = new AccessUnitAssembler();

    // One frame, two NAL units, same RTP timestamp, marker bit on the last one.
    byte[] sps = Nal(7, 0xAA, 8);
    byte[] idr = Nal(5, 0xBB, 16);
    if (assembler.Add(sps, isLastNalOfAccessUnit: false, timestamp: 1000) != null)
        return (false, "An access unit was completed before its marker bit arrived.");
    byte[]? accessUnit = assembler.Add(idr, isLastNalOfAccessUnit: true, timestamp: 1000);
    if (accessUnit == null)
        return (false, "The marker bit did not complete the access unit.");
    if (!accessUnit.SequenceEqual(AnnexB(sps, idr)))
        return (false, "The access unit is not the Annex B concatenation of its NAL units.");
    if (assembler.IncompleteAccessUnitsDropped != 0)
        return (false, "A complete access unit was counted as dropped.");

    // The packet carrying the marker bit of frame A is lost. The RTP timestamp changing on the
    // first packet of frame B is what proves A ended: without that second criterion the two
    // frames concatenate into one access unit and the decoder is handed two frames as one.
    byte[] frameA = Nal(1, 0xCC, 12);
    byte[] frameB = Nal(1, 0xDD, 12);
    if (assembler.Add(frameA, isLastNalOfAccessUnit: false, timestamp: 4000) != null)
        return (false, "An access unit was completed without a marker bit.");
    byte[]? afterLostMarker = assembler.Add(frameB, isLastNalOfAccessUnit: true, timestamp: 7000);
    if (afterLostMarker == null)
        return (false, "The access unit following a lost marker bit was never completed.");
    if (!afterLostMarker.SequenceEqual(AnnexB(frameB)))
        return (false, $"Losing a marker-bit packet produced a {afterLostMarker.Length}-byte access unit instead of dropping the unterminated frame and emitting the {AnnexB(frameB).Length}-byte one.");
    if (assembler.IncompleteAccessUnitsDropped != 1)
        return (false, $"Expected 1 incomplete access unit dropped, got {assembler.IncompleteAccessUnitsDropped}.");

    // Reset discards a half-collected frame without counting it as a loss.
    assembler.Add(frameA, isLastNalOfAccessUnit: false, timestamp: 9000);
    assembler.Reset();
    byte[]? afterReset = assembler.Add(frameB, isLastNalOfAccessUnit: true, timestamp: 9000);
    if (afterReset == null || !afterReset.SequenceEqual(AnnexB(frameB)))
        return (false, "Reset did not discard the half-collected frame.");

    return (true, null);
}

// Regression case for the cast_start idempotent-retry path, which used to return before the replay
// guard ran at all - so a captured cast_start stayed replayable indefinitely. Exercised here at the
// ReplayGuard primitive, since DiscoveryService itself is not compiled into these self-tests.
(bool, string?) TestReplayFreshness()
{
    var now = DateTimeOffset.UtcNow;
    static DiscoveryProtocol.CastStopMessage Message(DateTimeOffset issuedAt) => new()
    {
        SenderDeviceId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        IssuedAtUtc = issuedAt,
    };

    // A retry legitimately repeats a MessageId, so de-duplication must not gate it - but a message
    // issued outside the clock-skew window must still be refused on that same path.
    var fresh = Message(now);
    if (!ReplayGuard.IsFresh(fresh, now)) return (false, "A message issued now was judged stale.");
    // The retry path depends on IsFresh ignoring what the replay guard has already recorded (audit C-15:
    // this used to repeat the line above, which cannot fail independently).
    var seenGuard = new ReplayGuard();
    if (!seenGuard.TryAccept(fresh, now)) return (false, "The replay guard refused a fresh message.");
    if (seenGuard.TryAccept(fresh, now)) return (false, "The replay guard accepted a duplicate.");
    if (!ReplayGuard.IsFresh(fresh, now)) return (false, "IsFresh refused a retry the replay guard had already recorded - an idempotent cast_start retry would be dropped.");

    var stale = Message(now - ReplayGuard.DefaultAllowedClockSkew - TimeSpan.FromSeconds(1));
    if (ReplayGuard.IsFresh(stale, now))
        return (false, "A message issued outside the clock-skew window was accepted as fresh - a captured packet would stay replayable forever on the idempotent-retry path.");

    var future = Message(now + ReplayGuard.DefaultAllowedClockSkew + TimeSpan.FromSeconds(1));
    if (ReplayGuard.IsFresh(future, now)) return (false, "A message issued too far in the future was accepted as fresh.");

    var unidentified = Message(now);
    unidentified.MessageId = Guid.Empty;
    if (ReplayGuard.IsFresh(unidentified, now)) return (false, "A message with no identifier was accepted as fresh.");

    // TryAccept must still enforce BOTH halves: freshness and single use.
    var guard = new ReplayGuard();
    var once = Message(now);
    if (!guard.TryAccept(once, now)) return (false, "TryAccept refused a fresh, unseen message.");
    if (guard.TryAccept(once, now)) return (false, "TryAccept admitted the same message twice.");
    if (guard.TryAccept(stale, now)) return (false, "TryAccept admitted a stale message.");

    return (true, null);
}

// Regression cases for queue position being resolved by a cached index. The UI edits Activity.Files
// directly (ActivitiesPanel add/remove/reorder, and the playback-error Toast's 移除) without telling
// PlaybackEngine, so any cached index goes stale and the next advance silently skips a file.
(bool, string?) TestQueuePositionAfterListEdits()
{
    static MediaFile F(string name) => new() { Id = Guid.NewGuid(), SourcePath = name, Kind = MediaKind.Image };
    static int Next(Activity activity, MediaFile? current, int cachedIndex, int delta) =>
        PlaybackQueueNavigator.FindNextPlayable(
            activity,
            PlaybackQueueNavigator.ResolveScanOrigin(activity, current, cachedIndex, delta),
            delta);

    var a = F("a"); var b = F("b"); var c = F("c"); var d = F("d");

    // Undisturbed list: position still comes out right.
    var plain = new Activity { Name = "queue-test", Files = { a, b, c } };
    if (Next(plain, b, 1, 1) != 2) return (false, "Forward advance on an unmodified list did not reach the next file.");
    if (Next(plain, b, 1, -1) != 0) return (false, "Backward advance on an unmodified list did not reach the previous file.");

    // A file BEFORE the current one is removed: every later index shifts down by one, so a cached
    // index now points past where it should and the file that moved into the gap is skipped.
    var shifted = new Activity { Name = "queue-test", Files = { a, b, c, d } };
    shifted.Files.Remove(a); // cached index for c was 2; c now lives at 1
    int afterShift = Next(shifted, c, 2, 1);
    if (afterShift < 0) return (false, "Removing an earlier file ended the activity while a later file was still queued.");
    if (!ReferenceEquals(shifted.Files[afterShift], d))
        return (false, $"Removing an earlier file skipped past the next file (landed on '{shifted.Files[afterShift].SourcePath}', expected 'd').");

    // The currently playing file is removed: whatever shifted into its slot must play next, not be
    // stepped over.
    var removedCurrent = new Activity { Name = "queue-test", Files = { a, b, c } };
    removedCurrent.Files.Remove(b); // b was index 1; c now occupies index 1
    int afterRemoval = Next(removedCurrent, b, 1, 1);
    if (afterRemoval < 0) return (false, "Removing the playing file ended the activity even though a later file remained.");
    if (!ReferenceEquals(removedCurrent.Files[afterRemoval], c))
        return (false, "Removing the playing file skipped the file that took its place.");

    // Backward from a removed file: everything before the vacated slot is unmoved.
    var backward = new Activity { Name = "queue-test", Files = { a, b, c } };
    backward.Files.Remove(b);
    int back = Next(backward, b, 1, -1);
    if (back < 0 || !ReferenceEquals(backward.Files[back], a))
        return (false, "Backward advance from a removed file did not land on the file before it.");

    // Removing the last file leaves nothing to advance to, which is an ending, not a skip.
    var tail = new Activity { Name = "queue-test", Files = { a, b } };
    tail.Files.Remove(b);
    if (Next(tail, b, 1, 1) >= 0) return (false, "Advancing past a removed final file invented a file to play.");

    return (true, null);
}
async Task<(bool, string?)> TestIdleGapTimeout()
{
    using var portProbe = new System.Net.Sockets.UdpClient(0);
    int port = ((System.Net.IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
    portProbe.Dispose();
    using var receiver = new RawRtpReceiver(port, 96);
    using var sender = new System.Net.Sockets.UdpClient();
    var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.PayloadReceived += (bytes, _) => { if (bytes.SequenceEqual(new byte[] { 3 })) delivered.TrySetResult(true); };
    receiver.Start();
    foreach (ushort sequence in new ushort[] { 1, 3 })
    {
        var packet = new RtpPacket { PayloadType = 96, SequenceNumber = sequence, Ssrc = 1,
            Timestamp = sequence, Payload = new byte[] { (byte)sequence } };
        await sender.SendAsync(packet.Encode(), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
    }
    try { await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (TimeoutException) { return (false, "Buffered packet never delivered without subsequent traffic."); }
    return (receiver.PacketsLost == 1, "Expected exactly one missing packet.");
}

(bool, string?) TestUnreadableStores()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-StoreTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        string libraryPath = Path.Combine(directory, "library.json");
        var library = new FileLibraryStore(libraryPath);
        library.Import("original.png");
        string originalLibrary = File.ReadAllText(libraryPath);
        Guid originalId = library.Files[0].Id;
        using (var writeLock = new FileStream(libraryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { library.Import("unsaved.png"); return (false, "Import unexpectedly succeeded under lock."); }
            catch (IOException) { }
            if (library.Files.Count != 1 || library.Files[0].Id != originalId)
                return (false, "Failed import was not rolled back.");
            try { library.Remove(originalId); return (false, "Removal unexpectedly succeeded under lock."); }
            catch (IOException) { }
            if (library.Files.Count != 1 || library.Files[0].Id != originalId)
                return (false, "Failed removal was not rolled back.");
        }
        if (File.ReadAllText(libraryPath) != originalLibrary)
            return (false, "Failed writes changed original data.");
        FileLibraryStore failedLibrary;
        using (var locked = new FileStream(libraryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failedLibrary = new FileLibraryStore(libraryPath);
        try { failedLibrary.Import("replacement.png"); return (false, "Import allowed after failed load."); }
        catch (IOException) { }
        try { failedLibrary.Save(); return (false, "Library save allowed after failed load."); }
        catch (IOException) { }
        if (failedLibrary.Files.Count != 0 || File.ReadAllText(libraryPath) != originalLibrary)
            return (false, "Failed import changed memory or original library.");

        string scenarioPath = Path.Combine(directory, "scenarios.json");
        File.WriteAllText(scenarioPath, "{\"Scenarios\":[],\"CurrentScenarioId\":null}");
        string originalScenario = File.ReadAllText(scenarioPath);
        var repository = new ScenarioRepository(scenarioPath);
        ScenarioStore failedStore;
        using (var locked = new FileStream(scenarioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failedStore = repository.Load();
        try { repository.Save(failedStore); return (false, "Scenario save allowed after failed load."); }
        catch (IOException) { }
        if (File.ReadAllText(scenarioPath) != originalScenario)
            return (false, "Original scenarios overwritten.");
        repository.Save(repository.Load());
        string settingsPath = Path.Combine(directory, "settings.json");
        var settings = new SettingsStore(settingsPath);
        settings.Save(new AppSettings { CastSwitchDefaultOn = false, DefaultStayDurationSeconds = 12 });
        string originalSettings = File.ReadAllText(settingsPath);
        SettingsStore failedSettings;
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failedSettings = new SettingsStore(settingsPath);
        try { failedSettings.Save(new AppSettings()); return (false, "Settings save allowed after failed load."); }
        catch (IOException) { }
        if (File.ReadAllText(settingsPath) != originalSettings)
            return (false, "Original settings overwritten after failed load.");

        string pairedPath = Path.Combine(directory, "paired.json");
        var paired = new PairedDeviceStore(pairedPath);
        var pairedDevice = new PairedDevice { DeviceId = Guid.NewGuid(), DeviceName = "paired" };
        paired.Upsert(pairedDevice);
        string originalPaired = File.ReadAllText(pairedPath);
        PairedDeviceStore failedPaired;
        using (var locked = new FileStream(pairedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failedPaired = new PairedDeviceStore(pairedPath);
        try { failedPaired.Upsert(new PairedDevice { DeviceId = Guid.NewGuid(), DeviceName = "replacement" }); return (false, "Paired-device save allowed after failed load."); }
        catch (IOException) { }
        if (File.ReadAllText(pairedPath) != originalPaired)
            return (false, "Original paired-device data overwritten after failed load.");
        return (true, null);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

if (failures.Count == 0)
{
    Console.WriteLine("All core self-tests passed.");
    return 0;
}

(bool, string?) TestLogExport()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-LogExportTests", Guid.NewGuid().ToString("N"));
    string logs = Path.Combine(directory, "logs");
    string zip = Path.Combine(directory, "export.zip");
    Directory.CreateDirectory(Path.Combine(logs, "playback"));
    try
    {
        File.WriteAllText(Path.Combine(logs, "playback", "playback-20260920.log"), "{}\n");
        if (LogExporter.Export(zip, logs) != 1) return (false, "Exported log count was incorrect.");
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "playback/playback-20260920.log")
            return (false, "ZIP did not preserve the relative log path.");
        return (true, null);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

Console.Error.WriteLine($"{failures.Count} core self-test(s) failed:");
foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
return 1;

async Task RunAsync(string name, Func<Task<(bool Success, string? FailureReason)>> test)
{
    try
    {
        var result = await test();
        if (result.Success)
        {
            Console.WriteLine($"PASS: {name}");
            return;
        }

        failures.Add($"{name}: {result.FailureReason ?? "unknown failure"}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
    }
}

(bool, string?) TestOutputStateMachine()
{
    var machine = new OutputStateMachine();
    var observed = new List<OutputState>();
    machine.StateChanged += _ => throw new InvalidOperationException("subscriber isolation probe");
    machine.StateChanged += observed.Add;

    machine.SetCastSwitch(false);
    if (machine.RequestLocalFilePlayback()) return (false, "Local playback bypassed the disabled cast switch.");
    if (!machine.AcceptDeviceCastRequest()) return (false, "Device cast was rejected while the switch was off.");
    machine.SetCastSwitch(true);
    if (!machine.RequestLocalFilePlayback()) return (false, "Local playback was rejected while the cast switch was on.");
    machine.SetCastSwitch(false);
    if (machine.State != OutputState.Idle || machine.Source != ProgramSource.None)
        return (false, "Turning the cast switch off did not retract local Program output.");
    if (!machine.AcceptDeviceCastRequest()) return (false, "Device cast was rejected while the switch was off after local retraction.");
    machine.SetCastSwitch(true);
    machine.SetCastSwitch(false);
    if (machine.State != OutputState.Active || machine.Source != ProgramSource.DeviceCast)
        return (false, "Turning the cast switch off interrupted a device cast.");
    machine.Disconnect();
    if (machine.State != OutputState.Idle || machine.CastSwitchOn) return (false, "Disconnect changed an independent state axis.");
    if (!observed.SequenceEqual(new[] { OutputState.Active, OutputState.Idle, OutputState.Active, OutputState.Idle }))
        return (false, "State events were missing after a subscriber threw.");
    return (true, null);
}

(bool, string?) TestMediaQueue()
{
    static MediaFile File(string name, bool background = false) => new()
    {
        SourcePath = name,
        Kind = background ? MediaKind.Audio : MediaKind.Image,
        IsBackgroundAudio = background,
    };

    var background = File("background.mp3", true);
    var activity = new Activity { Name = "queue" };
    activity.Files.AddRange(new[] { File("first.png"), background, File("last.png") });
    var encountered = new List<MediaFile>();

    int next = PlaybackQueueNavigator.FindNextPlayable(activity, 0, 1, encountered.Add);
    if (next != 2 || encountered.Count != 1 || !ReferenceEquals(encountered[0], background))
        return (false, "Forward navigation did not transparently pass background audio.");
    if (PlaybackQueueNavigator.FindNextPlayable(activity, 2, 1) != -1)
        return (false, "Queue advanced beyond its end.");
    if (PlaybackQueueNavigator.FindNextPlayable(activity, 2, -1) != 0)
        return (false, "Reverse navigation did not skip background audio.");
    try
    {
        PlaybackQueueNavigator.FindNextPlayable(activity, 0, 2);
        return (false, "Queue accepted a non-adjacent navigation delta.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
    return (true, null);
}

(bool, string?) TestSettingsMigration()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-CoreSelfTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "settings.json");
    try
    {
        File.WriteAllText(path, "{\"CastSwitchDefaultOn\":false,\"PreferredMonitorDeviceName\":\"DISPLAY-LEGACY\",\"DefaultStayDurationSeconds\":15}");
        var migrated = new SettingsStore(path).Current;
        if (migrated.SchemaVersion != AppSettings.CurrentSchemaVersion || migrated.Theme != AppTheme.Dark
            || migrated.CastSwitchDefaultOn
            || migrated.PreferredMonitorDeviceName != "DISPLAY-LEGACY" || migrated.DefaultStayDurationSeconds != 15)
            return (false, "Legacy settings were not preserved during migration.");

        File.WriteAllText(path, "{\"SchemaVersion\":999,\"CastSwitchDefaultOn\":false}");
        var future = new SettingsStore(path).Current;
        if (future.SchemaVersion != AppSettings.CurrentSchemaVersion || future.Theme != AppTheme.Dark
            || !future.CastSwitchDefaultOn)
            return (false, "Future-version settings did not fail safe to defaults.");
        return (true, null);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

(bool, string?) TestPdfViewport()
{
    var pages = Enumerable.Range(0, 120).Select(i => i % 7 == 3 ? new System.Drawing.SizeF(842, 595) : new System.Drawing.SizeF(595, 842)).ToArray();
    // Same content position in different window sizes: the page and the offset inside it survive.
    var state = new EveryStage.Terminal.Documents.PdfViewportState(57, 0.42);
    foreach (var size in new[] { new System.Drawing.Size(1280, 720), new System.Drawing.Size(640, 300), new System.Drawing.Size(1920, 1080), new System.Drawing.Size(333, 777) })
    {
        var layout = new EveryStage.Terminal.Documents.PdfLayout(pages, size, state);
        var back = layout.StateAt(layout.ScrollOffset);
        if (back.PageIndex != 57 || Math.Abs(back.PageOffset - 0.42) > 0.002)
            return (false, $"Viewport {size}: state {state} came back as {back}.");
        var (first, last) = layout.VisiblePages(layout.ScrollOffset, preload: 2);
        if (last - first > 8) return (false, $"Viewport {size}: {last - first + 1} pages scheduled — should be the visible few plus preload.");
        if (first > 57 || last < 57) return (false, $"Viewport {size}: page 57 not among the rendered pages {first}..{last}.");
    }
    // Scrolling through the whole document visits every page in order.
    var walk = new EveryStage.Terminal.Documents.PdfLayout(pages, new System.Drawing.Size(800, 600), EveryStage.Terminal.Documents.PdfViewportState.Start);
    int previous = -1;
    for (double y = 0; y <= walk.MaxScroll; y += 97)
    {
        int page = walk.StateAt(y).PageIndex;
        if (page < previous || page > previous + 1) return (false, $"Scrolling skipped from page {previous} to {page}.");
        previous = page;
    }
    if (previous < 118) return (false, $"Scrolling to the end only reached page {previous}.");
    // Single-page mode lays out only the current page; zoom modes behave as named.
    var single = new EveryStage.Terminal.Documents.PdfLayout(pages, new System.Drawing.Size(800, 600),
        new EveryStage.Terminal.Documents.PdfViewportState(10, 0, EveryStage.Terminal.Documents.PdfDisplayMode.SinglePage, EveryStage.Terminal.Documents.PdfZoomMode.FitPage));
    if (single.FirstLaidOutPage != 10 || single.LastLaidOutPage != 10) return (false, "Single-page mode laid out more than one page.");
    var fit = single.PageRect(10, single.ScrollOffset);
    if (fit.Height > 600 || fit.Width > 800 || (fit.Height < 560 && fit.Width < 760)) return (false, $"Fit-page rectangle {fit} does not fit the 800x600 view.");
    var actual = new EveryStage.Terminal.Documents.PdfLayout(pages, new System.Drawing.Size(800, 600),
        new EveryStage.Terminal.Documents.PdfViewportState(0, 0, ZoomMode: EveryStage.Terminal.Documents.PdfZoomMode.ActualSize));
    if (Math.Abs(actual.RenderSize(0).Width - 793) > 2) return (false, $"Actual size rendered A4 at {actual.RenderSize(0).Width}px, expected ~793px at 96 dpi.");
    var clamped = new EveryStage.Terminal.Documents.PdfViewportState(500, 7, CustomZoom: 99).Normalize(120);
    if (clamped.PageIndex != 119 || clamped.PageOffset >= 1 || clamped.CustomZoom > EveryStage.Terminal.Documents.PdfLayout.MaxZoom) return (false, "Out-of-range state was not clamped.");
    return (true, null);
}

(bool, string?) TestDocumentSettings()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-DocSettingsTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var store = new ScenarioStore();
        var scenario = new Scenario { Name = "doc" };
        var activity = new Activity { Name = "a" };
        var file = new MediaFile
        {
            SourcePath = @"C:\docs\deck.pptx", Kind = MediaKind.Document,
            Document = new DocumentPlaybackSettings
            {
                PresentationMode = PresentationMode.Static, StartPage = 3, EndPage = 9, Loop = true, SecondsPerPage = 7.5,
                ReadingMode = EveryStage.Terminal.Documents.PdfDisplayMode.SinglePage, StartOffset = 0.25, LiveSync = false,
                OpenMode = OfficeOpenMode.MicrosoftOffice, AllowEdit = false, InitialScrollPercent = 40,
            },
        };
        activity.Files.Add(file);
        scenario.Activities.Add(activity);
        store.Scenarios.Add(scenario);
        string path = Path.Combine(directory, "scenarios.json");
        new ScenarioRepository(path).Save(store);
        var loaded = new ScenarioRepository(path).Load().Scenarios.Single().Activities.Single().Files.Single().Document;
        if (loaded is not { PresentationMode: PresentationMode.Static, StartPage: 3, EndPage: 9, Loop: true, SecondsPerPage: 7.5, LiveSync: false,
                OpenMode: OfficeOpenMode.MicrosoftOffice, AllowEdit: false, InitialScrollPercent: 40 }
            || loaded.ReadingMode != EveryStage.Terminal.Documents.PdfDisplayMode.SinglePage || Math.Abs(loaded.StartOffset - 0.25) > 1e-9)
            return (false, "Document settings did not survive save/load.");
        var copy = file.Clone();
        copy.Document!.StartPage = 99;
        if (file.Document.StartPage != 3) return (false, "Clone shares the document settings object with the original.");
        var initial = file.Document.InitialPdfState();
        if (initial.PageIndex != 2 || initial.DisplayMode != EveryStage.Terminal.Documents.PdfDisplayMode.SinglePage) return (false, "Initial PDF state ignores the start page / reading mode.");
        return (true, null);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

(bool, string?) TestPcmDownmix()
{
    static byte[] Frame(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) { bytes[i * 2] = (byte)samples[i]; bytes[i * 2 + 1] = (byte)(samples[i] >> 8); }
        return bytes;
    }
    static (short L, short R) Stereo(byte[] pcm, int frame) =>
        ((short)(pcm[frame * 4] | (pcm[frame * 4 + 1] << 8)), (short)(pcm[frame * 4 + 2] | (pcm[frame * 4 + 3] << 8)));

    var stereo = Frame(1000, -1000);
    if (!ReferenceEquals(EveryStage.Rendering.Audio.PcmDownmix.ToStereo(stereo, 2), stereo)) return (false, "Stereo input was copied instead of passed through.");
    if (EveryStage.Rendering.Audio.PcmDownmix.OutputChannels(1) != 1 || EveryStage.Rendering.Audio.PcmDownmix.OutputChannels(6) != 2)
        return (false, "Output channel count wrong for mono / 5.1.");

    // 5.1 order FL FR FC LFE BL BR; normalisation 1 / (1 + 2 x 0.7071) = 0.4142.
    var fiveOne = Frame(10000, 0, 0, 30000, 0, 0,   // front-left only (+ LFE, which is dropped)
                        0, 0, 10000, 0, 0, 0,        // centre only
                        0, 0, 0, 0, 0, 10000,        // back-right only
                        32767, 32767, 32767, 32767, 32767, 32767); // everything at full scale
    var folded = EveryStage.Rendering.Audio.PcmDownmix.ToStereo(fiveOne, 6);
    if (folded.Length != 4 * 4) return (false, $"5.1 -> stereo produced {folded.Length} bytes for 4 frames.");
    var (l0, r0) = Stereo(folded, 0);
    var (l1, r1) = Stereo(folded, 1);
    var (l2, r2) = Stereo(folded, 2);
    var (l3, r3) = Stereo(folded, 3);
    if (Math.Abs(l0 - 4142) > 2 || r0 != 0) return (false, $"Front-left (with LFE) folded to {l0}/{r0}, expected ~4142/0 (LFE must be dropped).");
    if (Math.Abs(l1 - 2929) > 2 || l1 != r1) return (false, $"Centre folded to {l1}/{r1}, expected ~2929 on both sides.");
    if (l2 != 0 || Math.Abs(r2 - 2929) > 2) return (false, $"Back-right folded to {l2}/{r2}, expected 0/~2929.");
    if (l3 < 32000 || r3 < 32000) return (false, $"Full-scale 5.1 folded to {l3}/{r3}: normalisation too strong.");

    // 7.1 and quad keep their frame counts; a trailing partial frame is dropped.
    var sevenOne = new byte[8 * 2 * 3 + 5];
    if (EveryStage.Rendering.Audio.PcmDownmix.ToStereo(sevenOne, 8).Length != 3 * 4) return (false, "7.1 frame count wrong (partial frame must be dropped).");
    var quad = Frame(0, 0, 10000, 0);
    var (ql, qr) = Stereo(EveryStage.Rendering.Audio.PcmDownmix.ToStereo(quad, 4), 0);
    if (ql <= 0 || qr != 0) return (false, $"Quad back-left folded to {ql}/{qr}, expected left only.");
    return (true, null);
}

(bool, string?) TestPcmConverter()
{
    static byte[] Samples(params short[] s)
    {
        var b = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++) { b[i * 2] = (byte)s[i]; b[i * 2 + 1] = (byte)(s[i] >> 8); }
        return b;
    }
    static short At(byte[] b, int i) => (short)(b[i * 2] | (b[i * 2 + 1] << 8));

    var same = Samples(1, 2, 3, 4);
    if (!ReferenceEquals(EveryStage.Rendering.Audio.PcmConverter.Convert(same, 2, 48000, 2, 48000), same)) return (false, "Matching formats were copied.");

    var mono = EveryStage.Rendering.Audio.PcmConverter.Convert(Samples(100, -300), 1, 48000, 2, 48000);
    if (mono.Length != 8 || At(mono, 0) != 100 || At(mono, 1) != 100 || At(mono, 2) != -300 || At(mono, 3) != -300) return (false, "Mono -> stereo did not duplicate samples.");

    var down = EveryStage.Rendering.Audio.PcmConverter.Convert(Samples(1000, 3000), 2, 48000, 1, 48000);
    if (down.Length != 2 || At(down, 0) != 2000) return (false, "Stereo -> mono did not average.");

    // The AC-3 case: the clock was opened 2ch/44.1k, the decoder now delivers 6ch/44.1k (audit A-25).
    var sixToStereo = EveryStage.Rendering.Audio.PcmConverter.Convert(new byte[6 * 2 * 441], 6, 44100, 2, 44100);
    if (sixToStereo.Length != 2 * 2 * 441) return (false, $"6ch -> stereo kept {sixToStereo.Length} bytes for 441 frames.");

    // HE-AAC SBR case: rate doubles mid-stream; 10 ms at 48k must come out as 10 ms at 24k (and back).
    var up = EveryStage.Rendering.Audio.PcmConverter.Convert(new byte[480 * 4], 2, 48000, 2, 24000);
    var back = EveryStage.Rendering.Audio.PcmConverter.Convert(new byte[240 * 4], 2, 24000, 2, 48000);
    if (up.Length != 240 * 4 || back.Length != 480 * 4) return (false, $"Resampling changed the duration ({up.Length / 4} / {back.Length / 4} frames).");
    var ramp = EveryStage.Rendering.Audio.PcmConverter.Convert(Samples(0, 1000), 1, 24000, 1, 48000);
    if (ramp.Length != 8 || At(ramp, 1) != 500) return (false, "Linear interpolation midpoint wrong.");
    return (true, null);
}

(bool, string?) TestDailyRollingLogWriter()
{
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-LogWriterTests", Guid.NewGuid().ToString("N"));
    try
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
        Directory.CreateDirectory(directory);
        string stale = Path.Combine(directory, "playback-20260905.log"); // 31 days before 2026-10-06
        File.WriteAllText(stale, "{}\n");
        var writer = new EveryStage.Terminal.Logging.DailyRollingLogWriter(directory, "playback", TimeSpan.FromDays(30), () => now);
        writer.Write("first", new { value = 1 });
        string day1 = Path.Combine(directory, "playback-20261001.log");
        if (!File.Exists(day1) || !File.ReadAllText(day1).Contains("\"value\":1")) return (false, "The first line was not written to the day's file.");
        if (!File.Exists(stale)) return (false, "A file inside the retention window was deleted.");

        // A-4: retention must also run while the process keeps running, not only at construction.
        now = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Local);
        writer.Write("later");
        if (File.Exists(stale)) return (false, "A file past retention survived a day change (cleanup only ran at construction).");
        if (!File.Exists(Path.Combine(directory, "playback-20261006.log"))) return (false, "The new day did not roll to a new file.");

        // B-4: a fields object that cannot be serialized must not throw into the caller; A-5: it is counted.
        long failedBefore = EveryStage.Terminal.Logging.DailyRollingLogWriter.FailedWriteCount;
        writer.Write("broken", new ThrowingFields());
        if (EveryStage.Terminal.Logging.DailyRollingLogWriter.FailedWriteCount != failedBefore + 1
            || EveryStage.Terminal.Logging.DailyRollingLogWriter.LastFailure?.Contains("playback") != true)
            return (false, "A failed log write was not recorded in the writer health.");

        // The log folder vanishes while running: the next write recreates it.
        Directory.Delete(directory, recursive: true);
        writer.Write("after-delete");
        if (!File.Exists(Path.Combine(directory, "playback-20261006.log"))) return (false, "Writing after the log folder was removed did not recreate it.");

        // C-3: export while a writer holds a log open, and over an existing export.
        string zip = Path.Combine(directory, "export", "logs.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.WriteAllText(zip, "previous export");
        using (new FileStream(Path.Combine(directory, "playback-20261006.log"), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            if (LogExporter.Export(zip, directory) != 1) return (false, "Export failed while a log was being written.");
        }
        using (var archive = ZipFile.OpenRead(zip))
            if (archive.Entries.Count != 1) return (false, "Export over an existing file did not produce a fresh archive.");
        if (Directory.GetFiles(Path.GetDirectoryName(zip)!).Length != 1) return (false, "Export left its temporary file behind.");
        return (true, null);
    }
    finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
}

(bool, string?) TestPcmTimeline()
{
    // 48 kHz, 10 ms (480-sample) capture buffers, 1024-sample AAC frames.
    long Ms(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);
    var timeline = new EveryStage.Caster.Casting.PcmTimeline(48000);
    timeline.Record(480, Ms(0));
    timeline.Record(480, Ms(10));
    timeline.Record(480, Ms(20));
    if (timeline.NextFrame(1024) != Ms(0)) return (false, "First frame not stamped with its first sample's capture time.");
    // Frame 2 starts at sample 1024, 64 samples into the third buffer — not at the time of the buffer
    // whose submission happened to produce it (audit A-3).
    long? second = timeline.NextFrame(1024);
    if (second == null || Math.Abs(second.Value - Ms(20 + 64 / 48.0)) > 2) return (false, $"Second frame stamped {second / 10_000.0} ms, expected ~21.33 ms.");
    // Silence: loopback delivers nothing for ~5 s; the next buffers keep their real capture times.
    timeline.Record(480, Ms(5000));
    timeline.Record(480, Ms(5010));
    timeline.Record(480, Ms(5020));
    long? third = timeline.NextFrame(1024); // sample 2048: 128 samples into the buffer captured at 5010 ms
    if (third == null || Math.Abs(third.Value - Ms(5010 + 128 / 48.0)) > 2) return (false, $"Frame after a silent gap stamped {third / 10_000.0} ms, expected ~5012.67 ms.");
    return (true, null);
}

sealed class ThrowingFields
{
    public string Value => throw new InvalidOperationException("getter failed");
}
