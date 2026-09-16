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

        return new Result(true, testPayloads.Count, receivedCount, null);
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
