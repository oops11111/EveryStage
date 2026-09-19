using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

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
/// PLANNING.md §8.2's audio "横向播放条" (播放/进度/音量/循环/独立投屏) now exists as
/// <see cref="_audioBarPanel"/> — a persistent row list above <see cref="_listView"/>, unaffected by
/// the category filter tabs (an audio file also still appears as a generic-icon tile in "全部"/"音频"
/// like before; this bar is additive, not a replacement — confirmed with the user before building
/// this, the alternative being to swap the whole grid for a row list whenever "音频" is selected).
/// See <see cref="RefreshAudioBar"/>/<see cref="AudioRow"/> for the row shape and
/// <see cref="OnCastFromAudioBar"/> for what "独立投屏" does (also confirmed with the user: exactly
/// what double-clicking a tile already does — <see cref="FilePlayRequested"/>, no new playback
/// behavior).
///
/// Not implemented (see this project's README "已知风险"/"尚未开始" for the full writeup of why):
/// real video/PDF/audio thumbnails (video and document items show a generic placeholder icon —
/// building a real one means decoding a frame/first page, which is extra work beyond what a file
/// browser strictly needs), and dragging a library item onto an activity (there is no activity list
/// UI yet in this same window for it to be dragged onto).
/// </summary>
public sealed class FilesPanel : UserControl
{
    private readonly FileLibraryStore _library;
    private readonly FileOperationLogger _fileOpLog;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    private readonly PlaybackEngine? _playback;
    private readonly ListView _listView;
    private readonly ImageList _thumbnails;
    private readonly Button _removeButton;
    private readonly Button _addToActivityButton;
    private MediaKind? _activeFilter;

    /// <summary>PLANNING.md §8.2's audio "横向播放条" — see class doc comment. A persistent
    /// <see cref="FlowLayoutPanel"/> (not the "全部/图片/视频/文档/音频" grid <see cref="_listView"/>
    /// already uses) rather than a plain <see cref="Panel"/> with manually-tracked row Y offsets: this
    /// codebase has never needed a scrollable stack of variable-count rows before, and
    /// <see cref="FlowLayoutPanel"/>'s <c>TopDown</c>/<c>AutoScroll</c> combination gives that for free
    /// without hand-rolling layout math this sandbox has no way to visually check. Each row is a
    /// fixed-width <see cref="Panel"/> (see <see cref="AudioRow"/>) rather than stretching to the full
    /// container width — a real but purely cosmetic gap (rows leave dead space on a wide window) this
    /// class accepts rather than adding resize-driven relayout for; see <c>ToastStack</c>'s own doc
    /// comment for why this codebase already avoids leaning on WinForms <c>Anchor</c> for anything
    /// beyond the simplest cases it can't compile-test.</summary>
    private readonly FlowLayoutPanel _audioBarPanel;
    private readonly List<AudioRow> _audioRows = new();
    private readonly System.Windows.Forms.Timer _audioBarRefreshTimer;

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions). Also raised by <see cref="_audioBarPanel"/>'s "投屏播放" button
    /// (<see cref="OnCastFromAudioBar"/>) — confirmed with the user that button should mean exactly
    /// this and nothing more.</summary>
    public event Action<MediaFile>? FilePlayRequested;

    public FilesPanel(
        FileLibraryStore library, FileOperationLogger fileOpLog, ScenarioStore scenarioStore,
        ScenarioRepository scenarioRepository, PlaybackEngine? playback)
    {
        _library = library;
        _fileOpLog = fileOpLog;
        _scenarioStore = scenarioStore;
        _scenarioRepository = scenarioRepository;
        _playback = playback;
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

        _audioBarPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 148,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false, // no audio files yet — RefreshAudioBar flips this once there are any.
        };

        // Control.Controls.Add order determines Dock z-order for same-DockStyle siblings: the LAST
        // one added ends up frontmost, claiming its edge first (see this class's own construction —
        // toolbar, added last, already relies on this to land at the very top with _listView filling
        // whatever's left below it). Adding _audioBarPanel (also Dock.Top) between the two puts it
        // right below toolbar and above _listView, matching the confirmed "persistent bar above the
        // grid" layout.
        Controls.Add(_listView);
        Controls.Add(_audioBarPanel);
        Controls.Add(toolbar);

        // Not tied to this panel's own show/hide — MainWindow.ShowPanel fully removes/re-adds panels
        // from _contentHost on every tab switch rather than hiding them (see that method), so there's
        // no "became visible" hook to start/stop this against the way FloatingPreviewWindow.
        // ShowForActiveOutput/HideForIdleOutput can for its own timer. Left running for this control's
        // whole lifetime instead — same always-on-regardless-of-visibility precedent
        // OverlayWindow._topMostReasserter already sets in this codebase, at a comparable 500ms/2s
        // cadence to FloatingPreviewWindow's own polling.
        _audioBarRefreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioBarRefreshTimer.Tick += (_, _) => RefreshAudioRowLiveState();
        _audioBarRefreshTimer.Start();

        Refresh_();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _audioBarRefreshTimer.Dispose();
        base.Dispose(disposing);
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

        RefreshAudioBar();
    }

    /// <summary>One row of <see cref="_audioBarPanel"/> — see that field's own doc comment for the
    /// container shape. Plain <see cref="Button"/>s/<see cref="Label"/>s/<see cref="CheckBox"/> at
    /// fixed <see cref="Control.Bounds"/>, the same "reuse already-proven control types over anything
    /// new" convention every other dialog in this project already follows (see e.g.
    /// <c>FloatingPreviewWindow._volumeDownButton</c>'s own doc comment).</summary>
    private sealed class AudioRow
    {
        public required MediaFile File { get; init; }
        public required Panel Container { get; init; }
        public required Button PlayPauseButton { get; init; }
        public required Label ProgressLabel { get; init; }
        public required Button VolumeDownButton { get; init; }
        public required Label VolumeLabel { get; init; }
        public required Button VolumeUpButton { get; init; }
        public required CheckBox LoopCheckbox { get; init; }
    }

    /// <summary>Rebuilds every row of <see cref="_audioBarPanel"/> from <see cref="_library"/>'s
    /// current audio files — called from <see cref="Refresh_()"/> (import/remove/filter-tab-click), so
    /// this only needs to run on the same cadence that already rebuilds <see cref="_listView"/>, not on
    /// every <see cref="_audioBarRefreshTimer"/> tick (see <see cref="RefreshAudioRowLiveState"/> for
    /// the lightweight per-tick update that runs instead). Hides the whole bar
    /// (<see cref="_audioBarPanel"/><c>.Visible = false</c>) when there are no audio files at all,
    /// rather than leaving an empty bordered box permanently on screen.
    ///
    /// Explicitly <see cref="Control.Dispose()"/>s each old row's <see cref="AudioRow.Container"/>
    /// before clearing — <see cref="Control.ControlCollection.Clear"/> alone only detaches child
    /// controls from their parent, it does not dispose them, which would otherwise leak a native
    /// window handle per row (multiplied by every button/label/checkbox inside it, since disposing a
    /// container disposes its children too) on every filter-tab click/import/removal — same repeated-
    /// small-leak concern this class's own <see cref="Refresh_()"/> already documents for
    /// <see cref="ImageList"/> thumbnails, just for HWNDs instead of GDI+ bitmaps.</summary>
    private void RefreshAudioBar()
    {
        foreach (var row in _audioRows) row.Container.Dispose();
        _audioBarPanel.Controls.Clear();
        _audioRows.Clear();

        var audioFiles = _library.Files.Where(f => f.Kind == MediaKind.Audio).ToList();
        _audioBarPanel.Visible = audioFiles.Count > 0;
        if (!_audioBarPanel.Visible) return;

        foreach (var file in audioFiles)
        {
            var row = BuildAudioRow(file);
            _audioRows.Add(row);
            _audioBarPanel.Controls.Add(row.Container);
        }

        RefreshAudioRowLiveState();
    }

    private AudioRow BuildAudioRow(MediaFile file)
    {
        var container = new Panel { Width = 632, Height = 30, Margin = new Padding(2) };

        var nameLabel = new Label
        {
            Text = Path.GetFileName(file.SourcePath),
            AutoEllipsis = true,
            Bounds = new Rectangle(4, 4, 200, 22),
        };

        // Pause is meaningful only while THIS row is the one PlaybackEngine.CurrentFile actually
        // is — there is exactly one live standalone-audio slot (PlaybackEngine._audioController), not
        // one per library entry, so every other row's pause/volume controls stay disabled/blank (see
        // RefreshAudioRowLiveState). Starting playback at all is "投屏播放"'s job, not this button's
        // — same division FloatingPreviewWindow's own "暂停" button already has with a fresh
        // RequestPlay/double-click (it can't start a new file either, only pause/resume whatever's
        // already current).
        var playPauseButton = new Button { Text = "暂停", Enabled = false, Bounds = new Rectangle(208, 1, 44, 26) };
        playPauseButton.Click += (_, _) =>
        {
            if (_playback == null || !ReferenceEquals(_playback.CurrentFile, file)) return;
            if (_playback.IsPaused) _playback.Resume(); else _playback.Pause();
            RefreshAudioRowLiveState();
        };

        var progressLabel = new Label { TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(256, 4, 90, 22) };

        // Same fixed-step convention as FloatingPreviewWindow._volumeDownButton/_volumeUpButton
        // (10% per click) — PlaybackEngine.AudioVolume is a single engine-wide value, so these are
        // only enabled while this row is the currently-playing one, same reasoning as playPauseButton
        // above.
        var volumeDownButton = new Button { Text = "－", Enabled = false, Bounds = new Rectangle(350, 1, 26, 26) };
        volumeDownButton.Click += (_, _) =>
        {
            if (_playback == null || !ReferenceEquals(_playback.CurrentFile, file)) return;
            _playback.AudioVolume -= 0.1f;
            RefreshAudioRowLiveState();
        };
        var volumeLabel = new Label { TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(378, 4, 60, 22) };
        var volumeUpButton = new Button { Text = "＋", Enabled = false, Bounds = new Rectangle(440, 1, 26, 26) };
        volumeUpButton.Click += (_, _) =>
        {
            if (_playback == null || !ReferenceEquals(_playback.CurrentFile, file)) return;
            _playback.AudioVolume += 0.1f;
            RefreshAudioRowLiveState();
        };

        // Unlike the controls above, meaningful regardless of whether this row is currently playing —
        // MediaFile.OnCompletion is a persisted per-file property, not live playback state. The first
        // editing entry point this field has ever had for a LIBRARY entry (as opposed to an activity's
        // own copy, which ActivitiesPanel.OnEditCompletionAction already covers) — see
        // FileLibraryStore.Save's own doc comment on why that method needed to become public for this.
        // A 2-state checkbox can only represent 2 of CompletionAction's 3 values; unchecking always
        // sets MediaFile.OnCompletion back to its own default (NextItem) rather than trying to restore
        // some other previous value (HoldOnLastFrame) this checkbox has no way to represent anyway —
        // no real loss, since nothing before this checkbox ever let a library entry's OnCompletion be
        // set to anything but its default in the first place.
        var loopCheckbox = new CheckBox { Text = "循环", Checked = file.OnCompletion == CompletionAction.Loop, Bounds = new Rectangle(470, 4, 56, 22) };
        loopCheckbox.CheckedChanged += (_, _) =>
        {
            var newValue = loopCheckbox.Checked ? CompletionAction.Loop : CompletionAction.NextItem;
            if (newValue == file.OnCompletion) return;
            string oldValue = file.OnCompletion.ToString();
            file.OnCompletion = newValue;
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue, newValue.ToString());
            _library.Save();
        };

        // "独立投屏" — confirmed with the user to mean exactly what double-clicking a tile in
        // _listView already does, nothing new: raise the same FilePlayRequested event MainWindow
        // already wires to PlaybackEngine.RequestPlay(MediaFile).
        var castButton = new Button { Text = "投屏播放", Bounds = new Rectangle(530, 1, 90, 26) };
        castButton.Click += (_, _) => OnCastFromAudioBar(file);

        container.Controls.AddRange(new Control[]
        {
            nameLabel, playPauseButton, progressLabel,
            volumeDownButton, volumeLabel, volumeUpButton,
            loopCheckbox, castButton,
        });

        return new AudioRow
        {
            File = file,
            Container = container,
            PlayPauseButton = playPauseButton,
            ProgressLabel = progressLabel,
            VolumeDownButton = volumeDownButton,
            VolumeLabel = volumeLabel,
            VolumeUpButton = volumeUpButton,
            LoopCheckbox = loopCheckbox,
        };
    }

    /// <summary><see cref="_audioBarRefreshTimer"/>'s own per-tick update — lightweight compared to
    /// <see cref="RefreshAudioBar"/>: only touches the small set of controls that reflect LIVE
    /// playback state (play/pause text, volume, position), never rebuilds the row list itself. Exactly
    /// one row (if any) can be <see cref="PlaybackEngine.CurrentFile"/> at a time — reference equality,
    /// not <see cref="MediaFile.Id"/>, since this compares the exact same library <see cref="MediaFile"/>
    /// instance <see cref="PlaybackEngine"/> was handed (<c>FilePlayRequested</c> never clones it, see
    /// class doc comment) — every other row gets its live-state controls disabled/blanked rather than
    /// left showing stale values from whenever it last was the current file.</summary>
    private void RefreshAudioRowLiveState()
    {
        MediaFile? currentFile = _playback?.CurrentFile;
        foreach (var row in _audioRows)
        {
            bool isCurrent = currentFile != null && ReferenceEquals(currentFile, row.File);
            row.PlayPauseButton.Enabled = isCurrent;
            row.PlayPauseButton.Text = isCurrent && _playback!.IsPaused ? "继续" : "暂停";
            row.VolumeDownButton.Enabled = isCurrent;
            row.VolumeUpButton.Enabled = isCurrent;
            row.VolumeLabel.Text = isCurrent ? $"{(int)Math.Round(_playback!.AudioVolume * 100)}%" : "";
            row.ProgressLabel.Text = isCurrent ? FormatPosition(_playback!.AudioPosition, _playback!.AudioDuration) : "";
        }
    }

    /// <summary>"独立投屏" — see <see cref="BuildAudioRow"/>'s own doc comment on why this is
    /// deliberately just <see cref="FilePlayRequested"/> and nothing more. Refreshes live row state
    /// immediately afterward rather than waiting for <see cref="_audioBarRefreshTimer"/>'s next tick —
    /// <c>MainWindow</c>'s subscriber calls <c>PlaybackEngine.RequestPlay</c> synchronously within this
    /// same event invocation (nothing here is fire-and-forget), so by the time
    /// <see cref="FilePlayRequested"/> returns, playback has already actually started; same "avoid a
    /// visible up-to-500ms lag" reasoning as <c>FloatingPreviewWindow</c>'s own seek/volume button
    /// handlers refreshing immediately rather than waiting for their own poll.</summary>
    private void OnCastFromAudioBar(MediaFile file)
    {
        FilePlayRequested?.Invoke(file);
        RefreshAudioRowLiveState();
    }

    /// <summary>"0:15" or, when <paramref name="duration"/> is known, "0:15 / 3:42" — copied from
    /// <c>FloatingPreviewWindow.FormatPosition</c> rather than shared: same small-pure-helper
    /// duplication convention this project already uses for e.g. each
    /// <c>ContentEngine.AudioContentController</c>/<c>VideoContentController</c>'s own independently-
    /// verifiable fade math.</summary>
    private static string FormatPosition(TimeSpan position, TimeSpan? duration)
    {
        string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return duration.HasValue ? $"{Format(position)} / {Format(duration.Value)}" : Format(position);
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
