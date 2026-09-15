using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EveryStage.Discovery;

/// <summary>
/// Wire format for LAN discovery/pairing (PLANNING.md §7: "发现：局域网UDP广播/mDNS"; UDP broadcast
/// chosen over mDNS here as the simpler, lower-risk option the doc explicitly allows either of).
///
/// This is THIS REPOSITORY'S OWN DRAFT, not a specification from PLANNING.md. It lives in this
/// shared project (rather than inside Terminal, where it started) because it now has two real
/// consumers that must agree on it byte-for-byte: EveryStage.Terminal (receiver/responder) and
/// EveryStage.Caster (sender/requester). Changing anything here changes both sides at once — that
/// coupling is the whole point of not letting each project keep its own copy.
/// </summary>
public static class DiscoveryProtocol
{
    /// <summary>Arbitrary, currently unregistered port picked for this draft protocol.</summary>
    public const int Port = 47990;

    /// <summary>Well-known, fixed UDP port both sides send/receive the live H.264-over-RTP video
    /// stream on (<c>EveryStage.Transport</c>'s <c>RtpSession</c>/<c>RtpReceiver</c>) — fixed
    /// rather than negotiated via SDP or similar, same reasoning as <see cref="Port"/> itself:
    /// simpler, and (today) there is only ever one active incoming video stream per Terminal, so
    /// there is nothing that needs disambiguating by a dynamically-chosen port.</summary>
    public const int VideoRtpPort = 47991;

    /// <summary>Well-known, fixed UDP port for the raw-PCM audio stream that accompanies a cast
    /// (<c>EveryStage.Transport</c>'s <c>RtpSession.SendRawPayloadAsync</c>/<c>RawRtpReceiver</c>) —
    /// a separate port from <see cref="VideoRtpPort"/> rather than muxing both onto one RTP session,
    /// since this project has no RTP session multiplexing (SSRC-based demuxing on one port) and
    /// audio/video use different payload framing (H.264 NAL/FU-A vs. a plain continuous byte
    /// stream) anyway.</summary>
    public const int AudioRtpPort = 47992;

    public abstract class Message
    {
        // Ignored on serialize: Encode() writes "type" itself (lowercase, once) after serializing
        // the rest of the message — without this attribute, reflection would also emit the base
        // class's own "Type" property (capital T) as a redundant second field on the wire.
        [JsonIgnore]
        public abstract string Type { get; }
    }

    /// <summary>Sent periodically, broadcast, by a Terminal — "here I am" for Caster-side discovery
    /// UI (PLANNING.md §12 "待机态：目标终端机列表") to build its list from.</summary>
    public sealed class BeaconMessage : Message
    {
        public override string Type => "beacon";
        public Guid DeviceId { get; set; }
        public string DeviceName { get; set; } = "";
    }

    /// <summary>Sent unicast, Caster -> Terminal, to request pairing (PLANNING.md §7 "配对：首次需
    /// 接收端确认").</summary>
    public sealed class PairRequestMessage : Message
    {
        public override string Type => "pair_request";
        public string RequestId { get; set; } = "";
        public Guid DeviceId { get; set; }
        public string DeviceName { get; set; } = "";
    }

    /// <summary>Sent unicast, Terminal -> Caster, answering a <see cref="PairRequestMessage"/>.</summary>
    public sealed class PairResponseMessage : Message
    {
        public override string Type => "pair_response";
        public string RequestId { get; set; } = "";
        public bool Accepted { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>Sent unicast, Caster -> Terminal, right before a live RTP/H.264 stream begins —
    /// lets the Terminal know a stream is coming and what resolution/payload type to configure its
    /// decoder and <c>RtpReceiver</c> for, rather than inferring "a stream started" purely from the
    /// arrival of RTP packets on <see cref="VideoRtpPort"/> (which alone carries no resolution
    /// information). Not something PLANNING.md specifies — this repository's own addition, needed
    /// once the video pipeline (built well after the discovery/pairing protocol was originally
    /// drafted) actually had to negotiate anything end to end.</summary>
    public sealed class CastStartMessage : Message
    {
        public override string Type => "cast_start";
        public Guid DeviceId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte PayloadType { get; set; }

        /// <summary>False when the Caster couldn't start audio capture at all (e.g.
        /// <c>AudioCaptureSource</c> construction threw) — the Terminal should not expect anything
        /// on <see cref="AudioRtpPort"/> in that case, and the fields below are meaningless.
        /// Casting video without audio is a real, expected outcome here, not an error state.</summary>
        public bool HasAudio { get; set; }
        public int AudioSampleRate { get; set; }
        public int AudioChannels { get; set; }
        public byte AudioPayloadType { get; set; }
    }

    /// <summary>Sent unicast, Caster -> Terminal, when the user stops casting — lets the Terminal
    /// return to standby deterministically instead of guessing "the stream stopped" from an RTP
    /// receive timeout (which this project doesn't implement).</summary>
    public sealed class CastStopMessage : Message
    {
        public override string Type => "cast_stop";
        public Guid DeviceId { get; set; }
    }

    /// <summary>Sent unicast, Terminal -> Caster, periodically while a cast is active — the
    /// acknowledgment channel this repository's own READMEs have flagged as missing ever since the
    /// live pipeline first connected: without this, a Caster has no way to know whether the
    /// Terminal actually received/decoded/played anything, and "投屏中" in its UI only ever meant
    /// "still sending without a local error". Not a full ack-per-packet protocol — just a periodic
    /// "still alive, here's roughly how much has gotten through" heartbeat, on the same
    /// best-effort, no-retry footing as every other message in this file.</summary>
    public sealed class CastStatusMessage : Message
    {
        public override string Type => "cast_status";

        /// <summary>The reporting Terminal's own DeviceId — lets a Caster that has cast to several
        /// terminals over time (never concurrently, see EveryStage.Caster's README "只支持单一目标")
        /// tell whose status this is, and lets <c>LiveCastSession</c> ignore a stray report from a
        /// terminal it isn't currently casting to.</summary>
        public Guid DeviceId { get; set; }
        public long FramesDecoded { get; set; }
        public long VideoBytesReceived { get; set; }
        public string? VideoError { get; set; }
        public bool HasAudio { get; set; }
        public long AudioBytesReceived { get; set; }
        public string? AudioError { get; set; }
    }

    public static byte[] Encode(Message message)
    {
        // Flatten to {"type": "...", ...the message's own fields} in one object, so a hand-written
        // test client can construct/read a valid packet without depending on this project's types.
        var node = JsonSerializer.SerializeToNode(message, message.GetType())!.AsObject();
        node["type"] = message.Type;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    /// <summary>Returns null for anything that isn't a recognized message of ours — a stray
    /// broadcast from an unrelated app sharing this port by coincidence should be ignored, not
    /// crash the receive loop.</summary>
    public static Message? Decode(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return null;

        return typeProp.GetString() switch
        {
            "beacon" => doc.RootElement.Deserialize<BeaconMessage>(),
            "pair_request" => doc.RootElement.Deserialize<PairRequestMessage>(),
            "pair_response" => doc.RootElement.Deserialize<PairResponseMessage>(),
            "cast_start" => doc.RootElement.Deserialize<CastStartMessage>(),
            "cast_stop" => doc.RootElement.Deserialize<CastStopMessage>(),
            "cast_status" => doc.RootElement.Deserialize<CastStatusMessage>(),
            _ => null,
        };
    }
}
