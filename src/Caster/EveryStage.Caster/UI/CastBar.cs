using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

internal enum CastHealth { Connecting, Good, Congested, Waiting, Failed }

/// <summary>Everything the bar shows, refreshed twice a second by MainForm. Screen/Latency/Sound/
/// Bitrate/Confirmed are the always-visible metrics row (C-03); Warning is the one backpressure
/// line, non-empty only while a drop counter is non-zero or the terminal has gone silent
/// (OP-14: the silence prompt names the 停止 action). Failed is the red state: the terminal
/// has not reported for over 5 s and the cast may not be reaching it at all.</summary>
internal sealed record CastBarState(
    string Elapsed, CastHealth Health, string HealthText,
    string Screen, string Latency, string Sound, bool SoftwareEncoding, string Details,
    string Bitrate, string Confirmed, string Warning);

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
    private const int MetricsHeight = 46, WarningHeight = 22, DisclosureHeight = 20;

    private readonly string _terminalName;
    private readonly BarButton _expand = new() { Glyph = ButtonGlyph.ChevronDown, AccessibleName = "展开详情" };
    private readonly BarButton _stop = new() { Text = "停止", Glyph = ButtonGlyph.Stop, Danger = true, AccessibleName = "停止投屏" };
    private readonly Panel _detailsPanel = new() { Visible = false };
    private readonly Panel _metricsPanel = new();
    private readonly ThemedLabel _latencyTitle = Small("延迟"), _resolutionTitle = Small("分辨率 · 码率"), _confirmedTitle = Small("终端确认"), _soundTitle = Small("声音");
    private readonly ThemedLabel _latency = Value(), _resolution = Value(), _confirmed = Value(), _sound = Value();
    private readonly ThemedLabel _warning = new ThemedLabel { Font = new Font(ModernUi.UiFont, 9F), Visible = false }.With(TextRole.Warning);
    // DEV-08: the auth≠encryption line, always visible while casting — the short form of the
    // standby page's wording, so the operator never has to unfold the details to see it.
    private readonly ThemedLabel _disclosure = new ThemedLabel
    {
        Font = new Font(ModernUi.UiFont, 8.5F),
        Text = "画面经设备认证但不加密，请只在受信任的局域网中使用。",
    }.With(TextRole.Muted);
    private readonly ThemedLabel _software = new ThemedLabel { Font = new Font(ModernUi.UiFont, 9F), Visible = false, Text = "这台电脑没有显卡编码器，正在用软件编码，CPU 占用会高一些。" }.With(TextRole.Warning);
    private readonly TextBox _details = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Font = new Font(ModernUi.UiFont, 8.5F) };
    private readonly ThemedLabel _note = new ThemedLabel { Font = new Font(ModernUi.UiFont, 8.5F) }.With(TextRole.Muted);
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
        ClientSize = new Size(BarWidth, BarHeight + MetricsHeight);

        _metricsPanel.Controls.AddRange(new Control[] { _latencyTitle, _resolutionTitle, _confirmedTitle, _soundTitle, _latency, _resolution, _confirmed, _sound, _warning, _disclosure });
        _detailsPanel.Controls.AddRange(new Control[] { _software, _details, _note });
        Controls.AddRange(new Control[] { _expand, _stop, _metricsPanel, _detailsPanel });
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
        // Pessimistic until MainForm places the bar and reports whether it sits on the captured
        // screen (DEV-08): without exclusion it would appear in the cast picture.
        UpdateCaptureNote(onCapturedScreen: true);
    }

    /// <summary>The details-panel note about where the bar may appear: exclusion working means
    /// never; otherwise it depends on whether the bar sits on the captured screen. Called after
    /// placement (and again if the bar is moved off the captured screen).</summary>
    public void UpdateCaptureNote(bool onCapturedScreen)
    {
        _note.Text = ExcludedFromCapture
            ? "可以拖到屏幕任何位置。这个小条不会出现在大屏上。"
            : onCapturedScreen
                ? "可以拖到屏幕任何位置。这台电脑的 Windows 版本较旧，这个小条会出现在大屏画面里。"
                : "可以拖到屏幕任何位置。这个小条在未投送的屏幕上，不会出现在大屏上。";
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
        _metricsPanel.BackColor = p.BarBackground;
        _detailsPanel.BackColor = p.Surface;
        _details.BackColor = p.Surface;
        _details.ForeColor = p.Muted;
        ModernUi.ApplyTheme(_metricsPanel);
        ModernUi.ApplyTheme(_detailsPanel);
        _detailsPanel.BackColor = p.Surface;
        Invalidate(true);
    }

    /// <summary>Collapsed height: title row + the always-visible metrics row (+ the warning line
    /// while a drop counter is non-zero) + the persistent auth≠encryption line (DEV-08).</summary>
    private int MetricsPanelHeight => LogicalToDeviceUnits(MetricsHeight + (_warning.Visible ? WarningHeight : 0) + DisclosureHeight);

    private void UpdateBarSize()
    {
        ClientSize = new Size(LogicalToDeviceUnits(BarWidth),
            LogicalToDeviceUnits(BarHeight) + MetricsPanelHeight + (_detailsPanel.Visible ? LogicalToDeviceUnits(DetailsHeight) : 0));
        Invalidate();
    }

    public void UpdateState(CastBarState state)
    {
        bool textChanged = _state == null || _state.Elapsed != state.Elapsed || _state.HealthText != state.HealthText || _state.Health != state.Health;
        _state = state;
        if (textChanged) Invalidate(new Rectangle(0, 0, Width, LogicalToDeviceUnits(BarHeight)));
        _latency.Text = state.Latency;
        _resolution.Text = string.IsNullOrEmpty(state.Bitrate) ? state.Screen : $"{state.Screen} · {state.Bitrate}";
        _confirmed.Text = state.Confirmed;
        _sound.Text = state.Sound;
        if (_warning.Text != state.Warning) _warning.Text = state.Warning;
        bool showWarning = state.Warning.Length > 0;
        if (_warning.Visible != showWarning)
        {
            _warning.Visible = showWarning;
            UpdateBarSize();
            PerformLayout();
        }
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
        UpdateBarSize();
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
        int metricsH = MetricsPanelHeight;
        _metricsPanel.Bounds = new Rectangle(0, bar, ClientSize.Width, metricsH);
        _detailsPanel.Bounds = new Rectangle(0, bar + metricsH, ClientSize.Width, Math.Max(0, ClientSize.Height - bar - metricsH));
        // The metrics row: four equal cells, small Muted labels over semibold values.
        int pad = s(16), gap = s(8);
        int col = (ClientSize.Width - pad * 2 + gap) / 4;
        var titles = new[] { _latencyTitle, _resolutionTitle, _confirmedTitle, _soundTitle };
        var values = new[] { _latency, _resolution, _confirmed, _sound };
        for (int i = 0; i < 4; i++)
        {
            int x = pad + i * col;
            titles[i].Bounds = new Rectangle(x, s(6), col - gap, s(16));
            values[i].Bounds = new Rectangle(x, s(24), col - gap, s(20));
        }
        if (_warning.Visible) _warning.Bounds = new Rectangle(pad, s(46), ClientSize.Width - pad * 2, s(20));
        _disclosure.Bounds = new Rectangle(pad, s(46) + (_warning.Visible ? s(WarningHeight) : 0), ClientSize.Width - pad * 2, s(18));
        int w = _detailsPanel.Width - s(32), y = s(12);
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
        // The dot reports health, not a live/recording indicator: it must agree with the pill
        // below (green = good, amber = degraded/connecting, red = the terminal is not responding).
        Color dot = health switch
        {
            CastHealth.Good => p.Success,
            CastHealth.Failed => p.Danger,
            _ => p.Warning,
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
            CastHealth.Good => (p.SuccessSurface, p.Success),
            CastHealth.Failed => (p.DangerSurface, p.DangerText),
            _ => (p.WarningSurface, p.WarningText),
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
            Color fill = Danger ? (_hot ? p.DangerHover : p.Danger) : (_hot ? p.SurfaceHover : p.BarBackground);
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
