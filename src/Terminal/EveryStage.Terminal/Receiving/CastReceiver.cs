using System.Diagnostics;
using EveryStage.Rendering.Audio;
using EveryStage.Rendering.Decode;
using EveryStage.Terminal.Display;
using EveryStage.Transport;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Terminal-side counterpart to Caster's <c>LiveCastSession</c>: receives the RTP/H.264 stream
/// (<c>RtpReceiver</c>, EveryStage.Transport), reassembles NAL units back into Annex-B access units
/// using the RTP marker bit (the inverse of <c>AnnexBNalSplitter</c>, which the Caster side used to
/// strip start codes before packetizing), decodes each with <see cref="H264HardwareDecoder"/>, and
/// presents the result through the shared <see cref="VideoSurface"/> bound to the overlay window's
/// video HWND — the same surface <c>ContentEngine.VideoContentController</c> uses for local file
/// playback, here fed from a live network stream instead of a file. Optionally also receives an
/// audio stream (<c>RawRtpReceiver</c>, EveryStage.Transport — raw 16-bit PCM, or, as of this round,
/// ADTS-framed AAC access units decoded through <see cref="AacAudioDecoder"/> first, per
/// <c>DiscoveryProtocol.CastStartMessage.AudioIsAac</c>; see <see cref="OnAudioPayloadReceived"/>)
/// and plays it through <see cref="AudioPlaybackClock"/> — the same WASAPI playback class
/// <c>VideoContentController</c> uses for local video files' audio track, and here it plays the same
/// master-clock role too:
/// decoded video frames are held on a background presentation thread and released only once
/// <see cref="AudioPlaybackClock.PositionTicks"/> reaches the frame's own presentation time, the same
/// "audio is the master clock" pattern <c>VideoContentController.RunPlaybackLoopCore</c> already uses
/// for local file playback (PLANNING.md §4.2). See <see cref="RunPresentLoop"/> for how the two
/// streams' RTP timestamps — independently generated on the Caster by two different capture
/// pipelines (Desktop Duplication for video, WASAPI loopback for audio) — get onto one shared
/// timeline in the first place, and for the accepted residual risks (capture-latency skew, 32-bit
/// RTP timestamp wraparound, first-audio-packet epoch fragility) this scheme does NOT solve.
///
/// Takes the <see cref="VideoSurface"/> from its caller rather than creating its own — see that
/// class's doc comment for why two independent D3D11 devices/swap chains bound to the same HWND was
/// a real bug, not a hypothetical one. This class still does not enforce mutual exclusion with
/// <c>VideoContentController</c> by itself: the caller (<c>Program.cs</c>'s
/// <c>TerminalApplicationContext</c>) is responsible for making sure local playback is stopped
/// (<c>PlaybackEngine.StopForDeviceCast</c>) before constructing a <see cref="CastReceiver"/>, and
/// for stopping this receiver before local playback resumes
/// (<c>PlaybackEngine.LocalPlaybackStarting</c>).
///
/// Decode calls happen synchronously on whatever thread invokes
/// <see cref="RtpReceiver.NalUnitReceived"/> — that event fires from `RtpReceiver`'s own single
/// sequential background receive loop (see its doc comment), so calls into this class from that side
/// are never concurrent with each other. The audio side runs on its own, entirely independent
/// <c>RawRtpReceiver</c> background loop, and a third thread (<see cref="RunPresentLoop"/>) does the
/// actual GPU presentation. The presentation thread snapshots the current audio clock and its
/// synchronized offset estimator together; a bounded queue transfers ownership of video frames.
/// All three also independently bump
/// <see cref="LastPacketReceivedAt"/>, which <c>TerminalApplicationContext.CheckCastLiveness</c>
/// polls to notice a Caster that's gone silent without ever sending
/// <c>DiscoveryProtocol.CastStopMessage</c> — UDP doesn't guarantee that message arrives, and a
/// crashed Caster never gets to send it at all.
/// </summary>
public sealed class CastReceiver : IDisposable
{
    public event Action<IReadOnlyList<ushort>>? VideoPacketsMissing;
    // Same reasoning/value as VideoContentController's identical constant: a fixed 30fps-equivalent
    // budget for "how far ahead of the audio clock to wait" / "how far behind before dropping".
    private static readonly long FrameBudgetTicks = TimeSpan.TicksPerSecond / 30;

    // Upper bound on how long RunPresentLoop will ever wait for the audio clock to "catch up" to a
    // single frame before giving up and presenting it anyway. This is a deliberate safety valve, not
    // a tuned latency target: if the epoch-alignment math in OnAudioPayloadReceived/OnNalUnitReceived
    // is ever wrong (e.g. the very first audio packet used to compute _audioSyncOffsetTicks was
    // itself delayed or reordered), the pacing comparison below could stay "true" forever and this
    // loop would otherwise wait indefinitely — freezing video completely. That failure mode is worse
    // than today's baseline (a frame presented at a somewhat wrong time is a sync glitch; a frame
    // that never presents is a frozen screen), so this bound guarantees the loop always eventually
    // makes progress regardless of whether the sync math holds.
    private static readonly long MaxWaitTicks = TimeSpan.TicksPerSecond; // 1 second.

    private readonly VideoSurface _surface;
    private H264HardwareDecoder? _decoder;
    private readonly RtpReceiver _rtpReceiver;
    private readonly AccessUnitAssembler _accessUnitAssembler = new();

    // Bug found and fixed here (self-review, same audit round that found the ObjectDisposedException
    // family — see this project's README): RtpReceiver.Dispose() cancels its token then waits up to
    // 2 seconds for its receive loop to actually stop, but does NOT guarantee the loop has stopped by
    // the time it returns — if the loop is, at that exact moment, synchronously inside dispatching
    // NalUnitReceived (i.e. inside OnNalUnitReceived below, which calls into _decoder.SubmitAccessUnit,
    // itself calling DrainOutput -> FrameDecoded -> OnFrameDecoded -> VideoSurface.PresentFrame),
    // RtpReceiver.Dispose() can return having merely timed out, not having actually waited for that
    // call to finish. Dispose() below used to proceed straight to _decoder.Dispose() immediately after
    // _rtpReceiver.Dispose() returns, regardless of which of those two outcomes actually happened —
    // H264HardwareDecoder has no internal synchronization of its own (SubmitAccessUnit/DrainOutput and
    // Dispose freely race), so that could mean disposing the decoder's native MFT while this exact
    // receive-loop thread is still inside a live SubmitAccessUnit call against it: a native-level
    // hazard (potential crash/corruption), not a catchable .NET exception — the same category of risk
    // as H264HardwareEncoder's documented (but unfixed, there, for lack of a verified non-blocking
    // GetEvent) GetEvent risk, except this one IS fixable, because unlike GetEvent, SubmitAccessUnit is
    // driven entirely by this codebase's own thread, not blocked inside an unverifiable native wait.
    // Fixed by having OnNalUnitReceived's call into _decoder and Dispose()'s call into _decoder share
    // this one lock: whichever gets there first now genuinely finishes before the other proceeds,
    // regardless of whether RtpReceiver.Dispose()'s 2-second wait actually completed or merely timed
    // out. This can make Dispose() block slightly longer than before if a decode call happens to still
    // be in flight, which is an acceptable, realistically-bounded cost (single-frame hardware decode
    // latency) in exchange for closing a real, if narrow, use-after-free-shaped race.
    private readonly object _decoderLock = new();
    private readonly object _programGate = new();
    private readonly object _audioLifetimeLock = new();
    private volatile bool _audioStopped;
    private VideoSurface? _programSurface;

    private readonly RawRtpReceiver? _audioRtpReceiver;
    private sealed class AudioSyncState
    {
        public AudioSyncState(AudioPlaybackClock clock) { Clock = clock; }
        public AudioPlaybackClock Clock { get; }
        public AvSyncOffsetEstimator Offset { get; } = new();
        public Action<string>? FaultHandler { get; set; }
    }
    private volatile AudioSyncState? _audioState;
    // Presentation may still hold a previous state; release old clocks only after that thread exits.
    private readonly List<AudioSyncState> _retiredAudioStates = new();
    private readonly Action<string>? _audioRecovered;
    private int _audioRecoveryPending;
    private int _audioRecoveryAttempted;
    private int _defaultAudioMigrationPending;

    // Channels the Caster sends. A Caster whose system audio is 5.1 sends 6; the clock is built stereo
    // and the PCM folded down (audit B-20), since a 6-channel shared-mode stream on a stereo output fails.
    private readonly int _sourceAudioChannels;

    // Audio stopped mid-cast (output device removed, or audio writes failing persistently): the cast
    // continues as video-only instead of being paced on a clock that no longer moves (audit A-24) or torn
    // down entirely (audit A-16).
    private int _audioOutputLost;
    private const int MaxConsecutiveAudioErrors = 90;

    // RTP timeline position of the audio packet being processed; read by EnqueueAudio, which runs
    // synchronously inside its processing (the AAC decoder is synchronous) on the audio receive thread.
    private long _pendingAudioTimelineTicks;

    // Non-null only when the Caster signaled AAC (DiscoveryProtocol.CastStartMessage.AudioIsAac) —
    // see OnAudioPayloadReceived for how this changes which path a received payload takes before
    // reaching _audioClock.
    private readonly AacAudioDecoder? _audioDecoder;

    // Timestamp unwrapping remains continuous across an audio-output replacement. The replacement
    // gets a fresh offset estimator and seeds it from its first decoded packet.
    private readonly uint _audioSampleRate;
    private readonly RtpTimestampUnwrapper _audioTimestampUnwrapper = new();
    private readonly RtpTimestampUnwrapper _videoTimestampUnwrapper = new();

    // Three queued frames plus the one currently presenting: latency and native ownership are bounded.
    private readonly BoundedLatestQueue<PendingFrame> _pendingFrames = new(3, frame => frame.Release());
    private CancellationTokenSource? _presentCts;
    private Thread? _presentThread;

    private readonly record struct PendingFrame(ID3D11Texture2D Texture, int ArraySlice, int Width, int Height, long PresentationTicks, IMFSample Sample)
    {
        public void Release()
        {
            Texture.Dispose();
            Sample.Dispose();
        }
    }

    public int Width { get; }
    public int Height { get; }
    private long _framesDecoded, _programSubmitted, _programSkipped, _previewSubmitted, _previewSkipped, _audioLateDropped;
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    internal CastFrameStatistics FrameStatistics => new(FramesDecoded,
        Interlocked.Read(ref _programSubmitted), Interlocked.Read(ref _programSkipped),
        Interlocked.Read(ref _previewSubmitted), Interlocked.Read(ref _previewSkipped),
        _pendingFrames.OverflowCount, Interlocked.Read(ref _audioLateDropped));
    public long BytesReceived { get; private set; }

    /// <summary>Access units dropped because the packet carrying their marker bit never
    /// arrived, detected by the RTP timestamp changing while NAL units were still pending.
    /// See OnNalUnitReceived for why the marker bit alone is not a sufficient access-unit
    /// boundary.</summary>
    public long IncompleteAccessUnitsDropped => _accessUnitAssembler.IncompleteAccessUnitsDropped;

    /// <summary>The most recent video decode attempt's error, or null if it succeeded — cleared back
    /// to null on the very next successful <see cref="H264HardwareDecoder.SubmitAccessUnit"/> call,
    /// NOT sticky for the receiver's whole lifetime the way an earlier version of this class left it
    /// (a single transient decode hiccup used to leave this permanently non-null, and therefore
    /// permanently reported as "投屏出错" in <c>CastStatusMessage</c>, even after decoding fully
    /// recovered). See <see cref="ConsecutiveVideoDecodeErrors"/> for the value that actually tracks
    /// an ongoing run of failures, which this single latest-error string cannot distinguish from "one
    /// isolated failure a while ago that happened to be the last one so far".</summary>
    public string? LastError { get; private set; }

    /// <summary>How many <see cref="H264HardwareDecoder.SubmitAccessUnit"/> calls have failed in a
    /// row, reset to 0 by the next success — see <see cref="TerminalApplicationContext.CheckDecodeHealth"/>
    /// (Program.cs) for the policy that watches this to decide when persistent decode failure should
    /// disconnect the cast entirely, the same way <c>CheckCastLiveness</c> already does for a Caster
    /// that's gone silent.</summary>
    private int _consecutiveVideoDecodeErrors;

    /// <summary>Written from two threads now that the decoder is an asynchronous MFT: the RTP
    /// receive thread (a submission that threw) and the decoder's own event-loop thread (a decode
    /// that failed, and a decoded frame that clears it). Interlocked rather than a plain property
    /// because Program.CheckDecodeHealth turns this into a disconnect decision - a lost update
    /// here is a Terminal that keeps showing a stream it can no longer decode.</summary>
    public int ConsecutiveVideoDecodeErrors => Volatile.Read(ref _consecutiveVideoDecodeErrors);

    /// <summary>True once <see cref="RunPresentLoop"/>'s background <see cref="Thread"/> has died from
    /// an unhandled exception (e.g. <see cref="VideoSurface.PresentFrame"/> failing on a lost/reset
    /// GPU device) — deliberately a one-way latch, never reset back to false for this instance's
    /// lifetime, unlike <see cref="ConsecutiveVideoDecodeErrors"/>/<see cref="LastError"/> just above.
    /// Those two get reset by the very next successful <see cref="OnNalUnitReceived"/> call, which
    /// keeps happening independently on the RTP receive path even after this present thread has
    /// already died — decoding can keep succeeding into a queue nothing is draining anymore. Reusing
    /// either of them for this signal would let a burst of otherwise-healthy incoming packets mask a
    /// dead present thread from <see cref="TerminalApplicationContext.CheckDecodeHealth"/> for as long
    /// as decoding kept succeeding, which defeats the point of that policy check entirely for exactly
    /// this failure. A raw <see cref="Thread"/> (unlike a <c>Task</c>) that lets an exception escape
    /// its entry point crashes the whole process, not just this one subsystem — <see cref="RunPresentLoop"/>
    /// now catches instead, and sets this so <c>CheckDecodeHealth</c> still gets a reliable, un-racy
    /// signal that this cast's video pipeline is permanently gone and the cast should be disconnected,
    /// even though decoding itself may keep reporting success.</summary>
    public bool PresentLoopFailed { get; private set; }

    /// <summary>The decoder's own terminal-failure flag, the decode-side counterpart of
    /// <see cref="PresentLoopFailed"/>. See H264HardwareDecoder.DecodeLoopFailed.</summary>
    public bool DecodeLoopFailed => _decoder?.DecodeLoopFailed ?? false;

    /// <summary>Combined video+audio "gap events / (received + gap events)" ratio as a percentage —
    /// see <see cref="RtpReceiver.GapEvents"/>'s own doc comment for why this is an honest
    /// approximation, not an exact packet-loss percentage (it under-counts multi-packet gaps as a
    /// single event, and can't distinguish real loss from this project's own lack of reordering
    /// support). Fed into <c>DeviceConnectionLogger.LogQualityMetric</c> (PLANNING.md §14.4's
    /// "连接质量指标（丢包率/延迟）", previously logged by nothing at all — see this project's
    /// README). Null only if zero packets (video or audio) have been received yet at all.</summary>
    public double? EstimatedPacketLossPercent
    {
        get
        {
            long packets = _rtpReceiver.PacketsReceived + (_audioRtpReceiver?.PacketsReceived ?? 0);
            long gaps = _rtpReceiver.GapEvents + (_audioRtpReceiver?.GapEvents ?? 0);
            if (packets == 0) return null;
            return gaps / (double)(packets + gaps) * 100.0;
        }
    }

    /// <summary>Combined video+audio count of <see cref="RtpReceiver.PayloadTypeMismatches"/> /
    /// <see cref="RawRtpReceiver.PayloadTypeMismatches"/> — see either property's own doc comment on
    /// why this is expected to stay 0 in this project's own traffic. Fed into
    /// <c>DiscoveryProtocol.CastStatusMessage.PayloadTypeMismatches</c> so the Caster side can see it
    /// too, not just this Terminal — previously this was purely a local counter with no consumer at
    /// all beyond the two underlying receivers themselves (see EveryStage.Transport's README).</summary>
    public long PayloadTypeMismatches => _rtpReceiver.PayloadTypeMismatches + (_audioRtpReceiver?.PayloadTypeMismatches ?? 0);
    public long VideoPacketsLost => _rtpReceiver.PacketsLost;

    private bool _hasAudioTransport;
    public bool HasAudio => _hasAudioTransport && !AudioOutputLost;
    public long AudioBytesReceived { get; private set; }

    /// <summary>Latest audio setup or playback error; output loss remains visible until recovery.</summary>
    public string? AudioError { get; private set; }
    public bool AudioMuted { get; set; }

    /// <summary>Same role as <see cref="ConsecutiveVideoDecodeErrors"/>, for the audio side — counts
    /// consecutive <see cref="OnAudioPayloadReceived"/> failures (currently, only
    /// <c>AudioPlaybackClock.Enqueue</c> throwing), reset to 0 by the next successful enqueue. Reaching
    /// <see cref="MaxConsecutiveAudioErrors"/> switches the cast to video-only (<see cref="AudioOutputLost"/>)
    /// rather than disconnecting it.</summary>
    public int ConsecutiveAudioPlaybackErrors { get; private set; }

    /// <summary>True while audio output is unavailable; video continues without audio pacing.
    /// A later default-device notification may recover it. <see cref="AudioError"/> says why.</summary>
    public bool AudioOutputLost => Volatile.Read(ref _audioOutputLost) != 0;

    /// <summary>Raised once when the cast switches to video-only mid-way, with the reason (for the
    /// connection log). May be raised from the RTP, present or UI thread.</summary>
    public event Action<string>? AudioDegraded;

    /// <summary>UTC time of the most recent video OR audio packet actually received — initialized
    /// to construction time (not <c>DateTime.MinValue</c>) so a brand-new receiver gets a grace
    /// period before <c>TerminalApplicationContext.CheckCastLiveness</c> can consider it stale;
    /// otherwise the very first liveness check (which can run before the Caster's first RTP packet
    /// has even arrived) would immediately look like a timeout.</summary>
    public DateTime LastPacketReceivedAt { get; private set; } = DateTime.UtcNow;

    public CastReceiver(VideoSurface surface, int width, int height, int listenPort,
        bool hasAudio = false, int audioSampleRate = 0, int audioChannels = 0, int audioListenPort = 0,
        bool audioIsAac = false, byte? payloadType = null, byte? audioPayloadType = null,
        Guid mediaSessionId = default, byte[]? videoKey = null, byte[]? audioKey = null, System.Net.IPAddress? expectedAddress = null,
        Func<int, int, AudioPlaybackClock>? audioOutputFactory = null, Action<string>? audioRecovered = null)
    {
        if (mediaSessionId == Guid.Empty || videoKey == null || audioKey == null || expectedAddress == null)
            throw new ArgumentException("Authenticated media session is required.");
        Width = width;
        Height = height;

        _surface = surface;
        _audioRecovered = audioRecovered;

        // Bug fixed here: same "step one succeeds and gets kept, step two throws, nothing disposes
        // step one" shape as EveryStage.Rendering's D3D11Device/SwapChainPresenter/
        // AudioPlaybackClock and Caster's BgraToNv12Converter constructor fixes (see those
        // libraries' READMEs) — new RtpReceiver(listenPort, ...) binds a UDP socket to a fixed,
        // well-known port (DiscoveryProtocol.VideoRtpPort), which can genuinely throw
        // SocketException if a previous CastReceiver's socket hasn't finished releasing it yet (a
        // rapid stop/restart of casting, or this Terminal recovering from an earlier crash) — a
        // real, reachable failure mode, not hypothetical. Without this try/catch, that failure
        // would leak the H264HardwareDecoder just constructed above: this constructor never
        // finishes, so no CastReceiver instance ever exists for Program.OnCastStartRequested to
        // later Dispose(), and the decoder's own MFStartup() reference count/GPU decoder MFT would
        // never be released — every one of this session's earlier fixes protecting
        // H264HardwareDecoder's OWN constructor doesn't help here, since the decoder here already
        // finished constructing successfully; the leak this time is one level up, in whichever
        // sibling construction step runs after it.
        // payloadType/audioPayloadType (see EveryStage.Transport's README) come from
        // DiscoveryProtocol.CastStartMessage.PayloadType/AudioPayloadType — this is that
        // field's first real consumer; previously it was received and stored in
        // DiscoveryService.CastStartInfo but never passed any further.
        _rtpReceiver = new RtpReceiver(listenPort, payloadType,
            new MediaPacketAuthentication(videoKey, mediaSessionId), expectedAddress, RtpSession.DefaultFecPayloadType);
        _rtpReceiver.NalUnitReceived += OnNalUnitReceived;
        _rtpReceiver.PacketGapDetected += missing => VideoPacketsMissing?.Invoke(missing);

        if (hasAudio)
        {
            try
            {
                if (audioSampleRate <= 0 || audioChannels <= 0)
                    throw new ArgumentException("无效的来投音频采样率或声道数");
                // Constructed here (not lazily on first packet) so a WASAPI failure is caught right
                // away rather than surfacing later as an unhandled exception from inside
                // OnAudioPayloadReceived on the RTP receive thread.
                _sourceAudioChannels = audioChannels;
                _audioSampleRate = (uint)audioSampleRate;
                try
                {
                    var clock = audioOutputFactory?.Invoke(audioSampleRate, PcmDownmix.OutputChannels(audioChannels))
                        ?? new AudioPlaybackClock(audioSampleRate, PcmDownmix.OutputChannels(audioChannels));
                    _audioState = CreateAudioState(clock);
                }
                catch (Exception ex)
                {
                    AudioError = $"无法打开音频输出（{ex.Message}），投屏继续无声显示";
                    Volatile.Write(ref _audioOutputLost, 1);
                }
                if (audioIsAac)
                {
                    // Same "catch it here, not later on the RTP receive thread" reasoning as
                    // _audioClock above — an AAC decoder MFT construction failure (no such MFT on
                    // this machine, an unsupported negotiated format) is caught by this same
                    // try/catch and degrades the whole cast to video-only, exactly like an
                    // AudioPlaybackClock/WASAPI failure always has.
                    _audioDecoder = new AacAudioDecoder(audioSampleRate, audioChannels);
                    _audioDecoder.PcmDecoded += OnAacPcmDecoded;
                    _audioDecoder.DecodingFailed += OnAacDecodingFailed;
                }
                _audioRtpReceiver = new RawRtpReceiver(audioListenPort, audioPayloadType,
                    new MediaPacketAuthentication(audioKey, mediaSessionId), expectedAddress);
                _audioRtpReceiver.PayloadReceived += OnAudioPayloadReceived;
                _hasAudioTransport = true;
            }
            catch (Exception ex)
            {
                // Video-only is a normal, expected outcome here — a WASAPI output failure on the
                // Terminal shouldn't take down a cast whose video side is working fine.
                AudioError = ex.Message;
                if (_audioState is { } failedState) DisposeAudioState(failedState);
                _audioState = null;
                if (_audioDecoder != null)
                {
                    _audioDecoder.PcmDecoded -= OnAacPcmDecoded;
                    _audioDecoder.DecodingFailed -= OnAacDecodingFailed;
                }
                _audioDecoder?.Dispose();
                _audioDecoder = null;
                _audioRtpReceiver?.Dispose();
                _audioRtpReceiver = null;
                _hasAudioTransport = false;
            }
        }
    }

    public void Start()
    {
        // Keep construction independent from the decoder MFT. This lets the controller establish
        // the shared-surface ownership contract before the first frame, and makes the no-frame
        // Attach/Detach smoke test exercise a real CastReceiver even on machines without an H.264
        // hardware transform. A real cast still initializes the decoder before opening RTP loops;
        // any MFT failure propagates through the existing Connect cleanup path.
        lock (_decoderLock)
        {
            if (_decoder == null)
            {
                _decoder = new H264HardwareDecoder(_surface.Gpu, Width, Height);
                _decoder.FrameDecoded += OnFrameDecoded;
                _decoder.DecodingFailed += OnVideoDecodingFailed;
            }
        }
        _rtpReceiver.Start();
        if (_hasAudioTransport)
        {
            _audioState?.Clock.Start();
            _audioRtpReceiver!.Start();

            var cts = new CancellationTokenSource();
            _presentCts = cts;
            _presentThread = new Thread(() => RunPresentLoop(cts.Token))
            {
                IsBackground = true,
                Name = "EveryStage.CastPresent",
            };
            _presentThread.Start();
        }
    }

    public void AttachProgram(VideoSurface surface)
    {
        lock (_programGate)
        {
            _programSurface = surface;
        }
    }

    public void DetachProgram()
    {
        lock (_programGate)
        {
            _programSurface = null;
        }
    }

    /// <summary>Defer switching until the next audio packet so the new clock can be synchronized.</summary>
    public void RequestDefaultAudioMigration()
    {
        lock (_audioLifetimeLock)
        {
            if (_audioStopped || !_hasAudioTransport) return;
            if (AudioOutputLost && _audioState == null)
            {
                Volatile.Write(ref _audioRecoveryPending, 1);
                return;
            }
            if (_audioState == null) return;
            if (AudioOutputLost && _audioState.Clock.IsFaulted)
                Volatile.Write(ref _audioRecoveryPending, 1);
            else if (!AudioOutputLost && _audioState.Clock.SelectedDeviceId == null)
                Volatile.Write(ref _defaultAudioMigrationPending, 1);
        }
    }

    private void OnAudioPayloadReceived(byte[] payload, uint timestamp)
    {
        // Unsubscribing does not cancel a delegate invocation already captured by the receiver.
        // Keep native audio use and teardown mutually exclusive, including after its stop timeout.
        lock (_audioLifetimeLock)
        {
            if (_audioStopped) return;
            ProcessAudioPayload(payload, timestamp);
        }
    }

    private void ProcessAudioPayload(byte[] payload, uint timestamp)
    {
        LastPacketReceivedAt = DateTime.UtcNow;
        AudioBytesReceived += payload.Length;

        // Establishes the mapping between "audio RTP timestamp" and "AudioPlaybackClock.PositionTicks"
        // from the first decoded audio packet for each output clock. RtpVideoClock.ToElapsedTicks
        // converts that packet's RTP timestamp back into "ticks elapsed since the Caster's cast-session
        // clock started" — call it C. At the moment this packet is handed to _audioClock.Enqueue below,
        // AudioPlaybackClock.PositionTicks is ~0 (nothing has played yet). So from here on,
        // (a video frame's own elapsed-since-cast-start time) - _audioSyncOffsetTicks should equal the
        // AudioPlaybackClock.PositionTicks at which that frame's audio counterpart is playing — see
        // RunPresentLoop. This is inherently approximate: it assumes this first audio packet reaches
        // the Terminal and gets enqueued with negligible delay relative to when it was captured, which
        // holds well enough on a LAN but is not something this scheme measures or corrects for. Same
        // timestamp convention whether payload is raw PCM or (as of this round) an AAC access unit —
        // LiveCastSession derives both the same way (elapsed wall-clock time × sample rate), just with
        // AAC's timestamps landing on AacSamplesPerFrame-sized boundaries instead of arbitrary ones.
        long audioTimelineTicks = RtpVideoClock.ToElapsedTicks(
            _audioTimestampUnwrapper.Unwrap(timestamp), _audioSampleRate);
        if (Interlocked.Exchange(ref _defaultAudioMigrationPending, 0) != 0 && !AudioOutputLost)
            TryReplaceAudioOnDefault(recovering: false);
        if (AudioOutputLost) TryRecoverAudio();
        if (AudioOutputLost) return;
        var audio = _audioState!;
        _ = audio.Clock.PositionTicks;
        if (audio.Clock.IsFaulted)
        {
            // Reading the position is what notices a stopped output (the Faulted event can arrive late).
            OnAudioClockFaulted(audio, audio.Clock.FaultReason ?? "音频输出设备已停止");
            return;
        }
        // The offset is sampled when this packet's audio actually reaches the playback buffer (EnqueueAudio),
        // not here: an AAC decoder outputs nothing for its first access units, and seeding the estimator
        // from a packet that produced no audio started every cast with a skewed offset (audit B-8).
        _pendingAudioTimelineTicks = audioTimelineTicks;

        if (_audioDecoder != null)
        {
            // AacAudioDecoder.SubmitAccessUnit never throws — it reports success/failure via
            // PcmDecoded/DecodingFailed instead (see that class's own doc comment), which
            // OnAacPcmDecoded/OnAacDecodingFailed below handle. AudioError/ConsecutiveAudioPlaybackErrors
            // are updated there, not here, so this call is deliberately not wrapped in a try/catch —
            // doing so and then unconditionally resetting AudioError afterward would incorrectly wipe
            // out a failure OnAacDecodingFailed had just synchronously recorded during this very call.
            _audioDecoder.SubmitAccessUnit(payload);
            return;
        }

        // No jitter buffer, no reordering/loss recovery — enqueued straight into WASAPI playback as
        // it arrives. BufferedWaveProvider (inside AudioPlaybackClock) discards on overflow rather
        // than blocking, so a burst of late packets thins itself out instead of building unbounded
        // latency; a dropped/reordered RTP packet here just becomes an audible gap/glitch, the same
        // "no loss recovery" limitation the video side already has and documents.
        EnqueueAudio(payload);
    }

    private void OnAacPcmDecoded(byte[] pcm)
    {
        // Raised synchronously from within _audioDecoder.SubmitAccessUnit, called above from
        // OnAudioPayloadReceived — same calling-thread context (the audio RTP receive loop) the
        // raw-PCM path's direct Enqueue already runs on.
        EnqueueAudio(pcm);
    }

    private void EnqueueAudio(byte[] pcm)
    {
        if (AudioOutputLost) return;
        try
        {
            var audio = _audioState!;
            audio.Offset.Update(_pendingAudioTimelineTicks, audio.Clock.PositionTicks + audio.Clock.BufferedDurationTicks);
            byte[] output = PcmDownmix.ToStereo(pcm, _sourceAudioChannels);
            if (AudioMuted) Array.Clear(output);
            audio.Clock.Enqueue(output);
            AudioError = null;
            ConsecutiveAudioPlaybackErrors = 0;
        }
        catch (Exception ex)
        {
            // Previously unhandled here entirely — a throw from Enqueue would have propagated out
            // of this event handler, up through RawRtpReceiver's ReceiveLoopAsync, and silently
            // killed that background Task with no error surfaced anywhere (an unobserved task
            // exception, not a crash). Caught here the same way OnNalUnitReceived already catches
            // decode failures, so it becomes a reportable AudioError instead of a silently dead
            // audio receive loop.
            AudioError = ex.Message;
            CountAudioError(ex.Message);
        }
    }

    private void CountAudioError(string message)
    {
        if (++ConsecutiveAudioPlaybackErrors >= MaxConsecutiveAudioErrors)
            DegradeAudio($"音频持续出错（{message}），投屏继续无声显示");
    }

    /// <summary>Gives up audio for the rest of this cast and keeps the video going unpaced. Used to be
    /// either never noticed (a stopped output reads position 0 and throws nothing — the picture dropped to
    /// about one frame per second, audit A-24) or a disconnect of the whole cast after ~90 audio errors
    /// (audit A-16), contradicting the "audio is best effort" rule applied at setup.</summary>
    private void DegradeAudio(string reason)
    {
        if (Interlocked.CompareExchange(ref _audioOutputLost, 1, 0) != 0) return;
        AudioError = reason;
        try { AudioDegraded?.Invoke(reason); } catch (Exception) { }
    }

    private AudioSyncState CreateAudioState(AudioPlaybackClock clock)
    {
        var state = new AudioSyncState(clock);
        state.FaultHandler = reason => OnAudioClockFaulted(state, reason);
        clock.Faulted += state.FaultHandler;
        return state;
    }

    private static void DisposeAudioState(AudioSyncState state)
    {
        if (state.FaultHandler != null) state.Clock.Faulted -= state.FaultHandler;
        state.Clock.Dispose();
    }

    private void OnAudioClockFaulted(AudioSyncState state, string reason)
    {
        if (_audioStopped || !ReferenceEquals(state, _audioState)) return;
        lock (_audioLifetimeLock)
        {
            if (_audioStopped || !ReferenceEquals(state, _audioState)) return;
            if (state.Clock.SelectedDeviceId != null && Interlocked.CompareExchange(ref _audioRecoveryAttempted, 1, 0) == 0)
                Volatile.Write(ref _audioRecoveryPending, 1);
            DegradeAudio($"声音输出已断开（{reason}），投屏继续无声显示");
        }
    }

    // Called only under _audioLifetimeLock by the RTP audio thread. Do not seek live input: use the
    // first newly received decoded packet to seed a new RTP-to-audio-clock mapping instead.
    private void TryRecoverAudio()
    {
        if (Interlocked.Exchange(ref _audioRecoveryPending, 0) == 0 || _audioStopped) return;
        TryReplaceAudioOnDefault(recovering: true);
    }

    private void TryReplaceAudioOnDefault(bool recovering)
    {
        AudioSyncState? replacement = null;
        try
        {
            replacement = CreateAudioState(new AudioPlaybackClock((int)_audioSampleRate, PcmDownmix.OutputChannels(_sourceAudioChannels)));
            replacement.Clock.Start();
            if (_audioState is { } old)
            {
                if (old.FaultHandler != null) old.Clock.Faulted -= old.FaultHandler;
                _retiredAudioStates.Add(old);
            }
            _audioState = replacement;
            replacement = null;
            AudioError = null;
            ConsecutiveAudioPlaybackErrors = 0;
            if (recovering)
            {
                Volatile.Write(ref _audioOutputLost, 0);
                try { _audioRecovered?.Invoke("设备来投的音频输出已切换到系统默认设备。"); } catch (Exception) { }
            }
        }
        catch (Exception) { /* Continue presenting video without audio. */ }
        finally
        {
            if (replacement != null)
                try { DisposeAudioState(replacement); } catch (Exception) { }
        }
    }

    private void ReleaseRetiredAudioStates()
    {
        List<AudioSyncState> retired;
        lock (_audioLifetimeLock)
        {
            if (_retiredAudioStates.Count == 0) return;
            retired = new List<AudioSyncState>(_retiredAudioStates);
            _retiredAudioStates.Clear();
        }
        foreach (var state in retired)
            try { DisposeAudioState(state); } catch (Exception) { }
    }

    /// <summary>The H.264 decoder now runs an asynchronous MFT event loop, so a decode failure
    /// surfaces on that loop's own thread with no caller to throw at - same shape as the audio
    /// side's OnAacDecodingFailed below. Recorded into the same two fields OnNalUnitReceived
    /// used to set directly from its own catch block.</summary>
    private void OnVideoDecodingFailed(Exception ex)
    {
        LastError = ex.Message;
        Interlocked.Increment(ref _consecutiveVideoDecodeErrors);
    }

    private void OnAacDecodingFailed(Exception ex)
    {
        AudioError = ex.Message;
        CountAudioError(ex.Message);
    }

    private void OnNalUnitReceived(byte[] nalUnit, bool isLastNalOfAccessUnit, uint timestamp)
    {
        LastPacketReceivedAt = DateTime.UtcNow;
        BytesReceived += nalUnit.Length;

        // Access-unit assembly lives in EveryStage.Transport.AccessUnitAssembler rather than
        // here so that the core self-tests can cover it: they run on any .NET runtime, and this
        // class cannot compile into them because it pulls in the D3D11/Media Foundation decoder.
        // See that class for why the marker bit alone is not a sufficient access-unit boundary.
        byte[]? accessUnit = _accessUnitAssembler.Add(nalUnit, isLastNalOfAccessUnit, timestamp);
        if (accessUnit == null) return;

        try
        {
            // Converted back to 100ns ticks on the "elapsed since cast session start" timeline shared
            // with the audio stream (see RtpVideoClock's doc comment and OnAudioPayloadReceived
            // above) — this is the value H264HardwareDecoder's FIFO carries through to OnFrameDecoded
            // as presentationTicks, and what RunPresentLoop paces against the audio clock.
            long presentationTicks = RtpVideoClock.ToElapsedTicks(
                _videoTimestampUnwrapper.Unwrap(timestamp), RtpVideoClock.ClockRate);
            lock (_decoderLock)
            {
                _decoder?.SubmitAccessUnit(accessUnit, presentationTicks);
            }
            // NOT cleared here any more. On the asynchronous decoder path SubmitAccessUnit only
            // enqueues, so reaching this line proves the access unit was accepted for decoding -
            // not that anything decoded. Clearing here meant every arriving access unit wiped the
            // failure count, and since a stream that cannot be decoded still delivers packets, the
            // count could never reach Program.MaxConsecutiveDecodeErrorsBeforeDisconnect: the
            // Terminal would sit on a frozen picture forever instead of dropping the cast. The
            // reset lives in OnFrameDecoded now, where a frame coming out is real proof of health.
        }
        catch (Exception ex)
        {
            // Submission itself failing is still a genuine failure, and still this thread's to count.
            LastError = ex.Message;
            Interlocked.Increment(ref _consecutiveVideoDecodeErrors);
        }
    }

    private void OnFrameDecoded(ID3D11Texture2D texture, int arraySlice, int width, int height, long presentationTicks, IMFSample sample)
    {
        // A frame coming out of the decoder is the only real evidence the video pipeline works,
        // so this is where the failure count is cleared. Same thread as OnVideoDecodingFailed
        // (the decoder's event loop), so clear-vs-increment cannot interleave between them.
        LastError = null;
        Interlocked.Exchange(ref _consecutiveVideoDecodeErrors, 0);

        // The decoder surface is macroblock-aligned (1920x1088 for a 1920x1080 desktop): presenting all of
        // it put the padding rows on the big screen and squeezed the picture (audit C-34). The Caster
        // encodes at the size it announced in the cast start, so that is the picture.
        if (Width > 0) width = Math.Min(width, Width);
        if (Height > 0) height = Math.Min(height, Height);
        var frame = new PendingFrame(texture, arraySlice, width, height, presentationTicks, sample);
        Interlocked.Increment(ref _framesDecoded);

        if (!_hasAudioTransport)
        {
            // No audio clock to pace against — present immediately, same as before this round.
            Present(frame);
            return;
        }

        // Ownership of the texture's COM reference (and of the sample reserving its decoder slot) passes
        // into the queue here — RunPresentLoop (or Dispose, on teardown) releases it exactly once.
        _pendingFrames.Enqueue(frame);
    }

    /// <summary>Entry point for <see cref="_presentThread"/> — a raw <see cref="Thread"/>, not a
    /// <c>Task</c>, which matters here specifically: an exception escaping a <see cref="Thread"/>'s
    /// entry point crashes the entire process (unlike an unobserved <c>Task</c> fault, which only
    /// silently kills that one background operation). <see cref="RunPresentLoopCore"/> does the
    /// actual work; this wrapper's only job is making sure a failure there degrades to "this cast's
    /// video pipeline is broken" instead of "the whole Terminal just crashed" — see
    /// <see cref="PresentLoopFailed"/>'s own doc comment for why that flag exists instead of reusing
    /// <see cref="LastError"/>/<see cref="ConsecutiveVideoDecodeErrors"/>. The leftover-frame drain
    /// moved here (into a <c>finally</c>) rather than staying at the end of the core loop's body: the
    /// core loop's own early <c>return</c> on mid-wait cancellation used to skip straight past that
    /// drain, leaking every frame still sitting in <see cref="_pendingFrames"/> at the time — a
    /// <c>finally</c> here runs no matter which of "loop condition went false", "cancelled mid-wait",
    /// or "threw" ended <see cref="RunPresentLoopCore"/>.</summary>
    private void RunPresentLoop(CancellationToken token)
    {
        try
        {
            RunPresentLoopCore(token);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            PresentLoopFailed = true;
        }
        finally
        {
            // Drain and dispose whatever's left so cancellation, an early return, or the catch above
            // doesn't leak GPU texture references still sitting in the queue.
            _pendingFrames.Dispose();
        }
    }

    private void RunPresentLoopCore(CancellationToken token)
    {
        var waitStopwatch = new Stopwatch();

        while (!token.IsCancellationRequested)
        {
            // No old clock snapshot is in use between iterations; reclaim replaced outputs here.
            ReleaseRetiredAudioStates();
            if (!_pendingFrames.WaitDequeue(token, out var frame)) return;

            // This loop only runs for casts WITH audio (video-only casts present straight from
            // OnFrameDecoded and never queue). Within such a cast, the estimator is unavailable only
            // in the short window before the first decoded audio reaches the playback buffer: frames
            // decoded then are presented immediately rather than dropped or held, so video shows right
            // away, and pacing begins once the offset exists (audit C-14).
            var audio = _audioState;
            if (AudioOutputLost || audio == null || !audio.Offset.TryGetOffset(out long offset))
            {
                // Also once audio has been given up (DegradeAudio): nothing left to pace against.
                Present(frame, token);
                continue;
            }

            long targetAudioPositionTicks = frame.PresentationTicks - offset;

            waitStopwatch.Restart();
            while (!token.IsCancellationRequested
                   && !AudioOutputLost && ReferenceEquals(audio, _audioState)
                   && audio.Clock.PositionTicks < targetAudioPositionTicks - FrameBudgetTicks
                   && waitStopwatch.Elapsed.Ticks < MaxWaitTicks)
            {
                if (audio.Clock.IsFaulted) OnAudioClockFaulted(audio, audio.Clock.FaultReason ?? "音频输出设备已停止");
                // Recheck the audio clock regularly while allowing stop/disconnect to wake immediately.
                if (token.WaitHandle.WaitOne(5)) break;
            }

            if (token.IsCancellationRequested)
            {
                frame.Release();
                return;
            }

            long behindByTicks = AudioOutputLost || !ReferenceEquals(audio, _audioState)
                ? 0 : audio.Clock.PositionTicks - targetAudioPositionTicks;
            if (behindByTicks > FrameBudgetTicks)
            {
                // Fell far enough behind the audio clock that this frame is stale — drop it rather
                // than present outdated video, same policy VideoContentController already applies.
                Interlocked.Increment(ref _audioLateDropped);
                frame.Release();
                continue;
            }

            Present(frame, token);
        }
    }

    private void Present(PendingFrame frame, CancellationToken token = default)
    {
        try
        {
            lock (_programGate)
            {
                // Keep a hidden or busy preview from delaying the on-air display.
                if (_programSurface != null)
                {
                    bool submitted = _programSurface.PresentFrame(frame.Texture, frame.ArraySlice, frame.Width, frame.Height, vsync: true, token);
                    if (submitted) Interlocked.Increment(ref _programSubmitted);
                    else if (!token.IsCancellationRequested) Interlocked.Increment(ref _programSkipped);
                }
                bool previewSubmitted = _surface.PresentFrame(frame.Texture, frame.ArraySlice, frame.Width, frame.Height, vsync: true, token,
                    waitMilliseconds: _programSurface == null ? 250 : 0);
                if (previewSubmitted) Interlocked.Increment(ref _previewSubmitted);
                else if (!token.IsCancellationRequested) Interlocked.Increment(ref _previewSkipped);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            PresentLoopFailed = true;
        }
        finally
        {
            frame.Release();
        }
    }

    public void Dispose()
    {
        // Does NOT dispose _surface — shared with VideoContentController, owned by
        // TerminalApplicationContext (see VideoSurface's doc comment).
        _rtpReceiver.Dispose();

        // Consumer before producer (audit A-17): the present thread draws textures that belong to the
        // decoder's DXVA allocator, so it must be stopped before the decoder (and with it the allocator
        // and an MFStartup reference) goes away. Frames the decoder still delivers meanwhile are released
        // by the already-disposed queue.
        _presentCts?.Cancel();
        _presentThread?.Join();
        _presentCts?.Dispose();

        lock (_decoderLock)
        {
            if (_decoder != null)
            {
                _decoder.FrameDecoded -= OnFrameDecoded;
                _decoder.DecodingFailed -= OnVideoDecodingFailed;
                _decoder.Dispose();
                _decoder = null;
            }
        }

        if (_audioRtpReceiver != null) _audioRtpReceiver.PayloadReceived -= OnAudioPayloadReceived;
        _audioRtpReceiver?.Dispose();
        List<AudioSyncState> statesToDispose;
        lock (_audioLifetimeLock)
        {
            _audioStopped = true;
            if (_audioDecoder != null)
            {
                _audioDecoder.PcmDecoded -= OnAacPcmDecoded;
                _audioDecoder.DecodingFailed -= OnAacDecodingFailed;
            }
            _audioDecoder?.Dispose();
            statesToDispose = new List<AudioSyncState>(_retiredAudioStates);
            if (_audioState is { } current) statesToDispose.Add(current);
            _audioState = null;
            _retiredAudioStates.Clear();
        }
        // A captured Faulted callback can be waiting on _audioLifetimeLock. Never join a WASAPI
        // worker while holding that lock, or teardown and the callback can deadlock.
        foreach (var state in statesToDispose) DisposeAudioState(state);

        // Anything still queued after the present thread has stopped (e.g. HasAudio was false and
        // frames were never routed through the queue at all — this is then always empty and the
        // loop is a no-op, but it's cheap insurance either way).
        _pendingFrames.Dispose();
    }
}
