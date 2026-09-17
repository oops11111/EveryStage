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
/// <see cref="Play"/>'s optional <c>fadeDuration</c> implements BOTH halves of PLANNING.md §6's
/// "淡入/淡出时长 + 音量是否随渐变" (<c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c>) — see
/// <see cref="RunPlaybackLoopCore"/>'s own doc comment for how fade-in and fade-out combine. Fade-in
/// only ever needed "how long has this file been playing", which
/// <see cref="AudioPlaybackClock.PositionTicks"/> already tracks precisely — no lookup required.
/// Fade-out needs the file's total duration in advance (to know when the final fade window begins),
/// which is why it was deferred a round past fade-in: see
/// <see cref="AudioDecodeSource.TryGetDuration"/>'s own doc comment for the considerable, explicitly
/// flagged risk in that one call (it's the least-verified Media Foundation call in this whole
/// codebase, going through <c>dynamic</c> specifically because of that) — a wrong guess there
/// degrades to "no fade-out for this file" rather than breaking anything else, which is what makes
/// attempting it here, despite the risk, still a reasonable trade.
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

    // Set fresh by each Play() call (unlike _volume below) — null means "no fade in/out for the
    // current file", not "fade finished", so RunPlaybackLoopCore can tell the two apart. Used for
    // both the fade-in ramp (always available) and the fade-out ramp (only when _totalDuration
    // below is also non-null) — PLANNING.md's MediaFile.FadeDuration is a single field covering
    // both directions, not a separate in/out pair.
    private TimeSpan? _fadeDuration;

    // Set fresh by each Play() call, from AudioDecodeSource.TryGetDuration() — null whenever that
    // call fails for any reason (see its own doc comment for how thoroughly unverified it is) or
    // fade-out wasn't requested in the first place. RunPlaybackLoopCore only attempts the fade-out
    // ramp when this is non-null; a null value here silently degrades this file to fade-in-only,
    // exactly like before fade-out existed at all.
    private TimeSpan? _totalDuration;

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
    /// name="path"/> from the beginning. <paramref name="fadeDuration"/>, when given, ramps
    /// <see cref="_audioClock"/>'s volume from 0 up to <see cref="_volume"/> linearly over that
    /// span at the start, AND — best-effort, see <see cref="AudioDecodeSource.TryGetDuration"/>'s
    /// own doc comment — ramps back down to 0 over the same span at the end. See
    /// <see cref="RunPlaybackLoopCore"/> for where both ramps are actually applied.</summary>
    public void Play(string path, TimeSpan? fadeDuration = null)
    {
        Stop();

        var source = new AudioDecodeSource(path);
        var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
        _fadeDuration = fadeDuration is { Ticks: > 0 } ? fadeDuration : null;
        // A zero-or-negative fade has nothing to ramp over, so it's treated as "no fade in/out"
        // rather than risking a divide-by-zero in RunPlaybackLoopCore's progress calculations.
        // TryGetDuration is only worth calling at all when a fade was actually requested — no
        // point risking its unverified `dynamic` call for a file that isn't going to fade either
        // way.
        _totalDuration = _fadeDuration.HasValue ? source.TryGetDuration() : null;
        audioClock.Volume = _fadeDuration.HasValue ? 0f : _volume; // see _volume's own doc comment — a fresh clock otherwise starts at full volume regardless of what was set for the previous file.
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

    /// <summary>See the class doc comment for the overall fade-in/fade-out scoping decision. Both
    /// ramps are applied here, once per chunk, rather than on a timer: this loop already runs
    /// roughly once per chunk duration (tens of milliseconds), which is frequent enough for a
    /// linear volume ramp to sound smooth, and piggybacking on an already-running loop avoids
    /// adding a second timer/thread just for this. <see cref="AudioPlaybackClock.PositionTicks"/>
    /// (not wall-clock elapsed time since <see cref="Play"/> was called) is deliberately used as
    /// the ramp's time base, since it's the same clock <see cref="Volume"/> actually plays back
    /// against — using it keeps both ramps correct even across a <see cref="Pause"/>/<see cref="Resume"/>
    /// (position simply stops advancing while paused, so the ramp pauses with it) instead of
    /// continuing to advance a wall-clock timer while audio isn't actually playing.
    ///
    /// The two ramps combine as <c>min(fadeInMultiplier, fadeOutMultiplier)</c>, not a sum or an
    /// either/or switch — this is what correctly handles a file shorter than
    /// <c>2 * _fadeDuration</c> (the fade-in and fade-out windows overlap): volume never reaches
    /// full even briefly in the middle, which is the audibly correct behavior a real fade
    /// implementation needs, not an edge case this round is skipping. <c>fadeOutMultiplier</c>
    /// itself defaults to 1 (no suppression at all) whenever <see cref="_totalDuration"/> is null —
    /// the same "silently fade-in-only" degradation <see cref="_totalDuration"/>'s own doc comment
    /// describes.</summary>
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

            if (_fadeDuration.HasValue)
            {
                double fadeInProgress = audioClock.PositionTicks / (double)_fadeDuration.Value.Ticks;
                double fadeInMultiplier = Math.Clamp(fadeInProgress, 0.0, 1.0);

                double fadeOutMultiplier = 1.0;
                if (_totalDuration.HasValue)
                {
                    long remainingTicks = _totalDuration.Value.Ticks - audioClock.PositionTicks;
                    fadeOutMultiplier = Math.Clamp(remainingTicks / (double)_fadeDuration.Value.Ticks, 0.0, 1.0);
                }

                audioClock.Volume = _volume * (float)Math.Min(fadeInMultiplier, fadeOutMultiplier);
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
