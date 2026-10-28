using System.Drawing;

namespace EveryStage.Terminal.Documents;

/// <summary>PDF reading mode: one long vertical strip of pages, or one page at a time.</summary>
public enum PdfDisplayMode { Continuous, SinglePage }

/// <summary>PDF zoom: fit the whole page, fit the width, 100% (96 dpi), or a custom percentage.</summary>
public enum PdfZoomMode { FitWidth, FitPage, ActualSize, Custom }

/// <summary>
/// Where a PDF view is scrolled to, independent of the window size: the page at the top of the view and
/// how far into that page (0..1, measured over the page plus the gap above it), plus zoom and mode. The
/// Preview and Program views each keep one; syncing Program means handing it Preview's state, and a
/// resized view restores the same content position instead of the same pixel offset.
/// </summary>
public readonly record struct PdfViewportState(
    int PageIndex,
    double PageOffset,
    PdfDisplayMode DisplayMode = PdfDisplayMode.Continuous,
    PdfZoomMode ZoomMode = PdfZoomMode.FitWidth,
    double CustomZoom = 1.0)
{
    public static PdfViewportState Start => new(0, 0);

    public PdfViewportState Normalize(int pageCount)
    {
        int page = pageCount <= 0 ? 0 : Math.Clamp(PageIndex, 0, pageCount - 1);
        double offset = double.IsFinite(PageOffset) ? Math.Clamp(PageOffset, 0, 0.999999) : 0;
        double zoom = double.IsFinite(CustomZoom) ? Math.Clamp(CustomZoom, PdfLayout.MinZoom, PdfLayout.MaxZoom) : 1;
        return this with { PageIndex = page, PageOffset = offset, CustomZoom = zoom };
    }
}

/// <summary>
/// Pure layout of a PDF in a view: page scale, page rectangles in scroll space, total height, and the
/// mapping between a <see cref="PdfViewportState"/> and a pixel scroll offset. Gaps and margins are
/// defined in PDF points and scale with the zoom, so Preview and Program (different sizes) lay out the
/// same proportions and show the same content for the same state.
/// </summary>
public sealed class PdfLayout
{
    public const double GapPoints = 12;
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;
    private const double PixelsPerPoint = 96.0 / 72.0;

    private readonly SizeF[] _pages;
    private readonly double[] _tops;     // page top in scroll space (px)
    private readonly double[] _regions;  // region i starts at _regions[i]: the gap above page i
    private readonly int _first, _last;  // pages laid out (all in continuous mode, one in single-page mode)

    public PdfLayout(IReadOnlyList<SizeF> pageSizes, Size viewport, PdfViewportState state)
    {
        if (pageSizes.Count == 0) throw new ArgumentException("PDF 没有页面。", nameof(pageSizes));
        _pages = pageSizes.ToArray();
        Viewport = new Size(Math.Max(1, viewport.Width), Math.Max(1, viewport.Height));
        State = state.Normalize(_pages.Length);
        bool single = State.DisplayMode == PdfDisplayMode.SinglePage;
        _first = single ? State.PageIndex : 0;
        _last = single ? State.PageIndex : _pages.Length - 1;

        float maxWidth = 0, maxHeight = 0;
        for (int i = _first; i <= _last; i++)
        {
            maxWidth = Math.Max(maxWidth, _pages[i].Width);
            maxHeight = Math.Max(maxHeight, _pages[i].Height);
        }
        double fitWidth = Viewport.Width / (maxWidth + 2 * GapPoints);
        double fitPage = Math.Min(fitWidth, Viewport.Height / (maxHeight + 2 * GapPoints));
        Scale = State.ZoomMode switch
        {
            PdfZoomMode.FitPage => fitPage,
            PdfZoomMode.ActualSize => PixelsPerPoint,
            PdfZoomMode.Custom => State.CustomZoom * PixelsPerPoint,
            _ => fitWidth,
        };

        double gap = GapPoints * Scale;
        _tops = new double[_pages.Length];
        _regions = new double[_pages.Length + 1];
        double y = gap;
        for (int i = _first; i <= _last; i++)
        {
            _regions[i] = y - gap;
            _tops[i] = y;
            y += _pages[i].Height * Scale + gap;
        }
        TotalHeight = y;
        _regions[_last + 1] = TotalHeight;
        MaxScroll = Math.Max(0, TotalHeight - Viewport.Height);
        ScrollOffset = Math.Clamp(_regions[State.PageIndex] + State.PageOffset * RegionHeight(State.PageIndex), 0, MaxScroll);
    }

    public Size Viewport { get; }
    public PdfViewportState State { get; }
    /// <summary>Pixels per PDF point.</summary>
    public double Scale { get; }
    public double TotalHeight { get; }
    public double MaxScroll { get; }
    /// <summary>Pixel offset of the view's top edge in scroll space, for <see cref="State"/>.</summary>
    public double ScrollOffset { get; }
    public int PageCount => _pages.Length;
    public int FirstLaidOutPage => _first;
    public int LastLaidOutPage => _last;

    private double RegionHeight(int page) => Math.Max(1, _regions[page + 1] - _regions[page]);

    /// <summary>Page rectangle in view coordinates for a view scrolled to <paramref name="scroll"/>.
    /// Pages are centred horizontally (wider pages are cropped equally on both sides).</summary>
    public RectangleF PageRect(int page, double scroll)
    {
        double width = _pages[page].Width * Scale, height = _pages[page].Height * Scale;
        // When everything fits, centre the document vertically instead of pinning it to the top.
        double centring = TotalHeight < Viewport.Height ? (Viewport.Height - TotalHeight) / 2 : 0;
        return new RectangleF((float)((Viewport.Width - width) / 2), (float)(_tops[page] - scroll + centring), (float)width, (float)height);
    }

    /// <summary>Pixel size to render <paramref name="page"/> at for this zoom.</summary>
    public Size RenderSize(int page) => new(
        Math.Max(1, (int)Math.Round(_pages[page].Width * Scale)),
        Math.Max(1, (int)Math.Round(_pages[page].Height * Scale)));

    /// <summary>State for the view's top edge at <paramref name="scroll"/> (keeps mode and zoom).</summary>
    public PdfViewportState StateAt(double scroll)
    {
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        int page = _first;
        while (page < _last && _regions[page + 1] <= scroll) page++;
        double offset = (scroll - _regions[page]) / RegionHeight(page);
        return State with { PageIndex = page, PageOffset = Math.Clamp(offset, 0, 0.999999) };
    }

    /// <summary>Pages whose rectangles intersect the view at <paramref name="scroll"/>, extended by
    /// <paramref name="preload"/> pages on each side.</summary>
    public (int First, int Last) VisiblePages(double scroll, int preload = 0)
    {
        int first = -1, last = -1;
        for (int i = _first; i <= _last; i++)
        {
            var rect = PageRect(i, scroll);
            if (rect.Bottom < 0 || rect.Top > Viewport.Height) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) first = last = State.PageIndex;
        return (Math.Max(_first, first - preload), Math.Min(_last, last + preload));
    }

    /// <summary>The page most of the view shows (for the "页码" readout).</summary>
    public int DominantPage(double scroll)
    {
        int best = State.PageIndex;
        double bestVisible = -1;
        var (first, last) = VisiblePages(scroll);
        for (int i = first; i <= last; i++)
        {
            var rect = PageRect(i, scroll);
            double visible = Math.Min(rect.Bottom, Viewport.Height) - Math.Max(rect.Top, 0);
            if (visible > bestVisible) { bestVisible = visible; best = i; }
        }
        return best;
    }
}
