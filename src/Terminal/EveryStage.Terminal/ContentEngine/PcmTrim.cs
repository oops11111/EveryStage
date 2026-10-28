namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Decode-to-target support for seeking. Media Foundation's SetCurrentPosition lands on the key frame
/// (or, for some audio formats such as FLAC, the seek point) at or before the requested time — for a
/// clip with sparse key frames that can be the very start of the file. After a seek the controllers
/// therefore drop whole decoded audio chunks that end before the target and trim the leading samples
/// of the chunk that straddles it, so playback and the position readout both resume exactly at the
/// target instead of silently restarting earlier.
/// </summary>
internal static class PcmTrim
{
    /// <summary>Returns the part of a 16-bit PCM chunk at or after <paramref name="targetTicks"/> with its
    /// adjusted timestamp, or null when the whole chunk lies before the target.</summary>
    public static (byte[] Pcm, long TimestampTicks)? ToTarget(byte[] pcm, long timestampTicks, long targetTicks, int sampleRate, int channels)
    {
        if (targetTicks <= timestampTicks || sampleRate <= 0 || channels <= 0) return (pcm, timestampTicks);
        int block = channels * 2;
        long bytesPerSecond = (long)sampleRate * block;
        long skipBytes = (targetTicks - timestampTicks) * bytesPerSecond / TimeSpan.TicksPerSecond;
        skipBytes -= skipBytes % block;
        if (skipBytes >= pcm.Length) return null;
        if (skipBytes <= 0) return (pcm, timestampTicks);
        return (pcm[(int)skipBytes..], timestampTicks + skipBytes * TimeSpan.TicksPerSecond / bytesPerSecond);
    }

    // Timestamp jitter below this is normal (codec frame rounding) and left alone.
    private static readonly long AlignToleranceTicks = TimeSpan.FromMilliseconds(30).Ticks;

    // Larger gaps are not filled: the clock's buffer holds 5 s, and a jump that big is a broken
    // timestamp rather than real silence.
    private static readonly long MaxFilledGapTicks = TimeSpan.FromSeconds(4).Ticks;

    /// <summary>Lines a chunk up with <paramref name="scheduleTicks"/>, the time (same base as its timestamp)
    /// at which the next enqueued byte will play (audit B-22). A chunk that starts later — a gap in the
    /// track — gets the gap prepended as silence; one that starts earlier — an overlap, or audio arriving
    /// after its play time following a stall — loses the part that is already due. Null when nothing of
    /// the chunk is left.</summary>
    public static byte[]? AlignTo(byte[] pcm, long timestampTicks, long scheduleTicks, int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0) return pcm;
        long offset = timestampTicks - scheduleTicks;
        if (offset < -AlignToleranceTicks) return ToTarget(pcm, timestampTicks, scheduleTicks, sampleRate, channels)?.Pcm;
        if (offset <= AlignToleranceTicks || offset > MaxFilledGapTicks) return pcm;
        int block = channels * 2;
        long silenceBytes = offset * sampleRate * block / TimeSpan.TicksPerSecond;
        silenceBytes -= silenceBytes % block;
        var padded = new byte[silenceBytes + pcm.Length];
        Buffer.BlockCopy(pcm, 0, padded, (int)silenceBytes, pcm.Length);
        return padded;
    }
}
