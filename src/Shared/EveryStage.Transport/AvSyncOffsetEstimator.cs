namespace EveryStage.Transport;

/// <summary>Tracks the mapping between a sender's RTP timeline and the receiver's audio playback
/// timeline. Corrections are smoothed and rate-limited so clock drift is followed without turning
/// short-lived packet jitter into visible video jumps.</summary>
public sealed class AvSyncOffsetEstimator
{
    private readonly object _gate = new();
    private readonly double _smoothingFactor;
    private readonly long _maxCorrectionPerUpdateTicks;
    private bool _initialized;
    private double _offsetTicks;

    public AvSyncOffsetEstimator(double smoothingFactor = 0.02, TimeSpan? maxCorrectionPerUpdate = null)
    {
        if (smoothingFactor <= 0 || smoothingFactor > 1) throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
        _smoothingFactor = smoothingFactor;
        _maxCorrectionPerUpdateTicks = (maxCorrectionPerUpdate ?? TimeSpan.FromMilliseconds(1)).Ticks;
        if (_maxCorrectionPerUpdateTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxCorrectionPerUpdate));
    }

    public void Update(long remoteTimelineTicks, long localScheduledPlaybackTicks)
    {
        double sample = remoteTimelineTicks - localScheduledPlaybackTicks;
        lock (_gate)
        {
            if (!_initialized)
            {
                _offsetTicks = sample;
                _initialized = true;
                return;
            }
            double correction = (sample - _offsetTicks) * _smoothingFactor;
            correction = Math.Clamp(correction, -_maxCorrectionPerUpdateTicks, _maxCorrectionPerUpdateTicks);
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
