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
    private EveryStage.Rendering.VideoScaleMode _scaleMode = EveryStage.Rendering.VideoScaleMode.Fit;

    /// <summary>How the frame maps onto this surface. Fit (default, the extended display) shows the whole
    /// frame; FitWidth (the Preview monitor) always fills the width and crops top/bottom as needed.</summary>
    public EveryStage.Rendering.VideoScaleMode ScaleMode
    {
        get => _scaleMode;
        set { _scaleMode = value; Invalidate(); }
    }

    public ContentSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Black;
        Dock = DockStyle.Fill;
    }

    public void SetFrame(Bitmap? frame)
    {
        _frame = frame;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        if (_frame == null) return;

        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

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

        DrawNoEdgeBleed(e.Graphics, new Rectangle(x, y, drawWidth, drawHeight));
    }

    /// <summary>Scaled draw without the dark 1px seam GDI+'s bicubic filter otherwise blends into every
    /// edge (it samples past the bitmap and mixes in transparent black); mirroring the edge pixels
    /// (TileFlipXY) keeps the border the image's own colour — most visible when the Preview enlarges.</summary>
    private void DrawNoEdgeBleed(Graphics g, Rectangle destination)
    {
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(_frame!, destination, 0, 0, _frame!.Width, _frame.Height, GraphicsUnit.Pixel, attributes);
    }
}
