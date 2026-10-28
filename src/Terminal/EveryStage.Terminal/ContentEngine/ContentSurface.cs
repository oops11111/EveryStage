using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Draws whichever image/document renderer's <see cref="IContentRenderer.CurrentFrame"/> is
/// active, letterboxed (contain-fit, black bars) to fill the extended display without distorting
/// the source aspect ratio — the expected look for LED/projector output per PLANNING.md's overall
/// framing of the Terminal as a dedicated display surface.
///
/// Owns none of the renderers or their bitmaps; callers (the future file-switching logic in
/// Program.cs / a Phase-4 UI) are responsible for renderer lifetime and calling
/// <see cref="SetFrame"/> after each page turn or file change.
/// </summary>
public sealed class ContentSurface : Control
{
    private Bitmap? _frame;
    private bool _liveFrames;
    private IContentPainter? _painter;
    private EveryStage.Rendering.VideoScaleMode _scaleMode = EveryStage.Rendering.VideoScaleMode.Fit;
    private static Cursor? s_hiddenCursor;

    /// <summary>How the frame maps onto this surface. Fit (default, the extended display) shows the whole
    /// frame; FitWidth (the Preview monitor) always fills the width and crops top/bottom as needed.</summary>
    public EveryStage.Rendering.VideoScaleMode ScaleMode
    {
        get => _scaleMode;
        set { _scaleMode = value; Invalidate(); }
    }

    /// <summary>The painter currently drawing this surface (null when a plain frame is shown).</summary>
    public IContentPainter? Painter => _painter;

    /// <summary>The plain frame currently shown (null while a painter draws the surface).</summary>
    public Bitmap? CurrentFrame => _frame;

    /// <summary>Program output: no mouse pointer over the extended display.</summary>
    public bool HideCursor
    {
        get => Cursor == s_hiddenCursor;
        set => Cursor = value ? (s_hiddenCursor ??= CreateHiddenCursor()) : Cursors.Default;
    }

    public ContentSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        Dock = DockStyle.Fill;
        TabStop = false;
    }

    /// <summary>Shows one bitmap (not owned). Replaces any painter. <paramref name="live"/> marks frames
    /// that change many times a second (window capture), which are scaled with a cheaper filter.</summary>
    public void SetFrame(Bitmap? frame, bool live = false)
    {
        SwapPainter(null);
        if (!ReferenceEquals(frame, _frame)) ReleaseScaled();
        _frame = frame;
        _liveFrames = live;
        Invalidate();
    }

    /// <summary>Hands the whole surface to <paramref name="painter"/> (null clears it).</summary>
    public void SetPainter(IContentPainter? painter)
    {
        _frame = null;
        ReleaseScaled();
        SwapPainter(painter);
        Invalidate();
    }

    private void SwapPainter(IContentPainter? painter)
    {
        if (ReferenceEquals(painter, _painter)) return;
        var previous = _painter;
        _painter = painter;
        previous?.Detach();
        painter?.Attach(this);
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _painter?.OnMouseWheel(e); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _painter?.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _painter?.OnMouseUp(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_painter == null) return;
        if (!Focused && !HideCursor) Focus(); // keyboard scrolling (PageUp/PageDown/Home/End) follows a click
        _painter.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        _painter != null && (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_painter?.OnKeyDown(e.KeyData) == true) e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _painter?.OnResize();
        Invalidate();
    }

    private static Cursor CreateHiddenCursor()
    {
        using var blank = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        return new Cursor(blank.GetHicon());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_painter != null)
        {
            _painter.Paint(e.Graphics, ClientRectangle);
            return;
        }
        e.Graphics.Clear(Color.Black);
        if (_frame == null) return;

        e.Graphics.InterpolationMode = _liveFrames ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;

        float scale = _scaleMode switch
        {
            EveryStage.Rendering.VideoScaleMode.FitWidth => (float)Width / _frame.Width,
            EveryStage.Rendering.VideoScaleMode.Fill => Math.Max((float)Width / _frame.Width, (float)Height / _frame.Height),
            EveryStage.Rendering.VideoScaleMode.Stretch => 0f, // handled below
            _ => Math.Min((float)Width / _frame.Width, (float)Height / _frame.Height),
        };
        if (_scaleMode == EveryStage.Rendering.VideoScaleMode.Stretch)
        {
            DrawNoEdgeBleed(e.Graphics, new Rectangle(0, 0, Width, Height));
            return;
        }
        int drawWidth = (int)(_frame.Width * scale);
        int drawHeight = (int)(_frame.Height * scale);
        int x = (Width - drawWidth) / 2;
        int y = (Height - drawHeight) / 2;
        if (Math.Abs(scale - 1f) < 0.004f)
        {
            // 1:1 (a slide show captured at the output's own resolution): no filtering at all.
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(_frame, new Rectangle(x, y, _frame.Width, _frame.Height), 0, 0, _frame.Width, _frame.Height, GraphicsUnit.Pixel);
            return;
        }

        DrawNoEdgeBleed(e.Graphics, new Rectangle(x, y, drawWidth, drawHeight));
    }

    /// <summary>Scaled draw without the dark 1px seam GDI+'s bicubic filter otherwise blends into every
    /// edge (it samples past the bitmap and mixes in transparent black); mirroring the edge pixels
    /// (TileFlipXY) keeps the border the image's own colour — most visible when the Preview enlarges.
    ///
    /// A still frame is scaled once and the result reused: high-quality bicubic from a 48 MP photo took
    /// ~0.5 s per repaint on the UI thread, on every invalidation of every surface showing it (audit
    /// C-29). Live frames (window capture) change every time and are drawn directly.</summary>
    private void DrawNoEdgeBleed(Graphics g, Rectangle destination)
    {
        bool cacheable = !_liveFrames && destination.Width > 0 && destination.Height > 0
            && (long)destination.Width * destination.Height <= 4L * Math.Max(1, Width) * Math.Max(1, Height);
        if (cacheable)
        {
            if (_scaled == null || !ReferenceEquals(_scaledSource, _frame) || _scaled.Size != destination.Size)
            {
                ReleaseScaled();
                var scaled = new Bitmap(destination.Width, destination.Height);
                using (var sg = Graphics.FromImage(scaled))
                {
                    sg.InterpolationMode = g.InterpolationMode;
                    DrawScaled(sg, new Rectangle(Point.Empty, destination.Size));
                }
                _scaled = scaled;
                _scaledSource = _frame;
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor; // 1:1 copy of the cached result
            g.DrawImage(_scaled, destination.X, destination.Y, destination.Width, destination.Height);
            return;
        }
        DrawScaled(g, destination);
    }

    private void DrawScaled(Graphics g, Rectangle destination)
    {
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(_frame!, destination, 0, 0, _frame!.Width, _frame.Height, GraphicsUnit.Pixel, attributes);
    }

    private Bitmap? _scaled;
    private Bitmap? _scaledSource;

    private void ReleaseScaled()
    {
        _scaled?.Dispose();
        _scaled = null;
        _scaledSource = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseScaled();
        base.Dispose(disposing);
    }
}
