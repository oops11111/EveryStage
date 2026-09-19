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

    // Bounded, drop-oldest input queue (README risk #17's second half: "没有实现任何编码器跟不上时
    // 该丢帧还是该等的策略"). Frames are fully independent units (unlike the NAL units in
    // LiveCastSession's send queue, splitting one access unit across a drop boundary would corrupt
    // it) — dropping the oldest queued frame outright, GPU texture and all, is the natural low-
    // latency choice: an encoder that's falling behind gains nothing from eventually encoding a
    // stale frame, and holding more than a couple of NV12 textures here just burns GPU memory
    // without buying anything. _frameAvailable's count is kept exactly in sync with
    // _pendingFrames.Count: SubmitFrame only calls Release when a drop did NOT happen (net queue
    // depth actually grew), since a drop-then-enqueue leaves the depth unchanged.
    private const int MaxPendingFrames = 3;
    private readonly ConcurrentQueue<(ID3D11Texture2D Texture, int ArraySlice)> _pendingFrames = new();
    private readonly SemaphoreSlim _frameAvailable = new(0);
    private int _framesDroppedForBackpressure;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _eventLoopTask;
    private long _nextSampleTime;

    /// <summary>Raised from the background event-loop thread with one Annex-B-framed encoded
    /// access unit — not yet split into RTP-sized packets, that's <c>AnnexBNalSplitter</c> +
    /// <c>H264RtpPacketizer</c>'s job (EveryStage.Transport).</summary>
    public event Action<byte[]>? AccessUnitEncoded;

    /// <summary>Frames dropped whole (GPU texture disposed, never handed to the encoder) because
    /// <see cref="_pendingFrames"/> was already <see cref="MaxPendingFrames"/> deep when
    /// <see cref="SubmitFrame"/> was called — see that method for the drop policy. Should stay at 0
    /// whenever the encoder can keep up with the capture rate; a climbing count means this machine's
    /// hardware encoder is the pipeline's bottleneck, not the network.</summary>
    public int FramesDroppedForBackpressure => Volatile.Read(ref _framesDroppedForBackpressure);

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

        // Bug fixed here (see this class's own Dispose() comment for the normal-teardown half of
        // the same MFStartup/MFShutdown pairing bug, and H264HardwareDecoder's mirror-image fix for
        // the identical shape on the decode side): ActivateFirstHardwareEncoder() above already
        // called MediaFactory.MFStartup() successfully by the time execution reaches this line —
        // but if any of the steps below throws, this constructor never finishes, so no
        // H264HardwareEncoder instance ever exists for LiveCastSession to later Dispose() and hit
        // the MFShutdown() call this class's Dispose() makes. Without this try/catch, that
        // MFStartup() reference-count increment would leak permanently every time construction
        // fails this way.
        try
        {
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
        }
        catch
        {
            _events?.Dispose();
            _encoder.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }

        _eventLoopTask = Task.Run(() => RunEventLoop(_cts.Token));
    }

    /// <summary>Queues one NV12 frame for encoding. Ownership of <paramref name="texture"/> passes
    /// to this encoder — do not dispose it yourself; the event loop disposes it after
    /// <c>ProcessInput</c> accepts it (or immediately, on shutdown, or on a backpressure drop, if it
    /// never gets that far). Called from the capture loop's thread (see <c>LiveCastSession.RunLoop</c>
    /// / <c>EncodeSelfTestRunner.RunLoop</c>), never blocks.</summary>
    public void SubmitFrame(ID3D11Texture2D texture, int arraySlice = 0)
    {
        bool dropped = false;
        if (_pendingFrames.Count >= MaxPendingFrames && _pendingFrames.TryDequeue(out var stale))
        {
            stale.Texture.Dispose();
            dropped = true;
            Interlocked.Increment(ref _framesDroppedForBackpressure);
        }

        _pendingFrames.Enqueue((texture, arraySlice));

        // Only signal a net increase in queue depth — a drop-then-enqueue leaves depth (and
        // therefore how many permits HandleNeedInput should be able to Wait for) unchanged.
        if (!dropped) _frameAvailable.Release();
    }

    private void RunEventLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // NOTE: verify this blocks with no timeout parameter in Vortice's binding, and
                // that "None" is the right no-flags value — native signature is
                // IMFMediaEventGenerator::GetEvent(DWORD dwFlags, IMFMediaEvent**).
                //
                // Deeper, more severe risk connected to this note (found this audit, documented
                // but NOT fixed — see README "已知风险" for the numbered entry): if this call
                // truly has no cancellation support, then once the MFT stops raising events at
                // all (e.g. capture upstream already died, so no more SubmitFrame calls ever
                // arrive and the MFT has nothing left to react to), this thread can be blocked
                // inside this single native call indefinitely — it never reaches the
                // token.IsCancellationRequested check above again. In that scenario, Dispose()'s
                // `_cts.Cancel(); _eventLoopTask.Wait(TimeSpan.FromSeconds(2));` is guaranteed to
                // time out (the thread has no way to observe the cancellation), and Dispose()
                // proceeds to dispose _events/_encoder/_frameAvailable regardless of that timeout
                // — unlike the ObjectDisposedException instances fixed elsewhere in this file/
                // session, disposing a COM object that a native call on another thread may still
                // be blocked inside of is not a catchable .NET exception; it is a native-level
                // risk (potential crash or memory corruption), not just an unhandled exception.
                // Not fixed here: a real fix needs a non-blocking/pollable GetEvent variant
                // (mirroring native MF_EVENT_FLAG_NO_WAIT) whose exact Vortice shape is
                // unverified without a Windows/dotnet environment — guessing at it risks adding a
                // second wrong assumption on top of this already-unverified one, so this is
                // disclosed rather than "fixed" with unverified code.
                using var mediaEvent = _events.GetEvent(EventGenerateFlags.None);

                // NOTE: IMFMediaEvent::GetType() almost certainly isn't exposed as literally
                // `GetType()` in the C# binding (that would collide with object.GetType()) —
                // this is a placeholder name for "however Vortice actually exposes the native
                // event type", likely a differently-named method or a property.
                var eventType = mediaEvent.EventType;

                if (eventType == MediaEventTypes.TransformNeedInput)
                {
                    HandleNeedInput(token);
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

    // Bounded rather than unbounded wait: if capture ever stops calling SubmitFrame altogether
    // (session stopping, capture already failed) this must not block the event-loop thread forever
    // — it just returns and waits for the MFT to raise another METransformNeedInput later, or for
    // Dispose's cancellation to be observed on the loop's next iteration. 200ms is comfortably
    // longer than one frame interval at any realistic capture rate (33ms at 30fps) without being so
    // long it meaningfully delays shutdown.
    private static readonly TimeSpan NeedInputWaitTimeout = TimeSpan.FromMilliseconds(200);

    private void HandleNeedInput(CancellationToken token)
    {
        // Blocks for a bounded time instead of the previous Thread.Sleep(1) busy-poll (README risk
        // #17's first half) — _frameAvailable's count exactly tracks _pendingFrames.Count (see
        // SubmitFrame), so a successful Wait means TryDequeue below should essentially always
        // succeed; this remains a single-consumer (this event-loop thread), single-producer
        // (whichever thread calls SubmitFrame) relationship, so there's no other consumer that could
        // race this dequeue out from under it.
        try
        {
            if (!_frameAvailable.Wait(NeedInputWaitTimeout, token)) return;
        }
        catch (OperationCanceledException)
        {
            return; // shutting down — the outer loop's token check ends RunEventLoop next iteration.
        }
        catch (ObjectDisposedException)
        {
            // Bug found (self-review, same audit that found the identical shape in
            // LiveCastSession/RtpReceiver/RawRtpReceiver/DiscoveryService/TerminalDiscoveryClient —
            // see this project's README) and fixed here: Dispose() below has the same unchecked-
            // timeout shape (_cts.Cancel() then _eventLoopTask.Wait(TimeSpan.FromSeconds(2)) without
            // checking whether that wait actually succeeded, before disposing _frameAvailable
            // regardless). If this event-loop thread is ever still here when that 2-second wait
            // expires — plausible given RunEventLoop's own GetEvent() call has no cancellation
            // support at all, see that method's own doc comment — _frameAvailable can be disposed
            // while this exact Wait() call is still pending, surfacing as ObjectDisposedException.
            // Treated the same as cancellation: there is nothing left to do once the thing being
            // waited on has been torn down.
            return;
        }

        if (!_pendingFrames.TryDequeue(out var frame))
        {
            // Should not normally happen given the semaphore accounting above; if it ever does
            // (e.g. a future change adds a second SubmitFrame caller), there's simply nothing to
            // encode this round rather than a reason to crash the event loop.
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

        if (!_outputProvidesOwnSamples)
        {
            // Self-allocate the output sample from GetOutputStreamInfo's reported buffer size —
            // every hardware encoder this was written against is expected to set
            // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (see README risk #16), so this branch remains
            // untested by construction, but a machine whose encoder MFT doesn't set that flag no
            // longer hard-fails outright.
            outputBuffer.Sample = CreateOutputSample(_encoder);
        }

        var buffers = new[] { outputBuffer };
        var result = _encoder.ProcessOutput(0, buffers, out _);

        // NOTE: MF_E_TRANSFORM_NEED_MORE_INPUT is a normal, expected outcome here (the MFT raised
        // METransformHaveOutput speculatively but isn't actually ready) — verify this comparison
        // against however Vortice surfaces that specific failure HRESULT.
        if (result.Failure)
        {
            // A self-allocated sample (if any) was never consumed on this path — release it rather
            // than leaking it; buffers[0].Sample is the same object as outputBuffer.Sample here
            // since ProcessOutput failed before it could have replaced it.
            outputBuffer.Sample?.Dispose();
            return;
        }

        IMFSample? ownedSample = null;
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
            // Correct either way: when _outputProvidesOwnSamples is true this is an MFT-allocated
            // sample we now own; when false it's the same self-allocated sample CreateOutputSample
            // returned above (buffers[0].Sample, unchanged by a successful ProcessOutput in this
            // mode — the caller-supplied sample is filled in place, not replaced).
            ownedSample?.Dispose();
        }
    }

    /// <summary>Builds the caller-allocated output sample <see cref="HandleHaveOutput"/> needs when
    /// <see cref="_outputProvidesOwnSamples"/> is false — see that field and README risk #16. Takes
    /// the encoder itself rather than a pre-fetched <c>GetOutputStreamInfo</c> result, so this
    /// method doesn't have to name that return type explicitly — <see cref="OutputProvidesOwnSamples"/>
    /// already gets away with just <c>var</c>, and there's no need to guess a type name here that
    /// Vortice may not actually call this.</summary>
    private static IMFSample CreateOutputSample(IMFTransform encoder)
    {
        var info = encoder.GetOutputStreamInfo(OutputStreamId);

        // NOTE: exact Vortice property names for MFT_OUTPUT_STREAM_INFO::cbSize/cbAlignment are
        // unverified — guessed as Size/Alignment following the same "drop the cb/dw prefix" pattern
        // OutputProvidesOwnSamples's own NOTE already flags for info.Flags (native: dwFlags). Native
        // cbAlignment's documented meaning is "required alignment minus 1, or 0 for none" (e.g. 15
        // for 16-byte alignment) — MFCreateAlignedMemoryBuffer's alignment parameter uses the exact
        // same "minus 1" convention, so it's passed straight through with no conversion.
        IMFMediaBuffer buffer;
        if (info.Alignment > 0)
            MediaFactory.MFCreateAlignedMemoryBuffer(info.Size, info.Alignment, out buffer).CheckError();
        else
            MediaFactory.MFCreateMemoryBuffer(info.Size, out buffer).CheckError();

        MediaFactory.MFCreateSample(out var sample).CheckError();
        // Same "using (buffer) { sample.AddBuffer(buffer); }" pattern HandleNeedInput already uses:
        // AddBuffer shares ownership via its own COM AddRef, so this method's own reference to
        // buffer can (and must) be released right after — unlike HandleNeedInput's sample, this
        // method's sample itself must NOT be wrapped in a using here, since it needs to survive
        // this method returning it to the caller (HandleHaveOutput), which owns disposing it.
        using (buffer)
        {
            sample.AddBuffer(buffer);
        }
        return sample;
    }

    private static IMFTransform ActivateFirstHardwareEncoder()
    {
        MediaFactory.MFStartup().CheckError();

        // Everything below is wrapped so that MFStartup() above is never left unbalanced by this
        // method throwing before ever returning a usable IMFTransform — see the constructor's own
        // try/catch (which covers everything AFTER this method returns) for the other half of the
        // same bug, and H264HardwareDecoder.ActivateFirstHardwareDecoder for the identical fix on
        // the decode side. MFTEnumEx finding no hardware encoder at all (a real, not just
        // hypothetical, condition on a machine without a compatible GPU/driver) is exactly the case
        // this exists for: without this, that specific failure would leak one MFStartup() reference
        // count every single time casting is attempted from such a machine, for the Caster's entire
        // uptime.
        try
        {
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
        catch
        {
            MediaFactory.MFShutdown();
            throw;
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
        _frameAvailable.Dispose();

        _events.Dispose();
        _encoder.Dispose();

        // Bug fixed here: this class's constructor (via ActivateFirstHardwareEncoder) calls
        // MediaFactory.MFStartup() but this Dispose() never called the matching MFShutdown() —
        // MFStartup/MFShutdown are reference-counted process-wide, so every encoder ever created
        // (one per LiveCastSession.Start(), one per EncodeSelfTestRunner run) bumped that count up
        // with nothing ever bumping it back down for this class's share of it. Every sibling class
        // in this same Encode/Decode family that calls MFStartup in its constructor
        // (AacAudioEncoder, AacAudioDecoder, VideoDecodeSource, AudioDecodeSource) already pairs it
        // with MFShutdown() here in Dispose() — this was the one that didn't.
        MediaFactory.MFShutdown();
    }
}
