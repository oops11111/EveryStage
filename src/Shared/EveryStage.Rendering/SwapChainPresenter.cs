using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EveryStage.Rendering;

/// <summary>
/// Presents decoded NV12 DXVA output textures straight to the window's swap chain using the
/// GPU video processor (ID3D11VideoProcessor), i.e. no ID3D11DeviceContext.Map / CPU readback and
/// no separate "download to system memory then re-upload" step. This is what PLANNING.md §4.2
/// means by "D3D11纹理直出 → DXGI SwapChain呈现，全程零拷贝": the only GPU-side work between the
/// decoder's output texture and the screen is a single color-convert + scale blit.
///
/// The swap chain itself is created once and kept alive for the life of the window (matches the
/// "断" behavior described in PLANNING.md §9.2 for the real Terminal: hide/show, don't
/// destroy/recreate) — only Resize() is called on window-size changes.
/// </summary>
public sealed class SwapChainPresenter : IDisposable
{
    private readonly D3D11Device _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private IDXGISwapChain1 _swapChain;
    private ID3D11Texture2D _backBuffer;
    private ID3D11VideoProcessorOutputView? _outputView;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _inputWidth, _inputHeight, _outputWidth, _outputHeight;

    public SwapChainPresenter(D3D11Device gpu, IntPtr hwnd, int width, int height)
    {
        _gpu = gpu;

        // Bug fixed here: same "step N succeeds and gets kept, step N+1 throws, nothing disposes
        // step N" shape as D3D11Device's own constructor fix (see that class's doc comment) — this
        // constructor chains four separate COM/DXGI resources (_videoDevice/_videoContext/
        // _swapChain/_backBuffer) with no rollback if a later one fails, e.g. CreateSwapChainForHwnd
        // rejecting this hwnd/format combination after _videoDevice/_videoContext already succeeded.
        // A caller whose `new SwapChainPresenter(...)` throws never gets an instance back to
        // Dispose() whichever of these already succeeded — each would otherwise leak a live COM/GPU
        // resource for the rest of the process's life. Deliberately does NOT dispose _gpu on
        // failure here — this class never owns it (see this class's own field/VideoSurface's doc
        // comment on why), only the four resources this constructor itself creates.
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11Texture2D? backBuffer = null;
        try
        {
            videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
            videoContext = gpu.ImmediateContext.QueryInterface<ID3D11VideoContext>();

            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipDiscard,
                SampleDescription = new SampleDescription(1, 0),
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            };

            swapChain = gpu.DxgiFactory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc);
            backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);

            _videoDevice = videoDevice;
            _videoContext = videoContext;
            _swapChain = swapChain;
            _backBuffer = backBuffer;
            _outputWidth = width;
            _outputHeight = height;
            CreateOutputView();
        }
        catch
        {
            backBuffer?.Dispose();
            swapChain?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            throw;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _outputView?.Dispose();
        _outputView = null;
        _backBuffer.Dispose();

        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _outputWidth = width;
        _outputHeight = height;
        CreateOutputView();
        // Output size changed -> processor/enumerator sized for the old target must be rebuilt.
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    /// <summary>
    /// Blits one decoded frame (an NV12 texture-array slot from the DXVA decoder pool, identified
    /// by <paramref name="arraySlice"/>) directly onto the current back buffer, then presents.
    /// </summary>
    public void PresentFrame(ID3D11Texture2D decodedTexture, int arraySlice, int frameWidth, int frameHeight, bool vsync)
    {
        EnsureProcessor(frameWidth, frameHeight);

        using var inputView = CreateInputView(decodedTexture, arraySlice);

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = inputView,
        };

        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RectI(0, 0, frameWidth, frameHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RectI(0, 0, _outputWidth, _outputHeight));
        _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, new[] { stream });

        _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);
    }

    private void EnsureProcessor(int frameWidth, int frameHeight)
    {
        if (_processor != null && _inputWidth == frameWidth && _inputHeight == frameHeight) return;

        _processor?.Dispose();
        _enumerator?.Dispose();

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)frameWidth,
            InputHeight = (uint)frameHeight,
            OutputWidth = (uint)_outputWidth,
            OutputHeight = (uint)_outputHeight,
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _inputWidth = frameWidth;
        _inputHeight = frameHeight;

        // The enumerator now exists; (re)build the output view that CreateOutputView() deferred
        // in the constructor (and after every Resize()) while _enumerator was still null.
        _outputView?.Dispose();
        CreateOutputView();
    }

    private ID3D11VideoProcessorInputView CreateInputView(ID3D11Texture2D texture, int arraySlice)
    {
        var desc = new VideoProcessorInputViewDescription
        {
            FourCC = 0, // 0 = use the texture's own format (NV12) as produced by the decoder.
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)arraySlice },
        };
        return _videoDevice.CreateVideoProcessorInputView(texture, _enumerator!, desc);
    }

    private void CreateOutputView()
    {
        var desc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        };
        // Recreated lazily once _enumerator exists (see EnsureProcessor); guard first call before
        // any frame has arrived by deferring until PresentFrame if no enumerator yet.
        if (_enumerator != null)
        {
            _outputView = _videoDevice.CreateVideoProcessorOutputView(_backBuffer, _enumerator, desc);
        }
    }

    public void Dispose()
    {
        _outputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _backBuffer.Dispose();
        _swapChain.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
