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
/// presents through a <see cref="VideoSurface"/> attached directly to the overlay window's HWND —
/// the same approach <c>ZeroCopyRenderDemo</c> validates, just wrapped as a start/stop-able
/// component instead of a one-shot CLI loop.
///
/// The <see cref="VideoSurface"/> is owned by whoever constructs this controller (see that class's
/// doc comment for why: it's now shared with <c>Receiving.CastReceiver</c>, since only one of them
/// is ever supposed to be actively presenting at a time) — this class does not create or dispose
/// it, only presents through it while playing. <see cref="Play"/> only swaps out the decode source
/// underneath it.
///
/// Runs its decode/present loop on its own background thread rather than the WinForms UI thread,
/// since the Terminal's UI thread is busy running the tray icon / (eventually) the Phase 4 UI
/// message loop and can't be blocked in a tight pacing loop the way the Phase 0 demo's Program.cs
/// does on its dedicated process.
///
/// <see cref="Play"/>'s optional <c>fadeDuration</c> implements BOTH halves of PLANNING.md §6's
/// "淡入/淡出时长 + 音量是否随渐变" (<c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c>) for a
/// video's own soundtrack — see <see cref="AudioContentController"/>'s class doc comment for the
/// fade-out half's considerable, explicitly flagged risk (it depends on
/// <see cref="VideoDecodeSource.TryGetDuration"/>, the least-verified Media Foundation call in this
/// codebase). Unlike <see cref="AudioContentController"/>, this class has no persistent
/// <c>Volume</c> property to preserve across <see cref="Play"/> calls — nothing in the Terminal UI
/// currently exposes per-file video volume control, so both ramps' target ("full volume") is simply
/// <see cref="AudioPlaybackClock"/>'s own default of 1.0, not some remembered user setting.
/// </summary>
public sealed class VideoContentController : IDisposable
{
    // Same reasoning as the Phase 0 demo: a fixed 30fps-equivalent budget for "how far ahead to
    // wait" / "how far behind before dropping". A real implementation should derive this from the
    // source's actual frame rate.
    private static readonly long FrameBudgetTicks = TimeSpan.TicksPerSecond / 30;

    private readonly VideoSurface _surface;

    private CancellationTokenSource? _playbackCts;
    private Thread? _playbackThread;
    private VideoDecodeSource? _source;
    private AudioPlaybackClock? _audioClock;

    // See RunPlaybackLoopCore and the class doc comment — null means "no fade in/out for the
    // current file", set fresh by each Play() call. Same single-field-covers-both-directions
    // reasoning as AudioContentController's own _fadeDuration.
    private TimeSpan? _fadeDuration;

    // Set fresh by each Play() call, from VideoDecodeSource.TryGetDuration() — null whenever that
    // call fails (see its own doc comment) or fade-out wasn't requested. Same
    // silently-degrades-to-fade-in-only behavior as AudioContentController's own _totalDuration.
    private TimeSpan? _totalDuration;

    /// <summary>Raised (from the background playback thread — marshal to the UI thread if the
    /// handler touches UI) when both the video and audio streams reach end-of-stream.</summary>
    public event Action? PlaybackCompleted;

    /// <summary>Raised (same background-thread caveat as <see cref="PlaybackCompleted"/>) when the
    /// decode/present loop dies from an unhandled exception — a bad file, a decoder error, a lost
    /// GPU device, etc. Without this, that thread would just exit silently and the last frame would
    /// stay frozen on screen forever with no record of why.</summary>
    public event Action<Exception>? PlaybackFailed;

    public VideoContentController(VideoSurface surface)
    {
        _surface = surface;
    }

    /// <summary>Stops whatever is currently playing (if anything) and starts <paramref
    /// name="path"/> from the beginning. <paramref name="fadeDuration"/>, when given, ramps
    /// <see cref="_audioClock"/>'s volume from 0 up to full linearly over that span at the start,
    /// AND — best-effort, see <see cref="VideoDecodeSource.TryGetDuration"/>'s own doc comment —
    /// ramps back down to 0 over the same span at the end. See <see cref="RunPlaybackLoopCore"/>
    /// for where both ramps are actually applied.</summary>
    public void Play(string path, TimeSpan? fadeDuration = null)
    {
        Stop();

        var source = new VideoDecodeSource(path, _surface.Gpu);
        var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
        _fadeDuration = fadeDuration is { Ticks: > 0 } ? fadeDuration : null;
        // A zero-or-negative fade has nothing to ramp over, so it's treated as "no fade in/out"
        // rather than risking a divide-by-zero in RunPlaybackLoopCore's progress calculations.
        // TryGetDuration is only worth calling when a fade was actually requested — no point
        // risking its unverified `dynamic` call for a file that isn't going to fade either way.
        _totalDuration = _fadeDuration.HasValue ? source.TryGetDuration() : null;
        audioClock.Volume = _fadeDuration.HasValue ? 0f : 1f;
        _source = source;
        _audioClock = audioClock;

        var cts = new CancellationTokenSource();
        _playbackCts = cts;

        audioClock.Start();
        _playbackThread = new Thread(() => RunPlaybackLoop(source, audioClock, cts.Token))
        {
            IsBackground = true,
            Name = "EveryStage.VideoPlayback",
        };
        _playbackThread.Start();
    }

    /// <summary>Pauses in place — unlike <see cref="Stop"/> (which tears the decode source down
    /// entirely), this leaves <see cref="RunPlaybackLoopCore"/>'s thread, decode source, and the
    /// currently-decoded-but-not-yet-presented frame all exactly where they are. This works because
    /// that loop's pacing is driven entirely by <see cref="_audioClock"/>'s
    /// <see cref="AudioPlaybackClock.PositionTicks"/> (the spin-wait
    /// <c>while (... audioClock.PositionTicks &lt; frame.Value.TimestampTicks - FrameBudgetTicks) ...</c>):
    /// pausing the clock (real WASAPI pause, see <see cref="AudioPlaybackClock.Pause"/>'s own doc
    /// comment) freezes <c>PositionTicks</c> in place, which means that spin-wait simply never
    /// exits — no video frame gets presented, no next audio chunk gets read, and the swap chain
    /// naturally keeps displaying whatever it last presented (a swap chain shows its last frame
    /// until something calls <c>Present</c> again), all without this class needing to separately
    /// track or restore "where the decode was" the way tearing down and rebuilding
    /// <see cref="VideoDecodeSource"/> would require. <see cref="Resume"/> lets
    /// <c>PositionTicks</c> start advancing again from exactly where it was, and the same frame the
    /// loop was already waiting on gets presented once it catches up — no discontinuity, no
    /// re-decoding. No-op if nothing is currently playing (<see cref="_audioClock"/> null).</summary>
    public void Pause() => _audioClock?.Pause();

    /// <summary>Resumes playback paused by <see cref="Pause"/> from the exact position it left off —
    /// see that method's own doc comment. No-op if nothing is currently playing.</summary>
    public void Resume() => _audioClock?.Resume();

    /// <summary>Stops playback without tearing down the swap chain/device — mirrors "断" only
    /// affecting the overlay's visibility, not its underlying GPU resources.</summary>
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

    private void RunPlaybackLoop(VideoDecodeSource source, AudioPlaybackClock audioClock, CancellationToken token)
    {
        try
        {
            RunPlaybackLoopCore(source, audioClock, token);
        }
        catch (Exception ex)
        {
            // A decode error, a lost GPU device, a corrupt file — whatever it is, the thread must
            // not just vanish: PLANNING.md §14.4 explicitly wants abnormal interruptions recorded,
            // and silently freezing on the last frame with no signal anywhere would be worse than
            // reporting the error and stopping.
            PlaybackFailed?.Invoke(ex);
        }
    }

    /// <summary>See the class doc comment for the fade-in/fade-out scoping decision, and
    /// <see cref="AudioContentController.RunPlaybackLoopCore"/>'s own doc comment for the
    /// <c>min(fadeInMultiplier, fadeOutMultiplier)</c> combination this uses too. Both ramps are
    /// applied here, right before each audio chunk is enqueued, using
    /// <see cref="AudioPlaybackClock.PositionTicks"/> as their time base rather than
    /// <paramref name="audioClock"/>-independent wall-clock time — this keeps both ramps correct
    /// across a <see cref="Pause"/>/<see cref="Resume"/> instead of continuing to advance while
    /// audio isn't actually playing.</summary>
    private void RunPlaybackLoopCore(VideoDecodeSource source, AudioPlaybackClock audioClock, CancellationToken token)
    {
        var frameStopwatch = Stopwatch.StartNew();
        bool audioDone = false, videoDone = false;

        while (!token.IsCancellationRequested)
        {
            if (audioDone && videoDone)
            {
                PlaybackCompleted?.Invoke();
                return;
            }

            if (!audioDone)
            {
                var chunk = source.ReadNextAudioChunk();
                if (chunk == null)
                {
                    audioDone = true;
                }
                else
                {
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

                        audioClock.Volume = (float)Math.Min(fadeInMultiplier, fadeOutMultiplier);
                    }

                    audioClock.Enqueue(chunk.Value.Pcm);
                }
            }

            if (videoDone) continue;

            var decodeStartedAt = frameStopwatch.Elapsed;
            var frame = source.ReadNextVideoFrame();
            if (frame == null) { videoDone = true; continue; }

            while (!token.IsCancellationRequested && audioClock.PositionTicks < frame.Value.TimestampTicks - FrameBudgetTicks)
                Thread.Sleep(1);

            if (token.IsCancellationRequested)
            {
                frame.Value.Texture.Dispose();
                return;
            }

            long behindByTicks = audioClock.PositionTicks - frame.Value.TimestampTicks;
            try
            {
                if (behindByTicks > FrameBudgetTicks)
                    continue; // fell behind — drop this frame rather than present stale video.

                _surface.PresentFrame(frame.Value.Texture, frame.Value.ArraySlice, frame.Value.Width, frame.Value.Height, vsync: false);
            }
            finally
            {
                // Release the COM ref into the decoder's texture-array pool every iteration
                // (present or drop) — see EveryStage.Rendering's README for why this matters.
                frame.Value.Texture.Dispose();
            }
        }
    }

    public void Dispose()
    {
        // Does NOT dispose _surface — it's owned by whoever constructed this controller (see
        // VideoSurface's doc comment), since it's now shared with a live device cast's
        // CastReceiver rather than belonging exclusively to this controller.
        Stop();
    }
}
