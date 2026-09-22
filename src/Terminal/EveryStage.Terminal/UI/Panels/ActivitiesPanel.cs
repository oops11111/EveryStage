using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 活动面板: "方案选择器（切换/另存为/新建）+ 活动列表（可折叠、拖拽排序、滚动）+
/// 输出状态条". Uses a <see cref="TreeView"/> for the activity/file hierarchy — collapse/expand and
/// scrolling come for free from the control; drag-to-reorder is NOT implemented (see this project's
/// README), only explicit 上移/下移 (move up/down) buttons, which get the same end result with far
/// less WinForms drag-and-drop-over-a-TreeView complexity to get wrong on a first, uncompiled pass.
///
/// "添加文件到活动" doesn't work by dragging from the 文件 panel (§11's "拖拽添加") because
/// <see cref="MainWindow"/> only shows one panel at a time — there's no moment where both panels are
/// visible to drag between. Instead this has its own "添加文件..." button that opens a picker over
/// the same <see cref="FileLibraryStore"/> the 文件 panel reads from.
///
/// Tracks playback as it advances (PLANNING.md §16第5项 "联动"): <see cref="OnFileStarted"/>
/// selects whichever tree node corresponds to the file <see cref="PlaybackEngine"/> just started
/// playing, so switching "下一项"/"上一项" from the floating preview window (or anything else that
/// advances playback) visibly moves the selection here too, instead of this panel silently staying
/// wherever it was last clicked — see <see cref="TryHighlightPlayingFile"/> for what this does and
/// doesn't cover.
/// </summary>
public sealed class ActivitiesPanel : UserControl
{
    private readonly ScenarioStore _store;
    private readonly ScenarioRepository _repository;
    private readonly FileLibraryStore _library;
    // Not readonly — see AttachPlaybackEngine's own doc comment: PLANNING.md §5's runtime
    // monitor-hot-plug binding needs to replace this from null to a real PlaybackEngine well after
    // this panel is already constructed.
    private PlaybackEngine? _playback;
    private readonly FileOperationLogger _fileOpLog;
    private readonly OutputStateMachine _stateMachine;

    private readonly ComboBox _scenarioCombo;
    private readonly TreeView _tree;
    private readonly Button _addFileButton;
    private readonly Button _playModeButton;
    private readonly Button _audioPropertiesButton;
    private readonly Button _fadeButton;
    private readonly Button _stayDurationButton;
    private readonly Button _completionActionButton;
    private readonly Button _batchPropertiesButton;
    private readonly Button _removeButton;
    private readonly Button _moveUpButton;
    private readonly Button _moveDownButton;
    private readonly Label _statusBar;

    /// <summary>File nodes Ctrl+Clicked into a multi-selection for "统一设置属性..." — see
    /// <see cref="OnTreeNodeMouseClick"/>. Deliberately separate from <see cref="TreeView.SelectedNode"/>
    /// (which every single-select-driven button above still reads via <see cref="GetSelection"/>,
    /// completely unaffected by this set): WinForms' <see cref="TreeView"/> has no built-in
    /// multi-select, and full owner-draw was rejected as more new/unverified surface than this
    /// project needs — same "reuse an established, simple control pattern over an unverified one"
    /// reasoning <see cref="FloatingPreviewWindow"/>'s own doc comment on its step buttons (instead of
    /// a <see cref="TrackBar"/>) already used. Highlighted manually via each node's own
    /// <see cref="TreeNode.BackColor"/>/<see cref="TreeNode.ForeColor"/> rather than owner-draw.
    /// Only ever holds file nodes (<c>Tag is MediaFile</c>) — activity nodes can't be batch-edited.
    /// Cleared by <see cref="RefreshTree"/> (old <see cref="TreeNode"/> references go stale once the
    /// tree is rebuilt) and by any plain, non-Ctrl click anywhere in the tree — Shift-range-select is
    /// deliberately not supported, same scope-minimization reasoning.</summary>
    private readonly HashSet<TreeNode> _multiSelectedFileNodes = new();

    public ActivitiesPanel(
        ScenarioStore store, ScenarioRepository repository, FileLibraryStore library,
        PlaybackEngine? playback, FileOperationLogger fileOpLog, OutputStateMachine stateMachine)
    {
        _store = store;
        _repository = repository;
        _library = library;
        _playback = playback;
        _fileOpLog = fileOpLog;
        _stateMachine = stateMachine;

        Dock = DockStyle.Fill;
        EnsureAtLeastOneScenario();

        // --- 方案选择器 ---
        var scenarioBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        ModernUi.MakeResponsiveToolbar(scenarioBar);
        _scenarioCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        ModernUi.StyleComboBox(_scenarioCombo);
        _scenarioCombo.SelectedIndexChanged += OnScenarioComboChanged;
        var newScenarioButton = new Button { Text = "新建方案", AutoSize = true };
        newScenarioButton.Click += (_, _) => OnNewScenario();
        var saveAsButton = new Button { Text = "另存为...", AutoSize = true };
        saveAsButton.Click += (_, _) => OnSaveAsScenario();
        var deleteScenarioButton = new Button { Text = "删除方案", AutoSize = true };
        deleteScenarioButton.Click += (_, _) => OnDeleteScenario();
        scenarioBar.Controls.AddRange(new Control[] { _scenarioCombo, newScenarioButton, saveAsButton, deleteScenarioButton });

        // --- 活动列表工具栏 ---
        var activityBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        ModernUi.MakeResponsiveToolbar(activityBar);
        var newActivityButton = new Button { Text = "新建活动", AutoSize = true };
        newActivityButton.Click += (_, _) => OnNewActivity();
        var renameActivityButton = new Button { Text = "重命名活动", AutoSize = true };
        renameActivityButton.Click += (_, _) => OnRenameActivity();
        var deleteActivityButton = new Button { Text = "删除活动", AutoSize = true };
        deleteActivityButton.Click += (_, _) => OnDeleteActivity();
        _addFileButton = new Button { Text = "添加文件...", AutoSize = true, Enabled = false };
        _addFileButton.Click += (_, _) => OnAddFile();
        // Editing PlayMode is new (see PlaybackEngine.EffectivePlayMode's doc comment on why it
        // previously had nothing to edit — the enum had no runtime effect at all until it did).
        // Context-sensitive: edits the selected FILE's PlayModeOverride if one is selected, else the
        // selected ACTIVITY's own DefaultPlayMode — see OnEditPlayMode.
        _playModeButton = new Button { Text = "播放方式...", AutoSize = true, Enabled = false };
        _playModeButton.Click += (_, _) => OnEditPlayMode();
        // Only ever enabled for a selected file whose Kind == MediaKind.Audio — see
        // UpdateButtonStates. New the same round PlaybackEngine.PlayStandaloneAudio/ApplyAudioVisual
        // first gave MediaFile.IsBackgroundAudio/BackgroundAudioVisual any runtime effect at all
        // (see this project's README risk #61), following the same "先做行为、再做UI" order
        // PlayMode's editor above already established.
        _audioPropertiesButton = new Button { Text = "音频属性...", AutoSize = true, Enabled = false };
        _audioPropertiesButton.Click += (_, _) => OnEditAudioProperties();
        // Only ever enabled for a selected file whose Kind is Video or Audio — see
        // UpdateButtonStates. New the same round PlaybackEngine.FadeDurationFor first gave
        // MediaFile.FadeDuration/VolumeFollowsFade any runtime effect at all (see this project's
        // README risk #109), same "先做行为、再做UI" order as PlayMode/IsBackgroundAudio/
        // StayDuration before it — except unlike those, the behavior itself is still only half done
        // (fade-in only), which FadeDialog's own caveat label discloses rather than hiding the
        // control until fade-out also exists.
        _fadeButton = new Button { Text = "淡入淡出...", AutoSize = true, Enabled = false };
        _fadeButton.Click += (_, _) => OnEditFade();
        // Only ever enabled for a selected file whose Kind is Image or Document (PLANNING.md §6
        // "停留时长（图片/文档）") — same "先做行为、再做UI" gap as PlayMode/AllowManualSkip/
        // IsBackgroundAudio before it, except MediaFile.StayDuration's own reading behavior
        // (PlaybackEngine.ArmStayDurationTimer) already existed and worked; only this editing entry
        // point was ever missing. Worse than a plain gap: SettingsPanel's own "默认停留时长" help
        // text already claimed a per-file override could be "配置" from "活动面板" before this
        // button existed to do it — see this project's README "已知风险".
        _stayDurationButton = new Button { Text = "停留时长...", AutoSize = true, Enabled = false };
        _stayDurationButton.Click += (_, _) => OnEditStayDuration();
        // Enabled for any selected file regardless of Kind — unlike StayDuration/AudioProperties,
        // MediaFile.OnCompletion applies uniformly (PlaybackEngine.HandleCompletion is reached from
        // video, standalone audio, and the image/document stay-duration timer alike, see that
        // method's own callers). No activity-level counterpart exists in the data model at all (no
        // Activity.DefaultCompletionAction), so — unlike PlayMode — there's nothing to fall back to
        // editing when only an activity is selected. Same "先做行为、再做UI" gap as StayDuration
        // before it: HandleCompletion has read and correctly acted on OnCompletion since the round
        // that gave PlaybackEngine its first real behavior at all, but nothing ever let anyone set
        // it away from its NextItem default for a specific file — see this project's README.
        _completionActionButton = new Button { Text = "完成后动作...", AutoSize = true, Enabled = false };
        _completionActionButton.Click += (_, _) => OnEditCompletionAction();
        // Enabled once 2+ file nodes are Ctrl+Click multi-selected — see _multiSelectedFileNodes'
        // own doc comment. Unlike every other button in this bar, this one is NOT driven by
        // GetSelection()/_tree.SelectedNode at all.
        _batchPropertiesButton = new Button { Text = "统一设置属性...", AutoSize = true, Enabled = false };
        _batchPropertiesButton.Click += (_, _) => OnEditBatchProperties();
        _removeButton = new Button { Text = "移除文件", AutoSize = true, Enabled = false };
        _removeButton.Click += (_, _) => OnRemoveFile();
        _moveUpButton = new Button { Text = "上移", AutoSize = true, Enabled = false };
        _moveUpButton.Click += (_, _) => MoveSelectedFile(-1);
        _moveDownButton = new Button { Text = "下移", AutoSize = true, Enabled = false };
        _moveDownButton.Click += (_, _) => MoveSelectedFile(1);
        activityBar.Controls.AddRange(new Control[]
        {
            newActivityButton, renameActivityButton, deleteActivityButton, _playModeButton,
            _audioPropertiesButton, _fadeButton, _stayDurationButton, _completionActionButton,
            _batchPropertiesButton, _addFileButton, _removeButton, _moveUpButton, _moveDownButton,
        });

        foreach (var button in scenarioBar.Controls.OfType<Button>().Concat(activityBar.Controls.OfType<Button>()))
        {
            button.Height = 32;
            ModernUi.StyleButton(button);
        }

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BackColor = ModernUi.Background,
            ForeColor = ModernUi.Text,
            BorderStyle = BorderStyle.None,
            LineColor = ModernUi.Border,
            ItemHeight = 62,
            Indent = 0,
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            ShowLines = false,
            ShowRootLines = false,
            HideSelection = false,
        };
        _tree.DrawNode += DrawActivityNode;
        _tree.AfterSelect += (_, _) => UpdateButtonStates();
        _tree.NodeMouseDoubleClick += OnNodeDoubleClick;
        _tree.NodeMouseClick += OnTreeNodeMouseClick;
        // Activity.IsCollapsed (PLANNING.md §6/§11's "可折叠") until now was collected (cloned by
        // OnSaveAsScenario) but never actually driven the tree either way: RefreshTree unconditionally
        // expanded every activity node regardless of this field, and nothing ever wrote a user's
        // manual collapse/expand back into it — so it silently reverted on the very next RefreshTree
        // call, which happens after nearly every edit in this panel (add/remove/move file, edit
        // play mode, etc.). AfterCollapse/AfterExpand only ever fire for a real toggle on a node
        // that's already part of this TreeView — RefreshTree's own Collapse()/Expand() calls happen
        // on a freshly-constructed, not-yet-added TreeNode, so they don't loop back into this handler.
        _tree.AfterCollapse += (_, e) => OnActivityCollapseStateChanged(e.Node, collapsed: true);
        _tree.AfterExpand += (_, e) => OnActivityCollapseStateChanged(e.Node, collapsed: false);

        _statusBar = new Label { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };

        Controls.Add(_tree);
        Controls.Add(activityBar);
        Controls.Add(scenarioBar);
        Controls.Add(_statusBar);

        if (_playback != null) _playback.FileStarted += OnFileStarted;
        _stateMachine.StateChanged += OnStateChanged;
        UpdateStatusBar(null);

        RefreshScenarioCombo();
        RefreshTree();
    }

    /// <summary>PLANNING.md §5's runtime monitor-hot-plug binding — called (at most once) from
    /// <c>MainWindow.AttachPlaybackEngine</c>, itself called from
    /// <c>TerminalApplicationContext.HandleDisplaySettingsChanged</c> when a display shows up after
    /// this Terminal already started with none bound. Sets the now-mutable <see cref="_playback"/>
    /// field and subscribes <see cref="PlaybackEngine.FileStarted"/> — the same conditional
    /// subscription this constructor already performs, now unconditional since
    /// <paramref name="playback"/> is guaranteed non-null here. Every other <see cref="_playback"/>
    /// usage in this class (<see cref="OnNodeDoubleClick"/>, this class's own <see cref="Dispose"/>)
    /// already reads the field itself rather than a value captured once at construction, so nothing
    /// else needs updating for the field to start actually working.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        _playback = playback;
        _playback.FileStarted += OnFileStarted;
    }

    private void EnsureAtLeastOneScenario()
    {
        if (_store.Scenarios.Count > 0) return;

        var scenario = new Scenario { Name = "默认方案" };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        _repository.Save(_store);
    }

    private Scenario? CurrentScenario =>
        _store.Scenarios.FirstOrDefault(s => s.Id == _store.CurrentScenarioId) ?? _store.Scenarios.FirstOrDefault();

    private void RefreshScenarioCombo()
    {
        _scenarioCombo.SelectedIndexChanged -= OnScenarioComboChanged;
        _scenarioCombo.Items.Clear();
        foreach (var scenario in _store.Scenarios) _scenarioCombo.Items.Add(scenario);
        _scenarioCombo.DisplayMember = nameof(Scenario.Name);
        _scenarioCombo.SelectedItem = CurrentScenario;
        _scenarioCombo.SelectedIndexChanged += OnScenarioComboChanged;
    }

    private void OnScenarioComboChanged(object? sender, EventArgs e)
    {
        if (_scenarioCombo.SelectedItem is not Scenario scenario) return;
        _store.CurrentScenarioId = scenario.Id;
        _repository.Save(_store);
        RefreshTree();
    }

    private void OnNewScenario()
    {
        string? name = TextInputDialog.Prompt(this, "新建方案", "方案名称：");
        if (name == null) return;

        var scenario = new Scenario { Name = name };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        _fileOpLog.LogScenarioCreated(scenario.Id, scenario.Name);
        _repository.Save(_store);
        RefreshScenarioCombo();
        RefreshTree();
    }

    private void OnSaveAsScenario()
    {
        var current = CurrentScenario;
        if (current == null) return;

        string? name = TextInputDialog.Prompt(this, "另存为", "新方案名称：", current.Name + " 副本");
        if (name == null) return;

        // Deep-clone activities/files so editing the copy never mutates the original scenario —
        // sharing MediaFile instances between two scenarios would make "another modified" quietly
        // change both.
        var clone = new Scenario
        {
            Name = name,
            Activities = current.Activities.Select(CloneActivity).ToList(),
        };
        _store.Scenarios.Add(clone);
        _store.CurrentScenarioId = clone.Id;
        _fileOpLog.LogScenarioCreated(clone.Id, clone.Name);
        _repository.Save(_store);
        RefreshScenarioCombo();
        RefreshTree();
    }

    private static Activity CloneActivity(Activity source)
    {
        var clone = new Activity
        {
            Name = source.Name,
            IsCollapsed = source.IsCollapsed,
            DefaultPlayMode = source.DefaultPlayMode,
        };
        // MediaFile.Clone() — see that method's own doc comment; this used to be a private
        // CloneFile method living only here, pulled out onto MediaFile itself now that FilesPanel's
        // "加入活动..." needs the exact same field-by-field copy.
        clone.Files.AddRange(source.Files.Select(file => file.Clone()));
        return clone;
    }

    private void OnDeleteScenario()
    {
        var current = CurrentScenario;
        if (current == null || _store.Scenarios.Count <= 1) return; // always keep at least one.

        var confirm = MessageBox.Show(this, $"确定要删除方案 \"{current.Name}\" 吗？其中的活动也会一并删除。",
            "删除方案", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _store.Scenarios.Remove(current);
        _store.CurrentScenarioId = _store.Scenarios.First().Id;
        _fileOpLog.LogScenarioDeleted(current.Id, current.Name);
        _repository.Save(_store);
        RefreshScenarioCombo();
        RefreshTree();
    }

    private void OnNewActivity()
    {
        var scenario = CurrentScenario;
        if (scenario == null) return;

        string? name = TextInputDialog.Prompt(this, "新建活动", "活动名称：");
        if (name == null) return;

        var activity = new Activity { Name = name };
        scenario.Activities.Add(activity);
        _fileOpLog.LogActivityCreated(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
        RefreshTree();
    }

    private void OnRenameActivity()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        string? name = TextInputDialog.Prompt(this, "重命名活动", "活动名称：", activity.Name);
        if (name == null) return;

        activity.Name = name;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
        RefreshTree();
    }

    private void OnDeleteActivity()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        var confirm = MessageBox.Show(this, $"确定要删除活动 \"{activity.Name}\" 吗？",
            "删除活动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        scenario.Activities.Remove(activity);
        _fileOpLog.LogActivityDeleted(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
        RefreshTree();
    }

    private void OnAddFile()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        using var picker = new LibraryFilePickerDialog(_library.Files);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;

        activity.Files.Add(picker.Selected.Clone());
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
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
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.PlayModeOverride), oldValue, newValue);
            _repository.Save(_store);
            return;
        }

        using var dialog = new PlayModeDialog($"活动播放方式 — {activity.Name}", activity.DefaultPlayMode);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.SelectedPlayMode == activity.DefaultPlayMode) return; // no actual change — nothing to log/save.

        activity.DefaultPlayMode = dialog.SelectedPlayMode!.Value; // never null — allowInherit defaults false above.
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
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
        _repository.Save(_store);
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
        _repository.Save(_store);
    }

    /// <summary>Only reachable when a selected file's <c>Kind</c> is Image, or Document that isn't an
    /// Office document (see <see cref="IsStayDurationApplicable"/>) — see
    /// <see cref="UpdateButtonStates"/>. Same no-activity-level-counterpart reasoning as
    /// <see cref="OnEditAudioProperties"/>: <c>MediaFile.StayDuration</c> is only ever a per-file
    /// override of the 设置 面板's single global default, there is no per-activity default to fall
    /// back to editing when nothing is selected.</summary>
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
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.StayDuration), oldValue, newValue);
        _repository.Save(_store);
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
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue, newValue);
        _repository.Save(_store);
    }

    private void OnRemoveFile()
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;

        activity.Files.Remove(file);
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
        RefreshTree();
    }

    private void MoveSelectedFile(int delta)
    {
        var (scenario, activity, file) = GetSelection();
        if (scenario == null || activity == null || file == null) return;

        int index = activity.Files.IndexOf(file);
        int newIndex = index + delta;
        if (newIndex < 0 || newIndex >= activity.Files.Count) return;

        (activity.Files[index], activity.Files[newIndex]) = (activity.Files[newIndex], activity.Files[index]);
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _repository.Save(_store);
        RefreshTree();
        SelectFileNode(activity, file);
    }

    private void OnNodeDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        var scenario = CurrentScenario;
        if (scenario == null || _playback == null) return;

        if (e.Node.Tag is Activity activity)
        {
            if (activity.Files.Count > 0) _playback.RequestPlay(activity, 0);
        }
        else if (e.Node.Tag is MediaFile file && e.Node.Parent?.Tag is Activity owningActivity)
        {
            int index = owningActivity.Files.IndexOf(file);
            if (index >= 0) _playback.RequestPlay(owningActivity, index);
        }
    }

    /// <summary>Ctrl+Click toggles a FILE node in/out of <see cref="_multiSelectedFileNodes"/>; any
    /// other click (no Ctrl, or on an activity node) clears the whole set — same "plain click starts a
    /// fresh selection" behavior most multi-select UIs use. Runs independently of the TreeView's own
    /// built-in single-selection handling (which still changes <see cref="TreeView.SelectedNode"/> on
    /// every click, Ctrl or not — <see cref="GetSelection"/> and every single-select-driven button
    /// keep reading that as before, unaffected by this method).</summary>
    private void OnTreeNodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node.Tag is MediaFile && Control.ModifierKeys.HasFlag(Keys.Control))
        {
            if (!_multiSelectedFileNodes.Remove(e.Node))
            {
                _multiSelectedFileNodes.Add(e.Node);
                e.Node.BackColor = SystemColors.Highlight;
                e.Node.ForeColor = SystemColors.HighlightText;
            }
            else
            {
                e.Node.BackColor = _tree.BackColor;
                e.Node.ForeColor = _tree.ForeColor;
            }
        }
        else
        {
            ClearMultiSelection();
        }
        UpdateButtonStates();
    }

    private void ClearMultiSelection()
    {
        foreach (var node in _multiSelectedFileNodes)
        {
            node.BackColor = _tree.BackColor;
            node.ForeColor = _tree.ForeColor;
        }
        _multiSelectedFileNodes.Clear();
    }

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

        _repository.Save(_store);
        // BuildFileNodeText depends on IsBackgroundAudio, and RefreshTree also clears
        // _multiSelectedFileNodes (see that method's own comment) — both reasons OnEditAudioProperties
        // already refreshes after its own IsBackgroundAudio change apply here too.
        RefreshTree();
    }

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
        _addFileButton.Enabled = activity != null;
        _playModeButton.Enabled = activity != null;
        _audioPropertiesButton.Enabled = file != null && file.Kind == MediaKind.Audio;
        _fadeButton.Enabled = file != null && file.Kind is MediaKind.Video or MediaKind.Audio;
        _stayDurationButton.Enabled = file != null && IsStayDurationApplicable(file);
        _completionActionButton.Enabled = file != null;
        _batchPropertiesButton.Enabled = _multiSelectedFileNodes.Count >= 2;
        _removeButton.Enabled = file != null;
        _moveUpButton.Enabled = file != null;
        _moveDownButton.Enabled = file != null;
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
        file.Kind == MediaKind.Image || (file.Kind == MediaKind.Document && !WpsDocumentController.IsOfficeDocument(file.SourcePath));

    private void SelectFileNode(Activity activity, MediaFile file)
    {
        foreach (TreeNode activityNode in _tree.Nodes)
        {
            if (activityNode.Tag != activity) continue;
            foreach (TreeNode fileNode in activityNode.Nodes)
            {
                if (fileNode.Tag == file) { _tree.SelectedNode = fileNode; return; }
            }
        }
    }

    private void DrawActivityNode(object? sender, DrawTreeNodeEventArgs e)
    {
        e.DrawDefault = false;
        if (e.Node is null) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var row = e.Bounds;
        row = new Rectangle(8, row.Top + 5, Math.Max(40, _tree.ClientSize.Width - 24), row.Height - 10);

        if (e.Node.Tag is Activity activity)
        {
            bool active = e.Node.IsSelected || activity.Files.Any(file => ReferenceEquals(file, _lastStartedFile));
            using var path = RoundedRect(row, 12);
            using var fill = new SolidBrush(active ? Color.FromArgb(30, 64, 112) : ModernUi.Surface);
            using var border = new Pen(active ? Color.FromArgb(130, 66, 139, 255) : ModernUi.Border, active ? 1.4F : 1F);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            int numberSize = 22;
            var number = new Rectangle(row.Left + 14, row.Top + 12, numberSize, numberSize);
            using var numberFill = new SolidBrush(active ? ModernUi.Accent : Color.FromArgb(38, 58, 86));
            g.FillEllipse(numberFill, number);
            TextRenderer.DrawText(g, (e.Node.Index + 1).ToString(), new Font("Segoe UI Semibold", 9F), number,
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using var titleFont = new Font("Segoe UI Semibold", 10F);
            TextRenderer.DrawText(g, activity.Name, titleFont,
                new Rectangle(number.Right + 12, row.Top + 8, row.Width - 190, 22), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string mode = activity.DefaultPlayMode == PlayMode.SequentialAuto ? "顺序自动" : "手动点选";
            TextRenderer.DrawText(g, $"{activity.Files.Count} 个文件 · {mode}", Font,
                new Rectangle(number.Right + 12, row.Top + 30, row.Width - 190, 18), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (activity.Files.Any(file => ReferenceEquals(file, _lastStartedFile)))
                TextRenderer.DrawText(g, "● 正在播放", Font, new Rectangle(row.Right - 110, row.Top + 14, 96, 22),
                    ModernUi.Success, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, e.Node.IsExpanded ? "⌃" : "⌄", Font,
                new Rectangle(row.Right - 34, row.Top + 12, 24, 24), ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (e.Node.Tag is MediaFile file)
        {
            bool selected = e.Node.IsSelected || _multiSelectedFileNodes.Contains(e.Node);
            var chip = new Rectangle(row.Left + 28, row.Top + 10, Math.Min(300, row.Width - 56), 42);
            using var chipPath = RoundedRect(chip, 8);
            using var chipFill = new SolidBrush(selected ? Color.FromArgb(38, 72, 112) : ModernUi.SurfaceRaised);
            using var chipBorder = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 1.2F : 1F);
            g.FillPath(chipFill, chipPath);
            g.DrawPath(chipBorder, chipPath);

            string badge = file.Kind switch
            {
                MediaKind.Video => "V",
                MediaKind.Audio => "A",
                MediaKind.Image => "I",
                MediaKind.Document => Path.GetExtension(file.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "W",
                _ => "•",
            };
            var badgeRect = new Rectangle(chip.Left + 8, chip.Top + 9, 24, 24);
            using var badgeFill = new SolidBrush(file.Kind switch
            {
                MediaKind.Video => Color.FromArgb(124, 58, 237),
                MediaKind.Audio => Color.FromArgb(20, 160, 130),
                MediaKind.Document => Color.FromArgb(41, 105, 214),
                _ => ModernUi.Accent,
            });
            g.FillEllipse(badgeFill, badgeRect);
            using var badgeFont = new Font("Segoe UI Semibold", badge.Length > 1 ? 6.5F : 9F);
            TextRenderer.DrawText(g, badge, badgeFont, badgeRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Path.GetFileName(file.SourcePath), Font,
                new Rectangle(badgeRect.Right + 8, chip.Top + 5, chip.Width - 44, 20), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string meta = file.IsBackgroundAudio ? "背景音频 · 循环" : file.Kind switch
            {
                MediaKind.Image or MediaKind.Document => file.StayDuration.HasValue ? $"停留 {file.StayDuration.Value.TotalSeconds:0}s" : "手动翻页",
                _ => file.OnCompletion == CompletionAction.Loop ? "循环" : "播放",
            };
            TextRenderer.DrawText(g, meta, new Font("Segoe UI", 8F),
                new Rectangle(badgeRect.Right + 8, chip.Top + 23, chip.Width - 44, 14), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Call after the scenario store changes from outside this control.</summary>
    public void RefreshTree()
    {
        // Old TreeNode references go stale the moment the tree they belonged to is torn down below —
        // see _multiSelectedFileNodes' own doc comment. No need to reset their BackColor/ForeColor
        // first (they're being discarded, not reused).
        _multiSelectedFileNodes.Clear();
        _tree.Nodes.Clear();
        var scenario = CurrentScenario;
        if (scenario == null) return;

        foreach (var activity in scenario.Activities)
        {
            var activityNode = new TreeNode(activity.Name) { Tag = activity };
            foreach (var file in activity.Files)
                activityNode.Nodes.Add(new TreeNode(BuildFileNodeText(file)) { Tag = file });
            if (activity.IsCollapsed) activityNode.Collapse(); else activityNode.Expand();
            _tree.Nodes.Add(activityNode);
        }
        UpdateButtonStates();
    }

    /// <summary>Flags a background-audio file right in the tree, rather than requiring the operator
    /// to open "音频属性..." on every audio file just to find out — previously the only place this
    /// state was visible at all (besides that dialog's own checkbox) was
    /// <c>AudioPropertiesDialog</c>'s red caveat label, shown only while that dialog is actually open.
    /// The label itself used to say "尚未实现，会被跳过" (see this project's README "已知风险" #84
    /// for the real `PlayMode.SequentialAuto`-stalls-forever bug that phrasing was explaining at the
    /// time) — `PlaybackEngine` now gives these files real overlay-playback behavior (README #61's
    /// remaining gap, closed in a later round), so this label was updated to describe what actually
    /// happens now: the file is skipped for DISPLAY purposes only, not left completely unplayed.</summary>
    private static string BuildFileNodeText(MediaFile file) =>
        file.IsBackgroundAudio
            ? $"{Path.GetFileName(file.SourcePath)} [背景音频叠加播放，不在此列表中显示为当前项]"
            : Path.GetFileName(file.SourcePath);

    /// <summary>Persists a real, user-initiated collapse/expand of an activity node back into
    /// <see cref="Activity.IsCollapsed"/> — see the comment on this class's AfterCollapse/AfterExpand
    /// subscriptions for why this doesn't also fire (and doesn't need to guard against) RefreshTree's
    /// own Collapse()/Expand() calls. Ignored for a file leaf node (its <c>Tag</c> is a
    /// <see cref="MediaFile"/>, not an <see cref="Activity"/>) — file nodes have no children of their
    /// own to collapse/expand in the first place, so <c>node?.Tag is not Activity</c> is defensive
    /// rather than something this method expects to actually hit.</summary>
    private void OnActivityCollapseStateChanged(TreeNode? node, bool collapsed)
    {
        if (node?.Tag is not Activity activity || activity.IsCollapsed == collapsed) return;

        activity.IsCollapsed = collapsed;
        _repository.Save(_store);
    }

    private void OnFileStarted(MediaFile file)
    {
        UpdateStatusBar(file);
        TryHighlightPlayingFile(file);
    }

    /// <summary>PLANNING.md §16第5项's "悬浮预览窗/文件面板/活动面板联动" (previously listed in this
    /// project's README as entirely unimplemented) — the activity-panel half of it: whenever
    /// playback advances to a new file, by any route (floating preview window's 上一项/下一项,
    /// double-clicking a file/activity node here, `CompletionAction.NextItem` auto-advancing), select
    /// that file's tree node so this panel visibly tracks what's actually playing rather than staying
    /// wherever the user last clicked. All of those routes funnel through
    /// <c>PlaybackEngine.PlayFile</c>, which raises <see cref="PlaybackEngine.FileStarted"/> with the
    /// SAME <see cref="MediaFile"/> instance stored in <c>Activity.Files</c> (never a clone) — the
    /// reference-equality check below relies on that.
    ///
    /// Clears the selection instead when the playing file isn't a node in the tree this panel
    /// currently shows: either it was started via
    /// <c>PlaybackEngine.RequestPlay(MediaFile)</c> with no activity context at all (e.g. a direct
    /// double-click from <c>FilesPanel</c> — see that overload's own doc comment), or it belongs to a
    /// scenario other than the one currently selected in <see cref="_scenarioCombo"/>. Clearing the
    /// selection in that case (rather than leaving a stale one) avoids the tree appearing to still
    /// point at whatever the user clicked before playback moved on to something this tree can't
    /// represent.
    ///
    /// Deliberately doesn't also try to highlight anything in <c>FilesPanel</c> (the other half
    /// PLANNING.md's §16第5项 gestures at) — an activity's files are deep copies of library entries
    /// (see risk #22), not the same object nor even the same <c>MediaFile.Id</c>, so there is no
    /// reference-equality check available there the way there is here; the only available signal
    /// would be matching by <c>SourcePath</c>, which is a weaker, more ambiguous link (the same
    /// source file could appear in the library once but be added to several activities, or removed
    /// from the library entirely while still playing from an activity) than this method's exact
    /// object-identity match. Left as a known, explicitly-scoped-out gap rather than built on that
    /// weaker foundation this round.</summary>
    private void TryHighlightPlayingFile(MediaFile? file)
    {
        if (file != null)
        {
            foreach (TreeNode activityNode in _tree.Nodes)
            {
                foreach (TreeNode fileNode in activityNode.Nodes)
                {
                    if (fileNode.Tag != file) continue;
                    _tree.SelectedNode = fileNode;
                    return;
                }
            }
        }

        _tree.SelectedNode = null;
    }

    private void OnStateChanged(OutputState state) => UpdateStatusBar(state == OutputState.Idle ? null : _lastStartedFile);

    private MediaFile? _lastStartedFile;

    private void UpdateStatusBar(MediaFile? file)
    {
        _lastStartedFile = file ?? _lastStartedFile;
        bool active = _stateMachine.State == OutputState.Active;
        _statusBar.Text = active && file != null
            ? $"● 输出中 — {Path.GetFileName(file.SourcePath)}"
            : active ? "● 输出中" : "○ 待机中";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_playback != null) _playback.FileStarted -= OnFileStarted;
            _stateMachine.StateChanged -= OnStateChanged;
        }
        base.Dispose(disposing);
    }
}
