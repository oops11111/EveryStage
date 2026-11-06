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
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;

        activity.Files.Add(picker.Selected.Clone());
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

    private void OnEditPlayMode()
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null) return;

        // A file node selected within the activity edits that FILE's PlayModeOverride (with an
        // "inherit" option); selecting just the activity node itself (file == null) edits the
        // activity's own DefaultPlayMode instead — same GetSelection() distinction UpdateButtonStates
        // already uses elsewhere in this class.
        if (file != null)
        {
            using var fileDialog = new PlayModeDialog(
                $"文件播放方式 — {Path.GetFileName(file.SourcePath)}", file.PlayModeOverride, allowInherit: true);
            if (fileDialog.ShowDialog(this) != DialogResult.OK) return;
            if (fileDialog.SelectedPlayMode == file.PlayModeOverride) return; // no actual change.

            // LogPlaybackPropertyChanged, not LogActivityModified — this is the first real caller
            // this method has ever had (see this project's README "已知风险"): a per-file property
            // value changing is exactly what it exists to record, more precisely than the generic
            // "activity modified" log the file-list-membership operations above use.
            string? oldValue = file.PlayModeOverride?.ToString();
            string? newValue = fileDialog.SelectedPlayMode?.ToString();
            file.PlayModeOverride = fileDialog.SelectedPlayMode;
            if (!TrySave()) return;
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.PlayModeOverride), oldValue, newValue);
            return;
        }

        using var dialog = new PlayModeDialog($"活动播放方式 — {activity.Name}", activity.DefaultPlayMode);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.SelectedPlayMode == activity.DefaultPlayMode) return; // no actual change — nothing to log/save.

        activity.DefaultPlayMode = dialog.SelectedPlayMode!.Value; // never null — allowInherit defaults false above.
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
    }

    /// <summary>Only reachable when a selected file's <c>Kind == MediaKind.Audio</c> — see
    /// <see cref="UpdateButtonStates"/>. Unlike <see cref="OnEditPlayMode"/> this has no
    /// activity-level counterpart: <c>IsBackgroundAudio</c>/<c>BackgroundAudioVisual</c> are only
    /// meaningful per-file (see <c>MediaFile</c>'s own doc comments), there's no equivalent
    /// activity-wide default to fall back to editing when nothing is selected.</summary>
    private void OnEditAudioProperties()
    {
        var (_, activity, file) = GetSelection();
        if (activity == null || file == null || file.Kind != MediaKind.Audio) return;

        using var dialog = new AudioPropertiesDialog(
            $"音频属性 — {Path.GetFileName(file.SourcePath)}", file.IsBackgroundAudio, file.BackgroundAudioVisual);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.IsBackgroundAudio == file.IsBackgroundAudio && dialog.BackgroundAudioVisual == file.BackgroundAudioVisual)
            return; // no actual change — nothing to log/save.

        // Two separate LogPlaybackPropertyChanged calls rather than one combined entry — same
        // per-property granularity OnEditPlayMode already uses, so the log can show exactly which
        // of the two properties actually changed rather than always recording both regardless.
        if (dialog.IsBackgroundAudio != file.IsBackgroundAudio)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.IsBackgroundAudio),
                file.IsBackgroundAudio.ToString(), dialog.IsBackgroundAudio.ToString());
            file.IsBackgroundAudio = dialog.IsBackgroundAudio;
        }
        if (dialog.BackgroundAudioVisual != file.BackgroundAudioVisual)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.BackgroundAudioVisual),
                file.BackgroundAudioVisual.ToString(), dialog.BackgroundAudioVisual.ToString());
            file.BackgroundAudioVisual = dialog.BackgroundAudioVisual;
        }
        if (!TrySave()) return;
        // Unlike OnEditPlayMode/OnEditStayDuration/OnEditCompletionAction just above/below (none of
        // which change anything the tree actually displays), IsBackgroundAudio now affects the file
        // node's own text (see BuildFileNodeText) — without this, toggling the checkbox wouldn't be
        // visible here until some unrelated action happened to trigger a full RefreshTree.
        RefreshTree();
    }

    /// <summary>Only reachable when a selected file's <c>Kind</c> is Video or Audio — see
    /// <see cref="UpdateButtonStates"/>. Same no-activity-level-counterpart reasoning as
    /// <see cref="OnEditAudioProperties"/>: <c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c>
    /// are only ever per-file, there is no per-activity default to fall back to editing when nothing
    /// is selected.</summary>
    private void OnEditFade()
    {
        var (_, activity, file) = GetSelection();
        if (activity == null || file == null || file.Kind is not (MediaKind.Video or MediaKind.Audio)) return;

        using var dialog = new FadeDialog(
            $"淡入淡出 — {Path.GetFileName(file.SourcePath)}", file.VolumeFollowsFade, file.FadeDuration);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.VolumeFollowsFade == file.VolumeFollowsFade && dialog.FadeDuration == file.FadeDuration)
            return; // no actual change — nothing to log/save.

        // Two separate LogPlaybackPropertyChanged calls, same per-property granularity
        // OnEditAudioProperties already uses for its own two-field dialog.
        if (dialog.VolumeFollowsFade != file.VolumeFollowsFade)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.VolumeFollowsFade),
                file.VolumeFollowsFade.ToString(), dialog.VolumeFollowsFade.ToString());
            file.VolumeFollowsFade = dialog.VolumeFollowsFade;
        }
        if (dialog.FadeDuration != file.FadeDuration)
        {
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.FadeDuration),
                file.FadeDuration?.ToString(), dialog.FadeDuration?.ToString());
            file.FadeDuration = dialog.FadeDuration;
        }
        if (!TrySave()) return;
    }

    /// <summary>Only reachable when a selected file's <c>Kind</c> is Image, or Document that isn't an
    /// Office document (see <see cref="IsStayDurationApplicable"/>) — see
    /// <see cref="UpdateButtonStates"/>. Same no-activity-level-counterpart reasoning as
    /// <see cref="OnEditAudioProperties"/>: <c>MediaFile.StayDuration</c> is only ever a per-file
    /// override of the 设置 面板's single global default, there is no per-activity default to fall
    /// back to editing when nothing is selected.</summary>
    /// <summary>"文档播放设置": PDF reading mode / page range / zoom, Word editor, presentation mode.</summary>
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

    private void OnEditStayDuration()
    {
        var (_, activity, file) = GetSelection();
        if (activity == null || file == null || !IsStayDurationApplicable(file)) return;

        using var dialog = new StayDurationDialog($"停留时长 — {Path.GetFileName(file.SourcePath)}", file.StayDuration);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.SelectedStayDuration == file.StayDuration) return; // no actual change — nothing to log/save.

        // LogPlaybackPropertyChanged, same per-property granularity OnEditPlayMode/
        // OnEditAudioProperties already use — same established convention as OnEditPlayMode's own
        // oldValue/newValue (see this method's README entry): a null TimeSpan? passes straight
        // through as a real null, not the string "null", since LogPlaybackPropertyChanged's two
        // value parameters are themselves string?.
        string? oldValue = file.StayDuration?.ToString();
        string? newValue = dialog.SelectedStayDuration?.ToString();
        file.StayDuration = dialog.SelectedStayDuration;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.StayDuration), oldValue, newValue);
    }

    /// <summary>Only reachable when a file is selected — see <see cref="UpdateButtonStates"/>.
    /// Unlike <see cref="OnEditStayDuration"/>/<see cref="OnEditAudioProperties"/>, not restricted to
    /// a particular <see cref="MediaKind"/>: <c>MediaFile.OnCompletion</c> applies to every kind
    /// alike (see this button's own construction comment).</summary>
    private void OnEditCompletionAction()
    {
        var (_, activity, file) = GetSelection();
        if (activity == null || file == null) return;

        using var dialog = new CompletionActionDialog($"完成后动作 — {Path.GetFileName(file.SourcePath)}", file.OnCompletion);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.SelectedCompletionAction == file.OnCompletion) return; // no actual change — nothing to log/save.

        string oldValue = file.OnCompletion.ToString();
        string newValue = dialog.SelectedCompletionAction.ToString();
        file.OnCompletion = dialog.SelectedCompletionAction;
        if (!TrySave()) return;
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue, newValue);
    }

    private void OnRemoveFile()
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;

        activity.Files.Remove(file);
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

    void IActivityRowHost.MoveFile(Activity activity, int from, int insertAt)
    {
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
