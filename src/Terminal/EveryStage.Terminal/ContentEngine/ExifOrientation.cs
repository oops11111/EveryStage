using System.Drawing;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Applies the EXIF Orientation tag (0x0112). Phones store a portrait photo as landscape pixels plus this
/// tag, and GDI+ never applies it: such photos showed sideways on the big screen and in the library, with
/// no way to fix it in the app (audit B-23).
/// </summary>
internal static class ExifOrientation
{
    private const int OrientationTag = 0x0112;

    /// <summary>The <see cref="RotateFlipType"/> that displays <paramref name="image"/> upright, or
    /// <see cref="RotateFlipType.RotateNoneFlipNone"/> when it has no (or a normal) orientation tag.</summary>
    public static RotateFlipType Of(Image image)
    {
        try
        {
            if (Array.IndexOf(image.PropertyIdList, OrientationTag) < 0) return RotateFlipType.RotateNoneFlipNone;
            var item = image.GetPropertyItem(OrientationTag);
            if (item?.Value is not { Length: >= 1 } value) return RotateFlipType.RotateNoneFlipNone;
            int orientation = value[0] | (value.Length > 1 ? value[1] << 8 : 0); // SHORT, little-endian in GDI+
            return orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.RotateNoneFlipY,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone,
            };
        }
        catch (Exception)
        {
            return RotateFlipType.RotateNoneFlipNone; // unreadable metadata: show the pixels as stored
        }
    }

    /// <summary>Rotates/flips <paramref name="image"/> in place so it displays upright.</summary>
    public static void Apply(Image image)
    {
        var rotation = Of(image);
        if (rotation != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(rotation);
    }
}
