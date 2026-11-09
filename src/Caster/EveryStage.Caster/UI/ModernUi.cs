using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

/// <summary>Which colours the Caster uses: follow Windows' app mode, or force light / dark.</summary>
public enum CasterTheme { System, Light, Dark }

/// <summary>One complete colour set. Every control reads the CURRENT palette when it paints or when
/// <see cref="ModernUi.ApplyTheme"/> walks the tree, so a theme switch is a repaint, not a rebuild.</summary>
internal sealed record CasterPalette(
    bool IsDark,
    Color Background, Color Sidebar, Color Surface, Color SurfaceHover, Color Border, Color ControlBorder,
    Color Text, Color Muted, Color Faint,
    Color Accent, Color AccentHover, Color AccentText, Color AccentSoft,
    Color Danger, Color DangerHover,
    Color Success, Color Warning, Color DangerText,
    Color WarningSurface, Color WarningBorder, Color WarningText,
    Color DangerSurface, Color DangerBorder,
    Color SuccessSurface, Color SuccessBorder,
    Color BarBackground, Color BarText, Color BarMuted)
{
    public static readonly CasterPalette Light = new(
        IsDark: false,
        Background: Color.FromArgb(243, 244, 246), Sidebar: Color.FromArgb(236, 238, 241),
        Surface: Color.White, SurfaceHover: Color.FromArgb(246, 248, 250),
        Border: Color.FromArgb(225, 228, 232), ControlBorder: Color.FromArgb(208, 215, 222),
        Text: Color.FromArgb(27, 31, 36), Muted: Color.FromArgb(87, 96, 106), Faint: Color.FromArgb(140, 149, 159),
        Accent: Color.FromArgb(0, 95, 184), AccentHover: Color.FromArgb(0, 78, 152), AccentText: Color.White,
        AccentSoft: Color.FromArgb(232, 241, 251),
        Danger: Color.FromArgb(198, 47, 56), DangerHover: Color.FromArgb(170, 36, 45),
        Success: Color.FromArgb(26, 127, 55), Warning: Color.FromArgb(154, 103, 0), DangerText: Color.FromArgb(138, 28, 35),
        WarningSurface: Color.FromArgb(255, 248, 229), WarningBorder: Color.FromArgb(240, 214, 145), WarningText: Color.FromArgb(110, 74, 0),
        DangerSurface: Color.FromArgb(255, 241, 240), DangerBorder: Color.FromArgb(243, 184, 180),
        SuccessSurface: Color.FromArgb(234, 247, 238), SuccessBorder: Color.FromArgb(166, 218, 182),
        BarBackground: Color.FromArgb(27, 31, 36), BarText: Color.White, BarMuted: Color.FromArgb(201, 209, 217));

    public static readonly CasterPalette Dark = new(
        IsDark: true,
        Background: Color.FromArgb(15, 23, 34), Sidebar: Color.FromArgb(11, 18, 27),
        Surface: Color.FromArgb(23, 34, 49), SurfaceHover: Color.FromArgb(30, 44, 62),
        Border: Color.FromArgb(38, 52, 69), ControlBorder: Color.FromArgb(52, 70, 92),
        Text: Color.FromArgb(238, 243, 249), Muted: Color.FromArgb(159, 178, 200), Faint: Color.FromArgb(110, 128, 150),
        Accent: Color.FromArgb(47, 124, 246), AccentHover: Color.FromArgb(76, 145, 255), AccentText: Color.White,
        AccentSoft: Color.FromArgb(24, 45, 74),
        Danger: Color.FromArgb(198, 47, 56), DangerHover: Color.FromArgb(222, 64, 73),
        Success: Color.FromArgb(95, 224, 160), Warning: Color.FromArgb(255, 198, 92), DangerText: Color.FromArgb(255, 160, 166),
        WarningSurface: Color.FromArgb(42, 34, 16), WarningBorder: Color.FromArgb(107, 84, 32), WarningText: Color.FromArgb(240, 220, 174),
        DangerSurface: Color.FromArgb(42, 18, 21), DangerBorder: Color.FromArgb(122, 42, 49),
        SuccessSurface: Color.FromArgb(15, 42, 31), SuccessBorder: Color.FromArgb(31, 107, 72),
        BarBackground: Color.FromArgb(6, 10, 16), BarText: Color.White, BarMuted: Color.FromArgb(170, 186, 206));
}

/// <summary>Text colour role of a <see cref="ThemedLabel"/>.</summary>
internal enum TextRole { Text, Muted, Faint, Accent, Success, Warning, Danger, WarningText, DangerText }

internal static class ModernUi
{
    /// <summary>The UI text font: Windows 11's Segoe UI Variable (Text optical size) when it is installed
    /// AND Windows links it to Microsoft YaHei UI for Chinese (HKLM FontLink\SystemLink); otherwise Segoe
    /// UI. The link check matters: the UI is mostly Chinese, Segoe UI Variable has no CJK glyphs, and a
    /// machine without that link (seen in the wild) renders the Chinese with a worse fallback or as boxes.</summary>
    internal static readonly string UiFont = EveryStage.SharedUi.UiFonts.Text;

    /// <summary>Semibold counterpart of <see cref="UiFont"/> (headings, labels).</summary>
    internal static readonly string UiFontSemibold = EveryStage.SharedUi.UiFonts.TextSemibold;

    public static CasterPalette Palette { get; private set; } = SystemPrefersLight() ? CasterPalette.Light : CasterPalette.Dark;
    public static CasterTheme Theme { get; private set; } = CasterTheme.System;

    public static Color Background => Palette.Background;
    public static Color Surface => Palette.Surface;
    public static Color Border => Palette.Border;
    public static Color Text => Palette.Text;
    public static Color Muted => Palette.Muted;
    public static Color Accent => Palette.Accent;
    public static Color Danger => Palette.Danger;
    public static Color Success => Palette.Success;
    public static Color Warning => Palette.Warning;

    /// <summary>Picks the palette for <paramref name="theme"/> (System reads Windows' "app mode").</summary>
    public static void SetTheme(CasterTheme theme)
    {
        Theme = theme;
        Palette = theme switch
        {
            CasterTheme.Light => CasterPalette.Light,
            CasterTheme.Dark => CasterPalette.Dark,
            _ => SystemPrefersLight() ? CasterPalette.Light : CasterPalette.Dark,
        };
    }

    public static string ThemeName(CasterTheme theme) => theme switch
    {
        CasterTheme.Light => "浅色",
        CasterTheme.Dark => "深色",
        _ => "跟随系统",
    };

    /// <summary>HKCU …\Themes\Personalize\AppsUseLightTheme: 1 (or missing) = light apps.</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception) { return true; }
    }

    public static Color For(TextRole role) => role switch
    {
        TextRole.Muted => Palette.Muted,
        TextRole.Faint => Palette.Faint,
        TextRole.Accent => Palette.Accent,
        TextRole.Success => Palette.Success,
        TextRole.Warning => Palette.Warning,
        TextRole.Danger => Palette.Danger,
        TextRole.WarningText => Palette.WarningText,
        TextRole.DangerText => Palette.DangerText,
        _ => Palette.Text,
    };

    /// <summary>Re-colours a whole control tree for the current palette. Custom-painted controls only
    /// need the repaint; stock controls get their colours set here.</summary>
    public static void ApplyTheme(Control root)
    {
        switch (root)
        {
            case ThemedLabel label:
                label.ForeColor = For(label.Role);
                label.BackColor = Color.Transparent;
                break;
            case CheckBox check:
                check.ForeColor = Palette.Text;
                check.BackColor = Color.Transparent;
                break;
            case ListBox list:
                list.ForeColor = Palette.Text;
                break;
            case Label label:
                label.ForeColor = Palette.Text;
                label.BackColor = Color.Transparent;
                break;
            case ThemedSurface surface:
                surface.BackColor = surface.Kind == SurfaceKind.Sidebar ? Palette.Sidebar : Palette.Background;
                break;
        }
        foreach (Control child in root.Controls) ApplyTheme(child);
        root.Invalidate();
    }

    /// <summary>The colour actually painted behind <paramref name="control"/>: the nearest opaque parent,
    /// or a card's fill (cards draw their own rounded surface over a transparent BackColor).</summary>
    public static Color BackgroundBehind(Control control)
    {
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is CardPanel card) return card.FillColor;
            if (parent.BackColor.A == 255) return parent.BackColor;
        }
        return Palette.Background;
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A label whose colour follows the theme by role.</summary>
internal sealed class ThemedLabel : Label
{
    public TextRole Role { get; set; } = TextRole.Text;

    public ThemedLabel()
    {
        BackColor = Color.Transparent;
        UseMnemonic = false;
    }

    public ThemedLabel With(TextRole role)
    {
        Role = role;
        ForeColor = ModernUi.For(role);
        return this;
    }
}

internal enum SurfaceKind { Background, Sidebar }

/// <summary>A plain panel that takes the page or sidebar background of the current palette.</summary>
internal class ThemedSurface : Panel
{
    public SurfaceKind Kind { get; set; } = SurfaceKind.Background;

    public ThemedSurface()
    {
        DoubleBuffered = true;
        BackColor = ModernUi.Background;
    }
}

internal enum CardKind { Normal, Warning, Danger, Success }

/// <summary>A rounded card: surface fill and a 1 px border, or a tinted notice (warning / danger /
/// success). Children with a transparent background show the card through.</summary>
internal sealed class CardPanel : Panel
{
    public CardKind Kind { get; set; } = CardKind.Normal;
    public int Radius { get; set; } = 10;

    public CardPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    public Color FillColor => Kind switch
    {
        CardKind.Warning => ModernUi.Palette.WarningSurface,
        CardKind.Danger => ModernUi.Palette.DangerSurface,
        CardKind.Success => ModernUi.Palette.SuccessSurface,
        _ => ModernUi.Palette.Surface,
    };

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = ModernUi.Palette;
        using (var back = new SolidBrush(ModernUi.BackgroundBehind(this)))
            e.Graphics.FillRectangle(back, ClientRectangle);
        if (ClientSize.Width < 4 || ClientSize.Height < 4) return;
        Color fill = FillColor;
        Color border = Kind switch
        {
            CardKind.Warning => p.WarningBorder,
            CardKind.Danger => p.DangerBorder,
            CardKind.Success => p.SuccessBorder,
            _ => p.Border,
        };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ModernUi.Rounded(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1.5f, ClientSize.Height - 1.5f), LogicalToDeviceUnits(Radius));
        using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
        using var pen = new Pen(border);
        e.Graphics.DrawPath(pen, path);
    }
}

internal enum ButtonKind { Primary, Secondary, Danger, Ghost }

/// <summary>A rounded, theme-painted push button (keyboard focus and accessibility stay the stock
/// Button's). <see cref="Glyph"/> draws a small icon left of the text.</summary>
internal sealed class ActionButton : Button
{
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public ButtonGlyph Glyph { get; set; } = ButtonGlyph.None;
    private bool _hot, _pressed;

    public ActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = ModernUi.Palette;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color behind = ModernUi.BackgroundBehind(this);
        using (var back = new SolidBrush(behind))
            g.FillRectangle(back, ClientRectangle);

        bool hot = (_hot || _pressed) && Enabled;
        (Color fill, Color border, Color ink) = Kind switch
        {
            ButtonKind.Primary => (hot ? p.AccentHover : p.Accent, Color.Empty, p.AccentText),
            ButtonKind.Danger => (hot ? p.DangerHover : p.Danger, Color.Empty, Color.White),
            ButtonKind.Ghost => (hot ? (behind == p.Surface ? p.Background : p.SurfaceHover) : Color.Empty, Color.Empty, p.Muted),
            _ => (hot ? p.SurfaceHover : p.Surface, p.ControlBorder, p.Text),
        };
        if (!Enabled)
        {
            if (Kind is ButtonKind.Primary or ButtonKind.Danger) fill = p.IsDark ? Color.FromArgb(44, 58, 76) : Color.FromArgb(208, 215, 222);
            ink = p.Faint;
        }
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = ModernUi.Rounded(rect, LogicalToDeviceUnits(8));
        if (!fill.IsEmpty) using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (!border.IsEmpty) using (var pen = new Pen(border)) g.DrawPath(pen, path);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(p.Accent, 2f);
            using var inner = ModernUi.Rounded(RectangleF.Inflate(rect, -2.5f, -2.5f), LogicalToDeviceUnits(6));
            g.DrawPath(focus, inner);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        Size textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
        int glyph = Glyph == ButtonGlyph.None ? 0 : LogicalToDeviceUnits(16);
        int gap = glyph > 0 && Text.Length > 0 ? LogicalToDeviceUnits(8) : 0;
        int total = glyph + gap + textSize.Width;
        int left = TextAlign is ContentAlignment.MiddleLeft ? LogicalToDeviceUnits(12) : Math.Max(LogicalToDeviceUnits(6), (Width - total) / 2);
        if (glyph > 0) Glyphs.Draw(g, Glyph, new Rectangle(left, (Height - glyph) / 2, glyph, glyph), ink);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left + glyph + gap, 0, Width - left - glyph - gap, Height), ink, flags);
    }
}

/// <summary>A selectable tile (a radio button underneath, so arrow keys and screen readers work): the
/// monitor picker uses one per display.</summary>
internal sealed class ChoiceTile : RadioButton
{
    public string Badge { get; set; } = "";

    public ChoiceTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AutoSize = false;
        UseMnemonic = false;
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = ModernUi.Palette;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(ModernUi.BackgroundBehind(this)))
            g.FillRectangle(back, ClientRectangle);
        var rect = new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f);
        using var path = ModernUi.Rounded(rect, LogicalToDeviceUnits(8));
        using (var fill = new SolidBrush(Checked ? p.AccentSoft : p.Surface)) g.FillPath(fill, path);
        using (var pen = new Pen(Checked ? p.Accent : p.ControlBorder, Checked ? 2f : 1f)) g.DrawPath(pen, path);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(p.Accent, 1f) { DashStyle = DashStyle.Dot };
            using var inner = ModernUi.Rounded(RectangleF.Inflate(rect, -3f, -3f), LogicalToDeviceUnits(6));
            g.DrawPath(focus, inner);
        }

        int pad = LogicalToDeviceUnits(10);
        var badge = new Rectangle(pad, (Height - LogicalToDeviceUnits(26)) / 2, LogicalToDeviceUnits(40), LogicalToDeviceUnits(26));
        using (var badgePath = ModernUi.Rounded(badge, LogicalToDeviceUnits(4)))
        using (var badgeFill = new SolidBrush(Checked ? p.Accent : p.ControlBorder))
            g.FillPath(badgeFill, badgePath);
        using (var badgeFont = new Font(ModernUi.UiFontSemibold, 9.5F))
            TextRenderer.DrawText(g, Badge, badgeFont, badge, Checked ? p.AccentText : p.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(badge.Right + pad, 0, Width - badge.Right - pad * 2, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

internal enum ButtonGlyph { None, Cast, Stop, Back, Gear, Theme, ChevronDown, ChevronUp, Check, Warning, Cross, Info, Clock, Monitor }

/// <summary>Small line icons drawn with GDI+ (no emoji, no icon font dependency).</summary>
internal static class Glyphs
{
    public static void Draw(Graphics g, ButtonGlyph glyph, Rectangle r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = r.Width / 24f;
        PointF P(float x, float y) => new(r.Left + x * u, r.Top + y * u);
        using var pen = new Pen(color, Math.Max(1.4f, 2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (glyph)
        {
            case ButtonGlyph.Cast:
            case ButtonGlyph.Monitor:
                using (var screen = ModernUi.Rounded(new RectangleF(P(2, 4).X, P(2, 4).Y, 20 * u, 13 * u), 2 * u)) g.DrawPath(pen, screen);
                g.DrawLine(pen, P(8, 21), P(16, 21));
                g.DrawLine(pen, P(12, 17), P(12, 21));
                if (glyph == ButtonGlyph.Cast) g.FillPolygon(brush, new[] { P(10, 8), P(15, 10.5f), P(10, 13) });
                break;
            case ButtonGlyph.Stop:
                using (var square = ModernUi.Rounded(new RectangleF(P(5, 5).X, P(5, 5).Y, 14 * u, 14 * u), 2 * u)) g.FillPath(brush, square);
                break;
            case ButtonGlyph.Back:
                g.DrawLines(pen, new[] { P(15, 5), P(8, 12), P(15, 19) });
                break;
            case ButtonGlyph.ChevronDown:
                g.DrawLines(pen, new[] { P(6, 9), P(12, 15), P(18, 9) });
                break;
            case ButtonGlyph.ChevronUp:
                g.DrawLines(pen, new[] { P(6, 15), P(12, 9), P(18, 15) });
                break;
            case ButtonGlyph.Check:
                g.DrawLines(pen, new[] { P(5, 12), P(10, 17), P(19, 7) });
                break;
            case ButtonGlyph.Cross:
                g.DrawLine(pen, P(6, 6), P(18, 18));
                g.DrawLine(pen, P(18, 6), P(6, 18));
                break;
            case ButtonGlyph.Warning:
                g.DrawPolygon(pen, new[] { P(12, 3), P(22, 21), P(2, 21) });
                g.DrawLine(pen, P(12, 10), P(12, 14));
                g.FillEllipse(brush, new RectangleF(P(11, 16.5f), new SizeF(2 * u, 2 * u)));
                break;
            case ButtonGlyph.Info:
                g.DrawEllipse(pen, new RectangleF(P(3, 3), new SizeF(18 * u, 18 * u)));
                g.DrawLine(pen, P(12, 11), P(12, 16));
                g.FillEllipse(brush, new RectangleF(P(11, 7), new SizeF(2 * u, 2 * u)));
                break;
            case ButtonGlyph.Clock:
                g.DrawEllipse(pen, new RectangleF(P(3, 3), new SizeF(18 * u, 18 * u)));
                g.DrawLines(pen, new[] { P(12, 7), P(12, 12), P(15, 14) });
                break;
            case ButtonGlyph.Gear:
                g.DrawEllipse(pen, new RectangleF(P(9, 9), new SizeF(6 * u, 6 * u)));
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(pen, P(12 + 6 * (float)Math.Cos(a), 12 + 6 * (float)Math.Sin(a)), P(12 + 9 * (float)Math.Cos(a), 12 + 9 * (float)Math.Sin(a)));
                }
                break;
            case ButtonGlyph.Theme:
                g.DrawEllipse(pen, new RectangleF(P(3, 3), new SizeF(18 * u, 18 * u)));
                g.FillPie(brush, P(3, 3).X, P(3, 3).Y, 18 * u, 18 * u, 90, 180);
                break;
        }
    }
}

/// <summary>The borderless main window: solid page colour, resizable from its edges, minimizable
/// from the task bar.</summary>
public class GradientForm : Form
{
    private Size _logicalMinimumSize;

    public GradientForm()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnShown(EventArgs e)
    {
        CaptureLogicalMinimumSize();
        FitToCurrentWorkingArea();
        base.OnShown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToCurrentWorkingArea();
    }

    private void CaptureLogicalMinimumSize()
    {
        if (!_logicalMinimumSize.IsEmpty || MinimumSize.IsEmpty) return;
        _logicalMinimumSize = new Size(
            Math.Max(1, MinimumSize.Width * 96 / DeviceDpi),
            Math.Max(1, MinimumSize.Height * 96 / DeviceDpi));
    }

    private void FitToCurrentWorkingArea()
    {
        if (WindowState != FormWindowState.Normal) return;

        CaptureLogicalMinimumSize();
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        int margin = LogicalToDeviceUnits(12);
        int maximumWidth = Math.Max(LogicalToDeviceUnits(320), workingArea.Width - margin * 2);
        int maximumHeight = Math.Max(LogicalToDeviceUnits(320), workingArea.Height - margin * 2);

        if (!_logicalMinimumSize.IsEmpty)
        {
            MinimumSize = new Size(
                Math.Min(LogicalToDeviceUnits(_logicalMinimumSize.Width), maximumWidth),
                Math.Min(LogicalToDeviceUnits(_logicalMinimumSize.Height), maximumHeight));
        }

        Size = new Size(Math.Min(Width, maximumWidth), Math.Min(Height, maximumHeight));
        Location = new Point(
            Math.Clamp(Left, workingArea.Left + margin, Math.Max(workingArea.Left + margin, workingArea.Right - margin - Width)),
            Math.Clamp(Top, workingArea.Top + margin, Math.Max(workingArea.Top + margin, workingArea.Bottom - margin - Height)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new SolidBrush(ModernUi.Background);
        e.Graphics.FillRectangle(background, ClientRectangle);
    }

    /// <summary>WinForms leaves WS_MINIMIZEBOX off a borderless form, so clicking the task-bar button
    /// could not minimize the Caster window. (The window has no maximize, see AppTitleBar.)</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (FormBorderStyle == FormBorderStyle.None && MinimizeBox) cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
        {
            var p = PointToClient(Cursor.Position);
            int g = LogicalToDeviceUnits(6);
            bool l = p.X <= g, r = p.X >= ClientSize.Width - g, t = p.Y <= g, b = p.Y >= ClientSize.Height - g;
            if (l && t) { m.Result = new IntPtr(13); return; }
            if (r && t) { m.Result = new IntPtr(14); return; }
            if (l && b) { m.Result = new IntPtr(16); return; }
            if (r && b) { m.Result = new IntPtr(17); return; }
            if (l) { m.Result = new IntPtr(10); return; }
            if (r) { m.Result = new IntPtr(11); return; }
            if (t) { m.Result = new IntPtr(12); return; }
            if (b) { m.Result = new IntPtr(15); return; }
        }
        base.WndProc(ref m);
    }
}

/// <summary>The custom title strip: drag to move, minimize and close. Takes the sidebar colour on its
/// left part (the sidebar runs under it) and the page colour on the right.</summary>
internal sealed class AppTitleBar : Panel
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly Form _form;
    public int SidebarWidth { get; set; }

    public AppTitleBar(Form form)
    {
        _form = form;
        Dock = DockStyle.Top;
        Height = 36;
        DoubleBuffered = true;
        var close = new CaptionButton(CaptionButton.CloseGlyph, isClose: true);
        var minimize = new CaptionButton(CaptionButton.MinimizeGlyph);
        close.Click += (_, _) => _form.Close();
        minimize.Click += (_, _) => _form.WindowState = FormWindowState.Minimized;
        // Dock=Right stacks so that the LAST-added control ends up right-most.
        Controls.Add(minimize);
        Controls.Add(close);
        MouseDown += DragWindow;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = ModernUi.Palette;
        BackColor = p.Background;
        using (var page = new SolidBrush(p.Background)) e.Graphics.FillRectangle(page, ClientRectangle);
        using (var side = new SolidBrush(p.Sidebar)) e.Graphics.FillRectangle(side, 0, 0, SidebarWidth, Height);
        using var line = new Pen(p.Border);
        e.Graphics.DrawLine(line, SidebarWidth - 1, 0, SidebarWidth - 1, Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = ModernUi.Palette;
        int s(int v) => LogicalToDeviceUnits(v);
        var icon = new Rectangle(s(20), (Height - s(18)) / 2, s(18), s(18));
        Glyphs.Draw(e.Graphics, ButtonGlyph.Monitor, icon, p.Accent);
        using var font = new Font(ModernUi.UiFontSemibold, 10F);
        TextRenderer.DrawText(e.Graphics, "EveryStage 投屏器", font, new Rectangle(icon.Right + s(10), 0, Math.Max(0, SidebarWidth - icon.Right - s(16)), Height),
            p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(_form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }
}

/// <summary>A Windows 11 caption button: Segoe Fluent Icons glyph (Segoe MDL2 Assets on Windows 10, the
/// same code points), 46 px wide, a faint fill on hover/press and the system red behind a white glyph
/// for close. Mirrors the Terminal's CaptionButton (each app keeps its own copy).</summary>
internal sealed class CaptionButton : Control
{
    public const string MinimizeGlyph = "", CloseGlyph = "";

    private static readonly string IconFontFamily = PickIconFont();
    private readonly string _glyph;
    private readonly bool _isClose;
    private bool _hot, _pressed;

    public CaptionButton(string glyph, bool isClose = false)
    {
        _glyph = glyph;
        _isClose = isClose;
        Dock = DockStyle.Right;
        Width = 46;
        TabStop = false;
        // Styles first: a transparent BackColor throws unless SupportsTransparentBackColor is already set.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Font = new Font(IconFontFamily, 7.5F);
        AccessibleName = isClose ? "关闭" : "最小化";
        AccessibleRole = AccessibleRole.PushButton;
    }

    private static string PickIconFont()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
            : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "Segoe UI Symbol";
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var background = ModernUi.Background;
        Color fill = background, ink = ModernUi.Text;
        if (_isClose && (_hot || _pressed))
        {
            fill = _pressed ? Color.FromArgb(148, 22, 15) : Color.FromArgb(196, 43, 28);
            ink = Color.White;
        }
        else if (_pressed) fill = Mix(background, ModernUi.Text, 0.06);
        else if (_hot) fill = Mix(background, ModernUi.Text, 0.09);
        using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, ClientRectangle);
        string text = IconFontFamily == "Segoe UI Symbol" ? (_glyph == CloseGlyph ? "×" : "—") : _glyph;
        TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
