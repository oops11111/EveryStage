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
}
