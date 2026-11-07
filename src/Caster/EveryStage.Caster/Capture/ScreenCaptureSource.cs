using System.Runtime.InteropServices;
using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EveryStage.Caster.Capture;

/// <summary>
/// Screen capture via the Desktop Duplication API (PLANNING.md §4.2 "屏幕捕获：Desktop Duplication
/// API (DDA)"). This is new, uncompiled DirectX interop — same risk category as the Phase 0 demo's
/// D3D11/Media Foundation code and written under the same constraint (this sandbox has no Windows
/// SDK/GPU to compile or run against). IDXGIOutputDuplication / OutputDuplicateFrameInformation
/// member names here are a best-effort guess at Vortice.DXGI's surface (it generally mirrors
/// SharpDX.DXGI naming) rather than something verified against the installed package — see this
/// project's README "已知风险" for the specific points to check first on a real build.
///
/// Known DDA behavior this class is built around (documented native Desktop Duplication semantics,
/// not guesses):
/// - <see cref="AcquireNextFrame"/> times out with nothing new when the screen genuinely hasn't
///   changed — that's DDA's normal "no frame yet" signal, not an error to handle specially.
/// - The texture DDA hands back is only valid until <c>ReleaseFrame()</c> is called, so this copies
///   it into a texture the caller owns before releasing — handing out a texture that becomes
///   invalid out from under the caller on the next Acquire would be a much worse bug to leave in
///   uncompiled, unverified code than the extra copy costs in performance.
/// - An acquire can succeed with only the cursor having moved and no new pixel data — see
///   <see cref="CapturedFrame.HasNewImage"/>.
/// - The desktop texture DDA hands back never contains the mouse pointer (it's not part of the
///   desktop image), so the pointer is composited in here, at capture time (P-28(a)): shape
///   data from GetFramePointerShape, converted by <see cref="PointerShapeConverter"/>, drawn
///   over the copied frame by an ID3D11VideoProcessor two-stream blend — the same video-
///   processor technique <c>BgraToNv12Converter</c> already uses, and the reason the pooled
///   copy textures carry a RenderTarget bind flag. The Terminal side needs no change at all:
///   the pointer simply arrives as part of the picture. UNVERIFIED on a real machine: whether
///   a given driver actually blends the foreground stream by its per-pixel alpha is
///   implementation-defined — if it doesn't, the symptom is a pointer drawn over an opaque
///   box (or no blend), never a capture failure, because any video-processor error falls back
///   to the plain copy for that frame.
/// - DXGI_ERROR_ACCESS_LOST is a real, expected failure mode (see <see cref="ScreenCaptureLostException"/>)
///   that permanently invalidates this instance; there is no in-place recovery from it.
/// </summary>
public sealed class ScreenCaptureSource : IDisposable
{
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private readonly D3D11Device _gpu;
    private readonly IDXGIOutputDuplication _duplication;

    // P-32 texture ring: AcquireNextFrame used to CreateTexture2D a fresh full-screen texture
    // for every frame (and the caller disposed it when done) — a GPU allocation plus driver
    // round-trip per frame at up to the display's refresh rate, for a texture whose content is
    // fully rewritten by CopyResource every time anyway. Pool depth 5, sized off the pipeline's
    // in-flight frame budget: the encoder's input queue holds at most 3 frames (see
    // H264HardwareEncoder's MaxPendingFrames), plus 1 frame being converted while its capture
    // texture is still checked out, plus 1 being acquired/filled here. Both consumers use the
    // texture strictly synchronously (LiveCastSession converts inside its `using (frame)` block;
    // EncodeSelfTestRunner likewise), so a checked-out slot is always returned before the ring
    // can wrap in practice; if every slot is somehow still out, AcquireNextFrame falls back to
    // an ordinary caller-owned allocation rather than stalling capture. Textures are created
    // lazily from the first frame's description and recreated if the description ever changes
    // (mode switch); idle slots are disposed with this source, checked-out ones on return.
    private const int TexturePoolSize = 5;
    private readonly ID3D11Texture2D?[] _poolTextures = new ID3D11Texture2D?[TexturePoolSize];
    private readonly bool[] _poolInUse = new bool[TexturePoolSize];
    private readonly object _poolGate = new();
    private int _poolNext;
    private bool _disposed;

    // P-28(a) pointer compositing state. The shape cache (_pointerBgra & friends) is updated
    // from each frame's PointerShapeBufferSize before that frame is released; the pointer
    // texture and video processor are created lazily on the first frame that actually needs
    // to draw a pointer, so a cast where the cursor never shows pays nothing. _deliveredPointer
    // records the pointer state the last RETURNED frame actually displayed (after fallback
    // decisions), which is what CapturedFrame.HasNewImage compares against.
    private byte[]? _pointerShapeScratch;
    private byte[]? _pointerBgra;
    private int _pointerShapeWidth, _pointerShapeHeight;
    private int _pointerHotSpotX, _pointerHotSpotY;
    private int _pointerShapeVersion;
    private ID3D11Texture2D? _pointerTexture;
    private int _pointerTextureVersion;
    private ID3D11VideoDevice? _pointerVideoDevice;
    private ID3D11VideoContext? _pointerVideoContext;
    private ID3D11VideoProcessorEnumerator? _pointerVpEnumerator;
    private ID3D11VideoProcessor? _pointerVp;
    private PointerVisualState _deliveredPointer = PointerVisualState.Hidden;

    /// <summary>The pointer state one delivered frame actually shows. Hidden stands for "no
    /// pointer in the picture" — including the fallback case where compositing failed and the
    /// frame went out as a plain desktop copy, so the next frame retries instead of believing
    /// the pointer is already on screen.</summary>
    private readonly record struct PointerVisualState(bool Visible, int X, int Y, int ShapeVersion)
    {
        public static readonly PointerVisualState Hidden = new(false, 0, 0, 0);
    }

    public int Width { get; }
    public int Height { get; }

    /// <param name="outputIndex">Which monitor to capture, in DXGI adapter-output enumeration
    /// order — see <see cref="EnumerateOutputs"/> for listing what's actually available before
    /// picking one, rather than assuming index 0 is always right. Defaults to 0 (typically the
    /// primary display) for callers (the two self-tests) that don't offer a picker at all.</param>
    public ScreenCaptureSource(D3D11Device gpu, int outputIndex = 0)
    {
        _gpu = gpu;

        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        adapter.EnumOutputs((uint)outputIndex, out var output).CheckError();
        using (output)
        {
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        _duplication = output1.DuplicateOutput(gpu.Device);
        }

        var desc = _duplication.Description;
        Width = (int)desc.ModeDescription.Width;
        Height = (int)desc.ModeDescription.Height;
    }

    /// <summary>One entry of <see cref="EnumerateOutputs"/> — <see cref="OutputIndex"/> is exactly
    /// what this class's own constructor expects for <c>outputIndex</c>, since both come from the
    /// same <c>adapter.GetOutput(i)</c> enumeration rather than some other API's independently
    /// numbered monitor list (see PLANNING.md §12's UI needing "选择捕获哪个显示器" — this exists so
    /// that picker can show real monitors instead of a caller guessing indices blind).</summary>
    public readonly record struct MonitorCaptureOption(int OutputIndex, string DeviceName, Rectangle Bounds);

    /// <summary>Lists every DXGI output (monitor) on <paramref name="gpu"/>'s adapter that a
    /// <see cref="ScreenCaptureSource"/> could be constructed against — the UI-facing counterpart to
    /// this class's own constructor always defaulting to output 0. NOTE: <c>IDXGIOutput.Description</c>
    /// (<c>OutputDescription</c>'s exact field names — <c>DeviceName</c>, <c>DesktopCoordinates</c>)
    /// is new, unverified Vortice.DXGI surface this session has never exercised before, in the same
    /// "guessed from how SharpDX/Vortice generally name DXGI members" risk category as
    /// <see cref="IDXGIOutputDuplication"/>/<c>OutputDuplicateFrameInformation</c> elsewhere in this
    /// class — see this project's README "已知风险" for what to check first on a real build. Looping
    /// <c>adapter.GetOutput(i)</c> until it throws (rather than some dedicated output-count query) is
    /// the same "loop until the API itself signals exhaustion" pattern
    /// <c>H264HardwareDecoder.ConfigureNv12OutputType</c> already uses for enumerating output media
    /// types — the real native exhaustion signal is the HRESULT <c>DXGI_ERROR_NOT_FOUND</c>, caught
    /// broadly here since this session can't verify what shape Vortice surfaces that as.</summary>
    public static IReadOnlyList<MonitorCaptureOption> EnumerateOutputs(D3D11Device gpu)
    {
        var results = new List<MonitorCaptureOption>();

        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        for (int i = 0; ; i++)
        {
            IDXGIOutput output;
            try
            {
                adapter.EnumOutputs((uint)i, out output).CheckError();
            }
            catch (Exception)
            {
                break; // no more outputs on this adapter.
            }

            using (output)
            {
                var desc = output.Description;
                var coords = desc.DesktopCoordinates;
                var bounds = new Rectangle(coords.Left, coords.Top, coords.Right - coords.Left, coords.Bottom - coords.Top);
                results.Add(new MonitorCaptureOption(i, desc.DeviceName, bounds));
            }
        }

        return results;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the next frame. Returns null on a normal
    /// timeout (nothing changed) — the common case at low desktop activity, not an error.
    /// </summary>
    /// <exception cref="ScreenCaptureLostException">The duplication interface is permanently gone;
    /// recreate this <see cref="ScreenCaptureSource"/> entirely rather than retrying the call.</exception>
    public CapturedFrame? AcquireNextFrame(int timeoutMs = 500)
    {
        var callResult = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
        if (callResult == Vortice.DXGI.ResultCode.WaitTimeout) return null;

        try
        {
            callResult.CheckError();
        }
        catch (Exception ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
        {
            throw new ScreenCaptureLostException(ex);
        }

        // Only reached on a genuinely successful acquire — DDA's contract is that ReleaseFrame()
        // corresponds 1:1 with a successful AcquireNextFrame, so it belongs in this block, not
        // wrapping the CheckError() call above (a failed acquire has nothing to release).
        try
        {
            using var sourceTexture = desktopResource.QueryInterface<ID3D11Texture2D>();

            // P-28(a): fold any new pointer shape this frame carries into the cache now —
            // the shape belongs to this acquired frame and must be fetched before
            // ReleaseFrame() runs in the finally below.
            UpdatePointerShape(frameInfo);

            var textureDesc = sourceTexture.Description;
            textureDesc.Usage = ResourceUsage.Default;
            // RenderTarget is new with P-28(a): the pooled copy doubles as the video
            // processor's output target when the pointer is blended into it. The encoder
            // still only ever consumes the copy as a ShaderResource, exactly as before.
            textureDesc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
            textureDesc.CPUAccessFlags = CpuAccessFlags.None;
            textureDesc.MiscFlags = ResourceOptionFlags.None;

            var (copy, pooled) = RentPooledTexture(textureDesc);
            PointerVisualState renderedPointer;
            try
            {
                renderedPointer = FillCopy(copy, sourceTexture, frameInfo);
            }
            catch
            {
                // Filling the copy threw after the texture was obtained — release it here or it leaks
                // a full-frame GPU texture on every failed acquire (this runs once per captured
                // frame, so a persistent failure would exhaust GPU memory), since it never reaches
                // the CapturedFrame the caller would otherwise Dispose (audit C-24). A pooled
                // texture goes back to the ring; an unpooled fallback is disposed outright.
                if (pooled) ReturnPooledTexture(copy);
                else copy.Dispose();
                throw;
            }

            // HasNewImage's pointer half: a frame whose only change is the baked-in pointer
            // (moved/shown/hidden/new shape) has LastPresentTime == 0 but different pixels
            // from the frame the caller last got — report it as new so it is encoded/sent
            // instead of skipped (see CapturedFrame.HasNewImage).
            bool hasNewImage = frameInfo.LastPresentTime != 0 || renderedPointer != _deliveredPointer;
            _deliveredPointer = renderedPointer;

            return new CapturedFrame(copy, Width, Height, hasNewImage,
                pooled ? ReturnPooledTexture : null);
        }
        finally
        {
            desktopResource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>P-28(a): if this frame carries a new pointer shape (PointerShapeBufferSize
    /// &gt; 0), fetch and convert it into the cache. Any failure keeps the previously converted
    /// shape — a stale cursor shape for a while beats failing the whole acquire.</summary>
    private void UpdatePointerShape(OutduplFrameInfo frameInfo)
    {
        if (frameInfo.PointerShapeBufferSize == 0) return;

        try
        {
            if (_pointerShapeScratch == null || _pointerShapeScratch.Length < frameInfo.PointerShapeBufferSize)
                _pointerShapeScratch = new byte[frameInfo.PointerShapeBufferSize];

            var handle = GCHandle.Alloc(_pointerShapeScratch, GCHandleType.Pinned);
            try
            {
                var result = _duplication.GetFramePointerShape((uint)_pointerShapeScratch.Length,
                    handle.AddrOfPinnedObject(), out uint dataSize, out var info);
                if (result == Vortice.DXGI.ResultCode.MoreData)
                {
                    // Scratch buffer smaller than the shape needs — grow to exactly the size
                    // the driver asked for and retry once (dataSize now holds the requirement).
                    handle.Free();
                    _pointerShapeScratch = new byte[dataSize];
                    handle = GCHandle.Alloc(_pointerShapeScratch, GCHandleType.Pinned);
                    result = _duplication.GetFramePointerShape((uint)_pointerShapeScratch.Length,
                        handle.AddrOfPinnedObject(), out dataSize, out info);
                }
                result.CheckError();

                var converted = PointerShapeConverter.ConvertToBgra(
                    _pointerShapeScratch.AsSpan(0, (int)dataSize), info.Type,
                    (int)info.Width, (int)info.Height, (int)info.Pitch);
                if (converted is { } shape)
                {
                    _pointerBgra = shape.BgraPixels;
                    _pointerShapeWidth = shape.Width;
                    _pointerShapeHeight = shape.Height;
                    _pointerHotSpotX = info.HotSpot.X;
                    _pointerHotSpotY = info.HotSpot.Y;
                    _pointerShapeVersion++;
                }
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed: pointer shape retrieval is best-effort decoration on top
            // of the desktop image, never a reason to fail capture (see class doc comment).
        }
    }

    /// <summary>P-28(a): fills <paramref name="copy"/> with the desktop image plus the pointer
    /// blended in, and reports the pointer state the filled copy actually ended up showing.
    /// Falls back to a plain copy (and reports Hidden) whenever no pointer should be drawn,
    /// the pointer is clipped entirely off-screen, or compositing itself fails.</summary>
    private PointerVisualState FillCopy(ID3D11Texture2D copy, ID3D11Texture2D desktop, OutduplFrameInfo frameInfo)
    {
        var position = frameInfo.PointerPosition;
        if (position.Visible && _pointerBgra != null)
        {
            // DDA reports Position relative to this output's top-left corner; the texture
            // position the user perceives as "the pointer" is its hotspot, not its top-left.
            int destX = position.Position.X - _pointerHotSpotX;
            int destY = position.Position.Y - _pointerHotSpotY;
            int srcX = Math.Max(0, -destX);
            int srcY = Math.Max(0, -destY);
            int dstX = Math.Max(0, destX);
            int dstY = Math.Max(0, destY);
            int w = Math.Min(_pointerShapeWidth - srcX, Width - dstX);
            int h = Math.Min(_pointerShapeHeight - srcY, Height - dstY);
            if (w > 0 && h > 0 && TryCompositePointer(copy, desktop, srcX, srcY, dstX, dstY, w, h))
                return new PointerVisualState(true, position.Position.X, position.Position.Y, _pointerShapeVersion);
        }

        _gpu.ImmediateContext.CopyResource(copy, desktop);
        return PointerVisualState.Hidden;
    }

    /// <summary>Two-stream video-processor blend: stream 0 = desktop full-frame, stream 1 =
    /// the pointer texture clipped to (srcX, srcY, w, h), placed at (dstX, dstY). Returns false
    /// on ANY failure — the caller then falls back to a plain copy for this frame, because a
    /// frame missing its pointer is a cosmetic defect while a stalled capture is a broken
    /// cast. NOTE (unverified, needs a real machine): this relies on the video processor
    /// blending stream 1 by its texture's per-pixel alpha, which is driver-implemented
    /// behavior, not something the API contract guarantees — see the class doc comment.</summary>
    private bool TryCompositePointer(ID3D11Texture2D copy, ID3D11Texture2D desktop,
        int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        try
        {
            EnsurePointerTexture();
            EnsurePointerProcessor();
            var videoDevice = _pointerVideoDevice!;
            var videoContext = _pointerVideoContext!;
            var processor = _pointerVp!;

            // Per-call views, same pattern BgraToNv12Converter.Convert already uses for its
            // input view — view creation is cheap next to a full-screen blt, and per-call
            // ownership sidesteps every cache-invalidation question across the texture ring.
            using var desktopView = videoDevice.CreateVideoProcessorInputView(desktop, _pointerVpEnumerator!, PointerInputViewDescription);
            using var pointerView = videoDevice.CreateVideoProcessorInputView(_pointerTexture!, _pointerVpEnumerator!, PointerInputViewDescription);
            using var outputView = videoDevice.CreateVideoProcessorOutputView(copy, _pointerVpEnumerator!, PointerOutputViewDescription);

            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RectI(0, 0, Width, Height));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RectI(0, 0, Width, Height));
            videoContext.VideoProcessorSetStreamSourceRect(processor, 1, true, new RectI(srcX, srcY, srcX + w, srcY + h));
            videoContext.VideoProcessorSetStreamDestRect(processor, 1, true, new RectI(dstX, dstY, dstX + w, dstY + h));

            var streams = new[]
            {
                new VideoProcessorStream { Enable = true, InputSurface = desktopView },
                new VideoProcessorStream { Enable = true, InputSurface = pointerView },
            };
            videoContext.VideoProcessorBlt(processor, outputView, 0, 2, streams);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly VideoProcessorInputViewDescription PointerInputViewDescription = new()
    {
        FourCC = 0, // 0 = use the source texture's own format (BGRA) as-is.
        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
    };

    private static readonly VideoProcessorOutputViewDescription PointerOutputViewDescription = new()
    {
        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
    };

    /// <summary>(Re)builds the pointer texture from the converted shape cache, only when the
    /// shape version moved since the texture was built.</summary>
    private void EnsurePointerTexture()
    {
        if (_pointerTexture != null && _pointerTextureVersion == _pointerShapeVersion) return;

        _pointerTexture?.Dispose();
        _pointerTexture = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)_pointerShapeWidth,
            Height = (uint)_pointerShapeHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        var handle = GCHandle.Alloc(_pointerBgra!, GCHandleType.Pinned);
        try
        {
            uint rowPitch = (uint)(_pointerShapeWidth * 4);
            _pointerTexture = _gpu.Device.CreateTexture2D(desc,
                new SubresourceData(handle.AddrOfPinnedObject(), rowPitch, rowPitch * (uint)_pointerShapeHeight));
        }
        finally
        {
            handle.Free();
        }
        _pointerTextureVersion = _pointerShapeVersion;
    }

    /// <summary>Lazy one-time video processor for compositing, sized to this source's desktop
    /// (Width/Height never change over a source's lifetime — a mode change is a new source).
    /// Partial progress is kept in the fields on failure so Dispose can clean it up, mirroring
    /// BgraToNv12Converter's constructor leak-avoidance shape.</summary>
    private void EnsurePointerProcessor()
    {
        if (_pointerVp != null) return;

        _pointerVideoDevice ??= _gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _pointerVideoContext ??= _gpu.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)Width,
            InputHeight = (uint)Height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _pointerVpEnumerator ??= _pointerVideoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _pointerVp = _pointerVideoDevice.CreateVideoProcessor(_pointerVpEnumerator, 0);
    }

    private (ID3D11Texture2D Texture, bool Pooled) RentPooledTexture(Texture2DDescription desc)
    {
        lock (_poolGate)
        {
            for (int i = 0; i < TexturePoolSize; i++)
            {
                int slot = (_poolNext + i) % TexturePoolSize;
                if (_poolInUse[slot]) continue;
                var texture = _poolTextures[slot];
                if (texture != null && !SameTextureShape(texture.Description, desc))
                {
                    // The desktop's mode changed under a live source — the pooled texture no
                    // longer matches what CopyResource needs; replace it rather than copying
                    // into a wrongly-shaped texture.
                    texture.Dispose();
                    _poolTextures[slot] = null;
                    texture = null;
                }
                texture ??= _gpu.Device.CreateTexture2D(desc);
                _poolTextures[slot] = texture;
                _poolInUse[slot] = true;
                _poolNext = (slot + 1) % TexturePoolSize;
                return (texture, true);
            }
        }
        // Every slot is checked out — fall back to an unpooled, caller-owned texture (its
        // CapturedFrame gets no release callback and disposes it, as before the pool existed).
        return (_gpu.Device.CreateTexture2D(desc), false);
    }

    private void ReturnPooledTexture(ID3D11Texture2D texture)
    {
        lock (_poolGate)
        {
            for (int slot = 0; slot < TexturePoolSize; slot++)
            {
                if (!ReferenceEquals(_poolTextures[slot], texture)) continue;
                _poolInUse[slot] = false;
                if (_disposed)
                {
                    // Returned after the source was disposed: no pool left to return to —
                    // dispose for real rather than parking it in a dead ring.
                    _poolTextures[slot] = null;
                    texture.Dispose();
                }
                return;
            }
            // Not one of the ring's textures (unreachable through CapturedFrame's callback
            // wiring) — dispose rather than leak.
            texture.Dispose();
        }
    }

    private static bool SameTextureShape(Texture2DDescription a, Texture2DDescription b) =>
        a.Width == b.Width && a.Height == b.Height && a.Format == b.Format
        && a.MipLevels == b.MipLevels && a.ArraySize == b.ArraySize
        && a.SampleDescription.Count == b.SampleDescription.Count
        && a.SampleDescription.Quality == b.SampleDescription.Quality
        && a.Usage == b.Usage && a.BindFlags == b.BindFlags
        && a.CPUAccessFlags == b.CPUAccessFlags && a.MiscFlags == b.MiscFlags;

    public void Dispose()
    {
        lock (_poolGate)
        {
            _disposed = true;
            for (int slot = 0; slot < TexturePoolSize; slot++)
            {
                // Slots still checked out are NOT disposed here — a caller may be mid-conversion
                // with one; ReturnPooledTexture disposes them when they come back.
                if (_poolInUse[slot]) continue;
                _poolTextures[slot]?.Dispose();
                _poolTextures[slot] = null;
            }
        }
        // P-28(a): the compositing resources are used only on the capture thread inside
        // AcquireNextFrame, so by the time Dispose runs no blt can be in flight against them.
        _pointerTexture?.Dispose();
        _pointerVp?.Dispose();
        _pointerVpEnumerator?.Dispose();
        _pointerVideoContext?.Dispose();
        _pointerVideoDevice?.Dispose();
        _duplication.Dispose();
    }
}
