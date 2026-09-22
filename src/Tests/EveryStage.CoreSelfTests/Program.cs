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
await RunAsync("Unreadable stores preserve original data", () => Task.FromResult(TestUnreadableStores()));
await RunAsync("RTP idle gap timeout", TestIdleGapTimeout);
await RunAsync("H.264 payload format robustness", () => Task.FromResult(TestPayloadFormatRobustness()));
await RunAsync("Access unit assembly", () => Task.FromResult(TestAccessUnitAssembly()));
await RunAsync("Media authentication and replay protection", () => Task.FromResult(TestMediaAuthentication()));
await RunAsync("Authenticated RTP delivery", TestAuthenticatedDelivery);
await RunAsync("Authenticated cast-start acknowledgement", () =>
{
    string key = PairingSecurity.GenerateKey();
    Guid session = Guid.NewGuid();
    Guid terminal = Guid.NewGuid();
    foreach (bool ready in new[] { true, false })
    {
        var ack = new DiscoveryProtocol.CastStartAckMessage
        { MediaSessionId = session, Ready = ready, Error = ready ? null : "display unavailable" };
        PairingSecurity.Sign(ack, terminal, key);
        var decoded = DiscoveryProtocol.Decode(DiscoveryProtocol.Encode(ack)) as DiscoveryProtocol.CastStartAckMessage;
        if (decoded == null || decoded.MediaSessionId != session || decoded.SenderDeviceId != terminal
            || decoded.Ready != ready || decoded.Error != ack.Error || !PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Start acknowledgement lost fields or authentication."));
        decoded.Ready = !ready;
        if (PairingSecurity.Verify(decoded, key))
            return Task.FromResult<(bool, string?)>((false, "Tampered readiness was accepted."));
        decoded.Ready = ready;
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
    machine.Disconnect();
    if (machine.State != OutputState.Idle || machine.CastSwitchOn) return (false, "Disconnect changed an independent state axis.");
    if (!observed.SequenceEqual(new[] { OutputState.Active, OutputState.Idle })) return (false, "State events were missing after a subscriber threw.");
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
