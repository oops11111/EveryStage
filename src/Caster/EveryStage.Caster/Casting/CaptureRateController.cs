namespace EveryStage.Caster.Casting;

/// <summary>Small, deterministic congestion policy for the capture loop. It changes capture
/// cadence rather than pretending the hardware encoder can be reconfigured safely mid-stream.
/// Severe pressure takes effect immediately; recovery is deliberately hysteretic so one healthy
/// status report cannot make the sender oscillate back to full rate.</summary>
public sealed class CaptureRateController
{
    private const int MaxStride = 3;
    private int _stride = 1;
    private int _healthyUpdates;
    private int _lastEncoderDrops;

    public int CurrentStride => Volatile.Read(ref _stride);

    public void Reset()
    {
        Volatile.Write(ref _stride, 1);
        _healthyUpdates = 0;
        _lastEncoderDrops = 0;
    }

    public int Update(double? packetLossPercent, TimeSpan? roundTrip, string? videoError,
        int queuedAccessUnits, int maxQueuedAccessUnits, int encoderFramesDropped)
    {
        double loss = packetLossPercent is { } value && double.IsFinite(value) ? Math.Max(0, value) : 0;
        double rttMs = roundTrip is { } duration && duration >= TimeSpan.Zero
            ? duration.TotalMilliseconds : 0;
        double queueRatio = maxQueuedAccessUnits > 0
            ? Math.Clamp((double)queuedAccessUnits / maxQueuedAccessUnits, 0, 1) : 0;
        bool newEncoderDrops = encoderFramesDropped > _lastEncoderDrops;
        _lastEncoderDrops = Math.Max(_lastEncoderDrops, encoderFramesDropped);

        bool severe = videoError != null || loss >= 12 || rttMs >= 500 || queueRatio >= .75 || newEncoderDrops;
        bool moderate = loss >= 5 || rttMs >= 200 || queueRatio >= .35;
        int current = CurrentStride;
        if (severe)
        {
            _healthyUpdates = 0;
            Volatile.Write(ref _stride, MaxStride);
        }
        else if (moderate)
        {
            _healthyUpdates = 0;
            Volatile.Write(ref _stride, Math.Max(current, 2));
        }
        else if (++_healthyUpdates >= 3 && current > 1)
        {
            _healthyUpdates = 0;
            Volatile.Write(ref _stride, current - 1);
        }
        return CurrentStride;
    }
}
