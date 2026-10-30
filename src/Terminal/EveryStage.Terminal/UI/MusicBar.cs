using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The background-music mini bar along the bottom of every page (design 「背景音乐」): ♪ · track ·
/// 「歌单 · 第 i / n 首 · 模式」 · ⏮ ⏯ ⏭ · progress · time · volume · 淡出 · 「歌单 ▴」. It drives the
/// <see cref="MusicChannel"/>, which plays independently of Preview and the big screen. Clicking the bar
/// itself (♪, the track text, empty space) or 「歌单 ▴」 slides the <see cref="MusicDrawer"/> up or back.
/// </summary>
internal sealed class MusicBar : Panel
{
    private const string IconFont = "Segoe MDL2 Assets";
    private readonly MusicChannel _music;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly RoundedActionButton _previous;
    private readonly RoundedActionButton _playPause;
    private readonly RoundedActionButton _next;
    private readonly MediaTrack _progress;
    private readonly Label _time;
    private readonly Label _volumeIcon;
    private readonly MediaTrack _volume;
    private readonly RoundedActionButton _fadeOut;
    private readonly RoundedActionButton _playlists;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _toolTip = new();

    /// <summary>The bar or 「歌单 ▴」 was clicked: MainWindow slides the playlist drawer up or back.</summary>
    public event Action? PlaylistsRequested;

    /// <summary>Whether the drawer is open: 「歌单 ▾」 then, 「歌单 ▴」 otherwise.</summary>
    public bool DrawerOpen
    {
        set
        {
            _playlists.Text = value ? "歌单 ▾" : "歌单 ▴";
            _toolTip.SetToolTip(_playlists, value ? "收起歌单" : "展开歌单");
        }
    }

    public MusicBar(MusicChannel music)
    {
        _music = music;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        Height = 56;

        _title = new Label { AutoEllipsis = true, ForeColor = ModernUi.Text, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 9.5F), TextAlign = ContentAlignment.BottomLeft };
        _subtitle = new Label { AutoEllipsis = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8F), TextAlign = ContentAlignment.TopLeft };
        _previous = IconButton("", "上一首（播放 3 秒后为从头播放）");
        _playPause = IconButton("", "播放 / 暂停背景音乐");
        ModernUi.StyleButton(_playPause, primary: true);
        _playPause.Font = new Font(IconFont, 10F);
        _next = IconButton("", "下一首");
        _progress = new MediaTrack();
        _time = new Label { ForeColor = ModernUi.Muted, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8.5F), TextAlign = ContentAlignment.MiddleLeft };
        _volumeIcon = new Label { Text = "", Font = new Font(IconFont, 10F), ForeColor = ModernUi.Muted, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter };
        _volume = new MediaTrack { Value = music.Volume };
        _fadeOut = TextButton("淡出", "3 秒内淡出后停止");
        _playlists = TextButton("歌单 ▴", "展开歌单");
        Controls.AddRange(new Control[] { _title, _subtitle, _previous, _playPause, _next, _progress, _time, _volumeIcon, _volume, _fadeOut, _playlists });

        _previous.Click += (_, _) => _music.Previous();
        _next.Click += (_, _) => _music.Next();
        _playPause.Click += (_, _) =>
        {
            if (!_music.IsPlaying && _music.Library.Current is not { Tracks.Count: > 0 }) { PlaylistsRequested?.Invoke(); return; }
            _music.PlayPause();
        };
        _progress.ValueCommitted += fraction => _music.Seek(fraction);
        _volume.ValueChanging += value => _music.Volume = value;
        _volume.ValueCommitted += value =>
        {
            _music.Volume = value;
            try { _music.Store.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        _fadeOut.Click += (_, _) => _music.FadeOutStop();
        _playlists.Click += (_, _) => PlaylistsRequested?.Invoke();
        // The bar itself is the drawer's handle.
        Cursor = _title.Cursor = _subtitle.Cursor = Cursors.Hand;
        _toolTip.SetToolTip(_title, "点击展开 / 收起歌单");
        _toolTip.SetToolTip(_subtitle, "点击展开 / 收起歌单");
        _title.Click += (_, _) => PlaylistsRequested?.Invoke();
        _subtitle.Click += (_, _) => PlaylistsRequested?.Invoke();
        _music.Changed += RefreshState;

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        RefreshState();

        RoundedActionButton IconButton(string glyph, string tip)
        {
            var button = new RoundedActionButton { Text = glyph, Margin = Padding.Empty, Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            button.Font = new Font(IconFont, 9F);
            _toolTip.SetToolTip(button, tip);
            return button;
        }

        RoundedActionButton TextButton(string text, string tip)
        {
            var button = new RoundedActionButton { Text = text, Margin = Padding.Empty, Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            button.Font = new Font(ModernUi.UiFont, 9F);
            _toolTip.SetToolTip(button, tip);
            return button;
        }
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) PlaylistsRequested?.Invoke();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_playlists == null) return; // raised from the base constructor
        int w = ClientSize.Width, h = ClientSize.Height, cy = h / 2 + S(2), x = S(34);
        int round = S(30), button = S(30);
        // Right side, right to left: 歌单 · 淡出 · volume.
        int right = w;
        _playlists.Bounds = new Rectangle(right -= S(70), cy - button / 2, S(70), button);
        _fadeOut.Bounds = new Rectangle(right -= S(8) + S(52), cy - button / 2, S(52), button);
        bool wide = w >= S(760);
        _volume.Visible = _volumeIcon.Visible = wide;
        if (wide)
        {
            _volume.Bounds = new Rectangle(right -= S(10) + S(80), cy - S(12), S(80), S(24));
            _volumeIcon.Bounds = new Rectangle(right -= S(22), cy - button / 2, S(22), button);
        }
        // Left: title / subtitle, then transport, then progress + time taking the rest.
        int textWidth = Math.Clamp(w / 4, S(120), S(240));
        _title.Bounds = new Rectangle(x, S(8), textWidth, cy - S(8));
        _subtitle.Bounds = new Rectangle(x, cy + S(1), textWidth, h - cy - S(4));
        x += textWidth + S(12);
        _previous.Bounds = new Rectangle(x, cy - round / 2, round, round);
        _playPause.Bounds = new Rectangle(x += round + S(6), cy - S(17), S(34), S(34));
        _next.Bounds = new Rectangle(x += S(34) + S(6), cy - round / 2, round, round);
        x += round + S(14);
        int timeWidth = TextRenderer.MeasureText("00:00 / 00:00", _time.Font).Width + S(4);
        int progressRight = right - S(14) - timeWidth;
        _progress.Visible = progressRight - x >= S(60);
        _progress.Bounds = new Rectangle(x, cy - S(12), Math.Max(1, progressRight - x), S(24));
        _time.Bounds = new Rectangle(_progress.Visible ? progressRight + S(6) : x, cy - button / 2, timeWidth, button);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(ModernUi.Mix(ModernUi.Background, ModernUi.Border, 0.45f)))
            g.DrawLine(line, 0, 0, Width, 0);
        // ♪ glyph in the music colour.
        var color = Color.FromArgb(139, 116, 240);
        int cy = Height / 2 + S(2);
        using var pen = new Pen(color, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);
        int x = S(4);
        g.DrawLine(pen, x + S(13), cy - S(8), x + S(13), cy + S(5));
        g.DrawLine(pen, x + S(13), cy - S(8), x + S(20), cy - S(6));
        g.FillEllipse(brush, x + S(6), cy + S(2), S(8), S(6));
    }

    private void RefreshState()
    {
        if (IsDisposed) return;
        var playlist = _music.Playlist ?? _music.Library.Current;
        string? track = _music.CurrentTrack;
        if (_music.LastError is { } error)
        {
            Set(_title, "背景音乐", ModernUi.Text);
            Set(_subtitle, error, ModernUi.Danger);
        }
        else if (track == null || !_music.IsPlaying)
        {
            Set(_title, playlist == null ? "背景音乐" : playlist.Name, ModernUi.Text);
            Set(_subtitle, playlist == null ? "还没有歌单 · 点这里新建" : $"{playlist.Tracks.Count} 首 · {ModeText(playlist.Mode)} · 未播放", ModernUi.Muted);
        }
        else
        {
            Set(_title, Path.GetFileName(track), ModernUi.Text);
            string state = _music.IsFadingOut ? " · 淡出中" : _music.IsPaused ? " · 已暂停" : _music.IsDucked ? " · 已压低（大屏视频有声音）" : "";
            Set(_subtitle, $"{playlist!.Name} · 第 {_music.TrackIndex + 1} / {playlist.Tracks.Count} 首 · {ModeText(playlist.Mode)}{state}", ModernUi.Muted);
        }
        bool playing = _music.IsPlaying && !_music.IsPaused;
        if (_playPause.Text != (playing ? "" : "")) _playPause.Text = playing ? "" : "";
        _previous.Enabled = _next.Enabled = _music.IsPlaying;
        _fadeOut.Enabled = _music.IsPlaying && !_music.IsFadingOut;
        var duration = _music.Duration;
        var position = _music.Position;
        _progress.Enabled = duration.HasValue;
        if (!_progress.IsDragging) _progress.Value = duration is { Ticks: > 0 } d ? (float)(position.Ticks / (double)d.Ticks) : 0f;
        Set(_time, _music.IsPlaying ? $"{Format(position)} / {(duration is { } total ? Format(total) : "--:--")}" : "--:-- / --:--", null);
        if (!_volume.IsDragging) _volume.Value = _music.Volume;
    }

    internal static string ModeText(PlaylistMode mode) => mode switch
    {
        PlaylistMode.PlayOnce => "顺序播完停止",
        PlaylistMode.LoopOne => "单曲循环",
        PlaylistMode.Shuffle => "随机播放",
        _ => "列表循环",
    };

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";

    private static void Set(Control control, string text, Color? color)
    {
        if (control.Text != text) control.Text = text;
        if (color is { } c && control.ForeColor != c) control.ForeColor = c;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _music.Changed -= RefreshState;
            _timer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
