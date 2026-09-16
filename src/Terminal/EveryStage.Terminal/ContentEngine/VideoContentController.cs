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
    /// name="path"/> from the beginning.</summary>
    public void Play(string path)
    {
        Stop();

        var source = new VideoDecodeSource(path, _surface.Gpu);
        var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
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
                if (chunk == null) audioDone = true;
                else audioClock.Enqueue(chunk.Value.Pcm);
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
