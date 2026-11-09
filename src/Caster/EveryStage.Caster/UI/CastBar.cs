using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

internal enum CastHealth { Connecting, Good, Congested, Waiting }

/// <summary>Everything the bar shows, refreshed twice a second by MainForm.</summary>
internal sealed record CastBarState(
    string Elapsed, CastHealth Health, string HealthText,
    string Screen, string Latency, string Sound, bool SoftwareEncoding, string Details);

/// <summary>
/// While casting, the main window steps aside and only this bar stays on screen: which terminal, how
/// long, whether the picture is getting through, and 停止. ⌄ unfolds the details. The bar can be
/// dragged anywhere; on Windows 10 2004 and later it asks Windows to leave it out of screen capture
/// (WDA_EXCLUDEFROMCAPTURE), so it does not appear on the big screen itself.
/// </summary>
internal sealed class CastBar : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const uint WdaExcludeFromCapture = 0x11;

    private const int BarHeight = 48, BarWidth = 560, DetailsHeight = 236;

    private readonly string _terminalName;
    private readonly BarButton _expand = new() { Glyph = ButtonGlyph.ChevronDown, AccessibleName = "展开详情" };
    private readonly BarButton _stop = new() { Text = "停止", Glyph = ButtonGlyph.Stop, Danger = true, AccessibleName = "停止投屏" };
    private readonly Panel _detailsPanel = new() { Visible = false };
    private readonly ThemedLabel _screenTitle = Small("屏幕"), _latencyTitle = Small("延迟"), _soundTitle = Small("声音");
    private readonly ThemedLabel _screen = Value(), _latency = Value(), _sound = Value();
    private readonly ThemedLabel _software = new ThemedLabel { Font = new Font(ModernUi.UiFont, 9F), Visible = false, Text = "这台电脑没有显卡编码器，正在用软件编码，CPU 占用会高一些。" }.With(TextRole.Warning);
    private readonly TextBox _details = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Font = new Font(ModernUi.UiFont, 8.5F) };
    private readonly ThemedLabel _note = new ThemedLabel { Font = new Font(ModernUi.UiFont, 8.5F) }.With(TextRole.Faint);
    private CastBarState? _state;
    private bool _stopping;

    public event Action? StopRequested;

    /// <summary>Whether Windows agreed to keep the bar out of the cast picture.</summary>
    public bool ExcludedFromCapture { get; private set; }

    public CastBar(string terminalName)
    {
        _terminalName = terminalName;
        Text = $"正在投到 {terminalName}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(ModernUi.UiFont, 10F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(BarWidth, BarHeight);

        _detailsPanel.Controls.AddRange(new Control[] { _screenTitle, _latencyTitle, _soundTitle, _screen, _latency, _sound, _software, _details, _note });
        Controls.AddRange(new Control[] { _expand, _stop, _detailsPanel });
        _expand.Click += (_, _) => SetExpanded(!_detailsPanel.Visible);
        _stop.Click += (_, _) => RequestStop();
        MouseDown += Drag;
        ApplyTheme();
    }

    private static ThemedLabel Small(string text) => new ThemedLabel { Text = text, Font = new Font(ModernUi.UiFont, 9F) }.With(TextRole.Muted);
    private static ThemedLabel Value() => new ThemedLabel { Font = new Font(ModernUi.UiFontSemibold, 10F), AutoEllipsis = true }.With(TextRole.Text);

    // The bar is a status display: showing it must not pull focus away from the presentation.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { ExcludedFromCapture = SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture); }
        catch (EntryPointNotFoundException) { ExcludedFromCapture = false; }
        _note.Text = ExcludedFromCapture
            ? "可以拖到屏幕任何位置。这个小条不会出现在大屏上。"
            : "可以拖到屏幕任何位置。这台电脑的 Windows 版本较旧，这个小条会出现在大屏画面里。";
    }

    /// <summary>Top-centre of the screen the operator was working on.</summary>
    public void PlaceOn(Screen screen)
    {
        var area = screen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + LogicalToDeviceUnits(8));
    }

    public void ApplyTheme()
    {
        var p = ModernUi.Palette;
        BackColor = p.BarBackground;
        _detailsPanel.BackColor = p.Surface;
        _details.BackColor = p.Surface;
        _details.ForeColor = p.Muted;
        ModernUi.ApplyTheme(_detailsPanel);
        _detailsPanel.BackColor = p.Surface;
        Invalidate(true);
    }

    public void UpdateState(CastBarState state)
    {
        bool textChanged = _state == null || _state.Elapsed != state.Elapsed || _state.HealthText != state.HealthText || _state.Health != state.Health;
        _state = state;
        if (textChanged) Invalidate(new Rectangle(0, 0, Width, LogicalToDeviceUnits(BarHeight)));
        _screen.Text = state.Screen;
        _latency.Text = state.Latency;
        _sound.Text = state.Sound;
        if (_software.Visible != state.SoftwareEncoding)
        {
            _software.Visible = state.SoftwareEncoding;
            PerformLayout();
        }
        if (_detailsPanel.Visible && _details.Text != state.Details)
        {
            int keep = _details.SelectionStart;
            _details.Text = state.Details;
            _details.SelectionStart = Math.Min(keep, _details.TextLength);
        }
    }

    /// <summary>CI screenshots only.</summary>
    internal void ExpandForScreenshot() => SetExpanded(true);

    private void SetExpanded(bool expanded)
    {
        _detailsPanel.Visible = expanded;
        _expand.Glyph = expanded ? ButtonGlyph.ChevronUp : ButtonGlyph.ChevronDown;
        _expand.AccessibleName = expanded ? "收起详情" : "展开详情";
        if (expanded && _state != null) _details.Text = _state.Details;
        ClientSize = new Size(LogicalToDeviceUnits(BarWidth), LogicalToDeviceUnits(expanded ? BarHeight + DetailsHeight : BarHeight));
        Invalidate();
    }

    private void RequestStop()
    {
        if (_stopping) return;
        _stopping = true;
        StopRequested?.Invoke();
    }

    /// <summary>Closing the bar any other way (Alt+F4, the task bar) also ends the cast — a cast with no
    /// visible way to stop it would be worse.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_stopping && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            RequestStop();
            return;
        }
        base.OnFormClosing(e);
    }

    private void Drag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = ModernUi.Rounded(new RectangleF(0, 0, Width, Height), LogicalToDeviceUnits(_detailsPanel.Visible ? 14 : BarHeight / 2));
        Region = new Region(path);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int s(int v) => LogicalToDeviceUnits(v);
        int bar = s(BarHeight);
        _stop.Bounds = new Rectangle(ClientSize.Width - s(8) - s(80), (bar - s(34)) / 2, s(80), s(34));
        _expand.Bounds = new Rectangle(_stop.Left - s(6) - s(34), (bar - s(34)) / 2, s(34), s(34));
        _detailsPanel.Bounds = new Rectangle(0, bar, ClientSize.Width, Math.Max(0, ClientSize.Height - bar));
        int w = _detailsPanel.Width - s(32), col = w / 3, y = s(12);
        _screenTitle.Bounds = new Rectangle(s(16), y, col, s(18));
        _latencyTitle.Bounds = new Rectangle(s(16) + col, y, col, s(18));
        _soundTitle.Bounds = new Rectangle(s(16) + col * 2, y, col, s(18));
        _screen.Bounds = new Rectangle(s(16), y + s(18), col - s(8), s(22));
        _latency.Bounds = new Rectangle(s(16) + col, y + s(18), col - s(8), s(22));
        _sound.Bounds = new Rectangle(s(16) + col * 2, y + s(18), col - s(8), s(22));
        y += s(48);
        if (_software.Visible)
        {
            _software.Bounds = new Rectangle(s(16), y, w, s(20));
            y += s(24);
        }
        _note.Bounds = new Rectangle(s(16), _detailsPanel.Height - s(26), w, s(20));
        _details.Bounds = new Rectangle(s(16), y, w, Math.Max(s(20), _note.Top - y - s(6)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = ModernUi.Palette;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int s(int v) => LogicalToDeviceUnits(v);
        int bar = s(BarHeight);
        using (var back = new SolidBrush(p.BarBackground)) g.FillRectangle(back, 0, 0, Width, bar);

        var state = _state;
        var health = state?.Health ?? CastHealth.Connecting;
        Color dot = health switch
        {
            CastHealth.Good => Color.FromArgb(255, 77, 85),
            CastHealth.Congested => Color.FromArgb(255, 198, 92),
            _ => Color.FromArgb(255, 198, 92),
        };
        int x = s(18);
        using (var dotBrush = new SolidBrush(dot)) g.FillEllipse(dotBrush, x, (bar - s(10)) / 2, s(10), s(10));
        x += s(20);

        using var nameFont = new Font(ModernUi.UiFontSemibold, 10.5F);
        using var timeFont = new Font(ModernUi.UiFontSemibold, 10.5F);
        using var pillFont = new Font(ModernUi.UiFont, 9F);
        int limit = _expand.Left - s(10);
        string title = health == CastHealth.Connecting ? $"正在连接 {_terminalName}" : $"正在投到 {_terminalName}";
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        string pill = state?.HealthText ?? "连接中";
        int pillW = TextRenderer.MeasureText(pill, pillFont).Width + s(16);
        string elapsed = state?.Elapsed ?? "00:00";
        int timeW = TextRenderer.MeasureText(elapsed, timeFont).Width + s(4);
        int titleW = Math.Max(s(40), Math.Min(TextRenderer.MeasureText(title, nameFont).Width + s(4), limit - x - timeW - pillW - s(24)));
        TextRenderer.DrawText(g, title, nameFont, new Rectangle(x, 0, titleW, bar), p.BarText, flags);
        x += titleW + s(12);
        TextRenderer.DrawText(g, elapsed, timeFont, new Rectangle(x, 0, timeW, bar), p.BarMuted, flags);
        x += timeW + s(10);

        (Color pillBack, Color pillInk) = health switch
        {
            CastHealth.Good => (Color.FromArgb(31, 74, 51), Color.FromArgb(126, 226, 168)),
            CastHealth.Congested => (Color.FromArgb(74, 58, 20), Color.FromArgb(255, 210, 120)),
            _ => (Color.FromArgb(58, 52, 30), Color.FromArgb(255, 210, 120)),
        };
        var pillRect = new Rectangle(x, (bar - s(22)) / 2, Math.Min(pillW, Math.Max(0, limit - x)), s(22));
        if (pillRect.Width > s(20))
        {
            using var pillPath = ModernUi.Rounded(pillRect, s(11));
            using (var pillBrush = new SolidBrush(pillBack)) g.FillPath(pillBrush, pillPath);
            TextRenderer.DrawText(g, pill, pillFont, pillRect, pillInk, TextFormatFlags.HorizontalCenter | flags);
        }
    }

    /// <summary>The bar's own small buttons: light glyphs on the dark bar in both themes.</summary>
    private sealed class BarButton : Button
    {
        public ButtonGlyph Glyph { get; set; }
        public bool Danger { get; set; }
        private bool _hot;

        public BarButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = new Font(ModernUi.UiFontSemibold, 9.5F);
            UseMnemonic = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ModernUi.Palette;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(p.BarBackground)) g.FillRectangle(back, ClientRectangle);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var path = ModernUi.Rounded(rect, Height / 2f);
            Color fill = Danger ? (_hot ? p.DangerHover : p.Danger) : (_hot ? Color.FromArgb(52, 60, 72) : p.BarBackground);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(Color.White, 1.5f);
                g.DrawPath(focus, path);
            }
            int icon = LogicalToDeviceUnits(Text.Length == 0 ? 16 : 12);
            int textW = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            int gap = Text.Length == 0 ? 0 : LogicalToDeviceUnits(6);
            int left = (Width - icon - gap - textW) / 2;
            Glyphs.Draw(g, Glyph, new Rectangle(left, (Height - icon) / 2, icon, icon), Color.White);
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(left + icon + gap, 0, textW + 2, Height), Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}
