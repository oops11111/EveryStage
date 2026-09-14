using System.Diagnostics;
using System.Drawing;
using EveryStage.Rendering;
using EveryStage.Rendering.Audio;
using EveryStage.Rendering.Decode;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Video playback for the Content Engine (PLANNING.md §3), built on the D3D11/Media Foundation
/// pipeline validated in Phase 0 (<c>EveryStage.Rendering</c>). Deliberately NOT an
/// <see cref="IContentRenderer"/>: that interface hands callers a <see cref="Bitmap"/> to draw via
/// GDI+, which is exactly the CPU round-trip the zero-copy pipeline exists to avoid. Instead this
/// owns its own D3D11 swap chain attached directly to the overlay window's HWND — the same
/// approach <c>ZeroCopyRenderDemo</c> validates, just wrapped as a start/stop-able component
/// instead of a one-shot CLI loop.
///
/// The swap chain and D3D11 device are created once and kept for this controller's lifetime
/// (matching the "断不销毁窗口/SwapChain" principle from §9.2 — the D3D11 device here plays the
/// same role for video that the overlay window itself plays for the picture as a whole);
/// <see cref="Play"/> only swaps out the decode source underneath it.
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

    private readonly D3D11Device _gpu;
    private readonly object _presenterLock = new();
    private readonly SwapChainPresenter _presenter;

    private CancellationTokenSource? _playbackCts;
    private Thread? _playbackThread;
    private VideoDecodeSource? _source;
    private AudioPlaybackClock? _audioClock;

    /// <summary>Raised (from the background playback thread — marshal to the UI thread if the
    /// handler touches UI) when both the video and audio streams reach end-of-stream.</summary>
    public event Action? PlaybackCompleted;

    public VideoContentController(IntPtr hostHandle, Size initialSize)
    {
        _gpu = new D3D11Device();
        _presenter = new SwapChainPresenter(_gpu, hostHandle, initialSize.Width, initialSize.Height);
    }

    /// <summary>Call when the host window's size changes (e.g. after an <c>OverlayWindow.Rebind</c>
    /// to a different-resolution monitor).</summary>
    public void Resize(int width, int height)
    {
        lock (_presenterLock) _presenter.Resize(width, height);
    }

    /// <summary>Stops whatever is currently playing (if anything) and starts <paramref
    /// name="path"/> from the beginning.</summary>
    public void Play(string path)
    {
        Stop();

        var source = new VideoDecodeSource(path, _gpu);
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

                lock (_presenterLock)
                {
                    _presenter.PresentFrame(frame.Value.Texture, frame.Value.ArraySlice, frame.Value.Width, frame.Value.Height, vsync: false);
                }
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
        Stop();
        _presenter.Dispose();
        _gpu.Dispose();
    }
}
