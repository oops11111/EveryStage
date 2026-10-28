namespace EveryStage.Transport;

/// <summary>Tracks the mapping between a sender's RTP timeline and the receiver's audio playback
/// timeline. Corrections are smoothed and rate-limited so clock drift is followed without turning
/// short-lived packet jitter into visible video jumps.
///
/// The rate limit only makes sense once the estimate is good. Right after the first sample (and after a
/// gap in the audio, when no samples arrived at all) a 1 ms-per-sample limit meant a 100 ms error took
/// about two seconds to work off — the first seconds of a cast, and every resumption after silence, were
/// the worst-synchronised moments (audit B-8 / B-9). So: the first sample after a gap of
/// <c>reseedAfterGap</c> sets the offset directly, and the following <c>warmupUpdates</c> samples converge
/// quickly before the slow, rate-limited tracking takes over.</summary>
public sealed class AvSyncOffsetEstimator
{
    private readonly object _gate = new();
    private readonly double _smoothingFactor;
    private readonly long _maxCorrectionPerUpdateTicks;
    private readonly int _warmupUpdates;
    private readonly long _reseedAfterGapMilliseconds;
    private bool _initialized;
    private double _offsetTicks;
    private int _updatesSinceSeed;
    private long _lastUpdateMilliseconds;

    private const double WarmupSmoothingFactor = 0.25;
    private static readonly long WarmupMaxCorrectionTicks = TimeSpan.FromMilliseconds(20).Ticks;

    public AvSyncOffsetEstimator(double smoothingFactor = 0.02, TimeSpan? maxCorrectionPerUpdate = null,
        int warmupUpdates = 50, TimeSpan? reseedAfterGap = null)
    {
        if (smoothingFactor <= 0 || smoothingFactor > 1) throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
        _smoothingFactor = smoothingFactor;
        _maxCorrectionPerUpdateTicks = (maxCorrectionPerUpdate ?? TimeSpan.FromMilliseconds(1)).Ticks;
        if (_maxCorrectionPerUpdateTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxCorrectionPerUpdate));
        if (warmupUpdates < 0) throw new ArgumentOutOfRangeException(nameof(warmupUpdates));
        _warmupUpdates = warmupUpdates;
        _reseedAfterGapMilliseconds = (long)(reseedAfterGap ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds;
    }

    public void Update(long remoteTimelineTicks, long localScheduledPlaybackTicks) =>
        Update(remoteTimelineTicks, localScheduledPlaybackTicks, Environment.TickCount64);

    /// <summary><paramref name="nowMilliseconds"/> is a monotonic clock (tests pass their own).</summary>
    public void Update(long remoteTimelineTicks, long localScheduledPlaybackTicks, long nowMilliseconds)
    {
        double sample = remoteTimelineTicks - localScheduledPlaybackTicks;
        lock (_gate)
        {
            bool gap = _initialized && nowMilliseconds - _lastUpdateMilliseconds > _reseedAfterGapMilliseconds;
            _lastUpdateMilliseconds = nowMilliseconds;
            if (!_initialized || gap)
            {
                _offsetTicks = sample;
                _initialized = true;
                _updatesSinceSeed = 0;
                return;
            }
            bool warmingUp = _updatesSinceSeed < _warmupUpdates;
            _updatesSinceSeed++;
            double factor = warmingUp ? Math.Max(_smoothingFactor, WarmupSmoothingFactor) : _smoothingFactor;
            long limit = warmingUp ? Math.Max(_maxCorrectionPerUpdateTicks, WarmupMaxCorrectionTicks) : _maxCorrectionPerUpdateTicks;
            double correction = Math.Clamp((sample - _offsetTicks) * factor, -limit, limit);
            _offsetTicks += correction;
        }
    }

    public bool TryGetOffset(out long offsetTicks)
    {
        lock (_gate)
        {
            offsetTicks = (long)_offsetTicks;
            return _initialized;
        }
    }
}
