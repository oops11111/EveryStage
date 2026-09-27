using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2 的音频「横向播放条」——设计图里是<b>一整条</b>细长播放器（音符图标 + 名称/时长 +
/// 播放键 + 进度条 + 音量滑块 + 循环 + 右侧「投屏」按钮），不是逐文件的一列小行。
///
/// 这里只改视觉与交互载体，不改任何播放数据流：
/// <list type="bullet">
/// <item>播放/暂停走 <see cref="PlaybackEngine.Pause"/>/<see cref="PlaybackEngine.Resume"/>（仅当
///   当前条对应的文件正是 <see cref="PlaybackEngine.CurrentFile"/> 时有意义，与原 AudioRow 一致）。</item>
/// <item>音量滑块走 <see cref="PlaybackEngine.AudioVolume"/>（引擎级单值，同原实现）。</item>
/// <item>循环开关改 <see cref="MediaFile.OnCompletion"/> 并持久化（同原 AudioRow 的 loopCheckbox）。</item>
/// <item>「投屏」按钮 raise <see cref="CastRequested"/>，由 FilesPanel 转成原有的
///   <c>FilePlayRequested</c> → <c>PlaybackEngine.RequestPlay</c>，行为不变。</item>
/// </list>
/// 「当前条」绑定的文件：优先是正在播放的音频文件；否则是音频列表里的第一个——这样这条播放器始终
/// 展示一个有意义的对象，符合设计图的单条形态。进度/时长/音量/播放态由外部计时器每 500ms 调
/// <see cref="RefreshLiveState"/> 刷新，同原 <c>_audioBarRefreshTimer</c> 的节奏。
/// </summary>
internal sealed class AudioPlayerBar : GlassPanel
{
    private readonly FileOperationLogger _fileOpLog;
    private readonly FileLibraryStore _library;
    private PlaybackEngine? _playback;

    private MediaFile? _boundFile;
    private readonly List<MediaFile> _audioFiles = new();

    private Rectangle _playButton;
    private Rectangle _progressTrack;
    private Rectangle _volumeTrack;
    private Rectangle _loopButton;
    private readonly Button _castButton;
    private bool _compact;
    private Rectangle _iconTile;
    private Rectangle _titleRect;
    private Rectangle _timeRect;

    /// <summary>Raised by the right-hand「投屏」button — FilesPanel forwards this to its existing
    /// <c>FilePlayRequested</c> event so playback behavior is byte-for-byte the same as before.</summary>
    public event Action<MediaFile>? CastRequested;

    /// <summary>Raised by the round play button when the bound file isn't loaded yet: play it in the
    /// Preview monitor with EveryStage's own audio controller (never an external player).</summary>
    public event Action<MediaFile>? PlayRequested;

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
        CornerRadius = 18;
        GlassTint = ModernUi.Palette.GlassTint;
        Visible = false; // flipped on by SetAudioFiles once there are any audio files.

        _castButton = new Button
        {
            Text = "🖵  投屏",
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = ModernUi.Text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Size = new Size(96, 34),
        };
        _castButton.FlatAppearance.BorderSize = 0;
        _castButton.BackColor = ModernUi.Accent;
        _castButton.Click += (_, _) => { if (_boundFile != null) CastRequested?.Invoke(_boundFile); };
        Controls.Add(_castButton);

        MouseDown += OnMouseDownHandler;
        Resize += (_, _) => LayoutControls();
    }

    public void AttachPlaybackEngine(PlaybackEngine playback) => _playback = playback;

    /// <summary>Rebinds the bar to the current set of audio files (called from FilesPanel.Refresh_).
    /// Hides the whole bar when there are none, matching the old RefreshAudioBar behavior.</summary>
    public void SetAudioFiles(IReadOnlyList<MediaFile> audioFiles)
    {
        _audioFiles.Clear();
        _audioFiles.AddRange(audioFiles);
        Visible = _audioFiles.Count > 0;
        RefreshLiveState();
    }

    /// <summary>Per-tick live update (play/pause, position, volume) — same cadence and intent as the
    /// old <c>RefreshAudioRowLiveState</c>. Also re-picks the bound file so the bar follows whatever
    /// is currently playing.</summary>
    public void RefreshLiveState()
    {
        if (_audioFiles.Count == 0) { _boundFile = null; return; }
        var current = _playback?.CurrentFile;
        _boundFile = current?.Kind == MediaKind.Audio
            ? _audioFiles.FirstOrDefault(f => SameSource(f, current)) ?? _audioFiles[0]
            : _audioFiles[0];
        LayoutControls();
        Invalidate();
    }

    private static bool SameSource(MediaFile left, MediaFile right) =>
        left.Kind == right.Kind && string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase);

    private bool IsBoundPlaying => _boundFile != null && _playback?.CurrentFile is { } c && SameSource(c, _boundFile);

    private void LayoutControls()
    {
        int S(int value) => LogicalToDeviceUnits(value);
        // 对齐效果图：宽度足够时是「一整条」单行（音符图标 · 名称/时长 · 播放 · 进度 · 音量 · 循环 ·
        // 投屏），约 64px 高；过窄时才退化为两行（约 104px），不再是旧版 116/172px 的大封面布局——
        // 那会在默认 960×720 窗口里挤掉大半个文件网格。
        _compact = ClientSize.Width < S(600);
        int desiredHeight = S(_compact ? 104 : 64);
        if (Height != desiredHeight) Height = desiredHeight;
        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = S(16);
        _castButton.Size = new Size(S(96), S(34));

        if (!_compact)
        {
            int cy = h / 2;
            _iconTile = new Rectangle(pad, cy - S(20), S(40), S(40));
            _titleRect = new Rectangle(_iconTile.Right + S(12), cy - S(19), S(128), S(20));
            _timeRect = new Rectangle(_titleRect.Left, cy + S(1), S(128), S(18));
            _playButton = new Rectangle(_titleRect.Right + S(12), cy - S(17), S(34), S(34));
            _castButton.Location = new Point(w - pad - _castButton.Width, cy - _castButton.Height / 2);
            _loopButton = new Rectangle(_castButton.Left - S(40), cy - S(14), S(28), S(28));
            int volWidth = w >= S(760) ? S(100) : S(64);
            _volumeTrack = new Rectangle(_loopButton.Left - S(16) - volWidth, cy - S(2), volWidth, S(4));
            int progLeft = _playButton.Right + S(16);
            _progressTrack = new Rectangle(progLeft, cy - S(2), Math.Max(S(40), _volumeTrack.Left - S(46) - progLeft), S(4));
        }
        else
        {
            int row1 = S(28), row2 = S(72);
            _iconTile = new Rectangle(pad, row1 - S(14), S(28), S(28));
            _timeRect = new Rectangle(w - pad - S(96), row1 - S(9), S(96), S(18));
            _titleRect = new Rectangle(_iconTile.Right + S(10), row1 - S(10),
                Math.Max(S(20), _timeRect.Left - S(8) - _iconTile.Right - S(10)), S(20));
            _playButton = new Rectangle(pad, row2 - S(17), S(34), S(34));
            _castButton.Location = new Point(w - pad - _castButton.Width, row2 - _castButton.Height / 2);
            _loopButton = new Rectangle(_castButton.Left - S(38), row2 - S(14), S(28), S(28));
            int volWidth = Math.Max(S(24), Math.Min(S(72), (w - S(300)) / 3));
            _volumeTrack = new Rectangle(_loopButton.Left - S(14) - volWidth, row2 - S(2), volWidth, S(4));
            int progLeft = _playButton.Right + S(14);
            _progressTrack = new Rectangle(progLeft, row2 - S(2), Math.Max(S(20), _volumeTrack.Left - S(42) - progLeft), S(4));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_boundFile == null) return;

        int S(int value) => LogicalToDeviceUnits(value);

        // Icon tile（效果图左侧的小方块音符图标）。旧版这里是一整块封面，且误用了下方给细进度条
        // 用的胶囊形 Rounded()，结果只在方块顶部画出一条空胶囊。
        using (var tilePath = RoundedBox(_iconTile, S(9)))
        using (var tileBg = new LinearGradientBrush(_iconTile, Color.FromArgb(47, 111, 208), Color.FromArgb(27, 63, 122), 60F))
            g.FillPath(tileBg, tilePath);
        using (var noteFont = new Font("Segoe UI Symbol", _iconTile.Height * 0.46f, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "♫", noteFont, _iconTile, Color.FromArgb(188, 214, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Title + time: single row stacks them beside the tile; compact puts time on the right.
        using (var titleFont = new Font("Segoe UI Semibold", 10.5F))
            TextRenderer.DrawText(g, Path.GetFileName(_boundFile.SourcePath), titleFont, _titleRect, ModernUi.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using (var timeFont = new Font("Segoe UI", 9F))
            TextRenderer.DrawText(g, PositionText(), timeFont, _timeRect, ModernUi.Muted,
                (_compact ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter);

        // Play/pause — dark rounded icon button, as in the reference (not a large accent circle).
        using (var btnPath = RoundedBox(_playButton, S(8)))
        {
            using var btnFill = new SolidBrush(ModernUi.Surface);
            g.FillPath(btnFill, btnPath);
            using var btnPen = new Pen(ModernUi.Border, 1F);
            g.DrawPath(btnPen, btnPath);
        }
        using (var glyph = new SolidBrush(ModernUi.Text))
        {
            int gcx = _playButton.Left + _playButton.Width / 2, gcy = _playButton.Top + _playButton.Height / 2;
            if (IsBoundPlaying && _playback?.IsPaused == false)
            {
                int bw = S(4), bh = S(14), gap = S(4);
                g.FillRectangle(glyph, gcx - bw - gap / 2, gcy - bh / 2, bw, bh);
                g.FillRectangle(glyph, gcx + gap / 2, gcy - bh / 2, bw, bh);
            }
            else
            {
                float x = gcx + S(1);
                g.FillPolygon(glyph, new[] { new PointF(x - S(5), gcy - S(7)), new PointF(x - S(5), gcy + S(7)), new PointF(x + S(7), gcy) });
            }
        }

        // Progress track + filled portion + knob.
        DrawTrack(g, _progressTrack, ProgressFraction(), true);

        // Volume icon + track.
        var volIconRect = new Rectangle(_volumeTrack.Left - S(30), _volumeTrack.Top - S(10), S(24), S(24));
        DrawVolumeIcon(g, volIconRect);
        DrawTrack(g, _volumeTrack, VolumeFraction(), true);

        // Loop icon.
        DrawLoopIcon(g, _loopButton, _boundFile.OnCompletion == CompletionAction.Loop);
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

    private void DrawLoopIcon(Graphics g, Rectangle r, bool active)
    {
        using var pen = new Pen(active ? ModernUi.Accent : ModernUi.Muted, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r.Left + 3, r.Top + 4, r.Width - 6, r.Height - 8, 30, 300);
        using var arrow = new SolidBrush(active ? ModernUi.Accent : ModernUi.Muted);
        g.FillPolygon(arrow, new[]
        {
            new PointF(r.Right - 4, r.Top + 3),
            new PointF(r.Right - 4, r.Top + 11),
            new PointF(r.Right - 11, r.Top + 7),
        });
    }

    private float ProgressFraction()
    {
        if (!IsBoundPlaying || _playback == null) return 0f;
        var dur = _playback.AudioDuration;
        if (dur is not { } d || d.TotalSeconds <= 0) return 0f;
        return (float)(_playback.AudioPosition.TotalSeconds / d.TotalSeconds);
    }

    private float VolumeFraction() => IsBoundPlaying && _playback != null ? Math.Clamp(_playback.AudioVolume, 0f, 1f) : 0.7f;

    private string PositionText()
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

        if (_playButton.Contains(e.Location))
        {
            if (_playback != null && IsBoundPlaying)
            {
                if (_playback.IsPaused) _playback.Resume(); else _playback.Pause();
                RefreshLiveState();
            }
            else
            {
                PlayRequested?.Invoke(_boundFile);
                RefreshLiveState();
            }
            return;
        }

        if (_loopButton.Contains(e.Location))
        {
            var newValue = _boundFile.OnCompletion == CompletionAction.Loop ? CompletionAction.NextItem : CompletionAction.Loop;
            var oldValue = _boundFile.OnCompletion;
            _boundFile.OnCompletion = newValue;
            try { _library.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _boundFile.OnCompletion = oldValue;
                MessageBox.Show(this, $"循环设置未保存：{ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Invalidate();
                return;
            }
            if (IsBoundPlaying && _playback?.CurrentFile is { } current) current.OnCompletion = newValue;
            _fileOpLog.LogPlaybackPropertyChanged(_boundFile.Id, nameof(MediaFile.OnCompletion), oldValue.ToString(), newValue.ToString());
            Invalidate();
            return;
        }

        if (Grew(_progressTrack).Contains(e.Location) && IsBoundPlaying && _playback?.AudioDuration is { } duration)
        {
            double fraction = Math.Clamp((double)(e.X - _progressTrack.Left) / Math.Max(1, _progressTrack.Width), 0, 1);
            _playback.SeekAudioRelative(TimeSpan.FromSeconds(duration.TotalSeconds * fraction) - _playback.AudioPosition);
            RefreshLiveState();
            return;
        }

        // Volume scrubbing (only meaningful while this file is the current one, same as before).
        if (Grew(_volumeTrack).Contains(e.Location) && _playback != null && IsBoundPlaying)
        {
            float f = (float)(e.X - _volumeTrack.Left) / _volumeTrack.Width;
            _playback.AudioVolume = Math.Clamp(f, 0f, 1f);
            RefreshLiveState();
        }
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
