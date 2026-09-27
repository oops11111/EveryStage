using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>Shared local-preview/output controls shown beneath every main page.</summary>
internal sealed class SharedPreviewDock : GlassPanel
{
    private readonly OutputStateMachine _stateMachine;
    private readonly PictureBox _preview;
    private readonly Label _fileName;
    private readonly Label _state;
    private readonly System.Windows.Forms.Timer _timer;
    private PlaybackEngine? _playback;
    private Bitmap? _placeholder;

    private const int ButtonHeight = 30;
    private const int SourceCardHeight = 46;
    private const string IdleHint = "ⓘ 同一时间仅一个窗口输出";

    /// <summary>Height of everything in the dock except the preview picture itself (padding, heading,
    /// transport buttons, source heading and cards) — MainWindow sizes the dock as this plus a preview
    /// that grows with the window, instead of one fixed row height that left the preview ~50px tall.</summary>
    public int ChromeHeight { get; }

    public SharedPreviewDock(OutputStateMachine stateMachine, PlaybackEngine? playback)
    {
        _stateMachine = stateMachine;
        _playback = playback;
        Dock = DockStyle.Fill;
        Padding = new Padding(10, 7, 10, 7);
        CornerRadius = 10;
        GlassTint = Color.FromArgb(190, 12, 29, 49);

        // Row heights come from the real fonts/control sizes, and every cell has a zero margin: the
        // old fixed 24/34/50px rows each silently lost 6px to TableLayoutPanel's default 3px cell
        // margins, which clipped the buttons, the「信号源窗口」heading (CJK falls back to a taller
        // font than Segoe UI's metrics) and the source cards even at the default window size.
        var titleFont = new Font("Segoe UI Semibold", 10.5F);
        var smallFont = new Font("Segoe UI", 9F);
        int titleHeight = TextRenderer.MeasureText("本地预览 Ag", titleFont).Height;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 5, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight + 10));   // heading
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                 // preview
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonHeight + 12));  // transport
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight + 6));    // 信号源窗口 heading
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SourceCardHeight + 2)); // source cards
        ChromeHeight = Padding.Vertical + (titleHeight + 10) + (ButtonHeight + 12) + (titleHeight + 6) + (SourceCardHeight + 2);

        // 「本地预览 / 播控窗口 ● 状态 …… 文件名 / 提示」标题行（对齐效果图），取代原来压在预览图下方的字幕条。
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "本地预览 / 播控窗口", AutoSize = true, ForeColor = ModernUi.Text, Font = titleFont, Margin = new Padding(2, 2, 10, 0) }, 0, 0);
        _state = new Label { Text = "● 待机", AutoSize = true, ForeColor = ModernUi.Muted, Font = smallFont, Margin = new Padding(0, 5, 0, 0) };
        heading.Controls.Add(_state, 1, 0);
        _fileName = new Label { Text = IdleHint, Dock = DockStyle.Fill, ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = new Padding(12, 0, 2, 2) };
        heading.Controls.Add(_fileName, 2, 0);
        layout.Controls.Add(heading, 0, 0);

        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = Color.FromArgb(4, 16, 31), // prototype --black letterbox
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        layout.Controls.Add(_preview, 0, 1);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        AddControl("上一项", () => _playback?.PreviousManual());
        AddControl("▶ / 暂停", () => { if (_playback == null) return; if (_playback.IsPaused) _playback.Resume(); else _playback.Pause(); });
        AddControl("下一项", () => _playback?.NextManual());
        AddControl("断开输出", _stateMachine.Disconnect, danger: true);
        layout.Controls.Add(controls, 0, 2);

        // 「信号源窗口」标题行（对齐效果图）。
        var sourcesHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(2, 2, 0, 0) };
        sourcesHeader.Controls.Add(new Label { Text = "信号源窗口", AutoSize = true, ForeColor = ModernUi.Text, Font = titleFont, Margin = new Padding(0, 0, 12, 0) });
        sourcesHeader.Controls.Add(new Label { Text = "选择一个窗口作为当前输出（同一时间仅有一个窗口输出）", AutoSize = true, ForeColor = ModernUi.Muted, Font = smallFont, Margin = new Padding(0, 3, 0, 0) });
        layout.Controls.Add(sourcesHeader, 0, 3);

        var sources = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        sources.Controls.Add(SourceCard(NavIcon.Devices, "本地预览 / 播控窗口", "本地预览中 · 未投屏", ModernUi.Success, selected: true));
        sources.Controls.Add(SourceCard(NavIcon.Devices, "设备投屏窗口", "等待设备来投", ModernUi.Muted, selected: false));
        layout.Controls.Add(sources, 0, 4);
        Controls.Add(layout);

        void AddControl(string text, Action action, bool danger = false)
        {
            var button = new RoundedActionButton { Text = text, Width = 104, Height = ButtonHeight, Margin = new Padding(0, 0, 8, 0) };
            ModernUi.StyleButton(button, danger: danger);
            button.Click += (_, _) => action();
            controls.Controls.Add(button);
        }

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => RefreshPreview();
        _timer.Start();
        RefreshPreview();
    }

    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        _playback = playback;
        RefreshPreview();
    }

    private static Control SourceCard(NavIcon icon, string title, string status, Color statusColor, bool selected)
    {
        var card = new GlassPanel
        {
            Width = 252,
            Height = SourceCardHeight,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(6, 5, 8, 5),
            CornerRadius = 9,
            GlassTint = selected ? Color.FromArgb(235, 14, 52, 96) : ModernUi.SurfaceRaised,
        };
        var thumb = new GlassPanel
        {
            Dock = DockStyle.Left, Width = 42, CornerRadius = 7,
            GlassTint = selected ? Color.FromArgb(205, 12, 46, 92) : ModernUi.Surface,
        };
        thumb.Paint += (_, e) =>
        {
            int s = 20;
            var r = new Rectangle((thumb.Width - s) / 2, (thumb.Height - s) / 2, s, s);
            VectorIcons.Draw(e.Graphics, icon, r, selected ? ModernUi.Accent : ModernUi.Muted);
        };
        var labels = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(10, 0, 0, 0) };
        var name = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 18,
            ForeColor = ModernUi.Text, Font = new Font("Segoe UI Semibold", 9F),
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        var state = new Label
        {
            Text = $"● {status}", Dock = DockStyle.Fill,
            ForeColor = statusColor,
            Font = new Font("Segoe UI", 8F), TextAlign = ContentAlignment.MiddleLeft,
        };
        labels.Controls.Add(state);
        labels.Controls.Add(name);
        card.Controls.Add(labels);
        card.Controls.Add(thumb);
        return card;
    }

    private void RefreshPreview()
    {
        if (IsDisposed) return;
        var file = _playback?.CurrentFile;
        _fileName.Text = file == null ? IdleHint : Path.GetFileName(file.SourcePath);
        bool active = _stateMachine.State == OutputState.Active;
        _state.Text = active ? "● 输出中" : file == null ? "● 待机" : "● 本地预览中 · 未投屏";
        _state.ForeColor = active || file != null ? ModernUi.Success : ModernUi.Muted;

        Image? next = null;
        var source = _playback?.CurrentThumbnail;
        if (source != null)
        {
            try
            {
                float scale = Math.Min(480f / source.Width, 180f / source.Height);
                next = new Bitmap(source, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
            }
            catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException) { }
        }
        // No live thumbnail → show the branded idle splash (matches the design prototype's preview
        // hero) rather than a bare dark box. Cached: regenerating a bitmap on every 500ms tick would
        // be wasteful, and the swap logic below must not dispose the shared cached instance.
        next ??= _placeholder ??= PreviewPlaceholder.Create(800, 450);

        var old = _preview.Image;
        if (!ReferenceEquals(old, next))
        {
            _preview.Image = next;
            if (!ReferenceEquals(old, _placeholder)) old?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            // _preview.Image may be the cached _placeholder — avoid disposing it twice.
            if (!ReferenceEquals(_preview.Image, _placeholder)) _preview.Image?.Dispose();
            _placeholder?.Dispose();
        }
        base.Dispose(disposing);
    }
}
