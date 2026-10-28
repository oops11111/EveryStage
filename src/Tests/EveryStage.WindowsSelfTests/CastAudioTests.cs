using EveryStage.Caster.Encode;
using EveryStage.Rendering.Decode;

/// <summary>
/// The cast audio path end to end without a network: the Caster's AAC encoder feeding the Terminal's AAC
/// decoder, exactly as LiveCastSession / CastReceiver wire them. Before audit A-26 / A-6 this could not
/// work on Windows 10: the decoder rejected its input type (missing MF_MT_USER_DATA, wrong payload-type
/// GUID), and its first output type is 32-bit float, which the 16-bit clock would have played as noise.
/// </summary>
internal static class CastAudioTests
{
    public static void Run()
    {
        const int rate = 48000, channels = 2, frequency = 440;
        AacAudioEncoder encoder;
        try
        {
            encoder = new AacAudioEncoder(rate, channels);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No matching Media Foundation transform"))
        {
            Console.WriteLine("SKIP: AAC encode/decode round trip (no AAC encoder MFT on this machine)");
            return;
        }
        catch (Exception ex)
        {
            // Anything else is a bug in how the encoder is set up, not a missing component.
            throw new InvalidOperationException("Cast audio tests failed: the AAC encoder could not be created: " + ex.Message
                + Environment.NewLine + ex.StackTrace, ex);
        }
        using (encoder)
        using (var decoder = new AacAudioDecoder(rate, channels))
        {
            static bool Provides(object owner, string field) => EveryStage.Rendering.MediaFoundationHelpers.ProvidesOutputSamples(
                (Vortice.MediaFoundation.IMFTransform)owner.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!);
            Console.WriteLine($"INFO: AAC encoder provides its output samples: {Provides(encoder, "_encoder")}; AAC decoder: {Provides(decoder, "_decoder")}");
            var accessUnits = new List<byte[]>();
            var decoded = new List<byte>();
            Exception? failure = null;
            encoder.AccessUnitEncoded += unit => accessUnits.Add(unit);
            encoder.EncodingFailed += ex => failure ??= ex;
            decoder.PcmDecoded += pcm => decoded.AddRange(pcm);
            decoder.DecodingFailed += ex => failure ??= ex;

            // 2 s of a 440 Hz tone at half scale, in 20 ms buffers like the loopback capture delivers.
            int frames = rate * 2;
            for (int start = 0; start < frames; start += rate / 50)
            {
                int count = Math.Min(rate / 50, frames - start);
                var pcm = new byte[count * channels * 2];
                for (int i = 0; i < count; i++)
                {
                    short s = (short)(Math.Sin(2 * Math.PI * frequency * (start + i) / rate) * 16000);
                    for (int c = 0; c < channels; c++) { pcm[(i * channels + c) * 2] = (byte)s; pcm[(i * channels + c) * 2 + 1] = (byte)(s >> 8); }
                }
                encoder.SubmitPcm(pcm);
            }
            Require(failure == null, "AAC encode failed: " + failure?.Message);
            Require(accessUnits.Count > 50, $"The AAC encoder produced only {accessUnits.Count} access units for 2 s of audio.");
            Require(accessUnits.All(u => u.Length > 7 && u[0] == 0xFF && (u[1] & 0xF0) == 0xF0),
                "The AAC encoder does not emit ADTS frames (the decoder is configured for ADTS).");

            foreach (var unit in accessUnits) decoder.SubmitAccessUnit(unit);
            Require(failure == null, "AAC decode failed: " + failure?.Message);

            // Duration: within one AAC frame of encoder priming/flush of what went in.
            int decodedFrames = decoded.Count / (channels * 2);
            Require(Math.Abs(decodedFrames - frames) < rate / 5, $"Decoded {decodedFrames} frames for {frames} encoded.");

            // Content: a clean 440 Hz tone crosses zero ~880 times a second. 32-bit float read as 16-bit
            // PCM, or a mangled stream, is noise with thousands of crossings (or silence with none).
            int crossings = 0, peak = 0;
            short previous = 0;
            for (int i = rate / 4; i < decodedFrames - rate / 4; i++) // skip the priming edges
            {
                short left = (short)(decoded[i * channels * 2] | (decoded[i * channels * 2 + 1] << 8));
                if ((left >= 0) != (previous >= 0)) crossings++;
                peak = Math.Max(peak, Math.Abs((int)left));
                previous = left;
            }
            double seconds = (decodedFrames - rate / 2) / (double)rate;
            double perSecond = crossings / seconds;
            Require(peak > 8000 && peak < 24000, $"Decoded tone peak {peak}, expected about 16000.");
            Require(perSecond is > 800 and < 960, $"Decoded audio has {perSecond:F0} zero crossings/s, expected ~880 for a 440 Hz tone (noise or silence).");
            Console.WriteLine($"PASS: cast audio AAC round trip (ADTS, {accessUnits.Count} units, {decodedFrames} frames, 440 Hz tone intact: {perSecond:F0} crossings/s, peak {peak})");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cast audio tests failed: " + message);
    }
}
