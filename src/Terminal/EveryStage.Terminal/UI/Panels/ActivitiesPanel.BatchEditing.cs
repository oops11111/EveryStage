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
    /// OnEdit* method above already uses, just looped over more than one file per property. A property
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
        // _multiSelectedFileNodes (see that method's own comment) — both reasons OnEditAudioProperties
        // already refreshes after its own IsBackgroundAudio change apply here too.
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
        var (_, activity, file) = GetSelection();
        _playSelectedItem.Enabled = _playback != null && activity != null;
        _renameActivityItem.Enabled = activity != null;
        _deleteActivityItem.Enabled = activity != null;
        _addFileButton.Enabled = activity != null;
        _playModeButton.Enabled = activity != null;
        _musicCueItem.Enabled = activity != null;
        _transitionItem.Enabled = file != null;
        _audioPropertiesButton.Enabled = file != null && file.Kind == MediaKind.Audio;
        _fadeButton.Enabled = file != null && file.Kind is MediaKind.Video or MediaKind.Audio;
        _stayDurationButton.Enabled = file != null && IsStayDurationApplicable(file);
        _completionActionButton.Enabled = file != null;
        _documentSettingsButton.Enabled = file?.Kind == MediaKind.Document;
        _batchPropertiesButton.Enabled = _multiSelectedFileNodes.Count >= 2;
        _removeButton.Enabled = file != null;
        _moveUpButton.Enabled = file != null;
        _moveDownButton.Enabled = file != null;
        UpdateRunControls();
    }

    /// <summary>Narrower than a bare <c>Kind is MediaKind.Image or MediaKind.Document</c> check —
    /// once <c>FileLibraryStore.InferKind</c> started mapping PPT/Word/Excel extensions onto the same
    /// <see cref="MediaKind.Document"/> value a PDF gets (PLANNING.md §14.1's WPS integration),
    /// <c>StayDuration</c> stopped being applicable to every Document: <c>PlaybackEngine.PlayOfficeDocument</c>
    /// deliberately never arms the stay-duration timer this setting controls (see that method's own
    /// doc comment — an auto-advance timer silently switching away mid-edit would be actively harmful),
    /// so setting one on an Office document would be accepted here and then silently do nothing at
    /// playback time. Shared by <see cref="OnEditStayDuration"/>/<see cref="UpdateButtonStates"/> and
    /// <see cref="BatchPropertiesDialog"/>'s own applicability check, so this one definition is the
    /// only place that needs to know about the Office-document exception.</summary>
    private static bool IsStayDurationApplicable(MediaFile file) =>
        file.Kind == MediaKind.Image || (file.Kind == MediaKind.Document && !EveryStage.Terminal.Documents.DocumentFormats.IsOffice(file.SourcePath));
}
