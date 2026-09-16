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
    public MediaKind SupportedKind => MediaKind.Image;
    public Bitmap? CurrentFrame { get; private set; }
    public int PageCount => 1;
    public int CurrentPageIndex => 0;

    public Task LoadAsync(string path)
    {
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
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var loaded = Image.FromStream(fileStream);
        CurrentFrame = new Bitmap(loaded);

        return Task.CompletedTask;
    }

    public bool NextPage() => false;
    public bool PreviousPage() => false;

    public void Dispose() => CurrentFrame?.Dispose();
}
