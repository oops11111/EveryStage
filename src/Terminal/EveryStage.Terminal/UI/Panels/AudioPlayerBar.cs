using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2 的音频「横向播放条」——设计图里是<b>一整条</b>大播放器（封面 + 标题 + 圆形
/// 播放键 + 进度条 + 时长 + 音量滑块 + 循环 + 右侧「投屏」按钮），不是逐文件的一列小行。
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

    /// <summary>Raised by the right-hand「投屏」button — FilesPanel forwards this to its existing
    /// <c>FilePlayRequested</c> event so playback behavior is byte-for-byte the same as before.</summary>
    public event Action<MediaFile>? CastRequested;

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
        Height = 116;
        Margin = new Padding(0);
        CornerRadius = 18;
        GlassTint = Color.FromArgb(205, 15, 33, 55);
        Visible = false; // flipped on by SetAudioFiles once there are any audio files.

        _castButton = new Button
        {
            Text = "🖵  投屏",
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Size = new Size(112, 48),
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
        _compact = ClientSize.Width < LogicalToDeviceUnits(780);
        int desiredHeight = LogicalToDeviceUnits(_compact ? 172 : 116);
        if (Height != desiredHeight) Height = desiredHeight;
        int h = ClientSize.Height, w = ClientSize.Width;
        int cy = h / 2;

        // Artwork square on the far left.
        int art = h - S(32);
        int artLeft = S(20);
        int cover = artLeft + art + S(20);

        _playButton = new Rectangle(cover, cy - S(22), S(44), S(44));
        int progLeft = _playButton.Right + S(20);

        // Cast button on the far right; volume + loop sit to its left.
        _castButton.Size = new Size(S(112), S(48));
        _castButton.Location = new Point(w - _castButton.Width - S(20), cy - _castButton.Height / 2);
        _loopButton = new Rectangle(_castButton.Left - S(44), cy - S(14), S(28), S(28));
        _volumeTrack = new Rectangle(_loopButton.Left - S(132), cy - S(2), S(110), S(4));
        var volIcon = _volumeTrack.Left - S(34);

        // Time label reserves ~92px; progress track fills the middle up to the volume icon.
        int timeWidth = S(100);
        int progRight = volIcon - timeWidth - S(24);
        _progressTrack = new Rectangle(progLeft, cy - S(2), Math.Max(S(60), progRight - progLeft), S(4));
        if (_compact)
        {
            _playButton = new Rectangle(S(20), S(56), S(44), S(44));
            _progressTrack = new Rectangle(S(80), S(76), Math.Max(S(20), w - S(204)), S(4));
            _castButton.Size = new Size(S(112), S(36));
            _castButton.Location = new Point(Math.Max(0, w - S(132)), S(116));
            _loopButton = new Rectangle(_castButton.Left - S(44), S(120), S(28), S(28));
            _volumeTrack = new Rectangle(S(50), S(132), Math.Max(S(20), Math.Min(S(110), _loopButton.Left - S(70))), S(4));
        }
        else
        {
            _castButton.Size = new Size(LogicalToDeviceUnits(112), LogicalToDeviceUnits(48));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_boundFile == null) return;

        int h = ClientSize.Height, cy = h / 2;

        // Artwork.
        int art = _compact ? 0 : h - LogicalToDeviceUnits(32);
        var artRect = new Rectangle(LogicalToDeviceUnits(20), (h - art) / 2, art, art);
        if (!_compact)
        using (var artPath = Rounded(artRect, 12))
        {
            using var artBg = new LinearGradientBrush(artRect, Color.FromArgb(58, 92, 150), Color.FromArgb(30, 52, 92), 60F);
            g.FillPath(artBg, artPath);
            using var note = new SolidBrush(Color.FromArgb(220, 235, 250));
            using var noteFont = new Font("Segoe UI Symbol", art * 0.34f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "♫", noteFont, artRect, Color.FromArgb(230, 240, 250),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Title.
        var titleRect = new Rectangle(artRect.Right + LogicalToDeviceUnits(20), artRect.Top - LogicalToDeviceUnits(2),
            Math.Max(1, _progressTrack.Right - artRect.Right - LogicalToDeviceUnits(20)), LogicalToDeviceUnits(28));
        if (_compact) titleRect = new Rectangle(LogicalToDeviceUnits(20), LogicalToDeviceUnits(12),
            Math.Max(1, Width - LogicalToDeviceUnits(40)), LogicalToDeviceUnits(30));
        using (var titleFont = new Font("Segoe UI Semibold", 13F))
            TextRenderer.DrawText(g, Path.GetFileName(_boundFile.SourcePath), titleFont, titleRect,
                ModernUi.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // Round play/pause button.
        using (var playFill = new SolidBrush(ModernUi.Accent))
            g.FillEllipse(playFill, _playButton);
        using (var glyph = new SolidBrush(Color.White))
        {
            if (IsBoundPlaying && _playback?.IsPaused == false)
            {
                int bw = 5, bh = 16, gap = 5;
                int bx = _playButton.Left + _playButton.Width / 2 - bw - gap / 2;
                int by = _playButton.Top + (_playButton.Height - bh) / 2;
                g.FillRectangle(glyph, bx, by, bw, bh);
                g.FillRectangle(glyph, bx + bw + gap, by, bw, bh);
            }
            else
            {
                int cx = _playButton.Left + _playButton.Width / 2 + 2;
                int cyy = _playButton.Top + _playButton.Height / 2;
                g.FillPolygon(glyph, new[]
                {
                    new PointF(cx - 7, cyy - 9), new PointF(cx - 7, cyy + 9), new PointF(cx + 8, cyy),
                });
            }
        }

        // Progress track + filled portion + knob.
        DrawTrack(g, _progressTrack, ProgressFraction(), true);

        // Time label.
        var timeRect = new Rectangle(_progressTrack.Right + LogicalToDeviceUnits(12), _progressTrack.Top - LogicalToDeviceUnits(10), LogicalToDeviceUnits(100), LogicalToDeviceUnits(24));
        using (var timeFont = new Font("Segoe UI", 10F))
            TextRenderer.DrawText(g, PositionText(), timeFont, timeRect, ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // Volume icon + track.
        var volIconRect = new Rectangle(_volumeTrack.Left - LogicalToDeviceUnits(30), _volumeTrack.Top - LogicalToDeviceUnits(10), LogicalToDeviceUnits(24), LogicalToDeviceUnits(24));
        DrawVolumeIcon(g, volIconRect);
        DrawTrack(g, _volumeTrack, VolumeFraction(), true);

        // Loop icon.
        DrawLoopIcon(g, _loopButton, _boundFile.OnCompletion == CompletionAction.Loop);
    }

    private void DrawTrack(Graphics g, Rectangle track, float fraction, bool knob)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        using (var bg = new SolidBrush(Color.FromArgb(60, 91, 126)))
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
                CastRequested?.Invoke(_boundFile);
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
}
