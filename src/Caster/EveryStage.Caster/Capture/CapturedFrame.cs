using Vortice.Direct3D11;

namespace EveryStage.Caster.Capture;

/// <summary>
/// One captured desktop frame — a private copy owned by the caller until <see cref="Dispose"/> is
/// called. See <see cref="ScreenCaptureSource"/>'s doc comment for why this is a copy rather than a
/// direct reference into the Desktop Duplication API's own buffer.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>True if this acquire reported actual new pixel data (DXGI's
    /// OutputDuplicateFrameInformation.LastPresentTime != 0) rather than only a cursor-position
    /// update — encoding/sending a frame with no LastPresentTime would just re-send an unchanged
    /// image for no reason.</summary>
    public bool HasNewImage { get; }

    internal CapturedFrame(ID3D11Texture2D texture, int width, int height, bool hasNewImage)
    {
        Texture = texture;
        Width = width;
        Height = height;
        HasNewImage = hasNewImage;
    }

    public void Dispose() => Texture.Dispose();
}
