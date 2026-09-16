using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// <see cref="TransportSelfTest"/>'s counterpart for the raw-payload RTP path
/// (<see cref="RtpSession.SendRawPayloadAsync"/> -> <see cref="RawRtpReceiver"/>) used by audio
/// (raw PCM chunks and, since AAC was wired into the live cast session, ADTS access units) instead
/// of <see cref="RtpSession.SendNalUnitAsync"/>/<see cref="RtpReceiver"/>'s NAL-specific framing.
/// Deliberately a separate class rather than a shared helper with <see cref="TransportSelfTest"/> —
/// same "two small, independent implementations rather than one shared abstraction" reasoning
/// <see cref="RawRtpReceiver"/>'s own doc comment already gives for why it isn't merged with
/// <see cref="RtpReceiver"/>: the two self-tests exercise genuinely different sender/receiver pairs,
/// and a shared runner would need a payload-kind switch inside it for no real benefit.
///
/// This closes this library's README "已知风险" item on <see cref="RawRtpReceiver"/>/
/// <see cref="RtpSession.SendRawPayloadAsync"/> having no automated/end-to-end verification of their
/// own — until now that path was only ever reasoned about by analogy to the video path's
/// already-verified <see cref="RtpPacket"/> encode/decode, never actually run.
/// </summary>
public static class RawTransportSelfTest
{
    public sealed record Result(bool Success, int PayloadsSent, int PayloadsReceived, long GapEvents, string? FailureReason);

    public static async Task<Result> RunAsync(TimeSpan? timeout = null)
    {
        var testPayloads = BuildTestPayloads();

        int port = GetLikelyFreeUdpPort();
        using var receiver = new RawRtpReceiver(port);

        var received = new List<byte[]>();
        var gate = new object();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.PayloadReceived += (payload, _) =>
        {
            lock (gate)
            {
                received.Add(payload);
                if (received.Count >= testPayloads.Count) allReceived.TrySetResult();
            }
        };
        receiver.Start();

        // payloadType is a placeholder here, same as TransportSelfTest's own choice — this self-test
        // never checks it (RawRtpReceiver doesn't either, see this library's README).
        using var sender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: 97);
        uint timestamp = 0;
        foreach (var payload in testPayloads)
        {
            await sender.SendRawPayloadAsync(payload, timestamp);
            // Stand-in for AacSamplesPerFrame (LiveCastSession.cs) — the actual value doesn't matter
            // to this self-test, only that consecutive payloads carry distinct, increasing
            // timestamps like the real audio path does.
            timestamp += 1024;
        }

        var finished = await Task.WhenAny(allReceived.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        int receivedCount;
        lock (gate) receivedCount = received.Count;

        if (finished != allReceived.Task)
            return new Result(false, testPayloads.Count, receivedCount, receiver.GapEvents, "Timed out waiting for all payloads to arrive.");

        lock (gate)
        {
            for (int i = 0; i < testPayloads.Count; i++)
            {
                if (!received[i].AsSpan().SequenceEqual(testPayloads[i].Span))
                    return new Result(false, testPayloads.Count, received.Count, receiver.GapEvents, $"Payload #{i} arrived but its bytes don't match what was sent.");
            }
        }

        // A genuine loopback run with nothing else on the socket should never see a sequence-number
        // gap — if it does, either RawRtpReceiver's tracking or RtpSession's sequence numbering has
        // a real bug, not just an unlucky network, so this counts as a self-test failure rather than
        // a warning.
        if (receiver.GapEvents != 0)
            return new Result(false, testPayloads.Count, receivedCount, receiver.GapEvents, $"All payloads matched, but GapEvents was {receiver.GapEvents} instead of 0 on a lossless loopback run.");

        string? mismatchFailure = await RunPayloadTypeMismatchCheckAsync();
        if (mismatchFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, receiver.GapEvents, mismatchFailure);

        string? resilienceFailure = await RunDispatchExceptionResilienceCheckAsync();
        if (resilienceFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, receiver.GapEvents, resilienceFailure);

        return new Result(true, testPayloads.Count, receivedCount, receiver.GapEvents, null);
    }

    /// <summary>Same check as <see cref="TransportSelfTest"/>'s own copy of this method, adapted to
    /// <see cref="RawRtpReceiver"/>/<see cref="RtpSession.SendRawPayloadAsync"/> instead of the
    /// NAL-framed path — see that copy's doc comment for what this verifies and why it runs on its
    /// own fresh port/receiver. Deliberately duplicated rather than shared, same "two small,
    /// independent implementations" reasoning as the rest of this class (see its own doc comment).</summary>
    private static async Task<string?> RunPayloadTypeMismatchCheckAsync()
    {
        const byte expectedType = 97;
        const byte wrongType = 98;

        int port = GetLikelyFreeUdpPort();
        using var receiver = new RawRtpReceiver(port, expectedPayloadType: expectedType);

        var received = new List<byte[]>();
        var gate = new object();
        var matchingArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PayloadReceived += (payload, _) =>
        {
            lock (gate)
            {
                received.Add(payload);
                matchingArrived.TrySetResult();
            }
        };
        receiver.Start();

        var matchingPayload = MakeFakePayload(new Random(997), 80);
        var mismatchedPayload = MakeFakePayload(new Random(996), 80);

        // Mismatched packet sent first — see TransportSelfTest's own copy of this method for why.
        using (var wrongSender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: wrongType))
            await wrongSender.SendRawPayloadAsync(mismatchedPayload, 0);

        using (var rightSender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: expectedType))
            await rightSender.SendRawPayloadAsync(matchingPayload, 1024);

        var finished = await Task.WhenAny(matchingArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != matchingArrived.Task)
            return "PayloadType mismatch check: timed out waiting for the correctly-typed payload to arrive.";

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        lock (gate)
        {
            if (received.Count != 1)
                return $"PayloadType mismatch check: expected exactly 1 delivered payload (the correctly-typed one), got {received.Count} — the mismatched packet was not dropped as expected.";
            if (!received[0].AsSpan().SequenceEqual(matchingPayload.Span))
                return "PayloadType mismatch check: the one delivered payload's bytes don't match the correctly-typed payload that was sent.";
        }

        if (receiver.PayloadTypeMismatches != 1)
            return $"PayloadType mismatch check: expected PayloadTypeMismatches == 1, got {receiver.PayloadTypeMismatches}.";
        if (receiver.PacketsReceived != 1)
            return $"PayloadType mismatch check: expected PacketsReceived == 1 (the mismatched packet shouldn't count), got {receiver.PacketsReceived}.";
        if (receiver.GapEvents != 0)
            return $"PayloadType mismatch check: expected GapEvents == 0 (a dropped mismatch isn't a sequence gap), got {receiver.GapEvents}.";

        return null;
    }

    /// <summary>Same check as <see cref="TransportSelfTest"/>'s own copy of this method, adapted to
    /// <see cref="RawRtpReceiver"/>/<see cref="RtpSession.SendRawPayloadAsync"/> instead of the
    /// NAL-framed path — see that copy's doc comment for what <see cref="RawRtpReceiver.DispatchExceptions"/>
    /// defends against and why this needed its own runnable check rather than just documenting the
    /// counter's existence. Deliberately duplicated rather than shared, same "two small, independent
    /// implementations" reasoning as the rest of this class (see its own doc comment).</summary>
    private static async Task<string?> RunDispatchExceptionResilienceCheckAsync()
    {
        int port = GetLikelyFreeUdpPort();
        using var receiver = new RawRtpReceiver(port);

        var received = new List<byte[]>();
        var gate = new object();
        bool firstCallSeen = false;
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.PayloadReceived += (payload, _) =>
        {
            lock (gate)
            {
                if (!firstCallSeen)
                {
                    firstCallSeen = true;
                    // Deliberate — this is what RawRtpReceiver.DispatchExceptions/its surrounding
                    // try/catch exist to survive. A lock statement releases its Monitor via a
                    // compiler-generated finally even when the guarded code throws, so this doesn't
                    // risk deadlocking the receive loop on its next iteration.
                    throw new InvalidOperationException("Deliberate self-test exception — verifying the receive loop survives a subscriber throwing.");
                }
                received.Add(payload);
                secondArrived.TrySetResult();
            }
        };
        receiver.Start();

        using var sender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: 97);
        var firstPayload = MakeFakePayload(new Random(552), 80);
        var secondPayload = MakeFakePayload(new Random(551), 80);

        await sender.SendRawPayloadAsync(firstPayload, 0);
        // Give the receive loop a moment to reach (and throw on) the first payload before sending
        // the second — this is what proves the two are handled as genuinely separate loop
        // iterations, not that the second just happened to win a race before the first was ever
        // dispatched.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await sender.SendRawPayloadAsync(secondPayload, 1024);

        var finished = await Task.WhenAny(secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != secondArrived.Task)
            return "Dispatch-exception resilience check: timed out waiting for the SECOND payload to arrive after the first subscriber call deliberately threw — the receive loop did not survive.";

        lock (gate)
        {
            if (received.Count != 1 || !received[0].AsSpan().SequenceEqual(secondPayload.Span))
                return "Dispatch-exception resilience check: the second payload's bytes don't match what was sent, or wasn't the only one recorded.";
        }

        if (receiver.DispatchExceptions != 1)
            return $"Dispatch-exception resilience check: expected DispatchExceptions == 1, got {receiver.DispatchExceptions}.";

        return null;
    }

    private static List<ReadOnlyMemory<byte>> BuildTestPayloads()
    {
        var rng = new Random(23456); // fixed seed, distinct from TransportSelfTest's own: a self-test should be deterministic between runs, and the two shouldn't coincidentally send identical bytes.
        return new List<ReadOnlyMemory<byte>>
        {
            MakeFakePayload(rng, 3200), // typical 20ms of 16-bit stereo PCM at 48kHz-ish sizes — an in-budget single packet, no fragmentation exists on this path at all.
            MakeFakePayload(rng, 200),  // a small one, like a compressed AAC access unit.
            MakeFakePayload(rng, 1400), // right at RtpSession's default maxPayloadSize — this path's caller (LiveCastSession) is responsible for never exceeding this itself, since unlike SendNalUnitAsync there is no fragmentation to fall back on.
        };
    }

    private static ReadOnlyMemory<byte> MakeFakePayload(Random rng, int size)
    {
        var bytes = new byte[size];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Same small, accepted bind-then-release race as <see cref="TransportSelfTest"/>'s own
    /// copy of this method — see its doc comment.</summary>
    private static int GetLikelyFreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
