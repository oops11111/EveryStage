using System.Diagnostics;
using EveryStage.Rendering;

namespace EveryStage.Caster.Capture;

/// <summary>
/// Drives <see cref="ScreenCaptureSource"/> on a background loop purely to prove it works — no
/// encoding, no network send, nothing leaves this process. This exists because PLANNING.md's
/// capture/encode/transport pipeline (§15's "第二大技术风险区") needs its own validation the same
/// way the D3D11/Media Foundation decode side got one in the Phase 0 demo, and this repo had no
/// screen-capture code at all before this pass. See this project's README for why it stops at
/// capture and doesn't attempt encoding or transport in the same pass.
/// </summary>
public sealed class CaptureSelfTestRunner : IDisposable
{
    private D3D11Device? _gpu;
    private ScreenCaptureSource? _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Written by the background capture loop, read by the UI thread (typically on a polling
    // Timer) with no lock between them. Deliberately left unsynchronized: these are diagnostic
    // stats for a self-test, not values anything correctness-sensitive depends on, and a
    // once-in-a-while stale read (the UI showing last tick's number instead of this tick's) costs
    // nothing here — not worth the complexity of synchronizing every field for a debug display.
    public bool IsRunning => _loopTask != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FrameCount { get; private set; }
    public double Fps { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised from the background capture loop — marshal to the UI thread before touching
    /// UI.</summary>
    public event Action? StatsUpdated;

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        FrameCount = 0;
        Fps = 0;

        var gpu = new D3D11Device();
        ScreenCaptureSource capture;
        try
        {
            capture = new ScreenCaptureSource(gpu);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            gpu.Dispose();
            StatsUpdated?.Invoke();
            return;
        }

        _gpu = gpu;
        _capture = capture;
        Width = capture.Width;
        Height = capture.Height;

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoop(capture, _cts.Token));
    }

    private void RunLoop(ScreenCaptureSource capture, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastFpsUpdate = stopwatch.Elapsed;
        int framesSinceLastFpsUpdate = 0;

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
                    // Expected, real DDA failure mode (see the exception's own doc comment) — this
                    // loop just stops; Stop() then Start() again builds a fresh ScreenCaptureSource.
                    LastError = ex.Message;
                    break;
                }

                if (frame == null) continue; // normal timeout — screen genuinely hasn't changed.

                using (frame)
                {
                    if (frame.HasNewImage)
                    {
                        FrameCount++;
                        framesSinceLastFpsUpdate++;
                    }
                }

                var elapsedSinceFpsUpdate = stopwatch.Elapsed - lastFpsUpdate;
                if (elapsedSinceFpsUpdate.TotalSeconds >= 1)
                {
                    Fps = framesSinceLastFpsUpdate / elapsedSinceFpsUpdate.TotalSeconds;
                    framesSinceLastFpsUpdate = 0;
                    lastFpsUpdate = stopwatch.Elapsed;
                    StatsUpdated?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            // Anything else unexpected: surface it in LastError rather than let the background
            // thread die silently with no trace, same reasoning as VideoContentController's
            // PlaybackFailed event on the Terminal side.
            LastError = ex.Message;
        }
        finally
        {
            StatsUpdated?.Invoke();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;

        _capture?.Dispose();
        _capture = null;
        _gpu?.Dispose();
        _gpu = null;
    }

    public void Dispose() => Stop();
}
