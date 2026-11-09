using System.Drawing.Drawing2D;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    // Shared row fonts (fixed sizes, so process-lifetime statics are safe).
    private static readonly Font RowTitleFont = new(ModernUi.UiFontSemibold, 10.5F);
    private static readonly Font RowMetaFont = new(ModernUi.UiFont, 9F);

    /// <summary>One terminal in the left list: a status dot (filled = online, ring = offline), the
    /// name, and whether it is paired. The selected row is a raised card with an accent edge.</summary>
    private void DrawTerminalRow(object? sender, DrawItemEventArgs e)
    {
        var p = ModernUi.Palette;
        var g = e.Graphics;
        using (var back = new SolidBrush(p.Sidebar)) g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || e.Index >= _terminalListBox.Items.Count) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var entry = (TerminalListEntry)_terminalListBox.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        int S(int value) => _terminalListBox.LogicalToDeviceUnits(value);

        var row = Rectangle.Inflate(e.Bounds, 0, -S(3));
        if (selected)
        {
            using var path = ModernUi.Rounded(row, S(8));
            using (var fill = new SolidBrush(p.Surface)) g.FillPath(fill, path);
            using (var border = new Pen(p.Border)) g.DrawPath(border, path);
            using var edge = new SolidBrush(p.Accent);
            g.FillRectangle(edge, row.Left, row.Top + S(12), S(3), row.Height - S(24));
        }

        bool online = entry.Live != null;
        bool paired = _pairedTerminals.Find(entry.DeviceId) != null;
        var dot = new Rectangle(row.Left + S(14), row.Top + (row.Height - S(9)) / 2, S(9), S(9));
        if (online)
        {
            using var dotBrush = new SolidBrush(p.Success);
            g.FillEllipse(dotBrush, dot);
        }
        else
        {
            using var ring = new Pen(p.Faint, 1.5f);
            g.DrawEllipse(ring, dot);
        }

        int textLeft = dot.Right + S(12), textWidth = row.Right - textLeft - S(8);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, entry.DeviceName, RowTitleFont, new Rectangle(textLeft, row.Top + S(6), textWidth, S(24)),
            online ? p.Text : p.Muted, flags);
        string meta = online ? (paired ? "已配对" : "未配对") : "离线";
        TextRenderer.DrawText(g, meta, RowMetaFont, new Rectangle(textLeft, row.Top + S(28), textWidth, S(20)), p.Muted, flags);
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
        {
            using var focus = new Pen(p.Accent, 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, Rectangle.Inflate(row, -2, -2));
        }
    }
}
