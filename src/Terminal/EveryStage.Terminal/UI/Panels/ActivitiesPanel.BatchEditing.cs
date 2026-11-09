using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    /// <summary>Opens <see cref="BatchPropertiesDialog"/> for the current
    /// <see cref="_multiSelectedFileNodes"/> and, for each property row the operator checked, applies
    /// the single chosen value to every selected file — same per-property
    /// <see cref="FileOperationLogger.LogPlaybackPropertyChanged"/> granularity every single-file
    /// Apply* method already uses, just looped over more than one file per property. A property
    /// row left unchecked in the dialog is skipped entirely here — no file's existing value for it is
    /// read, compared, or touched, exactly the "opt-in, default don't-change" design confirmed with the
    /// user before this method was written.</summary>
    private void OnEditBatchProperties()
    {
        var scenario = CurrentScenario;
        if (scenario == null) return;

        var files = _multiSelectedFileNodes.Select(n => n.Tag).OfType<MediaFile>().ToList();
        if (files.Count < 2) return;

        using var dialog = new BatchPropertiesDialog(files);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        bool anyChange = false;

        if (dialog.ChangePlayMode)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string? oldValue = file.PlayModeOverride?.ToString();
                string? newValue = dialog.PlayModeValue?.ToString();
                file.PlayModeOverride = dialog.PlayModeValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.PlayModeOverride), oldValue, newValue);
            }
        }
        if (dialog.ChangeOnCompletion)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string oldValue = file.OnCompletion.ToString();
                string newValue = dialog.OnCompletionValue.ToString();
                file.OnCompletion = dialog.OnCompletionValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue, newValue);
            }
        }
        if (dialog.ChangeAllowManualSkip)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string oldValue = file.AllowManualSkip.ToString();
                string newValue = dialog.AllowManualSkipValue.ToString();
                file.AllowManualSkip = dialog.AllowManualSkipValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.AllowManualSkip), oldValue, newValue);
            }
        }
        if (dialog.ChangeStayDuration)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string? oldValue = file.StayDuration?.ToString();
                string? newValue = dialog.StayDurationValue?.ToString();
                file.StayDuration = dialog.StayDurationValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.StayDuration), oldValue, newValue);
            }
        }
        if (dialog.ChangeFade)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string oldVolumeFollowsFade = file.VolumeFollowsFade.ToString();
                string? oldFadeDuration = file.FadeDuration?.ToString();
                file.VolumeFollowsFade = dialog.VolumeFollowsFadeValue;
                file.FadeDuration = dialog.FadeDurationValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.VolumeFollowsFade),
                    oldVolumeFollowsFade, dialog.VolumeFollowsFadeValue.ToString());
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.FadeDuration),
                    oldFadeDuration, dialog.FadeDurationValue?.ToString());
            }
        }
        if (dialog.ChangeBackgroundAudio)
        {
            anyChange = true;
            foreach (var file in files)
            {
                string oldIsBackgroundAudio = file.IsBackgroundAudio.ToString();
                string oldVisual = file.BackgroundAudioVisual.ToString();
                file.IsBackgroundAudio = dialog.IsBackgroundAudioValue;
                file.BackgroundAudioVisual = dialog.BackgroundAudioVisualValue;
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.IsBackgroundAudio),
                    oldIsBackgroundAudio, dialog.IsBackgroundAudioValue.ToString());
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.BackgroundAudioVisual),
                    oldVisual, dialog.BackgroundAudioVisualValue.ToString());
            }
        }

        if (!anyChange) return; // every row left unchecked — nothing to save/refresh.

        if (!TrySave()) return;
        // BuildFileNodeText depends on IsBackgroundAudio, and RefreshTree also clears
        // _multiSelectedFileNodes (see that method's own comment) — both reasons the audio
        // property apply path refreshes after its own IsBackgroundAudio change apply here too.
        RefreshTree();
    }

    private void FillMusicCueMenu()
    {
        _musicCueItem.DropDownItems.Clear();
        if (GetSelection() is not (var scenario, { } activity, _) || scenario == null) return;
        void Add(string text, bool chosen, Action apply)
        {
            var item = new ToolStripMenuItem(text) { Checked = chosen };
            item.Click += (_, _) =>
            {
                var (oldId, oldFade) = (activity.MusicPlaylistId, activity.MusicFadeOut);
                apply();
                if (!TrySave()) { (activity.MusicPlaylistId, activity.MusicFadeOut) = (oldId, oldFade); return; }
                _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
                RefreshCardStyles();
            };
            _musicCueItem.DropDownItems.Add(item);
        }
        Add("不改变（继续当前音乐）", activity.MusicPlaylistId == null && !activity.MusicFadeOut, () => { activity.MusicPlaylistId = null; activity.MusicFadeOut = false; });
        Add("淡出停止", activity.MusicFadeOut, () => { activity.MusicPlaylistId = null; activity.MusicFadeOut = true; });
        var playlists = Playlists?.Invoke() ?? Array.Empty<Playlist>();
        if (playlists.Count > 0) _musicCueItem.DropDownItems.Add(new ToolStripSeparator());
        foreach (var playlist in playlists)
            Add($"切换到歌单：{playlist.Name}", activity.MusicPlaylistId == playlist.Id,
                () => { activity.MusicPlaylistId = playlist.Id; activity.MusicFadeOut = false; });
        if (playlists.Count == 0) _musicCueItem.DropDownItems.Add(new ToolStripMenuItem("（还没有歌单 · 在底部音乐条「歌单」里新建）") { Enabled = false });
    }

    private void FillTransitionMenu()
    {
        _transitionItem.DropDownItems.Clear();
        if (GetSelection() is not (var scenario, { } activity, { } file) || scenario == null) return;
        foreach (var (text, value) in new (string, TransitionStyle?)[]
                 { ("默认（设置里的大屏过场）", null), ("直切", TransitionStyle.Cut), ("叠化", TransitionStyle.Dissolve), ("经黑场", TransitionStyle.DipToBlack) })
        {
            var item = new ToolStripMenuItem(text) { Checked = file.TransitionOverride == value };
            item.Click += (_, _) =>
            {
                var old = file.TransitionOverride;
                file.TransitionOverride = value;
                if (!TrySave()) { file.TransitionOverride = old; return; }
                _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.TransitionOverride), old?.ToString(), value?.ToString());
                RefreshCardStyles();
            };
            _transitionItem.DropDownItems.Add(item);
        }
    }

    /// <summary>Fills 「移动到环节 ▸」at submenu-open time, same fill-on-open pattern as
    /// <see cref="FillMusicCueMenu"/>/<see cref="FillTransitionMenu"/>. Single selection: one
    /// item per OTHER activity of the current scenario (the file's own activity is pointless
    /// as a target and is left out). A 2+ multi-selection (AP-07): EVERY activity is listed —
    /// the set can span several, so no single "own" activity exists to exclude — and the click
    /// moves the whole set. Either way the click captures the target object itself, not an
    /// index — the list can be reordered between fill and click, the object cannot go stale.</summary>
    private void FillMoveFileToActivityMenu()
    {
        _moveFileToActivityItem.DropDownItems.Clear();
        if (_multiSelectedFileNodes.Count >= 2)
        {
            if (CurrentScenario is not { } batchScenario) return;
            var batch = MultiSelectedFilesInOrder();
            foreach (var target in batchScenario.Activities)
            {
                var captured = target;
                var item = new ToolStripMenuItem(captured.Name);
                item.Click += (_, _) => MoveFilesToActivity(captured, batch);
                _moveFileToActivityItem.DropDownItems.Add(item);
            }
            return;
        }
        if (GetSelection() is not (var scenario, { } activity, { } file) || scenario == null) return;
        foreach (var target in scenario.Activities)
        {
            if (ReferenceEquals(target, activity)) continue;
            var captured = target;
            var item = new ToolStripMenuItem(captured.Name);
            item.Click += (_, _) => MoveFileToActivity(captured);
            _moveFileToActivityItem.DropDownItems.Add(item);
        }
        // UpdateButtonStates already disables the submenu unless another activity exists, so
        // this is only a belt-and-braces empty state, worded like FillMusicCueMenu's own.
        if (_moveFileToActivityItem.DropDownItems.Count == 0)
            _moveFileToActivityItem.DropDownItems.Add(new ToolStripMenuItem("（没有其他环节）") { Enabled = false });
    }

    /// <summary>The multi-selected files with their owning activities, in the tree's flat
    /// order — the one canonical enumeration every batch action (AP-07) works from, so a
    /// batch move appends in exactly the order the operator sees on screen.</summary>
    private List<(Activity Activity, MediaFile File)> MultiSelectedFilesInOrder()
    {
        var result = new List<(Activity Activity, MediaFile File)>();
        foreach (TreeNode activityNode in _tree.Nodes)
        {
            if (activityNode.Tag is not Activity owner) continue;
            foreach (TreeNode fileNode in activityNode.Nodes)
                if (_multiSelectedFileNodes.Contains(fileNode) && fileNode.Tag is MediaFile file)
                    result.Add((owner, file));
        }
        return result;
    }

    /// <summary>AP-07's batch twin of <see cref="MoveFileToActivity"/>: every selected file —
    /// the same objects, properties intact — moves to the END of <paramref name="target"/>,
    /// in flat selection order (a file already in the target is re-appended too, so the batch
    /// lands at the tail in its own relative order). One save for the whole batch, one log
    /// line per affected activity, selection follows the first moved file.</summary>
    private void MoveFilesToActivity(Activity target, List<(Activity Activity, MediaFile File)> batch)
    {
        var scenario = CurrentScenario;
        if (scenario == null || batch.Count == 0 || !scenario.Activities.Contains(target)) return;
        var affected = new HashSet<Activity>();
        foreach (var (from, file) in batch)
        {
            if (!from.Files.Remove(file)) continue;
            target.Files.Add(file);
            affected.Add(from);
        }
        if (affected.Count == 0) return;
        affected.Add(target);
        // Same as the single move: a collapsed target unfolds in the same save, so the moved
        // files aren't hidden the moment they arrive.
        if (target.IsCollapsed) target.IsCollapsed = false;
        if (!TrySave()) return;
        foreach (var changed in affected)
            _fileOpLog.LogActivityModified(scenario.Id, changed.Id, changed.Name);
        RefreshTree();
        SelectFileNode(target, batch[0].File);
    }

    /// <summary>AP-07's batch twin of <see cref="OnRemoveFile"/>: every multi-selected file is
    /// removed in one pass and one save — no confirmation, per AP-02's rule; the single toast
    /// below carries the one undo that puts them all back.</summary>
    private void OnRemoveFilesBatch()
    {
        var scenario = CurrentScenario;
        if (scenario == null) return;
        var removed = MultiSelectedFilesInOrder()
            .Select(entry => (entry.Activity, entry.File, Index: entry.Activity.Files.IndexOf(entry.File)))
            .Where(entry => entry.Index >= 0)
            .ToList();
        if (removed.Count < 2) return;
        foreach (var (activity, file, _) in removed) activity.Files.Remove(file);
        if (!TrySave()) return;
        foreach (var activity in removed.Select(entry => entry.Activity).Distinct())
            _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        OfferUndoFilesRemove(scenario, removed);
    }

    /// <summary>The batch remove's undo (AP-07), mirroring <see cref="OfferUndoFileRemove"/>'s
    /// contract: ONE toast, ONE 撤销， and every file goes back to its own original
    /// (activity, index) — reinserted in ascending index order per activity so each insert
    /// lands relative to the ones already restored, clamped to the list's current end. Each
    /// entry is guarded individually (scenario deleted meanwhile / activity removed / file
    /// re-added meanwhile) and skipped quietly, exactly like the single-file undo; the
    /// restore itself is deliberately not logged as a creation, same as there.</summary>
    private void OfferUndoFilesRemove(Scenario scenario, List<(Activity Activity, MediaFile File, int Index)> removed)
    {
        if (ShowToast == null) return;
        ShowToast($"已移除 {removed.Count} 个文件", ToastSeverity.Info, new ToastAction[]
        {
            new("撤销", () =>
            {
                if (!_store.Scenarios.Contains(scenario)) return;
                bool restored = false;
                foreach (var (activity, file, index) in removed.OrderBy(entry => entry.Index))
                {
                    if (!scenario.Activities.Contains(activity) || activity.Files.Contains(file)) continue;
                    activity.Files.Insert(Math.Min(index, activity.Files.Count), file);
                    restored = true;
                }
                if (!restored) return;
                if (!TrySave()) return;
                RefreshTree();
            }),
        });
    }

    string? IActivityRowHost.MusicCueText(Activity activity) =>
        activity.MusicFadeOut ? "♪ 音乐淡出"
        : activity.MusicPlaylistId is { } id ? "♪ " + (Playlists?.Invoke().FirstOrDefault(p => p.Id == id)?.Name ?? "歌单已删除")
        : null;

    private (Scenario? scenario, Activity? activity, MediaFile? file) GetSelection()
    {
        var scenario = CurrentScenario;
        var node = _tree.SelectedNode;
        if (scenario == null || node == null) return (scenario, null, null);

        if (node.Tag is Activity activity) return (scenario, activity, null);
        if (node.Tag is MediaFile file && node.Parent?.Tag is Activity owningActivity) return (scenario, owningActivity, file);
        return (scenario, null, null);
    }

    private void UpdateButtonStates()
    {
        var (scenario, activity, file) = GetSelection();
        bool filtering = IsFiltering;
        _playSelectedItem.Enabled = _playback != null && activity != null;
        _renameActivityItem.Enabled = activity != null;
        _deleteActivityItem.Enabled = activity != null;
        _duplicateActivityItem.Enabled = activity != null;
        // Activity reorder (AP-03a): position-sensitive, unlike the presence-only checks around
        // them — first activity cannot go up, last cannot go down. Frozen while a filter is up
        // (AP-06): a hidden neighbour would make a move look like it did nothing, or worse.
        int activityIndex = scenario != null && activity != null ? scenario.Activities.IndexOf(activity) : -1;
        _moveActivityUpItem.Enabled = activityIndex > 0 && !filtering;
        _moveActivityDownItem.Enabled = activityIndex >= 0 && scenario != null && activityIndex < scenario.Activities.Count - 1 && !filtering;
        _addFileButton.Enabled = activity != null;
        _musicCueItem.Enabled = activity != null;
        _transitionItem.Enabled = file != null;
        _documentSettingsButton.Enabled = file?.Kind == MediaKind.Document;
        _removeButton.Enabled = file != null;
        // File reorder: disabled while filtering too (AP-06, V1 gate #25's "筛选/搜索时禁拖排"
        // wording) — the rows' ⋮⋮ drag is refused at its own host entry point for the same
        // reason (see IActivityRowHost.MoveFile in ActivitiesPanel.Editing.cs).
        _moveUpButton.Enabled = file != null && !filtering;
        _moveDownButton.Enabled = file != null && !filtering;
        // The batch items (AP-07) read the Ctrl/Shift multi-selection, NOT GetSelection():
        // with 2+ files multi-selected they are live regardless of which single node is
        // selected, and below 2 they are dead regardless of it.
        int multiCount = _multiSelectedFileNodes.Count;
        _batchPropertiesButton.Enabled = multiCount >= 2;
        _batchRemoveButton.Enabled = multiCount >= 2;
        _batchRemoveButton.Text = $"批量移除（{multiCount} 项）";
        // 「移动到环节」 serves whichever selection is live: with a 2+ multi-selection it
        // moves the whole set (EVERY activity is a target — the set can span several, and
        // files already in the target are re-appended in batch order), while the single-file
        // path (AP-03b) needs a selected file AND at least one other activity to go to.
        _moveFileToActivityItem.Enabled = multiCount >= 2
            ? scenario != null && scenario.Activities.Count >= 1
            : file != null && scenario != null && scenario.Activities.Count > 1;
        UpdateRunControls();
    }

    /// <summary>Narrower than a bare <c>Kind is MediaKind.Image or MediaKind.Document</c> check —
    /// once <c>FileLibraryStore.InferKind</c> started mapping PPT/Word/Excel extensions onto the same
    /// <see cref="MediaKind.Document"/> value a PDF gets (PLANNING.md §14.1's WPS integration),
    /// <c>StayDuration</c> stopped being applicable to every Document: <c>PlaybackEngine.PlayOfficeDocument</c>
    /// deliberately never arms the stay-duration timer this setting controls (see that method's own
    /// doc comment — an auto-advance timer silently switching away mid-edit would be actively harmful),
    /// so setting one on an Office document would be accepted here and then silently do nothing at
    /// playback time. Shared by the 详情面板's editor/<see cref="UpdateButtonStates"/> and
    /// <see cref="BatchPropertiesDialog"/>'s own applicability check, so this one definition is the
    /// only place that needs to know about the Office-document exception.</summary>
    private static bool IsStayDurationApplicable(MediaFile file) =>
        file.Kind == MediaKind.Image || (file.Kind == MediaKind.Document && !EveryStage.Terminal.Documents.DocumentFormats.IsOffice(file.SourcePath));
}
