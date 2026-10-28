using System.Drawing;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// PLANNING.md §3 lists the image codec as "WIC" (Windows Imaging Component). This uses
/// <see cref="System.Drawing.Image"/> (GDI+) rather than calling WIC's COM interfaces
/// (IWICImagingFactory etc.) directly — on Windows, GDI+'s built-in codecs are themselves backed
/// by WIC, so this satisfies the same requirement with a much lower-risk, fully managed API.
/// Revisit with direct WIC interop only if a concrete need shows up that GDI+ can't cover (e.g.
/// >8-bit-per-channel/HDR image formats, which GDI+ doesn't handle but raw WIC does).
/// </summary>
public sealed class ImageContentRenderer : IContentRenderer
{
    private int _loadVersion;
    private bool _disposed;
    public MediaKind SupportedKind => MediaKind.Image;
    public Bitmap? CurrentFrame { get; private set; }

    /// <summary>Frames (animated GIF) or pages (multi-page TIFF) in the loaded file. Only the first is
    /// shown; <see cref="FirstFrameOnlyNote"/> says so for the UI.</summary>
    public int SourceFrameCount { get; private set; } = 1;

    /// <summary>"仅显示首帧" / "仅显示首页" when the file has more than one frame/page, else null.</summary>
    public string? FirstFrameOnlyNote { get; private set; }
    public int PageCount => 1;
    public int CurrentPageIndex => 0;

    public async Task LoadAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int version = ++_loadVersion;
        // Bug fixed here: same shape as PdfContentRenderer.LoadAsync's own fix (see that class's
        // doc comment for the full reasoning) — this used to Dispose() the old CurrentFrame without
        // nulling it out, so a failure below (the file was deleted/moved/corrupted since being
        // added to an activity — a real condition PlaybackEngine's own doc comments already
        // acknowledge) left CurrentFrame pointing at an already-disposed Bitmap instead of null.
        // PlaybackEngine reuses one ImageContentRenderer instance for its whole lifetime and does
        // NOT clear _currentFile on a failed load, so PlaybackEngine.CurrentThumbnail (read by
        // FloatingPreviewWindow) would keep handing out this disposed Bitmap for WinForms to draw
        // until the next successful LoadAsync overwrote it.
        CurrentFrame?.Dispose();
        CurrentFrame = null;

        // Load fully into memory and detach from the file handle: Image.FromFile keeps the file
        // locked open for the image's lifetime otherwise, which would block the file being
        // replaced/deleted from the 文件 panel while it's the currently displayed item.
        SourceFrameCount = 1;
        FirstFrameOnlyNote = null;
        var (frame, count, note) = await Task.Run(() =>
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var loaded = Image.FromStream(fileStream);
            // GDI+ exposes GIF animation frames under the Time dimension and TIFF pages under Page;
            // new Bitmap(loaded) copies only the active (first) one.
            int frames = 1;
            string? label = null;
            foreach (var dimension in loaded.FrameDimensionsList)
            {
                int n = loaded.GetFrameCount(new System.Drawing.Imaging.FrameDimension(dimension));
                if (n > frames)
                {
                    frames = n;
                    label = dimension == System.Drawing.Imaging.FrameDimension.Page.Guid ? "仅显示首页" : "仅显示首帧";
                }
            }
            // Phone photos: landscape pixels + an EXIF Orientation tag GDI+ ignores (audit B-23).
            var copy = CopyAtMostDisplaySize(loaded);
            try { copy.RotateFlip(ExifOrientation.Of(loaded)); }
            catch { copy.Dispose(); throw; }
            return (copy, frames, label);
        });
        if (_disposed || version != _loadVersion)
        {
            frame.Dispose();
            return;
        }
        CurrentFrame = frame;
        SourceFrameCount = count;
        FirstFrameOnlyNote = note;
    }

    public bool NextPage() => false;
    public bool PreviousPage() => false;

    // Longest side kept: enough for a 4K output in either orientation.
    private const int MaxDisplayEdge = 3840;

    /// <summary>The first frame as a standalone bitmap, scaled down (once, off the UI thread) when larger
    /// than any output needs. A 48 MP phone photo used to be kept at full size — 183 MB per channel, Preview
    /// and Program each holding one — and rescaled from that on every repaint (audit C-29).</summary>
    private static Bitmap CopyAtMostDisplaySize(Image loaded)
    {
        int longest = Math.Max(loaded.Width, loaded.Height);
        if (longest <= MaxDisplayEdge) return new Bitmap(loaded);
        double scale = MaxDisplayEdge / (double)longest;
        var scaled = new Bitmap(Math.Max(1, (int)Math.Round(loaded.Width * scale)), Math.Max(1, (int)Math.Round(loaded.Height * scale)));
        try
        {
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // no dark seam at the edges
            g.DrawImage(loaded, new Rectangle(0, 0, scaled.Width, scaled.Height), 0, 0, loaded.Width, loaded.Height, GraphicsUnit.Pixel, attributes);
            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ++_loadVersion;
        CurrentFrame?.Dispose();
        CurrentFrame = null;
    }
}
