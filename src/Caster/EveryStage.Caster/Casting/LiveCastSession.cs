using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using EveryStage.Caster.Capture;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Transport;
using Vortice.Direct3D11;

namespace EveryStage.Caster.Casting;

/// <summary>
/// The real "开始投屏" pipeline (PLANNING.md §12), and this repository's first attempt at wiring
/// every previously-isolated piece into one live stream: <see cref="ScreenCaptureSource"/> (BGRA
/// texture) -> <see cref="BgraToNv12Converter"/> (NV12 texture) -> <see cref="H264HardwareEncoder"/>
/// (Annex-B H.264 access units) -> <see cref="AnnexBNalSplitter"/> (individual NAL units) ->
/// <see cref="RtpSession"/> (RTP-over-UDP), sent to the paired Terminal's
/// <see cref="DiscoveryProtocol.VideoRtpPort"/>, plus <see cref="AudioCaptureSource"/> (16-bit PCM)
/// -> <see cref="AacAudioEncoder"/> (ADTS-framed AAC access units) -> a second
/// <see cref="RtpSession"/> on <see cref="DiscoveryProtocol.AudioRtpPort"/>. Sends AAC rather than
/// raw PCM as of this round (see <see cref="OnPcmCaptured"/>/<see cref="OnAacAccessUnitEncoded"/>
/// and this project's README risk #53-54 for the two encode/decode MFTs this replaces raw PCM with,
/// and everything about them that's still unverified on real hardware) — no RTP fragmentation is
/// needed for this stream even though <see cref="H264RtpPacketizer"/>-style splitting exists for
/// video, because one AAC access unit (a few hundred bytes at typical bitrates) comfortably fits
/// under a UDP payload budget that raw ~10ms WASAPI PCM buffers used to threaten; an unusually large
/// access unit (this round doesn't control/cap the negotiated bitrate — see
/// <c>AacAudioEncoder.ConfigureOutputType</c>'s own doc comment) would rely on ordinary IP
/// fragmentation rather than anything this class does, which this repo has no way to verify holds up
/// on a real LAN. <see cref="EncodeSelfTestRunner"/> already exercises the video chain up through the
/// encoder in isolation, and <c>Encode.AacEncodeSelfTestRunner</c> exercises the audio encode+decode
/// round trip in isolation; this class is the first to actually put AAC on the wire between the two
/// real processes rather than looping it back to itself in one. Signals the Terminal out-of-band
/// (<see cref="DiscoveryProtocol.CastStartMessage"/> / <see cref="DiscoveryProtocol.CastStopMessage"/>)
/// so it knows a stream is starting/stopping and what resolution/audio format (now including which
/// codec) to expect, instead of the Terminal having to infer that purely from RTP packets arriving on
/// a fixed port.
///
/// Audio is best-effort on top of video, not a co-equal requirement: if
/// <see cref="AudioCaptureSource"/> or <see cref="AacAudioEncoder"/> fails to construct (no active
/// audio session, a WASAPI error, no AAC encoder MFT on this machine), this class still casts
/// video-only rather than failing the whole session — PLANNING.md's screen capture is the core
/// feature, and a machine with nothing currently producing audio (or, now, no AAC encoder
/// registered) is a normal state, not an error.
///
/// Now has a real, if lightweight, acknowledgment channel: the Terminal reports back periodically
/// (<see cref="DiscoveryProtocol.CastStatusMessage"/>, roughly once a second, see
/// <c>TerminalApplicationContext.SendCastStatus</c> on the Terminal side) with how many frames it has
/// actually decoded and how many audio bytes it has received. That's not per-packet acknowledgment
/// or flow control — it's the smallest thing that turns "投屏中" from a purely local claim into
/// something the Terminal has actually confirmed, exposed here as <see cref="TerminalFramesDecoded"/>
/// and friends, and <see cref="IsTerminalAlive"/> for "has it said anything recently". If the
/// Terminal never accepted this cast in the first place (no display bound, no permission, a
/// construction failure), no status ever arrives and <see cref="IsTerminalAlive"/> stays false
/// forever — this class still can't distinguish "never confirmed" from "confirmed once, then went
/// silent", only whether a confirmation is currently recent.
///
/// One instance is single-use: <see cref="Start"/> then, once, <see cref="Stop"/> or
/// <see cref="Dispose"/> — the internal send queues are completed on stop and cannot be reopened.
/// This matches how the UI actually uses it (a fresh instance is constructed per successful
/// pairing), so re-starting the same instance was never a case worth supporting.
/// </summary>
public sealed class LiveCastSession : IDisposable
{
    // RFC 3551 dynamic payload type range — arbitrary, fixed by convention between this repo's two
    // sides the same way every other unnegotiated parameter here is (no SDP or equivalent exists).
    // Also sent explicitly in CastStartMessage.PayloadType/AudioPayloadType so the Terminal doesn't
    // additionally need its own hardcoded copies of these constants, even though today both sides
    // happen to agree anyway.
    private const byte PayloadType = 96;
    private const byte AudioPayloadType = 97;

    // AAC's fixed frame size (samples per channel) — one AacAudioEncoder.AccessUnitEncoded callback
    // represents exactly this many samples' worth of audio, used to derive each access unit's own
    // RTP timestamp within a batch (see OnPcmCaptured/OnAacAccessUnitEncoded). Independently
    // duplicated from AacAudioDecoder.SamplesPerFrame rather than shared — same "each file keeps its
    // own independently-verifiable copy" convention this repo already applies everywhere else.
    private const int AacSamplesPerFrame = 1024;

    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly DiscoveredTerminal _terminal;
    private readonly int _outputIndex;
    private readonly Stopwatch _clock = new();

    // Unbounded, single-reader: H264HardwareEncoder's own event-loop thread (via
    // OnAccessUnitEncoded) is the only writer, and a dedicated RunSendLoop task is the only reader.
    // Queuing (rather than firing off SendNalUnitAsync directly from OnAccessUnitEncoded) exists
    // for one reason: RTP requires strictly increasing sequence numbers in send order, and
    // RtpReceiver never reorders (see EveryStage.Transport's README) — if two NAL sends ran
    // concurrently as independent fire-and-forget tasks, their SendAsync completions (and therefore
    // which one's bytes actually reach the socket first) could interleave in a different order than
    // they were produced in, silently corrupting playback on the receiving end. A single sequential
    // reader task makes "sent in encode order" true by construction instead of by hoping two
    // concurrent async sends happen to complete in the order they were started.
    private readonly Channel<(ReadOnlyMemory<byte> Nal, uint Timestamp, bool IsLastNalOfAccessUnit)> _sendQueue =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, uint, bool)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Same ordering reasoning as _sendQueue, applied to audio: AacAudioEncoder.AccessUnitEncoded
    // fires sequentially (synchronously, from within OnPcmCaptured — see AacAudioEncoder's own doc
    // comment on having no background thread of its own) on NAudio's own capture callback thread,
    // but fire-and-forget sends from that callback could still complete out of order relative to
    // each other. Holds ADTS-framed AAC access units now, not raw PCM chunks (see this class's own
    // doc comment) — "Payload" rather than "Pcm" reflects that; RunAudioSendLoop itself doesn't care
    // either way, since RtpSession.SendRawPayloadAsync is payload-agnostic.
    private readonly Channel<(byte[] Payload, uint Timestamp)> _audioSendQueue =
        Channel.CreateUnbounded<(byte[], uint)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // Backpressure depth counters for the two queues above — see OnAccessUnitEncoded/OnAacAccessUnitEncoded
    // for the drop policy these back, and this project's README (risk #25's residual concern: "无
    // 界的...没有背压或丢弃策略") for why this was previously entirely unimplemented. Interlocked
    // rather than a lock: the writer thread (encoder event loop / NAudio capture callback) only
    // ever increments, the reader thread (RunSendLoop/RunAudioSendLoop) only ever decrements, and
    // neither side needs the read-and-act to be one atomic step — a queue depth that's off by one
    // for a moment costs nothing here, unlike the queue contents themselves (which stay ordered by
    // the Channel itself, untouched by this counter).
    private int _queuedAccessUnitCount;
    private int _queuedAudioChunkCount;

    // Roughly 2 seconds of video (30fps access units); the audio side's own budget used to be "~1
    // second of ~10ms raw PCM chunks" (100 of them) before this round switched audio to AAC — a
    // ~23ms AAC frame (1024 samples at 44.1kHz) means 100 of them is now closer to ~2.3 seconds, not
    // deliberately retuned for the new frame size, just inherited as-is (same "no real network to
    // tune against" reasoning this project's README gives for every other timing constant here; a
    // generous-rather-than-precise budget errs the same direction either way). Picked the same way
    // every other timing constant in this repo lacking a real network to tune against is (see this
    // project's README) — generous enough that a brief send-side hiccup doesn't start dropping data,
    // bounded so a genuinely stuck network connection can't grow either queue forever, which is the
    // whole point of this being a *bounded* backlog policy rather than the unbounded-forever
    // behavior this was before.
    private const int MaxQueuedAccessUnits = 60;
    private const int MaxQueuedAudioChunks = 100;

    private D3D11Device? _gpu;
    private ScreenCaptureSource? _capture;
    private BgraToNv12Converter? _converter;
    private H264HardwareEncoder? _encoder;
    private RtpSession? _rtpSession;
    private AudioCaptureSource? _audioCapture;
    private AacAudioEncoder? _audioEncoder;
    private RtpSession? _audioRtpSession;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _sendLoopTask;
    private Task? _audioSendLoopTask;
    private Task? _pingLoopTask;

    // How often RunPingLoop measures RealRoundTripEstimate — arbitrary, picked the same way every
    // other timing constant in this repo lacking a real network to tune against is (see this
    // project's README): frequent enough that the displayed number stays reasonably current, far
    // less often than actual media traffic so it can't meaningfully compete with it for bandwidth.
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    // Set at the start of each OnPcmCaptured call, read/advanced by OnAacAccessUnitEncoded — see
    // that method's own doc comment for why these need to be per-batch state rather than computed
    // independently per access unit. Safe as plain fields (not Interlocked/volatile): both methods
    // only ever run on the same thread, synchronously, within a single OnPcmCaptured call — never
    // concurrently with themselves or each other (same single-capture-callback-thread invariant
    // AudioCaptureSource's own doc comment already establishes).
    private uint _aacBatchBaseTimestamp;
    private int _aacSamplesEmittedInBatch;

    /// <summary>True from <see cref="Start"/> until <see cref="StopInternal"/> nulls
    /// <see cref="_loopTask"/> back out — NOT the same as "the capture loop is actually still
    /// executing": a <c>ScreenCaptureLostException</c>/other exception can make
    /// <see cref="RunLoop"/> itself exit early (see its own doc comment) while
    /// <see cref="_loopTask"/> stays non-null (nobody has called <see cref="Stop"/> yet), so this
    /// keeps reporting true — a deliberate "zombie" state, not a bug: it's exactly what makes
    /// <see cref="Start"/>'s own <c>if (IsRunning) return;</c> guard safe. Because that guard blocks
    /// a second <see cref="Start"/> call (and therefore <see cref="_cts"/> ever being reassigned to a
    /// different instance) until <see cref="Stop"/> has fully run, <see cref="RunLoop"/>'s own
    /// <c>finally</c> block can safely call <c>_cts?.Cancel()</c> on itself after an early exit
    /// without any risk of that racing a newer session's <see cref="_cts"/>.</summary>
    public bool IsRunning => _loopTask != null;

    /// <summary>Wall-clock time since <see cref="Start"/> restarted <see cref="_clock"/> — the same
    /// Stopwatch video/audio RTP timestamps are derived from (see <see cref="RtpVideoClock"/>'s doc
    /// comment), exposed here purely for UI display (PLANNING.md §12's "投屏中" state time indicator)
    /// with no bearing on the RTP/sync math itself. Reads as <c>TimeSpan.Zero</c> before <see
    /// cref="Start"/> has ever run, since a not-yet-started <see cref="Stopwatch"/> reports zero
    /// elapsed time on its own.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesCaptured { get; private set; }
    public long AccessUnitsSent { get; private set; }
    public long BytesSent { get; private set; }

    /// <summary>Access units dropped whole (never enqueued at all) because <see cref="_sendQueue"/>
    /// was already <see cref="MaxQueuedAccessUnits"/> deep when they finished encoding — see
    /// <see cref="OnAccessUnitEncoded"/> for the policy. Should stay at 0 on any healthy LAN; a
    /// climbing count means the network send side can't keep up with the encoder.</summary>
    public long AccessUnitsDroppedForBackpressure { get; private set; }

    /// <summary>Forwards <see cref="H264HardwareEncoder.FramesDroppedForBackpressure"/> — a
    /// different bottleneck than <see cref="AccessUnitsDroppedForBackpressure"/> (that one is the
    /// network send side falling behind the encoder; this one is the encoder itself falling behind
    /// capture). Read directly from the encoder rather than cached/summed here, since the encoder is
    /// the single source of truth for its own drop count and this class doesn't need to reset it
    /// mid-session (it already resets to 0 inside a fresh <see cref="H264HardwareEncoder"/> each
    /// <see cref="Start"/>). Reads as 0 before <see cref="Start"/> has constructed an encoder.</summary>
    public int EncoderFramesDroppedForBackpressure => _encoder?.FramesDroppedForBackpressure ?? 0;

    public string? LastError { get; private set; }

    /// <summary>Whether audio capture actually started for this session — false means this cast is
    /// video-only, either because <see cref="AudioCaptureSource"/>/<see cref="AacAudioEncoder"/>
    /// failed to construct or because <see cref="Start"/> hasn't run yet.</summary>
    public bool HasAudio { get; private set; }

    /// <summary>AAC bytes actually sent (as of this round — previously raw PCM bytes captured, back
    /// when this class sent PCM unmodified). Incremented per encoded access unit in
    /// <see cref="OnAacAccessUnitEncoded"/>, not per PCM buffer captured, precisely so this keeps
    /// meaning "bytes that went on the wire" rather than "bytes captured before compression".</summary>
    public long AudioBytesSent { get; private set; }

    /// <summary>Same role as <see cref="AccessUnitsDroppedForBackpressure"/>, for
    /// <see cref="_audioSendQueue"/> — see <see cref="OnAacAccessUnitEncoded"/>.</summary>
    public long AudioChunksDroppedForBackpressure { get; private set; }

    /// <summary>Set when audio capture/sending fails — unlike <see cref="LastError"/> this never
    /// stops the video side of the session; see this class's doc comment on audio being
    /// best-effort.</summary>
    public string? AudioError { get; private set; }

    // --- The Terminal's own confirmation, via DiscoveryProtocol.CastStatusMessage — the
    // acknowledgment channel this project's READMEs have flagged as missing since the live pipeline
    // first connected. Everything above this point is purely local ("did sending fail?"); everything
    // below is what the Terminal itself has reported back, best-effort and on its own 1-second timer
    // (see TerminalApplicationContext.SendCastStatus), so it always lags slightly behind the local
    // stats and can go stale if the Terminal stops reporting (see LastStatusReceivedAt/IsTerminalAlive).

    public long TerminalFramesDecoded { get; private set; }
    public long TerminalVideoBytesReceived { get; private set; }
    public string? TerminalVideoError { get; private set; }
    public bool TerminalHasAudio { get; private set; }
    public long TerminalAudioBytesReceived { get; private set; }
    public string? TerminalAudioError { get; private set; }

    /// <summary>Mirrors <c>DiscoveryProtocol.CastStatusMessage.PayloadTypeMismatches</c> — see that
    /// field's own doc comment. Expected to stay 0; see <c>Caster.UI.MainForm.RefreshLiveCastStats</c>
    /// for why this is only ever shown once it isn't.</summary>
    public long TerminalPayloadTypeMismatches { get; private set; }

    /// <summary>UTC time of the last <c>CastStatusMessage</c> received from this session's Terminal
    /// — null if none has arrived yet (could mean "just started, give it a second" or "the Terminal
    /// never actually accepted this cast at all", this class can't tell those apart).</summary>
    public DateTime? LastStatusReceivedAt { get; private set; }

    /// <summary>How stale the most recent status report was by the time it got here —
    /// <c>DateTimeOffset.UtcNow - CastStatusMessage.SentAtUtc</c>, computed the moment it arrives.
    /// Null until the first report arrives (mirrors <see cref="LastStatusReceivedAt"/>). NOTE: this
    /// is a real latency estimate only if the Terminal's and this Caster's clocks are reasonably
    /// synchronized — see <c>DiscoveryProtocol.CastStatusMessage.SentAtUtc</c>'s own doc comment.
    /// This project has no way to verify clock sync between two machines from inside this sandbox,
    /// so a value here that looks implausible (negative, or much larger than the ~1s report
    /// interval) more likely reflects clock skew between the two machines than real network delay
    /// — displayed as-is rather than clamped, since hiding an implausible value would also hide the
    /// clock-skew signal itself.</summary>
    public TimeSpan? LastStatusLatencyEstimate { get; private set; }

    /// <summary>Real round-trip time to this session's Terminal, measured via
    /// <see cref="TerminalDiscoveryClient.PingAsync"/> (see <see cref="DiscoveryProtocol.PingMessage"/>'s
    /// own doc comment) roughly every 2 seconds while this session runs (<see cref="RunPingLoop"/>) —
    /// unlike <see cref="LastStatusLatencyEstimate"/>, this never touches the Terminal's clock at
    /// all, so it can't be confounded by clock skew between the two machines. Keeps its last
    /// successful measurement rather than reverting to null after one missed ping cycle (see
    /// <see cref="RunPingLoop"/>) — the same "don't be trigger-happy about a single lost UDP
    /// datagram" reasoning <see cref="IsTerminalAlive"/>'s own staleness window uses, just applied to
    /// a value instead of a boolean. Null until the first successful ping completes.</summary>
    public TimeSpan? RealRoundTripEstimate { get; private set; }

    /// <summary>UTC time of the last successful ping/pong round trip — null until the first one
    /// completes. Same role as <see cref="LastStatusReceivedAt"/>, for <see cref="RealRoundTripEstimate"/>:
    /// lets a caller judge how stale that estimate might be, since <see cref="RunPingLoop"/> keeps the
    /// last successful value around indefinitely rather than clearing it on a single missed cycle.</summary>
    public DateTime? LastRttMeasuredAt { get; private set; }

    // A status report is expected roughly every second (TerminalApplicationContext's timer) —
    // several missed in a row is a reasonable "the Terminal's gone quiet" signal without being
    // trigger-happy about one lost UDP datagram, same reasoning TerminalDiscoveryClient's own
    // beacon-expiry window uses.
    private static readonly TimeSpan StatusStaleAfter = TimeSpan.FromSeconds(5);

    /// <summary>True once at least one status report has arrived and it's recent — false either
    /// means "no confirmation has ever arrived" or "the Terminal stopped reporting", and this
    /// property deliberately doesn't distinguish the two: both mean the UI shouldn't claim the
    /// Terminal is receiving anything right now.</summary>
    public bool IsTerminalAlive => LastStatusReceivedAt is { } at && DateTime.UtcNow - at < StatusStaleAfter;

    // RunPingLoop's own attempt timeout is 3s and it retries every PingInterval (2s) on top of
    // that, so a single missed cycle can already take ~5s before the next attempt even starts —
    // this allows roughly two missed cycles (same "several in a row, not one" reasoning
    // StatusStaleAfter uses above) before treating RealRoundTripEstimate as possibly outdated.
    private static readonly TimeSpan RttStaleAfter = TimeSpan.FromSeconds(10);

    /// <summary>True once <see cref="RealRoundTripEstimate"/> exists but hasn't been refreshed
    /// recently — <see cref="LastRttMeasuredAt"/>'s own doc comment always said a caller could use
    /// it to judge staleness, but until now nothing did, so a Terminal that stopped responding to
    /// pings kept showing its last real RTT number forever with no indication it might no longer be
    /// current (the same gap <see cref="StatusStaleAfter"/>/<see cref="IsTerminalAlive"/> already
    /// closed for the status channel, just never mirrored here for the independent ping/pong one).
    /// False both when no measurement has ever succeeded (nothing to go stale — check
    /// <see cref="RealRoundTripEstimate"/> for that case) and while a recent one exists.</summary>
    public bool IsRttStale => LastRttMeasuredAt is { } at && DateTime.UtcNow - at >= RttStaleAfter;

    /// <summary>Raised from the background capture/encode loop, or from the encoder's own event
    /// loop — marshal to the UI thread before touching UI (mirrors every other self-test runner in
    /// this project).</summary>
    public event Action? StatsUpdated;

    /// <param name="outputIndex">Which monitor to capture — see
    /// <see cref="ScreenCaptureSource.EnumerateOutputs"/> for the DXGI adapter-output enumeration
    /// this index refers into. Defaults to 0 (typically the primary display) so existing callers
    /// that don't offer a picker keep the previous behavior unchanged.</param>
    public LiveCastSession(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, DiscoveredTerminal terminal, int outputIndex = 0)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _terminal = terminal;
        _outputIndex = outputIndex;
    }

    private void OnCastStatusReceived(DiscoveryProtocol.CastStatusMessage status)
    {
        if (status.DeviceId != _terminal.DeviceId) return; // a report from some other terminal — not ours to track.

        TerminalFramesDecoded = status.FramesDecoded;
        TerminalVideoBytesReceived = status.VideoBytesReceived;
        TerminalVideoError = status.VideoError;
        TerminalHasAudio = status.HasAudio;
        TerminalAudioBytesReceived = status.AudioBytesReceived;
        TerminalAudioError = status.AudioError;
        TerminalPayloadTypeMismatches = status.PayloadTypeMismatches;
        LastStatusReceivedAt = DateTime.UtcNow;
        LastStatusLatencyEstimate = DateTimeOffset.UtcNow - status.SentAtUtc;
        StatsUpdated?.Invoke();
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        LastError = null;
        AudioError = null;
        HasAudio = false;
        FramesCaptured = 0;
        AccessUnitsSent = 0;
        BytesSent = 0;
        AccessUnitsDroppedForBackpressure = 0;
        AudioBytesSent = 0;
        AudioChunksDroppedForBackpressure = 0;
        _queuedAccessUnitCount = 0;
        _queuedAudioChunkCount = 0;
        TerminalFramesDecoded = 0;
        TerminalVideoBytesReceived = 0;
        TerminalVideoError = null;
        TerminalHasAudio = false;
        TerminalAudioBytesReceived = 0;
        TerminalAudioError = null;
        TerminalPayloadTypeMismatches = 0;
        LastStatusReceivedAt = null;
        LastStatusLatencyEstimate = null;
        RealRoundTripEstimate = null;
        LastRttMeasuredAt = null;

        _discoveryClient.CastStatusReceived += OnCastStatusReceived;

        Guid mediaSessionId = Guid.NewGuid();

        try
        {
            _gpu = new D3D11Device();
            _capture = new ScreenCaptureSource(_gpu, _outputIndex);
            Width = _capture.Width;
            Height = _capture.Height;
            _converter = new BgraToNv12Converter(_gpu);
            _encoder = new H264HardwareEncoder(_gpu, Width, Height, frameRateNumerator: 30, bitrateBps: 4_000_000);
            _rtpSession = new RtpSession(new IPEndPoint(_terminal.Address, DiscoveryProtocol.VideoRtpPort), PayloadType,
                authentication: new MediaPacketAuthentication(_discoveryClient.DeriveMediaKey(_terminal, mediaSessionId, "video"), mediaSessionId));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StopInternal();
            StatsUpdated?.Invoke();
            return;
        }

        _encoder.AccessUnitEncoded += OnAccessUnitEncoded;
        _encoder.EncodingFailed += OnEncodingFailed;

        TerminalDiscoveryClient.AudioStreamInfo? audioInfo = null;
        try
        {
            _audioCapture = new AudioCaptureSource();
            // AAC as of this round (see this class's own doc comment) — AacAudioEncoder's own
            // construction failure (no AAC encoder MFT on this machine, an unsupported negotiated
            // input format) is caught by this same try/catch and degrades to video-only, same as an
            // AudioCaptureSource failure always has.
            _audioEncoder = new AacAudioEncoder(_audioCapture.SampleRate, _audioCapture.Channels);
            _audioRtpSession = new RtpSession(new IPEndPoint(_terminal.Address, DiscoveryProtocol.AudioRtpPort), AudioPayloadType,
                authentication: new MediaPacketAuthentication(_discoveryClient.DeriveMediaKey(_terminal, mediaSessionId, "audio"), mediaSessionId));
            _audioEncoder.AccessUnitEncoded += OnAacAccessUnitEncoded;
            _audioEncoder.EncodingFailed += OnAudioCaptureFailed; // same AudioError surface as a capture failure — see that handler.
            _audioCapture.PcmCaptured += OnPcmCaptured;
            _audioCapture.CaptureFailed += OnAudioCaptureFailed;
            HasAudio = true;
            audioInfo = new TerminalDiscoveryClient.AudioStreamInfo(_audioCapture.SampleRate, _audioCapture.Channels, AudioPayloadType, IsAac: true);
        }
        catch (Exception ex)
        {
            // Best-effort, per this class's doc comment — video-only casting is a normal outcome,
            // not a failure of Start() itself.
            AudioError = ex.Message;
            _audioCapture?.Dispose();
            _audioCapture = null;
            _audioEncoder?.Dispose();
            _audioEncoder = null;
            _audioRtpSession?.Dispose();
            _audioRtpSession = null;
        }

        // Do not emit media until the authenticated receiver confirms native initialization.
        // The session owns cancellation so closing during startup cannot resurrect capture.
        _cts = new CancellationTokenSource();
        CancellationToken startupToken = _cts.Token;
        await _discoveryClient.SendCastStartAsync(_terminal, _identity, Width, Height, PayloadType, audioInfo, mediaSessionId, startupToken);
        startupToken.ThrowIfCancellationRequested();

        _clock.Restart();
        _loopTask = Task.Run(() => RunLoop(_gpu, _capture, _converter, _encoder, _cts.Token));
        _sendLoopTask = Task.Run(() => RunSendLoop(_rtpSession, _cts.Token));
        _pingLoopTask = Task.Run(() => RunPingLoop(_cts.Token));

        if (HasAudio)
        {
            _audioSendLoopTask = Task.Run(() => RunAudioSendLoop(_audioRtpSession!, _cts.Token));
            _audioCapture!.Start();
        }
    }

    private void RunLoop(D3D11Device gpu, ScreenCaptureSource capture, BgraToNv12Converter converter, H264HardwareEncoder encoder, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                CapturedFrame? frame;
                try
                {
                    frame = capture.AcquireNextFrame(500);
                }
                catch (ScreenCaptureLostException ex)
                {
                    LastError = ex.Message;
                    break;
                }

                if (frame == null) continue; // normal timeout — screen hasn't changed.

                using (frame)
                {
                    if (!frame.HasNewImage) continue;

                    var nv12 = converter.Convert(frame.Texture, frame.Width, frame.Height);
                    // Same reasoning as EncodeSelfTestRunner: the converter reuses one output
                    // texture across calls, so it must be copied before handing off to the
                    // encoder's async input queue.
                    encoder.SubmitFrame(CopyTexture(gpu, nv12));
                    FramesCaptured++;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            // Whenever this loop stops running for any reason OTHER than an external Stop() call —
            // a ScreenCaptureLostException, or any other unexpected exception from the convert/encode
            // step above — RunSendLoop/RunAudioSendLoop/RunPingLoop previously kept running anyway,
            // idly waiting on empty channels/timers, since nothing but StopInternal() ever cancelled
            // _cts. That left three background loops (and the GPU/encoder resources StopInternal()
            // would otherwise release) alive and doing nothing useful until the operator noticed the
            // "投屏出错" state MainForm shows and manually clicked "停止投屏" — this cast IS actively
            // operated by a person watching this screen (unlike the Terminal, which PLANNING.md
            // requires to self-heal unattended), so requiring that click to fully tear down/restart is
            // still the right UX; this fix is only about not leaving three loops spinning uselessly in
            // the meantime. Calling Cancel() here when this loop is instead exiting because Stop()
            // itself already cancelled _cts is a harmless no-op — CancellationTokenSource.Cancel() is
            // idempotent, and see IsRunning's own doc comment for why a concurrent new Start() call
            // reassigning _cts to a different instance out from under this can't actually happen.
            //
            // Bug found (self-review) and fixed here: StopInternal()'s own cleanup calls
            // _loopTask?.Wait(TimeSpan.FromSeconds(2)) WITHOUT checking whether that wait actually
            // succeeded or merely timed out — if this exact finally block is unusually slow (e.g.
            // StatsUpdated's subscriber blocks), StopInternal can proceed past its 2-second wait,
            // Dispose() _cts, and null the field out from under this method while it's still running
            // concurrently. The null-conditional `_cts?.` above already handles "the field was set to
            // null before this read" safely (no call at all), but it does NOT protect against "the
            // field was still non-null when read here, but the CancellationTokenSource it pointed to
            // had already been disposed by the other thread between that read and this call" —
            // Cancel() on an already-disposed CancellationTokenSource throws ObjectDisposedException
            // rather than no-op like the ordinary idempotent-Cancel() case above. An extremely narrow
            // window (needs StopInternal's whole 2-second wait to actually elapse), but a real one,
            // and unlike a thrown exception literally everywhere else in this loop, nothing here was
            // catching it — it would propagate out of this finally block as an unobserved exception on
            // a fire-and-forget Task.Run, the exact failure shape this project's Terminal side has its
            // own TaskScheduler.UnobservedTaskException handler for for exactly this reason. Caught the
            // same way OperationCanceledException already is elsewhere in this class: a harmless
            // artifact of concurrent shutdown, not a real failure worth surfacing via LastError.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            StatsUpdated?.Invoke();
        }
    }

    private static ID3D11Texture2D CopyTexture(D3D11Device gpu, ID3D11Texture2D source)
    {
        var copy = gpu.Device.CreateTexture2D(source.Description);
        gpu.ImmediateContext.CopyResource(copy, source);
        return copy;
    }

    private void OnAccessUnitEncoded(byte[] accessUnit)
    {
        // Backpressure — the residual risk this project's README flagged for risk #25 ("Channel是
        // 无界的...没有背压或丢弃策略"): if RunSendLoop can't keep up with real network sends, drop
        // this entire access unit rather than let the queue grow without bound. Dropping is
        // deliberately all-or-nothing per access unit, decided BEFORE any of its NAL units are
        // enqueued — a bounded Channel's built-in DropOldest policy would instead drop individual
        // NAL units off the front of the queue, which could tear a still-partially-queued access
        // unit in half and hand the Terminal's CastReceiver an access unit it can never decode at
        // all (not even the frames after it, since H.264 slices generally depend on earlier slices
        // within the same access unit). Dropping whole access units means the decoder only ever
        // sees complete ones; H264HardwareEncoder's short-GOP, no-B-frames settings mean the next
        // IDR frame self-heals the resulting gap within about a second either way.
        if (Interlocked.CompareExchange(ref _queuedAccessUnitCount, 0, 0) >= MaxQueuedAccessUnits)
        {
            AccessUnitsDroppedForBackpressure++;
            StatsUpdated?.Invoke();
            return;
        }

        uint timestamp = RtpVideoClock.FromElapsed(_clock.Elapsed);
        var nalUnits = AnnexBNalSplitter.Split(accessUnit).ToList();
        Interlocked.Increment(ref _queuedAccessUnitCount); // decremented in RunSendLoop once sent.
        for (int i = 0; i < nalUnits.Count; i++)
        {
            bool isLastNalOfAccessUnit = i == nalUnits.Count - 1;
            // Non-blocking enqueue — this callback runs on H264HardwareEncoder's own background
            // event-loop thread (see its doc comment), which must not block on network I/O.
            // RunSendLoop is the single reader that actually calls SendNalUnitAsync, in enqueue
            // order — see the field doc comment on _sendQueue for why order matters here.
            _sendQueue.Writer.TryWrite((nalUnits[i], timestamp, isLastNalOfAccessUnit));
        }

        AccessUnitsSent++;
        BytesSent += accessUnit.Length;
        StatsUpdated?.Invoke();
    }

    private async Task RunSendLoop(RtpSession rtpSession, CancellationToken token)
    {
        try
        {
            await foreach (var (nal, timestamp, isLastNalOfAccessUnit) in _sendQueue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await rtpSession.SendNalUnitAsync(nal, timestamp, isLastNalOfAccessUnit);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    StatsUpdated?.Invoke();
                }
                finally
                {
                    // Decremented once per access unit (on its last NAL), regardless of whether the
                    // send above succeeded — the item is gone from the queue either way, and
                    // OnAccessUnitEncoded's backpressure check only cares about queue depth, not
                    // send outcomes (a send failure is tracked separately via LastError above).
                    if (isLastNalOfAccessUnit) Interlocked.Decrement(ref _queuedAccessUnitCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — Stop()/Dispose() cancels the token that ReadAllAsync is
            // observing.
        }
    }

    private void OnEncodingFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    private void OnPcmCaptured(byte[] pcm)
    {
        // Derived from the SAME _clock (Stopwatch) video's RtpVideoClock.FromElapsed(_clock.Elapsed)
        // uses, just at the audio's own sample rate instead of 90000 — this is what lets
        // CastReceiver convert both streams' RTP timestamps back to one shared "elapsed since cast
        // start" timeline and pace video against audio at all (see this project's README's A/V sync
        // section). This replaces an earlier draft that accumulated a sample counter from a random
        // RFC-3550-style initial value — sample-accurate within one stream, but with no relationship
        // to video's wall-clock timestamps whatsoever, which made it useless for cross-stream sync.
        // Computed once per captured buffer (a "batch") — AacAudioEncoder.SubmitPcm below can raise
        // OnAacAccessUnitEncoded zero or more times per call, each representing exactly
        // AacSamplesPerFrame samples, so every access unit within this batch gets this same
        // wall-clock anchor offset by however many samples came before it (see
        // OnAacAccessUnitEncoded) rather than each one independently reading _clock.Elapsed — the
        // latter would let a slow encode call mid-batch skew later access units' timestamps away
        // from where they actually belong on the shared audio-sample timeline.
        _aacBatchBaseTimestamp = RtpVideoClock.FromElapsed(_clock.Elapsed, (uint)_audioCapture!.SampleRate);
        _aacSamplesEmittedInBatch = 0;

        // Synchronous, on this same NAudio capture callback thread — see AacAudioEncoder's own doc
        // comment on having no background thread of its own. Raises OnAacAccessUnitEncoded below
        // (possibly more than once, possibly not at all) before returning.
        _audioEncoder!.SubmitPcm(pcm);
    }

    private void OnAacAccessUnitEncoded(byte[] accessUnit)
    {
        // Backpressure — same policy/reasoning as OnAccessUnitEncoded's, applied to audio: if
        // RunAudioSendLoop can't keep up, drop this entire access unit rather than let
        // _audioSendQueue grow without bound. Unlike H.264 NAL units, AAC access units have no
        // inter-unit dependency (each one decodes independently), so there's no "tearing" concern
        // here — dropping a whole access unit is simply the natural granularity, not a compromise.
        if (Interlocked.CompareExchange(ref _queuedAudioChunkCount, 0, 0) >= MaxQueuedAudioChunks)
        {
            AudioChunksDroppedForBackpressure++;
            StatsUpdated?.Invoke();
            return;
        }

        uint timestamp = _aacBatchBaseTimestamp + (uint)_aacSamplesEmittedInBatch;
        _aacSamplesEmittedInBatch += AacSamplesPerFrame;

        // Non-blocking enqueue — same reasoning as OnAccessUnitEncoded's _sendQueue.TryWrite: this
        // runs on NAudio's own capture callback thread (via OnPcmCaptured -> AacAudioEncoder.SubmitPcm
        // -> this method, all synchronous), which must not block on network I/O. No RTP fragmentation
        // needed here — see this class's own doc comment on why one AAC access unit safely fits a
        // single UDP payload the way a raw ~10ms PCM buffer used to threaten to.
        _audioSendQueue.Writer.TryWrite((accessUnit, timestamp));
        Interlocked.Increment(ref _queuedAudioChunkCount); // decremented in RunAudioSendLoop once sent.

        AudioBytesSent += accessUnit.Length;
        StatsUpdated?.Invoke();
    }

    private void OnAudioCaptureFailed(Exception ex)
    {
        // A bad buffer format or a capture-thread failure — audio degrades but video keeps going;
        // there's no retry/restart policy for audio specifically (matches this repo's existing
        // "flag it, don't invent an untested recovery policy" pattern, e.g. H264HardwareEncoder's
        // own backpressure gap).
        AudioError = ex.Message;
        StatsUpdated?.Invoke();
    }

    private async Task RunAudioSendLoop(RtpSession audioRtpSession, CancellationToken token)
    {
        try
        {
            await foreach (var (payload, timestamp) in _audioSendQueue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await audioRtpSession.SendRawPayloadAsync(payload, timestamp);
                }
                catch (Exception ex)
                {
                    AudioError = ex.Message;
                    StatsUpdated?.Invoke();
                }
                finally
                {
                    // Decremented once per dequeued access unit regardless of send outcome — same
                    // reasoning as RunSendLoop's identical decrement for video.
                    Interlocked.Decrement(ref _queuedAudioChunkCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — Stop()/Dispose() cancels the token that ReadAllAsync is
            // observing.
        }
    }

    /// <summary>Measures <see cref="RealRoundTripEstimate"/> roughly every <see cref="PingInterval"/>
    /// for as long as this session runs — see that property's own doc comment. Runs independently of
    /// <see cref="HasAudio"/> (unlike <see cref="RunAudioSendLoop"/>): RTT is a general network
    /// diagnostic, not something that depends on this session's own audio state.</summary>
    private async Task RunPingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var rtt = await _discoveryClient.PingAsync(_terminal, TimeSpan.FromSeconds(3));
                if (rtt.HasValue)
                {
                    // Only updated on success — a single missed ping keeps showing the last real
                    // measurement rather than flickering to "no data" (see RealRoundTripEstimate's
                    // own doc comment), the same "don't be trigger-happy about one lost UDP datagram"
                    // reasoning IsTerminalAlive's staleness window already uses elsewhere in this
                    // class.
                    RealRoundTripEstimate = rtt;
                    LastRttMeasuredAt = DateTime.UtcNow;
                    StatsUpdated?.Invoke();
                }
            }
            catch (Exception)
            {
                // Best-effort, like every other discovery-protocol interaction in this repo — a
                // transient failure here (e.g. the socket briefly unavailable) shouldn't kill this
                // loop or the cast itself; just try again next cycle.
            }

            try { await Task.Delay(PingInterval, token); }
            catch (OperationCanceledException) { }
        }
    }

    public void Stop()
    {
        bool wasRunning = IsRunning;
        StopInternal();
        // Only worth telling the Terminal if a stream had actually started — an aborted Start()
        // (e.g. no hardware encoder found) never sent cast_start in the first place.
        if (wasRunning) _ = _discoveryClient.SendCastStopAsync(_terminal, _identity);
    }

    private void StopInternal()
    {
        _discoveryClient.CastStatusReceived -= OnCastStatusReceived;

        _cts?.Cancel();
        _sendQueue.Writer.TryComplete();
        _audioSendQueue.Writer.TryComplete();

        _audioCapture?.Stop(); // explicit stop before Dispose(), rather than relying on WasapiCapture.Dispose()'s own teardown to also fully quiesce the callback.

        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        try { _sendLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        try { _audioSendLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        try { _pingLoopTask?.Wait(TimeSpan.FromSeconds(4)); } // PingAsync's own 3s timeout can be mid-flight.
        catch (AggregateException) { }
        _loopTask = null;
        _sendLoopTask = null;
        _audioSendLoopTask = null;
        _pingLoopTask = null;
        _cts?.Dispose();
        _cts = null;

        if (_encoder != null)
        {
            _encoder.AccessUnitEncoded -= OnAccessUnitEncoded;
            _encoder.EncodingFailed -= OnEncodingFailed;
        }
        _encoder?.Dispose();
        _encoder = null;
        _converter?.Dispose();
        _converter = null;
        _capture?.Dispose();
        _capture = null;
        _gpu?.Dispose();
        _gpu = null;
        _rtpSession?.Dispose();
        _rtpSession = null;

        if (_audioCapture != null)
        {
            _audioCapture.PcmCaptured -= OnPcmCaptured;
            _audioCapture.CaptureFailed -= OnAudioCaptureFailed;
        }
        _audioCapture?.Dispose();
        _audioCapture = null;
        if (_audioEncoder != null)
        {
            _audioEncoder.AccessUnitEncoded -= OnAacAccessUnitEncoded;
            _audioEncoder.EncodingFailed -= OnAudioCaptureFailed;
        }
        _audioEncoder?.Dispose();
        _audioEncoder = null;
        _audioRtpSession?.Dispose();
        _audioRtpSession = null;
    }

    public void Dispose() => StopInternal();
}
