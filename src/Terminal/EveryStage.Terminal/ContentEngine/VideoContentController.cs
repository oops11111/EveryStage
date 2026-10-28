using System.Diagnostics;
using System.Drawing;
using EveryStage.Rendering;
using EveryStage.Rendering.Audio;
using EveryStage.Rendering.Decode;
using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Video playback for the Content Engine (PLANNING.md §3), built on the D3D11/Media Foundation
/// pipeline validated in Phase 0 (<c>EveryStage.Rendering</c>). Deliberately NOT an
/// <see cref="IContentRenderer"/>: that interface hands callers a <see cref="Bitmap"/> to draw via
/// GDI+, which is exactly the CPU round-trip the zero-copy pipeline exists to avoid. Instead this
/// presents through a <see cref="VideoSurface"/> attached directly to an HWND — the extended
/// display's overlay for the Program channel, or the in-app preview surface for the Preview channel.
///
/// The <see cref="VideoSurface"/> is owned by whoever constructs this controller; this class only
/// presents through it while playing.
///
/// Timing model:
/// <list type="bullet">
/// <item>Master clock: the audio output (<see cref="AudioPlaybackClock"/>, WASAPI) when the file has a
///   decodable audio track; otherwise a pausable wall clock. Files without audio used to fail to load
///   at all (see <see cref="VideoDecodeSource"/>).</item>
/// <item>Audio is kept buffered ~<see cref="AudioLeadTicks"/> ahead of playback and topped up while
///   waiting for the next video frame. The previous loop enqueued exactly one audio chunk per video
///   frame; AAC chunks (~21 ms) are shorter than a 30 fps frame (~33 ms), so the device was
///   starved and played padded silence.</item>
/// <item>File position = timestamp of the first audio sample delivered after start/seek (or the seek
///   target, without audio) + the clock's own counter. A fresh clock is built on every seek because
///   WASAPI's position counter only runs forward.</item>
/// </list>
///
/// <see cref="Play"/>'s optional <c>fadeDuration</c> implements PLANNING.md §6's
/// "淡入/淡出时长 + 音量是否随渐变" for the soundtrack: fade-in by position, fade-out once the
/// remaining time (from <see cref="VideoDecodeSource.TryGetDuration"/>) is within the fade window.
/// </summary>
public sealed class VideoContentController : IDisposable
{
    private static readonly long FrameBudgetTicks = TimeSpan.TicksPerSecond / 30;
    private static readonly long AudioLeadTicks = TimeSpan.FromMilliseconds(250).Ticks;

    private readonly VideoSurface _surface;
    private CancellationTokenSource? _playbackCts;
    private Thread? _playbackThread;
    private VideoDecodeSource? _source;
    private AudioPlaybackClock? _audioClock;
    private readonly Stopwatch _wallClock = new();

    private TimeSpan? _fadeDuration;
    private TimeSpan? _totalDuration;

    // File-time corresponding to clock position 0 for the current clock instance. Written by the
    // playback thread (first audio sample's timestamp) and by Play/TrySeekTo on the UI thread while
    // no playback thread is running, so plain long reads are safe enough for a position readout.
    private long _clockBaseTicks;

    // Where the last Play(startAt)/TrySeekTo asked playback to begin. MF seeks land on the preceding
    // key frame, so decoded audio/video before this point is discarded (decode-to-target).
    private long _seekTargetTicks;
    private bool _paused;
    private float _volume = 1f;
    private bool _muted;
    private float _fadeMultiplier = 1f;

    // Silent continuation (audit A-24/B-20): when the audio output cannot be opened, or dies mid-play,
    // the video keeps going on the wall clock, which then starts at _wallBaseTicks (the audio clock's
    // last good position) instead of 0. _audioNote says why there is no sound.
    private readonly object _clockGate = new();
    private volatile bool _audioLost;
    private long _wallBaseTicks;
    private volatile string? _audioNote;
    private volatile string? _performanceNote;

    /// <summary>Raised from the playback thread once the video stream (and any audio) has finished.</summary>
    public event Action? PlaybackCompleted;

    /// <summary>Raised from the playback thread when decoding/presenting throws; the thread then exits.</summary>
    public event Action<Exception>? PlaybackFailed;

    /// <summary>Raised from the playback thread when the first frame after <see cref="Play"/> is on
    /// screen — the channel's Loading → Playing transition.</summary>
    public event Action? FirstFramePresented;

    /// <summary>The audio output could not be opened or stopped mid-play; the video continues without
    /// sound. Raised from the UI thread (open failure) or the playback thread (loss), with the reason.</summary>
    public event Action<string>? AudioOutputLost;

    /// <summary>Raised once per file from the playback thread when frames are being dropped because
    /// decoding is slower than real time (the picture then updates at least every 250 ms).</summary>
    public event Action<string>? DecodeTooSlow;

    /// <summary>Set once decoding has been found too slow for this file; null otherwise.</summary>
    public string? PerformanceNote => _performanceNote;

    public bool IsPlaying => _source != null;
    public bool IsPaused => _paused;
    public bool HasAudio => _source?.HasAudio ?? false;
    public string? VideoCodec => _source?.VideoCodec;
    public string? AudioCodec => _source?.AudioCodec;
    public string? AudioUnavailableReason => _audioNote ?? _source?.AudioUnavailableReason;

    /// <summary>Short note for the Preview heading when the video plays without sound although it has
    /// (or should have) an audio track; null otherwise.</summary>
    public string? AudioNote => _audioNote
        ?? (_source is { HasAudio: false, AudioUnavailableReason: not null } source ? $"音频编码 {source.AudioCodec} 无法解码，无声播放" : null);

    public TimeSpan? Duration => _totalDuration;

    /// <summary>Current position within the file (zero when nothing is loaded).</summary>
    public TimeSpan Position
    {
        get
        {
            if (_source == null) return TimeSpan.Zero;
            long ticks = _clockBaseTicks + ClockTicks(_audioClock);
            if (_totalDuration is { } total && ticks > total.Ticks) ticks = total.Ticks;
            return TimeSpan.FromTicks(Math.Max(0, ticks));
        }
    }

    /// <summary>0..1, applied on top of any fade. Survives across <see cref="Play"/> calls.</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); ApplyVolume(); }
    }

    /// <summary>Silences the soundtrack without affecting timing (the Preview channel mutes itself while
    /// the Program channel is live so preview audio never leaks into the venue).</summary>
    public bool Muted
    {
        get => _muted;
        set { _muted = value; ApplyVolume(); }
    }

    /// <summary>Frames presented / dropped-for-lateness since <see cref="Play"/>, and the largest
    /// audio/video offset at presentation time. The offset is bounded by the pacing design itself (early
    /// frames wait, late ones are dropped), so the meaningful A/V-sync signal is <see cref="FramesDropped"/>:
    /// a drifting clock or a starved audio device shows up as a high drop rate.</summary>
    public long FramesPresented { get; private set; }
    public long FramesDropped { get; private set; }
    public double MaxAbsAvSkewMilliseconds { get; private set; }

    public VideoContentController(VideoSurface surface)
    {
        _surface = surface;
    }

    /// <summary>Stops whatever is playing and starts <paramref name="path"/>. Construction failures
    /// (missing file, unsupported codec) throw <see cref="MediaOpenException"/> synchronously; failures
    /// after that are reported through <see cref="PlaybackFailed"/>.</summary>
    public void Play(string path, TimeSpan? fadeDuration = null, TimeSpan startAt = default, bool startPaused = false)
    {
        Stop();

        var source = new VideoDecodeSource(path, _surface.Gpu);
        try
        {
            _fadeDuration = fadeDuration is { Ticks: > 0 } ? fadeDuration : null;
            _totalDuration = source.TryGetDuration();
            _source = source;
            FramesPresented = 0;
            FramesDropped = 0;
            MaxAbsAvSkewMilliseconds = 0;
            _paused = startPaused;

            if (startAt > TimeSpan.Zero && source.TrySeek(startAt)) _clockBaseTicks = startAt.Ticks;
            else _clockBaseTicks = 0;
            _seekTargetTicks = _clockBaseTicks;

            StartPlaybackThread(source, firstStart: true);
        }
        catch
        {
            _source = null;
            source.Dispose();
            throw;
        }
    }

    public void Pause()
    {
        if (_source == null || _paused) return;
        _paused = true;
        _audioClock?.Pause();
        lock (_clockGate) _wallClock.Stop();
    }

    public void Resume()
    {
        if (_source == null || !_paused) return;
        _paused = false;
        _audioClock?.Resume();
        lock (_clockGate) _wallClock.Start();
    }

    /// <summary>Jumps to <paramref name="position"/> (clamped to the file). Keeps the paused/playing
    /// state; while paused, the frame at the new position is still shown. Returns false if nothing is
    /// loaded or the source refused to seek.</summary>
    public bool TrySeekTo(TimeSpan position)
    {
        var source = _source;
        if (source == null) return false;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (_totalDuration is { } total && position > total) position = total;

        StopPlaybackThreadAndClock();
        if (!source.TrySeek(position))
        {
            // Couldn't move: resume where we were rather than leave playback dead.
            StartPlaybackThread(source, firstStart: false);
            return false;
        }
        _clockBaseTicks = position.Ticks;
        _seekTargetTicks = position.Ticks;
        StartPlaybackThread(source, firstStart: false);
        return true;
    }

    public void Stop()
    {
        StopPlaybackThreadAndClock();
        _source?.Dispose();
        _source = null;
        _clockBaseTicks = 0;
        _seekTargetTicks = 0;
        _paused = false;
        _audioNote = null;
        _performanceNote = null;
    }

    private void StopPlaybackThreadAndClock()
    {
        _playbackCts?.Cancel();
        _playbackThread?.Join();
        _playbackThread = null;
        _playbackCts?.Dispose();
        _playbackCts = null;

        _audioClock?.Dispose();
        _audioClock = null;
        lock (_clockGate)
        {
            _wallClock.Reset();
            _wallBaseTicks = 0;
            _audioLost = false;
        }
    }

    private void StartPlaybackThread(VideoDecodeSource source, bool firstStart)
    {
        _audioClock = null;
        if (source.HasAudio)
        {
            try
            {
                _audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
            }
            catch (Exception ex)
            {
                // No output device, or one that refuses the stream (audit B-20): the picture still plays,
                // silently, the same way a video whose audio codec cannot be decoded does. Thrown here this
                // used to refuse the whole file — or, on a seek, escape to the UI with playback stopped.
                source.DisableAudio();
                string reason = $"无法输出声音（{ex.Message.Trim()}），无声播放";
                _audioNote = reason;
                AudioOutputLost?.Invoke(reason);
            }
        }
        _fadeMultiplier = _fadeDuration.HasValue ? (float)ComputeFadeMultiplier(_clockBaseTicks) : 1f;
        ApplyVolume();

        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        var audioClock = _audioClock;
        _playbackThread = new Thread(() => RunPlaybackLoop(source, audioClock, firstStart, cts.Token))
        {
            IsBackground = true,
            Name = "EveryStage.VideoPlayback",
        };
        _playbackThread.Start();
    }

    private void RunPlaybackLoop(VideoDecodeSource source, AudioPlaybackClock? audioClock, bool firstStart, CancellationToken token)
    {
        try
        {
            RunPlaybackLoopCore(source, audioClock, firstStart, token);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) PlaybackFailed?.Invoke(ex);
        }
    }

    private long ClockTicks(AudioPlaybackClock? audioClock)
    {
        if (audioClock != null && !_audioLost) return audioClock.PositionTicks;
        lock (_clockGate) return _wallBaseTicks + _wallClock.Elapsed.Ticks;
    }

    private void RunPlaybackLoopCore(VideoDecodeSource source, AudioPlaybackClock? audioClock, bool firstStart, CancellationToken token)
    {
        bool audioDone = audioClock == null;
        bool videoDone = false;
        bool clockStarted = false;
        bool firstFrameShown = false;

        // Every audio byte is lined up with the time it will actually play (audit B-22): gaps in the track
        // become silence and late or overlapping audio is trimmed, so the clock's rendered-bytes position
        // stays equal to file time and the picture paced on it stays in sync.
        var timeline = audioClock != null ? new AudioTimeline(audioClock, source.AudioSampleRate, source.AudioChannels) : null;
        void EnqueueAligned(byte[] pcm, long timestampTicks)
        {
            var aligned = PcmTrim.AlignTo(pcm, timestampTicks, _clockBaseTicks + timeline!.ScheduleTicks, source.AudioSampleRate, source.AudioChannels);
            if (aligned != null) timeline.Enqueue(aligned);
        }

        // Prefill the audio lead before starting the clock so playback doesn't begin with padded
        // silence; the first delivered sample's timestamp anchors file time to clock position 0.
        if (audioClock != null)
        {
            bool anchored = false;
            while (!token.IsCancellationRequested && audioClock.BufferedDurationTicks < AudioLeadTicks)
            {
                var chunk = source.ReadNextAudioChunk();
                if (chunk == null) { audioDone = true; break; }
                var trimmed = PcmTrim.ToTarget(chunk.Value.Pcm, chunk.Value.TimestampTicks, _seekTargetTicks, source.AudioSampleRate, source.AudioChannels);
                if (trimmed == null) continue; // entirely before the seek target
                if (!anchored) { _clockBaseTicks = trimmed.Value.TimestampTicks; anchored = true; }
                EnqueueAligned(trimmed.Value.Pcm, trimmed.Value.TimestampTicks);
            }
        }
        if (token.IsCancellationRequested) return;

        void StartClock()
        {
            if (clockStarted) return;
            clockStarted = true;
            if (audioClock != null && !_audioLost)
            {
                // Not started while paused (start-then-pause races NAudio's playback thread; see
                // AudioContentController.StartPlaybackThread). Resume() starts it.
                if (!_paused) audioClock.Start();
            }
            else if (!_paused)
            {
                lock (_clockGate) _wallClock.Start();
            }
        }

        // The audio output stopped on its own (endpoint removed — audit A-24): its position no longer
        // moves, so pacing on it would hold this frame forever. Continue on the wall clock from where the
        // sound stopped, drop the audio track, and say so. Reading PositionTicks is what detects the stop.
        bool AudioLost()
        {
            if (_audioLost || audioClock == null) return _audioLost;
            long position = audioClock.PositionTicks;
            if (!audioClock.IsFaulted) return false;
            lock (_clockGate)
            {
                _wallBaseTicks = position;
                _wallClock.Reset();
                if (clockStarted && !_paused) _wallClock.Start();
                _audioLost = true;
            }
            audioDone = true;
            source.DisableAudio();
            string reason = $"声音输出已断开（{audioClock.FaultReason}），继续无声播放";
            _audioNote = reason;
            AudioOutputLost?.Invoke(reason);
            return true;
        }

        void TopUpAudio()
        {
            // Fault check first: it must run even after the audio track has ended, or a loss during the
            // video's silent tail would leave the pacing wait below on a frozen clock.
            if (audioClock == null || AudioLost() || audioDone) return;
            while (!token.IsCancellationRequested && audioClock.BufferedDurationTicks < AudioLeadTicks)
            {
                var chunk = source.ReadNextAudioChunk();
                if (chunk == null) { audioDone = true; return; }
                if (_fadeDuration.HasValue)
                {
                    _fadeMultiplier = (float)ComputeFadeMultiplier(_clockBaseTicks + audioClock.PositionTicks);
                    ApplyVolume();
                }
                EnqueueAligned(chunk.Value.Pcm, chunk.Value.TimestampTicks);
            }
        }

        var sinceLastPresent = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            TopUpAudio();

            if (videoDone)
            {
                if (!audioDone) { Thread.Sleep(5); continue; }
                // Let the audio tail finish playing before reporting completion — played, not merely taken
                // by WASAPI, which is about one device period earlier (audit C-33).
                while (!token.IsCancellationRequested && audioClock != null && !AudioLost() && timeline!.ContentTicks < timeline.EnqueuedContentTicks)
                    Thread.Sleep(10);
                if (!token.IsCancellationRequested) PlaybackCompleted?.Invoke();
                return;
            }

            var frame = source.ReadNextVideoFrame();
            if (frame == null) { videoDone = true; StartClock(); continue; }
            if (SimulatedDecodeDelay > TimeSpan.Zero) Thread.Sleep(SimulatedDecodeDelay);

            try
            {
                long frameTicks = frame.Value.TimestampTicks - _clockBaseTicks;

                if (!clockStarted)
                {
                    // Show the first frame at/after the start position immediately, then start the
                    // clock, so start-up (and a seek while paused) always puts a picture up.
                    if (frameTicks < -FrameBudgetTicks) continue; // before the seek target
                    PresentFrame(frame.Value, 0, audioClock);
                    StartClock();
                    continue;
                }

                while (!token.IsCancellationRequested && ClockTicks(audioClock) < frameTicks - FrameBudgetTicks)
                {
                    TopUpAudio();
                    Thread.Sleep(1);
                }
                if (token.IsCancellationRequested) return;

                long behindTicks = ClockTicks(audioClock) - frameTicks;
                if (behindTicks > FrameBudgetTicks)
                {
                    // Fell behind — drop instead of presenting stale video. But dropping does not skip
                    // decoding: when decode is slower than real time every later frame is late too, and
                    // dropping them all froze the picture until the end while the sound played on (audit
                    // B-24). Never let the picture stand still longer than MaxFrameGapTicks.
                    if (sinceLastPresent.ElapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency < MaxFrameGapTicks)
                    {
                        FramesDropped++;
                        ReportIfTooSlow();
                        continue;
                    }
                }
                PresentFrame(frame.Value, behindTicks, audioClock);
            }
            finally
            {
                frame.Value.Release(); // texture + the sample that keeps its decoder slot reserved
            }

            void PresentFrame(DecodedVideoFrame f, long skewTicks, AudioPlaybackClock? clock)
            {
                _surface.PresentFrame(f.Texture, f.ArraySlice, f.Width, f.Height, vsync: false);
                sinceLastPresent.Restart();
                FramesPresented++;
                if (clock != null && clockStarted && !_audioLost)
                    MaxAbsAvSkewMilliseconds = Math.Max(MaxAbsAvSkewMilliseconds, Math.Abs(skewTicks) / (double)TimeSpan.TicksPerMillisecond);
                if (!firstFrameShown)
                {
                    firstFrameShown = true;
                    FirstFramePresented?.Invoke();
                }
            }
        }

        // Once per file: a third or more of the frames dropped means the machine cannot decode this
        // file in real time — say so instead of leaving a jerky picture unexplained.
        void ReportIfTooSlow()
        {
            if (_performanceNote != null || FramesDropped < 30 || FramesDropped * 3 < FramesPresented + FramesDropped) return;
            string note = $"本机解码跟不上该视频（已丢弃 {FramesDropped} 帧），画面会跳帧";
            _performanceNote = note;
            DecodeTooSlow?.Invoke(note);
        }
    }

    // Longest the picture may stand still while frames are being dropped for lateness.
    private static readonly long MaxFrameGapTicks = TimeSpan.FromMilliseconds(250).Ticks;

    /// <summary>Test hook: extra time spent per decoded frame, simulating a machine too slow for the file.</summary>
    internal static TimeSpan SimulatedDecodeDelay { get; set; }

    private void ApplyVolume()
    {
        var clock = _audioClock;
        if (clock == null) return;
        try { clock.Volume = _muted ? 0f : _volume * _fadeMultiplier; }
        catch (Exception) { } // the device can vanish mid-flight; timing doesn't depend on volume.
    }

    /// <summary>min(fade-in, fade-out) at <paramref name="positionTicks"/>; fade-out only when the total
    /// duration is known.</summary>
    private double ComputeFadeMultiplier(long positionTicks)
    {
        if (_fadeDuration is not { } fade) return 1.0;
        double fadeIn = Math.Clamp(positionTicks / (double)fade.Ticks, 0.0, 1.0);
        double fadeOut = 1.0;
        if (_totalDuration is { } total)
            fadeOut = Math.Clamp((total.Ticks - positionTicks) / (double)fade.Ticks, 0.0, 1.0);
        return Math.Min(fadeIn, fadeOut);
    }

    public void Dispose()
    {
        Stop();
    }
}
