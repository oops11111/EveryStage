using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2 的「音频播放区」播放条，按效果图是<b>一整条</b>细长播放器：音符图标 · 名称 · 播放键 ·
/// 时间 · 进度条 · 音量 · 「☑ 循环播放」· 右侧「＋ 加入活动音轨」按钮。
///
/// 常驻显示：文件库里没有音频时不隐藏，而是显示同高度的占位条（「暂无音频 · 导入 MP3 / WAV / AAC 等作为
/// 背景音乐」），右侧按钮变为「导入音频」，布局不随文件库内容跳动。
/// <list type="bullet">
/// <item>播放/暂停走 <see cref="PlaybackEngine.Pause"/>/<see cref="PlaybackEngine.Resume"/>（仅当
///   当前条对应的文件正是 <see cref="PlaybackEngine.CurrentFile"/> 时有意义）；未载入时 raise
///   <see cref="PlayRequested"/>，在预监里用 EveryStage 自己的音频控制器播放。</item>
/// <item>音量滑块走 <see cref="PlaybackEngine.AudioVolume"/>（引擎级单值）。</item>
/// <item>「循环播放」复选框改 <see cref="MediaFile.OnCompletion"/> 并持久化。</item>
/// <item>「加入活动音轨」raise <see cref="AddToActivityTrackRequested"/>，由 FilesPanel 选活动后把这首
///   音频作为背景音乐（<see cref="MediaFile.IsBackgroundAudio"/>）加入。</item>
/// </list>
/// 「当前条」绑定的文件：操作员在文件名选择器（「背景音乐.mp3 ▾」）里选的那首；没选过时是正在播放的
/// 音频，否则是列表第一首。某首音频<b>新开始</b>播放时，播放条跟过去。进度/时长/音量/播放态由外部计时器
/// 每 500ms 调 <see cref="RefreshLiveState"/> 刷新，每次刷新后 raise <see cref="StateChanged"/>——
/// 侧栏的「音频独立播放区」卡片（<see cref="AudioSidebarCard"/>）是这条播放条的第二个视图，读它的状态、
/// 调它的方法，两处控制的永远是同一首。
/// </summary>
internal sealed class AudioPlayerBar : GlassPanel
{
    private const string AddToTrackText = "加入活动音轨";
    private const string ImportText = "导入音频";
    private const string LoopText = "循环播放";
    private const string EmptyText = "暂无音频 · 导入 MP3 / WAV / AAC 等作为背景音乐";
    private const string AddGlyph = "\uECC8";    // Segoe MDL2 AddTo (circled plus)
    private const string ImportGlyph = "\uE898"; // Segoe MDL2 Upload

    private readonly FileOperationLogger _fileOpLog;
    private readonly FileLibraryStore _library;
    private PlaybackEngine? _playback;

    private MediaFile? _boundFile;
    private readonly List<MediaFile> _audioFiles = new();
    // The operator's pick from the file-name selector; follows a newly started track (see RefreshLiveState).
    private MediaFile? _selectedFile;
    private MediaFile? _lastPlayingFile;
    private ContextMenuStrip? _filePicker;

    private Rectangle _playButton;
    private Rectangle _progressTrack;
    private Rectangle _volumeTrack;
    // The whole「☑ 循环播放」hit area: checkbox plus its label (the label is dropped when narrow).
    private Rectangle _loopButton;
    private Rectangle _loopBox;
    private bool _showLoopLabel;
    private readonly RoundedActionButton _actionButton;
    private bool _compact;
    private Rectangle _iconTile;
    private Rectangle _titleRect;
    private Rectangle _timeRect;
    private Rectangle _volumeIcon;

    /// <summary>Raised by the right-hand「＋ 加入活动音轨」button: FilesPanel picks an activity and adds
    /// a copy of this file to it as background audio.</summary>
    public event Action<MediaFile>? AddToActivityTrackRequested;

    /// <summary>Raised by the empty-state「导入音频」button (same position as「加入活动音轨」).</summary>
    public event Action? ImportRequested;

    /// <summary>Raised by the play button when the bound file isn't loaded yet: play it in the
    /// Preview monitor with EveryStage's own audio controller (never an external player).</summary>
    public event Action<MediaFile>? PlayRequested;

    /// <summary>Raised after every live refresh and every state change made through this bar.</summary>
    public event Action? StateChanged;

    /// <summary>File-name selector「打开文件所在位置」.</summary>
    public event Action<MediaFile>? OpenLocationRequested;

    /// <summary>File-name selector「从文件库移除」.</summary>
    public event Action<MediaFile>? RemoveRequested;

    public AudioPlayerBar(FileLibraryStore library, FileOperationLogger fileOpLog, PlaybackEngine? playback)
    {
        _library = library;
        _fileOpLog = fileOpLog;
        _playback = playback;

        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "音频播放器";
        AccessibleDescription = "空格播放或暂停，左右键跳转十秒，上下键调节音量，L 切换循环。";
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();

        Dock = DockStyle.Top;
        Height = 64;
        Margin = new Padding(0);
        CornerRadius = 12;
        GlassTint = ModernUi.Palette.GlassTint;

        _actionButton = new RoundedActionButton { Text = ImportText, IconGlyph = ImportGlyph, Size = new Size(132, 34) };
        ModernUi.StyleButton(_actionButton, primary: true);
        _actionButton.Click += (_, _) =>
        {
            if (_boundFile != null) AddToActivityTrackRequested?.Invoke(_boundFile);
            else ImportRequested?.Invoke();
        };
        Controls.Add(_actionButton);

        MouseDown += OnMouseDownHandler;
        Resize += (_, _) => LayoutControls();
    }

    public void AttachPlaybackEngine(PlaybackEngine playback) => _playback = playback;

    /// <summary>Rebinds the bar to the current set of audio files (called from FilesPanel.Refresh_).
    /// With none, the bar stays visible and shows its import placeholder.</summary>
    public void SetAudioFiles(IReadOnlyList<MediaFile> audioFiles)
    {
        _audioFiles.Clear();
        _audioFiles.AddRange(audioFiles);
        RefreshLiveState();
    }

    /// <summary>Per-tick live update (play/pause, position, volume). Also re-picks the bound file:
    /// the operator's selection, which a newly started track takes over.</summary>
    public void RefreshLiveState()
    {
        var current = _playback?.CurrentFile;
        var playing = current?.Kind == MediaKind.Audio ? _audioFiles.FirstOrDefault(f => SameSource(f, current)) : null;
        if (playing != null && (_lastPlayingFile == null || !SameSource(playing, _lastPlayingFile))) _selectedFile = playing;
        _lastPlayingFile = playing;
        if (_selectedFile != null) _selectedFile = _audioFiles.FirstOrDefault(f => f.Id == _selectedFile.Id);
        _boundFile = _selectedFile ?? playing ?? _audioFiles.FirstOrDefault();
        string buttonText = _boundFile == null ? ImportText : AddToTrackText;
        if (_actionButton.Text != buttonText)
        {
            _actionButton.Text = buttonText;
            _actionButton.IconGlyph = _boundFile == null ? ImportGlyph : AddGlyph;
            _actionButton.Invalidate();
        }
        LayoutControls();
        Invalidate();
        StateChanged?.Invoke();
    }

    // ── State and commands shared with the sidebar「音频独立播放区」card ──

    public MediaFile? BoundFile => _boundFile;
    public IReadOnlyList<MediaFile> AudioFiles => _audioFiles;
    public bool IsPlayingNow => IsBoundPlaying && _playback?.IsPaused == false;
    public bool IsLooping => _boundFile?.OnCompletion == CompletionAction.Loop;

    /// <summary>Switches the bar to <paramref name="file"/> (from the file-name selector). Playback is
    /// not touched: the play button then starts the newly selected track.</summary>
    public void SelectFile(MediaFile file)
    {
        _selectedFile = file;
        RefreshLiveState();
    }

    public void TogglePlayPause()
    {
        if (_boundFile == null) return;
        if (_playback != null && IsBoundPlaying)
        {
            if (_playback.IsPaused) _playback.Resume(); else _playback.Pause();
        }
        else PlayRequested?.Invoke(_boundFile);
        RefreshLiveState();
    }

    /// <summary>Plays the bound track from the beginning (the sidebar card's ↻).</summary>
    public void Restart()
    {
        if (_boundFile == null) return;
        if (_playback != null && IsBoundPlaying)
        {
            _playback.SeekAudioRelative(-_playback.AudioPosition);
            if (_playback.IsPaused) _playback.Resume();
        }
        else PlayRequested?.Invoke(_boundFile);
        RefreshLiveState();
    }

    public void SeekToFraction(double fraction)
    {
        if (!IsBoundPlaying || _playback?.AudioDuration is not { } duration) return;
        fraction = Math.Clamp(fraction, 0, 1);
        _playback.SeekAudioRelative(TimeSpan.FromSeconds(duration.TotalSeconds * fraction) - _playback.AudioPosition);
        RefreshLiveState();
    }

    public void SetVolume(float fraction)
    {
        if (_playback == null || !IsBoundPlaying) return;
        _playback.AudioVolume = Math.Clamp(fraction, 0f, 1f);
        RefreshLiveState();
    }

    public void ToggleLoop()
    {
        if (_boundFile == null) return;
        var newValue = _boundFile.OnCompletion == CompletionAction.Loop ? CompletionAction.NextItem : CompletionAction.Loop;
        var oldValue = _boundFile.OnCompletion;
        _boundFile.OnCompletion = newValue;
        try { _library.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _boundFile.OnCompletion = oldValue;
            MessageBox.Show(FindForm(), $"循环设置未保存：{ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshLiveState();
            return;
        }
        if (IsBoundPlaying && _playback?.CurrentFile is { } current) current.OnCompletion = newValue;
        _fileOpLog.LogPlaybackPropertyChanged(_boundFile.Id, nameof(MediaFile.OnCompletion), oldValue.ToString(), newValue.ToString());
        RefreshLiveState();
    }

    public void RequestImport() => ImportRequested?.Invoke();

    /// <summary>The「背景音乐.mp3 ▾」selector: every library audio file (current one checked), then
    /// 导入音频 / 打开文件所在位置 / 从文件库移除.</summary>
    public void ShowFilePicker(Control owner, Point location)
    {
        _filePicker?.Dispose();
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = ModernUi.Surface,
            ForeColor = ModernUi.Text,
            Renderer = new ThemedMenuRenderer(),
        };
        foreach (var file in _audioFiles)
        {
            var item = new ToolStripMenuItem(Path.GetFileName(file.SourcePath))
            {
                Checked = _boundFile != null && file.Id == _boundFile.Id,
                ForeColor = ModernUi.Text,
            };
            item.Click += (_, _) => SelectFile(file);
            menu.Items.Add(item);
        }
        if (_audioFiles.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("导入音频...", null, (_, _) => RequestImport()) { ForeColor = ModernUi.Text });
        if (_boundFile is { } bound)
        {
            menu.Items.Add(new ToolStripMenuItem("打开文件所在位置", null, (_, _) => OpenLocationRequested?.Invoke(bound)) { ForeColor = ModernUi.Text });
            menu.Items.Add(new ToolStripMenuItem("从文件库移除", null, (_, _) => RemoveRequested?.Invoke(bound)) { ForeColor = ModernUi.Text });
        }
        _filePicker = menu;
        menu.Show(owner, location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _filePicker?.Dispose();
        base.Dispose(disposing);
    }

    private static bool SameSource(MediaFile left, MediaFile right) =>
        left.Kind == right.Kind && string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase);

    private bool IsBoundPlaying => _boundFile != null && _playback?.CurrentFile is { } c && SameSource(c, _boundFile);

    private void LayoutControls()
    {
        int S(int value) => LogicalToDeviceUnits(value);
        // 对齐效果图：宽度足够时是「一整条」单行（音符图标 · 名称 · 播放 · 时间 · 进度 · 音量 · ☑ 循环播放 ·
        // 加入活动音轨），约 64px 高；放不下时退化为两行（约 84px）。空状态始终是单行占位条。
        _compact = _boundFile != null && ClientSize.Width < S(800);
        int desiredHeight = S(_compact ? 84 : 64);
        if (Height != desiredHeight) Height = desiredHeight;
        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = S(16);
        _actionButton.Size = new Size(S(132), S(34));

        if (_boundFile == null)
        {
            int cy = h / 2;
            _iconTile = new Rectangle(pad, cy - S(20), S(40), S(40));
            _actionButton.Location = new Point(w - pad - _actionButton.Width, cy - _actionButton.Height / 2);
            _titleRect = new Rectangle(_iconTile.Right + S(12), cy - S(10),
                Math.Max(S(20), _actionButton.Left - S(16) - _iconTile.Right - S(12)), S(20));
            _playButton = _progressTrack = _volumeTrack = _volumeIcon = _loopButton = _loopBox = _timeRect = Rectangle.Empty;
            return;
        }

        int row = h / 2;
        if (!_compact)
        {
            _iconTile = new Rectangle(pad, row - S(20), S(40), S(40));
            _titleRect = new Rectangle(_iconTile.Right + S(12), row - S(10), S(120), S(20));
            _playButton = new Rectangle(_titleRect.Right + S(8), row - S(17), S(34), S(34));
            _timeRect = new Rectangle(_playButton.Right + S(8), row - S(9), S(96), S(18));
        }
        else
        {
            int row1 = S(22);
            row = S(60);
            _iconTile = new Rectangle(pad, row1 - S(14), S(28), S(28));
            _timeRect = new Rectangle(w - pad - S(96), row1 - S(9), S(96), S(18));
            _titleRect = new Rectangle(_iconTile.Right + S(10), row1 - S(10),
                Math.Max(S(20), _timeRect.Left - S(8) - _iconTile.Right - S(10)), S(20));
            _playButton = new Rectangle(pad, row - S(17), S(34), S(34));
        }

        // Right to left: 加入活动音轨 · ☑ 循环播放 · 音量; the progress track takes what is left.
        _actionButton.Location = new Point(w - pad - _actionButton.Width, row - _actionButton.Height / 2);
        _showLoopLabel = !_compact || w >= S(520);
        int loopWidth = S(18);
        if (_showLoopLabel)
            using (var loopFont = new Font("Segoe UI", 9F))
                loopWidth = S(24) + TextRenderer.MeasureText(LoopText, loopFont).Width;
        _loopButton = new Rectangle(_actionButton.Left - S(16) - loopWidth, row - S(12), loopWidth, S(24));
        _loopBox = new Rectangle(_loopButton.Left + S(1), row - S(8), S(16), S(16));
        int volWidth = !_compact ? (w >= S(900) ? S(100) : S(64)) : Math.Clamp((w - S(360)) / 3, S(28), S(72));
        _volumeTrack = new Rectangle(_loopButton.Left - S(18) - volWidth, row - S(2), volWidth, S(4));
        _volumeIcon = new Rectangle(_volumeTrack.Left - S(30), row - S(12), S(24), S(24));
        int progLeft = (_compact ? _playButton.Right : _timeRect.Right) + S(12);
        _progressTrack = new Rectangle(progLeft, row - S(2), Math.Max(S(20), _volumeIcon.Left - S(14) - progLeft), S(4));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int S(int value) => LogicalToDeviceUnits(value);

        // Icon tile（效果图左侧的小方块音符图标）。
        using (var tilePath = RoundedBox(_iconTile, S(9)))
        using (var tileBg = new LinearGradientBrush(_iconTile, Color.FromArgb(22, 56, 100), Color.FromArgb(14, 40, 74), 60F))
            g.FillPath(tileBg, tilePath);
        using (var noteFont = new Font("Segoe UI Symbol", _iconTile.Height * 0.46f, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "♫", noteFont, _iconTile, Color.FromArgb(188, 214, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (_boundFile == null)
        {
            using var emptyFont = new Font("Segoe UI", 9.5F);
            TextRenderer.DrawText(g, EmptyText, emptyFont, _titleRect, ModernUi.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        // File name doubles as the track selector:「背景音乐.mp3 ▾」.
        using (var titleFont = new Font("Segoe UI Semibold", 10.5F))
        {
            var nameRect = Rectangle.FromLTRB(_titleRect.Left, _titleRect.Top, _titleRect.Right - S(16), _titleRect.Bottom);
            string name = Path.GetFileName(_boundFile.SourcePath);
            TextRenderer.DrawText(g, name, titleFont, nameRect, ModernUi.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int nameWidth = Math.Min(nameRect.Width, TextRenderer.MeasureText(g, name, titleFont, nameRect.Size, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width);
            DrawChevron(g, new Point(nameRect.Left + nameWidth + S(12), _titleRect.Top + _titleRect.Height / 2), ModernUi.Muted);
        }
        using (var timeFont = new Font("Segoe UI", 9F))
            TextRenderer.DrawText(g, PositionText(), timeFont, _timeRect, ModernUi.Muted,
                (_compact ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter);

        // Play/pause — a bare white glyph, as in the reference (no button chrome).
        using (var glyph = new SolidBrush(ModernUi.Text))
        {
            int gcx = _playButton.Left + _playButton.Width / 2, gcy = _playButton.Top + _playButton.Height / 2;
            if (IsBoundPlaying && _playback?.IsPaused == false)
            {
                int bw = S(5), bh = S(18), gap = S(5);
                g.FillRectangle(glyph, gcx - bw - gap / 2, gcy - bh / 2, bw, bh);
                g.FillRectangle(glyph, gcx + gap / 2, gcy - bh / 2, bw, bh);
            }
            else
            {
                float x = gcx + S(1);
                g.FillPolygon(glyph, new[] { new PointF(x - S(7), gcy - S(9)), new PointF(x - S(7), gcy + S(9)), new PointF(x + S(8), gcy) });
            }
        }

        // Progress track + filled portion + knob.
        DrawTrack(g, _progressTrack, ProgressFraction(), true);

        // Volume icon + track.
        DrawVolumeIcon(g, _volumeIcon);
        DrawTrack(g, _volumeTrack, VolumeFraction(), true);

        DrawLoopCheckbox(g, _boundFile.OnCompletion == CompletionAction.Loop);
    }

    private void DrawTrack(Graphics g, Rectangle track, float fraction, bool knob)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        using (var bg = new SolidBrush(ModernUi.Border))
        using (var bgPath = Rounded(track, track.Height / 2))
            g.FillPath(bg, bgPath);
        var filled = new Rectangle(track.Left, track.Top, (int)(track.Width * fraction), track.Height);
        if (filled.Width > 2)
            using (var fg = new SolidBrush(ModernUi.Accent))
            using (var fgPath = Rounded(filled, track.Height / 2))
                g.FillPath(fg, fgPath);
        if (knob)
        {
            int kx = track.Left + (int)(track.Width * fraction);
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx - 6, track.Top + track.Height / 2 - 6, 12, 12);
        }
    }

    private void DrawVolumeIcon(Graphics g, Rectangle r)
    {
        using var pen = new Pen(ModernUi.Muted, 2F) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(ModernUi.Muted);
        var speaker = new[]
        {
            new PointF(r.Left + 2, r.Top + r.Height * 0.38f),
            new PointF(r.Left + 6, r.Top + r.Height * 0.38f),
            new PointF(r.Left + 11, r.Top + r.Height * 0.22f),
            new PointF(r.Left + 11, r.Top + r.Height * 0.78f),
            new PointF(r.Left + 6, r.Top + r.Height * 0.62f),
            new PointF(r.Left + 2, r.Top + r.Height * 0.62f),
        };
        g.FillPolygon(fill, speaker);
        g.DrawArc(pen, r.Left + 11, r.Top + 5, 10, r.Height - 10, -55, 110);
    }

    /// <summary>效果图的「☑ 循环播放」：描边方框（循环时打勾）加文字标签。</summary>
    private void DrawLoopCheckbox(Graphics g, bool active)
    {
        int S(int value) => LogicalToDeviceUnits(value);
        using (var boxPath = RoundedBox(_loopBox, S(3)))
        using (var boxPen = new Pen(active ? ModernUi.Text : ModernUi.Muted, 1.4F))
            g.DrawPath(boxPen, boxPath);
        if (active)
        {
            using var check = new Pen(ModernUi.Text, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var b = _loopBox;
            g.DrawLines(check, new[]
            {
                new PointF(b.Left + b.Width * 0.24f, b.Top + b.Height * 0.52f),
                new PointF(b.Left + b.Width * 0.44f, b.Top + b.Height * 0.72f),
                new PointF(b.Left + b.Width * 0.78f, b.Top + b.Height * 0.30f),
            });
        }
        if (!_showLoopLabel) return;
        var labelRect = Rectangle.FromLTRB(_loopBox.Right + S(6), _loopButton.Top, _loopButton.Right, _loopButton.Bottom);
        using var labelFont = new Font("Segoe UI", 9F);
        TextRenderer.DrawText(g, LoopText, labelFont, labelRect, ModernUi.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public float ProgressFraction()
    {
        if (!IsBoundPlaying || _playback == null) return 0f;
        var dur = _playback.AudioDuration;
        if (dur is not { } d || d.TotalSeconds <= 0) return 0f;
        return (float)(_playback.AudioPosition.TotalSeconds / d.TotalSeconds);
    }

    public float VolumeFraction() => IsBoundPlaying && _playback != null ? Math.Clamp(_playback.AudioVolume, 0f, 1f) : 0.7f;

    public string PositionText()
    {
        if (!IsBoundPlaying || _playback == null) return "--:-- / --:--";
        string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
        var pos = _playback.AudioPosition;
        return _playback.AudioDuration is { } d ? $"{Fmt(pos)} / {Fmt(d)}" : Fmt(pos);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_boundFile == null || e.Modifiers != Keys.None) return;
        if (e.KeyCode is Keys.Space or Keys.L)
        {
            Rectangle target = e.KeyCode == Keys.Space ? _playButton : _loopButton;
            OnMouseDownHandler(this, new MouseEventArgs(MouseButtons.Left, 1, target.Left + target.Width / 2,
                target.Top + target.Height / 2, 0));
        }
        else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            if (IsBoundPlaying && _playback != null)
            {
                if (e.KeyCode is Keys.Left or Keys.Right)
                    _playback.SeekAudioRelative(TimeSpan.FromSeconds(e.KeyCode == Keys.Left ? -10 : 10));
                else _playback.AudioVolume = Math.Clamp(_playback.AudioVolume + (e.KeyCode == Keys.Up ? 0.05f : -0.05f), 0, 1);
                RefreshLiveState();
            }
        }
        else return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ModernUi.Text, BackColor);
    }

    private void OnMouseDownHandler(object? sender, MouseEventArgs e)
    {
        if (_boundFile == null || e.Button != MouseButtons.Left) return;
        Focus();

        if (_titleRect.Contains(e.Location))
            ShowFilePicker(this, new Point(_titleRect.Left, _titleRect.Bottom + 4));
        else if (_playButton.Contains(e.Location))
            TogglePlayPause();
        else if (_loopButton.Contains(e.Location))
            ToggleLoop();
        else if (Grew(_progressTrack).Contains(e.Location))
            SeekToFraction((double)(e.X - _progressTrack.Left) / Math.Max(1, _progressTrack.Width));
        // Volume scrubbing (only meaningful while this file is the current one, same as before).
        else if (Grew(_volumeTrack).Contains(e.Location))
            SetVolume((float)(e.X - _volumeTrack.Left) / Math.Max(1, _volumeTrack.Width));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool interactive = _boundFile != null && (_titleRect.Contains(e.Location) || _playButton.Contains(e.Location)
            || _loopButton.Contains(e.Location) || Grew(_progressTrack).Contains(e.Location) || Grew(_volumeTrack).Contains(e.Location));
        Cursor = interactive ? Cursors.Hand : Cursors.Default;
    }

    private void DrawChevron(Graphics g, Point center, Color color)
    {
        float s = LogicalToDeviceUnits(4);
        using var pen = new Pen(color, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(center.X - s, center.Y - s / 2), new PointF(center.X, center.Y + s / 2), new PointF(center.X + s, center.Y - s / 2) });
    }

    // Widen a thin track to a comfortable click target.
    private static Rectangle Grew(Rectangle r) => Rectangle.Inflate(r, 4, 12);

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        if (r.Width <= d || r.Height <= d) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    /// <summary>A real rounded rectangle (four corner arcs). <see cref="Rounded"/> above builds a
    /// stadium/pill for thin tracks and draws the wrong shape for anything taller than 2×radius.</summary>
    private static GraphicsPath RoundedBox(Rectangle r, int radius)
    {
        int d = Math.Min(Math.Max(2, radius * 2), Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
