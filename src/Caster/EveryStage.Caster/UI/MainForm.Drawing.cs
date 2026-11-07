using System.Drawing.Drawing2D;
using EveryStage.Caster.Capture;
using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Transport;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    // P-04: shared card fonts for DrawTerminalCard (fixed sizes, so statics are safe).
    private static readonly Font CardTitleFont = new(ModernUi.UiFontSemibold, 12F);
    private static readonly Font CardMetaFont = new(ModernUi.UiFont, 9.5F);

    private void DrawTerminalCard(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _terminalListBox.Items.Count) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var entry = (TerminalListEntry)_terminalListBox.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        // Layout in 96-DPI units scaled to the list's DPI: the fixed pixels clipped the title at 125–150 %.
        int S(int value) => _terminalListBox.LogicalToDeviceUnits(value);
        var card = e.Bounds;
        card.Inflate(-S(4), -S(6));
        using (var path = RoundedCard(card, S(14)))
        using (var fill = new SolidBrush(selected ? Color.FromArgb(26, 48, 82) : Color.FromArgb(22, 34, 54)))
        using (var border = new Pen(selected ? ModernUi.Accent : Color.FromArgb(44, 62, 90), selected ? 2F : 1F))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        // Monitor icon (matches the design's line-art display).
        var iconRect = new Rectangle(card.Left + S(20), card.Top + (card.Height - S(40)) / 2, S(48), S(40));
        DrawMonitorGlyph(g, iconRect, Color.FromArgb(150, 178, 220));

        // P-04: process-lifetime statics, never disposed — these two were allocated on every
        // card paint (and this list repaints on every refresh tick it doesn't skip).
        var titleFont = CardTitleFont;
        var metaFont = CardMetaFont;
        int textLeft = iconRect.Right + S(18);
        TextRenderer.DrawText(g, entry.DeviceName, titleFont,
            new Rectangle(textLeft, card.Top + S(14), card.Width - textLeft - S(60) + card.Left, S(28)), ModernUi.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        string meta = entry.Live != null ? "在线 · 可投屏" : "离线 · 等待终端上线";
        TextRenderer.DrawText(g, meta, metaFont,
            new Rectangle(textLeft, card.Top + S(42), card.Width - textLeft - S(60) + card.Left, S(22)),
            entry.Live != null ? ModernUi.Muted : Color.FromArgb(128, 143, 164),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        // Round radio selector on the right (design: filled blue check when selected, empty ring otherwise).
        var radio = new Rectangle(card.Right - S(46), card.Top + (card.Height - S(26)) / 2, S(26), S(26));
        if (selected)
        {
            using var fillSel = new SolidBrush(ModernUi.Accent);
            g.FillEllipse(fillSel, radio);
            using var check = new Pen(Color.White, 2.4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(check, new[]
            {
                new PointF(radio.Left + S(7), radio.Top + S(13)),
                new PointF(radio.Left + S(11), radio.Top + S(17)),
                new PointF(radio.Left + S(19), radio.Top + S(8)),
            });
        }
        else
        {
            using var ring = new Pen(Color.FromArgb(90, 110, 138), 2F);
            g.DrawEllipse(ring, radio);
        }
    }

    private static void DrawMonitorGlyph(Graphics g, Rectangle r, Color color)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 2.4F) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var screen = new RectangleF(r.Left, r.Top, r.Width, r.Height * 0.72f);
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            float d = 8;
            path.AddArc(screen.Left, screen.Top, d, d, 180, 90);
            path.AddArc(screen.Right - d, screen.Top, d, d, 270, 90);
            path.AddArc(screen.Right - d, screen.Bottom - d, d, d, 0, 90);
            path.AddArc(screen.Left, screen.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }
        g.DrawLine(pen, r.Left + r.Width / 2, screen.Bottom, r.Left + r.Width / 2, r.Bottom);
        g.DrawLine(pen, r.Left + r.Width * 0.30f, r.Bottom, r.Left + r.Width * 0.70f, r.Bottom);
    }

    private void DrawMonitorItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        using var background = new SolidBrush(ModernUi.SurfaceRaised);
        e.Graphics.FillRectangle(background, e.Bounds);
        string text = e.Index >= 0 && e.Index < _monitorComboBox.Items.Count
            ? _monitorComboBox.Items[e.Index]?.ToString() ?? ""
            : _monitorComboBox.Text;
        var iconRect = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + (e.Bounds.Height - 16) / 2, 20, 16);
        DrawMonitorGlyph(e.Graphics, iconRect, Color.FromArgb(150, 178, 220));
        var textRect = new Rectangle(iconRect.Right + 10, e.Bounds.Top, e.Bounds.Width - iconRect.Right - 14, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, textRect, ModernUi.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedCard(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
