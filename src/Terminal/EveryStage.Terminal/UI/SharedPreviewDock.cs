using System.Drawing.Drawing2D;
using EveryStage.Rendering;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 「监看」— the right-hand column beside every main page: the Preview monitor and its transport on
/// top, the big 「投到大屏」 button, then what is on the extended display (大屏) and its controls.
///
/// Everything above 「投到大屏」 operates the Preview engine, which renders into <see cref="PreviewSurface"/>
/// inside this column and never touches the extended display. 「投到大屏」 (<see cref="TakeRequested"/>),
/// 暂停大屏 and 「停止输出」 (<see cref="StopProgramRequested"/>) are the only controls that affect
/// Program; MainWindow owns both engines and performs take/stop. The 大屏 section shows Program's status
/// and which source is on air (local media vs. a device cast).
///
/// When the Preview engine fails, an error card covers the monitor with the file name, failure stage,
/// container and codecs, a suggestion, and 重试 / 跳过 / 从文件库移除 / 用系统程序打开.
///
/// Laid out by hand (<see cref="OnLayout"/>), top to bottom at the column's width: the monitor keeps
/// 16:9 and gives up height first when the window is short; the optional footer (the background-music
/// card) shows only while it fits.
/// </summary>
internal sealed class SharedPreviewDock : Panel
{
    private const int MinProgressWidth = 90;
    private const int ButtonHeight = 30;
    private const int SourceCardHeight = 46;
    private const int Gap = 10;
    private const string IconFont = "Segoe MDL2 Assets";

    private readonly OutputStateMachine _stateMachine;
    private readonly Label _title;
    private readonly RoundedActionButton _collapseButton;
    private readonly Badge _previewBadge;
    private readonly Label _previewState;
    private readonly Panel _previewFrame;
    private readonly Badge _programBadge;
    private readonly Label _programState;
    private readonly RoundedActionButton _previousButton;
    private readonly RoundedActionButton _playPauseButton;
    private readonly RoundedActionButton _stopButton;
    private readonly RoundedActionButton _nextButton;
    private readonly Label _timecode;
    private readonly MediaTrack _progress;
    private readonly Label _volumeIcon;
    private readonly MediaTrack _volume;
    private readonly RoundedActionButton _loopButton;
    private readonly RoundedActionButton _scaleButton;
    private readonly RoundedActionButton _takeButton;
    private readonly RoundedActionButton _programPauseButton;
    private readonly RoundedActionButton _stopOutputButton;
    private readonly SourceCardView _localCard;
    private readonly DeviceCastCard _deviceCard;
    private readonly ErrorCard _errorCard;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _toolTip = new();
    private PlaybackEngine? _preview;
    private PlaybackEngine? _program;
    private Bitmap? _placeholder;
    private string? _deviceName;
    private bool _deviceConnected;
    private bool _deviceOnProgram;
    private DocumentControlBar? _documentBar;
    private bool _documentBarShown;
    private Control? _footer;
    private Control? _runControls;

    /// <summary>The Preview channel's output surface (MainWindow builds the Preview engine on it).</summary>
    public PreviewSurface PreviewSurface { get; }

    /// <summary>"投到大屏": put the Preview content on the extended display.</summary>
    public event Action? TakeRequested;

    /// <summary>"停止输出": take whatever is on the extended display off air (Preview is unaffected).</summary>
    public event Action? StopProgramRequested;

    /// <summary>「收起 ›」: MainWindow hides the column and gives the page the full width.</summary>
    public event Action? CollapseRequested;
    public event Action? DeviceTakeRequested;
    public event Action? DeviceRevertRequested;
    public event Action<int, int>? DeviceVideoHostResized;

    /// <summary>Error card "从文件库移除".</summary>
    public event Action<MediaFile>? RemoveFromLibraryRequested;
    public Control DeviceVideoHost => _deviceCard.VideoHost;

    public SharedPreviewDock(OutputStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
        Padding = new Padding(14, 12, 14, 12);
        BackColor = Color.Transparent;
        DoubleBuffered = true;

        var smallFont = new Font(ModernUi.UiFont, 9F);
        _title = new Label { Text = "监看", AutoSize = false, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFontSemibold, 11F), TextAlign = ContentAlignment.MiddleLeft };
        _collapseButton = TextButton("收起 ›", "收起监看栏，文件区占满整个宽度");
        _collapseButton.Font = new Font(ModernUi.UiFont, 8.5F);
        _collapseButton.Click += (_, _) => CollapseRequested?.Invoke();

        _previewBadge = new Badge("预览");
        _previewState = new Label { Text = "待机", ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        _previewFrame = new Panel { BackColor = ModernUi.Border, Padding = new Padding(2) };
        PreviewSurface = new PreviewSurface { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _previewFrame.Controls.Add(PreviewSurface);

        _previousButton = IconButton("", "上一项 / 上一页");
        _playPauseButton = IconButton("", "播放 / 暂停");
        _stopButton = IconButton("", "停止预览");
        _nextButton = IconButton("", "下一项 / 下一页");
        _timecode = new Label { Text = "--:-- / --:--", ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleLeft };
        _progress = new MediaTrack();
        _volumeIcon = new Label { Text = "", Font = new Font(IconFont, 10F), ForeColor = ModernUi.Muted, TextAlign = ContentAlignment.MiddleCenter };
        _volume = new MediaTrack { Value = 1f };
        _loopButton = IconButton("", "循环播放当前文件（不保存到文件设置）");
        _scaleButton = TextButton("满宽", "视频显示：满宽（横向铺满，上下可能裁切）→ 适应（完整显示）→ 填充（铺满裁切）");

        _takeButton = TextButton("投到大屏  ↓", "把预览中的内容投到扩展屏正式输出");
        ModernUi.StyleButton(_takeButton, primary: true);
        _takeButton.Font = new Font(ModernUi.UiFontSemibold, 11F);

        _programBadge = new Badge("大屏") { Danger = true };
        _programState = new Label { Text = "未连接扩展屏", ForeColor = ModernUi.Muted, Font = smallFont, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        _localCard = new SourceCardView("本地媒体");
        _deviceCard = new DeviceCastCard();
        _deviceCard.TakeRequested += () => DeviceTakeRequested?.Invoke();
        _deviceCard.RevertRequested += () => DeviceRevertRequested?.Invoke();
        _deviceCard.VideoHost.SizeChanged += (_, _) => DeviceVideoHostResized?.Invoke(
            Math.Max(1, _deviceCard.VideoHost.ClientSize.Width), Math.Max(1, _deviceCard.VideoHost.ClientSize.Height));
        _programPauseButton = TextButton("暂停大屏", "暂停 / 继续扩展屏上的本地媒体");
        _stopOutputButton = TextButton("停止输出", "停止扩展屏上的正式输出（预览保留）");
        ModernUi.StyleButton(_stopOutputButton, danger: true);

        Controls.AddRange(new Control[]
        {
            _title, _collapseButton, _previewBadge, _previewState, _previewFrame,
            _previousButton, _playPauseButton, _stopButton, _nextButton, _volumeIcon, _volume, _loopButton, _scaleButton,
            _timecode, _progress, _takeButton, _programBadge, _programState, _localCard.Card, _deviceCard.Card,
            _programPauseButton, _stopOutputButton,
        });

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
            _preview.VideoScaleMode = _preview.VideoScaleMode switch
            {
                VideoScaleMode.FitWidth => VideoScaleMode.Fit,
                VideoScaleMode.Fit => VideoScaleMode.Fill,
                _ => VideoScaleMode.FitWidth,
            };
            RefreshNow();
        };
        _takeButton.Click += (_, _) => TakeRequested?.Invoke();
        _programPauseButton.Click += (_, _) =>
        {
            if (_program == null) return;
            if (_program.IsPaused || _program.IsCompleted) _program.Resume(); else if (_program.CanPause) _program.Pause();
            RefreshNow();
        };
        _stopOutputButton.Click += (_, _) => StopProgramRequested?.Invoke();

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        ShowIdleSplash();
        RefreshNow();

        RoundedActionButton IconButton(string glyph, string tip)
        {
            var button = new RoundedActionButton { Text = glyph, Height = ButtonHeight, Margin = Padding.Empty, Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            button.Font = new Font(IconFont, 10F);
            _toolTip.SetToolTip(button, tip);
            return button;
        }

        RoundedActionButton TextButton(string text, string tip)
        {
            var button = new RoundedActionButton { Text = text, Height = ButtonHeight, Margin = Padding.Empty, Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            _toolTip.SetToolTip(button, tip);
            return button;
        }
    }

    /// <summary>The 流程 page's 「下一项 ▶」 controls, shown under the 大屏 section while that page is
    /// visible (null removes them). They take the footer's place.</summary>
    public void SetRunControls(Control? controls)
    {
        if (ReferenceEquals(controls, _runControls)) return;
        if (_runControls != null) Controls.Remove(_runControls);
        _runControls = controls;
        if (controls != null) Controls.Add(controls);
        PerformLayout();
    }

    /// <summary>A control pinned under the 大屏 section (the background-music card), shown while it fits.</summary>
    public void SetFooter(Control footer)
    {
        if (_footer != null) Controls.Remove(_footer);
        _footer = footer;
        Controls.Add(footer);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_stopOutputButton == null) return; // raised from the base constructor / Padding setter
        int S(int v) => LogicalToDeviceUnits(v);
        var area = DisplayRectangle;
        area = new Rectangle(area.Left + S(Padding.Left) - Padding.Left, area.Top + S(Padding.Top) - Padding.Top,
            Math.Max(1, area.Width - (S(Padding.Horizontal) - Padding.Horizontal)), Math.Max(1, area.Height - (S(Padding.Vertical) - Padding.Vertical)));
        int x = area.Left, w = area.Width, gap = S(Gap), button = S(ButtonHeight), label = S(22);

        // Everything but the monitor has a fixed height; the monitor gets 16:9 or what is left.
        int deviceHeight = _deviceConnected ? Math.Max(S(SourceCardHeight), w * 9 / 16) : S(SourceCardHeight);
        int documentHeight = _documentBarShown ? button + S(6) : 0;
        int fixedHeight = S(28) + gap / 2 + label + S(4) + (button + S(6)) * 2 + documentHeight + gap
            + S(44) + gap + label + S(4) + S(SourceCardHeight) + S(6) + deviceHeight + gap + button
            + (_runControls != null ? _runControls.Height + gap : 0);
        int monitor = Math.Clamp(area.Height - fixedHeight, S(90), (w - S(4)) * 9 / 16 + S(4));

        int y = area.Top;
        _collapseButton.Bounds = new Rectangle(area.Right - S(64), y + (S(28) - S(26)) / 2, S(64), S(26));
        _title.Bounds = new Rectangle(x, y, w - S(70), S(28));
        y += S(28) + gap / 2;

        int badgeWidth = _previewBadge.PreferredWidth;
        _previewBadge.Bounds = new Rectangle(x, y + (label - S(18)) / 2, badgeWidth, S(18));
        _previewState.Bounds = new Rectangle(x + badgeWidth + S(6), y, w - badgeWidth - S(6), label);
        y += label + S(4);
        _previewFrame.Bounds = new Rectangle(x, y, w, monitor);
        y += monitor + S(6);

        // Transport row 1: ⏮ ⏯ ⏹ ⏭ · volume · loop · fit.
        int icon = S(34), step = icon + S(4);
        var row = new[] { _previousButton, _playPauseButton, _stopButton, _nextButton };
        for (int i = 0; i < row.Length; i++) row[i].Bounds = new Rectangle(x + i * step, y, icon, button);
        int scaleWidth = S(52);
        _scaleButton.Bounds = new Rectangle(area.Right - scaleWidth, y, scaleWidth, button);
        _loopButton.Bounds = new Rectangle(_scaleButton.Left - S(4) - icon, y, icon, button);
        int volumeLeft = x + row.Length * step + S(2);
        int volumeRight = _loopButton.Left - S(6);
        bool volumeFits = volumeRight - volumeLeft >= S(70);
        _volumeIcon.Visible = _volume.Visible = volumeFits;
        _volumeIcon.Bounds = new Rectangle(volumeLeft, y, S(22), button);
        _volume.Bounds = new Rectangle(volumeLeft + S(22), y + (button - S(24)) / 2, Math.Max(1, volumeRight - volumeLeft - S(22)), S(24));
        y += button + S(6);

        // Transport row 2: timecode · progress.
        int timeWidth = TextRenderer.MeasureText("00:00:00 / 00:00:00", _timecode.Font).Width;
        bool timeFits = w - timeWidth >= S(MinProgressWidth);
        _timecode.Visible = timeFits;
        _timecode.Bounds = new Rectangle(x, y, timeWidth, button);
        int progressLeft = timeFits ? x + timeWidth + S(4) : x;
        _progress.Bounds = new Rectangle(progressLeft, y + (button - S(24)) / 2, Math.Max(1, area.Right - progressLeft), S(24));
        y += button + S(6);

        if (_documentBar != null)
        {
            _documentBar.Bounds = new Rectangle(x, y, w, Math.Max(1, documentHeight));
            y += documentHeight;
        }
        y += gap;
        _takeButton.Bounds = new Rectangle(x, y, w, S(44));
        y += S(44) + gap;

        badgeWidth = _programBadge.PreferredWidth;
        _programBadge.Bounds = new Rectangle(x, y + (label - S(18)) / 2, badgeWidth, S(18));
        _programState.Bounds = new Rectangle(x + badgeWidth + S(6), y, w - badgeWidth - S(6), label);
        y += label + S(4);
        _localCard.Card.Bounds = new Rectangle(x, y, w, S(SourceCardHeight));
        y += S(SourceCardHeight) + S(6);
        _deviceCard.Card.Bounds = new Rectangle(x, y, w, deviceHeight);
        y += deviceHeight + gap;
        int half = (w - S(8)) / 2;
        _programPauseButton.Bounds = new Rectangle(x, y, half, button);
        _stopOutputButton.Bounds = new Rectangle(x + half + S(8), y, w - half - S(8), button);
        y += button + gap;

        if (_runControls != null)
        {
            _runControls.Width = w; // its own layout sets the height for this width
            _runControls.Bounds = new Rectangle(x, y, w, _runControls.Height);
            y += _runControls.Height + gap;
        }

        if (_footer != null)
        {
            int footerHeight = _footer.Height;
            _footer.Visible = _runControls == null && area.Bottom - y >= footerHeight;
            _footer.Bounds = new Rectangle(x, area.Bottom - footerHeight, w, footerHeight);
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

    public void UpdateDeviceCast(string? deviceName, bool connected, bool onProgram)
    {
        _deviceName = deviceName;
        bool relayout = _deviceConnected != connected;
        _deviceConnected = connected;
        _deviceOnProgram = onProgram;
        RefreshNow();
        if (relayout) PerformLayout();
    }

    private void OnPreviewStateChanged(PlaybackChannelState state)
    {
        if (state == PlaybackChannelState.Idle) ShowIdleSplash();
        RefreshNow();
    }

    /// <summary>Idle monitor shows the branded splash rather than a bare dark box.</summary>
    private void ShowIdleSplash()
    {
        _placeholder ??= PreviewPlaceholder.Create(800, 450); // wide splash: the preview fills its width
        PreviewSurface.ShowImageSurface();
        PreviewSurface.ContentSurface.SetFrame(_placeholder);
    }

    internal void RefreshTheme() => RefreshNow();

    /// <summary>Adds the document control row (PDF / Word / presentation controls and Program sync).</summary>
    public void BindDocumentSync(DocumentProgramSync sync)
    {
        _documentBar = new DocumentControlBar(sync, _toolTip, ButtonHeight) { Visible = false };
        _documentBar.StopProgramRequested += () => StopProgramRequested?.Invoke();
        _documentBar.Changed += RefreshNow;
        Controls.Add(_documentBar);
        sync.Preview.DocumentChanged += RefreshNow;
        RefreshNow();
    }

    private void RefreshDocumentBar()
    {
        if (_documentBar == null) return;
        _documentBar.RefreshState();
        bool show = _documentBar.HasDocument;
        if (show == _documentBarShown) return;
        _documentBarShown = show;
        _documentBar.Visible = show;
        PerformLayout();
    }

    private void RefreshNow()
    {
        if (IsDisposed) return;
        RefreshDocumentBar();
        var preview = _preview;
        var file = preview?.CurrentFile;
        var state = preview?.State ?? PlaybackChannelState.Idle;
        bool programLive = _stateMachine.State == OutputState.Active;
        var source = programLive ? _stateMachine.Source : ProgramSource.None;

        // 「预览」 line: file + state.
        string fileLabel = file == null ? "" : Path.GetFileName(file.SourcePath) + (file.IsBackgroundAudio ? "（背景音乐）" : "") + " · ";
        (string text, Color color) = state switch
        {
            PlaybackChannelState.Loading => (fileLabel + "加载中", ModernUi.Warning),
            PlaybackChannelState.Playing => (fileLabel + "预览中", ModernUi.Muted),
            PlaybackChannelState.Paused when preview!.IsCompleted => (fileLabel + "播放完成", ModernUi.Muted),
            PlaybackChannelState.Paused => (fileLabel + "已暂停", ModernUi.Warning),
            PlaybackChannelState.Failed => (fileLabel + "播放失败", ModernUi.Danger),
            _ => ("待机 · 单击文件在此预览", ModernUi.Muted),
        };
        if (preview?.CurrentContentNote is { } note && state == PlaybackChannelState.Playing) text += "（" + note + "）";
        if (preview?.Muted == true && state is PlaybackChannelState.Playing or PlaybackChannelState.Paused) text += " · 预监静音";
        SetText(_previewState, text, color);
        bool previewHasContent = state is not (PlaybackChannelState.Idle or PlaybackChannelState.Failed);
        var frameColor = previewHasContent ? ModernUi.Accent : ModernUi.Border;
        if (_previewFrame.BackColor != frameColor) _previewFrame.BackColor = frameColor;
        _previewBadge.Active = previewHasContent;

        // Program status (independent of Preview).
        var programState = ProgramStatus.Compute(_program != null, programLive, _program?.State);
        (string programText, Color programColor) = programState switch
        {
            ProgramOutputState.Disconnected => ("未连接扩展屏 · 仅本地预览", ModernUi.Muted),
            ProgramOutputState.Failed => ("播放失败", ModernUi.Danger),
            ProgramOutputState.Live when source == ProgramSource.DeviceCast => ("输出中 · 设备来投", ModernUi.Text),
            ProgramOutputState.Live => ((_program?.CurrentFile is { } pf ? Path.GetFileName(pf.SourcePath) : "本地媒体")
                + (_program?.Position is { } pp && _program.CanSeek ? " · " + Format(pp) : "") + (_program?.IsPaused == true ? " · 已暂停" : ""), ModernUi.Text),
            _ => (_stateMachine.CastSwitchOn ? "待机 · 没有输出" : "待机（投屏开关已关闭）", ModernUi.Muted),
        };
        SetText(_programState, programText, programColor);
        _programBadge.Active = programState == ProgramOutputState.Live;

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
        SetText(_scaleButton, preview?.VideoScaleMode switch { VideoScaleMode.Fill => "填充", VideoScaleMode.Fit => "适应", _ => "满宽" }, null);

        // Disabled (not just a warning on click) while no extended display is bound.
        _takeButton.Enabled = _program != null && hasFile && state != PlaybackChannelState.Failed && state != PlaybackChannelState.Loading;
        _stopOutputButton.Enabled = programLive;
        bool programLocal = programLive && source == ProgramSource.LocalMedia && _program != null;
        _programPauseButton.Enabled = programLocal && (_program!.CanPause || _program.IsPaused || _program.IsCompleted);
        SetText(_programPauseButton, programLocal && (_program!.IsPaused || _program.IsCompleted) ? "继续大屏" : "暂停大屏", null);

        // Source cards: which source is on air.
        string localStatus = state switch
        {
            _ when source == ProgramSource.LocalMedia => "输出中",
            PlaybackChannelState.Idle => "待机",
            PlaybackChannelState.Failed => "预览失败 · 未投屏",
            _ => "预览中 · 未投屏",
        };
        _localCard.Update(source == ProgramSource.LocalMedia, localStatus,
            source == ProgramSource.LocalMedia ? ModernUi.Danger : state == PlaybackChannelState.Failed ? ModernUi.Danger : state == PlaybackChannelState.Idle ? ModernUi.Muted : ModernUi.Success);
        _deviceCard.Update(_deviceConnected, _deviceName ?? "设备来投", _deviceOnProgram,
            _program == null ? "未连接扩展屏" : "等待设备来投", _program != null);

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
                Height = SourceCardHeight, Margin = Padding.Empty,
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
                Font = new Font(ModernUi.UiFontSemibold, 9F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            };
            _state = new Label { Dock = DockStyle.Fill, Font = new Font(ModernUi.UiFont, 8F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            labels.Controls.Add(_state);
            labels.Controls.Add(name);
            Card.Controls.Add(labels);
            Card.Controls.Add(_thumb);
        }

        public void Update(bool selected, string status, Color color)
        {
            SetText(_state, "● " + status, color);
            _selected = selected;
            Card.GlassTint = selected
                ? Color.FromArgb(202, Panels.ActivityRowView.Blend(ModernUi.Palette.GlassTint, ModernUi.Accent, 0.19f))
                : ModernUi.Palette.GlassTint;
            _thumb.GlassTint = selected
                ? Color.FromArgb(202, Panels.ActivityRowView.Blend(ModernUi.Palette.GlassTint, ModernUi.Accent, 0.11f))
                : ModernUi.Palette.GlassTint;
            Card.Invalidate(true);
        }
    }

    private sealed class DeviceCastCard
    {
        private readonly Label _title;
        private readonly Label _state;
        private readonly Label _caption;
        private readonly RoundedActionButton _actionButton;
        private readonly Panel _videoFrame;
        private readonly Panel _labels;
        private bool _onProgram;

        public GlassPanel Card { get; }
        public Control VideoHost { get; }
        public event Action? TakeRequested;
        public event Action? RevertRequested;

        public DeviceCastCard()
        {
            Card = new GlassPanel
            {
                Height = SourceCardHeight, Margin = Padding.Empty,
                Padding = new Padding(6, 5, 8, 5), CornerRadius = 9, GlassTint = ModernUi.SurfaceRaised,
            };

            _videoFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
            VideoHost = new Control { Dock = DockStyle.Fill, BackColor = Color.Black };
            _ = VideoHost.Handle;
            _caption = new Label
            {
                Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(210, 12, 29, 49),
                ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFont, 8.5F),
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 76, 0),
                AutoEllipsis = true,
            };
            _actionButton = new RoundedActionButton
            {
                Text = "投屏", Width = 62, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            ModernUi.StyleButton(_actionButton, primary: true);
            _actionButton.Click += (_, _) =>
            {
                if (_onProgram) RevertRequested?.Invoke(); else TakeRequested?.Invoke();
            };
            _videoFrame.Controls.Add(VideoHost);
            _videoFrame.Controls.Add(_caption);
            _videoFrame.Controls.Add(_actionButton);
            _videoFrame.Resize += (_, _) => _actionButton.Location = new Point(
                Math.Max(4, _videoFrame.ClientSize.Width - _actionButton.Width - 8),
                Math.Max(4, _videoFrame.ClientSize.Height - _actionButton.Height - 8));

            _labels = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(6, 0, 0, 0) };
            _title = new Label
            {
                Text = "设备来投", Dock = DockStyle.Top, Height = 19, ForeColor = ModernUi.Text,
                Font = new Font(ModernUi.UiFontSemibold, 9F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            };
            _state = new Label { Dock = DockStyle.Fill, Font = new Font(ModernUi.UiFont, 8F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _labels.Controls.Add(_state);
            _labels.Controls.Add(_title);
            Card.Controls.Add(_labels);
            Card.Controls.Add(_videoFrame);
        }

        public void Update(bool connected, string title, bool onProgram, string idleStatus, bool displayBound)
        {
            _onProgram = onProgram;
            _videoFrame.Visible = connected;
            _labels.Visible = !connected;
            _title.Text = connected ? title : "设备来投";
            _caption.Text = connected
                ? $"{title} · {(onProgram ? "大屏中" : "预览中")}"
                : "";
            _state.Text = connected
                ? (onProgram ? "● 大屏输出中" : "● 预览中（未上屏）")
                : "● " + idleStatus;
            _state.ForeColor = connected ? (onProgram ? ModernUi.Danger : ModernUi.Success) : ModernUi.Muted;
            _actionButton.Text = onProgram ? "回退" : "投屏";
            _actionButton.Enabled = connected && (displayBound || onProgram);
            ModernUi.StyleButton(_actionButton, primary: !onProgram, danger: onProgram);
            _actionButton.BringToFront();
            Card.GlassTint = onProgram
                ? Color.FromArgb(202, Panels.ActivityRowView.Blend(ModernUi.Palette.GlassTint, ModernUi.Accent, 0.19f))
                : ModernUi.Palette.GlassTint;
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
            var smallFont = new Font(ModernUi.UiFont, 8.5F);
            _title = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(255, 120, 120), Font = new Font(ModernUi.UiFontSemibold, 10F), AutoEllipsis = true };
            _detail = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = Color.FromArgb(210, 214, 222), Font = smallFont, AutoEllipsis = true };
            _suggestion = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(160, 170, 186), Font = smallFont, AutoEllipsis = true };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty };
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
                var button = new RoundedActionButton { Text = text, AutoSize = false, Width = TextRenderer.MeasureText(text, new Font(ModernUi.UiFont, 9F)).Width + 26, Height = 28, Margin = new Padding(0, 2, 8, 0), Padding = Padding.Empty };
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

/// <summary>The small 「预览」 / 「大屏」 tag over a monitor: filled in the channel colour (accent for
/// Preview, danger for Program) while that channel shows something, outlined and muted otherwise.</summary>
internal sealed class Badge : Control
{
    private bool _active;
    public bool Danger { get; init; }

    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    public Badge(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = new Font(ModernUi.UiFontSemibold, 8F);
    }

    public int PreferredWidth => TextRenderer.MeasureText(Text, Font).Width + LogicalToDeviceUnits(8);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int d = LogicalToDeviceUnits(8);
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Color channel = Danger ? ModernUi.Danger : ModernUi.Accent;
        Color fore;
        if (_active)
        {
            using var fill = new SolidBrush(channel);
            g.FillPath(fill, path);
            fore = Danger ? Color.White : ModernUi.AccentForeground;
        }
        else
        {
            using var pen = new Pen(ModernUi.Border);
            g.DrawPath(pen, path);
            fore = ModernUi.Muted;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
