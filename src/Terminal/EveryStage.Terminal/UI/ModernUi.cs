using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

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

    public static NavItem NavButton(NavIcon icon, string text, int top)
    {
        return new NavItem(icon, text) { Bounds = new Rectangle(10, top, 142, 46) };
    }

    public static void SetNavActive(IEnumerable<NavItem> buttons, NavItem active)
    {
        foreach (var button in buttons)
        {
            button.Selected = ReferenceEquals(button, active);
            button.Invalidate();
        }
    }

    public static void MakeResponsiveToolbar(FlowLayoutPanel toolbar, int minimumHeight = 44)
    {
        toolbar.MinimumSize = new Size(0, minimumHeight);
        toolbar.WrapContents = true;
        toolbar.Padding = new Padding(6, 5, 6, 5);
        toolbar.BackColor = Background;
        toolbar.Layout += (_, _) =>
        {
            if (toolbar.Controls.Count == 0) return;
            int required = toolbar.Controls.Cast<Control>().Max(control => control.Bottom + control.Margin.Bottom)
                + toolbar.Padding.Bottom;
            required = Math.Max(minimumHeight, required);
            if (toolbar.Height != required) toolbar.Height = required;
        };
    }

    public static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.DrawMode = DrawMode.OwnerDrawFixed;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.BackColor = SurfaceRaised;
        comboBox.ForeColor = Text;
        comboBox.ItemHeight = 26;
        comboBox.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(38, 72, 112) : SurfaceRaised);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, comboBox.Items[e.Index]?.ToString() ?? string.Empty,
                comboBox.Font, new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, e.Bounds.Width - 12, e.Bounds.Height),
                Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }

    public static void StyleDialog(Form dialog)
    {
        dialog.AutoScaleMode = AutoScaleMode.Dpi;
        dialog.BackColor = Background;
        dialog.ForeColor = Text;
        dialog.Font = new Font("Segoe UI", 9.5F);
        WindowsAppearance.UseDarkTitleBar(dialog);
        StyleDialogChildren(dialog, dialog.AcceptButton as Button);
    }

    private static void StyleDialogChildren(Control parent, Button? primaryButton)
    {
        foreach (Control child in parent.Controls)
        {
            child.ForeColor = child.ForeColor is var existing &&
                (existing == Color.DimGray || existing == Color.DarkRed || existing == Danger)
                    ? existing : Text;
            child.BackColor = child switch
            {
                Label or CheckBox or RadioButton => Color.Transparent,
                TextBoxBase or ListBox or ListView or TreeView or NumericUpDown => SurfaceRaised,
                Panel => Surface,
                _ => child.BackColor,
            };

            switch (child)
            {
                case Button button:
                    StyleButton(button, primary: ReferenceEquals(button, primaryButton));
                    break;
                case ComboBox comboBox when comboBox.DrawMode == DrawMode.Normal:
                    StyleComboBox(comboBox);
                    break;
                case TextBox textBox:
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListBox listBox:
                    listBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
            }
            StyleDialogChildren(child, primaryButton);
        }
    }
}

public class GradientForm : Form
{
    private Size _logicalMinimumSize;
    protected Size NormalWindowAspectRatio { get; set; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SizingRect { public int Left, Top, Right, Bottom; }

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

        var available = new Size(Math.Min(Width, maximumWidth), Math.Min(Height, maximumHeight));
        if (!NormalWindowAspectRatio.IsEmpty)
        {
            var maximum = AspectRatioSizing.Fit(new Size(maximumWidth, maximumHeight), NormalWindowAspectRatio);
            MinimumSize = new Size(Math.Min(MinimumSize.Width, maximum.Width), Math.Min(MinimumSize.Height, maximum.Height));
            Size = AspectRatioSizing.Resize(available, MinimumSize, maximum, NormalWindowAspectRatio, false);
        }
        else Size = available;
        Location = new Point(
            Math.Clamp(Left, workingArea.Left + margin, Math.Max(workingArea.Left + margin, workingArea.Right - margin - Width)),
            Math.Clamp(Top, workingArea.Top + margin, Math.Max(workingArea.Top + margin, workingArea.Bottom - margin - Height)));
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

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x214 && !NormalWindowAspectRatio.IsEmpty && WindowState == FormWindowState.Normal)
        {
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<SizingRect>(m.LParam);
            int edge = m.WParam.ToInt32();
            var area = Screen.FromControl(this).WorkingArea;
            int margin = LogicalToDeviceUnits(12);
            var size = AspectRatioSizing.Resize(new Size(rect.Right - rect.Left, rect.Bottom - rect.Top),
                MinimumSize, new Size(area.Width - margin * 2, area.Height - margin * 2),
                NormalWindowAspectRatio, edge is 3 or 6);
            if (edge is 1 or 4 or 7) rect.Left = rect.Right - size.Width;
            else rect.Right = rect.Left + size.Width;
            if (edge is 3 or 4 or 5) rect.Top = rect.Bottom - size.Height;
            else rect.Bottom = rect.Top + size.Height;
            System.Runtime.InteropServices.Marshal.StructureToPtr(rect, m.LParam, false);
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
        {
            var p = PointToClient(Cursor.Position);
            int g = LogicalToDeviceUnits(7);
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

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        FitToCurrentWorkingArea();
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
        base.OnPaintBackground(e);
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        int scaledRadius = LogicalToDeviceUnits(CornerRadius);
        using var path = Rounded(ClientRectangle, scaledRadius);
        using var fill = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(Math.Min(255, GlassTint.A + 24), GlassTint), GlassTint, 110F);
        e.Graphics.FillPath(fill, path);
        using var highlight = new Pen(Color.FromArgb(70, 185, 217, 255), 1F);
        e.Graphics.DrawPath(highlight, path);
        var inset = ClientRectangle;
        inset.Inflate(-2, -2);
        using var innerPath = Rounded(inset, Math.Max(2, scaledRadius - LogicalToDeviceUnits(2)));
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

internal sealed class AppTitleBar : Panel
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly Form _form;

    public AppTitleBar(Form form, string product)
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
            Text = $"EveryStage {product}", Dock = DockStyle.Left, Width = 220,
            Padding = new Padding(4, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ModernUi.Text, Font = new Font("Segoe UI Semibold", 10F),
        };
        var close = ChromeButton("×");
        var maximize = ChromeButton("□");
        var minimize = ChromeButton("—");
        close.Click += (_, _) => _form.Close();
        maximize.Click += (_, _) => ToggleMaximize();
        minimize.Click += (_, _) => _form.WindowState = FormWindowState.Minimized;
        close.MouseEnter += (_, _) => close.BackColor = Color.FromArgb(196, 43, 51);
        close.MouseLeave += (_, _) => close.BackColor = BackColor;

        Controls.Add(title);
        Controls.Add(appIcon);
        // Dock=Right stacks so that the LAST-added control ends up right-most. Windows order left→
        // right is minimize, maximize, close (close at the far right), so add in that same order.
        Controls.Add(minimize);
        Controls.Add(maximize);
        Controls.Add(close);
        title.MouseDown += DragWindow;
        appIcon.MouseDown += DragWindow;
        MouseDown += DragWindow;
        title.DoubleClick += (_, _) => ToggleMaximize();
        DoubleClick += (_, _) => ToggleMaximize();
    }

    private Button ChromeButton(string text)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Right, Width = 46,
            FlatStyle = FlatStyle.Flat, BackColor = BackColor, ForeColor = ModernUi.Muted,
            Font = new Font("Segoe UI Symbol", 11F), TabStop = false,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(_form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    private void ToggleMaximize() => _form.WindowState = _form.WindowState == FormWindowState.Maximized
        ? FormWindowState.Normal : FormWindowState.Maximized;
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

/// <summary>品牌标记——设计图左上角的蓝色叠层方块 logo，纯 GDI+ 绘制，不引资源。</summary>
internal sealed class BrandMark : Control
{
    public BrandMark()
    {
        // SupportsTransparentBackColor must be enabled BEFORE assigning a transparent BackColor —
        // a bare Control rejects Color.Transparent otherwise ("控件不支持透明的背景色").
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;
        float w = r.Width, h = r.Height;
        // Three slanted stacked bars, top brightest — a simple layered mark.
        Color[] shades = { Color.FromArgb(96, 165, 255), Color.FromArgb(66, 139, 255), Color.FromArgb(40, 96, 210) };
        for (int i = 0; i < 3; i++)
        {
            float y = r.Top + h * (0.12f + i * 0.28f);
            using var path = new GraphicsPath();
            path.AddLine(r.Left + w * 0.10f, y + h * 0.14f, r.Left + w * 0.55f, y);
            path.AddLine(r.Left + w * 0.55f, y, r.Left + w * 0.92f, y + h * 0.12f);
            path.AddLine(r.Left + w * 0.92f, y + h * 0.12f, r.Left + w * 0.47f, y + h * 0.26f);
            path.CloseFigure();
            using var brush = new SolidBrush(shades[i]);
            g.FillPath(brush, path);
        }
    }
}

internal enum NavIcon { Files, Activities, Devices, Settings }

/// <summary>PLANNING.md §8.1 的左侧导航项——设计图里是「圆角图标 + 文字」的一整块，选中态是一个
/// 圆角高亮块（非之前的字形按钮）。图标用 GDI+ 矢量绘制，不引入任何图标字体/图片资源依赖。
///
/// 继承自 <see cref="Control"/> 而非 <see cref="Button"/>：<see cref="Button"/> 会自绘一遍自己的
/// <c>Text</c>，叠在这里 <see cref="OnPaint"/> 画的文字上形成重影（曾出现「篓佛」这类叠字乱码）。
/// 裸 <see cref="Control"/> 完全由本类接管绘制，杜绝重复绘制，同时仍抛出 <see cref="Control.Click"/>。</summary>
internal sealed class NavItem : Control
{
    private readonly NavIcon _icon;
    private readonly string _label;
    private bool _hover;
    public bool Selected { get; set; }

    public NavItem(NavIcon icon, string text)
    {
        _icon = icon;
        _label = text;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 11.5F);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Inflate(-2, -3);

        if (Selected || _hover)
        {
            using var path = RoundedRect(rect, LogicalToDeviceUnits(12));
            using var fill = new SolidBrush(Selected ? Color.FromArgb(60, 66, 139, 255) : Color.FromArgb(28, 120, 160, 220));
            g.FillPath(fill, path);
            if (Selected)
            {
                using var border = new Pen(Color.FromArgb(120, 66, 139, 255));
                g.DrawPath(border, path);
            }
        }

        Color fg = Selected ? Color.White : ModernUi.Muted;
        int iconSize = LogicalToDeviceUnits(20);
        int iconLeft = rect.Left + LogicalToDeviceUnits(14);
        var iconRect = new Rectangle(iconLeft, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
        VectorIcons.Draw(g, _icon, iconRect, fg);

        var textRect = new Rectangle(iconRect.Right + LogicalToDeviceUnits(12), rect.Top,
            rect.Right - iconRect.Right - LogicalToDeviceUnits(12), rect.Height);
        TextRenderer.DrawText(g, _label, Font, textRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>共用的矢量图标绘制（导航 / 文件类型角标 / Caster 终端卡片都会用到），纯 GDI+ 描边，
/// 不引入图标字体或图片资源——符合"复用平台原生能力、不加依赖"的原则。</summary>
internal static class VectorIcons
{
    public static void Draw(Graphics g, NavIcon icon, Rectangle r, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.6f, r.Width / 11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);
        switch (icon)
        {
            case NavIcon.Files: DrawFolder(g, r, pen); break;
            case NavIcon.Activities: DrawCalendar(g, r, pen); break;
            case NavIcon.Devices: DrawMonitor(g, r, pen); break;
            case NavIcon.Settings: DrawGear(g, r, pen, brush); break;
        }
    }

    private static void DrawFolder(Graphics g, Rectangle r, Pen pen)
    {
        float x = r.Left, y = r.Top, w = r.Width, h = r.Height;
        using var path = new GraphicsPath();
        path.AddLine(x + w * 0.06f, y + h * 0.28f, x + w * 0.40f, y + h * 0.28f);
        path.AddLine(x + w * 0.40f, y + h * 0.28f, x + w * 0.50f, y + h * 0.42f);
        path.AddLine(x + w * 0.50f, y + h * 0.42f, x + w * 0.94f, y + h * 0.42f);
        path.AddLine(x + w * 0.94f, y + h * 0.42f, x + w * 0.94f, y + h * 0.86f);
        path.AddLine(x + w * 0.94f, y + h * 0.86f, x + w * 0.06f, y + h * 0.86f);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    private static void DrawCalendar(Graphics g, Rectangle r, Pen pen)
    {
        var body = new RectangleF(r.Left + r.Width * 0.08f, r.Top + r.Height * 0.16f, r.Width * 0.84f, r.Height * 0.74f);
        g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
        g.DrawLine(pen, body.Left, body.Top + body.Height * 0.28f, body.Right, body.Top + body.Height * 0.28f);
        g.DrawLine(pen, r.Left + r.Width * 0.30f, r.Top + r.Height * 0.06f, r.Left + r.Width * 0.30f, body.Top + 2);
        g.DrawLine(pen, r.Left + r.Width * 0.70f, r.Top + r.Height * 0.06f, r.Left + r.Width * 0.70f, body.Top + 2);
    }

    private static void DrawMonitor(Graphics g, Rectangle r, Pen pen)
    {
        var screen = new RectangleF(r.Left + r.Width * 0.06f, r.Top + r.Height * 0.14f, r.Width * 0.88f, r.Height * 0.56f);
        g.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
        g.DrawLine(pen, r.Left + r.Width * 0.5f, screen.Bottom, r.Left + r.Width * 0.5f, r.Top + r.Height * 0.86f);
        g.DrawLine(pen, r.Left + r.Width * 0.30f, r.Top + r.Height * 0.90f, r.Left + r.Width * 0.70f, r.Top + r.Height * 0.90f);
    }

    private static void DrawGear(Graphics g, Rectangle r, Pen pen, SolidBrush brush)
    {
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        float outer = r.Width * 0.42f, inner = r.Width * 0.18f;
        using var path = new GraphicsPath();
        for (int i = 0; i < 8; i++)
        {
            double a1 = i * Math.PI / 4 + Math.PI / 16;
            double a2 = i * Math.PI / 4 + Math.PI / 8 - Math.PI / 16;
            double a3 = i * Math.PI / 4 + Math.PI / 8 + Math.PI / 16;
            float tooth = r.Width * 0.50f;
            path.AddLine((float)(cx + outer * Math.Cos(i * Math.PI / 4 - Math.PI / 16)), (float)(cy + outer * Math.Sin(i * Math.PI / 4 - Math.PI / 16)),
                         (float)(cx + tooth * Math.Cos(a1)), (float)(cy + tooth * Math.Sin(a1)));
            path.AddLine((float)(cx + tooth * Math.Cos(a1)), (float)(cy + tooth * Math.Sin(a1)),
                         (float)(cx + tooth * Math.Cos(a2)), (float)(cy + tooth * Math.Sin(a2)));
            path.AddLine((float)(cx + tooth * Math.Cos(a2)), (float)(cy + tooth * Math.Sin(a2)),
                         (float)(cx + outer * Math.Cos(a3)), (float)(cy + outer * Math.Sin(a3)));
        }
        path.CloseFigure();
        g.DrawPath(pen, path);
        g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2, inner * 2);
    }
}
