using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 演讲者视图 for the presentation running on the big screen, laid out like a presentation program's own
/// presenter view: the current slide large on the left (a live copy of the big screen), the next slide,
/// the notes (A− / A+ change their size), the elapsed time and the page count on the right, and page
/// buttons. An ordinary window: it can be resized, maximized (or moved to another screen) and closed;
/// it follows whichever show is on air and says so when there is none.
/// </summary>
internal sealed class PresenterWindow : Form
{
    private readonly Func<SlideShowSession?> _show;
    private readonly Func<PlaybackEngine?> _program;
    private readonly ProgramMirror _current = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _next = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _nextCaption = new() { ForeColor = Color.FromArgb(150, 165, 185), Text = "下一页", AutoEllipsis = true };
    private readonly Label _notes = new() { ForeColor = Color.FromArgb(235, 240, 246), AutoEllipsis = false };
    private readonly Label _clock = new() { ForeColor = Color.White, Font = new Font("Consolas", 26F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _page = new() { ForeColor = Color.FromArgb(200, 210, 222), Font = new Font(ModernUi.UiFont, 12F), TextAlign = ContentAlignment.MiddleRight };
    private readonly Panel _side = new() { Dock = DockStyle.Right, BackColor = Color.FromArgb(14, 20, 28), Padding = new Padding(16) };
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom, BackColor = Color.FromArgb(14, 20, 28), Height = 56 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private SlideShowSession? _shown;
    private DateTime _started;
    private int _slide = -1;
    private int _version;
    private float _notesSize = 16F;

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
        MinimumSize = new Size(720, 420);
        Size = new Size(1280, 760);
        BackColor = Color.FromArgb(8, 12, 18);
        ForeColor = Color.White;
        Font = new Font(ModernUi.UiFont, 10F);
        KeyPreview = true;
        _current.SourceBounds = programBounds;
        _current.IdleText = "大屏没有在放映演示文稿";

        var currentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 8, 8), BackColor = BackColor };
        currentHost.Controls.Add(_current);
        _notes.Font = new Font(ModernUi.UiFont, _notesSize);

        var smaller = Button("A−", "备注字号变小", () => SetNotesSize(_notesSize - 2));
        var larger = Button("A+", "备注字号变大", () => SetNotesSize(_notesSize + 2));
        var previous = Button("◀ 上一步", "上一个动画 / 上一页（←）", () => Step(-1));
        var nextStep = Button("下一步 ▶", "下一个动画 / 下一页（→ / 空格）", () => Step(1));
        var reset = Button("计时归零", "重新开始计时", () => _started = DateTime.Now);
        var maximize = Button("⤢ 最大化", "最大化 / 还原（F11）", ToggleMaximize);
        foreach (var button in new[] { previous, nextStep, reset, smaller, larger, maximize }) _bottom.Controls.Add(button);
        _bottom.Resize += (_, _) => LayoutBottom(previous, nextStep, reset, smaller, larger, maximize);

        _side.Controls.AddRange(new Control[] { _clock, _page, _nextCaption, _next, _notes });
        _side.Resize += (_, _) => LayoutSide();
        Controls.Add(currentHost);
        Controls.Add(_side);
        Controls.Add(_bottom);
        Resize += (_, _) => _side.Width = Math.Max(300, ClientSize.Width * 36 / 100);
        _timer.Tick += (_, _) => RefreshShow();
        _timer.Start();
        _started = DateTime.Now;
        RefreshShow();
    }

    private Button Button(string text, string tip, Action onClick)
    {
        var button = new RoundedActionButton { Text = text, Height = 36, Width = 104 };
        ModernUi.StyleButton(button);
        button.Click += (_, _) => onClick();
        new ToolTip().SetToolTip(button, tip);
        return button;
    }

    private void LayoutBottom(params Control[] buttons)
    {
        int x = 16, y = (_bottom.Height - 36) / 2;
        buttons[0].Location = new Point(x, y);
        buttons[1].Location = new Point(x + 112, y);
        buttons[2].Location = new Point(x + 224, y);
        int right = _bottom.ClientSize.Width - 16;
        for (int i = buttons.Length - 1; i >= 3; i--)
        {
            int width = i == buttons.Length - 1 ? 104 : 52;
            buttons[i].Width = width;
            right -= width;
            buttons[i].Location = new Point(right, y);
            right -= 8;
        }
    }

    private void LayoutSide()
    {
        int w = _side.ClientSize.Width - 32, x = 16, y = 16;
        _clock.Bounds = new Rectangle(x, y, w / 2, 44);
        _page.Bounds = new Rectangle(x + w / 2, y, w - w / 2, 44);
        y += 52;
        int nextHeight = w * 9 / 16;
        _next.Bounds = new Rectangle(x, y, w, nextHeight);
        y += nextHeight + 4;
        _nextCaption.Bounds = new Rectangle(x, y, w, 22);
        y += 30;
        _notes.Bounds = new Rectangle(x, y, w, Math.Max(40, _side.ClientSize.Height - y - 16));
    }

    private void SetNotesSize(float size)
    {
        _notesSize = Math.Clamp(size, 10F, 48F);
        _notes.Font = new Font(ModernUi.UiFont, _notesSize);
    }

    private void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

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
            case Keys.Right or Keys.Down or Keys.PageDown or Keys.Space: Step(1); return true;
            case Keys.Left or Keys.Up or Keys.PageUp: Step(-1); return true;
            case Keys.F11: ToggleMaximize(); return true;
            case Keys.Control | Keys.Oemplus: SetNotesSize(_notesSize + 2); return true;
            case Keys.Control | Keys.OemMinus: SetNotesSize(_notesSize - 2); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshShow()
    {
        var show = _show();
        var elapsed = DateTime.Now - _started;
        _clock.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        if (!ReferenceEquals(show, _shown))
        {
            ++_version;
            _shown = show;
            _slide = -1;
            _started = DateTime.Now;
            SetNext(null, show == null ? "" : "下一页");
            _notes.Text = show == null ? "大屏没有在放映演示文稿。放映开始后这里显示备注和下一页。" : "";
        }
        if (show == null) { _page.Text = ""; return; }
        var position = show.Position;
        _page.Text = $"第 {Math.Max(1, position.Slide)} / {show.SlideCount} 页" + (position.Clicks > 0 ? $" · 动画 {position.Click}/{position.Clicks}" : "");
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
        _notes.Text = string.IsNullOrWhiteSpace(notes) ? "（这一页没有备注）" : notes.Trim();
        SetNext(next, slide < show.SlideCount ? $"下一页 · 第 {slide + 1} 页" : "已是最后一页");
    }

    private void SetNext(Bitmap? image, string caption)
    {
        var old = _next.Image;
        _next.Image = image;
        old?.Dispose();
        _nextCaption.Text = caption;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _next.Image?.Dispose();
            _next.Image = null;
        }
        base.Dispose(disposing);
    }
}
