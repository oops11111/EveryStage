using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 文件面板 ("默认首页"): thumbnail grid, category tabs (全部/图片/视频/文档/音频),
/// click a file to play it. Supports both an "导入" file picker and dragging files in from Explorer
/// (§11 "拖拽添加") for getting content into the library in the first place.
///
/// Not implemented (see this project's README for the full list): real video/PDF/audio thumbnails
/// (video and document items show a generic placeholder icon — building a real one means decoding a
/// frame/first page, which is extra work beyond what a file browser strictly needs), audio's
/// "横向播放条" treatment (§8.2 describes audio rows differently from the grid — audio playback
/// itself isn't implemented anywhere in this repo yet, so there is nothing to wire a play bar to),
/// batch selection with a floating toolbar (§11), and dragging a library item onto an activity
/// (there is no activity list UI yet in this same window for it to be dragged onto).
/// </summary>
public sealed class FilesPanel : UserControl
{
    private readonly FileLibraryStore _library;
    private readonly ListView _listView;
    private readonly ImageList _thumbnails;
    private MediaKind? _activeFilter;

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions).</summary>
    public event Action<MediaFile>? FilePlayRequested;

    public FilesPanel(FileLibraryStore library)
    {
        _library = library;
        Dock = DockStyle.Fill;
        AllowDrop = true;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, FlowDirection = FlowDirection.LeftToRight };
        toolbar.Controls.Add(MakeFilterButton("全部", null));
        toolbar.Controls.Add(MakeFilterButton("图片", MediaKind.Image));
        toolbar.Controls.Add(MakeFilterButton("视频", MediaKind.Video));
        toolbar.Controls.Add(MakeFilterButton("文档", MediaKind.Document));
        toolbar.Controls.Add(MakeFilterButton("音频", MediaKind.Audio));

        var importButton = new Button { Text = "导入...", AutoSize = true };
        importButton.Click += (_, _) => ImportViaDialog();
        toolbar.Controls.Add(importButton);

        _thumbnails = new ImageList { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            LargeImageList = _thumbnails,
            MultiSelect = false, // batch selection (§11) isn't implemented yet — see class doc comment.
        };
        _listView.DoubleClick += (_, _) =>
        {
            if (_listView.SelectedItems.Count > 0 && _listView.SelectedItems[0].Tag is MediaFile file)
                FilePlayRequested?.Invoke(file);
        };

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _listView.AllowDrop = true;

        Controls.Add(_listView);
        Controls.Add(toolbar);

        Refresh_();
    }

    private Button MakeFilterButton(string label, MediaKind? filter)
    {
        var button = new Button { Text = label, AutoSize = true };
        button.Click += (_, _) => { _activeFilter = filter; Refresh_(); };
        return button;
    }

    private void ImportViaDialog()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "导入文件到文件库" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            ImportPaths(paths);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            if (_library.Import(path) == null) rejected.Add(Path.GetFileName(path));
        }
        Refresh_();

        if (rejected.Count > 0)
        {
            MessageBox.Show(this,
                $"以下文件的类型不受支持，未导入：\n{string.Join("\n", rejected)}",
                "导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Call after the library changes from outside this control (e.g. a file removed
    /// elsewhere) to re-pull the list. Named with a trailing underscore only to avoid colliding
    /// with <see cref="Control.Refresh"/>, which does something unrelated (repaint).</summary>
    public void Refresh_()
    {
        _thumbnails.Images.Clear();
        _listView.Items.Clear();

        foreach (var file in _library.Files.Where(f => _activeFilter == null || f.Kind == _activeFilter))
        {
            string key = file.Id.ToString();
            _thumbnails.Images.Add(key, BuildThumbnail(file));

            var item = new ListViewItem(Path.GetFileName(file.SourcePath), key) { Tag = file };
            _listView.Items.Add(item);
        }
    }

    private static Image BuildThumbnail(MediaFile file)
    {
        if (file.Kind == MediaKind.Image)
        {
            try
            {
                using var stream = File.OpenRead(file.SourcePath);
                using var loaded = Image.FromStream(stream);
                return new Bitmap(loaded, new Size(96, 96));
            }
            catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException)
            {
                // Missing/moved/corrupt file since it was imported — show a warning icon rather
                // than let a bad file crash the whole library view.
                return SystemIcons.Warning.ToBitmap();
            }
        }

        // Video/Document/Audio: generic placeholder — see class doc comment on why a real
        // thumbnail (decoded video frame / PDF first page / waveform) isn't implemented here.
        return SystemIcons.Application.ToBitmap();
    }
}
