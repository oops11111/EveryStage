using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 「背景音乐歌单」: playlists on the left (新建 / 重命名 / 删除), the selected playlist's songs on the right
/// (从文件库添加 / 导入音频文件 / 上移 / 下移 / 移除, double-click plays from that song), its play mode, and
/// the channel options (大屏视频有声音时压低音乐, 启动后自动播放此歌单). Every change is saved immediately.
/// </summary>
internal sealed class PlaylistDialog : Form
{
    private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".wma", ".ogg" };
    private readonly MusicChannel _music;
    private readonly FileLibraryStore _fileLibrary;
    private readonly ListBox _playlists;
    private readonly ListBox _tracks;
    private readonly ComboBox _mode;
    private readonly CheckBox _duck;
    private readonly CheckBox _autoPlay;
    private bool _updating;

    private MusicLibrary Library => _music.Library;
    private Playlist? Selected => _playlists.SelectedIndex >= 0 && _playlists.SelectedIndex < Library.Playlists.Count ? Library.Playlists[_playlists.SelectedIndex] : null;

    public PlaylistDialog(MusicChannel music, FileLibraryStore fileLibrary)
    {
        _music = music;
        _fileLibrary = fileLibrary;
        Text = "背景音乐歌单";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(640, 420);

        var playlistLabel = new Label { Text = "歌单", Bounds = new Rectangle(16, 12, 200, 22) };
        _playlists = new ListBox { Bounds = new Rectangle(16, 38, 200, 340), IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left };
        var newPlaylist = Button("＋ 新建", new Rectangle(16, 386, 64, 30), NewPlaylist);
        var rename = Button("重命名", new Rectangle(84, 386, 64, 30), RenamePlaylist);
        var delete = Button("删除", new Rectangle(152, 386, 64, 30), DeletePlaylist);
        foreach (var b in new[] { newPlaylist, rename, delete }) b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        var trackLabel = new Label { Text = "歌曲（双击从这首开始播放）", Bounds = new Rectangle(232, 12, 300, 22) };
        _tracks = new ListBox
        {
            Bounds = new Rectangle(232, 38, 410, 300), IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        var addFromLibrary = Button("＋ 从文件库添加", new Rectangle(650, 38, 100, 30), AddFromLibrary);
        var import = Button("导入音频文件", new Rectangle(650, 74, 100, 30), ImportFiles);
        var up = Button("上移", new Rectangle(650, 122, 100, 30), () => MoveTrack(-1));
        var down = Button("下移", new Rectangle(650, 158, 100, 30), () => MoveTrack(1));
        var remove = Button("移除", new Rectangle(650, 194, 100, 30), RemoveTracks);
        foreach (var b in new[] { addFromLibrary, import, up, down, remove }) b.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var modeLabel = new Label { Text = "播放方式", Bounds = new Rectangle(232, 350, 70, 24), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(304, 348, 150, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _mode.Items.AddRange(new object[] { "列表循环", "顺序播完停止", "单曲循环", "随机播放" });
        _autoPlay = new CheckBox { Text = "终端机启动后自动播放此歌单", AutoSize = true, Location = new Point(232, 386), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _duck = new CheckBox { Text = "大屏视频有声音时，自动把音乐压低到 20%", AutoSize = true, Location = new Point(232, 414), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Checked = Library.DuckForVideo };
        var note = new Label
        {
            Text = "背景音乐独立于预览和大屏画面播放，切换画面不会打断音乐。",
            ForeColor = Color.DimGray, AutoSize = true, Location = new Point(16, 448), Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        var close = new Button { Text = "完成", DialogResult = DialogResult.OK, Bounds = new Rectangle(650, 440, 100, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        AcceptButton = close;
        CancelButton = close;

        Controls.AddRange(new Control[]
        {
            playlistLabel, _playlists, newPlaylist, rename, delete, trackLabel, _tracks, addFromLibrary, import, up, down, remove,
            modeLabel, _mode, _autoPlay, _duck, note, close,
        });

        _playlists.SelectedIndexChanged += (_, _) => ShowSelected();
        _tracks.DoubleClick += (_, _) =>
        {
            if (Selected is { } playlist && _tracks.SelectedIndex >= 0) _music.Play(playlist, _tracks.SelectedIndex);
        };
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
        _duck.CheckedChanged += (_, _) => { Library.DuckForVideo = _duck.Checked; Save(); };

        ModernUi.StyleDialog(this);
        RefreshPlaylists(Library.Current);
    }

    private RoundedActionButton Button(string text, Rectangle bounds, Action onClick)
    {
        var button = new RoundedActionButton { Text = text, Bounds = bounds };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void RefreshPlaylists(Playlist? select)
    {
        _playlists.BeginUpdate();
        _playlists.Items.Clear();
        foreach (var playlist in Library.Playlists) _playlists.Items.Add($"{playlist.Name}（{playlist.Tracks.Count} 首）");
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
            foreach (var track in playlist.Tracks) _tracks.Items.Add(Path.GetFileName(track) + (File.Exists(track) ? "" : "（文件不存在）"));
        _tracks.EndUpdate();
        _mode.Enabled = _autoPlay.Enabled = playlist != null;
        _mode.SelectedIndex = playlist == null ? -1 : (int)playlist.Mode;
        _autoPlay.Checked = playlist?.AutoPlayOnStartup == true;
        if (playlist != null) Library.CurrentPlaylistId = playlist.Id;
        _updating = false;
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
            MessageBox.Show(this, $"歌单未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void NewPlaylist()
    {
        if (TextInputDialog.Prompt(this, "新建歌单", "歌单名称：", $"歌单 {Library.Playlists.Count + 1}") is not { } name) return;
        var playlist = new Playlist { Name = name };
        Library.Playlists.Add(playlist);
        Save();
        RefreshPlaylists(playlist);
    }

    private void RenamePlaylist()
    {
        if (Selected is not { } playlist || TextInputDialog.Prompt(this, "重命名歌单", "歌单名称：", playlist.Name) is not { } name) return;
        playlist.Name = name;
        Save();
        RefreshPlaylists(playlist);
    }

    private void DeletePlaylist()
    {
        if (Selected is not { } playlist) return;
        if (MessageBox.Show(this, $"删除歌单“{playlist.Name}”？（音频文件本身不会被删除）", "删除歌单", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Library.Playlists.Remove(playlist);
        Save();
        RefreshPlaylists(null);
    }

    /// <summary>Adds paths to the selected playlist (creating 「默认歌单」 when there is none).</summary>
    public void AddTracks(IEnumerable<string> paths)
    {
        var playlist = Selected;
        if (playlist == null)
        {
            playlist = new Playlist { Name = "默认歌单" };
            Library.Playlists.Add(playlist);
        }
        playlist.Tracks.AddRange(paths);
        Save();
        RefreshPlaylists(playlist);
    }

    private void AddFromLibrary()
    {
        var audio = _fileLibrary.Files.Where(f => f.Kind == MediaKind.Audio).ToList();
        if (audio.Count == 0)
        {
            MessageBox.Show(this, "文件库里还没有音频。可以用「导入音频文件」直接添加。", "从文件库添加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var picker = new Form
        {
            Text = "从文件库添加", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(420, 360),
        };
        var list = new CheckedListBox { Bounds = new Rectangle(12, 12, 396, 296), CheckOnClick = true, IntegralHeight = false };
        foreach (var file in audio) list.Items.Add(Path.GetFileName(file.SourcePath));
        var ok = new Button { Text = "添加", DialogResult = DialogResult.OK, Bounds = new Rectangle(236, 318, 82, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(326, 318, 82, 30) };
        picker.AcceptButton = ok;
        picker.CancelButton = cancel;
        picker.Controls.AddRange(new Control[] { list, ok, cancel });
        ModernUi.StyleDialog(picker);
        if (picker.ShowDialog(this) != DialogResult.OK || list.CheckedIndices.Count == 0) return;
        AddTracks(list.CheckedIndices.Cast<int>().Select(i => audio[i].SourcePath));
    }

    private void ImportFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true, Title = "导入音频文件到歌单",
            Filter = "音频文件|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
        foreach (int index in _tracks.SelectedIndices.Cast<int>().OrderByDescending(i => i)) playlist.Tracks.RemoveAt(index);
        Save();
        RefreshPlaylists(playlist);
    }
}
