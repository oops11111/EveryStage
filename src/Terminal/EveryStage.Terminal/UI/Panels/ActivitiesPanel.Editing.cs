using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    private void OnAddFile()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        using var picker = new LibraryFilePickerDialog(_library.Files);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedFiles.Count == 0) return;

        // One save for the whole batch (AP-01): TrySave writes the entire store either way, so
        // saving per file would only repeat the same write N times — and if the one save fails,
        // TrySave's reload rolls every add back together, the all-or-nothing outcome a single
        // dialog confirmation should have. Each file is still cloned individually, exactly as
        // the single-pick path did (fresh Id, no shared instance with the library or a sibling).
        MediaFile? lastAdded = null;
        foreach (var libraryFile in picker.SelectedFiles)
        {
            lastAdded = libraryFile.Clone();
            activity.Files.Add(lastAdded);
        }
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        if (lastAdded != null) SelectFileNode(activity, lastAdded);
    }

    /// <summary>Applies a new <see cref="MediaFile.PlayModeOverride"/>: no-change check, set,
    /// <see cref="TrySave"/>, per-property log — the apply path of the 详情面板's in-place
    /// editor (ActivitiesPanel.Details.cs). The per-file 播放方式... menu dialog that also
    /// ended here was retired in AP-10 （属性双轨收敛）; the panel and (with its own loop)
    /// 统一设置属性 remain, and sharing this tail keeps them from drifting apart on how this
    /// property is saved or logged.</summary>
    private void ApplyPlayModeOverride(MediaFile file, PlayMode? newPlayMode)
    {
        if (newPlayMode == file.PlayModeOverride) return; // no actual change.

        // LogPlaybackPropertyChanged, not LogActivityModified — this is the first real caller
        // this method has ever had (see this project's README "已知风险"): a per-file property
        // value changing is exactly what it exists to record, more precisely than the generic
        // "activity modified" log the file-list-membership operations above use.
        string? oldValue = file.PlayModeOverride?.ToString();
        string? newValue = newPlayMode?.ToString();
        file.PlayModeOverride = newPlayMode;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.PlayModeOverride), oldValue, newValue);
    }

    /// <summary>Applies a new <see cref="Activity.DefaultPlayMode"/> — same reasoning as
    /// <see cref="ApplyPlayModeOverride"/>; called by the 详情面板's 环节播放方式 editor.
    /// No-ops on no actual change.</summary>
    private void ApplyActivityPlayMode(Scenario scenario, Activity activity, PlayMode newPlayMode)
    {
        if (newPlayMode == activity.DefaultPlayMode) return; // no actual change — nothing to log/save.

        activity.DefaultPlayMode = newPlayMode;
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
    }

    /// <summary>Applies a new <see cref="MediaFile.IsBackgroundAudio"/>/
    /// <see cref="MediaFile.BackgroundAudioVisual"/> pair — the 详情面板's apply path
    /// (ActivitiesPanel.Details.cs; the 音频属性... menu dialog was retired in AP-10); see
    /// <see cref="ApplyPlayModeOverride"/> for the reasoning. Refreshes the tree because
    /// <see cref="MediaFile.IsBackgroundAudio"/> changes the file node's own text
    /// (<see cref="BuildFileNodeText"/>).</summary>
    private void ApplyAudioProperties(MediaFile file, bool isBackgroundAudio, AudioVisual backgroundAudioVisual)
    {
        if (isBackgroundAudio == file.IsBackgroundAudio && backgroundAudioVisual == file.BackgroundAudioVisual)
            return; // no actual change — nothing to log/save.

        // Two separate LogPlaybackPropertyChanged calls rather than one combined entry — same
        // per-property granularity the other Apply* methods use, so the log can show exactly which
        // of the two properties actually changed rather than always recording both regardless.
        if (isBackgroundAudio != file.IsBackgroundAudio)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.IsBackgroundAudio),
                file.IsBackgroundAudio.ToString(), isBackgroundAudio.ToString());
            file.IsBackgroundAudio = isBackgroundAudio;
        }
        if (backgroundAudioVisual != file.BackgroundAudioVisual)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.BackgroundAudioVisual),
                file.BackgroundAudioVisual.ToString(), backgroundAudioVisual.ToString());
            file.BackgroundAudioVisual = backgroundAudioVisual;
        }
        if (!TrySave()) return;
        // Unlike the other Apply* methods (none of which change anything the tree actually
        // displays), IsBackgroundAudio now affects the file
        // node's own text (see BuildFileNodeText) — without this, toggling the checkbox wouldn't be
        // visible here until some unrelated action happened to trigger a full RefreshTree.
        RefreshTree();
    }

    /// <summary>Applies a new <see cref="MediaFile.VolumeFollowsFade"/>/
    /// <see cref="MediaFile.FadeDuration"/> pair — the 详情面板's apply path
    /// (ActivitiesPanel.Details.cs; the 淡入淡出... menu dialog was retired in AP-10); see
    /// <see cref="ApplyPlayModeOverride"/> for
    /// the reasoning. No tree refresh: neither field appears in the node's text.</summary>
    private void ApplyFade(MediaFile file, bool volumeFollowsFade, TimeSpan? fadeDuration)
    {
        if (volumeFollowsFade == file.VolumeFollowsFade && fadeDuration == file.FadeDuration)
            return; // no actual change — nothing to log/save.

        // Two separate LogPlaybackPropertyChanged calls, same per-property granularity
        // ApplyAudioProperties already uses for its own two-field apply.
        if (volumeFollowsFade != file.VolumeFollowsFade)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.VolumeFollowsFade),
                file.VolumeFollowsFade.ToString(), volumeFollowsFade.ToString());
            file.VolumeFollowsFade = volumeFollowsFade;
        }
        if (fadeDuration != file.FadeDuration)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.FadeDuration),
                file.FadeDuration?.ToString(), fadeDuration?.ToString());
            file.FadeDuration = fadeDuration;
        }
        if (!TrySave()) return;
    }

    /// <summary>"文档播放设置": PDF reading mode / page range / zoom, Word editor, presentation mode.
    /// Kept as a menu entry under AP-10: the 详情面板 reaches this same dialog through a button —
    /// it does not edit document settings in place — so this dialog IS the editor, not a second track.</summary>
    private void OnEditDocumentSettings()
    {
        var (_, activity, file) = GetSelection();
        if (activity == null || file?.Kind != MediaKind.Document) return;
        using var dialog = new DocumentSettingsDialog(file);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var old = file.Document;
        file.Document = dialog.Result;
        try { _repository.Save(_store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file.Document = old;
            MessageBox.Show(this, $"文档播放设置未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.Document), null, DocumentSettingsDialog.Summarize(file));
        RefreshTree();
    }

    /// <summary>Applies a new <see cref="MediaFile.StayDuration"/> — the 详情面板's apply path
    /// (ActivitiesPanel.Details.cs; the 停留时长... menu dialog was retired in AP-10); see
    /// <see cref="ApplyPlayModeOverride"/> for the reasoning.</summary>
    private void ApplyStayDuration(MediaFile file, TimeSpan? newStayDuration)
    {
        if (newStayDuration == file.StayDuration) return; // no actual change — nothing to log/save.

        // LogPlaybackPropertyChanged, same per-property granularity the other Apply* methods
        // use — same established convention as ApplyPlayModeOverride's own
        // oldValue/newValue (see this method's README entry): a null TimeSpan? passes straight
        // through as a real null, not the string "null", since LogPlaybackPropertyChanged's two
        // value parameters are themselves string?.
        string? oldValue = file.StayDuration?.ToString();
        string? newValue = newStayDuration?.ToString();
        file.StayDuration = newStayDuration;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.StayDuration), oldValue, newValue);
    }

    /// <summary>Applies a new <see cref="MediaFile.OnCompletion"/> — the 详情面板's apply path
    /// (ActivitiesPanel.Details.cs; the 完成后动作... menu dialog was retired in AP-10); see
    /// <see cref="ApplyPlayModeOverride"/> for the reasoning.</summary>
    private void ApplyCompletionAction(MediaFile file, CompletionAction newCompletionAction)
    {
        if (newCompletionAction == file.OnCompletion) return; // no actual change — nothing to log/save.

        string oldValue = file.OnCompletion.ToString();
        string newValue = newCompletionAction.ToString();
        file.OnCompletion = newCompletionAction;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue, newValue);
    }

    /// <summary>Applies a new <see cref="MediaFile.AllowManualSkip"/>. Unlike the other Apply*
    /// methods this one has no single-file dialog to share with — "允许手动切换" only ever had a
    /// row in <see cref="BatchPropertiesDialog"/> (see that dialog's own comment) — so this is
    /// the 详情面板's apply path, written to the same convention as <see cref="ApplyCompletionAction"/>
    /// (no-change check, set, <see cref="TrySave"/>, per-property
    /// <see cref="FileOperationLogger.LogPlaybackPropertyChanged"/> with
    /// <c>nameof(MediaFile.AllowManualSkip)</c>, matching <see cref="OnEditBatchProperties"/>'s
    /// own log entry for this property).</summary>
    private void ApplyAllowManualSkip(MediaFile file, bool allowManualSkip)
    {
        if (allowManualSkip == file.AllowManualSkip) return; // no actual change — nothing to log/save.

        string oldValue = file.AllowManualSkip.ToString();
        string newValue = allowManualSkip.ToString();
        file.AllowManualSkip = allowManualSkip;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.AllowManualSkip), oldValue, newValue);
    }

    private void OnRemoveFile()
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;

        // Remember the slot so the undo offer below can put the same object back exactly where
        // it was (AP-02).
        int index = activity.Files.IndexOf(file);
        activity.Files.Remove(file);
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        OfferUndoFileRemove(scenario, activity, file, index);
    }

    /// <summary>删后撤销 for a file removal (AP-02) — the file-level counterpart of
    /// <see cref="OfferUndoActivityDelete"/>, with one deliberate difference: removing a file
    /// never had the activity delete's confirmation box, and none is added — the 10 s 撤销 IS
    /// the misclick recovery, which only works because the removed object itself is what goes
    /// back (same instance, so every property and the playback state's by-reference identity
    /// come back with it). As with the activity/scenario offers, the restore is deliberately
    /// not logged as a creation, and a failed save means there is nothing to offer.</summary>
    private void OfferUndoFileRemove(Scenario scenario, Activity activity, MediaFile file, int index)
    {
        if (ShowToast == null) return;
        ShowToast($"文件“{Path.GetFileName(file.SourcePath)}”已移除", ToastSeverity.Info, new ToastAction[]
        {
            new("撤销", () =>
            {
                // Same guards as OfferUndoActivityDelete, one level down: the scenario and the
                // activity must both still be there, and the file must not already be back
                // (a second click, or the operator re-added it meanwhile).
                if (!_store.Scenarios.Contains(scenario) || !scenario.Activities.Contains(activity)
                    || activity.Files.Contains(file)) return;
                activity.Files.Insert(Math.Min(index, activity.Files.Count), file);
                if (!TrySave()) return;
                RefreshTree();
                SelectFileNode(activity, file);
            }),
        });
    }

    /// <summary>Moves the selected file to the end of another activity of the same scenario
    /// (AP-03b, 「移动到环节 ▸」). The object itself moves — no clone — so every per-file
    /// property (overrides, fades, stay duration…) travels with it, unlike the remove + re-add
    /// workaround this replaces, which silently reset them all.</summary>
    private void MoveFileToActivity(Activity target)
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;
        if (ReferenceEquals(target, activity) || !scenario.Activities.Contains(target)) return;

        activity.Files.Remove(file);
        target.Files.Add(file);
        // Land visibly: a collapsed target would swallow the file out of sight, so unfold it —
        // the same reasoning keyboard selection auto-expands a collapsed activity it lands in.
        // Folded into this edit's own save rather than a separate one.
        if (target.IsCollapsed) target.IsCollapsed = false;
        if (!TrySave()) return;
        // Both activities changed membership; log each, matching the one-LogActivityModified-
        // per-affected-activity granularity OnAddFile/OnRemoveFile already keep.
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _fileOpLog.LogActivityModified(scenario.Id, target.Id, target.Name);
        RefreshTree();
        SelectFileNode(target, file);
    }

    void IActivityRowHost.MoveFile(Activity activity, int from, int insertAt)
    {
        // The rows' ⋮⋮ drag lands here; while a filter is up it is refused (AP-06, same rule
        // as the 上移/下移 items in UpdateButtonStates) — the row doesn't know about the
        // filter, so the guard lives at this single entry point.
        if (IsFiltering) return;
        var scenario = CurrentScenario;
        if (scenario == null || from < 0 || from >= activity.Files.Count) return;
        var file = activity.Files[from];
        var before = activity.Files.ToList();
        activity.Files.RemoveAt(from);
        activity.Files.Insert(Math.Clamp(insertAt > from ? insertAt - 1 : insertAt, 0, activity.Files.Count), file);
        if (!TrySave())
        {
            activity.Files.Clear();
            activity.Files.AddRange(before);
            RefreshTree();
            return;
        }
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        SelectFileNode(activity, file);
    }

    private void MoveSelectedFile(int delta)
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;

        int index = activity.Files.IndexOf(file);
        int newIndex = index + delta;
        if (newIndex < 0 || newIndex >= activity.Files.Count) return;

        (activity.Files[index], activity.Files[newIndex]) = (activity.Files[newIndex], activity.Files[index]);
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        SelectFileNode(activity, file);
    }
}
