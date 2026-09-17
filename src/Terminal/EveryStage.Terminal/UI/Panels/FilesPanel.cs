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
/// it: both <c>ActivitiesPanel</c>'s "添加文件到活动" and this panel's own <see cref="OnAddToActivityClick"/>
/// deep-copy a <see cref="MediaFile"/> (<see cref="MediaFile.Clone"/>) rather than referencing the
/// library entry by <see cref="MediaFile.Id"/>, so an activity's files have no dependency on the
/// library entry they originated from still existing — see this project's README ("已知风险" #22) on
/// this copy-not-reference behavior being a deliberate, previously-documented choice, not something
/// decided here.
///
/// PLANNING.md §11's "批量选择" now covers two of its three actions: <see cref="_listView"/> allows
/// <c>MultiSelect</c>, "移除" (<see cref="OnRemoveClick"/>) handles any number of selected files at
/// once, and "加入活动" (<see cref="OnAddToActivityClick"/>, added once <see cref="ActivityPickerDialog"/>
/// gave this panel a way to pick a destination scenario/activity — see this project's README) does
/// too — both reuse the existing always-visible toolbar rather than a separate floating one, since it
/// already only enables once something is selected, the same enable-on-selection spirit §11's "选中后
/// 悬浮工具栏出现" describes. The remaining action, "统一设置属性", is NOT implemented — it needs a
/// product decision PLANNING.md doesn't specify (how a multi-file property editor should show/resolve
/// conflicting existing values across the selection) that this round didn't attempt; see this
/// project's README "尚未开始".
///
/// Not implemented (see this project's README "已知风险"/"尚未开始" for the full writeup of why):
/// real video/PDF/audio thumbnails (video and document items show a generic placeholder icon —
/// building a real one means decoding a frame/first page, which is extra work beyond what a file
/// browser strictly needs), audio's "横向播放条" treatment (§8.2 describes audio rows differently
/// from the grid — non-background audio playback itself has real behavior now via
/// <see cref="ContentEngine.AudioContentController"/>, which now also has real pause/resume (see this
/// project's README "已知风险"), but still no seek or volume control at all, which is most of the
/// rest of what a real play bar would need to actually do something rather than just look like
/// PLANNING.md's mockup), and dragging a library item onto an
/// activity (there is no activity list UI yet in this same window for it to be dragged onto).
/// </summary>
public sealed class FilesPanel : UserControl
{
    private readonly FileLibraryStore _library;
    private readonly FileOperationLogger _fileOpLog;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    private readonly ListView _listView;
    private readonly ImageList _thumbnails;
    private readonly Button _removeButton;
    private readonly Button _addToActivityButton;
    private MediaKind? _activeFilter;

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions).</summary>
    public event Action<MediaFile>? FilePlayRequested;

    public FilesPanel(FileLibraryStore library, FileOperationLogger fileOpLog, ScenarioStore scenarioStore, ScenarioRepository scenarioRepository)
    {
        _library = library;
        _fileOpLog = fileOpLog;
        _scenarioStore = scenarioStore;
        _scenarioRepository = scenarioRepository;
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

        // PLANNING.md §11 "批量选择"'s "加入活动" — previously left unimplemented (see this
        // project's README "尚未开始") specifically because this panel had no activity-list UI of
        // its own to add into; ActivityPickerDialog now supplies exactly that. Same
        // enable-on-selection reuse of the always-visible toolbar as _removeButton above, handles
        // any number of selected files at once.
        _addToActivityButton = new Button { Text = "加入活动...", AutoSize = true, Enabled = false };
        _addToActivityButton.Click += OnAddToActivityClick;
        toolbar.Controls.Add(_addToActivityButton);

        _thumbnails = new ImageList { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            LargeImageList = _thumbnails,
            // PLANNING.md §11 "批量选择": Ctrl/Shift+点击 multi-select now works, and both "删除"
            // (below) and "加入活动" (OnAddToActivityClick) handle any number of selected items —
            // see class doc comment for why the remaining action, "统一设置属性", still isn't
            // attempted, and why this reuses the existing always-visible toolbar buttons rather than
            // building a separate floating one for the same enable/disable-on-selection behavior
            // "选中后悬浮工具栏出现" already describes in spirit.
            MultiSelect = true,
        };
        _listView.SelectedIndexChanged += (_, _) =>
        {
            bool hasSelection = _listView.SelectedItems.Count > 0;
            _removeButton.Enabled = hasSelection;
            _addToActivityButton.Enabled = hasSelection;
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

    /// <summary>PLANNING.md §11 "批量选择" — the "删除" half of it (see class doc comment for the
    /// other two actions §11 describes for the same selection). Handles any number of selected
    /// items, not just one, now that <see cref="_listView"/> allows <c>MultiSelect</c>.</summary>
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

    /// <summary>PLANNING.md §11 "批量选择"'s "加入活动" half — see class doc comment for why this
    /// was previously left unimplemented, and <see cref="ActivityPickerDialog"/> for the picker this
    /// needed that didn't exist until now. Handles any number of selected files at once, same as
    /// <see cref="OnRemoveClick"/>. Deep-copies each selected file (<see cref="MediaFile.Clone"/>)
    /// into the chosen activity — same "always an independent copy, never a shared reference"
    /// behavior <c>ActivitiesPanel</c>'s own "添加文件..." already established (see this project's
    /// README "已知风险" #22). Doesn't refresh <c>ActivitiesPanel</c>'s own tree directly: this
    /// panel has no reference to it, and <c>MainWindow.ShowPanel</c> already calls
    /// <c>ActivitiesPanel.RefreshTree()</c> every time the operator navigates there, which is the
    /// same lazy-refresh-on-show convention every panel in this app already relies on (see e.g.
    /// <c>SettingsPanel.Refresh_</c>'s own doc comment on why only the 显示 tab needs it) rather than
    /// pushing a live update across panels that aren't currently visible anyway.</summary>
    private void OnAddToActivityClick(object? sender, EventArgs e)
    {
        var files = _listView.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList();
        if (files.Count == 0 || _scenarioStore.Scenarios.Count == 0) return;

        using var dialog = new ActivityPickerDialog(_scenarioStore.Scenarios, _scenarioStore.CurrentScenarioId);
        if (dialog.ShowDialog(this) != DialogResult.OK
            || dialog.SelectedScenario == null || dialog.SelectedActivity == null)
            return;

        foreach (var file in files) dialog.SelectedActivity.Files.Add(file.Clone());
        _fileOpLog.LogActivityModified(dialog.SelectedScenario.Id, dialog.SelectedActivity.Id, dialog.SelectedActivity.Name);
        _scenarioRepository.Save(_scenarioStore);

        MessageBox.Show(this,
            files.Count == 1
                ? $"已将 \"{Path.GetFileName(files[0].SourcePath)}\" 加入活动 \"{dialog.SelectedActivity.Name}\"。"
                : $"已将选中的 {files.Count} 个文件加入活动 \"{dialog.SelectedActivity.Name}\"。",
            "加入活动", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            // ImageList.Images.Add copies the bitmap's pixel data into its own native image list
            // handle — it does not take ownership of (or ever dispose) the Image instance passed in,
            // so without this `using`, every BuildThumbnail() result here would leak its GDI+ handle
            // until the next GC finalizer pass happens to run. This matters more than a one-off leak
            // would: Refresh_() re-runs on every filter tab click, import, and removal (MainWindow
            // also calls it every time the user switches to the 文件 tab), on what PLANNING.md §14.4
            // frames as an always-on, unattended device — repeated small leaks like this are exactly
            // the shape of thing that eventually exhausts the process's GDI object quota on a machine
            // that's never restarted.
            using (var thumbnail = BuildThumbnail(file))
            {
                _thumbnails.Images.Add(key, thumbnail);
            }

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
