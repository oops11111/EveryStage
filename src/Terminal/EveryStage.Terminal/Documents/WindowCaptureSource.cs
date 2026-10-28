using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EveryStage.Terminal.Documents;

public enum WindowCaptureMethod { Auto, GraphicsCapture, PrintWindow }

/// <summary>A captured frame shared by reference between the Preview and Program surfaces (a frozen
/// Program keeps showing an older frame while Preview moves on). Whoever keeps a frame holds a reference
/// (<see cref="AddRef"/>) and calls <see cref="Release"/> when it stops showing it; the bitmap is disposed
/// with the last reference.</summary>
public sealed class SharedFrame
{
    private int _refs = 1;

    public SharedFrame(Bitmap bitmap, long sequence)
    {
        Bitmap = bitmap;
        Sequence = sequence;
    }

    public Bitmap Bitmap { get; }
    public long Sequence { get; }

    public SharedFrame AddRef()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Bitmap.Dispose();
    }
}

/// <summary>
/// Captures one application window (by HWND) into bitmaps, cropped to its document area — how a WPS/Word
/// editor or a slide-show window reaches the Preview and Program outputs without that application's own
/// window ever being placed on the extended display.
///
/// Prefers Windows Graphics Capture (DWM composition: DirectX content, no cursor, keeps working while the
/// window is behind other windows). Falls back to PrintWindow(PW_RENDERFULLCONTENT) polling when WGC is
/// unavailable or fails to start. On Windows 10 WGC draws a yellow border around the captured window on the
/// operator's screen (hiding it needs Windows 11); the border never appears in the captured frames.
///
/// One worker thread does all the work (pulls the newest WGC frame or calls PrintWindow, at most ~30 times a
/// second), so the D3D11 immediate context is only ever used from that thread. Frames are published to
/// <see cref="AcquireLatest"/> and announced with <see cref="FrameAvailable"/> (raised on the worker; the
/// consumer marshals to its UI thread and takes the latest frame, so bursts coalesce). When the window goes
/// away, <see cref="Disconnected"/> is raised once and the last frame stays available.
/// </summary>
public sealed class WindowCaptureSource : IDisposable
{
    private const int FrameIntervalMs = 33;

    private readonly IntPtr _window;
    private readonly Func<Rectangle?>? _contentRegion;
    private readonly object _gate = new(); // guards _latest
    private SharedFrame? _latest;
    private long _sequence;
    private volatile bool _stopping;
    private bool _disposed;
    private int _disconnected;
    private Thread? _worker;

    // Graphics Capture state (created on Start, used only by the worker afterwards).
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;

    public WindowCaptureSource(IntPtr window, Func<Rectangle?>? contentRegion = null)
    {
        _window = window;
        _contentRegion = contentRegion;
    }

    public IntPtr Window => _window;
    public WindowCaptureMethod ActiveMethod { get; private set; } = WindowCaptureMethod.Auto;
    public long FrameCount => Interlocked.Read(ref _sequence);
    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    /// <summary>Why Graphics Capture was not used (null when it is running).</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>Raised on the worker thread whenever a newer frame is available.</summary>
    public event Action? FrameAvailable;

    /// <summary>Raised once, on the worker thread, when the captured window is gone.</summary>
    public event Action<string>? Disconnected;

    /// <summary>Most recent frame with a reference taken for the caller (release it), or null.</summary>
    public SharedFrame? AcquireLatest()
    {
        lock (_gate) return _latest?.AddRef();
    }

    public void Start(WindowCaptureMethod method = WindowCaptureMethod.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker != null) return;
        if (!NativeMethods.IsWindow(_window)) throw new InvalidOperationException("要捕获的窗口不存在。");
        if (method != WindowCaptureMethod.PrintWindow)
        {
            try
            {
                StartGraphicsCapture();
                ActiveMethod = WindowCaptureMethod.GraphicsCapture;
            }
            catch (Exception ex) when (method == WindowCaptureMethod.Auto)
            {
                ReleaseGraphicsCapture();
                FallbackReason = ex.Message;
            }
        }
        if (ActiveMethod != WindowCaptureMethod.GraphicsCapture) ActiveMethod = WindowCaptureMethod.PrintWindow;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "EveryStage window capture" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            long started = Environment.TickCount64;
            if (!NativeMethods.IsWindow(_window))
            {
                SignalDisconnected("窗口已关闭");
                return;
            }
            try
            {
                Bitmap? bitmap = ActiveMethod == WindowCaptureMethod.GraphicsCapture ? PullGraphicsFrame()
                    : NativeMethods.IsIconic(_window) ? null : CapturePrintWindow();
                if (bitmap != null) Publish(bitmap);
            }
            catch (Exception) when (!_stopping)
            {
                // One bad frame (window resizing mid-copy, device hiccup) is skipped; a dead window is
                // reported by the IsWindow check above.
            }
            int wait = FrameIntervalMs - (int)(Environment.TickCount64 - started);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    // ── Windows Graphics Capture ─────────────────────────────────────────────────────────────────

    private void StartGraphicsCapture()
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("此系统不支持 Windows Graphics Capture。");
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
            try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        _item = CreateItemForWindow(_window);
        _item.Closed += (_, _) => SignalDisconnected("窗口已关闭");
        _poolSize = _item.Size;
        // Free-threaded pool, polled by the worker with TryGetNextFrame (no FrameArrived handler), so the
        // pool's own threads never race the worker for the device context.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch (Exception) { } // Windows 10 2004+
        _session.StartCapture();
    }

    private Bitmap? PullGraphicsFrame()
    {
        using var frame = _framePool!.TryGetNextFrame();
        if (frame == null) return null;
        var size = frame.ContentSize;
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;
            _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }
        using var texture = TextureFromSurface(frame.Surface);
        var textureSize = new Size(Math.Min(size.Width, (int)texture.Description.Width), Math.Min(size.Height, (int)texture.Description.Height));
        var crop = CropInTexture(textureSize, NativeMethods.GetVisibleFrame(_window).Location);
        if (crop.Width < 2 || crop.Height < 2) return null;

        if (_staging == null || _staging.Description.Width != crop.Width || _staging.Description.Height != crop.Height)
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)crop.Width, (uint)crop.Height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        }
        _context!.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0,
            new Vortice.Mathematics.Box(crop.Left, crop.Top, 0, crop.Right, crop.Bottom, 1));
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppRgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < crop.Height; y++)
                    NativeMethods.CopyMemory(data.Scan0 + y * data.Stride, mapped.DataPointer + (int)(y * mapped.RowPitch), (uint)(crop.Width * 4));
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        finally { _context.Unmap(_staging, 0); }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Guid interopId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, ref interopId, out IntPtr interop));
        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size));
            Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            Marshal.ThrowExceptionForHR(create(interop, window, ref itemId, out IntPtr itemPtr));
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(interop); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowFn(IntPtr self, IntPtr window, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);

    private static ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"); // IDirect3DDxgiInterfaceAccess
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref accessId, out IntPtr access));
            try
            {
                var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
                Guid textureId = typeof(ID3D11Texture2D).GUID;
                Marshal.ThrowExceptionForHR(getInterface(access, ref textureId, out IntPtr texture));
                return new ID3D11Texture2D(texture);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unknown); }
    }

    private void ReleaseGraphicsCapture()
    {
        try { _session?.Dispose(); } catch (Exception) { }
        try { _framePool?.Dispose(); } catch (Exception) { }
        _session = null;
        _framePool = null;
        _item = null;
        _staging?.Dispose();
        _staging = null;
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch (Exception) { }
        _winrtDevice = null;
        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
    }

    // ── PrintWindow fallback ─────────────────────────────────────────────────────────────────────

    private Bitmap? CapturePrintWindow()
    {
        var windowRect = NativeMethods.WindowRect(_window);
        if (windowRect.Width < 2 || windowRect.Height < 2) return null;
        using var whole = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(whole))
        {
            IntPtr hdc = g.GetHdc();
            try { if (!NativeMethods.PrintWindow(_window, hdc, 2 /* PW_RENDERFULLCONTENT */)) return null; }
            finally { g.ReleaseHdc(hdc); }
        }
        var crop = CropInTexture(windowRect.Size, windowRect.Location);
        return whole.Clone(crop, PixelFormat.Format32bppRgb);
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Document area inside a capture whose top-left corner is at <paramref name="origin"/> on
    /// screen (WGC frames cover the window's visible frame; PrintWindow covers its full window rectangle).</summary>
    private Rectangle CropInTexture(Size captureSize, Point origin)
    {
        var full = new Rectangle(Point.Empty, captureSize);
        if (_contentRegion?.Invoke() is not { } region) return full;
        var crop = new Rectangle(region.X - origin.X, region.Y - origin.Y, region.Width, region.Height);
        crop.Intersect(full);
        return crop.Width >= 2 && crop.Height >= 2 ? crop : full;
    }

    private void Publish(Bitmap bitmap)
    {
        SharedFrame? previous;
        lock (_gate)
        {
            if (_disposed) { bitmap.Dispose(); return; }
            previous = _latest;
            _latest = new SharedFrame(bitmap, Interlocked.Increment(ref _sequence));
        }
        previous?.Release();
        try { FrameAvailable?.Invoke(); } catch (Exception) { }
    }

    private void SignalDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0 || _disposed) return;
        try { Disconnected?.Invoke(reason); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stopping = true;
        if (_worker != null && _worker != Thread.CurrentThread) _worker.Join(2000);
        _disposed = true;
        ReleaseGraphicsCapture();
        SharedFrame? latest;
        lock (_gate) { latest = _latest; _latest = null; }
        latest?.Release();
    }
}
