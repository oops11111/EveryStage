using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.UI;

internal static class ModernUi
{
    public static readonly Color Background = Color.FromArgb(10, 20, 34);
    public static readonly Color Rail = Color.FromArgb(11, 25, 43);
    public static readonly Color Surface = Color.FromArgb(18, 37, 59);
    public static readonly Color SurfaceRaised = Color.FromArgb(25, 48, 76);
    public static readonly Color Border = Color.FromArgb(48, 75, 106);
    public static readonly Color Text = Color.FromArgb(239, 245, 255);
    public static readonly Color Muted = Color.FromArgb(155, 174, 201);
    public static readonly Color Accent = Color.FromArgb(66, 139, 255);
    public static readonly Color Danger = Color.FromArgb(255, 73, 78);
    public static readonly Color Success = Color.FromArgb(42, 210, 128);

    public static void StyleButton(Button button, bool primary = false, bool danger = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = danger ? Danger : Border;
        button.BackColor = primary ? (danger ? Danger : Accent) : Surface;
        button.ForeColor = Text;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        button.Padding = new Padding(8, 0, 8, 0);
    }

    public static Button NavButton(string glyph, string text, int top)
    {
        var button = new Button
        {
            Text = $"{glyph}  {text}",
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(10, top, 140, 52),
            Font = new Font("Segoe UI", 11F),
        };
        StyleButton(button);
        return button;
    }

    public static void SetNavActive(IEnumerable<Button> buttons, Button active)
    {
        foreach (var button in buttons)
        {
            bool selected = ReferenceEquals(button, active);
            button.BackColor = selected ? Color.FromArgb(34, 76, 128) : Rail;
            button.FlatAppearance.BorderSize = 0;
            button.ForeColor = selected ? Color.White : Muted;
        }
    }
}

public class GradientForm : Form
{
    public GradientForm()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(7, 17, 31), Color.FromArgb(18, 39, 65), 32F);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var glow = new SolidBrush(Color.FromArgb(28, 55, 125, 218));
        e.Graphics.FillEllipse(glow, ClientSize.Width / 3, -ClientSize.Height / 2,
            ClientSize.Width, ClientSize.Height);
    }
}

internal class GlassPanel : Panel
{
    public int CornerRadius { get; set; } = 16;
    public Color GlassTint { get; set; } = Color.FromArgb(205, 13, 30, 50);

    public GlassPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(ClientRectangle, CornerRadius);
        using var fill = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(Math.Min(255, GlassTint.A + 24), GlassTint), GlassTint, 110F);
        e.Graphics.FillPath(fill, path);
        using var highlight = new Pen(Color.FromArgb(70, 185, 217, 255), 1F);
        e.Graphics.DrawPath(highlight, path);
        var inset = ClientRectangle;
        inset.Inflate(-2, -2);
        using var innerPath = Rounded(inset, Math.Max(2, CornerRadius - 2));
        using var inner = new Pen(Color.FromArgb(22, 255, 255, 255), 1F);
        e.Graphics.DrawPath(inner, innerPath);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        bounds.Width = Math.Max(1, bounds.Width - 1);
        bounds.Height = Math.Max(1, bounds.Height - 1);
        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class PillButton : Button
{
    public bool Selected { get; set; }

    public PillButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 38;
        Padding = new Padding(16, 0, 16, 0);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Inflate(-1, -1);
        using var path = Rounded(rect, rect.Height / 2);
        using var fill = new SolidBrush(Selected ? ModernUi.Accent : ModernUi.SurfaceRaised);
        using var border = new Pen(Selected ? Color.FromArgb(120, 117, 181, 255) : ModernUi.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, ModernUi.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Size = new Size(52, 28);
        Cursor = Cursors.Hand;
        Text = string.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = ClientRectangle;
        track.Inflate(-1, -3);
        using var path = Rounded(track, track.Height / 2);
        using var trackBrush = new SolidBrush(Checked ? ModernUi.Accent : Color.FromArgb(65, 79, 98));
        e.Graphics.FillPath(trackBrush, path);
        int d = track.Height - 6;
        int x = Checked ? track.Right - d - 3 : track.Left + 3;
        using var knob = new SolidBrush(Color.White);
        e.Graphics.FillEllipse(knob, x, track.Top + 3, d, d);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }
}
