using System.Net;
using System.Net.Sockets;

namespace EveryStage.Discovery;

/// <summary>
/// Sends one instance of every <see cref="DiscoveryProtocol.Message"/> subtype over real loopback
/// UDP (<see cref="DiscoveryProtocol.Encode"/> -> <c>UdpClient</c> -> <see cref="DiscoveryProtocol.Decode"/>)
/// and checks every field survives the round trip byte-for-byte-equivalent — the same "an actual,
/// runnable end-to-end check, not just hand-traced reasoning" role
/// <c>EveryStage.Transport.TransportSelfTest</c> plays for the RTP wire format, applied here to this
/// library's own JSON wire format for the first time. Until now, this project's README "已知风险"
/// item #1 ("全部内容都没有在真实网络环境验证过") was accurate about more than just the two real
/// services (<c>DiscoveryService</c>/<c>TerminalDiscoveryClient</c>) actually agreeing on the
/// protocol at runtime — even the more basic question of whether <see cref="DiscoveryProtocol.Encode"/>'s
/// "flatten to one JSON object, splice in a lowercase <c>type</c> field via <c>JsonNode</c>" trick
/// (see that method's own doc comment) actually round-trips every field of every one of the 8
/// message shapes correctly had never been executed even once — only reasoned about.
///
/// Deliberately does NOT spin up <see cref="DiscoveryService"/>/<c>Caster.TerminalDiscoveryClient</c>
/// themselves (that would additionally need on-disk <see cref="DeviceIdentity"/>/trust-list state and
/// a much larger test), and does not test the two real services' handshake logic (beacon ->
/// pair_request -> pair_response, etc.) — only that the wire format itself is lossless for every
/// message type this protocol defines, one at a time, independent of any handshake sequencing.
///
/// Also runs <see cref="CheckMalformedInputsDontThrow"/> (see its own doc comment) — added after
/// this session found a real bug this round-trip-only coverage would never have caught: a stray,
/// valid-but-non-object-shaped JSON datagram made <see cref="DiscoveryProtocol.Decode"/> throw
/// <see cref="InvalidOperationException"/> instead of returning null, which both real receive loops'
/// narrower <c>catch (JsonException)</c> let straight through.
/// </summary>
public static class DiscoveryProtocolSelfTest
{
    public sealed record Result(bool Success, int MessagesVerified, string? FailureReason);

    public static async Task<Result> RunAsync(TimeSpan? timeout = null)
    {
        var messages = BuildTestMessages();

        using var receiverSocket = new UdpClient(0);
        int port = ((IPEndPoint)receiverSocket.Client.LocalEndPoint!).Port;
        using var senderSocket = new UdpClient(0);
        var receiverEndPoint = new IPEndPoint(IPAddress.Loopback, port);

        int verified = 0;
        foreach (var original in messages)
        {
            byte[] bytes = DiscoveryProtocol.Encode(original);

            var receiveTask = receiverSocket.ReceiveAsync();
            await senderSocket.SendAsync(bytes, bytes.Length, receiverEndPoint);

            var finished = await Task.WhenAny(receiveTask, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
            if (finished != receiveTask)
                return new Result(false, verified, $"Timed out waiting for a {original.Type} message to arrive over loopback.");

            var received = DiscoveryProtocol.Decode(receiveTask.Result.Buffer);
            if (received == null)
                return new Result(false, verified, $"A {original.Type} message round-tripped through Encode/UDP but Decode returned null for it.");

            string? mismatch = CompareFields(original, received);
            if (mismatch != null)
                return new Result(false, verified, $"{original.Type} message round-trip mismatch: {mismatch}");

            verified++;
        }

        string? malformedInputFailure = CheckMalformedInputsDontThrow();
        if (malformedInputFailure != null)
            return new Result(false, verified, malformedInputFailure);

        string? compatibilityFailure = CheckVersionAndSequenceCompatibility();
        if (compatibilityFailure != null)
            return new Result(false, verified, compatibilityFailure);

        string? retryFailure = await CheckAcknowledgedRetryAsync();
        if (retryFailure != null)
            return new Result(false, verified, retryFailure);

        string? authenticationFailure = CheckAuthenticationAndReplayProtection();
        if (authenticationFailure != null)
            return new Result(false, verified, authenticationFailure);

        return new Result(true, verified, null);
    }

    /// <summary>Verifies <see cref="DiscoveryProtocol.Decode"/> never throws for a stray datagram
    /// that happens to be valid JSON but doesn't match this protocol's own message shape — the exact
    /// bug this session found and fixed (see <see cref="DiscoveryProtocol.Decode"/>'s own doc
    /// comment): <c>JsonElement.TryGetProperty</c>/<c>GetString</c> throw
    /// <see cref="InvalidOperationException"/>, not <see cref="System.Text.Json.JsonException"/>, for
    /// a root element that isn't a JSON object or a "type" field that isn't a string, and neither of
    /// this protocol's two real receive loops (<c>Terminal.Devices.DiscoveryService</c>,
    /// <c>Caster.Discovery.TerminalDiscoveryClient</c>) ever caught anything but
    /// <see cref="System.Text.Json.JsonException"/> around this call — an uncaught exception here
    /// would previously have killed whichever side's background receive loop hit it, permanently and
    /// silently (no <c>AppDomain.UnhandledException</c>/<c>TaskScheduler.UnobservedTaskException</c>
    /// handler exists in either app). Doesn't need any network I/O — <see cref="DiscoveryProtocol.Decode"/>
    /// is a pure function of its input bytes, so this just calls it directly.</summary>
    private static string? CheckMalformedInputsDontThrow()
    {
        (string Label, string Json)[] inputs =
        {
            ("a bare JSON number", "42"),
            ("a bare JSON string", "\"hello\""),
            ("a bare JSON array", "[1,2,3]"),
            ("a bare JSON boolean", "true"),
            ("the JSON literal null", "null"),
            ("an object whose \"type\" field is a number", "{\"type\":123}"),
            ("an object whose \"type\" field is an object", "{\"type\":{}}"),
            ("an object with no \"type\" field at all", "{\"foo\":\"bar\"}"),
        };

        foreach (var (label, json) in inputs)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            DiscoveryProtocol.Message? result;
            try
            {
                result = DiscoveryProtocol.Decode(bytes);
            }
            catch (Exception ex)
            {
                return $"Decode threw for {label} ('{json}'): {ex.GetType().Name}: {ex.Message}";
            }

            if (result != null)
                return $"Decode should have returned null for {label} ('{json}'), but returned a {result.GetType().Name}.";
        }

        return null;
    }

    /// <summary>Field-by-field comparison, one <c>switch</c> arm per message type — reflection-based
    /// comparison would be shorter but would silently stop catching a newly-added field the day
    /// someone adds one without also adding a comparison for it; an explicit arm per type means the
    /// compiler forces a decision (even if that decision is "ignore this new field") once a type gets
    /// touched, since every field referenced here is a real property that has to exist to compile.</summary>
    private static string? CompareFields(DiscoveryProtocol.Message original, DiscoveryProtocol.Message received)
    {
        if (original.GetType() != received.GetType())
            return $"decoded as {received.GetType().Name}, expected {original.GetType().Name}.";
        if (original.ProtocolVersion != received.ProtocolVersion)
            return $"ProtocolVersion {original.ProtocolVersion} != {received.ProtocolVersion}.";

        switch (original)
        {
            case DiscoveryProtocol.BeaconMessage o when received is DiscoveryProtocol.BeaconMessage r:
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                if (o.DeviceName != r.DeviceName) return $"DeviceName '{o.DeviceName}' != '{r.DeviceName}'";
                return null;

            case DiscoveryProtocol.PairRequestMessage o when received is DiscoveryProtocol.PairRequestMessage r:
                if (o.RequestId != r.RequestId) return $"RequestId '{o.RequestId}' != '{r.RequestId}'";
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                if (o.DeviceName != r.DeviceName) return $"DeviceName '{o.DeviceName}' != '{r.DeviceName}'";
                return null;

            case DiscoveryProtocol.PairResponseMessage o when received is DiscoveryProtocol.PairResponseMessage r:
                if (o.RequestId != r.RequestId) return $"RequestId '{o.RequestId}' != '{r.RequestId}'";
                if (o.Accepted != r.Accepted) return $"Accepted {o.Accepted} != {r.Accepted}";
                if (o.Reason != r.Reason) return $"Reason '{o.Reason}' != '{r.Reason}'";
                if (o.PairingKey != r.PairingKey) return "PairingKey did not round-trip.";
                if (o.PairingKeyFormatVersion != r.PairingKeyFormatVersion) return "PairingKeyFormatVersion did not round-trip.";
                return null;

            case DiscoveryProtocol.CastStartMessage o when received is DiscoveryProtocol.CastStartMessage r:
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                if (o.Width != r.Width) return $"Width {o.Width} != {r.Width}";
                if (o.Height != r.Height) return $"Height {o.Height} != {r.Height}";
                if (o.PayloadType != r.PayloadType) return $"PayloadType {o.PayloadType} != {r.PayloadType}";
                if (o.HasAudio != r.HasAudio) return $"HasAudio {o.HasAudio} != {r.HasAudio}";
                if (o.AudioSampleRate != r.AudioSampleRate) return $"AudioSampleRate {o.AudioSampleRate} != {r.AudioSampleRate}";
                if (o.AudioChannels != r.AudioChannels) return $"AudioChannels {o.AudioChannels} != {r.AudioChannels}";
                if (o.AudioPayloadType != r.AudioPayloadType) return $"AudioPayloadType {o.AudioPayloadType} != {r.AudioPayloadType}";
                if (o.AudioIsAac != r.AudioIsAac) return $"AudioIsAac {o.AudioIsAac} != {r.AudioIsAac}";
                return null;

            case DiscoveryProtocol.CastStopMessage o when received is DiscoveryProtocol.CastStopMessage r:
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                return null;

            case DiscoveryProtocol.CastStatusMessage o when received is DiscoveryProtocol.CastStatusMessage r:
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                if (o.SessionId != r.SessionId) return $"SessionId {o.SessionId} != {r.SessionId}";
                if (o.SequenceNumber != r.SequenceNumber) return $"SequenceNumber {o.SequenceNumber} != {r.SequenceNumber}";
                if (o.SentAtUtc != r.SentAtUtc) return $"SentAtUtc {o.SentAtUtc:O} != {r.SentAtUtc:O}";
                if (o.FramesDecoded != r.FramesDecoded) return $"FramesDecoded {o.FramesDecoded} != {r.FramesDecoded}";
                if (o.VideoBytesReceived != r.VideoBytesReceived) return $"VideoBytesReceived {o.VideoBytesReceived} != {r.VideoBytesReceived}";
                if (o.VideoError != r.VideoError) return $"VideoError '{o.VideoError}' != '{r.VideoError}'";
                if (o.HasAudio != r.HasAudio) return $"HasAudio {o.HasAudio} != {r.HasAudio}";
                if (o.AudioBytesReceived != r.AudioBytesReceived) return $"AudioBytesReceived {o.AudioBytesReceived} != {r.AudioBytesReceived}";
                if (o.AudioError != r.AudioError) return $"AudioError '{o.AudioError}' != '{r.AudioError}'";
                if (o.PayloadTypeMismatches != r.PayloadTypeMismatches) return $"PayloadTypeMismatches {o.PayloadTypeMismatches} != {r.PayloadTypeMismatches}";
                return null;

            case DiscoveryProtocol.CastStatusAckMessage o when received is DiscoveryProtocol.CastStatusAckMessage r:
                if (o.DeviceId != r.DeviceId) return $"DeviceId {o.DeviceId} != {r.DeviceId}";
                if (o.SessionId != r.SessionId) return $"SessionId {o.SessionId} != {r.SessionId}";
                if (o.SequenceNumber != r.SequenceNumber) return $"SequenceNumber {o.SequenceNumber} != {r.SequenceNumber}";
                return null;

            case DiscoveryProtocol.PingMessage o when received is DiscoveryProtocol.PingMessage r:
                if (o.RequestId != r.RequestId) return $"RequestId '{o.RequestId}' != '{r.RequestId}'";
                return null;

            case DiscoveryProtocol.PongMessage o when received is DiscoveryProtocol.PongMessage r:
                if (o.RequestId != r.RequestId) return $"RequestId '{o.RequestId}' != '{r.RequestId}'";
                return null;

            default:
                // Unreachable given the type-equality check above and DiscoveryProtocol.Message's own
                // closed set of subtypes (all sealed, all listed in Decode's switch) — kept as an
                // explicit failure rather than a silent "return null" so a future 9th message type
                // added here without a matching case above fails loudly instead of appearing to pass.
                return $"no comparison case implemented for message type {original.GetType().Name}.";
        }
    }

    /// <summary>One instance per message type, with deliberately distinct/non-default field values
    /// (including at least one nullable field actually set to <c>null</c> and another actually set to
    /// a real string) so a bug that only manifests for a specific value — a default `0`/`false`/`null`
    /// that would "accidentally" round-trip correctly even through a broken path — has a chance to
    /// surface.</summary>
    private static List<DiscoveryProtocol.Message> BuildTestMessages()
    {
        var deviceId = Guid.NewGuid();
        return new List<DiscoveryProtocol.Message>
        {
            new DiscoveryProtocol.BeaconMessage { DeviceId = deviceId, DeviceName = "自检-Terminal" },
            new DiscoveryProtocol.PairRequestMessage { RequestId = "req-1", DeviceId = deviceId, DeviceName = "自检-Caster" },
            new DiscoveryProtocol.PairResponseMessage { RequestId = "req-1", Accepted = false, Reason = "用户拒绝", PairingKey = null },
            new DiscoveryProtocol.PairResponseMessage { RequestId = "req-2", Accepted = true, Reason = null, PairingKey = PairingSecurity.GenerateKey(), PairingKeyFormatVersion = PairingSecurity.CurrentKeyFormatVersion },
            new DiscoveryProtocol.CastStartMessage
            {
                DeviceId = deviceId, Width = 1920, Height = 1080, PayloadType = 96,
                HasAudio = true, AudioSampleRate = 48000, AudioChannels = 2, AudioPayloadType = 97,
                AudioIsAac = true,
            },
            new DiscoveryProtocol.CastStopMessage { DeviceId = deviceId },
            new DiscoveryProtocol.CastStatusMessage
            {
                DeviceId = deviceId, SessionId = Guid.NewGuid(), SequenceNumber = 42, SentAtUtc = DateTimeOffset.UtcNow, FramesDecoded = 12345,
                VideoBytesReceived = 987654321, VideoError = "解码失败：测试用错误信息",
                HasAudio = true, AudioBytesReceived = 123456, AudioError = null,
                PayloadTypeMismatches = 3,
            },
            new DiscoveryProtocol.CastStatusAckMessage { DeviceId = deviceId, SessionId = Guid.NewGuid(), SequenceNumber = 42 },
            new DiscoveryProtocol.PingMessage { RequestId = "ping-1" },
            new DiscoveryProtocol.PongMessage { RequestId = "ping-1" },
        };
    }

    private static string? CheckVersionAndSequenceCompatibility()
    {
        byte[] missingVersion = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"beacon\",\"deviceId\":\"00000000-0000-0000-0000-000000000001\",\"deviceName\":\"old\"}");
        if (DiscoveryProtocol.Decode(missingVersion) != null)
            return "A message without protocolVersion should be rejected.";

        byte[] futureVersion = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"beacon\",\"protocolVersion\":999,\"deviceId\":\"00000000-0000-0000-0000-000000000001\",\"deviceName\":\"future\"}");
        if (DiscoveryProtocol.Decode(futureVersion) != null)
            return "A message with an unsupported protocolVersion should be rejected.";

        var tracker = new CastStatusSequenceTracker();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid firstSession = Guid.NewGuid();
        if (!tracker.TryAccept(first, firstSession, 10)) return "Sequence tracker rejected the first status.";
        if (tracker.TryAccept(first, firstSession, 10)) return "Sequence tracker accepted a duplicate status.";
        if (tracker.TryAccept(first, firstSession, 9)) return "Sequence tracker accepted an out-of-order status.";
        if (!tracker.TryAccept(first, firstSession, 11)) return "Sequence tracker rejected a newer status.";
        if (!tracker.TryAccept(second, firstSession, 1)) return "Sequence tracker mixed independent devices.";
        if (!tracker.TryAccept(first, Guid.NewGuid(), 1)) return "Sequence tracker rejected a restarted Terminal session.";
        string key1 = PairingSecurity.GenerateKey();
        string key2 = PairingSecurity.GenerateKey();
        if (!PairingSecurity.IsValidKey(key1) || !PairingSecurity.IsValidKey(key2))
            return "Generated pairing key is not a valid 256-bit key.";
        if (key1 == key2) return "Two generated pairing keys unexpectedly matched.";
        if (PairingSecurity.IsValidKey(null) || PairingSecurity.IsValidKey("not-base64"))
            return "Pairing key validation accepted invalid input.";
        if (!PairingSecurity.IsSupportedKey(key1, PairingSecurity.CurrentKeyFormatVersion))
            return "Current key format was rejected.";
        if (PairingSecurity.IsSupportedKey(key1, 0) || PairingSecurity.IsSupportedKey(key1, 999))
            return "Legacy or future key format was accepted without an explicit migration.";
        return null;
    }

    private static async Task<string?> CheckAcknowledgedRetryAsync()
    {
        int timedOutAttempts = 0;
        bool timedOut = await AcknowledgedMessageRetry.SendUntilAcknowledgedAsync(
            () => { timedOutAttempts++; return Task.CompletedTask; },
            new TaskCompletionSource<bool>().Task,
            maxAttempts: 3, attemptTimeout: TimeSpan.FromMilliseconds(1));
        if (timedOut || timedOutAttempts != 3)
            return $"ACK timeout should make exactly 3 attempts; result={timedOut}, attempts={timedOutAttempts}.";

        int successfulAttempts = 0;
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool succeeded = await AcknowledgedMessageRetry.SendUntilAcknowledgedAsync(
            () => { if (++successfulAttempts == 2) ack.TrySetResult(true); return Task.CompletedTask; },
            ack.Task, maxAttempts: 3, attemptTimeout: TimeSpan.FromMilliseconds(1));
        if (!succeeded || successfulAttempts != 2)
            return $"ACK retry should stop on the second attempt; result={succeeded}, attempts={successfulAttempts}.";
        foreach (bool cancelled in new[] { false, true })
        foreach (bool completeDuringSend in new[] { false, true })
        {
            var failedAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void FailAck()
            {
                if (cancelled) failedAck.TrySetCanceled();
                else failedAck.TrySetException(new InvalidDataException("Rejected acknowledgement"));
            }
            if (!completeDuringSend) FailAck();
            bool propagated = false;
            try
            {
                await AcknowledgedMessageRetry.SendUntilAcknowledgedAsync(
                    () => { if (completeDuringSend) FailAck(); return Task.CompletedTask; }, failedAck.Task,
                    attemptTimeout: TimeSpan.FromMilliseconds(1));
            }
            catch (OperationCanceledException) when (cancelled) { propagated = true; }
            catch (InvalidDataException) when (!cancelled) { propagated = true; }
            if (!propagated) return "Faulted/cancelled ACK was incorrectly accepted as success.";
        }
        return null;
    }

    private static string? CheckAuthenticationAndReplayProtection()
    {
        string key = PairingSecurity.GenerateKey();
        var message = new DiscoveryProtocol.CastStopMessage { DeviceId = Guid.NewGuid() };
        Guid sender = Guid.NewGuid();
        PairingSecurity.Sign(message, sender, key);
        if (!PairingSecurity.Verify(message, key)) return "A freshly signed message failed authentication.";

        message.DeviceId = Guid.NewGuid();
        if (PairingSecurity.Verify(message, key)) return "Authentication accepted a tampered message.";
        message.DeviceId = sender;
        PairingSecurity.Sign(message, sender, key);
        if (PairingSecurity.Verify(message, PairingSecurity.GenerateKey())) return "Authentication accepted the wrong key.";

        var guard = new ReplayGuard();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!guard.TryAccept(message, now)) return "Replay guard rejected a fresh message.";
        if (guard.TryAccept(message, now)) return "Replay guard accepted the same MessageId twice.";

        PairingSecurity.Sign(message, sender, key);
        message.IssuedAtUtc = now - ReplayGuard.DefaultAllowedClockSkew - TimeSpan.FromSeconds(1);
        if (guard.TryAccept(message, now)) return "Replay guard accepted an expired message.";
        return null;
    }
}
