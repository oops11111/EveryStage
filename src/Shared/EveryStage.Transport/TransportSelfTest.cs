using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Sends synthetic NAL-like payloads over real loopback UDP (<see cref="RtpSession"/> ->
/// <see cref="RtpReceiver"/>) and checks every one arrives byte-for-byte and in order — including
/// one payload deliberately larger than the packetizer's MTU budget, to exercise FU-A
/// fragmentation/reassembly. Unlike the hand-traced bit-level reasoning that backed this library's
/// initial commit, this is an actual, runnable end-to-end check — the first one in this repo that
/// doesn't need Windows, a GPU, or any not-yet-built encoder, only a real .NET runtime and a
/// working loopback network stack. This is exactly the kind of check that should be turned into a
/// real automated test the first time this repository has a working `dotnet test` setup.
/// </summary>
public static class TransportSelfTest
{
    public sealed record Result(bool Success, int NalUnitsSent, int NalUnitsReceived, string? FailureReason);

    public static async Task<Result> RunAsync(TimeSpan? timeout = null)
    {
        var testPayloads = BuildTestPayloads();

        int port = GetLikelyFreeUdpPort();
        using var receiver = new RtpReceiver(port);

        var received = new List<byte[]>();
        var gate = new object();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.NalUnitReceived += (nal, _, _) =>
        {
            lock (gate)
            {
                received.Add(nal);
                if (received.Count >= testPayloads.Count) allReceived.TrySetResult();
            }
        };
        receiver.Start();

        using var sender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: 96);
        uint timestamp = 0;
        foreach (var payload in testPayloads)
        {
            await sender.SendNalUnitAsync(payload, timestamp, isLastNalOfAccessUnit: true);
            timestamp += RtpVideoClock.ClockRate / 30; // pretend 30fps, one NAL per "frame" for this test.
        }

        var finished = await Task.WhenAny(allReceived.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        int receivedCount;
        lock (gate) receivedCount = received.Count;

        if (finished != allReceived.Task)
            return new Result(false, testPayloads.Count, receivedCount, "Timed out waiting for all NAL units to arrive.");

        lock (gate)
        {
            for (int i = 0; i < testPayloads.Count; i++)
            {
                if (!received[i].AsSpan().SequenceEqual(testPayloads[i].Span))
                    return new Result(false, testPayloads.Count, received.Count, $"NAL unit #{i} arrived but its bytes don't match what was sent.");
            }
        }

        string? mismatchFailure = await RunPayloadTypeMismatchCheckAsync();
        if (mismatchFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, mismatchFailure);

        string? resilienceFailure = await RunDispatchExceptionResilienceCheckAsync();
        if (resilienceFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, resilienceFailure);

        string? clockFailure = RunRtpVideoClockRoundTripCheck();
        if (clockFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, clockFailure);

        string? splitterFailure = RunAnnexBNalSplitterExtraPaddingCheck();
        if (splitterFailure != null)
            return new Result(false, testPayloads.Count, receivedCount, splitterFailure);

        return new Result(true, testPayloads.Count, receivedCount, null);
    }

    /// <summary>Verifies the bug fixed in <see cref="AnnexBNalSplitter.Split"/> (see that method's
    /// own doc comment for the full reasoning) actually stays fixed: extra zero bytes between one
    /// NAL unit's real content and the next start code — Annex B permits an arbitrary number of
    /// leading_zero_8bits before a start code, not just the usual 2-3 — used to get silently
    /// absorbed into the PRECEDING NAL unit's returned slice as a spurious trailing zero byte,
    /// since the scanner's single-byte fallback only records where the start code it ends up
    /// matching begins, not where the zero run before it actually started. Pure byte-array input,
    /// no sockets/hardware at all — same category as <see cref="RunRtpVideoClockRoundTripCheck"/>.
    /// Returns null on success, or a failure message.</summary>
    private static string? RunAnnexBNalSplitterExtraPaddingCheck()
    {
        // One extra zero byte (at index 6) beyond the 2 that would normally immediately precede
        // the second start code's own leading zeros — this is exactly the shape that used to leak
        // a trailing 0x00 into the first NAL unit's slice.
        byte[] annexB =
        {
            0x00, 0x00, 0x01,             // first start code (3-byte)
            0x67, 0x41, 0x42,             // first NAL unit's real content
            0x00, 0x00,                   // extra padding zero bytes, not part of either NAL
            0x00, 0x00, 0x01,             // second start code (4-byte, one byte of it overlapping the padding above)
            0x68, 0x43, 0x44,             // second NAL unit's real content
        };

        var nalUnits = AnnexBNalSplitter.Split(annexB).Select(m => m.ToArray()).ToList();
        if (nalUnits.Count != 2)
            return $"AnnexBNalSplitter extra-padding check: expected exactly 2 NAL units, got {nalUnits.Count}.";

        byte[] expectedFirst = { 0x67, 0x41, 0x42 };
        byte[] expectedSecond = { 0x68, 0x43, 0x44 };
        if (!nalUnits[0].AsSpan().SequenceEqual(expectedFirst))
            return $"AnnexBNalSplitter extra-padding check: first NAL unit should be exactly {{{string.Join(", ", expectedFirst.Select(b => $"0x{b:X2}"))}}}, got {{{string.Join(", ", nalUnits[0].Select(b => $"0x{b:X2}"))}}} — a spurious trailing zero byte from the padding between the two start codes leaked through.";
        if (!nalUnits[1].AsSpan().SequenceEqual(expectedSecond))
            return $"AnnexBNalSplitter extra-padding check: second NAL unit should be exactly {{{string.Join(", ", expectedSecond.Select(b => $"0x{b:X2}"))}}}, got {{{string.Join(", ", nalUnits[1].Select(b => $"0x{b:X2}"))}}}.";

        return null;
    }

    /// <summary>Verifies <see cref="RtpVideoClock.FromElapsed(TimeSpan, uint)"/>/
    /// <see cref="RtpVideoClock.ToElapsedTicks"/>'s own documented round-trip and wraparound
    /// behavior — pure math, no sockets/GPU/audio hardware at all, the most self-contained check in
    /// this whole self-test (not even async). Confirms two things those methods' own doc comments
    /// only ever reasoned about in prose, never actually exercised: (1) a normal-range elapsed
    /// duration round-trips through FromElapsed -> ToElapsedTicks losslessly, and (2)
    /// <see cref="RtpVideoClock.FromElapsed(TimeSpan, uint)"/>'s "wraps naturally... receivers are
    /// supposed to handle that" comment is actually true of this implementation, not just a stated
    /// intent — a duration past the (2^32 / clockRate)-second boundary really does wrap to a
    /// small-looking timestamp that <see cref="RtpVideoClock.ToElapsedTicks"/> converts back to a
    /// dramatically smaller elapsed value, exactly the "indistinguishable from an early one"
    /// ambiguity that method's own doc comment warns callers about. Returns null on success, or a
    /// failure message.</summary>
    private static string? RunRtpVideoClockRoundTripCheck()
    {
        // An exact multiple of clockRate avoids any fractional-truncation ambiguity in
        // FromElapsed's own `(ulong)ticks` cast, so this round trip should be exact, not just close.
        var normal = TimeSpan.FromSeconds(1.5);
        uint normalRtp = RtpVideoClock.FromElapsed(normal, RtpVideoClock.ClockRate);
        if (normalRtp != 135000)
            return $"RtpVideoClock round-trip check: FromElapsed(1.5s, 90000Hz) should be exactly 135000, got {normalRtp}.";

        long roundTrippedTicks = RtpVideoClock.ToElapsedTicks(normalRtp, RtpVideoClock.ClockRate);
        if (roundTrippedTicks != normal.Ticks)
            return $"RtpVideoClock round-trip check: ToElapsedTicks(FromElapsed(1.5s)) should return exactly 1.5s of ticks ({normal.Ticks}), got {roundTrippedTicks}.";

        // Wraparound: past (2^32 / clockRate) seconds (~47721.86s at 90kHz — about 13.26 hours),
        // FromElapsed must wrap modulo 2^32 rather than throw or silently clamp — documented as
        // deliberate RFC 3550 behavior, not a bug, but until now nothing actually exercised it.
        // expectedWrapped is computed independently via `%` rather than mirroring FromElapsed's own
        // cast, so this is actually checking the implementation, not just restating it.
        var pastWrap = TimeSpan.FromSeconds(47722);
        ulong totalUnits = (ulong)(pastWrap.TotalSeconds * RtpVideoClock.ClockRate);
        uint expectedWrapped = (uint)(totalUnits % (1UL << 32));
        uint actualWrapped = RtpVideoClock.FromElapsed(pastWrap, RtpVideoClock.ClockRate);
        if (actualWrapped != expectedWrapped)
            return $"RtpVideoClock wraparound check: expected FromElapsed to wrap to {expectedWrapped}, got {actualWrapped}.";

        // The documented consequence: converting the wrapped value back looks like a dramatically
        // smaller elapsed time than the 47722 seconds that actually passed — this is the exact
        // ambiguity ToElapsedTicks's own doc comment warns about, now actually demonstrated rather
        // than just asserted in a comment.
        long wrappedBackTicks = RtpVideoClock.ToElapsedTicks(actualWrapped, RtpVideoClock.ClockRate);
        if (wrappedBackTicks >= pastWrap.Ticks)
            return "RtpVideoClock wraparound check: converting the wrapped timestamp back should yield a dramatically smaller elapsed value than what actually passed (that's the whole point of the documented ambiguity) — it didn't.";

        return null;
    }

    /// <summary>Verifies <see cref="RtpReceiver"/>'s <c>expectedPayloadType</c> mismatch handling
    /// (see this library's README "已知风险" item on this): a packet whose PayloadType doesn't match
    /// what the receiver was constructed with should be dropped — not delivered via
    /// <see cref="RtpReceiver.NalUnitReceived"/>, not counted in <see cref="RtpReceiver.PacketsReceived"/>
    /// or <see cref="RtpReceiver.GapEvents"/> — while a correctly-typed packet on the same receiver
    /// still arrives normally. Runs on its own fresh port/receiver rather than reusing the one above,
    /// so this check's mismatched packet can never be confused with (or count as a gap relative to)
    /// the sequence already verified there. Returns null on success, or a failure message.</summary>
    private static async Task<string?> RunPayloadTypeMismatchCheckAsync()
    {
        const byte expectedType = 96;
        const byte wrongType = 99;

        int port = GetLikelyFreeUdpPort();
        using var receiver = new RtpReceiver(port, expectedPayloadType: expectedType);

        var received = new List<byte[]>();
        var gate = new object();
        var matchingArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.NalUnitReceived += (nal, _, _) =>
        {
            lock (gate)
            {
                received.Add(nal);
                matchingArrived.TrySetResult();
            }
        };
        receiver.Start();

        var matchingPayload = MakeFakeNal(new Random(999), 100, nalType: 1);
        var mismatchedPayload = MakeFakeNal(new Random(998), 100, nalType: 1);

        // Sent in this order — mismatched first — precisely so a receiver that (incorrectly) didn't
        // drop it would surface as an extra/wrong item in `received`, not just a timing coincidence
        // that happened to let the correct packet win a race.
        using (var wrongSender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: wrongType))
            await wrongSender.SendNalUnitAsync(mismatchedPayload, 0, isLastNalOfAccessUnit: true);

        using (var rightSender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: expectedType))
            await rightSender.SendNalUnitAsync(matchingPayload, RtpVideoClock.ClockRate / 30, isLastNalOfAccessUnit: true);

        var finished = await Task.WhenAny(matchingArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != matchingArrived.Task)
            return "PayloadType mismatch check: timed out waiting for the correctly-typed NAL unit to arrive.";

        // Give the (already-sent, already-processed-or-not) mismatched packet a little more time in
        // case it's still in flight through the receive loop — on loopback UDP this is generous, not
        // a tight race; the matching packet above already proves the receive loop has caught up to
        // at least that point in the stream, sent after the mismatched one.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        lock (gate)
        {
            if (received.Count != 1)
                return $"PayloadType mismatch check: expected exactly 1 delivered NAL unit (the correctly-typed one), got {received.Count} — the mismatched packet was not dropped as expected.";
            if (!received[0].AsSpan().SequenceEqual(matchingPayload.Span))
                return "PayloadType mismatch check: the one delivered NAL unit's bytes don't match the correctly-typed payload that was sent.";
        }

        if (receiver.PayloadTypeMismatches != 1)
            return $"PayloadType mismatch check: expected PayloadTypeMismatches == 1, got {receiver.PayloadTypeMismatches}.";
        if (receiver.PacketsReceived != 1)
            return $"PayloadType mismatch check: expected PacketsReceived == 1 (the mismatched packet shouldn't count), got {receiver.PacketsReceived}.";
        if (receiver.GapEvents != 0)
            return $"PayloadType mismatch check: expected GapEvents == 0 (a dropped mismatch isn't a sequence gap), got {receiver.GapEvents}.";

        return null;
    }

    /// <summary>Verifies the defensive hardening this session added around
    /// <see cref="RtpReceiver.NalUnitReceived"/>'s dispatch (see <see cref="RtpReceiver.DispatchExceptions"/>'s
    /// own doc comment): a subscriber throwing must not kill the receive loop for every SUBSEQUENT
    /// packet, and must be counted. Until now nothing verified the counter actually increments, or —
    /// more importantly, since a counter that never moves is a much smaller problem than a receive
    /// loop that silently dies — that the loop genuinely keeps processing packets afterward, which is
    /// the actual property this fix exists to guarantee. Runs on its own fresh port/receiver, same
    /// reasoning as <see cref="RunPayloadTypeMismatchCheckAsync"/>.</summary>
    private static async Task<string?> RunDispatchExceptionResilienceCheckAsync()
    {
        int port = GetLikelyFreeUdpPort();
        using var receiver = new RtpReceiver(port);

        var received = new List<byte[]>();
        var gate = new object();
        bool firstCallSeen = false;
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.NalUnitReceived += (nal, _, _) =>
        {
            lock (gate)
            {
                if (!firstCallSeen)
                {
                    firstCallSeen = true;
                    // Deliberate — this is what RtpReceiver.DispatchExceptions/its surrounding
                    // try/catch exist to survive. A lock statement releases its Monitor via a
                    // compiler-generated finally even when the guarded code throws, so this doesn't
                    // risk deadlocking the receive loop on its next iteration.
                    throw new InvalidOperationException("Deliberate self-test exception — verifying the receive loop survives a subscriber throwing.");
                }
                received.Add(nal);
                secondArrived.TrySetResult();
            }
        };
        receiver.Start();

        using var sender = new RtpSession(new IPEndPoint(IPAddress.Loopback, port), payloadType: 96);
        var firstNal = MakeFakeNal(new Random(554), 100, nalType: 1);
        var secondNal = MakeFakeNal(new Random(553), 100, nalType: 1);

        await sender.SendNalUnitAsync(firstNal, 0, isLastNalOfAccessUnit: true);
        // Give the receive loop a moment to reach (and throw on) the first NAL unit before sending
        // the second — this is what proves the two are handled as genuinely separate loop
        // iterations, not that the second just happened to win a race before the first was ever
        // dispatched.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await sender.SendNalUnitAsync(secondNal, RtpVideoClock.ClockRate / 30, isLastNalOfAccessUnit: true);

        var finished = await Task.WhenAny(secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != secondArrived.Task)
            return "Dispatch-exception resilience check: timed out waiting for the SECOND NAL unit to arrive after the first subscriber call deliberately threw — the receive loop did not survive.";

        lock (gate)
        {
            if (received.Count != 1 || !received[0].AsSpan().SequenceEqual(secondNal.Span))
                return "Dispatch-exception resilience check: the second NAL unit's bytes don't match what was sent, or wasn't the only one recorded.";
        }

        if (receiver.DispatchExceptions != 1)
            return $"Dispatch-exception resilience check: expected DispatchExceptions == 1, got {receiver.DispatchExceptions}.";

        return null;
    }

    private static List<ReadOnlyMemory<byte>> BuildTestPayloads()
    {
        var rng = new Random(12345); // fixed seed: a self-test should be deterministic between runs.
        return new List<ReadOnlyMemory<byte>>
        {
            MakeFakeNal(rng, 200, nalType: 1),  // fits in one Single NAL Unit packet.
            MakeFakeNal(rng, 5000, nalType: 5), // forces FU-A fragmentation (default MTU budget is 1400).
            MakeFakeNal(rng, 50, nalType: 1),   // a small one after a fragmented one, to catch state leaking between NAL units.
        };
    }

    private static ReadOnlyMemory<byte> MakeFakeNal(Random rng, int size, byte nalType)
    {
        var bytes = new byte[size];
        rng.NextBytes(bytes);
        // Force a realistic NAL header shape (forbidden_zero_bit=0, a real nal_unit_type) so
        // H264RtpPacketizer's single-packet-vs-FU-A decision sees real input — everything after
        // the header byte is just random noise standing in for actual encoded video data.
        bytes[0] = (byte)(nalType & 0x1F);
        return bytes;
    }

    /// <summary>Binds an ephemeral UDP socket to find a currently-unused port, then releases it —
    /// a small, generally-accepted race (another process could grab the same port before
    /// RtpReceiver rebinds it) that's an acceptable risk for a local self-test, not something to
    /// harden against here.</summary>
    private static int GetLikelyFreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
