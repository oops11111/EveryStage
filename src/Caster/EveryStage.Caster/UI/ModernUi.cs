using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

internal static class ModernUi
{
    public static readonly Color Background = Color.FromArgb(10, 20, 34);
    public static readonly Color Surface = Color.FromArgb(18, 37, 59);
    public static readonly Color SurfaceRaised = Color.FromArgb(25, 48, 76);
    public static readonly Color Border = Color.FromArgb(48, 75, 106);
    public static readonly Color Text = Color.FromArgb(239, 245, 255);
    public static readonly Color Muted = Color.FromArgb(155, 174, 201);
    public static readonly Color Accent = Color.FromArgb(66, 139, 255);
    public static readonly Color Danger = Color.FromArgb(239, 63, 68);
    public static readonly Color Success = Color.FromArgb(42, 210, 128);
    public static readonly Color Warning = Color.FromArgb(255, 181, 71);

    public static void StyleTree(Control root)
    {
        root.Font = new Font("Segoe UI", 10F);
        Apply(root, isRoot: true);
    }

    private static void Apply(Control root, bool isRoot = false)
    {
        if (isRoot) root.BackColor = Background;
        else if (root is GlassPanel) root.BackColor = Color.Transparent;
        else if (root is Panel) root.BackColor = Background;
        if (root is not Label || root.ForeColor == SystemColors.ControlText) root.ForeColor = Text;
        foreach (Control child in root.Controls)
        {
            if (child is not Label || child.ForeColor == SystemColors.ControlText) child.ForeColor = Text;
            child.BackColor = child switch
            {
                Button => Surface,
                ListBox or ComboBox => SurfaceRaised,
                Label or CheckBox or RadioButton => Color.Transparent,
                Panel => Background,
                _ => Background,
            };
            if (child is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.Cursor = Cursors.Hand;
            }
            Apply(child);
        }
    }

    public static void Primary(Button button, bool danger = false)
    {
        button.BackColor = danger ? Danger : Accent;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 11F);
    }
}

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
        using var background = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(7, 17, 31), Color.FromArgb(18, 39, 65), 35F);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var glow = new SolidBrush(Color.FromArgb(24, 65, 137, 232));
        e.Graphics.FillEllipse(glow, ClientSize.Width / 3, -ClientSize.Height / 3,
            ClientSize.Width, ClientSize.Height);
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

internal class GlassPanel : Panel
{
    public int CornerRadius { get; set; } = 18;
    public Color GlassTint { get; set; } = Color.FromArgb(195, 14, 32, 53);

    public GlassPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        base.OnPaintBackground(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var path = Rounded(ClientRectangle, LogicalToDeviceUnits(CornerRadius));
        using var fill = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(Math.Min(255, GlassTint.A + 25), GlassTint), GlassTint, 112F);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(75, 166, 207, 255));
        e.Graphics.DrawPath(border, path);
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

internal sealed class AppTitleBar : Panel
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly Form _form;

    public AppTitleBar(Form form)
    {
        _form = form;
        Dock = DockStyle.Top;
        Height = 42;
        BackColor = Color.FromArgb(9, 23, 39);
        var appIcon = new PictureBox
        {
            Dock = DockStyle.Left, Width = 42, Padding = new Padding(12, 9, 4, 9),
            SizeMode = PictureBoxSizeMode.Zoom, Image = form.Icon?.ToBitmap(), BackColor = BackColor,
        };
        var title = new Label
        {
            Text = "EveryStage Caster", Dock = DockStyle.Left, Width = 220,
            Padding = new Padding(4, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernUi.Text, Font = new Font("Segoe UI Semibold", 10F),
        };
        // Minimize and close only: this window has a fixed layout (MaximizeBox = false), so the old
        // maximize button produced a screen-sized window of empty space.
        var close = new CaptionButton(CaptionButton.CloseGlyph, isClose: true);
        var minimize = new CaptionButton(CaptionButton.MinimizeGlyph);
        close.Click += (_, _) => _form.Close();
        minimize.Click += (_, _) => _form.WindowState = FormWindowState.Minimized;
        Controls.Add(title);
        Controls.Add(appIcon);
        // Dock=Right stacks so that the LAST-added control ends up right-most. Windows order left→
        // right is minimize, maximize, close (close at the far right), so add in that same order.
        Controls.Add(minimize);
        Controls.Add(close);
        title.MouseDown += DragWindow;
        appIcon.MouseDown += DragWindow;
        MouseDown += DragWindow;
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
    public const string MinimizeGlyph = "\uE921", CloseGlyph = "\uE8BB";

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
        var background = Parent?.BackColor ?? ModernUi.Background;
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
