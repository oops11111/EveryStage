namespace EveryStage.Caster.Casting;

/// <summary>
/// Maps encoded audio frames back to the capture time of their first sample (audit A-3). The AAC
/// encoder buffers PCM across calls — 480-sample capture buffers in, 1024-sample frames out — so a frame
/// usually began in an earlier buffer than the one whose submission produced it; and loopback capture
/// delivers nothing while the machine is silent, so sample count and wall time drift apart across every
/// silent gap. Each captured buffer is recorded with the index of its first sample and the session-clock
/// time of that sample; a frame's timestamp is looked up from the buffer that contains its first sample.
/// Not thread-safe: used on the single capture callback thread.
/// </summary>
internal sealed class PcmTimeline
{
    private readonly Queue<(long FirstSample, long ClockTicks)> _buffers = new();
    private readonly int _sampleRate;
    private long _samplesRecorded;
    private long _samplesEmitted;

    public PcmTimeline(int sampleRate) => _sampleRate = sampleRate;

    /// <summary>A captured buffer of <paramref name="frames"/> sample frames whose first sample was
    /// captured at <paramref name="firstSampleClockTicks"/> (session clock, 100 ns).</summary>
    public void Record(int frames, long firstSampleClockTicks)
    {
        if (frames <= 0) return;
        _buffers.Enqueue((_samplesRecorded, firstSampleClockTicks));
        _samplesRecorded += frames;
    }

    /// <summary>Session-clock time of the next encoded frame's first sample; advances by
    /// <paramref name="frameSamples"/>. Null when nothing recorded covers it yet.</summary>
    public long? NextFrame(int frameSamples)
    {
        long sample = _samplesEmitted;
        _samplesEmitted += frameSamples;
        while (_buffers.Count > 1 && _buffers.ElementAt(1).FirstSample <= sample) _buffers.Dequeue();
        if (_buffers.Count == 0) return null;
        var (firstSample, clockTicks) = _buffers.Peek();
        return clockTicks + (sample - firstSample) * TimeSpan.TicksPerSecond / _sampleRate;
    }
}
