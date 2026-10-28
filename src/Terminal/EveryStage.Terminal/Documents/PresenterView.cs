using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using EveryStage.Terminal.ContentEngine;

namespace EveryStage.Terminal.Documents;

/// <summary>
/// The operator's presenter view for a presentation in the Preview monitor: the current slide (the live
/// slide-show capture, or a slide image while browsing), the next slide, the position ("第 3 / 12 张 ·
/// 动画 1/2"), the show state, and the speaker notes when the presentation has them. Never shown on Program.
/// </summary>
public sealed class PresenterView : IContentPainter, IDisposable
{
    private static readonly Color Background = Color.FromArgb(18, 20, 24);
    private Control? _host;
    private Bitmap? _current;      // borrowed (owned by the engine's shared frame)
    private Bitmap? _next;         // owned
    private Bitmap? _browse;       // owned: a slide image shown instead of the live capture
    private string _header = "";
    private string _status = "";
    private string _notes = "";

    public void Attach(Control host) => _host = host;
    public void Detach() => _host = null;

    /// <summary>The live frame to show as "当前" (not owned; valid until replaced).</summary>
    public void SetCurrent(Bitmap? frame)
    {
        _current = frame;
        _host?.Invalidate();
    }

    /// <summary>A slide image to show instead of the live frame (owned), or null for live.</summary>
    public void SetBrowseImage(Bitmap? image)
    {
        var old = _browse;
        _browse = image;
        old?.Dispose();
        _host?.Invalidate();
    }

    public void SetNext(Bitmap? image)
    {
        var old = _next;
        _next = image;
        old?.Dispose();
        _host?.Invalidate();
    }

    public void SetInfo(string header, string status, string notes)
    {
        _header = header;
        _status = status;
        _notes = notes;
        _host?.Invalidate();
    }

    public Bitmap? DisplayedCurrent => _browse ?? _current;

    public void Paint(Graphics g, Rectangle bounds)
    {
        using (var back = new SolidBrush(Background)) g.FillRectangle(back, bounds);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        int gap = Math.Max(4, bounds.Width / 120);
        int mainWidth = (int)(bounds.Width * 0.64);
        var main = new Rectangle(bounds.X, bounds.Y, mainWidth, bounds.Height);
        var side = Rectangle.FromLTRB(main.Right + gap, bounds.Y + gap, bounds.Right - gap, bounds.Bottom - gap);
        DrawFitted(g, _browse ?? _current, main);

        using var small = new Font("Microsoft YaHei UI", Math.Clamp(bounds.Height / 22f, 7.5f, 10f));
        int line = TextRenderer.MeasureText("第", small).Height;
        TextRenderer.DrawText(g, _header, small, new Rectangle(side.X, side.Y, side.Width, line), Color.White, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _status, small, new Rectangle(side.X, side.Y + line, side.Width, line), Color.FromArgb(150, 200, 255), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        int y = side.Y + line * 2 + gap;
        int nextHeight = Math.Min(side.Width * 9 / 16, Math.Max(0, (side.Bottom - y) / 2));
        if (nextHeight > 12)
        {
            TextRenderer.DrawText(g, "下一张", small, new Rectangle(side.X, y, side.Width, line), Color.FromArgb(160, 160, 170), TextFormatFlags.Left);
            y += line;
            var nextRect = new Rectangle(side.X, y, side.Width, nextHeight);
            if (_next != null) DrawFitted(g, _next, nextRect);
            else
            {
                using var empty = new SolidBrush(Color.FromArgb(32, 34, 40));
                g.FillRectangle(empty, nextRect);
                TextRenderer.DrawText(g, "（最后一张）", small, nextRect, Color.FromArgb(120, 120, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            y += nextHeight + gap;
        }
        if (_notes.Length > 0 && side.Bottom - y > line)
            TextRenderer.DrawText(g, "备注：" + _notes, small, Rectangle.FromLTRB(side.X, y, side.Right, side.Bottom), Color.FromArgb(215, 215, 220),
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    private static void DrawFitted(Graphics g, Bitmap? image, Rectangle area)
    {
        if (image == null || area.Width <= 0 || area.Height <= 0) return;
        float scale = Math.Min((float)area.Width / image.Width, (float)area.Height / image.Height);
        int w = (int)(image.Width * scale), h = (int)(image.Height * scale);
        g.DrawImage(image, new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    public void Dispose()
    {
        _next?.Dispose();
        _browse?.Dispose();
        _next = _browse = null;
        _current = null;
        _host = null;
    }
}
