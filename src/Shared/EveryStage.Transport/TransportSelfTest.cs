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

        receiver.NalUnitReceived += (nal, _) =>
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

        return new Result(true, testPayloads.Count, receivedCount, null);
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
