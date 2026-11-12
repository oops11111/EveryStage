using System.Diagnostics;
using System.Drawing.Drawing2D;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 演讲者视图 for the presentation running on the big screen. One fixed grid, everything aligned to it:
///
///   ┌ 已用时间 00:12:34 [暂停][归零] ───── 第 6 / 18 页 · 动画 1/3 ───── 现在 14:32 ┐
///   │ ● 大屏正在显示                        │ 下一页 · 第 7 页                    │
///   │ ┌──────────────────────────────────┐ │ ┌─────────────────────────────┐    │
///   │ │   current slide (live copy of     │ │ │        next slide           │    │
///   │ │   the big screen, red frame)      │ │ └─────────────────────────────┘    │
///   │ │                                   │ │ 备注                     [A−][A+]  │
///   │ └──────────────────────────────────┘ │ ┌ scrolling notes ────────────┐    │
///   └ ← → 翻页 · 空格 下一步 ──── [◀ 上一步] [下一步 ▶] ──────────── [⤢ 全屏] ┘
///
/// Dark and fixed (it is read in a dim hall); resizable and maximizable like a presentation program's own
/// presenter view; follows whichever show is on air and says so when there is none.
/// </summary>
internal sealed class PresenterWindow : Form
{
    // Fixed palette: this window is not themed — it is read from a distance in a dark room.
    private static readonly Color Back = Color.FromArgb(11, 16, 23);
    private static readonly Color Card = Color.FromArgb(20, 28, 38);
    private static readonly Color Line = Color.FromArgb(34, 48, 63);
    private static readonly Color Ink = Color.FromArgb(236, 241, 247);
    private static readonly Color Muted = Color.FromArgb(143, 163, 184);
    private static readonly Color Live = Color.FromArgb(229, 72, 77);
    private static readonly Color Accent = Color.FromArgb(47, 124, 246);

    private const int Pad = 20, Gap = 16, HeaderHeight = 72, FooterHeight = 76, CaptionHeight = 30, StripHeight = 118;

    private readonly Func<SlideShowSession?> _show;
    private readonly Func<PlaybackEngine?> _program;

    // Header.
    private readonly Label _elapsedCaption = MakeLabel("已用时间", 9.5F, Muted);
    private readonly Label _elapsed = new() { ForeColor = Ink, BackColor = Color.Transparent, Font = new Font("Consolas", 26F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Text = "00:00:00" };
    private readonly Button _pause;
    private readonly Button _reset;
    private readonly Label _page = MakeLabel("", 20F, Ink, semibold: true, ContentAlignment.MiddleCenter);
    private readonly Label _step = MakeLabel("", 10F, Muted, align: ContentAlignment.MiddleCenter);
    private readonly Label _clockCaption = MakeLabel("现在", 9.5F, Muted, align: ContentAlignment.TopRight);
    private readonly Label _clock = new() { ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Consolas", 22F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };

    // Current slide (left).
    private readonly Label _currentCaption = MakeLabel("", 10.5F, Ink, semibold: true);
    private readonly Panel _currentFrame = new() { BackColor = Line, Padding = new Padding(3) };
    private readonly ProgramMirror _current = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _currentStill = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Visible = false };

    // Next slide and notes (right).
    private readonly Label _nextCaption = MakeLabel("下一页", 10.5F, Ink, semibold: true);
    private readonly Panel _nextFrame = new() { BackColor = Line, Padding = new Padding(1) };
    private readonly PictureBox _next = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _nextEmpty = MakeLabel("", 11F, Muted, align: ContentAlignment.MiddleCenter);
    private readonly Label _notesCaption = MakeLabel("备注", 10.5F, Ink, semibold: true);
    private readonly Button _smaller;
    private readonly Button _larger;
    private readonly Panel _notesFrame = new() { BackColor = Card, Padding = new Padding(14, 10, 6, 10) };
    private readonly TextBox _notes = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = Card, ForeColor = Ink, TabStop = false, WordWrap = true,
    };

    // All pages: slide with the wheel or by dragging, click to put that page on the big screen.
    private readonly SlideThumbnailStrip _strip = new(Back, Card, Line, Ink, Muted, Accent);

    // Footer.
    private readonly Label _keys = MakeLabel("← →  翻页　·　空格  下一步　·　F11  全屏　·　Ctrl + / −  备注字号", 9.5F, Muted, align: ContentAlignment.MiddleLeft);
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private readonly Button _fullScreen;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Stopwatch _stopwatch = new();
    private SlideShowSession? _shown;
    private int _slide = -1;
    private int _version;
    private float _notesSize = 16F;
    private bool _sample;

    public PresenterWindow(Func<SlideShowSession?> show, Func<Rectangle?> programBounds, Func<PlaybackEngine?> program)
    {
        _show = show;
        _program = program;
        Text = "演讲者视图";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        MinimumSize = new Size(960, 600);
        Size = new Size(1280, 760);
        BackColor = Back;
        ForeColor = Ink;
        Font = new Font(ModernUi.UiFont, 10F);
        KeyPreview = true;
        DoubleBuffered = true;

        _current.SourceBounds = programBounds;
        _current.IdleText = "大屏没有在放映演示文稿";
        _currentFrame.Controls.Add(_currentStill);
        _currentFrame.Controls.Add(_current);
        _nextFrame.Controls.Add(_next);
        _next.Controls.Add(_nextEmpty);
        _nextEmpty.Dock = DockStyle.Fill;
        _notes.Font = new Font(ModernUi.UiFont, _notesSize);
        _notesFrame.Controls.Add(_notes);

        _pause = SmallButton("暂停", "暂停 / 继续计时", TogglePause);
        _reset = SmallButton("归零", "计时从 0 重新开始", () => { _stopwatch.Reset(); if (_shown != null || _sample) _stopwatch.Start(); UpdatePauseText(); RefreshClock(); });
        _smaller = SmallButton("A−", "备注字号变小（Ctrl −）", () => SetNotesSize(_notesSize - 2));
        _larger = SmallButton("A+", "备注字号变大（Ctrl +）", () => SetNotesSize(_notesSize + 2));
        _previousButton = BigButton("◀  上一步", "上一个动画 / 上一页（←）", () => Step(-1), primary: false);
        _nextButton = BigButton("下一步  ▶", "下一个动画 / 下一页（→ 或 空格）", () => Step(1), primary: true);
        _fullScreen = SmallButton("⤢ 全屏", "全屏 / 还原（F11）", ToggleMaximize);

        Controls.AddRange(new Control[]
        {
            _elapsedCaption, _elapsed, _pause, _reset, _page, _step, _clockCaption, _clock,
            _currentCaption, _currentFrame, _nextCaption, _nextFrame, _notesCaption, _smaller, _larger, _notesFrame,
            _strip, _keys, _previousButton, _nextButton, _fullScreen,
        });
        _strip.SlideChosen += GoTo;

        _timer.Tick += (_, _) => RefreshShow();
        _timer.Start();
        RefreshShow();
    }

    private static Label MakeLabel(string text, float size, Color color, bool semibold = false, ContentAlignment align = ContentAlignment.MiddleLeft) => new()
    {
        Text = text,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = new Font(semibold ? ModernUi.UiFontSemibold : ModernUi.UiFont, size),
        TextAlign = align,
        AutoEllipsis = true,
        UseMnemonic = false,
    };

    private PresenterButton SmallButton(string text, string tip, Action onClick)
    {
        var button = new PresenterButton { Text = text, Font = new Font(ModernUi.UiFont, 9.5F) };
        button.Click += (_, _) => onClick();
        new ToolTip().SetToolTip(button, tip);
        return button;
    }

    private PresenterButton BigButton(string text, string tip, Action onClick, bool primary)
    {
        var button = new PresenterButton { Text = text, Primary = primary, Font = new Font(ModernUi.UiFontSemibold, 12F) };
        button.Click += (_, _) => onClick();
        new ToolTip().SetToolTip(button, tip);
        return button;
    }

    private int S(int value) => LogicalToDeviceUnits(value);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_pause == null) return; // base constructor lays out before the fields exist
        int pad = S(Pad), gap = S(Gap), width = ClientSize.Width, height = ClientSize.Height;
        int inner = width - pad * 2;

        // Header: timer left, page centred, clock right. The centre column is at most 300 wide and the
        // sides share the rest equally, so the page number stays centred and the timer group (with its two
        // buttons) still fits at the minimum window width.
        int header = S(HeaderHeight), top = S(10);
        int centre = Math.Min(inner / 3, S(300)), side = (inner - centre) / 2;
        _elapsedCaption.Bounds = new Rectangle(pad, top, side, S(20));
        _elapsed.Bounds = new Rectangle(pad, top + S(20), S(172), S(40));
        _pause.Bounds = new Rectangle(_elapsed.Right + S(8), top + S(26), S(56), S(30));
        _reset.Bounds = new Rectangle(_pause.Right + S(6), top + S(26), S(56), S(30));
        _page.Bounds = new Rectangle(pad + side, top + S(4), centre, S(36));
        _step.Bounds = new Rectangle(pad + side, top + S(40), centre, S(22));
        _clockCaption.Bounds = new Rectangle(pad + side + centre, top, inner - side - centre, S(20));
        _clock.Bounds = new Rectangle(pad + side + centre, top + S(20), inner - side - centre, S(40));

        // Footer.
        int footerTop = height - S(FooterHeight);
        int bigW = S(190), bigH = S(50), bigTop = footerTop + (S(FooterHeight) - bigH) / 2;
        _previousButton.Bounds = new Rectangle(width / 2 - gap / 2 - bigW, bigTop, bigW, bigH);
        _nextButton.Bounds = new Rectangle(width / 2 + gap / 2, bigTop, bigW, bigH);
        _fullScreen.Bounds = new Rectangle(width - pad - S(96), bigTop + (bigH - S(34)) / 2, S(96), S(34));
        _keys.Bounds = new Rectangle(pad, bigTop, Math.Max(0, _previousButton.Left - pad - gap), bigH);

        // Thumbnail strip: full width, directly above the footer.
        int stripTop = footerTop - S(StripHeight);
        _strip.Bounds = new Rectangle(pad, stripTop + S(6), inner, S(StripHeight) - S(10));

        // Body: current slide ~62 %, next + notes the rest.
        int bodyTop = header + S(6), bodyBottom = stripTop - S(2);
        int leftW = (inner - gap) * 62 / 100, rightW = inner - gap - leftW, rightX = pad + leftW + gap;
        _currentCaption.Bounds = new Rectangle(pad, bodyTop, leftW, S(CaptionHeight));
        int frameTop = bodyTop + S(CaptionHeight);
        int availableH = Math.Max(S(60), bodyBottom - frameTop);
        // 16:9, as large as fits, top-aligned so the captions line up.
        int frameW = Math.Min(leftW, availableH * 16 / 9), frameH = frameW * 9 / 16;
        _currentFrame.Bounds = new Rectangle(pad + (leftW - frameW) / 2, frameTop, frameW, frameH);
        _currentFrame.Padding = new Padding(S(3));

        _nextCaption.Bounds = new Rectangle(rightX, bodyTop, rightW, S(CaptionHeight));
        int nextH = Math.Min(rightW * 9 / 16, (bodyBottom - frameTop) / 2);
        int nextW = nextH * 16 / 9;
        _nextFrame.Bounds = new Rectangle(rightX + (rightW - nextW) / 2, frameTop, nextW, nextH);
        int notesTop = frameTop + nextH + gap;
        _notesCaption.Bounds = new Rectangle(rightX, notesTop, rightW - S(80), S(CaptionHeight));
        _larger.Bounds = new Rectangle(rightX + rightW - S(36), notesTop, S(36), S(28));
        _smaller.Bounds = new Rectangle(_larger.Left - S(6) - S(36), notesTop, S(36), S(28));
        int notesFrameTop = notesTop + S(CaptionHeight) + S(2);
        _notesFrame.Bounds = new Rectangle(rightX, notesFrameTop, rightW, Math.Max(S(40), bodyBottom - notesFrameTop));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Thin rules under the header and above the footer: the grid made visible.
        using var pen = new Pen(Line);
        int y1 = S(HeaderHeight) - S(2), y2 = ClientSize.Height - S(FooterHeight);
        int y3 = y2 - S(StripHeight);
        e.Graphics.DrawLine(pen, S(Pad), y3, ClientSize.Width - S(Pad), y3);
        e.Graphics.DrawLine(pen, S(Pad), y1, ClientSize.Width - S(Pad), y1);
        e.Graphics.DrawLine(pen, S(Pad), y2, ClientSize.Width - S(Pad), y2);
    }

    private void SetNotesSize(float size)
    {
        _notesSize = Math.Clamp(size, 10F, 48F);
        _notes.Font = new Font(ModernUi.UiFont, _notesSize);
    }

    private void TogglePause()
    {
        if (_stopwatch.IsRunning) _stopwatch.Stop(); else _stopwatch.Start();
        UpdatePauseText();
    }

    private void UpdatePauseText() => _pause.Text = _stopwatch.IsRunning || _stopwatch.Elapsed == TimeSpan.Zero ? "暂停" : "继续";

    private void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    /// <summary>A thumbnail was clicked: put that page on the big screen.</summary>
    private void GoTo(int slide)
    {
        if (_program() is not { } program || _show() == null) return;
        _ = program.SlideCommandAsync(s => s.GoToSlideAsync(slide), $"跳到第 {slide} 页", SlideMove.Jump);
    }

    private void Step(int direction)
    {
        if (_program() is not { } program || _show() == null) return;
        _ = program.SlideCommandAsync(s => direction > 0 ? s.NextStepAsync() : s.PreviousStepAsync(),
            direction > 0 ? "下一步" : "上一步", direction > 0 ? SlideMove.NextStep : SlideMove.PreviousStep);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            // Up/Down are left to the notes box (scrolling long notes); Page keys and arrows turn pages.
            case Keys.Right or Keys.PageDown or Keys.Space: Step(1); return true;
            case Keys.Left or Keys.PageUp: Step(-1); return true;
            case Keys.F11: ToggleMaximize(); return true;
            case Keys.Control | Keys.Oemplus: SetNotesSize(_notesSize + 2); return true;
            case Keys.Control | Keys.OemMinus: SetNotesSize(_notesSize - 2); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshClock()
    {
        var elapsed = _stopwatch.Elapsed;
        _elapsed.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        _clock.Text = DateTime.Now.ToString("HH:mm");
    }

    private void RefreshShow()
    {
        if (_sample) { RefreshClock(); return; }
        var show = _show();
        if (!ReferenceEquals(show, _shown))
        {
            ++_version;
            _shown = show;
            _slide = -1;
            _stopwatch.Reset();
            if (show != null) _stopwatch.Start();
            UpdatePauseText();
            SetNext(null, show == null ? "" : "下一页");
            _strip.Reset(show?.SlideCount ?? 0, show == null ? null : async n => await show.GetSlideImageAsync(n, 240));
            _notes.Text = show == null ? "大屏没有在放映演示文稿。放映开始后，这里显示当前页的备注。" : "";
        }
        RefreshClock();
        bool live = show != null;
        _currentCaption.Text = live ? "●  大屏正在显示" : "大屏没有在放映";
        _currentCaption.ForeColor = live ? Live : Muted;
        _currentFrame.BackColor = live ? Live : Line;
        _previousButton.Enabled = _nextButton.Enabled = live;
        if (show == null)
        {
            _page.Text = "—";
            _step.Text = "";
            return;
        }
        var position = show.Position;
        _page.Text = $"第 {Math.Max(1, position.Slide)} / {show.SlideCount} 页";
        _step.Text = position.Clicks > 0 ? $"动画 {position.Click} / {position.Clicks}" : "本页没有动画";
        _strip.Current = position.Slide;
        if (position.Slide >= 1 && position.Slide != _slide)
        {
            _slide = position.Slide;
            _ = LoadAsync(show, position.Slide, ++_version);
        }
    }

    private async Task LoadAsync(SlideShowSession show, int slide, int version)
    {
        string notes = "";
        Bitmap? next = null;
        try { notes = await show.NotesAsync(slide); } catch (Exception) { }
        if (slide < show.SlideCount)
            try { next = await show.GetSlideImageAsync(slide + 1, 960); } catch (Exception) { }
        if (IsDisposed || version != _version) { next?.Dispose(); return; }
        _notes.Text = string.IsNullOrWhiteSpace(notes) ? "（这一页没有备注）" : notes.Trim().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _notes.SelectionStart = 0;
        _notes.ScrollToCaret();
        SetNext(next, slide < show.SlideCount ? $"下一页 · 第 {slide + 1} 页" : "下一页");
    }

    private void SetNext(Bitmap? image, string caption)
    {
        var old = _next.Image;
        _next.Image = image;
        old?.Dispose();
        _nextCaption.Text = caption;
        _nextEmpty.Text = image != null ? "" : _shown == null && !_sample ? "" : _slide >= 1 && _shown != null && _slide >= _shown.SlideCount ? "已是最后一页" : "";
        _nextEmpty.Visible = image == null && _nextEmpty.Text.Length > 0;
    }

    /// <summary>CI screenshot only: fills the window with a made-up slide pair and notes, since the
    /// real content needs a running presentation program.</summary>
    internal void ShowSampleForScreenshot()
    {
        _sample = true;
        _current.Visible = false;
        _currentStill.Visible = true;
        _currentStill.Image = SampleSlide(1600, "第 6 页 · 年度回顾", Color.FromArgb(40, 70, 110));
        _next.Image = SampleSlide(960, "第 7 页 · 明年计划", Color.FromArgb(70, 50, 100));
        _nextCaption.Text = "下一页 · 第 7 页";
        _nextEmpty.Visible = false;
        _currentCaption.Text = "●  大屏正在显示";
        _currentCaption.ForeColor = Live;
        _currentFrame.BackColor = Live;
        _strip.Reset(18, null);
        for (int i = 1; i <= 18; i++)
            _strip.SetImage(i, SampleSlide(320, $"第 {i} 页", i % 3 == 0 ? Color.FromArgb(70, 50, 100) : Color.FromArgb(40, 70, 110)));
        _strip.Current = 6;
        _page.Text = "第 6 / 18 页";
        _step.Text = "动画 1 / 3";
        _previousButton.Enabled = _nextButton.Enabled = true;
        _notes.Text = "先停顿两秒，等掌声落下。" + Environment.NewLine + Environment.NewLine
            + "1. 三项核心指标：用户数、留存、营收。" + Environment.NewLine
            + "2. 强调第二季度的转折，点名感谢运营团队。" + Environment.NewLine
            + "3. 点下一步出现对比图，再讲同比增长。";
        _stopwatch.Restart();
        UpdatePauseText();
        RefreshClock();
        _elapsed.Text = "00:12:34";
        PerformLayout();
        Invalidate(true);
    }

    private static Bitmap SampleSlide(int width, string title, Color tint)
    {
        int height = width * 9 / 16;
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(245, 247, 250), Color.FromArgb(222, 230, 240), 90F))
            g.FillRectangle(back, 0, 0, width, height);
        using (var band = new SolidBrush(tint)) g.FillRectangle(band, 0, 0, width, height / 7);
        using var titleFont = new Font(ModernUi.UiFontSemibold, width / 32F, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(30, 40, 52));
        g.DrawString(title, titleFont, ink, width / 14F, height / 4.5F);
        using var bar = new SolidBrush(tint);
        for (int i = 0; i < 4; i++)
        {
            int h = height / 6 + i * height / 12;
            g.FillRectangle(bar, width / 14 + i * width / 7, height * 5 / 6 - h, width / 10, h);
        }
        return bitmap;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _next.Image?.Dispose();
            _next.Image = null;
            _currentStill.Image?.Dispose();
            _currentStill.Image = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Flat rounded button in the window's fixed palette; the primary one is filled blue.</summary>
    private sealed class PresenterButton : Button
    {
        public bool Primary { get; set; }
        private bool _hot;

        public PresenterButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            UseMnemonic = false;
            TabStop = false; // keyboard input belongs to page turning
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Back)) g.FillRectangle(back, ClientRectangle);
            Color fill = !Enabled ? Card : Primary ? (_hot ? Color.FromArgb(76, 145, 255) : Accent) : (_hot ? Color.FromArgb(30, 41, 55) : Card);
            Color ink = !Enabled ? Color.FromArgb(90, 104, 120) : Primary ? Color.White : Ink;
            float r = LogicalToDeviceUnits(8);
            using var path = new GraphicsPath();
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            path.AddArc(rect.Left, rect.Top, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Top, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (!Primary) using (var pen = new Pen(Line)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}
