using EveryStage.Rendering.Audio;
using EveryStage.Rendering.Decode;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Standalone-audio playback for the Content Engine (PLANNING.md §6 "音频特殊性") — plays a
/// non-background <c>MediaFile</c> (<c>IsBackgroundAudio == false</c>) as a normal queue item, the
/// same way <see cref="VideoContentController"/> plays video: takes over the current playback slot,
/// has a real completion action, participates in sequential auto-advance. See
/// <c>PlaybackEngine</c>'s doc comment for exactly what remains out of scope — background-audio
/// overlay (playing concurrently with other visual content, PLANNING.md's "叠加在其他视觉内容之上
/// 播放，不占用主队列顺序位") needs a genuinely concurrent playback-track model this class doesn't
/// attempt.
///
/// <see cref="Play"/>'s optional <c>fadeInDuration</c> implements the fade-IN half of PLANNING.md
/// §6's "淡入/淡出时长 + 音量是否随渐变" (<c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c>) —
/// see <see cref="RunPlaybackLoopCore"/>'s own doc comment for why fade-OUT is deliberately NOT
/// attempted this round: it needs to know the file's total duration in advance to know when the
/// final fade window begins, and neither <see cref="AudioDecodeSource"/> nor
/// <c>VideoDecodeSource</c> currently expose one — this sandbox has no way to verify a new
/// <c>IMFSourceReader::GetPresentationAttribute(MF_PD_DURATION, ...)</c> call against a real Media
/// Foundation build, so adding it blind here risked compounding this already-unverified file's risk
/// for the less commonly needed half of the feature. Fade-in needs no such lookup — it only needs
/// "how long has this file been playing", which <see cref="AudioPlaybackClock.PositionTicks"/>
/// already tracks precisely.
///
/// Reuses <see cref="AudioPlaybackClock"/> (WASAPI output) exactly as
/// <see cref="VideoContentController"/> does for a video's own audio track, but with nothing to pace
/// against except the audio itself — there's no separate video frame clock to stay in sync with
/// here, so <see cref="AheadBudgetTicks"/> exists purely to stop this loop from decoding arbitrarily
/// far ahead of what <see cref="AudioPlaybackClock"/>'s internal buffer can actually hold (see that
/// field's own doc comment).
/// </summary>
public sealed class AudioContentController : IDisposable
{
    // See RunPlaybackLoopCore: how far ahead of actual playback position this loop lets itself
    // decode before pausing. AudioPlaybackClock's BufferedWaveProvider holds 5 seconds with
    // DiscardOnBufferOverflow — without some cap here, decode (typically much cheaper than video
    // decode) would tear through an entire file into that 5-second buffer almost immediately, and
    // everything past the 5-second window would then be silently discarded before ever reaching
    // WASAPI. One second of slack is comfortably more than any single decode/enqueue iteration
    // should ever take, while staying well under that 5-second ceiling.
    private static readonly long AheadBudgetTicks = TimeSpan.TicksPerSecond;

    private CancellationTokenSource? _playbackCts;
    private Thread? _playbackThread;
    private AudioDecodeSource? _source;
    private AudioPlaybackClock? _audioClock;

    // Set fresh by each Play() call (unlike _volume below) — null means "no fade-in for the
    // current file", not "fade-in finished", so RunPlaybackLoopCore can tell the two apart.
    private TimeSpan? _fadeInDuration;

    // Survives across Play() calls (unlike _audioClock, which gets torn down and rebuilt every
    // time) precisely so adjusting volume once stays in effect for whatever file plays next within
    // this controller's lifetime, not just the currently-playing one — matches how a real player's
    // volume control behaves, rather than silently resetting to full volume every track.
    private float _volume = 1f;

    /// <summary>Playback volume, 0.0 (silent) to 1.0 (full) — clamped on write. Applied immediately
    /// to <see cref="_audioClock"/> if something is currently playing, and to whatever
    /// <see cref="AudioPlaybackClock"/> the next <see cref="Play"/> call constructs, since a fresh
    /// clock always starts at NAudio's own default volume (1.0) otherwise.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_audioClock != null) _audioClock.Volume = _volume;
        }
    }

    /// <summary>Raised (from the background playback thread — marshal to the UI thread if the
    /// handler touches UI) when the audio stream reaches end-of-stream.</summary>
    public event Action? PlaybackCompleted;

    /// <summary>Same "don't let the thread vanish silently" reasoning as
    /// <see cref="VideoContentController.PlaybackFailed"/>.</summary>
    public event Action<Exception>? PlaybackFailed;

    /// <summary>Raised from the background playback thread with each newly-decoded PCM chunk's peak
    /// amplitude, normalized to [0, 1] — the only consumer is <c>PlaybackEngine</c>'s
    /// <c>AudioVisual.Waveform</c> handling. Assumes 16-bit PCM (see
    /// <see cref="AudioDecodeSource"/>'s <c>MFAudioFormat_PCM</c> negotiation).</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Stops whatever is currently playing (if anything) and starts <paramref
    /// name="path"/> from the beginning. <paramref name="fadeInDuration"/>, when given, ramps
    /// <see cref="_audioClock"/>'s volume from 0 up to <see cref="_volume"/> linearly over that
    /// span — see <see cref="RunPlaybackLoopCore"/> for where that ramp is actually applied, and
    /// the class doc comment for why there's no equivalent fade-OUT parameter yet.</summary>
    public void Play(string path, TimeSpan? fadeInDuration = null)
    {
        Stop();

        var source = new AudioDecodeSource(path);
        var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
        _fadeInDuration = fadeInDuration is { Ticks: > 0 } ? fadeInDuration : null;
        // A zero-or-negative fade-in has nothing to ramp over, so it's treated as "no fade-in"
        // rather than risking a divide-by-zero in RunPlaybackLoopCore's progress calculation.
        audioClock.Volume = _fadeInDuration.HasValue ? 0f : _volume; // see _volume's own doc comment — a fresh clock otherwise starts at full volume regardless of what was set for the previous file.
        _source = source;
        _audioClock = audioClock;

        var cts = new CancellationTokenSource();
        _playbackCts = cts;

        audioClock.Start();
        _playbackThread = new Thread(() => RunPlaybackLoop(source, audioClock, cts.Token))
        {
            IsBackground = true,
            Name = "EveryStage.AudioPlayback",
        };
        _playbackThread.Start();
    }

    /// <summary>Pauses in place — unlike <see cref="VideoContentController"/> (whose <c>Stop()</c>
    /// tears the decode source down entirely, see its own doc comment on why video pause-in-place
    /// isn't implemented), audio can pause safely here because <see cref="AudioPlaybackClock"/>'s
    /// underlying WASAPI output has a real pause primitive (<c>WasapiOut.Pause()</c>) that leaves
    /// playback position exactly where it stopped — nothing about the decode source/thread needs to
    /// be torn down or reconstructed. The background <see cref="RunPlaybackLoopCore"/> loop doesn't
    /// need to be told to pause separately: it already only decodes/enqueues as far ahead of
    /// <see cref="AudioPlaybackClock.PositionTicks"/> as <see cref="AheadBudgetTicks"/> allows, and
    /// that position stops advancing the moment WASAPI is paused — so the loop naturally blocks in
    /// its existing wait spin the same way it would if playback were simply running slow, not
    /// stopped. No-op if nothing is currently playing (<see cref="_audioClock"/> null).</summary>
    public void Pause() => _audioClock?.Pause();

    /// <summary>Resumes playback paused by <see cref="Pause"/> from the exact position it left off —
    /// see that method's own doc comment. No-op if nothing is currently playing.</summary>
    public void Resume() => _audioClock?.Resume();

    public void Stop()
    {
        _playbackCts?.Cancel();
        _playbackThread?.Join();
        _playbackThread = null;
        _playbackCts?.Dispose();
        _playbackCts = null;

        _audioClock?.Dispose();
        _audioClock = null;
        _source?.Dispose();
        _source = null;
    }

    private void RunPlaybackLoop(AudioDecodeSource source, AudioPlaybackClock audioClock, CancellationToken token)
    {
        try
        {
            RunPlaybackLoopCore(source, audioClock, token);
        }
        catch (Exception ex)
        {
            PlaybackFailed?.Invoke(ex);
        }
    }

    /// <summary>See the class doc comment for the overall fade-in/fade-out scoping decision. The
    /// fade-in ramp itself is applied here, once per chunk, rather than on a timer: this loop
    /// already runs roughly once per chunk duration (tens of milliseconds), which is frequent
    /// enough for a linear volume ramp to sound smooth, and piggybacking on an already-running
    /// loop avoids adding a second timer/thread just for this. <see cref="AudioPlaybackClock.PositionTicks"/>
    /// (not wall-clock elapsed time since <see cref="Play"/> was called) is deliberately used as
    /// the ramp's time base, since it's the same clock <see cref="Volume"/> actually plays back
    /// against — using it keeps the ramp correct even across a <see cref="Pause"/>/<see cref="Resume"/>
    /// (position simply stops advancing while paused, so the ramp pauses with it) instead of
    /// continuing to advance a wall-clock timer while audio isn't actually playing.</summary>
    private void RunPlaybackLoopCore(AudioDecodeSource source, AudioPlaybackClock audioClock, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var chunk = source.ReadNextChunk();
            if (chunk == null)
            {
                PlaybackCompleted?.Invoke();
                return;
            }

            while (!token.IsCancellationRequested && audioClock.PositionTicks < chunk.Value.TimestampTicks - AheadBudgetTicks)
                Thread.Sleep(5);

            if (token.IsCancellationRequested) return;

            if (_fadeInDuration.HasValue)
            {
                double progress = audioClock.PositionTicks / (double)_fadeInDuration.Value.Ticks;
                audioClock.Volume = _volume * (float)Math.Clamp(progress, 0.0, 1.0);
            }

            audioClock.Enqueue(chunk.Value.Pcm);
            LevelChanged?.Invoke(ComputePeakLevel(chunk.Value.Pcm));
        }
    }

    /// <summary>Peak (not RMS) absolute sample value in this chunk, normalized against
    /// <see cref="short.MaxValue"/> — a peak meter reacts more visibly to percussive/transient audio
    /// than an RMS average would, which matters more for "does this look alive" than for accurate
    /// loudness measurement. Assumes 16-bit little-endian PCM and silently ignores a trailing odd
    /// byte, if the chunk length is odd (should never happen for whole-sample PCM chunks, but this
    /// is display-only data, not something worth throwing over).</summary>
    private static float ComputePeakLevel(byte[] pcm)
    {
        short peak = 0;
        int sampleCount = pcm.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            short abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (abs > peak) peak = abs;
        }
        return peak / (float)short.MaxValue;
    }

    public void Dispose() => Stop();
}
