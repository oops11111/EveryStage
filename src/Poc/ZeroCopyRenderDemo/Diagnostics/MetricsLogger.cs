using System.Diagnostics;
using System.Globalization;

namespace EveryStage.Poc.ZeroCopyRenderDemo.Diagnostics;

/// <summary>
/// Captures the numbers PLANNING.md §4.4 lists as the Phase-0 demo's acceptance criteria:
///   - 端到端延迟 ≤30-50ms (decode timestamp -> actual Present call, per frame)
///   - 帧率无掉帧 (dropped-frame count under CBR-equivalent test playback)
///   - 音画偏差 ≤1帧 (audio-clock vs. video-timestamp skew at present time)
///   - 连续播放1小时无内存泄漏 (periodic working-set/GC-heap samples to plot a trend line)
/// This only measures the local decode→render pipeline; it does not include network capture or
/// encode, matching the demo's explicit non-goals in PLANNING.md §4.4.
/// </summary>
public sealed class MetricsLogger : IDisposable
{
    private readonly record struct FrameSample(int FrameIndex, double LatencyMs, double AvSkewMs);
    private readonly record struct MemorySample(TimeSpan Elapsed, long WorkingSetBytes, long GcHeapBytes);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<FrameSample> _frames = new();
    private readonly List<MemorySample> _memorySamples = new();
    private int _droppedFrames;
    private DateTime _lastMemorySampleAt = DateTime.MinValue;

    /// <param name="decodeToPresentLatency">
    /// Wall-clock elapsed between the caller's <c>ReadNextVideoFrame()</c> call returning and its
    /// matching <c>PresentFrame()</c> call returning — this is the end-to-end pipeline latency
    /// PLANNING.md §4.4 wants bounded to 30-50ms. Caller measures this with its own Stopwatch
    /// rather than logger-side, since MF sample timestamps are stream-time, not wall-clock time,
    /// and can't be diffed against a process Stopwatch directly.
    /// </param>
    /// <param name="avSkewMs">
    /// |audio playback position - this frame's MF presentation timestamp|, both in stream-time,
    /// at the moment this frame was presented. Comparable because both streams share one
    /// zero-based presentation timeline from the same source file.
    /// </param>
    public void RecordPresentedFrame(int frameIndex, TimeSpan decodeToPresentLatency, double avSkewMs)
    {
        _frames.Add(new FrameSample(frameIndex, decodeToPresentLatency.TotalMilliseconds, Math.Abs(avSkewMs)));
        MaybeSampleMemory();
    }

    public void RecordDroppedFrame() => _droppedFrames++;

    private void MaybeSampleMemory()
    {
        var now = DateTime.UtcNow;
        if (now - _lastMemorySampleAt < TimeSpan.FromSeconds(10)) return;
        _lastMemorySampleAt = now;

        using var process = Process.GetCurrentProcess();
        _memorySamples.Add(new MemorySample(_clock.Elapsed, process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false)));
    }

    /// <summary>Writes per-frame + memory-trend CSVs and prints a pass/fail summary against the
    /// PLANNING.md §4.4 thresholds. Call once at shutdown.</summary>
    public void WriteReport(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var framesCsv = Path.Combine(outputDirectory, "frames.csv");
        using (var writer = new StreamWriter(framesCsv))
        {
            writer.WriteLine("frame_index,latency_ms,av_skew_ms");
            foreach (var f in _frames)
                writer.WriteLine($"{f.FrameIndex},{f.LatencyMs.ToString("F2", CultureInfo.InvariantCulture)},{f.AvSkewMs.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        var memoryCsv = Path.Combine(outputDirectory, "memory.csv");
        using (var writer = new StreamWriter(memoryCsv))
        {
            writer.WriteLine("elapsed_seconds,working_set_bytes,gc_heap_bytes");
            foreach (var m in _memorySamples)
                writer.WriteLine($"{m.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)},{m.WorkingSetBytes},{m.GcHeapBytes}");
        }

        PrintSummary();
    }

    private void PrintSummary()
    {
        if (_frames.Count == 0)
        {
            Console.WriteLine("[metrics] no frames recorded.");
            return;
        }

        var latencies = _frames.Select(f => f.LatencyMs).OrderBy(x => x).ToList();
        var skews = _frames.Select(f => f.AvSkewMs).OrderBy(x => x).ToList();
        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);
        double maxLatency = latencies[^1];
        double maxSkew = skews[^1];

        long memoryGrowth = _memorySamples.Count >= 2
            ? _memorySamples[^1].WorkingSetBytes - _memorySamples[0].WorkingSetBytes
            : 0;

        Console.WriteLine("==== Phase 0 demo — acceptance summary (PLANNING.md §4.4) ====");
        Console.WriteLine($"frames presented   : {_frames.Count}");
        Console.WriteLine($"frames dropped     : {_droppedFrames}  (target: 0)");
        Console.WriteLine($"latency p50/p95/max: {p50:F1} / {p95:F1} / {maxLatency:F1} ms  (target: <= 30-50ms)");
        Console.WriteLine($"av skew max        : {maxSkew:F1} ms  (target: <= ~1 frame, e.g. 16.7ms@60fps / 33ms@30fps)");
        Console.WriteLine($"working-set growth : {memoryGrowth / 1024.0 / 1024.0:F1} MB over {_memorySamples.LastOrDefault().Elapsed.TotalMinutes:F1} min (watch for a non-flat trend over a 1h run)");
        Console.WriteLine("Full per-frame and memory-trend data written to frames.csv / memory.csv next to this report.");
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int index = (int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    public void Dispose() { }
}
