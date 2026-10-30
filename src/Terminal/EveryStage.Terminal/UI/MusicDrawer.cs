using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 「背景音乐歌单」 as a drawer that slides up out of the <see cref="MusicBar"/> (click the bar or 「歌单 ▴」;
/// click again, 「收起」 or Esc to slide it back): playlists on the left (新建 / 重命名 / 删除), the selected
/// playlist's songs on the right (从文件库添加 / 导入音频 / 上移 / 下移 / 移除, double-click plays from that
/// song, the playing song is marked), then its play mode and the channel options (启动后自动播放,
/// 大屏视频有声音时压低). Every change is saved immediately. The page above stays usable while it is open.
/// </summary>
internal sealed class MusicDrawer : Panel
{
    private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".wma", ".ogg" };
    private const int SheetLogicalHeight = 300;
    private const int SlideMilliseconds = 180;

    private readonly MusicChannel _music;
    private readonly FileLibraryStore _fileLibrary;
    private readonly Panel _sheet;
    private readonly Label _heading;
    private readonly RoundedActionButton _collapse;
    private readonly Label _playlistLabel;
    private readonly ListBox _playlists;
    private readonly RoundedActionButton _newPlaylist, _rename, _delete;
    private readonly Label _trackLabel;
    private readonly ListBox _tracks;
    private readonly RoundedActionButton _addFromLibrary, _import, _up, _down, _remove;
    private readonly Label _modeLabel;
    private readonly ComboBox _mode;
    private readonly CheckBox _autoPlay;
    private readonly CheckBox _duck;
    private readonly System.Windows.Forms.Timer _slide = new() { Interval = 15 };
    private readonly ToolTip _toolTip = new();
    private DateTime _slideStart;
    private int _slideFrom;
    private bool _open;
    private bool _updating;

    /// <summary>Raised when the drawer starts opening or closing (the bar flips its 「歌单 ▴/▾」).</summary>
    public event Action<bool>? OpenChanged;

    private MusicLibrary Library => _music.Library;
    private Playlist? Selected => _playlists.SelectedIndex >= 0 && _playlists.SelectedIndex < Library.Playlists.Count ? Library.Playlists[_playlists.SelectedIndex] : null;

    public bool IsOpen => _open;

    public MusicDrawer(MusicChannel music, FileLibraryStore fileLibrary)
    {
        _music = music;
        _fileLibrary = fileLibrary;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        Height = 0;
        Visible = false;

        _sheet = new SheetPanel { BackColor = Color.Transparent };
        _heading = new Label { Text = "背景音乐歌单", AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFont, 10.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        _collapse = Button("收起 ▾", "收起歌单（Esc）", Close);

        _playlistLabel = Caption("歌单");
        _playlists = List(SelectionMode.One, DrawPlaylist);
        _newPlaylist = Button("＋ 新建", "新建歌单", NewPlaylist);
        _rename = Button("重命名", "重命名选中的歌单", RenamePlaylist);
        _delete = Button("删除", "删除选中的歌单（音频文件本身不会被删除）", DeletePlaylist);

        _trackLabel = Caption("歌曲 · 双击从这首开始播放");
        _tracks = List(SelectionMode.MultiExtended, DrawTrack);
        _addFromLibrary = Button("＋ 从文件库添加", "把文件库里的音频加入这个歌单", AddFromLibrary);
        _import = Button("导入音频", "从电脑选择音频文件加入这个歌单", ImportFiles);
        _up = Button("上移", "把选中的歌曲上移一位", () => MoveTrack(-1));
        _down = Button("下移", "把选中的歌曲下移一位", () => MoveTrack(1));
        _remove = Button("移除", "从歌单移除选中的歌曲（文件本身不会被删除）", RemoveTracks);

        _modeLabel = Caption("播放方式");
        _modeLabel.TextAlign = ContentAlignment.MiddleLeft;
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font(ModernUi.UiFont, 9F) };
        _mode.Items.AddRange(new object[] { "列表循环", "顺序播完停止", "单曲循环", "随机播放" });
        ModernUi.StyleComboBox(_mode);
        _autoPlay = Check("启动时自动播放");
        _toolTip.SetToolTip(_autoPlay, "终端机启动后自动播放这个歌单");
        _duck = Check("大屏有声时压低");
        _toolTip.SetToolTip(_duck, "大屏视频有声音时，自动把音乐压低到 20%");
        _duck.Checked = Library.DuckForVideo;

        _sheet.Controls.AddRange(new Control[]
        {
            _heading, _collapse, _playlistLabel, _playlists, _newPlaylist, _rename, _delete,
            _trackLabel, _tracks, _addFromLibrary, _import, _up, _down, _remove, _modeLabel, _mode, _autoPlay, _duck,
        });
        Controls.Add(_sheet);
        _sheet.Layout += (_, _) => LayoutSheet();

        _playlists.SelectedIndexChanged += (_, _) => ShowSelected();
        _tracks.SelectedIndexChanged += (_, _) => UpdateButtons();
        _tracks.DoubleClick += (_, _) =>
        {
            if (Selected is { } playlist && _tracks.SelectedIndex >= 0) _music.Play(playlist, _tracks.SelectedIndex);
        };
        _tracks.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveTracks(); };
        _mode.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || Selected is not { } playlist || _mode.SelectedIndex < 0) return;
            playlist.Mode = (PlaylistMode)_mode.SelectedIndex;
            Save();
        };
        _autoPlay.CheckedChanged += (_, _) =>
        {
            if (_updating || Selected is not { } playlist) return;
            foreach (var other in Library.Playlists) other.AutoPlayOnStartup = false;
            playlist.AutoPlayOnStartup = _autoPlay.Checked;
            Save();
        };
        _duck.CheckedChanged += (_, _) => { if (!_updating) { Library.DuckForVideo = _duck.Checked; Save(); } };
        // The playing song's marker follows the channel.
        _music.Changed += OnMusicChanged;
        _slide.Tick += (_, _) => StepSlide();

        RefreshPlaylists(Library.Current);

        Label Caption(string text) => new() { Text = text, AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = ModernUi.Muted, Font = new Font(ModernUi.UiFont, 8.5F), TextAlign = ContentAlignment.BottomLeft };

        CheckBox Check(string text) => new() { Text = text, AutoSize = true, BackColor = Color.Transparent, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFont, 9F) };

        RoundedActionButton Button(string text, string tip, Action onClick)
        {
            var button = new RoundedActionButton { Text = text, Margin = Padding.Empty, Padding = Padding.Empty };
            ModernUi.StyleButton(button);
            button.Font = new Font(ModernUi.UiFont, 9F);
            button.Click += (_, _) => onClick();
            _toolTip.SetToolTip(button, tip);
            return button;
        }

        ListBox List(SelectionMode mode, DrawItemEventHandler draw)
        {
            var list = new ListBox
            {
                SelectionMode = mode, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 26, BackColor = ModernUi.Surface, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFont, 9F),
            };
            list.DrawItem += draw;
            return list;
        }
    }

    private int S(int v) => LogicalToDeviceUnits(v);
    private IWin32Window OwnerWindow => (IWin32Window?)FindForm() ?? this;

    /// <summary>Full height of the open drawer: 300 px, but never more than 60 % of the page.</summary>
    private int TargetHeight => Math.Max(S(200), Math.Min(S(SheetLogicalHeight), (Parent?.ClientSize.Height ?? S(SheetLogicalHeight * 2)) * 3 / 5));

    public void Toggle() { if (_open) Close(); else Open(); }

    public void Open()
    {
        if (_open) return;
        _open = true;
        RefreshPlaylists(Selected ?? Library.Current);
        Visible = true;
        BringToSlide();
        OpenChanged?.Invoke(true);
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        BringToSlide();
        OpenChanged?.Invoke(false);
    }

    /// <summary>Opens the drawer and adds <paramref name="paths"/> to the selected playlist
    /// (creating 「默认歌单」 when there is none).</summary>
    public void OpenAndAdd(IEnumerable<string> paths)
    {
        Open();
        AddTracks(paths);
    }

    private void BringToSlide()
    {
        _slideFrom = Height;
        _slideStart = DateTime.UtcNow;
        _slide.Start();
        StepSlide();
    }

    private void StepSlide()
    {
        if (IsDisposed) { _slide.Stop(); return; }
        int target = _open ? TargetHeight : 0;
        double t = Math.Min(1, (DateTime.UtcNow - _slideStart).TotalMilliseconds / SlideMilliseconds);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out: fast start, gentle stop
        Height = (int)Math.Round(_slideFrom + (target - _slideFrom) * eased);
        if (t >= 1)
        {
            _slide.Stop();
            Height = target;
            if (!_open) Visible = false;
            else if (_tracks.Items.Count > 0) _tracks.Focus();
            else _playlists.Focus();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_sheet == null) return; // raised from the base constructor
        // The sheet keeps its full height and rides on the drawer's bottom edge, so opening reveals it
        // from the top edge down — it slides up out of the music bar rather than unrolling.
        int full = _open || _slide.Enabled ? Math.Max(Height, TargetHeight) : TargetHeight;
        _sheet.Bounds = new Rectangle(0, Height - full, ClientSize.Width, full);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null) Parent.Resize += (_, _) => { if (_open && !_slide.Enabled) Height = TargetHeight; };
    }

    private void LayoutSheet()
    {
        int w = _sheet.ClientSize.Width, h = _sheet.ClientSize.Height;
        int pad = S(14), row = S(30), gap = S(8), x = pad, top = S(14);
        int contentWidth = Math.Max(S(200), w - 2 * pad);
        _heading.Bounds = new Rectangle(x, top, contentWidth / 2, row);
        _collapse.Bounds = new Rectangle(w - pad - S(72), top, S(72), row);
        top += row + S(6);

        // Bottom options row.
        int optionsY = h - pad - row;
        _modeLabel.Bounds = new Rectangle(x, optionsY, S(60), row);
        _mode.Bounds = new Rectangle(x + S(62), optionsY + (row - _mode.Height) / 2, S(130), _mode.Height);
        int cx = _mode.Right + S(20);
        _autoPlay.Location = new Point(cx, optionsY + (row - _autoPlay.Height) / 2);
        _duck.Location = new Point(_autoPlay.Right + S(16), optionsY + (row - _duck.Height) / 2);

        int listsBottom = optionsY - S(10);
        // Left column: playlists with 新建 / 重命名 / 删除 under it.
        int leftWidth = Math.Clamp(contentWidth / 3, S(170), S(240));
        _playlistLabel.Bounds = new Rectangle(x, top, leftWidth, S(20));
        int listTop = top + S(22);
        int buttonsY = listsBottom - row;
        _playlists.Bounds = new Rectangle(x, listTop, leftWidth, Math.Max(S(26), buttonsY - gap - listTop));
        int third = (leftWidth - 2 * S(6)) / 3;
        _newPlaylist.Bounds = new Rectangle(x, buttonsY, third, row);
        _rename.Bounds = new Rectangle(x + third + S(6), buttonsY, third, row);
        _delete.Bounds = new Rectangle(x + 2 * (third + S(6)), buttonsY, leftWidth - 2 * (third + S(6)), row);

        // Right column: songs with the song buttons under them.
        int rx = x + leftWidth + S(16), rightWidth = Math.Max(S(120), w - pad - rx);
        _trackLabel.Bounds = new Rectangle(rx, top, rightWidth, S(20));
        _tracks.Bounds = new Rectangle(rx, listTop, rightWidth, Math.Max(S(26), buttonsY - gap - listTop));
        // The song buttons share the column: their preferred widths, squeezed evenly when it is narrower.
        var songButtons = new[] { (_addFromLibrary, 112), (_import, 80), (_up, 52), (_down, 52), (_remove, 52) };
        int spacing = S(6), preferred = songButtons.Sum(b => S(b.Item2));
        float squeeze = Math.Min(1f, (rightWidth - spacing * (songButtons.Length - 1)) / (float)preferred);
        int bx = rx;
        foreach (var (button, width) in songButtons)
        {
            int bw = Math.Max(S(36), (int)(S(width) * squeeze));
            button.Bounds = new Rectangle(bx, buttonsY, bw, row);
            bx += bw + spacing;
        }
        if (_addFromLibrary.Text != (squeeze < 0.85f ? "＋ 文件库" : "＋ 从文件库添加"))
            _addFromLibrary.Text = squeeze < 0.85f ? "＋ 文件库" : "＋ 从文件库添加";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _open && !_mode.DroppedDown) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnMusicChanged()
    {
        if (!IsDisposed && _open) _tracks.Invalidate();
    }

    private void DrawPlaylist(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Library.Playlists.Count) return;
        var playlist = Library.Playlists[e.Index];
        bool playing = _music.IsPlaying && ReferenceEquals(_music.Playlist, playlist);
        DrawRow(_playlists, e, playlist.Name, $"{playlist.Tracks.Count} 首", playing, false);
    }

    private void DrawTrack(object? sender, DrawItemEventArgs e)
    {
        if (Selected is not { } playlist || e.Index < 0 || e.Index >= playlist.Tracks.Count) return;
        string path = playlist.Tracks[e.Index];
        bool playing = _music.IsPlaying && ReferenceEquals(_music.Playlist, playlist) && _music.TrackIndex == e.Index;
        bool missing = !File.Exists(path);
        DrawRow(_tracks, e, $"{e.Index + 1}.  {Path.GetFileName(path)}", missing ? "文件不存在" : "", playing, missing);
    }

    /// <summary>One list row: accent tint when selected, ♪ and accent text on the playing entry,
    /// a muted note on the right.</summary>
    private void DrawRow(ListBox list, DrawItemEventArgs e, string text, string note, bool playing, bool warn)
    {
        var g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? ModernUi.Mix(list.BackColor, ModernUi.Accent, 0.22f) : list.BackColor))
            g.FillRectangle(background, e.Bounds);
        int left = e.Bounds.Left + S(8);
        if (playing)
        {
            using var font = new Font(ModernUi.UiFont, 8F);
            TextRenderer.DrawText(g, "♪", font, new Rectangle(left, e.Bounds.Top, S(14), e.Bounds.Height), ModernUi.AccentText, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            left += S(14);
        }
        int noteWidth = note.Length == 0 ? 0 : TextRenderer.MeasureText(note, e.Font).Width + S(8);
        var textRect = Rectangle.FromLTRB(left, e.Bounds.Top, e.Bounds.Right - S(6) - noteWidth, e.Bounds.Bottom);
        TextRenderer.DrawText(g, text, e.Font, textRect, playing ? ModernUi.AccentText : ModernUi.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (noteWidth > 0)
            TextRenderer.DrawText(g, note, e.Font, Rectangle.FromLTRB(e.Bounds.Right - S(6) - noteWidth, e.Bounds.Top, e.Bounds.Right - S(6), e.Bounds.Bottom),
                warn ? ModernUi.Danger : ModernUi.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private void RefreshPlaylists(Playlist? select)
    {
        _playlists.BeginUpdate();
        _playlists.Items.Clear();
        foreach (var playlist in Library.Playlists) _playlists.Items.Add(playlist.Name);
        _playlists.EndUpdate();
        int index = select == null ? -1 : Library.Playlists.IndexOf(select);
        _playlists.SelectedIndex = index >= 0 ? index : Library.Playlists.Count > 0 ? 0 : -1;
        ShowSelected();
    }

    private void ShowSelected()
    {
        _updating = true;
        var playlist = Selected;
        _tracks.BeginUpdate();
        _tracks.Items.Clear();
        if (playlist != null)
            foreach (var track in playlist.Tracks) _tracks.Items.Add(Path.GetFileName(track));
        _tracks.EndUpdate();
        _mode.SelectedIndex = playlist == null ? 0 : (int)playlist.Mode;
        _autoPlay.Checked = playlist?.AutoPlayOnStartup == true;
        _duck.Checked = Library.DuckForVideo;
        if (playlist != null) Library.CurrentPlaylistId = playlist.Id;
        _trackLabel.Text = playlist == null ? "歌曲 · 先在左边新建一个歌单" : $"歌曲 · {playlist.Tracks.Count} 首 · 双击从这首开始播放";
        _updating = false;
        _playlists.Invalidate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = Selected != null;
        _rename.Enabled = _delete.Enabled = _autoPlay.Enabled = has; // the mode box stays enabled: a disabled flat combo draws a white frame
        _remove.Enabled = has && _tracks.SelectedIndices.Count > 0;
        _up.Enabled = has && _tracks.SelectedIndices.Count == 1 && _tracks.SelectedIndex > 0;
        _down.Enabled = has && _tracks.SelectedIndices.Count == 1 && _tracks.SelectedIndex < _tracks.Items.Count - 1;
    }

    private bool Save()
    {
        try
        {
            _music.Store.Save();
            _music.PlaylistsEdited();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(OwnerWindow, $"歌单未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void NewPlaylist()
    {
        if (TextInputDialog.Prompt(OwnerWindow, "新建歌单", "歌单名称：", $"歌单 {Library.Playlists.Count + 1}") is not { } name) return;
        var playlist = new Playlist { Name = name };
        Library.Playlists.Add(playlist);
        Save();
        RefreshPlaylists(playlist);
    }

    private void RenamePlaylist()
    {
        if (Selected is not { } playlist || TextInputDialog.Prompt(OwnerWindow, "重命名歌单", "歌单名称：", playlist.Name) is not { } name) return;
        playlist.Name = name;
        Save();
        RefreshPlaylists(playlist);
    }

    private void DeletePlaylist()
    {
        if (Selected is not { } playlist) return;
        if (MessageBox.Show(OwnerWindow, $"删除歌单“{playlist.Name}”？（音频文件本身不会被删除）", "删除歌单", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Library.Playlists.Remove(playlist);
        Save();
        RefreshPlaylists(null);
    }

    private void AddTracks(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        var playlist = Selected;
        if (playlist == null)
        {
            playlist = new Playlist { Name = "默认歌单" };
            Library.Playlists.Add(playlist);
        }
        int first = playlist.Tracks.Count;
        playlist.Tracks.AddRange(list);
        Save();
        RefreshPlaylists(playlist);
        // Show what was just added.
        _tracks.ClearSelected();
        for (int i = first; i < _tracks.Items.Count; i++) _tracks.SetSelected(i, true);
        _tracks.TopIndex = Math.Max(0, first - 1);
    }

    /// <summary>「从文件库添加」: a menu of the library's audio right under the button — click one to add it,
    /// or 「全部添加」.</summary>
    private void AddFromLibrary()
    {
        var audio = _fileLibrary.Files.Where(f => f.Kind == MediaKind.Audio).ToList();
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        if (audio.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("文件库里还没有音频 · 用「导入音频」直接添加") { Enabled = false });
        }
        else
        {
            if (audio.Count > 1)
            {
                menu.Items.Add($"全部添加（{audio.Count} 首）", null, (_, _) => AddTracks(audio.Select(f => f.SourcePath)));
                menu.Items.Add(new ToolStripSeparator());
            }
            foreach (var file in audio)
            {
                var path = file.SourcePath;
                menu.Items.Add(Path.GetFileName(path), null, (_, _) => AddTracks(new[] { path }));
            }
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_addFromLibrary, new Point(0, _addFromLibrary.Height), ToolStripDropDownDirection.AboveRight);
    }

    private void ImportFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true, Title = "导入音频文件到歌单",
            Filter = "音频文件|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|所有文件|*.*",
        };
        if (dialog.ShowDialog(OwnerWindow) != DialogResult.OK) return;
        AddTracks(dialog.FileNames);
    }

    private void MoveTrack(int delta)
    {
        if (Selected is not { } playlist || _tracks.SelectedIndices.Count != 1) return;
        int index = _tracks.SelectedIndex, target = index + delta;
        if (target < 0 || target >= playlist.Tracks.Count) return;
        (playlist.Tracks[index], playlist.Tracks[target]) = (playlist.Tracks[target], playlist.Tracks[index]);
        Save();
        ShowSelected();
        _tracks.SelectedIndex = target;
    }

    private void RemoveTracks()
    {
        if (Selected is not { } playlist || _tracks.SelectedIndices.Count == 0) return;
        int first = _tracks.SelectedIndices.Cast<int>().Min();
        foreach (int index in _tracks.SelectedIndices.Cast<int>().OrderByDescending(i => i)) playlist.Tracks.RemoveAt(index);
        Save();
        RefreshPlaylists(playlist);
        if (_tracks.Items.Count > 0) _tracks.SelectedIndex = Math.Min(first, _tracks.Items.Count - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _music.Changed -= OnMusicChanged;
            _slide.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The drawer's card: a raised surface (the lists sit in it as darker wells) with rounded top corners, a hairline border and a
    /// small grab handle, sitting flush on the music bar.</summary>
    private sealed class SheetPanel : Panel
    {
        public SheetPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var page = new SolidBrush(ModernUi.Background)) g.FillRectangle(page, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int r = LogicalToDeviceUnits(12);
            var rect = new Rectangle(0, 0, Width - 1, Height + r); // bottom corners hidden below the edge
            using var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, 2 * r, 2 * r, 180, 90);
            path.AddArc(rect.Right - 2 * r, rect.Top, 2 * r, 2 * r, 270, 90);
            path.AddLine(rect.Right, rect.Top + r, rect.Right, rect.Bottom);
            path.AddLine(rect.Left, rect.Bottom, rect.Left, rect.Top + r);
            path.CloseFigure();
            using (var fill = new SolidBrush(ModernUi.SurfaceRaised)) g.FillPath(fill, path);
            using (var border = new Pen(ModernUi.Mix(ModernUi.SurfaceRaised, ModernUi.Border, 0.6f))) g.DrawPath(border, path);
            // Grab handle.
            int hw = LogicalToDeviceUnits(36), hh = LogicalToDeviceUnits(4);
            using var handle = new SolidBrush(ModernUi.Mix(ModernUi.SurfaceRaised, ModernUi.Muted, 0.45f));
            using var pill = new GraphicsPath();
            var hr = new Rectangle((Width - hw) / 2, LogicalToDeviceUnits(5), hw, hh);
            pill.AddArc(hr.Left, hr.Top, hh, hh, 90, 180);
            pill.AddArc(hr.Right - hh, hr.Top, hh, hh, 270, 180);
            pill.CloseFigure();
            g.FillPath(handle, pill);
        }
    }
}
