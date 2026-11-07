using Vortice.Direct3D11;

namespace EveryStage.Caster.Capture;

/// <summary>
/// One captured desktop frame — a private copy owned by the caller until <see cref="Dispose"/> is
/// called. See <see cref="ScreenCaptureSource"/>'s doc comment for why this is a copy rather than a
/// direct reference into the Desktop Duplication API's own buffer.
///
/// The texture is usually one of <see cref="ScreenCaptureSource"/>'s pooled ring textures
/// (P-32): <see cref="Dispose"/> then returns it to the pool instead of disposing it, via the
/// optional release callback. Callers that construct a frame without one (none in production —
/// the constructor is internal and only the source calls it) get the original behavior:
/// <see cref="Dispose"/> disposes the texture outright.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private readonly Action<ID3D11Texture2D>? _releaseTexture;
    private bool _released;

    public ID3D11Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>True if this frame's pixels differ from the previously delivered frame. Two
    /// sources count: the desktop image itself is new (DXGI's
    /// OutputDuplicateFrameInformation.LastPresentTime != 0), or only the mouse pointer baked
    /// into the image changed — moved, shown, hidden, or a new shape (P-28(a): the pointer is
    /// composited into the picture at capture time, so a pointer-only update IS a pixel change
    /// the encoder would otherwise never see; DXGI reports those acquires with
    /// LastPresentTime == 0). Consumers skip frames where this is false, which now means
    /// "literally nothing visible changed — re-sending would duplicate the previous frame".</summary>
    public bool HasNewImage { get; }

    internal CapturedFrame(ID3D11Texture2D texture, int width, int height, bool hasNewImage,
        Action<ID3D11Texture2D>? releaseTexture = null)
    {
        Texture = texture;
        Width = width;
        Height = height;
        HasNewImage = hasNewImage;
        _releaseTexture = releaseTexture;
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        if (_releaseTexture != null) _releaseTexture(Texture);
        else Texture.Dispose();
    }
}
