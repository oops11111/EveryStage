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
        _document?.Dispose();
        CurrentFrame?.Dispose();

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
        CurrentFrame?.Dispose();

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
