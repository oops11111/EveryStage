namespace EveryStage.Transport;

/// <summary>RFC 6184 mandates a 90kHz clock rate for the H.264 RTP payload format — every NAL unit
/// belonging to the same encoded frame must carry the same RTP timestamp, derived from this clock,
/// regardless of wall-clock send time.
///
/// Also holds the generic form of the same elapsed-time/RTP-timestamp conversion
/// (<see cref="FromElapsed(TimeSpan, uint)"/>/<see cref="ToElapsedTicks"/>), despite the "Video" in
/// this class's name — <c>Caster.Casting.LiveCastSession</c> derives its audio stream's RTP
/// timestamps the same way, using the audio's own sample rate as the clock rate instead of 90000,
/// specifically so both streams' timestamps sit on the same "elapsed seconds since the cast
/// session started" timeline and <c>Terminal.Receiving.CastReceiver</c> can compare them for A/V
/// sync. Not worth a rename or a new file for one extra parameter on an existing method.</summary>
public static class RtpVideoClock
{
    public const uint ClockRate = 90000;

    public static uint FromElapsed(TimeSpan elapsed) => FromElapsed(elapsed, ClockRate);

    public static uint FromElapsed(TimeSpan elapsed, uint clockRate)
    {
        // Wraps naturally at (2^32 / clockRate) seconds elapsed (~13.25 hours at 90kHz) into a
        // 32-bit RTP timestamp — by design; RFC 3550 timestamps are expected to wrap and receivers
        // are supposed to handle that via modular arithmetic, not treat it as an error.
        double ticks = elapsed.TotalSeconds * clockRate;
        return unchecked((uint)(ulong)ticks);
    }

    /// <summary>Inverse of <see cref="FromElapsed(TimeSpan, uint)"/>: converts an RTP timestamp back
    /// into .NET 100ns ticks elapsed since whatever epoch the sender's clock started counting from.
    /// Does NOT detect or correct for the 32-bit wraparound <see cref="FromElapsed(TimeSpan, uint)"/>'s
    /// own doc comment describes — a timestamp from a session running longer than
    /// (2^32 / clockRate) seconds converts to a wrapped, too-small value indistinguishable from an
    /// early one. Acceptable for this project's live-cast sessions (measured in minutes/hours, not
    /// days) but worth remembering if that assumption ever stops holding.</summary>
    public static long ToElapsedTicks(uint rtpTimestamp, uint clockRate) =>
        (long)(rtpTimestamp / (double)clockRate * TimeSpan.TicksPerSecond);
}
