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
/// <see cref="AacAudioEncoder"/> now emits) specifically because ADTS's self-describing per-frame
/// header means this class needs no separate out-of-band <c>AudioSpecificConfig</c> at all — see
/// that GUID's own doc comment in <c>EncoderGuids.cs</c>/this file for the full reasoning. Not wired
/// into <c>Terminal.Receiving.CastReceiver</c> yet — see <see cref="AacAudioEncoder"/>'s doc comment
/// and this repo's READMEs for what that still needs (an RTP depacketizer for this stream, and a
/// <c>CastStartMessage</c> field to signal the codec). Validated only via
/// <c>EveryStage.Caster.Encode.AacEncodeSelfTestRunner</c>'s in-process encode-then-decode round
/// trip for now.
/// </summary>
public sealed class AacAudioDecoder : IDisposable
{
    // The standard AAC frame size (samples per channel) — used only to synthesize a nominal
    // SetSampleTime/SetSampleDuration for each submitted access unit (see SubmitAccessUnit), since
    // this class has no other source of per-frame timing for a live, sync-only stream. Real
    // multi-second drift accumulation from this being a nominal value rather than a measured one
    // doesn't matter for what this class is validated for today (a synchronous round-trip self-test,
    // not a played-back timeline), but would matter if this were ever wired into a real playback
    // clock later.
    private const int SamplesPerFrame = 1024;

    private readonly IMFTransform _decoder;
    private readonly long _frameDurationTicks; // 100ns units, per SamplesPerFrame's worth of audio.
    private long _nextSampleTime;

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
            ConfigureOutputType(_decoder);

            // NOTE: same unverified exact enum member names as H264HardwareEncoder/AacAudioEncoder's
            // identical two calls.
            _decoder.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
            _decoder.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);
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
        MediaFactory.MFCreateMemoryBuffer(accessUnit.Length, out var buffer).CheckError();
        using (buffer)
        {
            var span = buffer.Lock(out _, out _);
            accessUnit.AsSpan().CopyTo(span);
            buffer.SetCurrentLength(accessUnit.Length);
            buffer.Unlock();

            MediaFactory.MFCreateSample(out var sample).CheckError();
            using (sample)
            {
                sample.AddBuffer(buffer);
                // Nominal per-frame timing — see SamplesPerFrame's own doc comment on why this is a
                // fixed assumption rather than something measured.
                sample.SetSampleTime(_nextSampleTime);
                sample.SetSampleDuration(_frameDurationTicks);
                _nextSampleTime += _frameDurationTicks;

                // Same "no retry-on-MF_E_NOTACCEPTING" simplification as AacAudioEncoder.SubmitPcmCore
                // — SubmitAccessUnit always fully drains output (below) before returning.
                _decoder.ProcessInput(0, sample, 0).CheckError();
            }
        }

        DrainOutput();
    }

    private void DrainOutput()
    {
        while (true)
        {
            var outputBuffer = new MFTOutputDataBuffer { StreamID = 0 };
            var buffers = new[] { outputBuffer };
            // Same "treat any ProcessOutput failure as nothing-ready-yet" simplification
            // H264HardwareEncoder.HandleHaveOutput/AacAudioEncoder.DrainOutput already use.
            var result = _decoder.ProcessOutput(0, buffers, out _);
            if (result.Failure) return;

            var sample = buffers[0].Sample;
            if (sample == null) return;

            try
            {
                using var contiguousBuffer = sample.ConvertToContiguousBuffer();
                var span = contiguousBuffer.Lock(out _, out var currentLength);
                var pcm = new byte[currentLength];
                span.Slice(0, currentLength).CopyTo(pcm);
                contiguousBuffer.Unlock();

                PcmDecoded?.Invoke(pcm);
            }
            finally
            {
                sample.Dispose();
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
            var inputType = new MFTRegisterTypeInfo { GuidMajorType = MFMediaType_Audio, GuidSubtype = MFAudioFormat_AAC };

            MediaFactory.MFTEnumEx(
                MFT_CATEGORY_AUDIO_DECODER,
                MFTEnumFlag.SortAndFilter,
                inputType,
                null,
                out IMFActivate[] activates).CheckError();

            if (activates == null || activates.Length == 0)
                throw new InvalidOperationException("No AAC decoder MFT found on this machine (MFTEnumEx returned none).");

            try
            {
                return activates[0].ActivateObject<IMFTransform>();
            }
            finally
            {
                foreach (var activate in activates) activate.Dispose();
            }
        }
        catch
        {
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static void UnlockAsyncProcessingIfNeeded(IMFTransform decoder)
    {
        // Same defensive-regardless-of-the-sync-assumption reasoning as
        // AacAudioEncoder.UnlockAsyncProcessingIfNeeded's identical call.
        using var attributes = decoder.Attributes;
        attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
    }

    private static void ConfigureInputType(IMFTransform decoder, int sampleRate, int channels)
    {
        MediaFactory.MFCreateMediaType(out var type).CheckError();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            type.Set(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            type.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            type.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            // ADTS — matches what AacAudioEncoder emits; see that class's and this file's own doc
            // comments for why this avoids needing a separate out-of-band AudioSpecificConfig.
            type.Set(MF_MT_AAC_PAYLOAD_TYPE, 1u);
            decoder.SetInputType(0, type, 0).CheckError();
        }
    }

    /// <summary>Takes whichever PCM output type index 0 offers rather than constructing one
    /// explicitly — same simplification <c>AacAudioEncoder.ConfigureOutputType</c> makes for the
    /// encode direction's output type, and for the same reason: a decoder's set of valid output
    /// types depends on the input type just negotiated by <see cref="ConfigureInputType"/>, so
    /// there's nothing to gain here by hand-constructing a type the decoder might reject anyway.</summary>
    private static void ConfigureOutputType(IMFTransform decoder)
    {
        // NOTE: same unverified GetOutputAvailableType signature caveat as
        // AacAudioEncoder.ConfigureOutputType's identical call.
        decoder.GetOutputAvailableType(0, 0, out var type).CheckError();
        using (type)
        {
            decoder.SetOutputType(0, type, 0).CheckError();
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
