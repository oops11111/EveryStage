namespace EveryStage.Caster.Capture;

/// <summary>The result of <see cref="PointerShapeConverter.ConvertToBgra"/>: tightly packed
/// (no row padding) B8G8R8A8 pixels, ready to upload into a same-format texture.</summary>
public readonly record struct ConvertedPointerShape(byte[] BgraPixels, int Width, int Height);

/// <summary>
/// Converts a Desktop Duplication pointer shape (DXGI_OUTDUPL_POINTER_SHAPE_INFO + its pixel
/// buffer, as returned by <c>IDXGIOutputDuplication.GetFramePointerShape</c>) into plain
/// BGRA pixels with a real alpha channel, so <see cref="ScreenCaptureSource"/> can upload the
/// result as a texture and alpha-blend it over the desktop image (P-28(a) — the pointer is
/// composited into the cast picture on the Caster side; the Terminal needs no changes).
///
/// Pure function over the raw shape bytes — no DirectX anywhere — so the conversion rules are
/// reviewable (and testable) without a GPU. Returns null for an unrecognized shape type or a
/// buffer too small for the declared dimensions, rather than producing a corrupt cursor.
///
/// Approximations, by shape type — read these before trusting the rendered pointer:
/// - Color shapes carry per-pixel BGRA (including alpha) already; rows are just repacked from
///   the shape's pitch to a tight stride.
/// - MaskedColor shapes mark "invert this pixel against what's underneath" via an alpha of
///   0xFF (alpha 0x00 keeps the color). True inversion is not expressible as alpha blending,
///   so this converter approximates: alpha &lt; 128 keeps the color as an opaque pixel, and
///   alpha &gt;= 128 becomes fully transparent (the desktop shows through instead of an
///   inverted pixel). Masked-color cursors therefore lose their inversion effect.
/// - Monochrome shapes are the classic 1-bpp AND/XOR mask pair (the shape's Height covers both
///   masks, so the cursor is Height/2 tall). Exact semantics need the desktop pixel underneath
///   (XOR), which alpha blending also cannot express; the mapping used here is the standard
///   static approximation: AND=0 → opaque, colored by the XOR bit (1 = white, 0 = black);
///   AND=1, XOR=0 → transparent; AND=1, XOR=1 → opaque white (stands in for "inverted").
/// </summary>
public static class PointerShapeConverter
{
    // Native DXGI_OUTDUPL_POINTER_SHAPE_TYPE values, compared against
    // OutduplPointerShapeInfo.Type (a raw uint in Vortice 3.6.2) instead of Vortice's
    // PointerShapeType enum on purpose: the enum as shipped in 3.6.2 numbers Monochrome=1 /
    // Color=2, which is swapped relative to the native COLOR=1 / MONOCHROME=2 the driver
    // actually reports. Flagged for first-check on a real machine together with the rest of
    // this project's unverified Vortice surface — if a real Color cursor ever shows up
    // garbled/missing, this constant mapping is the first suspect.
    public const uint ShapeTypeColor = 1;
    public const uint ShapeTypeMonochrome = 2;
    public const uint ShapeTypeMaskedColor = 4;

    /// <param name="shapeData">Exactly the shape's bytes (the caller slices its scratch buffer
    /// down to the size the driver reported).</param>
    /// <param name="shapeType">One of the native ShapeType* constants above.</param>
    /// <param name="pitch">Bytes per row in <paramref name="shapeData"/> (for monochrome, per
    /// mask row).</param>
    public static ConvertedPointerShape? ConvertToBgra(
        ReadOnlySpan<byte> shapeData, uint shapeType, int width, int height, int pitch)
    {
        if (width <= 0 || height <= 0 || pitch <= 0) return null;

        return shapeType switch
        {
            ShapeTypeColor => ConvertColor(shapeData, width, height, pitch, masked: false),
            ShapeTypeMaskedColor => ConvertColor(shapeData, width, height, pitch, masked: true),
            ShapeTypeMonochrome => ConvertMonochrome(shapeData, width, height, pitch),
            _ => null,
        };
    }

    private static ConvertedPointerShape? ConvertColor(
        ReadOnlySpan<byte> shapeData, int width, int height, int pitch, bool masked)
    {
        int rowBytes = width * 4;
        if (pitch < rowBytes) return null;
        // Last row only needs its own pixels present, not a full pitch of trailing padding.
        if (shapeData.Length < pitch * (height - 1) + rowBytes) return null;

        var pixels = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            var srcRow = shapeData.Slice(y * pitch, rowBytes);
            var dstRow = pixels.AsSpan(y * rowBytes, rowBytes);
            if (!masked)
            {
                srcRow.CopyTo(dstRow);
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    if (srcRow[i + 3] < 128)
                    {
                        dstRow[i] = srcRow[i];
                        dstRow[i + 1] = srcRow[i + 1];
                        dstRow[i + 2] = srcRow[i + 2];
                        dstRow[i + 3] = 255;
                    }
                    // else: leave the zeroed (fully transparent) pixel — see the class doc
                    // comment for why the "invert" pixels degrade to transparent.
                }
            }
        }
        return new ConvertedPointerShape(pixels, width, height);
    }

    private static ConvertedPointerShape? ConvertMonochrome(
        ReadOnlySpan<byte> shapeData, int width, int height, int pitch)
    {
        // Height counts both masks stacked (AND rows first, then XOR rows); an odd height is
        // malformed, and the AND mask alone already needs `height` mask rows of data.
        if (height % 2 != 0) return null;
        if (pitch < (width + 7) / 8) return null;
        if (shapeData.Length < pitch * height) return null;

        int cursorHeight = height / 2;
        var pixels = new byte[width * cursorHeight * 4];
        for (int y = 0; y < cursorHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = 0x80 >> (x % 8);
                bool andBit = (shapeData[y * pitch + x / 8] & bit) != 0;
                bool xorBit = (shapeData[(y + cursorHeight) * pitch + x / 8] & bit) != 0;

                int i = (y * width + x) * 4;
                if (!andBit)
                {
                    // Opaque, colored by the XOR bit: 1 = white, 0 = black.
                    byte c = xorBit ? (byte)255 : (byte)0;
                    pixels[i] = c;
                    pixels[i + 1] = c;
                    pixels[i + 2] = c;
                    pixels[i + 3] = 255;
                }
                else if (xorBit)
                {
                    // "Invert the desktop" — approximated as opaque white (class doc comment).
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = 255;
                }
                // AND=1, XOR=0: transparent — leave the zeroed pixel.
            }
        }
        return new ConvertedPointerShape(pixels, width, cursorHeight);
    }
}
