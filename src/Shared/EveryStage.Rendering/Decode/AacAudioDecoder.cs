using Vortice.MediaFoundation;
using static EveryStage.Rendering.Decode.WellKnownGuids;

namespace EveryStage.Rendering.Decode;

/// <summary>
/// Drives the built-in AAC decoder MFT directly (ADTS-framed AAC access units in, 16-bit PCM out) —
/// the decode-side mirror of <c>EveryStage.Caster.Encode.AacAudioEncoder</c>, which this repo built
/// first — see that class's own doc comment for the shared reasoning this one inherits: Windows
/// ships exactly one built-in AAC decoder MFT to find, and it's documented/commonly reported to be a
/// *synchronous* transform, so — same single biggest risk as the encoder — this class has no
/// <c>IMFMediaEventGenerator</c> event loop and no background thread, just direct
/// <c>ProcessInput</c>/<c>ProcessOutput</c> calls. If that assumption is wrong on some machine, every
/// such call below fails outright and this class needs rewriting around
/// <c>H264HardwareEncoder</c>'s async event-loop pattern instead.
///
/// Deliberately duplicates rather than shares code with <see cref="AacAudioEncoder"/> despite the
/// two being near-mirror-images of each other (build a sample, ProcessInput, drain ProcessOutput in
/// a loop) — same "two independent implementations, not one shared abstraction" convention this repo
/// already applies to <see cref="VideoDecodeSource"/>/<see cref="AudioDecodeSource"/> and to the
/// Terminal/Caster discovery protocol's two independent client implementations: encode and decode
/// are different enough directions (PCM-in-AAC-out vs. AAC-in-PCM-out) that forcing them through one
/// shared helper would trade a small amount of duplication for a worse "which direction does this
/// generic parameter actually mean" abstraction, for two files that are never edited together anyway.
///
/// Expects ADTS-framed input (<c>MF_MT_AAC_PAYLOAD_TYPE</c> = 1, matching what
/// <see cref="AacAudioEncoder"/> emits) specifically because ADTS's self-describing per-frame header
/// means this class needs no separate out-of-band <c>AudioSpecificConfig</c> at all — see that GUID's
/// own doc comment in <c>WellKnownGuids.cs</c>. Wired into <c>Terminal.Receiving.CastReceiver</c> for
/// device casts with AAC audio (<c>CastStartMessage.AudioIsAac</c>), and verified against the real
/// Microsoft AAC decoder on Windows 10 19045 by WindowsSelfTests' <c>CastAudioTests</c> (encode → decode
/// round trip). That verification found the decoder synchronous (as assumed), without its own output
/// samples, requiring <c>MF_MT_USER_DATA</c> on the input type, and offering 32-bit float before
/// 16-bit PCM — all handled below (audit A-6 / A-26).
/// </summary>
public sealed class AacAudioDecoder : IDisposable
{
    // The standard AAC frame size (samples per channel) — used only to synthesize a nominal
    // SetSampleTime/SetSampleDuration for each submitted access unit (see SubmitAccessUnit). The
    // decoded PCM is played as a byte stream; output sample times are not used for timing (the
    // receiver paces on its own clock and the RTP timestamps), so a nominal value is enough here.
    private const int SamplesPerFrame = 1024;

    private readonly IMFTransform _decoder;
    private readonly long _frameDurationTicks; // 100ns units, per SamplesPerFrame's worth of audio.
    private long _nextSampleTime;

    // What PcmDecoded promises (the stream's announced format) vs what the decoder currently outputs;
    // they differ when the decoder reports a different or changed format (HE-AAC doubling its rate).
    private readonly int _channels;
    private readonly int _sampleRate;
    private int _outputChannels;
    private int _outputRate;
    private readonly bool _providesSamples;

    /// <summary>Raised (from whichever thread calls <see cref="SubmitAccessUnit"/> — see that
    /// method) with one chunk of decoded interleaved 16-bit PCM.</summary>
    public event Action<byte[]>? PcmDecoded;

    /// <summary>Raised (same calling-thread caveat as <see cref="PcmDecoded"/>) if decoding fails —
    /// same "don't let a failure vanish silently" reasoning as every other *Failed event in this
    /// repo.</summary>
    public event Action<Exception>? DecodingFailed;

    public AacAudioDecoder(int sampleRate, int channels)
    {
        _frameDurationTicks = (long)(SamplesPerFrame / (double)sampleRate * 10_000_000L);
        _channels = channels;
        _sampleRate = sampleRate;

        _decoder = ActivateFirstAacDecoder();

        // Bug fixed here (same shape as H264HardwareDecoder/H264HardwareEncoder/AudioDecodeSource/
        // VideoDecodeSource's own constructor fixes, see any of their doc comments):
        // ActivateFirstAacDecoder() above already called MediaFactory.MFStartup() successfully by
        // the time execution reaches this line — but if any of the calls below throws, this
        // constructor never finishes, so no AacAudioDecoder instance ever exists for its owner
        // (CastReceiver, once per device cast with AAC audio) to later Dispose() and hit the
        // MFShutdown() call below.
        try
        {
            UnlockAsyncProcessingIfNeeded(_decoder);

            // Input type must be set before enumerating output types — same order (and same reasoning)
            // as AacAudioEncoder.ConfigureOutputType's doc comment gives for the encode direction.
            ConfigureInputType(_decoder, sampleRate, channels);
            (_outputChannels, _outputRate) = ConfigureOutputType(_decoder);
            _providesSamples = EveryStage.Rendering.MediaFoundationHelpers.ProvidesOutputSamples(_decoder);

            // NOTE: same unverified exact enum member names as H264HardwareEncoder/AacAudioEncoder's
            // identical two calls.
            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _decoder.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Decodes one ADTS-framed AAC access unit, synchronously, on whichever thread calls
    /// this — there is no background thread in this class at all (see this class's doc comment on
    /// why, and the risk if that assumption is wrong). Raises <see cref="PcmDecoded"/> zero or more
    /// times before returning.</summary>
    public void SubmitAccessUnit(byte[] accessUnit)
    {
        try
        {
            SubmitAccessUnitCore(accessUnit);
        }
        catch (Exception ex)
        {
            DecodingFailed?.Invoke(ex);
        }
    }

    private void SubmitAccessUnitCore(byte[] accessUnit)
    {
        // NOTE: same unverified MFCreateMemoryBuffer signature caveat as
        // AacAudioEncoder.SubmitPcmCore's identical call.
        var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        using (buffer)
        {
            buffer.Lock(out var data, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(accessUnit, 0, data, accessUnit.Length);
            buffer.CurrentLength = accessUnit.Length;
            buffer.Unlock();

            var sample = MediaFactory.MFCreateSample();
            using (sample)
            {
                sample.AddBuffer(buffer);
                // Nominal per-frame timing — see SamplesPerFrame's own doc comment on why this is a
                // fixed assumption rather than something measured.
                sample.SampleTime = _nextSampleTime;
                sample.SampleDuration = _frameDurationTicks;
                _nextSampleTime += _frameDurationTicks;

                // Same "no retry-on-MF_E_NOTACCEPTING" simplification as AacAudioEncoder.SubmitPcmCore
                // — SubmitAccessUnit always fully drains output (below) before returning.
                _decoder.ProcessInput(0, sample, 0);
            }
        }

        DrainOutput();
    }

    private void DrainOutput()
    {
        while (true)
        {
            var status = EveryStage.Rendering.MediaFoundationHelpers.ProcessOneOutput(_decoder, _providesSamples, out var sample);
            if (status == EveryStage.Rendering.TransformOutput.NeedMoreInput) return;
            if (status == EveryStage.Rendering.TransformOutput.StreamChanged)
            {
                // The decoder's output format changed (e.g. SBR detected): set it again and keep going.
                (_outputChannels, _outputRate) = ConfigureOutputType(_decoder);
                continue;
            }
            try
            {
                var pcm = EveryStage.Rendering.MediaFoundationHelpers.CopySampleData(sample!);
                PcmDecoded?.Invoke(EveryStage.Rendering.Audio.PcmConverter.Convert(pcm, _outputChannels, _outputRate, _channels, _sampleRate));
            }
            finally
            {
                sample!.Dispose();
            }
        }
    }

    private static IMFTransform ActivateFirstAacDecoder()
    {
        MediaFactory.MFStartup().CheckError();

        // Everything below is wrapped so that MFStartup() above is never left unbalanced by this
        // method throwing before ever returning a usable IMFTransform — see this class's
        // constructor for the other half of the same bug, and H264HardwareDecoder/H264HardwareEncoder's
        // identical fix for the same shape. No AAC decoder MFT found at all is a real, not just
        // hypothetical, condition on a machine lacking one — without this, that failure would leak
        // one MFStartup() reference count every time a device cast with AAC audio is attempted.
        try
        {
            // Matched against the AAC subtype on the INPUT side this time (MFTEnumEx's typeInfo
            // parameters describe input/output types the candidate MFT must support — for a decoder
            // that's the compressed format going in) — mirrors AacAudioEncoder.ActivateFirstAacEncoder's
            // identical call shape, matched on the opposite side.
            var inputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Audio, GuidSubtype = MFAudioFormat_AAC };

            return MediaFoundationHelpers.ActivateFirstTransform(
                MFT_CATEGORY_AUDIO_DECODER,
                EnumFlag.EnumFlagSortandfilter,
                inputType,
                null);
        }
        catch
        {
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static void UnlockAsyncProcessingIfNeeded(IMFTransform decoder)
    {
        // Same reasoning as AacAudioEncoder.UnlockAsyncProcessingIfNeeded: a synchronous transform may have
        // no attribute store at all (E_NOTIMPL), which is not an error.
        EveryStage.Rendering.MediaFoundationHelpers.UnlockAsyncIfSupported(decoder);
    }

    private static void ConfigureInputType(IMFTransform decoder, int sampleRate, int channels)
    {
        var type = MediaFactory.MFCreateMediaType();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            type.Set(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            type.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            type.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            // ADTS — matches what AacAudioEncoder emits; see that class's and this file's own doc
            // comments for why this avoids needing a separate out-of-band AudioSpecificConfig.
            type.Set(MF_MT_AAC_PAYLOAD_TYPE, 1u);
            // HEAACWAVEINFO after its WAVEFORMATEX: wPayloadType = 1 (ADTS), wAudioProfileLevelIndication =
            // 0xFE (unspecified), wStructType = 0, wReserved1 = 0, dwReserved2 = 0. No AudioSpecificConfig
            // follows — ADTS carries it in every frame. Required by the Microsoft AAC decoder (audit A-26).
            type.SetBlob(MF_MT_USER_DATA, new byte[] { 1, 0, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            decoder.SetInputType(0, type, 0);
        }
    }

    /// <summary>Selects the decoder's 16-bit PCM output type and returns its channels/rate. The first
    /// type offered is 32-bit float on the Microsoft AAC decoder (verified on Windows 10 19045): taking
    /// index 0, as this used to, would hand float samples to a clock playing them as 16-bit — noise.</summary>
    private static (int Channels, int SampleRate) ConfigureOutputType(IMFTransform decoder)
    {
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try { type = decoder.GetOutputAvailableType(0, index); }
            catch (Exception ex) { throw new NotSupportedException("AAC 解码器不提供 16 位 PCM 输出", ex); } // MF_E_NO_MORE_TYPES
            using (type)
            {
                if (type.GetGUID(MF_MT_SUBTYPE) != MFAudioFormat_PCM || type.GetUInt32(MF_MT_AUDIO_BITS_PER_SAMPLE) != 16) continue;
                decoder.SetOutputType(0, type, 0);
                return ((int)type.GetUInt32(MF_MT_AUDIO_NUM_CHANNELS), (int)type.GetUInt32(MF_MT_AUDIO_SAMPLES_PER_SECOND));
            }
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
