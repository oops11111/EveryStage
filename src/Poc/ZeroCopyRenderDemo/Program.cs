using System.Diagnostics;
using EveryStage.Poc.ZeroCopyRenderDemo.Diagnostics;
using EveryStage.Rendering;
using EveryStage.Rendering.Audio;
using EveryStage.Rendering.Decode;

namespace EveryStage.Poc.ZeroCopyRenderDemo;

/// <summary>
/// Phase 0 technical validation demo (PLANNING.md §4.4 / §15): local file -> DXVA hardware
/// decode -> D3D11 texture -> DXGI swap chain, with an audio-clock-paced video schedule and
/// instrumentation for the acceptance thresholds (latency, drops, A/V skew, memory over time).
/// Deliberately excludes capture/encode/network transport — see PLANNING.md §4.4 scope note.
/// </summary>
internal static class Program
{
    // One 30fps-equivalent frame interval; used both as the "how far ahead can we safely wait"
    // pacing budget and the "how far behind before we give up and drop" threshold. A real
    // scheduler would derive this from the source's actual frame rate instead of a fixed guess.
    private static readonly long FrameBudgetTicks = TimeSpan.TicksPerSecond / 30;

    [STAThread]
    private static void Main(string[] args)
    {
        string inputPath = GetArg(args, "--input") ?? throw new ArgumentException(
            "Usage: ZeroCopyRenderDemo --input <video file> [--metrics-dir <dir>] [--minutes <n>]");
        string metricsDir = GetArg(args, "--metrics-dir") ?? Path.Combine(AppContext.BaseDirectory, "metrics");
        double? soakMinutes = double.TryParse(GetArg(args, "--minutes"), out var m) ? m : null;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var window = new RenderWindow("EveryStage Phase 0 — Zero-Copy Render Demo", 1280, 720);
        window.Show();

        using var gpu = new D3D11Device();
        using var source = new VideoDecodeSource(inputPath, gpu);
        using var presenter = new SwapChainPresenter(gpu, window.Handle, window.ClientSize.Width, window.ClientSize.Height);
        window.SizeChangedPx += (w, h) => presenter.Resize(w, h);

        using var audioClock = new AudioPlaybackClock(source.AudioSampleRate, source.AudioChannels);
        using var metrics = new MetricsLogger();

        Console.WriteLine($"[demo] source: {source.VideoWidth}x{source.VideoHeight}, {source.AudioSampleRate}Hz/{source.AudioChannels}ch");
        Console.WriteLine(soakMinutes is { } min
            ? $"[demo] soak mode: looping input for {min:F1} minute(s) to exercise the memory-leak criterion."
            : "[demo] single pass (pass --minutes N for a longer soak run).");

        audioClock.Start();
        RunPipeline(window, source, audioClock, presenter, metrics, soakMinutes);

        metrics.WriteReport(metricsDir);
        Console.WriteLine($"[demo] metrics written to {metricsDir}");
    }

    private static void RunPipeline(
        RenderWindow window,
        VideoDecodeSource source,
        AudioPlaybackClock audioClock,
        SwapChainPresenter presenter,
        MetricsLogger metrics,
        double? soakMinutes)
    {
        var runClock = Stopwatch.StartNew();
        var frameStopwatch = Stopwatch.StartNew();
        int frameIndex = 0;
        bool audioDone = false, videoDone = false;

        while (!window.IsDisposed && window.Visible)
        {
            Application.DoEvents();

            if (soakMinutes is { } min && runClock.Elapsed.TotalMinutes >= min)
                break;

            // Loop playback for soak testing rather than exiting at end-of-stream, so the same
            // file can drive the 1-hour memory-leak check called for in PLANNING.md §4.4.
            if (audioDone && videoDone)
            {
                if (soakMinutes is null) break;
                audioDone = videoDone = false;
                continue;
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

            // Pace to the audio master clock: don't present a frame before its timestamp is due.
            while (audioClock.PositionTicks < frame.Value.TimestampTicks - FrameBudgetTicks)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }

            long behindByTicks = audioClock.PositionTicks - frame.Value.TimestampTicks;
            try
            {
                if (behindByTicks > FrameBudgetTicks)
                {
                    // We've fallen more than one frame behind the audio clock — drop instead of
                    // presenting stale video, matching the "帧率无掉帧" criterion's intent: this
                    // counter should stay at zero on a healthy run, and a nonzero value here is a
                    // real finding, not noise to suppress.
                    metrics.RecordDroppedFrame();
                    continue;
                }

                presenter.PresentFrame(frame.Value.Texture, frame.Value.ArraySlice, frame.Value.Width, frame.Value.Height, vsync: false);

                var latency = frameStopwatch.Elapsed - decodeStartedAt;
                double avSkewMs = behindByTicks / (double)TimeSpan.TicksPerMillisecond;
                metrics.RecordPresentedFrame(frameIndex++, latency, avSkewMs);
            }
            finally
            {
                // The texture is a QueryInterface'd reference into the decoder's own texture-array
                // pool (see VideoDecodeSource.ReadNextVideoFrame), not a fresh allocation — release
                // the COM ref every iteration (present or drop) or the pool never recycles slots,
                // which would show up as exactly the kind of leak the 1-hour soak test is for.
                frame.Value.Texture.Dispose();
            }
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
