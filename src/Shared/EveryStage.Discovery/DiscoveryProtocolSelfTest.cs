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

        return new Result(true, verified, null);
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
                if (o.SentAtUtc != r.SentAtUtc) return $"SentAtUtc {o.SentAtUtc:O} != {r.SentAtUtc:O}";
                if (o.FramesDecoded != r.FramesDecoded) return $"FramesDecoded {o.FramesDecoded} != {r.FramesDecoded}";
                if (o.VideoBytesReceived != r.VideoBytesReceived) return $"VideoBytesReceived {o.VideoBytesReceived} != {r.VideoBytesReceived}";
                if (o.VideoError != r.VideoError) return $"VideoError '{o.VideoError}' != '{r.VideoError}'";
                if (o.HasAudio != r.HasAudio) return $"HasAudio {o.HasAudio} != {r.HasAudio}";
                if (o.AudioBytesReceived != r.AudioBytesReceived) return $"AudioBytesReceived {o.AudioBytesReceived} != {r.AudioBytesReceived}";
                if (o.AudioError != r.AudioError) return $"AudioError '{o.AudioError}' != '{r.AudioError}'";
                if (o.PayloadTypeMismatches != r.PayloadTypeMismatches) return $"PayloadTypeMismatches {o.PayloadTypeMismatches} != {r.PayloadTypeMismatches}";
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
            new DiscoveryProtocol.PairResponseMessage { RequestId = "req-1", Accepted = false, Reason = "用户拒绝" },
            new DiscoveryProtocol.PairResponseMessage { RequestId = "req-2", Accepted = true, Reason = null },
            new DiscoveryProtocol.CastStartMessage
            {
                DeviceId = deviceId, Width = 1920, Height = 1080, PayloadType = 96,
                HasAudio = true, AudioSampleRate = 48000, AudioChannels = 2, AudioPayloadType = 97,
                AudioIsAac = true,
            },
            new DiscoveryProtocol.CastStopMessage { DeviceId = deviceId },
            new DiscoveryProtocol.CastStatusMessage
            {
                DeviceId = deviceId, SentAtUtc = DateTimeOffset.UtcNow, FramesDecoded = 12345,
                VideoBytesReceived = 987654321, VideoError = "解码失败：测试用错误信息",
                HasAudio = true, AudioBytesReceived = 123456, AudioError = null,
                PayloadTypeMismatches = 3,
            },
            new DiscoveryProtocol.PingMessage { RequestId = "ping-1" },
            new DiscoveryProtocol.PongMessage { RequestId = "ping-1" },
        };
    }
}
