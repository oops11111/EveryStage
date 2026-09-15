using System.Collections.Concurrent;
using System.Diagnostics;
using EveryStage.Rendering.Audio;
using EveryStage.Terminal.Display;
using EveryStage.Transport;
using Vortice.Direct3D11;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Terminal-side counterpart to Caster's <c>LiveCastSession</c>: receives the RTP/H.264 stream
/// (<c>RtpReceiver</c>, EveryStage.Transport), reassembles NAL units back into Annex-B access units
/// using the RTP marker bit (the inverse of <c>AnnexBNalSplitter</c>, which the Caster side used to
/// strip start codes before packetizing), decodes each with <see cref="H264HardwareDecoder"/>, and
/// presents the result through the shared <see cref="VideoSurface"/> bound to the overlay window's
/// video HWND — the same surface <c>ContentEngine.VideoContentController</c> uses for local file
/// playback, here fed from a live network stream instead of a file. Optionally also receives a raw
/// PCM audio stream (<c>RawRtpReceiver</c>, EveryStage.Transport) and plays it through
/// <see cref="AudioPlaybackClock"/> — the same WASAPI playback class <c>VideoContentController</c>
/// uses for local video files' audio track, and here it plays the same master-clock role too:
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
/// actual GPU presentation — <see cref="_pendingFrames"/> (a <see cref="ConcurrentQueue{T}"/>) and
/// <see cref="_audioSyncOffsetTicks"/> (written at most once under a lock, then only ever read) are
/// the only state shared between them. All three also independently bump
/// <see cref="LastPacketReceivedAt"/>, which <c>TerminalApplicationContext.CheckCastLiveness</c>
/// polls to notice a Caster that's gone silent without ever sending
/// <c>DiscoveryProtocol.CastStopMessage</c> — UDP doesn't guarantee that message arrives, and a
/// crashed Caster never gets to send it at all.
/// </summary>
public sealed class CastReceiver : IDisposable
{
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
    private readonly H264HardwareDecoder _decoder;
    private readonly RtpReceiver _rtpReceiver;
    private readonly List<byte[]> _pendingNals = new();

    private readonly RawRtpReceiver? _audioRtpReceiver;
    private readonly AudioPlaybackClock? _audioClock;

    // Guards the one-time initialization of _audioSyncOffsetTicks below — the audio RTP receive loop
    // is the only writer, but it's simplest to make the "first packet establishes the offset" check
    // explicit rather than relying on a data race that happens to be benign.
    private readonly object _syncLock = new();
    private long? _audioSyncOffsetTicks;
    private readonly uint _audioSampleRate;

    private readonly ConcurrentQueue<PendingFrame> _pendingFrames = new();
    private CancellationTokenSource? _presentCts;
    private Thread? _presentThread;

    private readonly record struct PendingFrame(ID3D11Texture2D Texture, int ArraySlice, int Width, int Height, long PresentationTicks);

    public int Width { get; }
    public int Height { get; }
    public long FramesDecoded { get; private set; }
    public long BytesReceived { get; private set; }
    public string? LastError { get; private set; }

    public bool HasAudio { get; private set; }
    public long AudioBytesReceived { get; private set; }

    /// <summary>Set when audio construction fails — unlike a video-side failure (which fails this
    /// whole constructor, since there's no cast without video), an audio failure here degrades to
    /// video-only, matching <c>LiveCastSession</c>'s own audio-is-best-effort handling on the Caster
    /// side. Video-only mode presents each decoded frame immediately (see <see cref="OnFrameDecoded"/>)
    /// rather than routing it through the audio-paced presentation thread, since there's no audio
    /// clock left to pace against.</summary>
    public string? AudioError { get; private set; }

    /// <summary>UTC time of the most recent video OR audio packet actually received — initialized
    /// to construction time (not <c>DateTime.MinValue</c>) so a brand-new receiver gets a grace
    /// period before <c>TerminalApplicationContext.CheckCastLiveness</c> can consider it stale;
    /// otherwise the very first liveness check (which can run before the Caster's first RTP packet
    /// has even arrived) would immediately look like a timeout.</summary>
    public DateTime LastPacketReceivedAt { get; private set; } = DateTime.UtcNow;

    public CastReceiver(VideoSurface surface, int width, int height, int listenPort,
        bool hasAudio = false, int audioSampleRate = 0, int audioChannels = 0, int audioListenPort = 0)
    {
        Width = width;
        Height = height;

        _surface = surface;
        _decoder = new H264HardwareDecoder(_surface.Gpu, width, height);
        _decoder.FrameDecoded += OnFrameDecoded;

        _rtpReceiver = new RtpReceiver(listenPort);
        _rtpReceiver.NalUnitReceived += OnNalUnitReceived;

        if (hasAudio)
        {
            try
            {
                // Constructed here (not lazily on first packet) so a WASAPI failure is caught right
                // away rather than surfacing later as an unhandled exception from inside
                // OnAudioPayloadReceived on the RTP receive thread.
                _audioClock = new AudioPlaybackClock(audioSampleRate, audioChannels);
                _audioSampleRate = (uint)audioSampleRate;
                _audioRtpReceiver = new RawRtpReceiver(audioListenPort);
                _audioRtpReceiver.PayloadReceived += OnAudioPayloadReceived;
                HasAudio = true;
            }
            catch (Exception ex)
            {
                // Video-only is a normal, expected outcome here — a WASAPI output failure on the
                // Terminal shouldn't take down a cast whose video side is working fine.
                AudioError = ex.Message;
                _audioClock?.Dispose();
                _audioClock = null;
                _audioRtpReceiver?.Dispose();
                _audioRtpReceiver = null;
                HasAudio = false;
            }
        }
    }

    public void Start()
    {
        _rtpReceiver.Start();
        if (HasAudio)
        {
            _audioClock!.Start();
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

    private void OnAudioPayloadReceived(byte[] pcm, uint timestamp)
    {
        LastPacketReceivedAt = DateTime.UtcNow;
        AudioBytesReceived += pcm.Length;

        // Establishes the mapping between "audio RTP timestamp" and "AudioPlaybackClock.PositionTicks"
        // exactly once, from the first audio packet this receiver ever sees. RtpVideoClock.ToElapsedTicks
        // converts that packet's RTP timestamp back into "ticks elapsed since the Caster's cast-session
        // clock started" — call it C. At the moment this packet is handed to _audioClock.Enqueue below,
        // AudioPlaybackClock.PositionTicks is ~0 (nothing has played yet). So from here on,
        // (a video frame's own elapsed-since-cast-start time) - _audioSyncOffsetTicks should equal the
        // AudioPlaybackClock.PositionTicks at which that frame's audio counterpart is playing — see
        // RunPresentLoop. This is inherently approximate: it assumes this first audio packet reaches
        // the Terminal and gets enqueued with negligible delay relative to when it was captured, which
        // holds well enough on a LAN but is not something this scheme measures or corrects for.
        if (_audioSyncOffsetTicks == null)
        {
            lock (_syncLock)
            {
                _audioSyncOffsetTicks ??= RtpVideoClock.ToElapsedTicks(timestamp, _audioSampleRate);
            }
        }

        // No jitter buffer, no reordering/loss recovery — enqueued straight into WASAPI playback as
        // it arrives. BufferedWaveProvider (inside AudioPlaybackClock) discards on overflow rather
        // than blocking, so a burst of late packets thins itself out instead of building unbounded
        // latency; a dropped/reordered RTP packet here just becomes an audible gap/glitch, the same
        // "no loss recovery" limitation the video side already has and documents.
        _audioClock!.Enqueue(pcm);
    }

    private void OnNalUnitReceived(byte[] nalUnit, bool isLastNalOfAccessUnit, uint timestamp)
    {
        LastPacketReceivedAt = DateTime.UtcNow;
        BytesReceived += nalUnit.Length;
        _pendingNals.Add(nalUnit);
        if (!isLastNalOfAccessUnit) return;

        byte[] accessUnit = BuildAnnexBAccessUnit(_pendingNals);
        _pendingNals.Clear();

        try
        {
            // Converted back to 100ns ticks on the "elapsed since cast session start" timeline shared
            // with the audio stream (see RtpVideoClock's doc comment and OnAudioPayloadReceived
            // above) — this is the value H264HardwareDecoder's FIFO carries through to OnFrameDecoded
            // as presentationTicks, and what RunPresentLoop paces against the audio clock.
            long presentationTicks = RtpVideoClock.ToElapsedTicks(timestamp, RtpVideoClock.ClockRate);
            _decoder.SubmitAccessUnit(accessUnit, presentationTicks);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static byte[] BuildAnnexBAccessUnit(List<byte[]> nalUnits)
    {
        // Inverse of AnnexBNalSplitter: prefix each NAL unit with a 4-byte start code and
        // concatenate. The Caster side stripped these off when packetizing per RFC 6184, so they
        // must be put back before handing the bytestream to a decoder MFT that expects Annex B.
        int total = 0;
        foreach (var nal in nalUnits) total += nal.Length + 4;

        var buffer = new byte[total];
        int offset = 0;
        foreach (var nal in nalUnits)
        {
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 0;
            buffer[offset++] = 1;
            nal.CopyTo(buffer, offset);
            offset += nal.Length;
        }
        return buffer;
    }

    private void OnFrameDecoded(ID3D11Texture2D texture, int arraySlice, int width, int height, long presentationTicks)
    {
        if (!HasAudio)
        {
            // No audio clock to pace against — present immediately, same as before this round.
            using (texture)
            {
                _surface.Presenter.PresentFrame(texture, arraySlice, width, height, vsync: false);
            }
            FramesDecoded++;
            return;
        }

        // Ownership of the texture's COM reference passes into the queue here — RunPresentLoop (or
        // Dispose, on teardown) is now responsible for disposing it exactly once.
        _pendingFrames.Enqueue(new PendingFrame(texture, arraySlice, width, height, presentationTicks));
    }

    /// <summary>Background thread that paces decoded-frame presentation against
    /// <see cref="AudioPlaybackClock.PositionTicks"/> — the live-cast equivalent of
    /// <c>VideoContentController.RunPlaybackLoopCore</c>'s identical wait/drop logic for local file
    /// playback, adapted to run against a queue fed asynchronously by the RTP receive thread instead
    /// of pulling frames synchronously from a decode source. Runs only while <see cref="HasAudio"/>
    /// is true (see <see cref="Start"/>); with no audio, <see cref="OnFrameDecoded"/> presents
    /// immediately and this loop never starts.</summary>
    private void RunPresentLoop(CancellationToken token)
    {
        var waitStopwatch = new Stopwatch();

        while (!token.IsCancellationRequested)
        {
            if (!_pendingFrames.TryDequeue(out var frame))
            {
                Thread.Sleep(1);
                continue;
            }

            // _audioSyncOffsetTicks is null until the first audio packet arrives — a frame decoded
            // before that point has no audio position to compare against yet. Presenting it
            // immediately (rather than dropping it or blocking indefinitely) means a cast with a
            // slow-starting audio path still shows video right away instead of a black/frozen
            // screen; sync simply begins once the offset becomes available.
            long? offset = _audioSyncOffsetTicks;
            if (offset == null)
            {
                Present(frame);
                continue;
            }

            long targetAudioPositionTicks = frame.PresentationTicks - offset.Value;

            waitStopwatch.Restart();
            while (!token.IsCancellationRequested
                   && _audioClock!.PositionTicks < targetAudioPositionTicks - FrameBudgetTicks
                   && waitStopwatch.Elapsed.Ticks < MaxWaitTicks)
            {
                Thread.Sleep(1);
            }

            if (token.IsCancellationRequested)
            {
                frame.Texture.Dispose();
                return;
            }

            long behindByTicks = _audioClock!.PositionTicks - targetAudioPositionTicks;
            if (behindByTicks > FrameBudgetTicks)
            {
                // Fell far enough behind the audio clock that this frame is stale — drop it rather
                // than present outdated video, same policy VideoContentController already applies.
                frame.Texture.Dispose();
                continue;
            }

            Present(frame);
        }

        // Drain and dispose whatever's left so cancellation doesn't leak GPU texture references.
        while (_pendingFrames.TryDequeue(out var leftover)) leftover.Texture.Dispose();
    }

    private void Present(PendingFrame frame)
    {
        using (frame.Texture)
        {
            _surface.Presenter.PresentFrame(frame.Texture, frame.ArraySlice, frame.Width, frame.Height, vsync: false);
        }
        FramesDecoded++;
    }

    public void Dispose()
    {
        // Does NOT dispose _surface — shared with VideoContentController, owned by
        // TerminalApplicationContext (see VideoSurface's doc comment).
        _rtpReceiver.Dispose();
        _decoder.FrameDecoded -= OnFrameDecoded;
        _decoder.Dispose();

        _presentCts?.Cancel();
        _presentThread?.Join();
        _presentCts?.Dispose();

        if (_audioRtpReceiver != null) _audioRtpReceiver.PayloadReceived -= OnAudioPayloadReceived;
        _audioRtpReceiver?.Dispose();
        _audioClock?.Dispose();

        // Anything still queued after the present thread has stopped (e.g. HasAudio was false and
        // frames were never routed through the queue at all — this is then always empty and the
        // loop is a no-op, but it's cheap insurance either way).
        while (_pendingFrames.TryDequeue(out var leftover)) leftover.Texture.Dispose();
    }
}
