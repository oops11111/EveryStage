using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

internal static class ModernUi
{
    private static UiPalette _palette = UiPalette.For(EveryStage.Terminal.Data.AppTheme.Dark);
    private static EveryStage.Terminal.Data.AppTheme _currentTheme = EveryStage.Terminal.Data.AppTheme.Dark;
    internal static UiPalette Palette => _palette;
    internal static EveryStage.Terminal.Data.AppTheme CurrentTheme => _currentTheme;
    public static Color Background => _palette.Background;
    public static Color Rail => _palette.Rail;
    public static Color Surface => _palette.Surface;
    public static Color SurfaceRaised => _palette.SurfaceRaised;
    public static Color Border => _palette.Border;
    public static Color Text => _palette.Text;
    public static Color Muted => _palette.Muted;
    public static Color Accent => _palette.Accent;
    public static Color Danger => _palette.Danger;
    public static Color Warning => _palette.Warning;
    public static Color Success => _palette.Success;

    internal static void SetPalette(UiPalette palette, EveryStage.Terminal.Data.AppTheme theme)
    {
        _palette = palette;
        _currentTheme = theme;
    }

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
        ApplyRoundedButtonRegion(button);
        button.SizeChanged -= RefreshRoundedButtonRegion;
        button.SizeChanged += RefreshRoundedButtonRegion;
    }

    private static void RefreshRoundedButtonRegion(object? sender, EventArgs e)
    {
        if (sender is Control control) ApplyRoundedButtonRegion(control);
    }

    private static void ApplyRoundedButtonRegion(Control control)
    {
        if (control.Width < 4 || control.Height < 4) return;
        var bounds = new Rectangle(0, 0, control.Width, control.Height);
        bounds.Inflate(-1, -1);
        int diameter = Math.Min(16, bounds.Height);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var previous = control.Region;
        control.Region = new Region(path);
        previous?.Dispose();
    }

    public static NavItem NavButton(NavIcon icon, string text, int top)
    {
        // 侧栏加宽到 250px 后导航项整条填满（16px 内边距，218px 宽），选中态实心蓝对齐效果图。
        return new NavItem(icon, text) { Bounds = new Rectangle(16, top, 218, 46) };
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
            using var background = new SolidBrush(selected ? Color.FromArgb(64, Accent.R, Accent.G, Accent.B) : SurfaceRaised);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, comboBox.GetItemText(comboBox.Items[e.Index]),
                comboBox.Font, new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, e.Bounds.Width - 12, e.Bounds.Height),
                Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
        // The drop-down list above is owner-drawn, but the closed box is still WinForms' Flat frame,
        // which ignores BackColor for its border and button face (white outlines on the dark themes).
        // ComboChrome repaints the closed box; the rounded region matches StyleButton's buttons.
        ComboChrome.Attach(comboBox);
        ApplyRoundedButtonRegion(comboBox);
        comboBox.SizeChanged -= RefreshRoundedButtonRegion;
        comboBox.SizeChanged += RefreshRoundedButtonRegion;
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

    /// <summary>
    /// Repaints the closed box of a <see cref="StyleComboBox"/> combo (fill, current item, chevron,
    /// border) right after WinForms' own WM_PAINT, in the current palette. Hooked as a
    /// <see cref="NativeWindow"/> rather than a ComboBox subclass so every existing
    /// <c>new ComboBox</c> + <see cref="StyleComboBox"/> call site — panels and dialogs alike — picks it
    /// up unchanged. Only DropDownList combos are styled this way (all of this app's are); an editable
    /// combo's text box child would paint over this. Colours are read at paint time, so theme
    /// switches need no re-attach.
    /// </summary>
    private sealed class ComboChrome : NativeWindow
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_PRINT = 0x0317;
        private const int WM_PRINTCLIENT = 0x0318;
        private readonly ComboBox _combo;
        private bool _hot;

        private ComboChrome(ComboBox combo)
        {
            _combo = combo;
            if (combo.IsHandleCreated) AssignHandle(combo.Handle);
            combo.HandleCreated += (_, _) => AssignHandle(combo.Handle);
            combo.HandleDestroyed += (_, _) => ReleaseHandle();
            combo.MouseEnter += (_, _) => { _hot = true; combo.Invalidate(); };
            combo.MouseLeave += (_, _) => { _hot = false; combo.Invalidate(); };
            combo.GotFocus += (_, _) => combo.Invalidate();
            combo.LostFocus += (_, _) => combo.Invalidate();
            combo.DropDownClosed += (_, _) => combo.Invalidate();
            combo.EnabledChanged += (_, _) => combo.Invalidate();
            combo.SelectedIndexChanged += (_, _) => combo.Invalidate();
        }

        public static void Attach(ComboBox combo)
        {
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList) _ = new ComboChrome(combo);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (!_combo.IsHandleCreated || _combo.IsDisposed) return;
            if (m.Msg == WM_PAINT)
            {
                using var g = Graphics.FromHwnd(_combo.Handle);
                Paint(g);
            }
            else if (m.Msg is WM_PRINT or WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
            {
                // DrawToBitmap/PrintWindow go through WM_PRINT, not WM_PAINT — paint the same chrome there.
                using var g = Graphics.FromHdc(m.WParam);
                Paint(g);
            }
        }

        private void Paint(Graphics g)
        {
            int w = _combo.ClientSize.Width, h = _combo.ClientSize.Height;
            if (w < 8 || h < 8) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool enabled = _combo.Enabled;
            bool active = enabled && (_combo.Focused || _combo.DroppedDown);

            // Same geometry as ApplyRoundedButtonRegion's clip, one pixel further in for the border.
            var box = new Rectangle(1, 1, w - 3, h - 3);
            int diameter = Math.Min(16, box.Height);
            using var path = new GraphicsPath();
            path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            Color fill = enabled && _hot ? Blend(SurfaceRaised, Text, 0.05f) : SurfaceRaised;
            // Fill only the region-shaped area, never Clear(): under WM_PRINT the DC is the whole target
            // bitmap, not this control's clipped window.
            using (var brush = new SolidBrush(fill))
            {
                g.FillRectangle(brush, 1, 1, w - 2, h - 2);
            }
            using (var pen = new Pen(active ? Accent : enabled && _hot ? Blend(Border, Text, 0.25f) : Border, 1F))
                g.DrawPath(pen, path);

            int button = Math.Min(h, 28);
            var textRect = Rectangle.FromLTRB(10, 0, w - button - 2, h);
            TextRenderer.DrawText(g, _combo.GetItemText(_combo.SelectedItem), _combo.Font, textRect,
                enabled ? Text : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            float cx = w - button / 2f - 2, cy = h / 2f;
            using var chevron = new Pen(enabled ? Muted : Border, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(chevron, new[] { new PointF(cx - 4.5f, cy - 2f), new PointF(cx, cy + 2.5f), new PointF(cx + 4.5f, cy - 2f) });
        }

        private static Color Blend(Color baseColor, Color overlay, float amount) => Color.FromArgb(
            (int)(baseColor.R + (overlay.R - baseColor.R) * amount),
            (int)(baseColor.G + (overlay.G - baseColor.G) * amount),
            (int)(baseColor.B + (overlay.B - baseColor.B) * amount));
    }
}

public class GradientForm : Form
{
    private Size _logicalMinimumSize;
    protected Size NormalWindowAspectRatio { get; set; }
    internal bool IsMicaBackdropActive { get; set; }

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
        if (IsMicaBackdropActive) return;
        using var background = new LinearGradientBrush(ClientRectangle,
            ModernUi.Background, ModernUi.Surface, 32F);
        e.Graphics.FillRectangle(background, ClientRectangle);
        var accent = ModernUi.Accent;
        using var glow = new SolidBrush(Color.FromArgb(22, accent.R, accent.G, accent.B));
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
        using var highlight = new Pen(Color.FromArgb(70, ModernUi.Accent.R, ModernUi.Accent.G, ModernUi.Accent.B), 1F);
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

internal sealed class PillButton : Control
{
    public bool Selected { get; set; }

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Height = 38;
        Padding = new Padding(16, 0, 16, 0);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(Text, Font);
        return new Size(Math.Max(42, textSize.Width + Padding.Horizontal),
            Math.Max(Height, textSize.Height + Padding.Vertical));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        rect.Inflate(-1, -1);
        using var path = Rounded(rect, rect.Height / 2);
        using var fill = new SolidBrush(Selected ? ModernUi.Accent
            : !Enabled ? ModernUi.Surface : ModernUi.SurfaceRaised);
        using var border = new Pen(Selected ? Color.FromArgb(150, ModernUi.Accent.R, ModernUi.Accent.G, ModernUi.Accent.B)
            : ModernUi.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? ModernUi.Text : ModernUi.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -6, -6));
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
        Height = 32;
        BackColor = ModernUi.Rail;

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
        // Dock=Right stacks so that the LAST-added control ends up right-most. Windows order left→
        // right is minimize, maximize, close (close at the far right), so add in that same order.
        Controls.Add(minimize);
        Controls.Add(maximize);
        Controls.Add(close);
        title.MouseDown += DragWindow;
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

internal sealed class RoundedActionButton : Button
{
    public RoundedActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        using var path = Rounded(bounds, Math.Min(8, bounds.Height / 2));
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
        if (FlatAppearance.BorderSize > 0)
        {
            using var border = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize);
            e.Graphics.DrawPath(border, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds,
            Enabled ? ForeColor : ModernUi.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5));
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
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

internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Size = new Size(52, 28);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = ClientRectangle;
        track.Inflate(-1, -3);
        using var path = Rounded(track, track.Height / 2);
        using var trackBrush = new SolidBrush(Checked ? ModernUi.Accent : ModernUi.Border);
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
    private readonly Image _image;

    public BrandMark()
    {
        // SupportsTransparentBackColor must be enabled BEFORE assigning a transparent BackColor —
        // a bare Control rejects Color.Transparent otherwise ("控件不支持透明的背景色").
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        _image = ProductIcon.LoadBitmap();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_image, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image.Dispose();
        base.Dispose(disposing);
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

        if (Selected)
        {
            // 对齐效果图：选中项为实心品牌蓝整条 + 白字（不再是半透明浅色块）。
            using var path = RoundedRect(rect, LogicalToDeviceUnits(12));
            using var fill = new SolidBrush(ModernUi.Accent);
            g.FillPath(fill, path);
        }
        else if (_hover)
        {
            using var path = RoundedRect(rect, LogicalToDeviceUnits(12));
            using var fill = new SolidBrush(Color.FromArgb(28, ModernUi.Accent.R, ModernUi.Accent.G, ModernUi.Accent.B));
            g.FillPath(fill, path);
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
