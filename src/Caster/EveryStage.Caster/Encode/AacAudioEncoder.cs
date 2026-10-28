using EveryStage.Rendering;
using Vortice.MediaFoundation;
using static EveryStage.Caster.Encode.EncoderGuids;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Drives the built-in AAC encoder MFT directly (PCM in, ADTS-framed AAC access units out) — this
/// repo's first audio-encoding MFT, closing the "尚未开始" gap this project's README has flagged
/// since the live-cast pipeline first connected: audio is currently sent as uncompressed 16-bit PCM,
/// at noticeably higher bandwidth than the H.264 video it travels alongside.
///
/// Meaningfully lower-risk than <see cref="H264HardwareEncoder"/> despite being new territory, for
/// two reasons. First, this repo does not need to find/select a specific hardware vendor's
/// implementation — Windows ships one built-in software AAC encoder MFT
/// (<c>MFT_CATEGORY_AUDIO_ENCODER</c> / AAC subtype), so <see cref="ActivateFirstAacEncoder"/> is a
/// simpler, single-candidate enumeration than <c>H264HardwareEncoder.ActivateFirstHardwareEncoder</c>'s
/// "pick the best of possibly several hardware vendors' MFTs". Second, and more importantly: that
/// built-in AAC encoder MFT is documented/commonly reported to be a *synchronous* transform, unlike
/// hardware video encoders — meaning this class drives it with a direct
/// <c>ProcessInput</c>/<c>ProcessOutput</c> call pattern, with no <c>IMFMediaEventGenerator</c> event
/// loop, no async-unlock requirement, and no background thread at all. <b>This assumption is the
/// single biggest risk in this file</b>: this sandbox has no way to verify it on a real machine — if
/// this turns out wrong for some encoder that gets activated here, every <c>ProcessInput</c>/
/// <c>ProcessOutput</c> call below fails outright, and this class would need rewriting around the
/// same async event-loop pattern <see cref="H264HardwareEncoder"/> already uses. See this project's
/// README "已知风险" for the rest of what's unverified here.
///
/// Emits ADTS-framed access units (<c>MF_MT_AAC_PAYLOAD_TYPE</c> = 1) rather than raw/headerless
/// ones — see that GUID's own doc comment in <c>EncoderGuids.cs</c> for why: since this repo's own
/// two ends are the only consumer of this stream (no RFC 3640 RTP packetizer exists here, or is
/// planned for this round — see below), ADTS's self-describing per-frame header means
/// <see cref="AacAudioDecoder"/> needs no separate out-of-band AudioSpecificConfig at all, a
/// meaningful simplification a real RFC 3640 receiver wouldn't get to make.
///
/// Wired into <see cref="Casting.LiveCastSession"/>'s send path (one ADTS access unit per RTP packet,
/// <c>CastStartMessage.AudioIsAac</c>). Verified against the real Microsoft AAC encoder on Windows 10
/// 19045 by WindowsSelfTests' <c>CastAudioTests</c> (encode → decode round trip): synchronous as assumed,
/// but without an attribute store (GetAttributes = E_NOTIMPL), without its own output samples, and
/// accepting an output type only BEFORE the input type is set — all handled below (audit A-6 / A-26).
/// </summary>
public sealed class AacAudioEncoder : IDisposable
{
    private readonly IMFTransform _encoder;
    private long _nextSampleTime; // 100ns units.
    private readonly long _bytesPerSecond;
    private readonly bool _providesSamples;

    /// <summary>Raised (from whichever thread calls <see cref="SubmitPcm"/> — see that method) with
    /// one ADTS-framed AAC access unit.</summary>
    public event Action<byte[]>? AccessUnitEncoded;

    /// <summary>Raised (same calling-thread caveat as <see cref="AccessUnitEncoded"/>) if encoding
    /// fails — same "don't let a failure vanish silently" reasoning as every other *Failed event in
    /// this repo.</summary>
    public event Action<Exception>? EncodingFailed;

    public AacAudioEncoder(int sampleRate, int channels)
    {
        _bytesPerSecond = sampleRate * channels * 2L; // 16-bit PCM.

        _encoder = ActivateFirstAacEncoder();

        // Bug fixed here (same shape as H264HardwareDecoder/H264HardwareEncoder/AudioDecodeSource/
        // VideoDecodeSource/AacAudioDecoder's own constructor fixes, see any of their doc comments):
        // ActivateFirstAacEncoder() above already called MediaFactory.MFStartup() successfully by
        // the time execution reaches this line — but if any of the calls below throws, this
        // constructor never finishes, so no AacAudioEncoder instance ever exists for its owner
        // (LiveCastSession, once per cast with audio) to later Dispose() and hit the MFShutdown()
        // call below.
        try
        {
            UnlockAsyncProcessingIfNeeded(_encoder);

            // Output type first — the Microsoft AAC encoder rejects any output type once the input type is
            // set (see ConfigureOutputType).
            ConfigureOutputType(_encoder, sampleRate, channels);
            ConfigureInputType(_encoder, sampleRate, channels);
            // The Microsoft AAC encoder does not allocate its own output samples; calling ProcessOutput
            // without one fails every time, which used to be read as "nothing ready" (audit A-6).
            _providesSamples = EveryStage.Rendering.MediaFoundationHelpers.ProvidesOutputSamples(_encoder);

            // NOTE: same unverified exact enum member names as H264HardwareEncoder's identical two
            // calls — native values are MFT_MESSAGE_NOTIFY_BEGIN_STREAMING / MFT_MESSAGE_NOTIFY_START_OF_STREAM.
            _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _encoder.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Encodes one chunk of interleaved 16-bit PCM, synchronously, on whichever thread calls
    /// this — there is no background thread in this class at all (see this class's doc comment on
    /// why, and the risk if that assumption is wrong). Raises <see cref="AccessUnitEncoded"/> zero or
    /// more times before returning: the encoder buffers PCM internally until it has enough samples
    /// for one AAC frame (1024 samples/channel is the standard AAC frame size), so a chunk smaller
    /// than that produces no output yet, and a large-enough chunk can produce more than one.</summary>
    public void SubmitPcm(byte[] pcm)
    {
        try
        {
            SubmitPcmCore(pcm);
        }
        catch (Exception ex)
        {
            EncodingFailed?.Invoke(ex);
        }
    }

    private void SubmitPcmCore(byte[] pcm)
    {
        // NOTE: MFCreateMemoryBuffer's exact Vortice signature (does it take the size as int or
        // uint?) is unverified, same caveat as H264HardwareEncoder.CreateOutputSample's identical
        // call.
        var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        using (buffer)
        {
            // Same Lock()-returns-a-Span<byte> assumption AudioDecodeSource.ReadNextChunk/
            // VideoDecodeSource.ReadNextAudioChunk already make for reading — here used in reverse,
            // to write into the buffer instead of out of it.
            buffer.Lock(out var data, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(pcm, 0, data, pcm.Length);
            buffer.CurrentLength = pcm.Length;
            buffer.Unlock();

            var sample = MediaFactory.MFCreateSample();
            using (sample)
            {
                sample.AddBuffer(buffer);
                long durationTicks = (long)(pcm.Length / (double)_bytesPerSecond * 10_000_000L);
                sample.SampleTime = _nextSampleTime;
                sample.SampleDuration = durationTicks;
                _nextSampleTime += durationTicks;

                // Unlike H264HardwareEncoder's HandleNeedInput (which only ever calls ProcessInput
                // once per available frame, driven by the MFT's own METransformNeedInput event),
                // this is a direct synchronous call with no retry-on-MF_E_NOTACCEPTING handling —
                // SubmitPcm always fully drains output (below) before returning, so by the time the
                // next call comes in the encoder's internal output queue should already be empty in
                // the steady state. A real MF_E_NOTACCEPTING here would surface as an exception via
                // CheckError() and reach EncodingFailed like any other failure, rather than being
                // silently retried — flagged here as a known simplification, not a verified-safe one.
                _encoder.ProcessInput(0, sample, 0);
            }
        }

        DrainOutput();
    }

    private void DrainOutput()
    {
        while (true)
        {
            var status = EveryStage.Rendering.MediaFoundationHelpers.ProcessOneOutput(_encoder, _providesSamples, out var sample);
            if (status == EveryStage.Rendering.TransformOutput.NeedMoreInput) return;
            if (status == EveryStage.Rendering.TransformOutput.StreamChanged)
                throw new InvalidOperationException("AAC 编码器在推流中改变了输出格式");
            try
            {
                AccessUnitEncoded?.Invoke(EveryStage.Rendering.MediaFoundationHelpers.CopySampleData(sample!));
            }
            finally
            {
                sample!.Dispose();
            }
        }
    }

    private static IMFTransform ActivateFirstAacEncoder()
    {
        MediaFactory.MFStartup().CheckError();

        // Everything below is wrapped so that MFStartup() above is never left unbalanced by this
        // method throwing before ever returning a usable IMFTransform — see this class's
        // constructor for the other half of the same bug, and H264HardwareDecoder/
        // H264HardwareEncoder/AacAudioDecoder's identical fix for the same shape. No AAC encoder MFT
        // found at all is a real, not just hypothetical, condition — without this, that failure
        // would leak one MFStartup() reference count every time casting with audio is attempted.
        try
        {
            var outputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Audio, GuidSubtype = MFAudioFormat_AAC };

            // No MFTEnumFlag.Hardware here (unlike H264HardwareEncoder's identical call) — the built-in
            // AAC encoder is a software MFT, and filtering for hardware would just find nothing.
            return MediaFoundationHelpers.ActivateFirstTransform(
                MFT_CATEGORY_AUDIO_ENCODER,
                EnumFlag.EnumFlagSortandfilter,
                null,
                outputType);
        }
        catch
        {
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static void UnlockAsyncProcessingIfNeeded(IMFTransform encoder)
    {
        // The built-in AAC encoder MFT is synchronous and has no attribute store at all (GetAttributes
        // returns E_NOTIMPL, verified on Windows 10 19045) — reading .Attributes unconditionally made this
        // constructor throw on every machine, so casts never carried audio.
        EveryStage.Rendering.MediaFoundationHelpers.UnlockAsyncIfSupported(encoder);
    }

    private static void ConfigureInputType(IMFTransform encoder, int sampleRate, int channels)
    {
        var type = MediaFactory.MFCreateMediaType();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            type.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
            type.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            type.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            type.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            type.Set(MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(channels * 2));
            type.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(sampleRate * channels * 2));
            encoder.SetInputType(0, type, 0);
        }
    }

    /// <summary>Sets an explicit AAC output type: ADTS framing (see MF_MT_AAC_PAYLOAD_TYPE in
    /// EncoderGuids.cs), 16-bit, the stream's rate/channels, and the first bitrate the encoder accepts from
    /// the fixed set it supports (128 kbps preferred). Must run BEFORE <see cref="ConfigureInputType"/>:
    /// the Microsoft AAC encoder rejects every output type — even the ones it enumerates itself — once
    /// the input type is set (MF_E_INVALIDMEDIATYPE, verified on Windows 10 19045). The previous
    /// input-first, "take index 0" setup therefore never worked; it was masked by the constructor failing
    /// even earlier on the attribute store (audit A-26).</summary>
    private static void ConfigureOutputType(IMFTransform encoder, int sampleRate, int channels)
    {
        Exception? last = null;
        foreach (uint bytesPerSecond in new uint[] { 16000, 24000, 20000, 12000 }) // 128 / 192 / 160 / 96 kbps
        {
            var type = MediaFactory.MFCreateMediaType();
            using (type)
            {
                type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                type.Set(MF_MT_SUBTYPE, MFAudioFormat_AAC);
                type.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
                type.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
                type.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                type.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytesPerSecond);
                type.Set(MF_MT_AAC_PAYLOAD_TYPE, 1u);
                try
                {
                    encoder.SetOutputType(0, type, 0);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
        }
        throw new NotSupportedException($"AAC 编码器不支持 {sampleRate} Hz {channels} 声道", last);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        MediaFactory.MFShutdown();
    }
}
