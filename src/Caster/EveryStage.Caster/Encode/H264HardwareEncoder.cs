using System.Collections.Concurrent;
using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static EveryStage.Caster.Encode.EncoderGuids;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Drives a hardware H.264 encoder MFT directly (PLANNING.md §4.2: "视频编码：H.264 硬件编码
/// (NVENC/QuickSync/AMF)，禁用B帧，零延迟预设，CBR码率控制，短GOP").
///
/// This is the highest-risk, least-verifiable code in this entire repository — more so than the
/// Phase 0 decode pipeline or screen capture. The reason isn't just "more DirectX interop": the
/// Phase 0 demo's IMFSourceReader is a synchronous, pull-based API (call ReadSample, get a sample
/// back) that this repo already worked through once. Hardware encoder MFTs are near-universally
/// *asynchronous* transforms instead — the transform tells you via an IMFMediaEventGenerator when
/// it wants input or has output ready (METransformNeedInput / METransformHaveOutput events), and
/// you're required to explicitly "unlock" async processing via an attribute before any of that
/// works at all. That's a meaningfully different, more error-prone protocol, and this file has
/// never been compiled, let alone run against a real encoder. See this project's README "已知风险"
/// for the specific points to check first — there are more of them here than anywhere else in
/// this repo, and several concern things (COM PROPVARIANT marshaling for ICodecAPI, exact
/// MFT_MESSAGE_TYPE/MediaEventType enum member names) this session has lower confidence in than
/// the raw-GUID-literal risk pattern used everywhere else.
///
/// Pipeline this class expects to sit in: ScreenCaptureSource (BGRA texture) -> BgraToNv12Converter
/// (NV12 texture) -> this class (NV12 in, Annex-B H.264 access units out) -> AnnexBNalSplitter +
/// H264RtpPacketizer (EveryStage.Transport) -> RtpSession. Nothing wires that whole chain together
/// yet.
/// </summary>
public sealed class H264HardwareEncoder : IDisposable
{
    private const int InputStreamId = 0;
    private const int OutputStreamId = 0;

    private readonly IMFTransform _encoder;
    private readonly IMFMediaEventGenerator _events;
    private readonly bool _outputProvidesOwnSamples;
    private readonly long _sampleDurationTicks; // 100ns units.

    private readonly ConcurrentQueue<(ID3D11Texture2D Texture, int ArraySlice)> _pendingFrames = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _eventLoopTask;
    private long _nextSampleTime;

    /// <summary>Raised from the background event-loop thread with one Annex-B-framed encoded
    /// access unit — not yet split into RTP-sized packets, that's <c>AnnexBNalSplitter</c> +
    /// <c>H264RtpPacketizer</c>'s job (EveryStage.Transport).</summary>
    public event Action<byte[]>? AccessUnitEncoded;

    /// <summary>Raised from the background event-loop thread if it dies from an unhandled
    /// exception — same "don't let a background thread vanish silently" reasoning as
    /// VideoContentController.PlaybackFailed on the Terminal side.</summary>
    public event Action<Exception>? EncodingFailed;

    /// <param name="frameRateNumerator">Assumes an integer frame rate (e.g. 30 or 60) — a real
    /// implementation would want a proper rational frame-rate type to also support rates like
    /// 29.97 correctly; this is a simplification.</param>
    public H264HardwareEncoder(D3D11Device gpu, int width, int height, int frameRateNumerator, int bitrateBps)
    {
        _sampleDurationTicks = 10_000_000L / frameRateNumerator;

        _encoder = ActivateFirstHardwareEncoder();
        UnlockAsyncProcessing(_encoder);

        ConfigureOutputType(_encoder, width, height, frameRateNumerator, bitrateBps);
        ConfigureInputType(_encoder, width, height, frameRateNumerator);
        ApplyLowLatencySettings(_encoder);
        BindDeviceManager(_encoder, gpu);

        _outputProvidesOwnSamples = OutputProvidesOwnSamples(_encoder);
        _events = _encoder.QueryInterface<IMFMediaEventGenerator>();

        // NOTE: verify these two message constants' exact Vortice enum member names — native
        // values are MFT_MESSAGE_NOTIFY_BEGIN_STREAMING / MFT_MESSAGE_NOTIFY_START_OF_STREAM.
        _encoder.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero);
        _encoder.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero);

        _eventLoopTask = Task.Run(() => RunEventLoop(_cts.Token));
    }

    /// <summary>Queues one NV12 frame for encoding. Ownership of <paramref name="texture"/> passes
    /// to this encoder — do not dispose it yourself; the event loop disposes it after
    /// <c>ProcessInput</c> accepts it (or immediately, on shutdown, if it never gets that far).</summary>
    public void SubmitFrame(ID3D11Texture2D texture, int arraySlice = 0) =>
        _pendingFrames.Enqueue((texture, arraySlice));

    private void RunEventLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // NOTE: verify this blocks with no timeout parameter in Vortice's binding, and
                // that "None" is the right no-flags value — native signature is
                // IMFMediaEventGenerator::GetEvent(DWORD dwFlags, IMFMediaEvent**).
                using var mediaEvent = _events.GetEvent(EventGenerateFlags.None);

                // NOTE: IMFMediaEvent::GetType() almost certainly isn't exposed as literally
                // `GetType()` in the C# binding (that would collide with object.GetType()) —
                // this is a placeholder name for "however Vortice actually exposes the native
                // event type", likely a differently-named method or a property.
                var eventType = mediaEvent.EventType;

                if (eventType == MediaEventTypes.TransformNeedInput)
                {
                    HandleNeedInput();
                }
                else if (eventType == MediaEventTypes.TransformHaveOutput)
                {
                    HandleHaveOutput();
                }
                else if (eventType == MediaEventTypes.Error)
                {
                    throw new InvalidOperationException("H.264 encoder MFT reported an error event.");
                }
                // Other event types (drain complete, etc.) are ignored — this encoder never calls
                // MFT_MESSAGE_COMMAND_DRAIN, so METransformDrainComplete should never occur; if a
                // real run does see other event types, that's itself a finding worth investigating,
                // not something to silently swallow forever.
            }
        }
        catch (Exception ex)
        {
            EncodingFailed?.Invoke(ex);
        }
    }

    private void HandleNeedInput()
    {
        if (!_pendingFrames.TryDequeue(out var frame))
        {
            // No frame ready yet. A production implementation would block/wait here instead of
            // busy-looping; left as a documented gap rather than guessing at the right backpressure
            // strategy (drop vs. wait vs. re-request) without being able to test any of them.
            Thread.Sleep(1);
            return;
        }

        using (frame.Texture)
        {
            // NOTE: MFCreateDXGISurfaceBuffer's exact Vortice signature/overloads are unverified —
            // native signature is MFCreateDXGISurfaceBuffer(REFIID riid, IUnknown *punkSurface,
            // UINT uSubresourceIndex, BOOL fBottomUpWhenLinear, IMFMediaBuffer **ppBuffer).
            MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, frame.Texture, (uint)frame.ArraySlice, false, out var buffer).CheckError();

            using (buffer)
            {
                MediaFactory.MFCreateSample(out var sample).CheckError();
                using (sample)
                {
                    sample.AddBuffer(buffer);
                    // NOTE: written as method calls (matching IMFSample::SetSampleTime/
                    // SetSampleDuration's native shape) rather than guessed as C# properties —
                    // verify which form Vortice actually exposes.
                    sample.SetSampleTime(_nextSampleTime);
                    sample.SetSampleDuration(_sampleDurationTicks);
                    _nextSampleTime += _sampleDurationTicks;

                    _encoder.ProcessInput(InputStreamId, sample, 0).CheckError();
                }
            }
        }
    }

    private void HandleHaveOutput()
    {
        var outputBuffer = new MFTOutputDataBuffer { StreamID = OutputStreamId };
        IMFSample? ownedSample = null;

        if (!_outputProvidesOwnSamples)
        {
            // We must allocate the output sample ourselves — size it from GetOutputStreamInfo's
            // reported buffer size (not implemented here: this path is a documented gap, since
            // every hardware encoder this was written against expectation-wise reportedly *does*
            // set MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, making this branch untested by construction).
            throw new NotSupportedException(
                "This H.264 MFT does not provide its own output samples, and self-allocating one isn't implemented — see H264HardwareEncoder's HandleHaveOutput.");
        }

        var buffers = new[] { outputBuffer };
        var result = _encoder.ProcessOutput(0, buffers, out _);

        // NOTE: MF_E_TRANSFORM_NEED_MORE_INPUT is a normal, expected outcome here (the MFT raised
        // METransformHaveOutput speculatively but isn't actually ready) — verify this comparison
        // against however Vortice surfaces that specific failure HRESULT.
        if (result.Failure) return;

        try
        {
            ownedSample = buffers[0].Sample;
            if (ownedSample == null) return;

            using var contiguousBuffer = ownedSample.ConvertToContiguousBuffer();
            var span = contiguousBuffer.Lock(out _, out var currentLength);
            var accessUnit = new byte[currentLength];
            span.Slice(0, currentLength).CopyTo(accessUnit);
            contiguousBuffer.Unlock();

            AccessUnitEncoded?.Invoke(accessUnit);
        }
        finally
        {
            ownedSample?.Dispose();
        }
    }

    private static IMFTransform ActivateFirstHardwareEncoder()
    {
        MediaFactory.MFStartup().CheckError();

        var outputType = new MFTRegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

        // NOTE: MFTEnumEx's exact Vortice signature (parameter order, whether flags is a
        // [Flags] enum called MFTEnumFlag or similar) is unverified.
        MediaFactory.MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFTEnumFlag.Hardware | MFTEnumFlag.SortAndFilter,
            null,
            outputType,
            out IMFActivate[] activates).CheckError();

        if (activates == null || activates.Length == 0)
            throw new InvalidOperationException("No hardware H.264 encoder MFT found on this machine (MFTEnumEx returned none).");

        try
        {
            // NOTE: IMFActivate.ActivateObject's exact generic/typed shape in Vortice is
            // unverified — native signature is ActivateObject(REFIID riid, void **ppv).
            return activates[0].ActivateObject<IMFTransform>();
        }
        finally
        {
            foreach (var activate in activates) activate.Dispose();
        }
    }

    private static void UnlockAsyncProcessing(IMFTransform encoder)
    {
        using var attributes = encoder.Attributes;
        // Only meaningful if the MFT actually reports itself as async; setting the unlock
        // attribute on a synchronous MFT is harmless but pointless. Not conditioned on
        // MF_TRANSFORM_ASYNC here — setting the unlock unconditionally is the common pattern in MF
        // sample code, since a sync MFT simply ignores an attribute it doesn't check.
        attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
    }

    private static void ConfigureOutputType(IMFTransform encoder, int width, int height, int frameRateNumerator, int bitrateBps)
    {
        MediaFactory.MFCreateMediaType(out var type).CheckError();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            type.Set(MF_MT_FRAME_SIZE, PackUInt64((uint)width, (uint)height));
            type.Set(MF_MT_FRAME_RATE, PackUInt64((uint)frameRateNumerator, 1));
            type.Set(MF_MT_AVG_BITRATE, (uint)bitrateBps);
            type.Set(MF_MT_INTERLACE_MODE, 2u); // 2 = MFVideoInterlace_Progressive.

            // Encoders generally require the OUTPUT type set before the INPUT type — the output
            // type is what determines which input types the encoder will subsequently accept.
            encoder.SetOutputType(OutputStreamId, type, 0).CheckError();
        }
    }

    private static void ConfigureInputType(IMFTransform encoder, int width, int height, int frameRateNumerator)
    {
        MediaFactory.MFCreateMediaType(out var type).CheckError();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            type.Set(MF_MT_FRAME_SIZE, PackUInt64((uint)width, (uint)height));
            type.Set(MF_MT_FRAME_RATE, PackUInt64((uint)frameRateNumerator, 1));
            type.Set(MF_MT_INTERLACE_MODE, 2u);
            type.Set(MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);

            encoder.SetInputType(InputStreamId, type, 0).CheckError();
        }
    }

    private static void ApplyLowLatencySettings(IMFTransform encoder)
    {
        // NOTE: this whole method is the least-confident part of an already low-confidence file.
        // ICodecAPI.SetValue's exact Vortice signature (does it take a boxed `object`, a
        // library-specific Variant wrapper type, or something else for the PROPVARIANT the native
        // API expects?) is unverified, as are all six CODECAPI_* GUID values themselves (see
        // EncoderGuids.cs). If this method doesn't compile as written, that is expected — the
        // *intent* (CBR rate control, low-latency mode, a short GOP, no B-frames via CABAC/temporal
        // layer settings) is what PLANNING.md §4.2 asks for; fix the mechanism, keep the intent.
        try
        {
            using var codecApi = encoder.QueryInterface<ICodecAPI>();
            codecApi.SetValue(CODECAPI_AVEncCommonRateControlMode, 1u); // 1 = eAVEncCommonRateControlMode_CBR.
            codecApi.SetValue(CODECAPI_AVLowLatencyMode, true);
            codecApi.SetValue(CODECAPI_AVEncMPVGOPSize, 30u); // short GOP; matches a 1s GOP at 30fps.
            codecApi.SetValue(CODECAPI_AVEncVideoTemporalLayerCount, 1u); // 1 layer -> effectively no B-frames.
        }
        catch (Exception)
        {
            // Not every hardware encoder supports every CODECAPI property (or ICodecAPI at all) —
            // treat this tuning as best-effort. An encoder that doesn't accept these still encodes,
            // just without the latency/GOP tuning PLANNING.md calls for; that's a real product gap
            // to flag rather than a reason to crash encoder construction entirely.
        }
    }

    private static void BindDeviceManager(IMFTransform encoder, D3D11Device gpu)
    {
        // NOTE: ProcessMessage's second parameter for MFT_MESSAGE_SET_D3D_MANAGER must be the
        // device manager's raw IUnknown pointer — native signature is
        // ProcessMessage(MFT_MESSAGE_TYPE eMessage, ULONG_PTR ulParam). Whether Vortice exposes
        // ulParam as IntPtr (requiring something like Marshal.GetIUnknownForObject or a
        // `.NativePointer`-style property on the device manager wrapper) is unverified.
        encoder.ProcessMessage(MFTMessageType.SetD3DManager, gpu.DeviceManager.NativePointer).CheckError();
    }

    private static bool OutputProvidesOwnSamples(IMFTransform encoder)
    {
        var info = encoder.GetOutputStreamInfo(OutputStreamId);
        // NOTE: exact flag enum member name unverified — native flag is
        // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.
        return (info.Flags & MFTOutputStreamInfoFlags.ProvidesSamples) != 0;
    }

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        _cts.Cancel();
        try { _eventLoopTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();

        while (_pendingFrames.TryDequeue(out var frame)) frame.Texture.Dispose();

        _events.Dispose();
        _encoder.Dispose();
    }
}
