using System.Drawing.Drawing2D;
using EveryStage.Rendering;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>
/// "本地预览 / 播控窗口" + "信号源窗口" beneath every main page — the Preview monitor and its transport,
/// laid out after the design prototype (screen, then one transport row: ⏮ ⏯ ⏹ ⏭ · timecode · progress ·
/// volume · loop · fit/fill · 投到屏幕 · 停止输出).
///
/// Everything here operates the Preview engine, which renders into <see cref="PreviewSurface"/> inside
/// this dock and never touches the extended display. "投到屏幕" (<see cref="TakeRequested"/>) and
/// "停止输出" (<see cref="StopProgramRequested"/>) are the only controls that affect Program; MainWindow
/// owns both engines and performs them. The heading and the two source cards show Program's status and
/// what is on air (local media vs. a device cast).
///
/// When the Preview engine fails, an error card covers the monitor with the file name, failure stage,
/// container and codecs, a suggestion, and 重试 / 跳过 / 从文件库移除 / 用系统程序打开.
///
/// As in the reference mockups, the two areas are two separate rounded cards stacked with a gap —
/// 「本地预览 / 播控窗口」 (heading, monitor, transport) above 「信号源窗口」 (heading, source cards) —
/// so this control itself is only a transparent container for them.
/// </summary>
internal sealed class SharedPreviewDock : Panel
{
    private const int CardGap = 8;
    private const int MinProgressWidth = 90;
    private const int ButtonHeight = 30;
    private const int SourceCardHeight = 46;
    private const string IconFont = "Segoe MDL2 Assets";

    private readonly OutputStateMachine _stateMachine;
    private readonly Label _previewState;
    private readonly Label _programState;
    private readonly RoundedActionButton _previousButton;
    private readonly RoundedActionButton _playPauseButton;
    private readonly RoundedActionButton _stopButton;
    private readonly RoundedActionButton _nextButton;
    private readonly Label _timecode;
    private readonly MediaTrack _progress;
    private readonly MediaTrack _volume;
    private readonly RoundedActionButton _loopButton;
    private readonly RoundedActionButton _scaleButton;
    private readonly RoundedActionButton _takeButton;
    private readonly RoundedActionButton _stopOutputButton;
    private readonly SourceCardView _localCard;
    private readonly SourceCardView _deviceCard;
    private readonly ErrorCard _errorCard;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _toolTip = new();
    private PlaybackEngine? _preview;
    private PlaybackEngine? _program;
    private Bitmap? _placeholder;

    /// <summary>The Preview channel's output surface (MainWindow builds the Preview engine on it).</summary>
    public PreviewSurface PreviewSurface { get; }

    /// <summary>Height of everything in the dock except the preview monitor itself — MainWindow sizes the
    /// dock as this plus a monitor that grows with the window.</summary>
    public int ChromeHeight { get; }

    /// <summary>"投到屏幕": put the Preview content on the extended display.</summary>
    public event Action? TakeRequested;

    /// <summary>"停止输出": take whatever is on the extended display off air (Preview is unaffected).</summary>
    public event Action? StopProgramRequested;

    /// <summary>Error card "从文件库移除".</summary>
    public event Action<MediaFile>? RemoveFromLibraryRequested;

    public SharedPreviewDock(OutputStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
        Dock = DockStyle.Fill;
        Padding = Padding.Empty;
        BackColor = Color.Transparent;

        // Two cards: the preview card fills; the 信号源窗口 card has a fixed height at the bottom.
        var previewCard = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(10, 7, 10, 7), CornerRadius = 10,
            GlassTint = Color.FromArgb(190, 12, 29, 49),
        };
        var sourcesCard = new GlassPanel
        {
            Dock = DockStyle.Bottom, Padding = new Padding(10, 5, 10, 7), CornerRadius = 10,
            GlassTint = Color.FromArgb(190, 12, 29, 49),
        };
        var cardGap = new Panel { Dock = DockStyle.Bottom, Height = CardGap, BackColor = Color.Transparent };

        // Row heights come from the real fonts/control sizes, and every cell has a zero margin
        // (TableLayoutPanel's default 3px cell margins used to clip the buttons and cards).
        var titleFont = new Font("Segoe UI Semibold", 10.5F);
        var smallFont = new Font("Segoe UI", 9F);
        int titleHeight = TextRenderer.MeasureText("本地预览 Ag", titleFont).Height;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight + 10));      // heading
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                    // monitor
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonHeight + 12));     // transport
        var sourcesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = Padding.Empty, Margin = Padding.Empty };
        sourcesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight + 6));      // 信号源窗口 heading
        sourcesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SourceCardHeight + 2)); // source cards
        sourcesCard.Height = sourcesCard.Padding.Vertical + (titleHeight + 6) + (SourceCardHeight + 2);
        ChromeHeight = previewCard.Padding.Vertical + (titleHeight + 10) + (ButtonHeight + 12) + CardGap + sourcesCard.Height;

        // Heading: 「本地预览 / 播控窗口 ● 预览状态 …… 输出状态」.
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        heading.Controls.Add(new Label { Text = "本地预览 / 播控窗口", AutoSize = true, ForeColor = ModernUi.Text, Font = titleFont, Margin = new Padding(2, 2, 10, 0) }, 0, 0);
        _previewState = new Label { Text = "● 待机", Dock = DockStyle.Fill, ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0, 0, 8, 2) };
        heading.Controls.Add(_previewState, 1, 0);
        _programState = new Label { Text = "输出：未连接扩展屏", Dock = DockStyle.Fill, ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = new Padding(0, 0, 2, 2) };
        heading.Controls.Add(_programState, 2, 0);
        layout.Controls.Add(heading, 0, 0);

        PreviewSurface = new PreviewSurface { Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(PreviewSurface, 0, 1);

        // Transport: one row, the progress track takes the remaining width.
        var transport = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, RowCount = 1, ColumnCount = 12, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0) };
        int[] columnWidths = { 34, 34, 34, 34, 92, -1, 22, 64, 34, 50, 92, 82 };
        foreach (var width in columnWidths)
            transport.ColumnStyles.Add(width < 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width + 4));

        _previousButton = IconButton("\uE892", "上一项 / 上一页");
        _playPauseButton = IconButton("\uE768", "播放 / 暂停");
        _stopButton = IconButton("\uE71A", "停止预览");
        _nextButton = IconButton("\uE893", "下一项 / 下一页");
        _timecode = new Label { Text = "--:-- / --:--", Dock = DockStyle.Fill, ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
        _progress = new MediaTrack { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 10, 0) };
        var volumeIcon = new Label { Text = "\uE767", Font = new Font(IconFont, 10F), Dock = DockStyle.Fill, ForeColor = ModernUi.Muted, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
        _volume = new MediaTrack { Dock = DockStyle.Fill, Margin = new Padding(2, 0, 8, 0), Value = 1f };
        _loopButton = IconButton("\uE8EE", "循环播放当前文件（不保存到文件设置）");
        _scaleButton = TextButton("适应", "视频显示：适应（完整显示）/ 填充（铺满裁切）");
        _takeButton = TextButton("投到屏幕", "把预览中的内容投到扩展屏正式输出");
        ModernUi.StyleButton(_takeButton, primary: true);
        _stopOutputButton = TextButton("停止输出", "停止扩展屏上的正式输出（预览保留）");
        ModernUi.StyleButton(_stopOutputButton, danger: true);

        Control[] cells = { _previousButton, _playPauseButton, _stopButton, _nextButton, _timecode, _progress, volumeIcon, _volume, _loopButton, _scaleButton, _takeButton, _stopOutputButton };
        for (int i = 0; i < cells.Length; i++) transport.Controls.Add(cells[i], i, 0);
        layout.Controls.Add(transport, 0, 2);

        // The fixed-width columns take ~616px, which left the progress track only a few pixels wide at
        // the default window size. While the track would be shorter than MinProgressWidth, drop the
        // least essential columns in order: the volume icon + slider, then the timecode, then the
        // 适应/填充 button (video only). Default size drops just the volume; 800×600 drops all three.
        int[][] droppable = { new[] { 6, 7 }, new[] { 4 }, new[] { 9 } };
        int dropped = 0;
        transport.Resize += (_, _) =>
        {
            int available = transport.ClientSize.Width - transport.Padding.Horizontal;
            int fixedWidth = columnWidths.Where(w => w > 0).Sum(w => w + 4);
            int drop = 0;
            while (drop < droppable.Length && available - fixedWidth < MinProgressWidth)
                fixedWidth -= droppable[drop++].Sum(column => columnWidths[column] + 4);
            if (drop == dropped) return;
            dropped = drop;
            for (int group = 0; group < droppable.Length; group++)
                foreach (int column in droppable[group])
                {
                    bool show = group >= drop;
                    transport.ColumnStyles[column].Width = show ? columnWidths[column] + 4 : 0;
                    cells[column].Visible = show;
                }
        };

        // 「信号源窗口」: what is on the extended display right now.
        var sourcesHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(2, 2, 0, 0) };
        sourcesHeader.Controls.Add(new Label { Text = "信号源窗口", AutoSize = true, ForeColor = ModernUi.Text, Font = titleFont, Margin = new Padding(0, 0, 12, 0) });
        sourcesHeader.Controls.Add(new Label { Text = "扩展屏同一时间只输出一个来源（本地媒体与设备来投互斥）", AutoSize = true, ForeColor = ModernUi.Muted, Font = smallFont, Margin = new Padding(0, 3, 0, 0) });
        sourcesLayout.Controls.Add(sourcesHeader, 0, 0);

        var sources = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        _localCard = new SourceCardView("本地预览 / 播控窗口");
        _deviceCard = new SourceCardView("设备投屏窗口");
        sources.Controls.Add(_localCard.Card);
        sources.Controls.Add(_deviceCard.Card);
        sourcesLayout.Controls.Add(sources, 0, 1);

        previewCard.Controls.Add(layout);
        sourcesCard.Controls.Add(sourcesLayout);
        // Dock order: the last-added Bottom control sits lowest, so the sources card goes in last.
        Controls.Add(previewCard);
        Controls.Add(cardGap);
        Controls.Add(sourcesCard);

        _errorCard = new ErrorCard();
        _errorCard.Retry += () => _preview?.RetryCurrentFile();
        _errorCard.Skip += () => { if (_preview != null && !_preview.NextManual()) _preview.Stop(); };
        _errorCard.Remove += file => RemoveFromLibraryRequested?.Invoke(file);
        _errorCard.OpenExternally += file => ExternalOpener.OpenWithDefaultApp(FindForm(), file.SourcePath);

        _previousButton.Click += (_, _) => _preview?.PreviousManual();
        _nextButton.Click += (_, _) => _preview?.NextManual();
        _playPauseButton.Click += (_, _) =>
        {
            if (_preview == null) return;
            if (_preview.IsPaused || _preview.IsCompleted) _preview.Resume(); else _preview.Pause();
            RefreshNow();
        };
        _stopButton.Click += (_, _) => { _preview?.Stop(); RefreshNow(); };
        _progress.ValueCommitted += fraction =>
        {
            if (_preview?.Duration is { } duration) _preview.SeekTo(TimeSpan.FromTicks((long)(duration.Ticks * fraction)));
            RefreshNow();
        };
        _volume.ValueCommitted += value => { if (_preview != null) _preview.AudioVolume = value; };
        _volume.ValueChanging += value => { if (_preview != null) _preview.AudioVolume = value; };
        _loopButton.Click += (_, _) => { if (_preview != null) _preview.LoopCurrent = !_preview.LoopCurrent; RefreshNow(); };
        _scaleButton.Click += (_, _) =>
        {
            if (_preview == null) return;
            _preview.VideoScaleMode = _preview.VideoScaleMode == VideoScaleMode.Fill ? VideoScaleMode.Fit : VideoScaleMode.Fill;
            RefreshNow();
        };
        _takeButton.Click += (_, _) => TakeRequested?.Invoke();
        _stopOutputButton.Click += (_, _) => StopProgramRequested?.Invoke();

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        ShowIdleSplash();
        RefreshNow();

        RoundedActionButton IconButton(string glyph, string tip)
        {
            var button = new RoundedActionButton { Text = glyph, Dock = DockStyle.Fill, Height = ButtonHeight, Margin = new Padding(0, 0, 4, 0), Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            button.Font = new Font(IconFont, 10F);
            _toolTip.SetToolTip(button, tip);
            return button;
        }

        RoundedActionButton TextButton(string text, string tip)
        {
            var button = new RoundedActionButton { Text = text, Dock = DockStyle.Fill, Height = ButtonHeight, Margin = new Padding(0, 0, 4, 0), Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            _toolTip.SetToolTip(button, tip);
            return button;
        }
    }

    /// <summary>Binds the Preview engine (built by MainWindow on <see cref="PreviewSurface"/>).</summary>
    public void BindPreview(PlaybackEngine preview)
    {
        if (_preview != null) _preview.StateChanged -= OnPreviewStateChanged;
        _preview = preview;
        _preview.StateChanged += OnPreviewStateChanged;
        _volume.Value = _preview.AudioVolume;
        RefreshNow();
    }

    /// <summary>The Program engine, or null while no extended display is bound.</summary>
    public void AttachProgram(PlaybackEngine? program)
    {
        _program = program;
        RefreshNow();
    }

    private void OnPreviewStateChanged(PlaybackChannelState state)
    {
        if (state == PlaybackChannelState.Idle) ShowIdleSplash();
        RefreshNow();
    }

    /// <summary>Idle monitor shows the branded splash rather than a bare dark box.</summary>
    private void ShowIdleSplash()
    {
        _placeholder ??= PreviewPlaceholder.Create(800, 600);
        PreviewSurface.ShowImageSurface();
        PreviewSurface.ContentSurface.SetFrame(_placeholder);
    }

    private void RefreshNow()
    {
        if (IsDisposed) return;
        var preview = _preview;
        var file = preview?.CurrentFile;
        var state = preview?.State ?? PlaybackChannelState.Idle;
        bool programLive = _stateMachine.State == OutputState.Active;
        var source = programLive ? _stateMachine.Source : ProgramSource.None;

        // Heading: preview state + file.
        string fileLabel = file == null ? "" : " · " + Path.GetFileName(file.SourcePath) + (file.IsBackgroundAudio ? "（背景音乐）" : "");
        (string text, Color color) = state switch
        {
            PlaybackChannelState.Loading => ("● 加载中" + fileLabel, ModernUi.Warning),
            PlaybackChannelState.Playing => ("● 预览中" + fileLabel, ModernUi.Success),
            PlaybackChannelState.Paused when preview!.IsCompleted => ("● 播放完成" + fileLabel, ModernUi.Muted),
            PlaybackChannelState.Paused => ("● 已暂停" + fileLabel, ModernUi.Warning),
            PlaybackChannelState.Failed => ("● 播放失败" + fileLabel, ModernUi.Danger),
            _ => ("● 待机 · 双击文件在此预览", ModernUi.Muted),
        };
        if (preview?.CurrentImageNote is { } note && state == PlaybackChannelState.Playing) text += "（" + note + "）";
        if (preview?.Muted == true && state is PlaybackChannelState.Playing or PlaybackChannelState.Paused) text += " · 预监静音";
        SetText(_previewState, text, color);

        // Program status (independent of Preview).
        var programState = ProgramStatus.Compute(_program != null, programLive, _program?.State);
        (string programText, Color programColor) = programState switch
        {
            ProgramOutputState.Disconnected => ("输出：未连接扩展屏", ModernUi.Muted),
            ProgramOutputState.Failed => ("输出：播放失败", ModernUi.Danger),
            ProgramOutputState.Live when source == ProgramSource.DeviceCast => ("● 输出中 · 设备来投", ModernUi.Danger),
            ProgramOutputState.Live => ("● 输出中 · " + (_program?.CurrentFile is { } pf ? Path.GetFileName(pf.SourcePath) : "本地媒体"), ModernUi.Danger),
            _ => (_stateMachine.CastSwitchOn ? "输出：待机" : "输出：待机（投屏开关已关闭）", ModernUi.Muted),
        };
        SetText(_programState, programText, programColor);

        // Transport.
        bool hasFile = file != null && state != PlaybackChannelState.Idle;
        _previousButton.Enabled = hasFile && state != PlaybackChannelState.Loading;
        _nextButton.Enabled = hasFile && state != PlaybackChannelState.Loading;
        _stopButton.Enabled = hasFile;
        bool canToggle = preview != null && (preview.CanPause || preview.IsPaused || preview.IsCompleted);
        _playPauseButton.Enabled = canToggle;
        SetText(_playPauseButton, preview != null && (preview.IsPaused || preview.IsCompleted || state != PlaybackChannelState.Playing) ? "\uE768" : "\uE769", null);

        var duration = preview?.Duration;
        var position = preview?.Position ?? TimeSpan.Zero;
        bool timeline = preview?.CanSeek == true;
        SetText(_timecode, timeline ? $"{Format(position)} / {(duration is { } d ? Format(d) : "--:--")}" : "--:-- / --:--", null);
        _progress.Enabled = timeline && duration.HasValue;
        if (!_progress.IsDragging) _progress.Value = duration is { Ticks: > 0 } dd ? (float)(position.Ticks / (double)dd.Ticks) : 0f;

        _loopButton.Enabled = hasFile;
        _loopButton.ForeColor = preview?.LoopCurrent == true ? ModernUi.Accent : ModernUi.Text;
        _scaleButton.Enabled = file?.Kind == MediaKind.Video && hasFile;
        SetText(_scaleButton, preview?.VideoScaleMode == VideoScaleMode.Fill ? "填充" : "适应", null);

        // Disabled (not just a warning on click) while no extended display is bound.
        _takeButton.Enabled = _program != null && hasFile && state != PlaybackChannelState.Failed && state != PlaybackChannelState.Loading;
        _stopOutputButton.Enabled = programLive;

        // Source cards: which source is on air.
        string localStatus = state switch
        {
            PlaybackChannelState.Idle => "待机",
            PlaybackChannelState.Failed => "预览失败",
            _ => "预览中",
        } + (source == ProgramSource.LocalMedia ? " · 输出中" : " · 未投屏");
        _localCard.Update(source == ProgramSource.LocalMedia, localStatus,
            source == ProgramSource.LocalMedia ? ModernUi.Danger : state == PlaybackChannelState.Failed ? ModernUi.Danger : state == PlaybackChannelState.Idle ? ModernUi.Muted : ModernUi.Success);
        _deviceCard.Update(source == ProgramSource.DeviceCast,
            source == ProgramSource.DeviceCast ? "设备投屏中 · 输出中" : _program == null ? "未连接扩展屏" : "等待设备来投",
            source == ProgramSource.DeviceCast ? ModernUi.Danger : ModernUi.Muted);

        // Error card.
        if (state == PlaybackChannelState.Failed && preview?.LastError is { } error)
        {
            _errorCard.Show(error);
            PreviewSurface.SetOverlayCard(_errorCard);
        }
        else
        {
            PreviewSurface.SetOverlayCard(null);
        }
    }

    private static void SetText(Control control, string text, Color? color)
    {
        if (control.Text != text) control.Text = text;
        if (color is { } c && control.ForeColor != c) control.ForeColor = c;
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _toolTip.Dispose();
            if (_preview != null) _preview.StateChanged -= OnPreviewStateChanged;
            PreviewSurface.ContentSurface.SetFrame(null);
            _placeholder?.Dispose();
            _errorCard.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>One "信号源" card whose highlight and status line follow what is on air.</summary>
    private sealed class SourceCardView
    {
        private readonly Label _state;
        private readonly GlassPanel _thumb;
        private bool _selected;
        public GlassPanel Card { get; }

        public SourceCardView(string title)
        {
            Card = new GlassPanel
            {
                Width = 252, Height = SourceCardHeight, Margin = new Padding(0, 0, 10, 0),
                Padding = new Padding(6, 5, 8, 5), CornerRadius = 9, GlassTint = ModernUi.SurfaceRaised,
            };
            _thumb = new GlassPanel { Dock = DockStyle.Left, Width = 42, CornerRadius = 7, GlassTint = ModernUi.Surface };
            _thumb.Paint += (_, e) =>
            {
                int s = 20;
                var r = new Rectangle((_thumb.Width - s) / 2, (_thumb.Height - s) / 2, s, s);
                VectorIcons.Draw(e.Graphics, NavIcon.Devices, r, _selected ? ModernUi.Accent : ModernUi.Muted);
            };
            var labels = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(10, 0, 0, 0) };
            var name = new Label
            {
                Text = title, Dock = DockStyle.Top, Height = 18, ForeColor = ModernUi.Text,
                Font = new Font("Segoe UI Semibold", 9F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            };
            _state = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            labels.Controls.Add(_state);
            labels.Controls.Add(name);
            Card.Controls.Add(labels);
            Card.Controls.Add(_thumb);
        }

        public void Update(bool selected, string status, Color color)
        {
            SetText(_state, "● " + status, color);
            if (_selected == selected) return;
            _selected = selected;
            Card.GlassTint = selected ? Color.FromArgb(235, 14, 52, 96) : ModernUi.SurfaceRaised;
            _thumb.GlassTint = selected ? Color.FromArgb(205, 12, 46, 92) : ModernUi.Surface;
            Card.Invalidate(true);
        }
    }

    /// <summary>The Preview error card: what failed, where, and what to do about it.</summary>
    private sealed class ErrorCard : Panel
    {
        private readonly Label _title;
        private readonly Label _detail;
        private readonly Label _suggestion;
        private PlaybackError? _error;

        public event Action? Retry;
        public event Action? Skip;
        public event Action<MediaFile>? Remove;
        public event Action<MediaFile>? OpenExternally;

        public ErrorCard()
        {
            BackColor = Color.FromArgb(24, 14, 20);
            Padding = new Padding(14, 8, 14, 8);
            var smallFont = new Font("Segoe UI", 8.5F);
            _title = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(255, 120, 120), Font = new Font("Segoe UI Semibold", 10F), AutoEllipsis = true };
            _detail = new Label { Dock = DockStyle.Top, Height = 18, ForeColor = Color.FromArgb(210, 214, 222), Font = smallFont, AutoEllipsis = true };
            _suggestion = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(160, 170, 186), Font = smallFont, AutoEllipsis = true };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
            actions.Controls.Add(Action("重试", () => Retry?.Invoke(), primary: true));
            actions.Controls.Add(Action("跳过", () => Skip?.Invoke()));
            actions.Controls.Add(Action("从文件库移除", () => { if (_error != null) Remove?.Invoke(_error.File); }));
            actions.Controls.Add(Action("用系统程序打开", () => { if (_error != null) OpenExternally?.Invoke(_error.File); }));
            Controls.Add(_suggestion);
            Controls.Add(actions);
            Controls.Add(_detail);
            Controls.Add(_title);

            static RoundedActionButton Action(string text, Action onClick, bool primary = false)
            {
                var button = new RoundedActionButton { Text = text, AutoSize = false, Width = TextRenderer.MeasureText(text, new Font("Segoe UI", 9F)).Width + 26, Height = 28, Margin = new Padding(0, 2, 8, 0), Padding = Padding.Empty };
                ModernUi.StyleButton(button, primary: primary);
                button.Click += (_, _) => onClick();
                return button;
            }
        }

        public void Show(PlaybackError error)
        {
            if (ReferenceEquals(error, _error)) return;
            _error = error;
            _title.Text = $"⚠ 无法播放：{error.FileName}";
            string codecs = string.Join(" / ", new[] { error.VideoCodec, error.AudioCodec }.Where(c => !string.IsNullOrEmpty(c)));
            _detail.Text = $"失败阶段：{error.Stage}　·　格式：{(error.Extension.Length > 0 ? error.Extension : "未知")}"
                + (codecs.Length > 0 ? $"　·　编码：{codecs}" : "") + $"　·　{error.Message}";
            _suggestion.Text = "建议：" + error.Suggestion;
        }
    }
}

/// <summary>Derives <see cref="ProgramOutputState"/> from what the Terminal knows.</summary>
internal static class ProgramStatus
{
    public static ProgramOutputState Compute(bool displayBound, bool outputActive, PlaybackChannelState? programEngineState)
    {
        if (!displayBound) return ProgramOutputState.Disconnected;
        if (programEngineState == PlaybackChannelState.Failed) return ProgramOutputState.Failed;
        return outputActive ? ProgramOutputState.Live : ProgramOutputState.Standby;
    }
}

/// <summary>A thin rounded track with a knob (progress / volume), click or drag to set.
/// <see cref="ValueCommitted"/> fires on mouse-up; <see cref="ValueChanging"/> while dragging.</summary>
internal sealed class MediaTrack : Control
{
    private float _value;
    public bool IsDragging { get; private set; }
    public event Action<float>? ValueCommitted;
    public event Action<float>? ValueChanging;

    public float Value
    {
        get => _value;
        set { float v = Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f); if (Math.Abs(v - _value) > 0.0005f) { _value = v; Invalidate(); } }
    }

    public MediaTrack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 24;
    }

    private float FractionAt(int x) => Math.Clamp((x - 6) / (float)Math.Max(1, Width - 12), 0f, 1f);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        IsDragging = true;
        Capture = true;
        Value = FractionAt(e.X);
        ValueChanging?.Invoke(Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsDragging) return;
        Value = FractionAt(e.X);
        ValueChanging?.Invoke(Value);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsDragging) return;
        IsDragging = false;
        Capture = false;
        Value = FractionAt(e.X);
        ValueCommitted?.Invoke(Value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        var track = new Rectangle(6, cy - 2, Math.Max(1, Width - 12), 4);
        using (var bg = new SolidBrush(ModernUi.Border)) g.FillRectangle(bg, track);
        if (!Enabled) return;
        int filled = (int)(track.Width * _value);
        using (var fg = new SolidBrush(ModernUi.Accent)) g.FillRectangle(fg, track.X, track.Y, filled, track.Height);
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, track.X + filled - 6, cy - 6, 12, 12);
    }
}
