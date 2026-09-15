using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static EveryStage.Terminal.Receiving.DecoderGuids;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Drives a hardware H.264 decoder MFT directly, decoding one pushed Annex-B access unit at a time
/// into D3D11 NV12 textures (PLANNING.md §4.2: "Media Foundation硬件解码(DXVA) → D3D11纹理直出").
/// This is the decode-side counterpart of Caster's <c>H264HardwareEncoder</c> — same
/// "MFTEnumEx -> configure input/output media types -> bind IMFDXGIDeviceManager ->
/// ProcessInput/ProcessOutput" backbone — but the risk profile differs in one specific way:
///
/// - LOWER risk than the encoder in one respect: decoder MFTs (the built-in software H.264 decoder,
///   and every DXVA-capable hardware decoder this session is aware of) are documented as
///   SYNCHRONOUS transforms — ProcessInput/ProcessOutput block and return directly, no
///   IMFMediaEventGenerator event loop, no MF_TRANSFORM_ASYNC_UNLOCK dance. The entire category of
///   risk H264HardwareEncoder carries around its async event-loop protocol (GetEvent semantics,
///   MediaEventType member names, a dedicated background thread) doesn't apply here — this class has
///   no background thread of its own; the caller (<see cref="CastReceiver"/>) drives it synchronously
///   from whatever thread receives NAL units. (NOTE: verify — if the specific hardware decoder this
///   ends up running against reports itself async instead, this class's whole control flow is wrong
///   and would need the same event-loop pattern H264HardwareEncoder uses.)
/// - SAME risk as everywhere else in this repo in every other respect: this project has still never
///   driven any MFT, sync or async, against a compiler or a real device — MFTEnumEx's signature,
///   IMFActivate.ActivateObject's shape, ProcessInput/ProcessOutput's exact overloads, and the
///   IMFDXGIBuffer extraction below are all exactly as unverified as their counterparts in
///   H264HardwareEncoder and VideoDecodeSource. See this project's README "已知风险".
///
/// Pipeline this sits in: <c>RtpReceiver</c> (EveryStage.Transport) -> <c>H264RtpDepacketizer</c>
/// (one NAL unit at a time) -> <see cref="CastReceiver"/> reassembles NAL units into one Annex-B
/// access unit per encoded frame (using the RTP marker bit) -> this class (Annex-B in, NV12 D3D11
/// texture out) -> <c>SwapChainPresenter</c> (EveryStage.Rendering, already validated for local file
/// playback by <c>VideoContentController</c>) -> <c>OverlayWindow.VideoHost</c>.
/// </summary>
public sealed class H264HardwareDecoder : IDisposable
{
    private const int InputStreamId = 0;
    private const int OutputStreamId = 0;

    private readonly IMFTransform _decoder;
    private readonly bool _outputProvidesOwnSamples;

    /// <summary>Raised synchronously, from within <see cref="SubmitAccessUnit"/>, once per decoded
    /// frame the MFT produces — zero, one, or (in principle, though not expected given this
    /// pipeline's no-B-frames encoder settings) more than one call per submitted access unit, since
    /// decoder MFTs are free to buffer internally before their first output. Texture ownership
    /// passes to the handler — same convention as <c>VideoDecodeSource.ReadNextVideoFrame</c>, the
    /// caller must dispose it once done presenting.</summary>
    public event Action<ID3D11Texture2D, int, int, int>? FrameDecoded; // texture, arraySlice, width, height

    public H264HardwareDecoder(D3D11Device gpu, int width, int height)
    {
        _decoder = ActivateFirstHardwareDecoder();

        ConfigureInputType(_decoder, width, height);
        ConfigureNv12OutputType(_decoder);
        BindDeviceManager(_decoder, gpu);

        _outputProvidesOwnSamples = OutputProvidesOwnSamples(_decoder);

        // NOTE: verify these two message constants' exact Vortice enum member names, same caveat as
        // H264HardwareEncoder — native values are MFT_MESSAGE_NOTIFY_BEGIN_STREAMING /
        // MFT_MESSAGE_NOTIFY_START_OF_STREAM.
        _decoder.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
        _decoder.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);
    }

    /// <summary>Feeds one complete Annex-B-framed access unit (one encoded frame's worth of NAL
    /// units, start codes included — see <see cref="CastReceiver"/>'s reassembly) into the decoder,
    /// raising <see cref="FrameDecoded"/> for whatever comes out (possibly nothing yet) before
    /// returning.</summary>
    public void SubmitAccessUnit(byte[] annexBAccessUnit, long sampleTimeTicks)
    {
        MediaFactory.MFCreateMemoryBuffer((uint)annexBAccessUnit.Length, out var buffer).CheckError();
        using (buffer)
        {
            // NOTE: Lock()'s exact signature/return type is unverified — same caveat already
            // flagged in VideoDecodeSource.ReadNextAudioChunk. Native IMFMediaBuffer::Lock is
            // (out BYTE*, out maxLength, out currentLength); this assumes it comes back as a
            // writable Span<byte>.
            var span = buffer.Lock(out _, out _);
            annexBAccessUnit.CopyTo(span);
            // NOTE: written as a method call (native IMFMediaBuffer::SetCurrentLength(DWORD))
            // rather than guessed as a C# property, matching this codebase's established
            // convention for uncertain COM setters (see H264HardwareEncoder's SetSampleTime note).
            buffer.SetCurrentLength((uint)annexBAccessUnit.Length);
            buffer.Unlock();

            MediaFactory.MFCreateSample(out var sample).CheckError();
            using (sample)
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(sampleTimeTicks);

                // NOTE: MF_E_NOTACCEPTING (input queue full, ProcessOutput must be drained first)
                // is not expected here since DrainOutput() below always drains to exhaustion after
                // every submitted access unit — but if a real decoder buffers more aggressively
                // than assumed, this call could fail and the access unit would be lost. Flagged via
                // CheckError() rather than silently swallowed.
                _decoder.ProcessInput(InputStreamId, sample, 0).CheckError();
            }
        }

        DrainOutput();
    }

    private void DrainOutput()
    {
        while (true)
        {
            var outputBuffer = new MFTOutputDataBuffer { StreamID = OutputStreamId };
            IMFSample? ownedSample = null;

            if (!_outputProvidesOwnSamples)
            {
                // Same documented gap as H264HardwareEncoder.HandleHaveOutput's mirror-image branch
                // — every DXVA-capable hardware decoder this was written against expectation-wise
                // reportedly does provide its own output samples (that is the entire point of DXVA:
                // the decoder owns the surface pool), making this branch untested by construction.
                throw new NotSupportedException(
                    "This H.264 decoder MFT does not provide its own output samples, and self-allocating one isn't implemented — see H264HardwareDecoder.DrainOutput.");
            }

            var buffers = new[] { outputBuffer };
            var result = _decoder.ProcessOutput(0, buffers, out _);

            // NOTE: MF_E_TRANSFORM_NEED_MORE_INPUT is the normal "nothing more to drain right now"
            // outcome — verify this Failure-result comparison against however Vortice surfaces that
            // specific HRESULT, same caveat as H264HardwareEncoder.HandleHaveOutput.
            if (result.Failure) return;

            try
            {
                ownedSample = buffers[0].Sample;
                if (ownedSample == null) return;

                using var contiguousBuffer = ownedSample.ConvertToContiguousBuffer();
                using var dxgiBuffer = contiguousBuffer.QueryInterface<IMFDXGIBuffer>();
                var texture = dxgiBuffer.GetResource<ID3D11Texture2D>();
                int arraySlice = (int)dxgiBuffer.GetSubresourceIndex();
                var desc = texture.Description;
                FrameDecoded?.Invoke(texture, arraySlice, (int)desc.Width, (int)desc.Height);
            }
            finally
            {
                ownedSample?.Dispose();
            }
        }
    }

    private static IMFTransform ActivateFirstHardwareDecoder()
    {
        MediaFactory.MFStartup().CheckError();

        var inputType = new MFTRegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

        // NOTE: MFTEnumEx's exact Vortice signature (parameter order, whether flags is a [Flags]
        // enum called MFTEnumFlag or similar) is unverified — same caveat as
        // H264HardwareEncoder.ActivateFirstHardwareEncoder, mirrored here with input/output type
        // arguments swapped (this asks "which MFTs accept H264 input", not "which produce H264
        // output").
        MediaFactory.MFTEnumEx(
            MFT_CATEGORY_VIDEO_DECODER,
            MFTEnumFlag.Hardware | MFTEnumFlag.SortAndFilter,
            inputType,
            null,
            out IMFActivate[] activates).CheckError();

        if (activates == null || activates.Length == 0)
            throw new InvalidOperationException("No hardware H.264 decoder MFT found on this machine (MFTEnumEx returned none).");

        try
        {
            return activates[0].ActivateObject<IMFTransform>();
        }
        finally
        {
            foreach (var activate in activates) activate.Dispose();
        }
    }

    private static void ConfigureInputType(IMFTransform decoder, int width, int height)
    {
        MediaFactory.MFCreateMediaType(out var type).CheckError();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            type.Set(MF_MT_FRAME_SIZE, PackUInt64((uint)width, (uint)height));
            // Decoders generally require the INPUT type set before the OUTPUT type can be
            // negotiated — the opposite order from H264HardwareEncoder, where the encoder's OUTPUT
            // type must be set first.
            decoder.SetInputType(InputStreamId, type, 0).CheckError();
        }
    }

    private static void ConfigureNv12OutputType(IMFTransform decoder)
    {
        // Ask the decoder what output types it actually offers rather than constructing one from
        // scratch and hoping SetOutputType accepts it — the decoder (not this code) knows which
        // exact NV12 variant/attributes its own DXVA surface pool produces, same reasoning
        // VideoDecodeSource relies on IMFSourceReader to handle internally; here there's no source
        // reader doing that for us, so this loop does it directly.
        for (uint i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = decoder.GetOutputAvailableType(OutputStreamId, i);
            }
            catch (Exception)
            {
                // NOTE: the real failure signal for "no more types to enumerate" is the HRESULT
                // MF_E_NO_MORE_TYPES — caught broadly here because this session can't verify what
                // exception type (if any) Vortice throws for a failed GetOutputAvailableType call.
                throw new InvalidOperationException("H.264 decoder MFT offered no NV12 output type (GetOutputAvailableType exhausted).");
            }

            using (candidate)
            {
                // NOTE: Get<Guid> is extrapolated from VideoDecodeSource's use of Get<uint>/
                // Get<ulong> on the same IMFMediaType.Get<T> generic accessor — never verified with
                // Guid as the type argument specifically.
                var subtype = candidate.Get<Guid>(MF_MT_SUBTYPE);
                if (subtype == MFVideoFormat_NV12)
                {
                    decoder.SetOutputType(OutputStreamId, candidate, 0).CheckError();
                    return;
                }
            }
        }
    }

    private static void BindDeviceManager(IMFTransform decoder, D3D11Device gpu) =>
        // NOTE: same caveat as H264HardwareEncoder.BindDeviceManager — ulParam must be the device
        // manager's raw IUnknown pointer; whether Vortice exposes that as `.NativePointer` on the
        // wrapper is unverified.
        decoder.ProcessMessage(MFTMessageType.SetD3DManager, gpu.DeviceManager.NativePointer).CheckError();

    private static bool OutputProvidesOwnSamples(IMFTransform decoder)
    {
        var info = decoder.GetOutputStreamInfo(OutputStreamId);
        // NOTE: exact flag enum member name unverified — native flag is
        // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, same as H264HardwareEncoder's identical check.
        return (info.Flags & MFTOutputStreamInfoFlags.ProvidesSamples) != 0;
    }

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose() => _decoder.Dispose();
}
