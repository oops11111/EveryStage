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
    /// <summary>Wire compatibility boundary. Messages from a different version are ignored rather
    /// than deserialized with dangerous default values for fields introduced later.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Arbitrary, currently unregistered port picked for this draft protocol.</summary>
    public const int Port = 47990;

    /// <summary>Well-known, fixed UDP port both sides send/receive the live H.264-over-RTP video
    /// stream on (<c>EveryStage.Transport</c>'s <c>RtpSession</c>/<c>RtpReceiver</c>) — fixed
    /// rather than negotiated via SDP or similar, same reasoning as <see cref="Port"/> itself:
    /// simpler, and (today) there is only ever one active incoming video stream per Terminal, so
    /// there is nothing that needs disambiguating by a dynamically-chosen port.</summary>
    public const int VideoRtpPort = 47991;

    /// <summary>Well-known, fixed UDP port for the audio stream that accompanies a cast
    /// (<c>EveryStage.Transport</c>'s <c>RtpSession.SendRawPayloadAsync</c>/<c>RawRtpReceiver</c>) —
    /// a separate port from <see cref="VideoRtpPort"/> rather than muxing both onto one RTP session,
    /// since this project has no RTP session multiplexing (SSRC-based demuxing on one port) and
    /// audio/video use different payload framing (H.264 NAL/FU-A vs. a plain continuous byte
    /// stream) anyway. Carries raw 16-bit PCM or ADTS-framed AAC access units depending on
    /// <see cref="CastStartMessage.AudioIsAac"/> — same port either way, since only one is ever
    /// active per cast and the receiving side already has to be told which framing to expect
    /// out-of-band via that field regardless of which port the bytes arrive on.</summary>
    public const int AudioRtpPort = 47992;

    public abstract class Message
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = CurrentVersion;

        // Ignored on serialize: Encode() writes "type" itself (lowercase, once) after serializing
        // the rest of the message — without this attribute, reflection would also emit the base
        // class's own "Type" property (capital T) as a redundant second field on the wire.
        [JsonIgnore]
        public abstract string Type { get; }
    }

    public abstract class AuthenticatedMessage : Message
    {
        public Guid SenderDeviceId { get; set; }
        public Guid MessageId { get; set; }
        public DateTimeOffset IssuedAtUtc { get; set; }
        public string? AuthenticationTag { get; set; }
    }

    /// <summary>Sent periodically, broadcast, by a Terminal — "here I am" for Caster-side discovery
    /// UI (PLANNING.md §12 "待机态：目标终端机列表") to build its list from.</summary>
    public sealed class BeaconMessage : Message
    {
        public int MediaAuthenticationVersion { get; set; }
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
        /// <summary>Base64-encoded 256-bit device key issued only after the Terminal accepts pairing.
        /// Null for a rejection. Subsequent authenticated control traffic uses this shared key.</summary>
        public string? PairingKey { get; set; }
        public int PairingKeyFormatVersion { get; set; }
    }

    /// <summary>Sent unicast, Caster -> Terminal, right before a live RTP/H.264 stream begins —
    /// lets the Terminal know a stream is coming and what resolution/payload type to configure its
    /// decoder and <c>RtpReceiver</c> for, rather than inferring "a stream started" purely from the
    /// arrival of RTP packets on <see cref="VideoRtpPort"/> (which alone carries no resolution
    /// information). Not something PLANNING.md specifies — this repository's own addition, needed
    /// once the video pipeline (built well after the discovery/pairing protocol was originally
    /// drafted) actually had to negotiate anything end to end.</summary>
    public sealed class CastStartMessage : AuthenticatedMessage
    {
        public Guid MediaSessionId { get; set; }
        public int MediaAuthenticationVersion { get; set; }
        public override string Type => "cast_start";
        public Guid DeviceId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Checked against every video RTP packet's own <c>RtpPacket.PayloadType</c> by
        /// <c>Terminal.Receiving.CastReceiver</c>'s <c>EveryStage.Transport.RtpReceiver</c> — a
        /// mismatch is dropped, treated exactly like a packet that failed to decode at all (see that
        /// library's README). Not a real SDP-style negotiation: both ends just happen to use the
        /// same hardcoded constant, so this is a defensive check against a stray/foreign packet on
        /// the same port, not something ever expected to actually fire in this project's own
        /// traffic.</summary>
        public byte PayloadType { get; set; }

        /// <summary>False when the Caster couldn't start audio capture at all (e.g.
        /// <c>AudioCaptureSource</c>/<c>AacAudioEncoder</c> construction threw) — the Terminal should
        /// not expect anything on <see cref="AudioRtpPort"/> in that case, and the fields below are
        /// meaningless. Casting video without audio is a real, expected outcome here, not an error
        /// state.</summary>
        public bool HasAudio { get; set; }
        public int AudioSampleRate { get; set; }
        public int AudioChannels { get; set; }

        /// <summary>Same role as <see cref="PayloadType"/> above, checked by
        /// <c>EveryStage.Transport.RawRtpReceiver</c> instead — see that field's own doc comment.</summary>
        public byte AudioPayloadType { get; set; }

        /// <summary>True means each <see cref="AudioRtpPort"/> payload is one ADTS-framed AAC access
        /// unit (<c>Caster.Encode.AacAudioEncoder</c>) that the Terminal must run through
        /// <c>EveryStage.Rendering.Decode.AacAudioDecoder</c> before playback; false means raw
        /// interleaved 16-bit PCM, playable directly. Added this round alongside the Caster side
        /// actually switching to AAC (see this project's README) — a Terminal built before this field
        /// existed would default-deserialize it to false and misinterpret an AAC stream as raw PCM,
        /// producing noise; there is no protocol-version negotiation in this repo to guard against
        /// that mismatch; both sides are expected to be redeployed from the same commit.</summary>
        public bool AudioIsAac { get; set; }
    }

    /// <summary>Sent unicast, Caster -> Terminal, when the user stops casting — lets the Terminal
    /// return to standby deterministically instead of guessing "the stream stopped" from an RTP
    /// receive timeout (which this project doesn't implement).</summary>
    public sealed class CastStopMessage : AuthenticatedMessage
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
    public sealed class CastStatusMessage : AuthenticatedMessage
    {
        public override string Type => "cast_status";

        /// <summary>The reporting Terminal's own DeviceId — lets a Caster that has cast to several
        /// terminals over time (never concurrently, see EveryStage.Caster's README "只支持单一目标")
        /// tell whose status this is, and lets <c>LiveCastSession</c> ignore a stray report from a
        /// terminal it isn't currently casting to.</summary>
        public Guid DeviceId { get; set; }

        /// <summary>Changes whenever the Terminal discovery service is recreated, allowing sequence
        /// numbers to restart after a process restart without being mistaken for stale packets.</summary>
        public Guid SessionId { get; set; }

        /// <summary>Monotonically increasing per Terminal process. The Caster ACKs every received
        /// value but only publishes values newer than the last one seen for this device.</summary>
        public long SequenceNumber { get; set; }

        /// <summary>The Terminal's own clock at the moment it built this message (not when the
        /// socket actually put it on the wire, and not adjusted for how long <c>SendCastStatusAsync</c>
        /// itself takes) — added so a receiving Caster can estimate one-way latency/staleness instead
        /// of only knowing "a status arrived just now" (<c>LiveCastSession.LastStatusReceivedAt</c>
        /// already covered that half). NOTE: this is only a meaningful latency estimate if the
        /// Terminal's and Caster's system clocks are reasonably synchronized (e.g. both on the same
        /// NTP-synced LAN) — this protocol has no clock-offset negotiation of its own, so a Caster
        /// computing <c>DateTimeOffset.UtcNow - SentAtUtc</c> against an unsynchronized Terminal
        /// clock would get a number that reflects clock skew, not network delay, with no way to tell
        /// the two apart from this field alone.</summary>
        public DateTimeOffset SentAtUtc { get; set; }

        public long FramesDecoded { get; set; }
        public long VideoBytesReceived { get; set; }
        public string? VideoError { get; set; }
        public bool HasAudio { get; set; }
        public long AudioBytesReceived { get; set; }
        public string? AudioError { get; set; }

        /// <summary>Combined video+audio count of <c>EveryStage.Transport.RtpReceiver.PayloadTypeMismatches</c>
        /// / <c>RawRtpReceiver.PayloadTypeMismatches</c> — see either property's own doc comment.
        /// Expected to be 0 in this project's own Caster-to-Terminal traffic always (see
        /// <see cref="CastStartMessage.PayloadType"/>'s doc comment); a Caster that sees this go
        /// non-zero is hearing about a stray/foreign RTP-shaped datagram landing on its Terminal's
        /// listening port, not a normal operating condition, so unlike the byte/frame counters above
        /// this one is meant to stay invisible in the UI until it actually happens (see
        /// <c>Caster.UI.MainForm.RefreshLiveCastStats</c>'s only-shown-if-nonzero treatment).</summary>
        public long PayloadTypeMismatches { get; set; }
        public double? EstimatedPacketLossPercent { get; set; }
        public long VideoPacketsLost { get; set; }
    }

    /// <summary>Caster acknowledgment for one status report. A duplicate report receives another
    /// ACK so a lost ACK can recover without delivering duplicate status to the application.</summary>
    public sealed class CastStartAckMessage : AuthenticatedMessage
    {
        public override string Type => "cast_start_ack";
        public Guid MediaSessionId { get; set; }
        public bool Ready { get; set; }
        public string? Error { get; set; }
    }

    public sealed class CastStatusAckMessage : AuthenticatedMessage
    {
        public override string Type => "cast_status_ack";
        public Guid DeviceId { get; set; }
        public Guid SessionId { get; set; }
        public long SequenceNumber { get; set; }
    }

    /// <summary>Sent unicast, Caster -> Terminal, purely to measure real network round-trip time —
    /// added alongside <see cref="CastStatusMessage.SentAtUtc"/>'s latency estimate to give a second,
    /// clock-skew-immune number: that estimate reads <c>DateTimeOffset.UtcNow - SentAtUtc</c> across
    /// two machines' independent clocks, which conflates real network delay with however far apart
    /// those two clocks' wall time actually is (this repo has no way to verify real-world clock sync
    /// between two Windows machines from this sandbox, and the receiving side's own doc comment
    /// already says so). A ping/pong round trip instead measures elapsed time entirely on the
    /// Caster's own clock (send, then time until the matching <see cref="PongMessage"/> arrives) —
    /// no cross-machine clock comparison at all, so it can't be skewed by one. Unauthenticated like
    /// every other message here (see this project's README): a Terminal echoes any ping addressed to
    /// it, paired or not, which adds no new attack surface beyond what this protocol's existing
    /// cleartext, no-signature design already accepts.</summary>
    public sealed class PingMessage : Message
    {
        public override string Type => "ping";
        public string RequestId { get; set; } = "";
    }

    /// <summary>Sent unicast, Terminal -> Caster, immediately upon receiving a <see cref="PingMessage"/>
    /// — see that class's own doc comment. Deliberately carries nothing but the correlating
    /// <see cref="RequestId"/>: this measures round-trip transport time, not anything about the
    /// Terminal's own state (that's <see cref="CastStatusMessage"/>'s job).</summary>
    public sealed class PongMessage : Message
    {
        public override string Type => "pong";
        public string RequestId { get; set; } = "";
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
    /// crash the receive loop. **Bug fixed here, found by an audit subagent hunting for the same
    /// "silently kills a background receive loop forever" shape this project had already found and
    /// fixed once in `RawRtpReceiver` (see this library's own README)**: <see cref="JsonDocument.Parse"/>
    /// happily accepts any valid JSON token as the root — a bare number, string, boolean, `null`, or
    /// array is all valid JSON, not just an object — but <see cref="JsonElement.TryGetProperty"/>
    /// throws <see cref="InvalidOperationException"/> (NOT <see cref="JsonException"/>) if
    /// <see cref="JsonElement.ValueKind"/> isn't <see cref="JsonValueKind.Object"/>. Both this
    /// project's real call sites (<c>Terminal.Devices.DiscoveryService.HandleDatagram</c>,
    /// <c>Caster.Discovery.TerminalDiscoveryClient.HandleDatagram</c>) only ever caught
    /// <see cref="JsonException"/> around this call — a stray non-object-but-valid-JSON datagram on
    /// this shared, unauthenticated, arbitrarily-chosen port (see this library's README on
    /// <see cref="Port"/> never having been checked against other LAN software) would throw straight
    /// through both of those try/catches, out of the bare <c>Task.Run</c> each side's receive loop
    /// runs as, and die there completely unobserved (no
    /// <c>AppDomain.UnhandledException</c>/<c>TaskScheduler.UnobservedTaskException</c> handler exists
    /// anywhere in either app) — permanently killing that side's ability to process ANY further
    /// discovery/pairing/cast-status traffic, with zero log entry and zero visible symptom at the
    /// moment it happens (the beacon-broadcast loop on the Terminal side is a separate `Task`, so a
    /// Terminal in this state keeps *looking* alive while being unable to answer anything). Far worse
    /// than the "messages can be lost on the wire" best-effort limitation this protocol already
    /// accepts (a lost packet is retried and mostly succeeds next attempt; this kills 100% of future
    /// traffic on whichever side hits it, forever). Fixed at the source, in this one shared method,
    /// rather than broadening each call site's own catch clause independently — protects both
    /// existing callers and any future one without relying on each remembering to do it themselves.</summary>
    public static Message? Decode(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null; // not an object at all — same "not one of ours" treatment as an unrecognized "type" value below.
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return null;
        // Second instance of the exact same throw-on-wrong-ValueKind shape the check above fixes:
        // JsonElement.GetString() throws InvalidOperationException (not JsonException) for any
        // ValueKind other than String or Null — a stray datagram shaped like {"type": 123} or
        // {"type": true} would otherwise hit this immediately below.
        if (typeProp.ValueKind != JsonValueKind.String) return null;
        if (!doc.RootElement.TryGetProperty("protocolVersion", out var versionProp)) return null;
        if (versionProp.ValueKind != JsonValueKind.Number
            || !versionProp.TryGetInt32(out int version)
            || version != CurrentVersion) return null;

        return typeProp.GetString() switch
        {
            "beacon" => doc.RootElement.Deserialize<BeaconMessage>(),
            "pair_request" => doc.RootElement.Deserialize<PairRequestMessage>(),
            "pair_response" => doc.RootElement.Deserialize<PairResponseMessage>(),
            "cast_start" => doc.RootElement.Deserialize<CastStartMessage>(),
            "cast_start_ack" => doc.RootElement.Deserialize<CastStartAckMessage>(),
            "cast_stop" => doc.RootElement.Deserialize<CastStopMessage>(),
            "cast_status" => doc.RootElement.Deserialize<CastStatusMessage>(),
            "cast_status_ack" => doc.RootElement.Deserialize<CastStatusAckMessage>(),
            "ping" => doc.RootElement.Deserialize<PingMessage>(),
            "pong" => doc.RootElement.Deserialize<PongMessage>(),
            _ => null,
        };
    }
}
