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
    private int _loadVersion;
    private bool _disposed;
    private Task<Bitmap>? _pageRenderTask;
    public bool IsTurningPage => _pageRenderTask != null;
    private Size _targetSize = new(1920, 1080);

    public MediaKind SupportedKind => MediaKind.Document;
    public Bitmap? CurrentFrame { get; private set; }
    public int PageCount { get; private set; }
    public int CurrentPageIndex { get; private set; }

    /// <summary>Renders at this pixel size — set to the bound extended display's resolution before
    /// LoadAsync so pages don't need re-rendering on show.</summary>
    public void SetTargetSize(Size size) => _targetSize = size;

    public async Task LoadAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int version = ++_loadVersion;
        Size target = _targetSize;
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
        ReleaseDocument();
        CurrentFrame?.Dispose();
        CurrentFrame = null;
        PageCount = 0;
        CurrentPageIndex = 0;

        var loaded = await Task.Run(() =>
        {
            var document = PdfDocument.Load(path);
            try
            {
                Size fitted = FitPage(document.PageSizes[0], target);
                using var rendered = document.Render(0, fitted.Width, fitted.Height, 96, 96, forPrinting: false);
                return (Document: document, Frame: new Bitmap(rendered));
            }
            catch { document.Dispose(); throw; }
        });
        if (_disposed || version != _loadVersion)
        {
            loaded.Frame.Dispose();
            loaded.Document.Dispose();
            return;
        }
        _document = loaded.Document;
        CurrentFrame = loaded.Frame;
        PageCount = _document.PageCount;
    }

    public bool NextPage()
    {
        if (IsTurningPage) return false;
        if (_document == null || CurrentPageIndex >= PageCount - 1) return false;
        CurrentPageIndex++;
        RenderCurrentPage();
        return true;
    }

    public bool PreviousPage()
    {
        if (IsTurningPage) return false;
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
        Size fitted = FitPage(_document.PageSizes[CurrentPageIndex], _targetSize);
        using var rendered = _document.Render(CurrentPageIndex, fitted.Width, fitted.Height, 96, 96, forPrinting: false);
        CurrentFrame = new Bitmap(rendered);
    }

    /// <summary>Called on the owning UI context; native rendering runs off-thread. The previous
    /// frame remains valid until its replacement is ready. Concurrent clicks are coalesced.</summary>
    public async Task<bool> TurnPageAsync(int direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (_document == null || IsTurningPage) return false;
        int page = CurrentPageIndex + direction;
        if (page < 0 || page >= PageCount) return false;
        var document = _document;
        int version = _loadVersion;
        Size target = _targetSize;
        var task = Task.Run(() =>
        {
            Size fitted = FitPage(document.PageSizes[page], target);
            using var rendered = document.Render(page, fitted.Width, fitted.Height, 96, 96, forPrinting: false);
            return new Bitmap(rendered);
        });
        _pageRenderTask = task;
        try
        {
            Bitmap frame = await task;
            if (_disposed || version != _loadVersion)
            {
                frame.Dispose();
                return false;
            }
            var previous = CurrentFrame;
            CurrentFrame = frame;
            CurrentPageIndex = page;
            previous?.Dispose();
            return true;
        }
        finally
        {
            if (ReferenceEquals(_pageRenderTask, task)) _pageRenderTask = null;
        }
    }

    private void ReleaseDocument()
    {
        var document = _document;
        var pending = _pageRenderTask;
        _document = null;
        _pageRenderTask = null;
        if (document == null) return;
        // Do not block the UI or free PDFium's document while its worker is still using it.
        if (pending is { IsCompleted: false })
            _ = pending.ContinueWith(_ => document.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else document.Dispose();
    }

    public static Bitmap CreateThumbnail(string path, Size target)
    {
        using var document = PdfDocument.Load(path);
        Size fitted = FitPage(document.PageSizes[0], target);
        using var rendered = document.Render(0, fitted.Width, fitted.Height, 96, 96, forPrinting: false);
        return new Bitmap(rendered);
    }

    private static Size FitPage(SizeF page, Size target)
    {
        if (page.Width <= 0 || page.Height <= 0 || !float.IsFinite(page.Width) || !float.IsFinite(page.Height))
            throw new InvalidDataException("PDF 页面尺寸无效。");
        double scale = Math.Min(Math.Max(1, target.Width) / (double)page.Width,
            Math.Max(1, target.Height) / (double)page.Height);
        return new Size(Math.Max(1, (int)Math.Round(page.Width * scale)),
            Math.Max(1, (int)Math.Round(page.Height * scale)));
    }

    public void Dispose()
    {
        _disposed = true;
        ++_loadVersion;
        CurrentFrame?.Dispose();
        ReleaseDocument();
        CurrentFrame = null;
        _document = null;
    }
}
