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
/// -> a second <see cref="RtpSession"/> on <see cref="DiscoveryProtocol.AudioRtpPort"/>.
/// <see cref="EncodeSelfTestRunner"/> already exercises the video chain up through the encoder in
/// isolation; this class carries it the rest of the way onto the network, adds the audio side
/// (which has no equivalent self-test — see this project's README), and signals the Terminal
/// out-of-band (<see cref="DiscoveryProtocol.CastStartMessage"/> /
/// <see cref="DiscoveryProtocol.CastStopMessage"/>) so it knows a stream is starting/stopping and
/// what resolution/audio format to expect, instead of the Terminal having to infer that purely from
/// RTP packets arriving on a fixed port.
///
/// Audio is best-effort on top of video, not a co-equal requirement: if
/// <see cref="AudioCaptureSource"/> fails to construct (no active audio session, a WASAPI error),
/// this class still casts video-only rather than failing the whole session — PLANNING.md's screen
/// capture is the core feature, and a machine with nothing currently producing audio is a normal
/// state, not an error.
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

    // Keeps each audio RTP packet's total IP-packet size safely under the ~1500-byte Ethernet MTU
    // once RTP/UDP/IP headers are added — same conservative-budget reasoning as RtpSession's own
    // default maxPayloadSize for video, picked independently here because WASAPI loopback capture
    // buffers (commonly ~10ms, e.g. ~1920 bytes at 48kHz stereo 16-bit) can otherwise exceed it.
    private const int MaxAudioPayloadBytes = 1280;

    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly DiscoveredTerminal _terminal;
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

    // Same ordering reasoning as _sendQueue, applied to audio: AudioCaptureSource.PcmCaptured fires
    // sequentially from NAudio's own capture callback thread, but fire-and-forget sends from that
    // callback could still complete out of order relative to each other.
    private readonly Channel<(byte[] Pcm, uint Timestamp)> _audioSendQueue =
        Channel.CreateUnbounded<(byte[], uint)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private D3D11Device? _gpu;
    private ScreenCaptureSource? _capture;
    private BgraToNv12Converter? _converter;
    private H264HardwareEncoder? _encoder;
    private RtpSession? _rtpSession;
    private AudioCaptureSource? _audioCapture;
    private RtpSession? _audioRtpSession;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _sendLoopTask;
    private Task? _audioSendLoopTask;

    public bool IsRunning => _loopTask != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesCaptured { get; private set; }
    public long AccessUnitsSent { get; private set; }
    public long BytesSent { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Whether audio capture actually started for this session — false means this cast is
    /// video-only, either because <see cref="AudioCaptureSource"/> failed to construct or because
    /// <see cref="Start"/> hasn't run yet.</summary>
    public bool HasAudio { get; private set; }
    public long AudioBytesSent { get; private set; }

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

    /// <summary>UTC time of the last <c>CastStatusMessage</c> received from this session's Terminal
    /// — null if none has arrived yet (could mean "just started, give it a second" or "the Terminal
    /// never actually accepted this cast at all", this class can't tell those apart).</summary>
    public DateTime? LastStatusReceivedAt { get; private set; }

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

    /// <summary>Raised from the background capture/encode loop, or from the encoder's own event
    /// loop — marshal to the UI thread before touching UI (mirrors every other self-test runner in
    /// this project).</summary>
    public event Action? StatsUpdated;

    public LiveCastSession(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, DiscoveredTerminal terminal)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _terminal = terminal;
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
        LastStatusReceivedAt = DateTime.UtcNow;
        StatsUpdated?.Invoke();
    }

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        AudioError = null;
        HasAudio = false;
        FramesCaptured = 0;
        AccessUnitsSent = 0;
        BytesSent = 0;
        AudioBytesSent = 0;
        TerminalFramesDecoded = 0;
        TerminalVideoBytesReceived = 0;
        TerminalVideoError = null;
        TerminalHasAudio = false;
        TerminalAudioBytesReceived = 0;
        TerminalAudioError = null;
        LastStatusReceivedAt = null;

        _discoveryClient.CastStatusReceived += OnCastStatusReceived;

        try
        {
            _gpu = new D3D11Device();
            _capture = new ScreenCaptureSource(_gpu);
            Width = _capture.Width;
            Height = _capture.Height;
            _converter = new BgraToNv12Converter(_gpu);
            _encoder = new H264HardwareEncoder(_gpu, Width, Height, frameRateNumerator: 30, bitrateBps: 4_000_000);
            _rtpSession = new RtpSession(new IPEndPoint(_terminal.Address, DiscoveryProtocol.VideoRtpPort), PayloadType);
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
            _audioRtpSession = new RtpSession(new IPEndPoint(_terminal.Address, DiscoveryProtocol.AudioRtpPort), AudioPayloadType);
            _audioCapture.PcmCaptured += OnPcmCaptured;
            _audioCapture.CaptureFailed += OnAudioCaptureFailed;
            HasAudio = true;
            audioInfo = new TerminalDiscoveryClient.AudioStreamInfo(_audioCapture.SampleRate, _audioCapture.Channels, AudioPayloadType);
        }
        catch (Exception ex)
        {
            // Best-effort, per this class's doc comment — video-only casting is a normal outcome,
            // not a failure of Start() itself.
            AudioError = ex.Message;
            _audioCapture?.Dispose();
            _audioCapture = null;
            _audioRtpSession?.Dispose();
            _audioRtpSession = null;
        }

        // Best-effort, fire-and-forget, same as every other discovery-protocol send in this repo —
        // there is no acknowledgment/retry to wait on, and a lost cast_start datagram on an
        // otherwise-healthy LAN is treated as an acceptable risk rather than a reason to block
        // pipeline startup (see this project's README).
        _ = _discoveryClient.SendCastStartAsync(_terminal, _identity, Width, Height, PayloadType, audioInfo);

        _clock.Restart();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoop(_gpu, _capture, _converter, _encoder, _cts.Token));
        _sendLoopTask = Task.Run(() => RunSendLoop(_rtpSession, _cts.Token));

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
        uint timestamp = RtpVideoClock.FromElapsed(_clock.Elapsed);
        var nalUnits = AnnexBNalSplitter.Split(accessUnit).ToList();
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
        int bytesPerSampleFrame = 2 * _audioCapture!.Channels; // 16-bit samples, interleaved by channel.

        // Derived from the SAME _clock (Stopwatch) video's RtpVideoClock.FromElapsed(_clock.Elapsed)
        // uses, just at the audio's own sample rate instead of 90000 — this is what lets
        // CastReceiver convert both streams' RTP timestamps back to one shared "elapsed since cast
        // start" timeline and pace video against audio at all (see this project's README's A/V sync
        // section). This replaces an earlier draft that accumulated a sample counter from a random
        // RFC-3550-style initial value — sample-accurate within one stream, but with no relationship
        // to video's wall-clock timestamps whatsoever, which made it useless for cross-stream sync.
        uint baseTimestamp = RtpVideoClock.FromElapsed(_clock.Elapsed, (uint)_audioCapture.SampleRate);
        int samplesEmittedSoFar = 0;

        int offset = 0;
        while (offset < pcm.Length)
        {
            int maxChunk = MaxAudioPayloadBytes - (MaxAudioPayloadBytes % bytesPerSampleFrame);
            int chunkBytes = Math.Min(maxChunk, pcm.Length - offset);
            var chunk = new byte[chunkBytes];
            Buffer.BlockCopy(pcm, offset, chunk, 0, chunkBytes);

            // Non-blocking enqueue — same reasoning as OnAccessUnitEncoded's _sendQueue.TryWrite:
            // this runs on NAudio's own capture callback thread, which must not block on network I/O.
            uint timestamp = baseTimestamp + (uint)samplesEmittedSoFar;
            _audioSendQueue.Writer.TryWrite((chunk, timestamp));

            samplesEmittedSoFar += chunkBytes / bytesPerSampleFrame;
            offset += chunkBytes;
        }

        AudioBytesSent += pcm.Length;
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
            await foreach (var (pcm, timestamp) in _audioSendQueue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await audioRtpSession.SendRawPayloadAsync(pcm, timestamp);
                }
                catch (Exception ex)
                {
                    AudioError = ex.Message;
                    StatsUpdated?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — Stop()/Dispose() cancels the token that ReadAllAsync is
            // observing.
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
        _loopTask = null;
        _sendLoopTask = null;
        _audioSendLoopTask = null;
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
        _audioRtpSession?.Dispose();
        _audioRtpSession = null;
    }

    public void Dispose() => StopInternal();
}
