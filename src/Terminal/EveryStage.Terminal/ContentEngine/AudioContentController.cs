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
/// <see cref="ComputeFadeMultiplier"/>'s own doc comment for how fade-in and fade-out combine.
/// Fade-in only ever needed "how long has this file been playing", which
/// <see cref="CurrentPosition"/> already tracks precisely — no lookup required. Fade-out needs the
/// file's total duration in advance (to know when the final fade window begins), which is why it
/// was deferred a round past fade-in: see <see cref="AudioDecodeSource.TryGetDuration"/>'s own doc
/// comment for the considerable, explicitly flagged risk in that one call (the least-verified Media
/// Foundation call in this whole codebase, going through <c>dynamic</c> specifically because of
/// that) — a wrong guess there degrades to "no fade-out for this file" rather than breaking
/// anything else.
///
/// <see cref="TrySeekTo"/> implements PLANNING.md §8.2's audio play-bar "进度" — see its own doc
/// comment for why seeking has to rebuild <see cref="_audioClock"/> rather than just repositioning
/// <see cref="_source"/>, and for <see cref="AudioDecodeSource.TrySeek"/>'s own doc comment on why
/// this is, if anything, an even less-verified Media Foundation call than
/// <see cref="AudioDecodeSource.TryGetDuration"/>.
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
    // call fails for any reason (see its own doc comment for how thoroughly unverified it is).
    // Queried unconditionally now (not just when a fade was requested, as in the previous round):
    // TrySeekTo/CurrentPosition/TotalDuration need it too, for the audio play bar's position
    // readout and to know a sensible upper bound isn't strictly required (seeking past the real
    // end just reaches end-of-stream naturally, see TrySeekTo's own doc comment) but is still nice
    // to show. A null value here silently degrades this file to fade-in-only and an
    // unknown-duration position readout — exactly the graceful behavior this class already had
    // before either feature could query real duration at all.
    private TimeSpan? _totalDuration;

    // Ticks the current AudioPlaybackClock's own PositionTicks needs added to it to get "position
    // within the file" — 0 for a fresh Play(), the seek target for whatever AudioPlaybackClock
    // TrySeekTo most recently built. See TrySeekTo's own doc comment for why a seek can't just let
    // the existing clock keep counting: WASAPI's own hardware position counter only ever counts
    // forward from 0 for a given AudioPlaybackClock instance, so a seek needs a fresh one, and this
    // field is what makes "current position in the file" and "current clock's own counter" the
    // same only when this is 0 (immediately after Play(), never after a TrySeekTo()).
    private long _seekBaseTicks;

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

    /// <summary>Current position within the file, accounting for any <see cref="TrySeekTo"/> calls
    /// since <see cref="Play"/> started — see <see cref="_seekBaseTicks"/>'s own doc comment for why
    /// this isn't simply <see cref="AudioPlaybackClock.PositionTicks"/>. <see cref="TimeSpan.Zero"/>
    /// if nothing is currently playing.</summary>
    public TimeSpan CurrentPosition =>
        _audioClock == null ? TimeSpan.Zero : TimeSpan.FromTicks(_seekBaseTicks + _audioClock.PositionTicks);

    /// <summary>Best-effort total duration of the current file — null if nothing is playing, or if
    /// <see cref="AudioDecodeSource.TryGetDuration"/> failed for it (see that method's own doc
    /// comment for how thoroughly unverified it is). For PLANNING.md §8.2's audio play-bar position
    /// readout ("00:15 / 03:42"-style); a null value here just means the readout shows position
    /// alone, with no "/ total" suffix.</summary>
    public TimeSpan? TotalDuration => _totalDuration;

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
    /// <see cref="ComputeFadeMultiplier"/> for where both ramps are actually computed.</summary>
    public void Play(string path, TimeSpan? fadeDuration = null)
    {
        Stop();

        var source = new AudioDecodeSource(path);
        var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
        _fadeDuration = fadeDuration is { Ticks: > 0 } ? fadeDuration : null;
        // A zero-or-negative fade has nothing to ramp over, so it's treated as "no fade in/out"
        // rather than risking a divide-by-zero in ComputeFadeMultiplier.
        _totalDuration = source.TryGetDuration();
        _seekBaseTicks = 0;
        audioClock.Volume = _fadeDuration.HasValue ? 0f : _volume; // see _volume's own doc comment — a fresh clock otherwise starts at full volume regardless of what was set for the previous file.
        _source = source;
        _audioClock = audioClock;

        StartPlaybackThread(source, audioClock);
    }

    /// <summary>Best-effort "跳转到指定位置" (PLANNING.md §8.2's "进度") — returns false (no-op) if
    /// nothing is currently playing, or if <see cref="AudioDecodeSource.TrySeek"/> itself fails (see
    /// that method's own doc comment on why this is, if anything, an even less-verified Media
    /// Foundation call than <see cref="AudioDecodeSource.TryGetDuration"/>). <paramref
    /// name="position"/> is clamped to non-negative; there is no upper clamp against
    /// <see cref="_totalDuration"/> even when known — seeking past the real end of the file just
    /// makes the very next <see cref="AudioDecodeSource.ReadNextChunk"/> report end-of-stream almost
    /// immediately, which <see cref="RunPlaybackLoopCore"/> already handles correctly as ordinary
    /// completion, so there is no failure mode an explicit clamp would actually be preventing.
    ///
    /// Unlike <see cref="Play"/>, this does NOT tear down and reopen <see cref="_source"/> — it
    /// seeks the SAME already-open decode source in place, then only rebuilds
    /// <see cref="_audioClock"/> (a fresh <see cref="AudioPlaybackClock"/>/WASAPI output) and the
    /// background playback thread around it. This is necessary, not just an optimization:
    /// <see cref="AudioPlaybackClock.PositionTicks"/> is WASAPI's own hardware-rendered-bytes
    /// counter for THIS clock instance — it only ever counts forward from 0 as audio is actually
    /// rendered, with no way to jump it backward or forward. Reusing the old clock after seeking the
    /// decode source would mean <see cref="RunPlaybackLoopCore"/>'s own pacing wait (comparing
    /// <see cref="AudioPlaybackClock.PositionTicks"/> against the next chunk's, now much
    /// larger-or-smaller, timestamp) would either spin silently for however long the seek jumped, or
    /// immediately dump a large batch of chunks — audibly broken either way. A fresh clock starts
    /// its own position counter over from 0, so <see cref="_seekBaseTicks"/> tracks the offset
    /// needed to convert "this clock's own position" back into "position within the file", which
    /// <see cref="CurrentPosition"/> and <see cref="ComputeFadeMultiplier"/> both need.
    ///
    /// Does not preserve pause state on its own — a paused <see cref="AudioPlaybackClock"/> that
    /// gets replaced here is simply gone, and the new one defaults to playing.
    /// <c>PlaybackEngine</c>'s own seek-triggering method is responsible for re-calling
    /// <see cref="Pause"/> immediately afterward if the caller was paused before seeking, since only
    /// <c>PlaybackEngine</c> (via its own <c>IsPaused</c>) actually tracks that state — this class
    /// never has.</summary>
    public bool TrySeekTo(TimeSpan position)
    {
        if (_source == null || _audioClock == null) return false;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (!_source.TrySeek(position)) return false;

        StopPlaybackThreadAndClock();

        _seekBaseTicks = position.Ticks;
        var audioClock = new AudioPlaybackClock(_source.AudioSampleRate, _source.AudioChannels);
        // Computed up front (rather than left at some default and corrected on the next loop
        // iteration, tens of milliseconds later) so a seek landing inside the fade-in/fade-out
        // window doesn't produce a brief, audible full-volume blip before the first real chunk
        // corrects it.
        audioClock.Volume = (float)(_volume * ComputeFadeMultiplier(_seekBaseTicks));
        _audioClock = audioClock;

        StartPlaybackThread(_source, audioClock);
        return true;
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
        StopPlaybackThreadAndClock();
        _source?.Dispose();
        _source = null;
        _seekBaseTicks = 0;
    }

    /// <summary>Shared teardown between <see cref="Stop"/> and <see cref="TrySeekTo"/> — the latter
    /// deliberately stops here, not at <see cref="Stop"/>, since it must NOT dispose
    /// <see cref="_source"/> (already seeked in place and still needed by the new playback thread it
    /// is about to start).</summary>
    private void StopPlaybackThreadAndClock()
    {
        _playbackCts?.Cancel();
        _playbackThread?.Join();
        _playbackThread = null;
        _playbackCts?.Dispose();
        _playbackCts = null;

        _audioClock?.Dispose();
        _audioClock = null;
    }

    /// <summary>Shared startup between <see cref="Play"/> and <see cref="TrySeekTo"/> — assumes the
    /// caller has already assigned <see cref="_audioClock"/>/<see cref="_source"/> (or, for
    /// <see cref="TrySeekTo"/>, kept the existing <see cref="_source"/>) and set
    /// <see cref="_seekBaseTicks"/> appropriately.</summary>
    private void StartPlaybackThread(AudioDecodeSource source, AudioPlaybackClock audioClock)
    {
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

    /// <summary>The fade ramp is applied here, once per chunk, rather than on a timer: this loop
    /// already runs roughly once per chunk duration (tens of milliseconds), which is frequent
    /// enough for a linear volume ramp to sound smooth, and piggybacking on an already-running loop
    /// avoids adding a second timer/thread just for this. See <see cref="ComputeFadeMultiplier"/>
    /// for the math and why <see cref="CurrentPosition"/> (not wall-clock elapsed time) is the right
    /// time base — it keeps the ramp correct across a <see cref="Pause"/>/<see cref="Resume"/> and a
    /// <see cref="TrySeekTo"/> alike.</summary>
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
                audioClock.Volume = (float)(_volume * ComputeFadeMultiplier(_seekBaseTicks + audioClock.PositionTicks));

            audioClock.Enqueue(chunk.Value.Pcm);
            LevelChanged?.Invoke(ComputePeakLevel(chunk.Value.Pcm));
        }
    }

    /// <summary>Combined fade-in/fade-out volume multiplier (before <see cref="_volume"/> itself is
    /// applied) for a given position within the file — shared by <see cref="RunPlaybackLoopCore"/>'s
    /// per-chunk update and <see cref="TrySeekTo"/>'s own upfront one, so a seek's very first chunk
    /// and every chunk after it compute this identically. Only meaningful when
    /// <see cref="_fadeDuration"/> is set — callers already guard that themselves rather than this
    /// method returning some default for "no fade requested", since that default's correct value
    /// (1.0, i.e. no suppression) would be indistinguishable from "the fade windows just don't reach
    /// this far" without a caller-side check anyway.
    ///
    /// The two ramps combine as <c>min(fadeInMultiplier, fadeOutMultiplier)</c>, not a sum or an
    /// either/or switch — this is what correctly handles a file shorter than
    /// <c>2 * _fadeDuration</c> (the fade-in and fade-out windows overlap): volume never reaches
    /// full even briefly in the middle, which is the audibly correct behavior a real fade
    /// implementation needs, not an edge case this round is skipping. <c>fadeOutMultiplier</c>
    /// itself defaults to 1 (no suppression at all) whenever <see cref="_totalDuration"/> is null —
    /// the same "silently fade-in-only" degradation that field's own doc comment describes.</summary>
    private double ComputeFadeMultiplier(long positionTicks)
    {
        double fadeInMultiplier = Math.Clamp(positionTicks / (double)_fadeDuration!.Value.Ticks, 0.0, 1.0);

        double fadeOutMultiplier = 1.0;
        if (_totalDuration.HasValue)
        {
            long remainingTicks = _totalDuration.Value.Ticks - positionTicks;
            fadeOutMultiplier = Math.Clamp(remainingTicks / (double)_fadeDuration.Value.Ticks, 0.0, 1.0);
        }

        return Math.Min(fadeInMultiplier, fadeOutMultiplier);
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
