using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Converts a captured BGRA desktop frame (Desktop Duplication's native output format) into NV12
/// (4:2:0 chroma-subsampled) — the input format hardware H.264 encoders overwhelmingly expect.
/// Uses the GPU video processor (ID3D11VideoProcessor) to do the conversion, the same technique
/// <c>EveryStage.Rendering.SwapChainPresenter</c> already uses for the opposite direction (NV12 ->
/// RGBA on decode) — just run backwards. Same risk category as the rest of this repo's DirectX
/// interop: written against Vortice.Direct3D11's likely video-processor API shape, never compiled.
/// </summary>
public sealed class BgraToNv12Converter : IDisposable
{
    private readonly D3D11Device _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _outputTexture;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _width, _height;

    public BgraToNv12Converter(D3D11Device gpu)
    {
        _gpu = gpu;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.ImmediateContext.QueryInterface<ID3D11VideoContext>();
    }

    /// <summary>
    /// Converts one frame. The returned texture is owned by this converter and reused on every
    /// call — the caller must finish using it (e.g. hand it to the encoder's ProcessInput, which
    /// copies/references it into its own pipeline) before calling <see cref="Convert"/> again.
    /// </summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgraSource, int width, int height)
    {
        EnsureProcessor(width, height);

        var inputViewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0, // 0 = use the source texture's own format (BGRA) as-is.
            ViewDimension = VpivDimension.Texture2D,
            Texture2D = new Texture2DVpiv { MipSlice = 0, ArraySlice = 0 },
        };
        using var inputView = _videoDevice.CreateVideoProcessorInputView(bgraSource, _enumerator!, inputViewDesc);

        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RectI(0, 0, width, height));
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RectI(0, 0, width, height));
        _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, new[] { stream });

        return _outputTexture!;
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_processor != null && _width == width && _height == height) return;

        _outputView?.Dispose();
        _outputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();

        _width = width;
        _height = height;

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // NOTE: NV12 render-target-capable output textures are a somewhat unusual combination
        // (BindFlags.RenderTarget on an NV12 format) — this mirrors how decode's SwapChainPresenter
        // treats its own video-processor output target, but hasn't been verified for the encode
        // direction specifically. If VideoProcessorBlt rejects this as an output, the fallback is a
        // plain Default-usage NV12 texture with no bind flags, relying on the video processor's own
        // output-view creation to validate rather than the texture description.
        var textureDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };
        _outputTexture = _gpu.Device.CreateTexture2D(textureDesc);

        var outputViewDesc = new VideoProcessorOutputViewDescription { ViewDimension = VpovDimension.Texture2D };
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_outputTexture, _enumerator, outputViewDesc);
    }

    public void Dispose()
    {
        _outputView?.Dispose();
        _outputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
