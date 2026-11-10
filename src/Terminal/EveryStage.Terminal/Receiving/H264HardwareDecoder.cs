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
    private int _decodeLoopFailed;
    private int _disposed;

    /// <summary>Raised once per decoded frame the MFT produces. On the asynchronous path - the
    /// normal one for a hardware MFT - this comes from the event-loop thread, NOT from the caller's
    /// thread inside SubmitAccessUnit as it used to, so handlers must be thread-safe with respect
    /// to their own state. Ownership of the texture AND of the output sample (the last argument, which
    /// keeps the texture's decoder slot reserved) passes to the handler, which must dispose both once
    /// done presenting.
    ///
    /// The long comes from the decoded output sample's own Media Foundation SampleTime. Reading the
    /// output metadata directly is essential: a decoder may buffer, drop, or reorder frames, so a
    /// separate input-order FIFO cannot reliably identify an output frame. The two ints after the slice
    /// are the decoder surface's size, which includes alignment padding (1088 rows for 1080p).</summary>
    public event Action<ID3D11Texture2D, int, int, int, long, IMFSample>? FrameDecoded;

    /// <summary>Raised when the decode pipeline fails. On the asynchronous path a failure surfaces
    /// on the event-loop thread with no caller to throw at, so it is reported here instead - the
    /// same convention as AacAudioDecoder.DecodingFailed, which CastReceiver already consumes. A
    /// DeviceLostException here means the D3D11 device went away underneath the decoder and this
    /// instance is finished; the caller has to tear the cast down and rebuild.</summary>
    public event Action<Exception>? DecodingFailed;

    /// <summary>Access units dropped because the decoder fell far enough behind to fill the queue.
    /// Non-zero means visible corruption until the next IDR.</summary>
    public int AccessUnitsDropped => Volatile.Read(ref _accessUnitsDropped);

    private long _keyframeRequests;

    /// <summary>Cumulative keyframe requests raised by this decoder (P-25). Incremented once per
    /// <see cref="SubmitAccessUnit"/> call that had to drop at least one queued access unit — per
    /// drop EPISODE, not per dropped unit: one damaged reference chain needs exactly one IDR to
    /// heal, however many units were lost — and once when decoding fails outright (the event
    /// loop dying on the asynchronous path, or a throw on the synchronous path). <c>CastReceiver</c>
    /// forwards this count in the periodic CastStatus report; the Caster forces an IDR on its
    /// encoder whenever the reported value grows, so a damaged picture heals on the next frame
    /// instead of waiting up to a full GOP for the next scheduled keyframe.</summary>
    public long KeyframeRequests => Interlocked.Read(ref _keyframeRequests);

    /// <summary>Raised whenever <see cref="KeyframeRequests"/> increments — on the caller's
    /// thread for the submit-side drop/synchronous-failure cases, on the event-loop thread when
    /// the loop dies. Handlers must be thread-safe; <c>CastReceiver</c> reads the count
    /// cumulatively rather than depending on this event, which exists for diagnostics/tests.</summary>
    public event Action? KeyframeRequested;

    private void NoteKeyframeRequest()
    {
        Interlocked.Increment(ref _keyframeRequests);
        KeyframeRequested?.Invoke();
    }

    /// <summary>True when the MFT reported MF_TRANSFORM_ASYNC and this class is running its event
    /// loop. Expected to be true for any hardware decoder; exposed so a diagnostic can assert which
    /// path was actually taken rather than assuming.</summary>
    public bool IsAsynchronous => _isAsynchronous;

    /// <summary>True once the asynchronous event loop has ended from an unhandled exception, so
    /// no further access unit will ever be decoded by this instance.
    ///
    /// This needs to be its own terminal flag rather than something a caller infers from an error
    /// count, for exactly the reason CastReceiver.PresentLoopFailed exists: a thread that dies
    /// raises DecodingFailed once and then stops, so an error COUNT sits at one forever while the
    /// pipeline is permanently dead. Program.CheckDecodeHealth disconnects at 90 consecutive
    /// errors, which a dead loop can never reach - without this flag a lost D3D11 device (the
    /// whole point of the per-frame TestDevice check) would leave an unattended Terminal frozen on
    /// its last decoded frame indefinitely, which is precisely the state that check exists to
    /// avoid.</summary>
    public bool DecodeLoopFailed => Volatile.Read(ref _decodeLoopFailed) != 0;

    /// <summary>True when no hardware decoder could be used and Windows' software H.264 decoder runs
    /// instead: frames are decoded in system memory and uploaded to a D3D11 texture for display.</summary>
    public bool UsesSoftwareDecoder { get; }

    /// <summary>Why the hardware decoder was not used, when <see cref="UsesSoftwareDecoder"/>.</summary>
    public string? HardwareUnavailableReason { get; }

    public H264HardwareDecoder(D3D11Device gpu, int width, int height)
    {
        _gpu = gpu;
        // A hardware (DXVA) decoder first. A machine without a usable one used to refuse every cast
        // with "No matching Media Foundation transform was found."; it now falls back to Windows' own
        // software decoder, the same way the Caster falls back to the software encoder.
        Exception? hardwareFailure = null;
        (IMFTransform Decoder, IMFMediaEventGenerator? Events, bool Async, bool OwnSamples, IntPtr Handle)? opened = null;
        try
        {
            opened = OpenHardware(gpu, width, height);
        }
        catch (Exception ex)
        {
            hardwareFailure = ex;
        }
        if (hardwareFailure != null)
        {
            try
            {
                opened = OpenSoftware(width, height);
                UsesSoftwareDecoder = true;
                HardwareUnavailableReason = hardwareFailure.Message;
            }
            catch (Exception softwareFailure)
            {
                throw new InvalidOperationException(
                    "终端机没有可用的 H.264 视频解码器，无法接收投屏。"
                    + $"显卡硬件解码：{Describe(hardwareFailure)}；Windows 自带软件解码：{Describe(softwareFailure)}。"
                    + "请更新显卡驱动；如果是 Windows N / KN 版，请先安装「媒体功能包」。", softwareFailure);
            }
        }
        var parts = opened!.Value;
        _decoder = parts.Decoder;
        _events = parts.Events;
        _isAsynchronous = parts.Async;
        _outputProvidesOwnSamples = parts.OwnSamples;
        _deviceHandle = parts.Handle;
        if (UsesSoftwareDecoder) ReadSoftwareOutputLayout();
        if (_isAsynchronous) _eventLoopTask = Task.Run(() => RunEventLoop(_cts.Token));
    }

    private static string Describe(Exception ex) => ex.Message.StartsWith("No matching Media Foundation transform", StringComparison.Ordinal)
        ? "没有找到" : ex.Message;

    private static (IMFTransform, IMFMediaEventGenerator?, bool, bool, IntPtr) OpenHardware(D3D11Device gpu, int width, int height)
    {
        var decoder = ActivateFirstHardwareDecoder(gpu.AdapterLuid);

        // ActivateFirstHardwareDecoder() above already called MediaFactory.MFStartup(), so everything
        // below is wrapped to keep that paired with an MFShutdown() if setup fails.
        IntPtr deviceHandle = IntPtr.Zero;
        IMFMediaEventGenerator? events = null;
        try
        {
            // ORDER IS LOAD-BEARING FROM HERE DOWN.
            // 1. Unlock first. Until this is set a hardware MFT rejects everything below with
            //    MF_E_TRANSFORM_ASYNC_LOCKED, including the attribute reads on the next line.
            UnlockAsyncProcessing(decoder);
            bool isAsynchronous = ReportsAsynchronous(decoder);
            RequestLowLatency(decoder);

            // 2. Refuse a transform that cannot take a DXGI device manager at all, rather than
            //    handing it one anyway and hoping. MF_SA_D3D11_AWARE == TRUE is the documented
            //    precondition for MFT_MESSAGE_SET_D3D_MANAGER.
            RequireD3D11Aware(decoder);

            // 3. Device manager BEFORE the media types: the MFT works out its hardware
            //    configuration inside SetInputType and needs the manager already in hand. This is
            //    the ordering whose absence degrades silently to software decoding.
            BindDeviceManager(decoder, gpu);
            ConfigureOutputStreamAllocation(decoder);

            // 4. Types last. A hardware decoder holding a manager but with no usable D3D11
            //    configuration answers MF_E_UNSUPPORTED_D3D_TYPE here.
            ConfigureInputType(decoder, width, height);
            ConfigureNv12OutputType(decoder);

            bool ownSamples = OutputProvidesOwnSamples(decoder);

            // Opened once and tested per frame (see EnsureDeviceStillValid).
            gpu.DeviceManager.OpenDeviceHandle(out deviceHandle);

            if (isAsynchronous) events = decoder.QueryInterface<IMFMediaEventGenerator>();

            decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return (decoder, events, isAsynchronous, ownSamples, deviceHandle);
        }
        catch
        {
            if (deviceHandle != IntPtr.Zero)
            {
                try { gpu.DeviceManager.CloseDeviceHandle(deviceHandle); } catch (Exception) { }
            }
            events?.Dispose();
            decoder.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Windows' synchronous software H.264 decoder, deliberately WITHOUT a device manager:
    /// on a machine whose GPU cannot decode H.264, handing it one only invites the same failure. It
    /// decodes into system memory; <see cref="EmitSoftwareOutput"/> uploads each frame.</summary>
    private static (IMFTransform, IMFMediaEventGenerator?, bool, bool, IntPtr) OpenSoftware(int width, int height)
    {
        MediaFactory.MFStartup().CheckError();
        IMFTransform? decoder = null;
        try
        {
            var inputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
            decoder = MediaFoundationHelpers.ActivateFirstTransform(
                MFT_CATEGORY_VIDEO_DECODER,
                EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter,
                inputType,
                null);
            MediaFoundationHelpers.UnlockAsyncIfSupported(decoder);
            RequestLowLatency(decoder);
            ConfigureInputType(decoder, width, height);
            ConfigureNv12OutputType(decoder);
            bool ownSamples = OutputProvidesOwnSamples(decoder);
            decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            return (decoder, null, false, ownSamples, IntPtr.Zero);
        }
        catch
        {
            decoder?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    // Software path: the negotiated NV12 frame (decoders pad to whole macroblocks, e.g. 1920x1088)
    // and its row pitch in the output buffer.
    private int _softwareWidth, _softwareHeight, _softwareStride;

    // MF_MT_DEFAULT_STRIDE and MF_E_TRANSFORM_STREAM_CHANGE.
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    private void ReadSoftwareOutputLayout()
    {
        using var type = _decoder.GetOutputCurrentType(OutputStreamId);
        ulong size = type.GetUInt64(MF_MT_FRAME_SIZE);
        _softwareWidth = (int)(size >> 32);
        _softwareHeight = (int)(size & 0xFFFFFFFF);
        int stride;
        try { stride = Math.Abs(unchecked((int)type.GetUInt32(MfMtDefaultStride))); }
        catch (Exception) { stride = 0; }
        _softwareStride = stride >= _softwareWidth ? stride : _softwareWidth;
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
        // Teardown is not an error. CastReceiver.Dispose disposes RtpReceiver first, but that only
        // waits two seconds for its receive loop to actually stop and returns either way, so a NAL
        // unit can still arrive here after this decoder has been disposed. Without this guard the
        // enqueue below would throw ObjectDisposedException off the semaphore, which CastReceiver
        // would faithfully record as a video decode failure - a spurious error message attached to
        // an ordinary shutdown.
        if (Volatile.Read(ref _disposed) != 0) return;

        if (!_isAsynchronous)
        {
            try
            {
                EnsureDeviceStillValid();
                SubmitAccessUnitCore(annexBAccessUnit, sampleTimeTicks);
                DrainOutput();
            }
            catch
            {
                // A decode failure on the synchronous path damages the reference chain exactly
                // like a drop does (P-25) — count a keyframe request before the exception
                // propagates to the caller, which records it as a decode error either way.
                // DrainOutput's normal "nothing to take" outcome is a returned false, not a
                // throw, so reaching this catch is a genuine failure.
                NoteKeyframeRequest();
                throw;
            }
            return;
        }

        bool dropped = false;
        while (_pending.Count >= MaxPendingAccessUnits && _pending.TryDequeue(out _))
        {
            Interlocked.Increment(ref _accessUnitsDropped);
            dropped = true;
        }
        // One request per SubmitAccessUnit call that dropped anything (P-25) — see
        // KeyframeRequests' own doc comment for why this counts episodes, not units.
        if (dropped) NoteKeyframeRequest();

        _pending.Enqueue((annexBAccessUnit, sampleTimeTicks));

        // Only signal a net increase in queue depth, matching H264HardwareEncoder.SubmitFrame's
        // accounting: a drop-then-enqueue leaves the depth unchanged, so releasing a permit for it
        // would drift the semaphore count permanently above _pending.Count. Every surplus permit
        // buys one wasted lap where the MFT asked for input, a permit was consumed, and the queue
        // turned out to be empty - harmless individually, unbounded over a long cast under
        // sustained backpressure.
        if (!dropped) _accessUnitAvailable.Release();
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
            // Set BEFORE raising, so a handler that reacts to the failure already sees the loop
            // as terminally dead rather than merely having reported one error.
            Volatile.Write(ref _decodeLoopFailed, 1);
            // The decode chain is broken (P-25): count a keyframe request alongside the failure,
            // before DecodingFailed, so a handler reacting to the failure already sees it.
            NoteKeyframeRequest();
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
        if (UsesSoftwareDecoder) return EmitSoftwareOutput();
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
            var handler = FrameDecoded;
            if (handler == null)
            {
                texture.Dispose();
                return true;
            }
            // The sample goes with the texture: releasing it hands this texture-array slice back to the
            // decoder, which may decode the next frame into it while the receiver still holds this one for
            // up to a second of A/V pacing (audit C-28). The handler releases both after presenting.
            var sample = ownedSample;
            ownedSample = null;
            handler(texture, arraySlice, (int)desc.Width, (int)desc.Height, sampleTimeTicks, sample);
            return true;
        }
        finally
        {
            ownedSample?.Dispose();
        }
    }

    /// <summary>Software path: one decoded NV12 frame out of the MFT (into a sample this class
    /// allocates, as the software decoder does not provide its own), uploaded into a fresh D3D11
    /// NV12 texture the presenter consumes exactly like a DXVA surface. A stream change (the decoder
    /// learning the real frame size from the first SPS) re-reads the output layout and asks again.</summary>
    private bool EmitSoftwareOutput()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IMFSample sample;
            if (_outputProvidesOwnSamples) sample = null!;
            else
            {
                var info = _decoder.GetOutputStreamInfo(OutputStreamId);
                var buffer = info.Alignment > 0
                    ? MediaFactory.MFCreateAlignedMemoryBuffer(info.Size, info.Alignment)
                    : MediaFactory.MFCreateMemoryBuffer(info.Size);
                sample = MediaFactory.MFCreateSample();
                using (buffer) sample.AddBuffer(buffer);
            }
            var outputBuffer = new OutputDataBuffer { StreamID = OutputStreamId, Sample = sample };
            var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);
            if (result.Code == MfETransformStreamChange)
            {
                outputBuffer.Sample?.Dispose();
                ConfigureNv12OutputType(_decoder);
                ReadSoftwareOutputLayout();
                continue;
            }
            if (result.Failure)
            {
                outputBuffer.Sample?.Dispose();
                return false;
            }

            IMFSample? owned = outputBuffer.Sample;
            try
            {
                if (owned == null) return false;
                ID3D11Texture2D texture;
                using (var contiguous = owned.ConvertToContiguousBuffer())
                {
                    contiguous.Lock(out var data, out _, out var length);
                    try { texture = UploadNv12(data, length); }
                    finally { contiguous.Unlock(); }
                }
                var handler = FrameDecoded;
                if (handler == null)
                {
                    texture.Dispose();
                    return true;
                }
                var handed = owned;
                owned = null;
                handler(texture, 0, _softwareWidth, _softwareHeight, handed.SampleTime, handed);
                return true;
            }
            finally
            {
                owned?.Dispose();
            }
        }
        return false;
    }

    private ID3D11Texture2D UploadNv12(IntPtr data, int length)
    {
        int width = _softwareWidth, height = _softwareHeight;
        // The decoder's buffer holds the Y plane (height rows) then the interleaved UV plane, at one
        // pitch; take the pitch the buffer length implies when the type did not state one.
        int pitch = _softwareStride;
        if ((long)pitch * height * 3 / 2 > length) pitch = Math.Max(width, length * 2 / (height * 3));
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        var initial = new SubresourceData(data, (uint)pitch, (uint)(pitch * height));
        try
        {
            return _gpu.Device.CreateTexture2D(desc, new[] { initial });
        }
        catch (Exception)
        {
            // Some drivers refuse a shader-resource NV12 texture; the video processor does not need it.
            desc.BindFlags = BindFlags.None;
            return _gpu.Device.CreateTexture2D(desc, new[] { initial });
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
        if (UsesSoftwareDecoder)
        {
            if (_gpu.Device.DeviceRemovedReason.Failure) throw new DeviceLostException();
            return;
        }
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

    private static IMFTransform ActivateFirstHardwareDecoder(long adapterLuid)
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            var inputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
            return MediaFoundationHelpers.ActivateFirstTransform(
                MFT_CATEGORY_VIDEO_DECODER,
                EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter,
                inputType,
                null,
                adapterLuid);
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

    /// <summary>MF_LOW_LATENCY: without it an H.264 decoder may hold several decoded frames back for
    /// reordering before releasing the first, which adds that many frame times of delay to a live cast
    /// whose stream never has B-frames to reorder. Best effort: a decoder that ignores the attribute
    /// just keeps its default.</summary>
    private static void RequestLowLatency(IMFTransform decoder)
    {
        try
        {
            using var attributes = decoder.Attributes;
            attributes.Set(MF_LOW_LATENCY, 1u);
        }
        catch (Exception) { }
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
