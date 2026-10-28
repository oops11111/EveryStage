using EveryStage.Rendering.Audio;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Bookkeeping for one <see cref="AudioPlaybackClock"/>: how much audio has been handed to it, and how
/// much silence it rendered on its own while it had nothing to play (an underrun — the decoder stalled on a
/// slow disk, a network share, an antivirus scan). The clock itself only counts rendered bytes, silence
/// included, so on its own it cannot tell "played content" from "played padding" (audit B-22): a stall made
/// an audio file's position run ahead permanently (and its fade-out end early), and after a gap in a
/// video's audio track the sound ran ahead of the picture for the rest of the file.
///
/// All values are on the clock's own timeline (100 ns ticks from 0 for this clock instance).
/// </summary>
internal sealed class AudioTimeline
{
    private readonly AudioPlaybackClock _clock;
    private readonly long _bytesPerSecond;
    private readonly object _gate = new();
    private long _enqueuedTicks;
    private long _silenceTicks;

    public AudioTimeline(AudioPlaybackClock clock, int sampleRate, int channels)
    {
        _clock = clock;
        _bytesPerSecond = (long)sampleRate * channels * 2;
    }

    /// <summary>Clock time at which the next enqueued byte will play.</summary>
    public long ScheduleTicks
    {
        get { lock (_gate) { Update(); return _enqueuedTicks; } }
    }

    /// <summary>Clock time of audio content handed over so far (underrun silence excluded).</summary>
    public long EnqueuedContentTicks
    {
        get { lock (_gate) { Update(); return _enqueuedTicks - _silenceTicks; } }
    }

    /// <summary>Content actually played so far: the clock's position minus the silence it rendered while
    /// starved.</summary>
    public long ContentTicks
    {
        get { lock (_gate) { long position = Update(); return Math.Max(0, position - _silenceTicks); } }
    }

    public void Enqueue(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            // Account for any starvation before this data: it is appended at the play head, not at the end
            // of what was handed over earlier.
            Update();
            _clock.Enqueue(pcm);
            _enqueuedTicks += pcm.Length * TimeSpan.TicksPerSecond / _bytesPerSecond;
        }
    }

    // A position beyond everything handed over means the device played padding for the difference.
    private long Update()
    {
        long position = _clock.PositionTicks;
        if (position > _enqueuedTicks)
        {
            _silenceTicks += position - _enqueuedTicks;
            _enqueuedTicks = position;
        }
        return position;
    }
}
