using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EveryStage.Terminal.Devices;

/// <summary>
/// Wire format for LAN discovery/pairing (PLANNING.md §7: "发现：局域网UDP广播/mDNS"; UDP broadcast
/// chosen over mDNS here as the simpler, lower-risk option the doc explicitly allows either of).
///
/// This is THIS REPOSITORY'S OWN DRAFT, not a specification from PLANNING.md — the doc says
/// discovery/pairing should exist and broadly how (UDP broadcast or mDNS; PIN/popup confirmation;
/// remembered device fingerprint; independent cast/monitor permissions), but never defines a wire
/// format, since the Caster side that must speak the same protocol doesn't exist as code yet
/// anywhere in this repo. Treat every field/port/message shape here as a placeholder to be
/// reconciled once Caster development actually starts — whichever side is built second should
/// adapt to real constraints discovered building the first, not assume this file is final.
/// </summary>
public static class DiscoveryProtocol
{
    /// <summary>Arbitrary, currently unregistered port picked for this draft protocol.</summary>
    public const int Port = 47990;

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
            _ => null,
        };
    }
}
