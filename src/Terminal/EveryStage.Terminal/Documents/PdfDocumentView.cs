using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using EveryStage.Terminal.ContentEngine;
using PdfiumViewer;

namespace EveryStage.Terminal.Documents;

/// <summary>Cache key for one rendered page: page index and the exact pixel size it was rendered at
/// (the size already encodes the zoom and the view's resolution).</summary>
public readonly record struct PdfPageKey(int Page, int Width, int Height);

/// <summary>
/// Owns one opened PDF and renders pages on a single background thread, newest request first. Requests
/// replace the pending queue (a scroll makes older requests irrelevant); results are delivered through
/// <see cref="Rendered"/> / <see cref="Failed"/> on the worker thread.
/// </summary>
internal sealed class PdfRenderService : IDisposable
{
    private readonly PdfDocument _document;
    private readonly object _gate = new();
    private readonly List<(PdfPageKey Key, bool Fast)> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    private PdfRenderService(PdfDocument document)
    {
        _document = document;
        PageSizes = document.PageSizes.ToArray();
        _worker = new Thread(Run) { IsBackground = true, Name = "EveryStage PDF render" };
        _worker.Start();
    }

    public IReadOnlyList<SizeF> PageSizes { get; }
    public event Action<PdfPageKey, Bitmap>? Rendered;
    public event Action<PdfPageKey, Exception>? Failed;

    public static async Task<PdfRenderService> OpenAsync(string path)
    {
        var document = await Task.Run(() =>
        {
            // Load from a memory copy: the file is never held open (it can be replaced or edited while it
            // plays), and a corrupt file does not leave a handle behind (PdfiumViewer's path overload does).
            var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            try { return PdfDocument.Load(stream); }
            catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex is PdfException { Error: PdfError.PasswordProtected })
            {
                stream.Dispose();
                throw new InvalidDataException("PDF 受密码保护，无法在 EveryStage 中打开。", ex);
            }
            catch (Exception) { stream.Dispose(); throw; }
        });
        if (document.PageCount <= 0)
        {
            document.Dispose();
            throw new InvalidDataException("PDF 没有页面。");
        }
        foreach (var size in document.PageSizes)
            if (!(size.Width > 0 && size.Height > 0 && float.IsFinite(size.Width) && float.IsFinite(size.Height)))
            {
                document.Dispose();
                throw new InvalidDataException("PDF 页面尺寸无效。");
            }
        return new PdfRenderService(document);
    }

    /// <summary>Replaces the pending work with <paramref name="requests"/> (highest priority first).
    /// <paramref name="requests"/> with Fast set render without anti-aliased text, for use while scrolling.</summary>
    public void Schedule(IEnumerable<(PdfPageKey Key, bool Fast)> requests)
    {
        lock (_gate)
        {
            _queue.Clear();
            _queue.AddRange(requests);
            Monitor.Pulse(_gate);
        }
    }

    private void Run()
    {
        while (true)
        {
            (PdfPageKey Key, bool Fast) job;
            lock (_gate)
            {
                while (!_disposed && _queue.Count == 0) Monitor.Wait(_gate);
                if (_disposed) break;
                job = _queue[0];
                _queue.RemoveAt(0);
            }
            try
            {
                var flags = job.Fast ? PdfRenderFlags.Annotations : PdfRenderFlags.Annotations | PdfRenderFlags.LcdText;
                using var image = _document.Render(job.Key.Page, job.Key.Width, job.Key.Height, 96, 96, flags);
                var bitmap = new Bitmap(image);
                bool deliver;
                lock (_gate) deliver = !_disposed;
                if (deliver) Rendered?.Invoke(job.Key, bitmap); else bitmap.Dispose();
            }
            catch (Exception ex)
            {
                bool deliver;
                lock (_gate) deliver = !_disposed;
                if (deliver) Failed?.Invoke(job.Key, ex);
            }
        }
        _document.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
        // The worker disposes the document once the page it may be rendering has finished.
    }
}

/// <summary>Least-recently-used page bitmaps under a byte budget. UI thread only.</summary>
internal sealed class PdfPageCache : IDisposable
{
    private readonly long _budgetBytes;
    private readonly LinkedList<(PdfPageKey Key, Bitmap Bitmap)> _order = new();
    private readonly Dictionary<PdfPageKey, LinkedListNode<(PdfPageKey Key, Bitmap Bitmap)>> _map = new();
    private long _bytes;

    public PdfPageCache(long budgetBytes) => _budgetBytes = budgetBytes;

    public int Count => _map.Count;
    public long Bytes => _bytes;

    public Bitmap? Get(PdfPageKey key)
    {
        if (!_map.TryGetValue(key, out var node)) return null;
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Bitmap;
    }

    public bool Contains(PdfPageKey key) => _map.ContainsKey(key);

    /// <summary>The largest cached rendering of <paramref name="page"/> at any size (a stand-in while the
    /// exact size is still rendering), or null.</summary>
    public Bitmap? GetAnySize(int page)
    {
        Bitmap? best = null;
        foreach (var (key, node) in _map)
            if (key.Page == page && (best == null || key.Width > best.Width)) best = node.Value.Bitmap;
        return best;
    }

    public void Add(PdfPageKey key, Bitmap bitmap, ISet<int> keepPages)
    {
        if (_map.Remove(key, out var existing))
        {
            _order.Remove(existing);
            _bytes -= Size(existing.Value.Bitmap);
            existing.Value.Bitmap.Dispose();
        }
        _map[key] = _order.AddFirst((key, bitmap));
        _bytes += Size(bitmap);
        // Evict least recently used, but never a page that is on screen right now.
        var node = _order.Last;
        while (_bytes > _budgetBytes && node != null)
        {
            var previous = node.Previous;
            if (!keepPages.Contains(node.Value.Key.Page) && !node.Value.Key.Equals(key))
            {
                _order.Remove(node);
                _map.Remove(node.Value.Key);
                _bytes -= Size(node.Value.Bitmap);
                node.Value.Bitmap.Dispose();
            }
            node = previous;
        }
    }

    private static long Size(Bitmap bitmap) => (long)bitmap.Width * bitmap.Height * 4;

    public void Dispose()
    {
        foreach (var (_, bitmap) in _order) bitmap.Dispose();
        _order.Clear();
        _map.Clear();
        _bytes = 0;
    }
}

/// <summary>
/// A PDF drawn into a <see cref="ContentSurface"/>: continuous vertical scrolling or single-page mode,
/// fit-page / fit-width / 100% / custom zoom. Only the visible pages plus a few preloaded neighbours are
/// rendered (never the whole document into one bitmap); renders are cached by page and pixel size. While
/// the operator scrolls quickly, missing pages are rendered at half resolution and replaced with sharp
/// renders once scrolling pauses. The surface is double-buffered, and a page whose sharp render is not
/// ready is drawn from any cached size (or as a blank page) — never a black flash.
///
/// The Preview view shows chrome (scrollbar, page indicator) and takes wheel, scrollbar-drag and keyboard
/// input; the Program view shows none and is driven only through <see cref="ApplyState"/>.
/// </summary>
public sealed class PdfDocumentView : IContentPainter, IDisposable
{
    public static readonly Color PageBackground = Color.FromArgb(34, 36, 40);
    private const int ScrollbarWidth = 10;
    private const int PreloadPages = 2;
    private const long MaxRenderPixels = 24_000_000; // ~96 MB per page bitmap at most

    private readonly PdfRenderService _service;
    private readonly PdfPageCache _cache;
    private readonly HashSet<int> _failedPages = new();
    private readonly System.Windows.Forms.Timer _settleTimer = new() { Interval = 180 };
    private Control? _host;
    private PdfViewportState _state;
    private PdfLayout? _layout;
    private bool _scrollingFast;
    private bool _draggingThumb;
    private double _dragStartScroll;
    private int _dragStartY;
    private bool _disposed;

    private PdfDocumentView(PdfRenderService service, bool interactive, long cacheBudget)
    {
        _service = service;
        Interactive = interactive;
        _cache = new PdfPageCache(cacheBudget);
        _service.Rendered += OnRendered;
        _service.Failed += OnFailed;
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            _scrollingFast = false;
            RequestRenders();
        };
    }

    /// <summary>Opens <paramref name="path"/> (off the UI thread). <paramref name="interactive"/> is true
    /// for the Preview view (chrome and input), false for Program output.</summary>
    public static async Task<PdfDocumentView> OpenAsync(string path, bool interactive, PdfViewportState initial)
    {
        var service = await PdfRenderService.OpenAsync(path);
        var view = new PdfDocumentView(service, interactive, interactive ? 96L << 20 : 256L << 20);
        view._state = initial.Normalize(service.PageSizes.Count);
        return view;
    }

    public bool Interactive { get; }
    public int PageCount => _service.PageSizes.Count;
    public PdfViewportState State => _state;
    public int CachedPages => _cache.Count;
    public long CachedBytes => _cache.Bytes;
    public IReadOnlyCollection<int> FailedPages => _failedPages;

    /// <summary>1-based page the view mostly shows.</summary>
    public int CurrentPage => (Layout()?.DominantPage(Layout()!.ScrollOffset) ?? _state.PageIndex) + 1;

    /// <summary>Raised on the UI thread whenever the viewport state changes (scroll, page, zoom, mode).
    /// The bool is true when the operator caused it (wheel, keys, scrollbar) rather than code.</summary>
    public event Action<PdfViewportState, bool>? StateChanged;

    /// <summary>Single-page mode only, raised just before the shown page changes (the old page is still
    /// on screen), with the 0-based page indices: the page transition freezes the picture here.</summary>
    public event Action<int, int>? PageTurning;

    /// <summary>Raised once the pages visible at the current position have sharp renders.</summary>
    public event Action? VisiblePagesReady;

    private Size ViewSize => _host == null ? new Size(1280, 720) : new Size(Math.Max(1, _host.ClientSize.Width - (Interactive ? ScrollbarWidth : 0)), Math.Max(1, _host.ClientSize.Height));

    private PdfLayout? Layout()
    {
        if (_disposed) return null;
        var size = ViewSize;
        if (_layout == null || _layout.Viewport != size || _layout.State != _state)
            _layout = new PdfLayout(_service.PageSizes, size, _state);
        return _layout;
    }

    public void Attach(Control host)
    {
        _host = host;
        _layout = null;
        RequestRenders();
    }

    public void Detach() => _host = null;

    public void OnResize()
    {
        _layout = null; // same state, new pixel layout: the content position is preserved
        RequestRenders();
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Moves to <paramref name="state"/> (Program sync, Take, activity start position).</summary>
    public void ApplyState(PdfViewportState state) => SetState(state.Normalize(PageCount), user: false);

    public void ScrollBy(double pixels, bool user = true)
    {
        var layout = Layout();
        if (layout == null) return;
        if (_state.DisplayMode == PdfDisplayMode.SinglePage)
        {
            // Single-page mode scrolls within the page and turns the page at its ends.
            if (pixels > 0 && layout.ScrollOffset >= layout.MaxScroll - 0.5 && _state.PageIndex < PageCount - 1)
            {
                SetState(_state with { PageIndex = _state.PageIndex + 1, PageOffset = 0 }, user);
                return;
            }
            if (pixels < 0 && layout.ScrollOffset <= 0.5 && _state.PageIndex > 0)
            {
                SetState(_state with { PageIndex = _state.PageIndex - 1, PageOffset = 0.999 }, user);
                return;
            }
        }
        MarkScrolling();
        SetState(layout.StateAt(layout.ScrollOffset + pixels), user);
    }

    public bool NextPage(bool user = true)
    {
        if (_state.PageIndex >= PageCount - 1) return false;
        SetState(_state with { PageIndex = _state.PageIndex + 1, PageOffset = 0 }, user);
        return true;
    }

    public bool PreviousPage(bool user = true)
    {
        // In continuous mode, "previous" from the middle of a page first returns to that page's top.
        if (_state.DisplayMode == PdfDisplayMode.Continuous && _state.PageOffset > 0.02)
        {
            SetState(_state with { PageOffset = 0 }, user);
            return true;
        }
        if (_state.PageIndex <= 0) return false;
        SetState(_state with { PageIndex = _state.PageIndex - 1, PageOffset = 0 }, user);
        return true;
    }

    /// <summary>Jumps to the top of 1-based <paramref name="page"/>.</summary>
    public void GoToPage(int page, bool user = true) =>
        SetState(_state with { PageIndex = Math.Clamp(page - 1, 0, PageCount - 1), PageOffset = 0 }, user);

    public void SetZoom(PdfZoomMode mode, double customZoom = 1.0, bool user = true) =>
        SetState(_state with { ZoomMode = mode, CustomZoom = customZoom }, user);

    public void SetDisplayMode(PdfDisplayMode mode, bool user = true) =>
        SetState(_state with { DisplayMode = mode, PageIndex = Math.Max(0, CurrentPage - 1), PageOffset = 0 }, user);

    /// <summary>True when the bottom of the last page (or of the single page) is in view.</summary>
    public bool AtEnd => Layout() is { } l && _state.PageIndex >= PageCount - 1 && l.ScrollOffset >= l.MaxScroll - 0.5;

    private void SetState(PdfViewportState state, bool user)
    {
        state = state.Normalize(PageCount);
        if (state == _state) return;
        if (state.PageIndex != _state.PageIndex && state.DisplayMode == PdfDisplayMode.SinglePage && _state.DisplayMode == PdfDisplayMode.SinglePage)
            PageTurning?.Invoke(_state.PageIndex, state.PageIndex);
        _state = state;
        _layout = null;
        RequestRenders();
        _host?.Invalidate();
        StateChanged?.Invoke(_state, user);
    }

    private void MarkScrolling()
    {
        _scrollingFast = true;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────

    private PdfPageKey KeyFor(PdfLayout layout, int page, bool fast)
    {
        var size = layout.RenderSize(page);
        double pixels = (double)size.Width * size.Height;
        double factor = pixels > MaxRenderPixels ? Math.Sqrt(MaxRenderPixels / pixels) : 1;
        if (fast) factor *= 0.5;
        return new PdfPageKey(page, Math.Max(1, (int)(size.Width * factor)), Math.Max(1, (int)(size.Height * factor)));
    }

    private void RequestRenders()
    {
        var layout = Layout();
        if (layout == null || _host == null) return;
        var (first, last) = layout.VisiblePages(layout.ScrollOffset);
        var (preFirst, preLast) = layout.VisiblePages(layout.ScrollOffset, PreloadPages);
        int anchor = layout.DominantPage(layout.ScrollOffset);
        var requests = new List<(PdfPageKey, bool)>();
        // Visible pages first (nearest the middle first), then the preloaded neighbours.
        foreach (int page in Enumerable.Range(first, last - first + 1).OrderBy(p => Math.Abs(p - anchor)))
        {
            if (_failedPages.Contains(page)) continue;
            var sharp = KeyFor(layout, page, fast: false);
            if (_cache.Contains(sharp)) continue;
            if (_scrollingFast)
            {
                var quick = KeyFor(layout, page, fast: true);
                if (_cache.GetAnySize(page) == null && !_cache.Contains(quick)) requests.Add((quick, true));
            }
            else requests.Add((sharp, false));
        }
        if (!_scrollingFast)
            foreach (int page in Enumerable.Range(preFirst, preLast - preFirst + 1).Where(p => p < first || p > last).OrderBy(p => Math.Abs(p - anchor)))
            {
                var sharp = KeyFor(layout, page, fast: false);
                if (!_failedPages.Contains(page) && !_cache.Contains(sharp)) requests.Add((sharp, false));
            }
        _service.Schedule(requests);
        if (requests.Count == 0 && !_scrollingFast) VisiblePagesReady?.Invoke();
    }

    private void OnRendered(PdfPageKey key, Bitmap bitmap)
    {
        var host = _host;
        if (host == null || host.IsDisposed || !host.IsHandleCreated) { bitmap.Dispose(); return; }
        try
        {
            host.BeginInvoke(() =>
            {
                if (_disposed) { bitmap.Dispose(); return; }
                var layout = Layout();
                var keep = new HashSet<int>();
                if (layout != null)
                {
                    var (first, last) = layout.VisiblePages(layout.ScrollOffset, PreloadPages);
                    for (int i = first; i <= last; i++) keep.Add(i);
                }
                _cache.Add(key, bitmap, keep);
                _host?.Invalidate();
                if (layout != null && !_scrollingFast)
                {
                    var (first, last) = layout.VisiblePages(layout.ScrollOffset);
                    bool ready = true;
                    for (int i = first; i <= last && ready; i++)
                        ready = _failedPages.Contains(i) || _cache.Contains(KeyFor(layout, i, fast: false));
                    if (ready) VisiblePagesReady?.Invoke();
                }
            });
        }
        catch (InvalidOperationException) { bitmap.Dispose(); } // host handle gone
    }

    private void OnFailed(PdfPageKey key, Exception ex)
    {
        var host = _host;
        if (host == null || host.IsDisposed || !host.IsHandleCreated) return;
        try
        {
            host.BeginInvoke(() =>
            {
                if (_disposed) return;
                _failedPages.Add(key.Page);
                PageRenderFailed?.Invoke(key.Page, ex);
                _host?.Invalidate();
            });
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>A single page failed to render (0-based page); it is drawn as an error box.</summary>
    public event Action<int, Exception>? PageRenderFailed;

    public void Paint(Graphics g, Rectangle bounds)
    {
        using (var background = new SolidBrush(PageBackground)) g.FillRectangle(background, bounds);
        var layout = Layout();
        if (layout == null) return;
        double scroll = layout.ScrollOffset;
        var (first, last) = layout.VisiblePages(scroll);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int page = first; page <= last; page++)
        {
            var rect = layout.PageRect(page, scroll);
            var target = Rectangle.Round(rect);
            if (_failedPages.Contains(page))
            {
                DrawFailedPage(g, target, page);
                continue;
            }
            var sharpKey = KeyFor(layout, page, fast: false);
            var bitmap = _cache.Get(sharpKey) ?? _cache.GetAnySize(page);
            if (bitmap == null)
            {
                g.FillRectangle(Brushes.White, target); // blank page while it renders — no black flash
                continue;
            }
            g.InterpolationMode = bitmap.Width == target.Width && bitmap.Height == target.Height
                ? InterpolationMode.NearestNeighbor
                : (_scrollingFast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear);
            g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
        }
        if (Interactive) DrawChrome(g, layout);
    }

    private static void DrawFailedPage(Graphics g, Rectangle target, int page)
    {
        using var fill = new SolidBrush(Color.FromArgb(255, 238, 238));
        g.FillRectangle(fill, target);
        using var font = new Font("Microsoft YaHei UI", Math.Max(9f, Math.Min(18f, target.Width / 30f)));
        TextRenderer.DrawText(g, $"第 {page + 1} 页渲染失败", font, target, Color.FromArgb(180, 40, 40),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private Rectangle TrackRect => _host == null ? Rectangle.Empty : new Rectangle(_host.ClientSize.Width - ScrollbarWidth, 0, ScrollbarWidth, _host.ClientSize.Height);

    private Rectangle ThumbRect(PdfLayout layout)
    {
        var track = TrackRect;
        if (layout.MaxScroll <= 0) return new Rectangle(track.X + 2, track.Y, track.Width - 4, track.Height);
        int height = Math.Max(24, (int)(track.Height * (layout.Viewport.Height / layout.TotalHeight)));
        int top = (int)((track.Height - height) * (layout.ScrollOffset / layout.MaxScroll));
        return new Rectangle(track.X + 2, track.Y + top, track.Width - 4, height);
    }

    private void DrawChrome(Graphics g, PdfLayout layout)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = new SolidBrush(Color.FromArgb(20, 22, 26))) g.FillRectangle(track, TrackRect);
        using (var thumb = new SolidBrush(_draggingThumb ? Color.FromArgb(170, 190, 215) : Color.FromArgb(110, 125, 145)))
            g.FillRectangle(thumb, ThumbRect(layout));
        string label = $"{CurrentPage} / {PageCount}";
        using var font = new Font("Segoe UI", 8.5f);
        var size = TextRenderer.MeasureText(label, font);
        var pill = new Rectangle(TrackRect.X - size.Width - 18, _host!.ClientSize.Height - size.Height - 12, size.Width + 12, size.Height + 4);
        using (var back = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillRectangle(back, pill);
        TextRenderer.DrawText(g, label, font, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ── Input (Preview only) ─────────────────────────────────────────────────────────────────────

    public void OnMouseWheel(MouseEventArgs e)
    {
        if (!Interactive) return;
        if ((Control.ModifierKeys & Keys.Control) != 0)
        {
            double zoom = Math.Clamp((Layout()?.Scale ?? 1) / (96.0 / 72.0) * (e.Delta > 0 ? 1.1 : 1 / 1.1), PdfLayout.MinZoom, PdfLayout.MaxZoom);
            SetZoom(PdfZoomMode.Custom, zoom);
            return;
        }
        // Precision touchpads send many small deltas; mice send ±120 per notch (≈ 3 lines).
        ScrollBy(-e.Delta / 120.0 * 60);
    }

    public void OnMouseDown(MouseEventArgs e)
    {
        if (!Interactive || e.Button != MouseButtons.Left || Layout() is not { } layout) return;
        if (!TrackRect.Contains(e.Location)) return;
        var thumb = ThumbRect(layout);
        if (thumb.Contains(e.Location))
        {
            _draggingThumb = true;
            _dragStartY = e.Y;
            _dragStartScroll = layout.ScrollOffset;
        }
        else ScrollBy(e.Y < thumb.Top ? -layout.Viewport.Height * 0.9 : layout.Viewport.Height * 0.9);
        _host?.Invalidate();
    }

    public void OnMouseMove(MouseEventArgs e)
    {
        if (!_draggingThumb || Layout() is not { } layout) return;
        var thumb = ThumbRect(layout);
        double travel = Math.Max(1, TrackRect.Height - thumb.Height);
        double target = _dragStartScroll + (e.Y - _dragStartY) / travel * layout.MaxScroll;
        MarkScrolling();
        SetState(layout.StateAt(target), user: true);
    }

    public void OnMouseUp(MouseEventArgs e)
    {
        if (!_draggingThumb) return;
        _draggingThumb = false;
        _host?.Invalidate();
    }

    public bool OnKeyDown(Keys key)
    {
        if (!Interactive || Layout() is not { } layout) return false;
        double page = layout.Viewport.Height * 0.9;
        switch (key & Keys.KeyCode)
        {
            case Keys.PageDown: ScrollBy(page); return true;
            case Keys.PageUp: ScrollBy(-page); return true;
            case Keys.Down: ScrollBy(60); return true;
            case Keys.Up: ScrollBy(-60); return true;
            case Keys.Home: SetState(_state with { PageIndex = 0, PageOffset = 0 }, true); return true;
            case Keys.End: SetState(_state with { PageIndex = PageCount - 1, PageOffset = 0.999 }, true); return true;
        }
        return false;
    }

    /// <summary>The cached rendering of the page the view mostly shows (borrowed; may be replaced on the
    /// next render), or null.</summary>
    public Bitmap? CurrentPageBitmap => _disposed ? null : _cache.GetAnySize(Math.Max(0, CurrentPage - 1));

    /// <summary>A copy of the page the view mostly shows (for thumbnails), or null if not rendered yet.</summary>
    public Bitmap? CopyCurrentPage()
    {
        var bitmap = _cache.GetAnySize(Math.Max(0, CurrentPage - 1));
        return bitmap == null ? null : new Bitmap(bitmap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settleTimer.Dispose();
        _service.Rendered -= OnRendered;
        _service.Failed -= OnFailed;
        _service.Dispose();
        _cache.Dispose();
        _host = null;
    }
}
