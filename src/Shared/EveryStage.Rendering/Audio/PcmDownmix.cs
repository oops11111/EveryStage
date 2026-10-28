namespace EveryStage.Rendering.Audio;

/// <summary>
/// Folds multichannel 16-bit PCM (quad, 5.1, 6.1, 7.1…) down to stereo. Shared-mode WASAPI on a stereo
/// output rejects a 6-channel stream outright (audit B-20: <c>new AudioPlaybackClock(48000, 6)</c>
/// throws "Value does not fall within the expected range"), and decoders do not reliably honour a
/// request for 2 channels (the Dolby AC-3 decoder still delivers 6, audit A-25), so the playback
/// chains downmix themselves before audio reaches the clock.
///
/// Channel order is the WAVE default (FL FR FC LFE BL BR SL SR, per <c>WAVEFORMATEXTENSIBLE</c>);
/// centre and surrounds go in at -3 dB, a back-centre to both sides at -6 dB, LFE is dropped. The mix
/// is normalised by the larger per-side weight sum so it can never clip (the same choice ffmpeg's
/// resampler makes by default) — quieter than the original, never distorted.
/// </summary>
public static class PcmDownmix
{
    private const float Minus3dB = 0.70710677f;

    /// <summary>True when <paramref name="channels"/> needs folding before playback.</summary>
    public static bool IsNeeded(int channels) => channels > 2;

    /// <summary>Channel count the playback clock should be built with for a source of
    /// <paramref name="channels"/> channels.</summary>
    public static int OutputChannels(int channels) => IsNeeded(channels) ? 2 : channels;

    /// <summary>Stereo version of interleaved 16-bit little-endian PCM with <paramref name="channels"/>
    /// channels. Returns <paramref name="pcm"/> itself when no folding is needed; a trailing partial
    /// frame is dropped.</summary>
    public static byte[] ToStereo(byte[] pcm, int channels)
    {
        if (!IsNeeded(channels)) return pcm;
        var weights = Weights(channels);
        float left = 0, right = 0;
        foreach (var (l, r) in weights) { left += l; right += r; }
        float scale = 1f / Math.Max(1f, Math.Max(left, right));

        int frames = pcm.Length / (channels * 2);
        var output = new byte[frames * 4];
        for (int frame = 0; frame < frames; frame++)
        {
            int inBase = frame * channels * 2;
            float l = 0, r = 0;
            for (int c = 0; c < channels; c++)
            {
                short sample = (short)(pcm[inBase + c * 2] | (pcm[inBase + c * 2 + 1] << 8));
                l += sample * weights[c].Left;
                r += sample * weights[c].Right;
            }
            WriteSample(output, frame * 4, l * scale);
            WriteSample(output, frame * 4 + 2, r * scale);
        }
        return output;
    }

    private static void WriteSample(byte[] buffer, int offset, float value)
    {
        short s = (short)Math.Clamp((int)MathF.Round(value), short.MinValue, short.MaxValue);
        buffer[offset] = (byte)s;
        buffer[offset + 1] = (byte)(s >> 8);
    }

    /// <summary>(left, right) weight of each input channel, WAVE default order.</summary>
    internal static (float Left, float Right)[] Weights(int channels)
    {
        (float, float) FL = (1f, 0f), FR = (0f, 1f), FC = (Minus3dB, Minus3dB), LFE = (0f, 0f),
            SideL = (Minus3dB, 0f), SideR = (0f, Minus3dB), BC = (0.5f, 0.5f);
        return channels switch
        {
            3 => new[] { FL, FR, FC },
            4 => new[] { FL, FR, SideL, SideR },                      // quad: FL FR BL BR
            5 => new[] { FL, FR, FC, SideL, SideR },                  // FL FR FC BL BR
            6 => new[] { FL, FR, FC, LFE, SideL, SideR },             // 5.1
            7 => new[] { FL, FR, FC, LFE, BC, SideL, SideR },         // 6.1
            8 => new[] { FL, FR, FC, LFE, SideL, SideR, SideL, SideR }, // 7.1
            _ => Enumerable.Range(0, channels)
                .Select(i => i == 0 ? FL : i == 1 ? FR : i % 2 == 0 ? SideL : SideR).ToArray(),
        };
    }
}
