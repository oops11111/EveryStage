using System.Collections.Concurrent;
using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static EveryStage.Terminal.Receiving.DecoderGuids;

namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Drives a hardware H.264 decoder MFT directly, decoding one pushed Annex-B access unit at a time
/// into D3D11 NV12 textures (PLANNING.md 4.2: "Media Foundation硬件解码(DXVA) -> D3D11纹理直出").
///
/// ASYNCHRONOUS, not synchronous. An earlier version of this class was written on the premise that
/// decoder MFTs are synchronous transforms, so ProcessInput/ProcessOutput could be called inline
/// with no event loop and no MF_TRANSFORM_ASYNC_UNLOCK. That premise holds for the built-in
/// SOFTWARE H.264 decoder, but this class does not use it: ActivateFirstHardwareDecoder enumerates
/// with MFT_ENUM_FLAG_HARDWARE, and every hardware MFT is an asynchronous MFT that starts out
/// locked, failing every call with MF_E_TRANSFORM_ASYNC_LOCKED until MF_TRANSFORM_ASYNC_UNLOCK is
/// set. The control flow now matches Caster's H264HardwareEncoder: unlock, then pump
/// METransformNeedInput / METransformHaveOutput off an IMFMediaEventGenerator on a dedicated
/// thread, with SubmitAccessUnit only queueing work rather than performing it. The synchronous path
/// is kept for a transform that reports itself sync after all, so a software fallback MFT still
/// works rather than deadlocking against an event loop that never receives events.
///
/// The D3D11 binding ORDER also matters and is easy to get silently wrong. A decoder MFT decides
/// whether a hardware configuration is available INSIDE SetInputType, so the DXGI device manager
/// must be attached BEFORE the media types are negotiated, not after. Attaching it afterwards
/// compiles, runs, and produces correct pictures - by quietly negotiating a software configuration
/// and copying every frame back through system memory, which is precisely the zero-copy pipeline
/// this project exists to avoid. There is no visible symptom; only CPU load and latency change.
///
/// Pipeline this sits in: RtpReceiver (EveryStage.Transport) -> H264RtpDepacketizer (one NAL unit
/// at a time) -> AccessUnitAssembler (one Annex-B access unit per encoded frame) -> CastReceiver ->
/// this class (Annex-B in, NV12 D3D11 texture out) -> SwapChainPresenter (EveryStage.Rendering) ->
/// OverlayWindow.VideoHost.
/// </summary>
public sealed class H264HardwareDecoder : IDisposable
{
    private const int InputStreamId = 0;
    private const int OutputStreamId = 0;

    /// <summary>How many undecoded access units may pile up before the oldest is dropped. Dropping
    /// a video access unit corrupts the picture until the next IDR, so this is deliberately not a
    /// tuning knob to reach for - but an unbounded queue in front of a stalled decoder is worse,
    /// because it grows without limit for as long as the cast lasts. Four frames is roughly 130ms
    /// at 30fps: long enough to ride out a scheduling hiccup, short enough that a genuinely stalled
    /// decoder gets noticed rather than hidden behind ever-growing latency.</summary>
    private const int MaxPendingAccessUnits = 4;

    /// <summary>Written to the MFT output stream attributes before media type negotiation so the
    /// decoder sizes its own DXVA surface pool rather than falling back to a driver default this
    /// code never sees. The documented sane range is 3-32; 6 leaves room for one frame decoding,
    /// one presenting, and a few in flight between them.</summary>
    private const uint MinimumOutputSampleCount = 6;

    /// <summary>D3D11_BIND_DECODER. Deliberately the only bind flag set: SwapChainPresenter consumes
    /// these textures through ID3D11VideoProcessor (VideoProcessorInputView), which needs no shader
    /// resource binding, and demanding D3D11_BIND_SHADER_RESOURCE on an NV12 decoder texture array
    /// is exactly the kind of extra constraint a driver is entitled to refuse outright.</summary>
    private const uint DecoderBindFlags = 0x200;

    private readonly D3D11Device _gpu;
    private readonly IMFTransform _decoder;
    private readonly IMFMediaEventGenerator? _events;
    private readonly bool _isAsynchronous;
    private readonly bool _outputProvidesOwnSamples;
    private readonly IntPtr _deviceHandle;

    private readonly ConcurrentQueue<(byte[] AccessUnit, long SampleTimeTicks)> _pending = new();
    private readonly SemaphoreSlim _accessUnitAvailable = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _eventLoopTask;
    private readonly object _lifecycleGate = new();
    private int _accessUnitsDropped;
    private int _disposed;

    /// <summary>Raised once per decoded frame the MFT produces. On the asynchronous path - the
    /// normal one for a hardware MFT - this comes from the event-loop thread, NOT from the caller's
    /// thread inside SubmitAccessUnit as it used to, so handlers must be thread-safe with respect
    /// to their own state. Texture ownership passes to the handler, which must dispose it once done
    /// presenting.
    ///
    /// The long comes from the decoded output sample's own Media Foundation SampleTime. Reading the
    /// output metadata directly is essential: a decoder may buffer, drop, or reorder frames, so a
    /// separate input-order FIFO cannot reliably identify an output frame.</summary>
    public event Action<ID3D11Texture2D, int, int, int, long>? FrameDecoded;

    /// <summary>Raised when the decode pipeline fails. On the asynchronous path a failure surfaces
    /// on the event-loop thread with no caller to throw at, so it is reported here instead - the
    /// same convention as AacAudioDecoder.DecodingFailed, which CastReceiver already consumes. A
    /// DeviceLostException here means the D3D11 device went away underneath the decoder and this
    /// instance is finished; the caller has to tear the cast down and rebuild.</summary>
    public event Action<Exception>? DecodingFailed;

    /// <summary>Access units dropped because the decoder fell far enough behind to fill the queue.
    /// Non-zero means visible corruption until the next IDR.</summary>
    public int AccessUnitsDropped => Volatile.Read(ref _accessUnitsDropped);

    /// <summary>True when the MFT reported MF_TRANSFORM_ASYNC and this class is running its event
    /// loop. Expected to be true for any hardware decoder; exposed so a diagnostic can assert which
    /// path was actually taken rather than assuming.</summary>
    public bool IsAsynchronous => _isAsynchronous;

    public H264HardwareDecoder(D3D11Device gpu, int width, int height)
    {
        _gpu = gpu;
        _decoder = ActivateFirstHardwareDecoder();

        // ActivateFirstHardwareDecoder() above already called MediaFactory.MFStartup() by the time
        // execution reaches here, so everything below is wrapped to keep that paired with an
        // MFShutdown() if construction fails - without it, a machine with no usable hardware
        // decoder configuration leaks one startup reference per attempted device cast, for the
        // Terminal's entire uptime.
        IntPtr deviceHandle = IntPtr.Zero;
        try
        {
            // ORDER IS LOAD-BEARING FROM HERE DOWN.
            // 1. Unlock first. Until this is set a hardware MFT rejects everything below with
            //    MF_E_TRANSFORM_ASYNC_LOCKED, including the attribute reads on the next line.
            UnlockAsyncProcessing(_decoder);
            _isAsynchronous = ReportsAsynchronous(_decoder);

            // 2. Refuse a transform that cannot take a DXGI device manager at all, rather than
            //    handing it one anyway and hoping. MF_SA_D3D11_AWARE == TRUE is the documented
            //    precondition for MFT_MESSAGE_SET_D3D_MANAGER.
            RequireD3D11Aware(_decoder);

            // 3. Device manager BEFORE the media types: the MFT works out its hardware
            //    configuration inside SetInputType and needs the manager already in hand. This is
            //    the ordering whose absence degrades silently to software decoding.
            BindDeviceManager(_decoder, gpu);
            ConfigureOutputStreamAllocation(_decoder);

            // 4. Types last. A hardware decoder holding a manager but with no usable D3D11
            //    configuration answers MF_E_UNSUPPORTED_D3D_TYPE here.
            ConfigureInputType(_decoder, width, height);
            ConfigureNv12OutputType(_decoder);

            _outputProvidesOwnSamples = OutputProvidesOwnSamples(_decoder);

            // Opened once and tested per frame (see EnsureDeviceStillValid). Opening it per frame
            // would be its own leak; not opening it at all was the old behaviour, which left a lost
            // device looking exactly like a stream that had simply gone quiet.
            gpu.DeviceManager.OpenDeviceHandle(out deviceHandle);
            _deviceHandle = deviceHandle;

            if (_isAsynchronous) _events = _decoder.QueryInterface<IMFMediaEventGenerator>();

            // Native values are MFT_MESSAGE_NOTIFY_BEGIN_STREAMING / MFT_MESSAGE_NOTIFY_START_OF_STREAM.
            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            if (_isAsynchronous) _eventLoopTask = Task.Run(() => RunEventLoop(_cts.Token));
        }
        catch
        {
            if (deviceHandle != IntPtr.Zero)
            {
                try { gpu.DeviceManager.CloseDeviceHandle(deviceHandle); } catch (Exception) { }
            }
            _events?.Dispose();
            _decoder.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Feeds one complete Annex-B-framed access unit (one encoded frame's worth of NAL
    /// units, start codes included) into the decoder.
    ///
    /// On the asynchronous path this only enqueues: the access unit is handed to the MFT later,
    /// from the event-loop thread, when the MFT asks for input. It therefore returns without having
    /// decoded anything, and a decode failure for this access unit surfaces on DecodingFailed
    /// rather than being thrown back at the caller.</summary>
    public void SubmitAccessUnit(byte[] annexBAccessUnit, long sampleTimeTicks)
    {
        if (!_isAsynchronous)
        {
            EnsureDeviceStillValid();
            SubmitAccessUnitCore(annexBAccessUnit, sampleTimeTicks);
            DrainOutput();
            return;
        }

        while (_pending.Count >= MaxPendingAccessUnits && _pending.TryDequeue(out _))
        {
            Interlocked.Increment(ref _accessUnitsDropped);
            // The semaphore count is intentionally not decremented to match: a surplus permit only
            // makes the event loop take one spurious lap and find an empty queue, which it handles.
        }

        _pending.Enqueue((annexBAccessUnit, sampleTimeTicks));
        _accessUnitAvailable.Release();
    }

    private void RunEventLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                IMFMediaEvent mediaEvent;
                try
                {
                    // MF_EVENT_FLAG_NO_WAIT = 1, same as H264HardwareEncoder.RunEventLoop.
                    mediaEvent = _events!.GetEvent(1);
                }
                catch (Exception ex) when (ex.HResult == Vortice.MediaFoundation.ResultCode.NoEventsAvailable.Code)
                {
                    token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(5));
                    continue;
                }

                using (mediaEvent)
                {
                    var eventType = mediaEvent.EventType;
                    if (eventType == MediaEventTypes.TransformNeedInput) HandleNeedInput(token);
                    else if (eventType == MediaEventTypes.TransformHaveOutput) HandleHaveOutput();
                    else if (eventType == MediaEventTypes.Error)
                        throw new InvalidOperationException("H.264 decoder MFT reported an error event.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DecodingFailed?.Invoke(ex);
        }
    }

    /// <summary>Bounded wait, for the same reason H264HardwareEncoder bounds its own: if the sender
    /// stops producing access units the event loop must not park forever holding a NeedInput it can
    /// never satisfy - it returns and waits for the MFT to ask again, or for Dispose's cancellation
    /// to be seen on the next lap. 200ms is several frame intervals at any realistic cast rate
    /// without meaningfully delaying shutdown.</summary>
    private static readonly TimeSpan NeedInputWaitTimeout = TimeSpan.FromMilliseconds(200);

    private void HandleNeedInput(CancellationToken token)
    {
        if (!_accessUnitAvailable.Wait(NeedInputWaitTimeout, token)) return;
        if (!_pending.TryDequeue(out var queued)) return;

        EnsureDeviceStillValid();
        SubmitAccessUnitCore(queued.AccessUnit, queued.SampleTimeTicks);
    }

    private void SubmitAccessUnitCore(byte[] annexBAccessUnit, long sampleTimeTicks)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(annexBAccessUnit.Length);
        using (buffer)
        {
            buffer.Lock(out var data, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(annexBAccessUnit, 0, data, annexBAccessUnit.Length);
            buffer.CurrentLength = annexBAccessUnit.Length;
            buffer.Unlock();

            var sample = MediaFactory.MFCreateSample();
            using (sample)
            {
                sample.AddBuffer(buffer);
                sample.SampleTime = sampleTimeTicks;
                _decoder.ProcessInput(InputStreamId, sample, 0);
            }
        }
    }

    private void HandleHaveOutput() => EmitOneOutput();

    private void DrainOutput()
    {
        while (EmitOneOutput()) { }
    }

    /// <summary>Pulls at most one decoded frame out of the MFT and raises FrameDecoded for it.
    /// Returns false when there is nothing (more) to take, which on the synchronous path is the
    /// drain loop's exit condition and on the asynchronous path just means the METransformHaveOutput
    /// was speculative.</summary>
    private bool EmitOneOutput()
    {
        var outputBuffer = new OutputDataBuffer { StreamID = OutputStreamId };

        if (!_outputProvidesOwnSamples)
        {
            // Untested by construction: every DXVA-capable hardware decoder provides its own output
            // samples, since owning the surface pool is the entire point of DXVA.
            throw new NotSupportedException(
                "This H.264 decoder MFT does not provide its own output samples, and self-allocating one isn't implemented - see H264HardwareDecoder.EmitOneOutput.");
        }

        var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);

        // MF_E_TRANSFORM_NEED_MORE_INPUT is the normal "nothing to take right now" outcome.
        if (result.Failure) return false;

        IMFSample? ownedSample = null;
        try
        {
            ownedSample = outputBuffer.Sample;
            if (ownedSample == null) return false;

            using var contiguousBuffer = ownedSample.ConvertToContiguousBuffer();
            using var dxgiBuffer = contiguousBuffer.QueryInterface<IMFDXGIBuffer>();
            var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            int arraySlice = (int)dxgiBuffer.SubresourceIndex;
            var desc = texture.Description;
            long sampleTimeTicks = ownedSample.SampleTime;
            FrameDecoded?.Invoke(texture, arraySlice, (int)desc.Width, (int)desc.Height, sampleTimeTicks);
            return true;
        }
        finally
        {
            ownedSample?.Dispose();
        }
    }

    /// <summary>Checks that the DXGI device is still the one this decoder was built against, which
    /// the documented decode loop requires on every frame. A TDR, a driver update, or a remote
    /// desktop session transition replaces the device underneath a long-running Terminal; with no
    /// check at all the old behaviour was to keep feeding a dead decoder forever and freeze on the
    /// last decoded frame, indistinguishable from a cast that had simply gone quiet.
    ///
    /// What this does NOT do is rebuild in place (close the handle, release every D3D11 resource,
    /// reopen, renegotiate, recreate the decoder). That reaches well past this class - the surface,
    /// the presenter and the swap chain belong to the device too - so a device change is reported
    /// as a terminal failure for this decoder instead and the cast is torn down. A Caster that is
    /// still sending gets picked up again by the existing pairing path.</summary>
    private void EnsureDeviceStillValid()
    {
        bool valid;
        try
        {
            valid = _gpu.DeviceManager.TestDevice(_deviceHandle).Success;
        }
        catch (Exception)
        {
            // MF_E_DXGI_NEW_VIDEO_DEVICE / MF_E_DXGI_DEVICE_NOT_INITIALIZED may surface as a thrown
            // exception rather than a failed Result depending on the binding; either way the device
            // is not the one this decoder was configured against.
            valid = false;
        }

        if (!valid) throw new DeviceLostException();
    }

    private static IMFTransform ActivateFirstHardwareDecoder()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            var inputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
            return MediaFoundationHelpers.ActivateFirstTransform(
                MFT_CATEGORY_VIDEO_DECODER,
                EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter,
                inputType,
                null);
        }
        catch
        {
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static void UnlockAsyncProcessing(IMFTransform decoder)
    {
        using var attributes = decoder.Attributes;
        // Set unconditionally rather than only when MF_TRANSFORM_ASYNC reads TRUE: reading that
        // attribute is itself one of the calls a locked MFT may refuse, and a synchronous transform
        // simply ignores an attribute it never checks. Same call and same reasoning as
        // H264HardwareEncoder.UnlockAsyncProcessing and AacAudioDecoder.UnlockAsyncProcessingIfNeeded.
        attributes.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
    }

    private static bool ReportsAsynchronous(IMFTransform decoder)
    {
        using var attributes = decoder.Attributes;
        return ReadUInt32(attributes, MF_TRANSFORM_ASYNC, 0u) != 0u;
    }

    private static void RequireD3D11Aware(IMFTransform decoder)
    {
        using var attributes = decoder.Attributes;
        if (ReadUInt32(attributes, MF_SA_D3D11_AWARE, 0u) == 0u)
        {
            throw new NotSupportedException(
                "The enumerated H.264 decoder MFT does not report MF_SA_D3D11_AWARE, so it cannot accept a DXGI device manager and cannot decode into D3D11 textures. Refusing to continue rather than silently decoding through system memory.");
        }
    }

    /// <summary>Reads a UINT32 MFT attribute, treating "not set" as the supplied default. An absent
    /// attribute comes back as MF_E_ATTRIBUTENOTFOUND, which a binding may surface as a thrown
    /// exception or as a failed result; both mean the same thing here.</summary>
    private static uint ReadUInt32(IMFAttributes attributes, Guid key, uint fallback)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void ConfigureInputType(IMFTransform decoder, int width, int height)
    {
        var type = MediaFactory.MFCreateMediaType();
        using (type)
        {
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            type.Set(MF_MT_FRAME_SIZE, PackUInt64((uint)width, (uint)height));
            // Decoders require the INPUT type set before the OUTPUT type can be negotiated - the
            // opposite order from H264HardwareEncoder, where the encoder's OUTPUT type goes first.
            decoder.SetInputType(InputStreamId, type, 0);
        }
    }

    private static void ConfigureNv12OutputType(IMFTransform decoder)
    {
        // Ask the decoder which output types it actually offers rather than constructing one and
        // hoping SetOutputType accepts it - the decoder knows which exact NV12 variant its own DXVA
        // surface pool produces.
        for (var i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = decoder.GetOutputAvailableType(OutputStreamId, i);
            }
            catch (Exception)
            {
                // The real signal for "no more types to enumerate" is MF_E_NO_MORE_TYPES.
                throw new InvalidOperationException("H.264 decoder MFT offered no NV12 output type (GetOutputAvailableType exhausted).");
            }

            using (candidate)
            {
                var subtype = candidate.GetGUID(MF_MT_SUBTYPE);
                if (subtype == MFVideoFormat_NV12)
                {
                    decoder.SetOutputType(OutputStreamId, candidate, 0);
                    return;
                }
            }
        }
    }

    private static void BindDeviceManager(IMFTransform decoder, D3D11Device gpu) =>
        // ulParam is the device manager's raw IUnknown pointer.
        decoder.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)gpu.DeviceManager.NativePointer);

    /// <summary>Steers the decoder's own surface allocation, on the MFT output stream attributes,
    /// before any media type is negotiated. Without these the driver picks both values and this
    /// code never learns what it picked.</summary>
    private static void ConfigureOutputStreamAllocation(IMFTransform decoder)
    {
        using var outputAttributes = decoder.GetOutputStreamAttributes(OutputStreamId);
        outputAttributes.Set(MF_SA_D3D11_BINDFLAGS, DecoderBindFlags);
        outputAttributes.Set(MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT, MinimumOutputSampleCount);
    }

    private static bool OutputProvidesOwnSamples(IMFTransform decoder)
    {
        var info = decoder.GetOutputStreamInfo(OutputStreamId);
        return (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
    }

    private static ulong PackUInt64(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
        }

        _cts.Cancel();
        if (_eventLoopTask != null)
        {
            try { _eventLoopTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        _cts.Dispose();

        while (_pending.TryDequeue(out _)) { }
        _accessUnitAvailable.Dispose();

        if (_deviceHandle != IntPtr.Zero)
        {
            try { _gpu.DeviceManager.CloseDeviceHandle(_deviceHandle); } catch (Exception) { }
        }

        _events?.Dispose();
        _decoder.Dispose();

        // MFStartup/MFShutdown are reference-counted process-wide; this pairs the startup that
        // ActivateFirstHardwareDecoder performed.
        MediaFactory.MFShutdown();
    }
}

/// <summary>The D3D11 device this decoder was built against was replaced underneath it (TDR, driver
/// update, session transition). The decoder cannot continue and the cast has to be rebuilt.</summary>
public sealed class DeviceLostException : Exception
{
    public DeviceLostException()
        : base("The D3D11 video device was replaced while decoding; this decoder instance cannot continue.")
    {
    }
}
