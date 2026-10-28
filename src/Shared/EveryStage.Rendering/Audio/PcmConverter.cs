namespace EveryStage.Rendering.Audio;

/// <summary>
/// Converts interleaved 16-bit PCM from whatever the decoder currently delivers to the fixed format the
/// playback clock was opened with. Media Foundation may change the decoded format at any sample — it
/// reports <c>MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED</c> with the first sample in the new format (the
/// Dolby AC-3 decoder announces 2ch/44.1 kHz, then delivers 6 channels from the very first sample; HE-AAC
/// can double its rate once SBR is detected). Playing that through a clock opened for the old format
/// plays noise at the wrong speed (audit A-25), so the decode sources run every chunk through this.
/// </summary>
public static class PcmConverter
{
    /// <summary>Returns <paramref name="pcm"/> unchanged when the formats already match.
    /// <paramref name="outChannels"/> is 1 or 2 (what <see cref="PcmDownmix.OutputChannels"/> yields).</summary>
    public static byte[] Convert(byte[] pcm, int inChannels, int inRate, int outChannels, int outRate)
    {
        if (inChannels <= 0 || outChannels <= 0) return pcm;
        byte[] data = pcm;
        if (inChannels > 2)
        {
            data = PcmDownmix.ToStereo(data, inChannels);
            inChannels = 2;
        }
        if (inChannels == 1 && outChannels == 2) data = MonoToStereo(data);
        else if (inChannels == 2 && outChannels == 1) data = StereoToMono(data);
        if (inRate > 0 && outRate > 0 && inRate != outRate) data = Resample(data, outChannels, inRate, outRate);
        return data;
    }

    private static byte[] MonoToStereo(byte[] pcm)
    {
        int frames = pcm.Length / 2;
        var output = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            output[i * 4] = output[i * 4 + 2] = pcm[i * 2];
            output[i * 4 + 1] = output[i * 4 + 3] = pcm[i * 2 + 1];
        }
        return output;
    }

    private static byte[] StereoToMono(byte[] pcm)
    {
        int frames = pcm.Length / 4;
        var output = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            int sum = (short)(pcm[i * 4] | (pcm[i * 4 + 1] << 8)) + (short)(pcm[i * 4 + 2] | (pcm[i * 4 + 3] << 8));
            short mono = (short)(sum / 2);
            output[i * 2] = (byte)mono;
            output[i * 2 + 1] = (byte)(mono >> 8);
        }
        return output;
    }

    /// <summary>Linear interpolation per chunk. A mid-stream rate change is rare (HE-AAC SBR, a spliced
    /// file); this keeps duration and pitch right, which is what matters for sync — not audiophile quality.</summary>
    internal static byte[] Resample(byte[] pcm, int channels, int inRate, int outRate)
    {
        int block = channels * 2;
        int inFrames = pcm.Length / block;
        if (inFrames == 0) return Array.Empty<byte>();
        int outFrames = (int)Math.Round(inFrames * (double)outRate / inRate);
        var output = new byte[outFrames * block];
        double step = (double)inRate / outRate;
        for (int o = 0; o < outFrames; o++)
        {
            double position = o * step;
            int i0 = Math.Min((int)position, inFrames - 1);
            int i1 = Math.Min(i0 + 1, inFrames - 1);
            double fraction = position - i0;
            for (int c = 0; c < channels; c++)
            {
                short a = (short)(pcm[i0 * block + c * 2] | (pcm[i0 * block + c * 2 + 1] << 8));
                short b = (short)(pcm[i1 * block + c * 2] | (pcm[i1 * block + c * 2 + 1] << 8));
                short value = (short)Math.Round(a + (b - a) * fraction);
                output[o * block + c * 2] = (byte)value;
                output[o * block + c * 2 + 1] = (byte)(value >> 8);
            }
        }
        return output;
    }
}
