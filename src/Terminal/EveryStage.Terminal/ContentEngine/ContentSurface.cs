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

        float scale = Math.Min((float)Width / _frame.Width, (float)Height / _frame.Height);
        int drawWidth = (int)(_frame.Width * scale);
        int drawHeight = (int)(_frame.Height * scale);
        int x = (Width - drawWidth) / 2;
        int y = (Height - drawHeight) / 2;

        e.Graphics.DrawImage(_frame, x, y, drawWidth, drawHeight);
    }
}
