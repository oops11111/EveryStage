using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 文件面板 ("默认首页"): thumbnail grid, category tabs (全部/图片/视频/文档/音频),
/// click a file to play it. Supports both an "导入" file picker and dragging files in from Explorer
/// (§11 "拖拽添加") for getting content into the library in the first place, plus a "移除" button for
/// taking a file back out of the library — the counterpart operation, previously entirely absent
/// from this panel (see this project's README's history on `FileOperationLogger` having a
/// `LogFileRemoved` method with no caller anywhere in the repo; there was no UI path to actually
/// remove a library file at all, not just a missing log call).
///
/// Removing a library entry never touches any <see cref="Activity"/> that already contains a copy of
/// it: <c>ActivitiesPanel</c>'s "添加文件到活动" deep-copies a <see cref="MediaFile"/>
/// (<c>ActivitiesPanel.CloneFile</c>) rather than referencing the library entry by
/// <see cref="MediaFile.Id"/>, so an activity's files have no dependency on the library entry they
/// originated from still existing — see this project's README ("已知风险" #22) on this copy-not-
/// reference behavior being a deliberate, previously-documented choice, not something decided here.
///
/// PLANNING.md §11's "批量选择" now half-works: <see cref="_listView"/> allows <c>MultiSelect</c> and
/// "移除" (<see cref="OnRemoveClick"/>) handles any number of selected files at once — reusing the
/// existing always-visible toolbar button rather than a separate floating one, since it already only
/// enables once something is selected, the same enable-on-selection spirit §11's "选中后悬浮工具栏
/// 出现" describes. The other two actions §11 lists for the same selection, "加入活动" and "统一
/// 设置属性", are NOT implemented — see this project's README "尚未开始" for why each needs either
/// new cross-panel plumbing (an activity picker reachable from here, which panel/scenario context to
/// add into) or product decisions PLANNING.md doesn't specify (how a multi-file property editor
/// should show/resolve conflicting existing values across the selection) that this round didn't
/// attempt.
///
/// Not implemented (see this project's README "已知风险"/"尚未开始" for the full writeup of why):
/// real video/PDF/audio thumbnails (video and document items show a generic placeholder icon —
/// building a real one means decoding a frame/first page, which is extra work beyond what a file
/// browser strictly needs), audio's "横向播放条" treatment (§8.2 describes audio rows differently
/// from the grid — non-background audio playback itself has real behavior now via
/// <see cref="ContentEngine.AudioContentController"/>, but that controller has no pause/resume, seek,
/// or volume control at all, which is most of what a real play bar would need to actually do
/// something rather than just look like PLANNING.md's mockup), and dragging a library item onto an
/// activity (there is no activity list UI yet in this same window for it to be dragged onto).
/// </summary>
public sealed class FilesPanel : UserControl
{
    private readonly FileLibraryStore _library;
    private readonly FileOperationLogger _fileOpLog;
    private readonly ListView _listView;
    private readonly ImageList _thumbnails;
    private readonly Button _removeButton;
    private MediaKind? _activeFilter;

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions).</summary>
    public event Action<MediaFile>? FilePlayRequested;

    public FilesPanel(FileLibraryStore library, FileOperationLogger fileOpLog)
    {
        _library = library;
        _fileOpLog = fileOpLog;
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

        _removeButton = new Button { Text = "移除", AutoSize = true, Enabled = false };
        _removeButton.Click += OnRemoveClick;
        toolbar.Controls.Add(_removeButton);

        _thumbnails = new ImageList { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            LargeImageList = _thumbnails,
            // PLANNING.md §11 "批量选择": Ctrl/Shift+点击 multi-select now works and "删除" (below)
            // handles any number of selected items — see class doc comment for why "加入活动"/
            // "统一设置属性" (the other two actions §11 describes for the same selection) aren't
            // attempted here, and why this reuses the existing always-visible toolbar's "移除" button
            // rather than building a separate floating one for the same enable/disable-on-selection
            // behavior "选中后悬浮工具栏出现" already describes in spirit.
            MultiSelect = true,
        };
        _listView.SelectedIndexChanged += (_, _) => _removeButton.Enabled = _listView.SelectedItems.Count > 0;
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

    /// <summary>PLANNING.md §11 "批量选择" — the "删除" half of it (see class doc comment for why
    /// "加入活动"/"统一设置属性" aren't attempted here). Handles any number of selected items, not
    /// just one, now that <see cref="_listView"/> allows <c>MultiSelect</c>.</summary>
    private void OnRemoveClick(object? sender, EventArgs e)
    {
        var files = _listView.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList();
        if (files.Count == 0) return;

        string prompt = files.Count == 1
            ? $"确定要从文件库中移除 \"{Path.GetFileName(files[0].SourcePath)}\" 吗？"
            : $"确定要从文件库中移除选中的 {files.Count} 个文件吗？";
        var confirm = MessageBox.Show(this,
            prompt + "\n已经添加到某个活动里的副本不受影响——活动保存的是独立拷贝，不是对这个文件库条目的引用。",
            "移除文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        foreach (var file in files)
        {
            _library.Remove(file.Id);
            _fileOpLog.LogFileRemoved(file.Id, file.SourcePath);
        }
        Refresh_();
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            var imported = _library.Import(path);
            if (imported == null) rejected.Add(Path.GetFileName(path));
            else _fileOpLog.LogFileImported(imported.Id, imported.SourcePath);
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
