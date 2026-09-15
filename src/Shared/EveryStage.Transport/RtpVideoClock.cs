namespace EveryStage.Transport;

/// <summary>RFC 6184 mandates a 90kHz clock rate for the H.264 RTP payload format — every NAL unit
/// belonging to the same encoded frame must carry the same RTP timestamp, derived from this clock,
/// regardless of wall-clock send time.</summary>
public static class RtpVideoClock
{
    public const uint ClockRate = 90000;

    public static uint FromElapsed(TimeSpan elapsed)
    {
        // Wraps naturally at ~13.25 hours of elapsed time (2^32 / 90000 seconds) into a 32-bit RTP
        // timestamp — by design; RFC 3550 timestamps are expected to wrap and receivers are
        // supposed to handle that via modular arithmetic, not treat it as an error.
        double ticks = elapsed.TotalSeconds * ClockRate;
        return unchecked((uint)(ulong)ticks);
    }
}
