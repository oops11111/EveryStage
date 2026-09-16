using System.Drawing;
using EveryStage.Terminal.Data;
using PdfiumViewer;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// PDF via PDFium (PLANNING.md §3), through the PdfiumViewer NuGet wrapper rather than raw
/// PDFium P/Invoke — view-only paging is all the requirements call for here (unlike Office
/// documents through WPS, PDFs in this product aren't editable), so a maintained managed wrapper
/// is the lower-risk choice. See this project's README for the native-dependency caveat.
/// </summary>
public sealed class PdfContentRenderer : IContentRenderer
{
    private PdfDocument? _document;
    private Size _targetSize = new(1920, 1080);

    public MediaKind SupportedKind => MediaKind.Document;
    public Bitmap? CurrentFrame { get; private set; }
    public int PageCount { get; private set; }
    public int CurrentPageIndex { get; private set; }

    /// <summary>Renders at this pixel size — set to the bound extended display's resolution before
    /// LoadAsync so pages don't need re-rendering on show.</summary>
    public void SetTargetSize(Size size) => _targetSize = size;

    public Task LoadAsync(string path)
    {
        // Bug fixed here: this used to Dispose() the old _document/CurrentFrame without also
        // nulling them out — if PdfDocument.Load(path) below then throws (a real, reachable
        // condition this class's own doc comment/PlaybackEngine's already acknowledge: the file was
        // deleted/moved/corrupted since being added to an activity), _document/CurrentFrame were
        // left pointing at already-disposed objects instead of null. PlaybackEngine reuses one
        // PdfContentRenderer instance for its whole lifetime and does NOT clear _currentFile on a
        // failed load (see OnImageOrDocumentFailed), so PlaybackEngine.CurrentThumbnail (read by
        // FloatingPreviewWindow) and NextPage/PreviousPage (whose _document == null guards were the
        // only thing meant to protect them) would all keep reading/using these stale disposed
        // references until the next successful LoadAsync overwrote them — a disposed Bitmap handed
        // to WinForms to draw throws on every repaint attempt, not just once. Resetting to null/0
        // BEFORE the risky Load() call below means a failure leaves this renderer in the same
        // correctly-recognized-as-empty state its own guards already expect.
        _document?.Dispose();
        _document = null;
        CurrentFrame?.Dispose();
        CurrentFrame = null;
        PageCount = 0;
        CurrentPageIndex = 0;

        _document = PdfDocument.Load(path);
        PageCount = _document.PageCount;
        CurrentPageIndex = 0;
        RenderCurrentPage();

        return Task.CompletedTask;
    }

    public bool NextPage()
    {
        if (_document == null || CurrentPageIndex >= PageCount - 1) return false;
        CurrentPageIndex++;
        RenderCurrentPage();
        return true;
    }

    public bool PreviousPage()
    {
        if (_document == null || CurrentPageIndex <= 0) return false;
        CurrentPageIndex--;
        RenderCurrentPage();
        return true;
    }

    private void RenderCurrentPage()
    {
        if (_document == null) return;
        // Same reasoning as LoadAsync's own fix above, applied here too — this method is also
        // called directly from NextPage/PreviousPage on an already-successfully-loaded _document,
        // so a page-specific render failure (a corrupt page within an otherwise valid PDF) needs
        // the same "null it before the risky call, not just dispose it" treatment, or CurrentFrame
        // would be left pointing at a disposed Bitmap that CurrentThumbnail could still hand out.
        CurrentFrame?.Dispose();
        CurrentFrame = null;

        // NOTE: verify this Render() overload's exact parameter order/types against the installed
        // PdfiumViewer version — this project has not been compiled in this sandbox (no Windows,
        // no native pdfium.dll available here). 96 dpi matches the requested pixel size 1:1.
        var rendered = _document.Render(CurrentPageIndex, _targetSize.Width, _targetSize.Height, 96, 96, forPrinting: false);
        CurrentFrame = new Bitmap(rendered);
        rendered.Dispose();
    }

    public void Dispose()
    {
        CurrentFrame?.Dispose();
        _document?.Dispose();
    }
}
