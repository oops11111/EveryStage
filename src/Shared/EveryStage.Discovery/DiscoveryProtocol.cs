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
    }

    /// <summary>Sent unicast, Caster -> Terminal, when the user stops casting — lets the Terminal
    /// return to standby deterministically instead of guessing "the stream stopped" from an RTP
    /// receive timeout (which this project doesn't implement).</summary>
    public sealed class CastStopMessage : Message
    {
        public override string Type => "cast_stop";
        public Guid DeviceId { get; set; }
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
            _ => null,
        };
    }
}
