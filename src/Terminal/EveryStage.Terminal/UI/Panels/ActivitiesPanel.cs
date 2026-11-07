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
///
/// The visible surface follows the reference mockup (页头 · 方案工具栏 · 活动节目单 · 播控条): each
/// activity is an <see cref="ActivityRowView"/>, the TreeView stays hidden as the selection/model
/// adapter, and the selection-dependent edits live in one menu (<see cref="_editMenu"/>) shown from
/// 「编辑 ▾」 and on right-click, instead of two rows of always-visible buttons.
/// </summary>
public sealed partial class ActivitiesPanel : UserControl, IActivityRowHost
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

    /// <summary>The visible 活动节目单: one <see cref="ActivityRowView"/> per activity. <see cref="_tree"/>
    /// stays hidden as the selection/model adapter every editing handler reads via <see cref="GetSelection"/>.</summary>
    private readonly FlowLayoutPanel _activityRows;
    /// <summary>The row width <see cref="LayoutRows"/> last fitted, so a drag-resize that fires
    /// Resize repeatedly at the same width doesn't refit every row each time (P-07). Reset by
    /// <see cref="RefreshTree"/> whenever the rows themselves are rebuilt.</summary>
    private int _lastRowsLayoutWidth = -1;
    private readonly Label _activityCount;
    private readonly Label _emptyState;
    private readonly System.Windows.Forms.Timer _saveFeedbackTimer;

    /// <summary>Every selection-dependent edit, opened from the list header's「编辑 ▾」and as the
    /// right-click menu of a row or file. The *Button field names below predate this: they were
    /// always-visible buttons until the reference-design rework, and kept their names so the comments
    /// and handlers that refer to them stay valid.</summary>
    private readonly ContextMenuStrip _editMenu;
    private readonly ContextMenuStrip _scenarioMenu;
    private readonly ToolStripMenuItem _playSelectedItem;
    private readonly ToolStripMenuItem _renameActivityItem;
    private readonly ToolStripMenuItem _deleteActivityItem;
    private readonly ToolStripMenuItem _addFileButton;
    private readonly ToolStripMenuItem _playModeButton;
    private readonly ToolStripMenuItem _audioPropertiesButton;
    private readonly ToolStripMenuItem _fadeButton;
    private readonly ToolStripMenuItem _stayDurationButton;
    private readonly ToolStripMenuItem _completionActionButton;
    private readonly ToolStripMenuItem _documentSettingsButton;
    private readonly ToolStripMenuItem _batchPropertiesButton;
    private readonly ToolStripMenuItem _removeButton;
    private readonly ToolStripMenuItem _moveUpButton;
    private readonly ToolStripMenuItem _moveDownButton;
    private readonly ToolStripMenuItem _musicCueItem;
    private readonly ToolStripMenuItem _transitionItem;

    /// <summary>The background-music playlists, for the per-section music cue.</summary>
    public Func<IReadOnlyList<Playlist>>? Playlists { get; set; }

    // 「下一项」 run controls: shown in MainWindow's 监看 column while this page is visible.
    private readonly Panel _runControls;
    private readonly Label _nextName;
    private readonly RoundedActionButton _runNextButton;
    private readonly RoundedActionButton _runPreviousButton;
    private readonly RoundedActionButton _runPreviewButton;
    private readonly System.Windows.Forms.Timer _liveTimer;
    private MediaFile? _lastLiveFile;
    private readonly ToolTip _toolTip = new();

    /// <summary>The engine that drives the big screen (null while no extended display is bound). The
    /// run sheet's 「大屏播放中 / 下一项 / 已播」 follow it, or the Preview engine when it is null.</summary>
    public Func<PlaybackEngine?>? ProgramEngine { get; set; }

    /// <summary>Shows a toast in the main window (wired by MainWindow to its ToastStack; same pattern as
    /// SettingsPanel's). The delete flows in ActivitiesPanel.Scenarios.cs use it for the 10 s 撤销 offer
    /// after a scenario/activity is deleted. Null when not wired — deletions then behave exactly as
    /// before, just without the undo offer.</summary>
    public Func<string, ToastSeverity, ToastAction[], ToastNotification>? ShowToast { get; set; }

    /// <summary>Run this item now: MainWindow puts it on the big screen (or in Preview without one).</summary>
    public event Action<Activity, int>? RunRequested;

    /// <summary>「下一项 ▶」 and friends, for MainWindow's 监看 column.</summary>
    public Control RunControls => _runControls;

    /// <summary>Row thumbnails, keyed by kind + source path, built off the UI thread by
    /// <see cref="FilesPanel.BuildThumbnail"/> (the same readers and fallback tiles the 文件 page uses).</summary>
    private readonly Dictionary<string, Image> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thumbnailsRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _thumbnailUnavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _thumbnailSourceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _visibleFileCheckTimer = new() { Interval = 3000 };
    private bool _visibleFileCheckRunning;
    private int _thumbnailGeneration;

    /// <summary>File nodes Ctrl+Clicked into a multi-selection for "统一设置属性..." — see
    /// <see cref="OnTreeNodeMouseClick"/>. Deliberately separate from <see cref="TreeView.SelectedNode"/>
    /// (which every single-select-driven button above still reads via <see cref="GetSelection"/>,
    /// completely unaffected by this set): WinForms' <see cref="TreeView"/> has no built-in
    /// multi-select, and full owner-draw was rejected as more new/unverified surface than this
    /// project needs — same "reuse an established, simple control pattern over an unverified one"
    /// reasoning the former floating preview window's own doc comment on its step buttons (instead of
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

        // --- 页头：流程 · 方案 [▾] · N 个环节 …… ＋ 环节 · ＋ 添加文件 · ⋯ ---
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        var title = ModernUi.SetForegroundRole(new Label { Text = "流程", AutoSize = true, Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent }, UiForegroundRole.Text);
        var scenarioLabel = ModernUi.SetForegroundRole(new Label { Text = "方案", AutoSize = true, BackColor = Color.Transparent }, UiForegroundRole.Muted);
        _scenarioCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        ModernUi.StyleComboBox(_scenarioCombo);
        _scenarioCombo.SelectedIndexChanged += OnScenarioComboChanged;
        _activityCount = ModernUi.SetForegroundRole(new Label { AutoSize = true, BackColor = Color.Transparent }, UiForegroundRole.Muted);
        _saveFeedbackTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _saveFeedbackTimer.Tick += (_, _) => { _saveFeedbackTimer.Stop(); UpdateCountLabel(); };
        var addSectionButton = ActionButton("＋ 环节", OnNewActivity, width: 84);
        var addFileButton = ActionButton("＋ 添加文件", OnAddFile, width: 104);
        var checkButton = ActionButton("开演前检查", () => RunPreShowCheck(requestedByOperator: true), width: 100);
        var scenarioMenu = _scenarioMenu = ModernUi.CreateThemedMenu(this);
        scenarioMenu.Items.Add("新建方案...", null, (_, _) => OnNewScenario());
        scenarioMenu.Items.Add("保存方案", null, (_, _) => OnSaveScenario());
        scenarioMenu.Items.Add("方案另存为...", null, (_, _) => OnSaveAsScenario());
        scenarioMenu.Items.Add("删除方案", null, (_, _) => OnDeleteScenario());
        scenarioMenu.Items.Add(new ToolStripSeparator());
        var expandAllItem = new ToolStripMenuItem("全部展开 / 收起", null, (_, _) => OnToggleExpandAll());
        scenarioMenu.Items.Add(expandAllItem);
        scenarioMenu.Opening += (_, _) =>
        {
            scenarioMenu.BackColor = ModernUi.Surface;
            foreach (ToolStripItem item in scenarioMenu.Items) item.ForeColor = ModernUi.Text;
            expandAllItem.Text = CurrentScenario?.Activities.Any(a => !a.IsCollapsed) == true ? "全部收起" : "全部展开";
        };
        RoundedActionButton? moreButton = null;
        moreButton = ActionButton("⋯", () => scenarioMenu.Show(moreButton!, new Point(0, moreButton!.Height)), width: 40);
        _toolTip.SetToolTip(moreButton, "方案：新建 / 保存 / 另存为 / 删除；全部展开或收起");
        _toolTip.SetToolTip(addFileButton, "从文件库添加到所选环节");
        _toolTip.SetToolTip(checkButton, "检查方案里每个文件是否存在、能否读取、格式是否支持（打开方案时也会自动检查）");
        header.Controls.AddRange(new Control[] { title, scenarioLabel, _scenarioCombo, _activityCount, checkButton, addSectionButton, addFileButton, moreButton });
        header.Resize += (_, _) =>
        {
            int cy = (header.ClientSize.Height - 6) / 2, gap = 10;
            title.Location = new Point(0, cy - title.Height / 2);
            scenarioLabel.Location = new Point(title.Right + 16, cy - scenarioLabel.Height / 2);
            int right = header.ClientSize.Width;
            foreach (var button in new[] { moreButton, addFileButton, addSectionButton, checkButton })
            {
                right -= button.Width;
                button.Location = new Point(right, cy - button.Height / 2);
                right -= gap;
            }
            int comboLeft = scenarioLabel.Right + 8;
            // Narrow page: 开演前检查 shortens to 检查 so it never covers the scenario list.
            bool narrow = right - comboLeft < 120 + gap;
            checkButton.Text = narrow ? "检查" : "开演前检查";
            if (narrow)
            {
                right += checkButton.Width + gap;
                checkButton.Width = 56;
                right -= checkButton.Width + gap;
                checkButton.Location = new Point(right + gap, cy - checkButton.Height / 2);
            }
            else checkButton.Width = 100;
            _scenarioCombo.Width = Math.Clamp(right - comboLeft - _activityCount.PreferredWidth - 2 * gap, Math.Min(120, Math.Max(60, right - comboLeft - gap)), 240);
            _scenarioCombo.Location = new Point(comboLeft, cy - _scenarioCombo.Height / 2);
            _activityCount.Location = new Point(_scenarioCombo.Right + gap, cy - _activityCount.Height / 2);
            _activityCount.Visible = _activityCount.Right <= right;
        };

        // --- 编辑菜单：「编辑 ▾」按钮与行/文件的右键菜单共用同一个菜单 ---
        // These used to be two always-visible rows of 14 buttons above the list, which the reference
        // design doesn't have. Each item's enabled state still comes from UpdateButtonStates, and each
        // still calls the exact same handler it did as a button.
        _playSelectedItem = new ToolStripMenuItem("▶ 从这里开始播") { Enabled = false };
        _playSelectedItem.Click += (_, _) => PlaySelectedActivity();
        _renameActivityItem = new ToolStripMenuItem("重命名环节...") { Enabled = false };
        _renameActivityItem.Click += (_, _) => OnRenameActivity();
        _deleteActivityItem = new ToolStripMenuItem("删除环节") { Enabled = false };
        _deleteActivityItem.Click += (_, _) => OnDeleteActivity();
        _addFileButton = new ToolStripMenuItem("添加文件...") { Enabled = false };
        _addFileButton.Click += (_, _) => OnAddFile();
        // Editing PlayMode is new (see PlaybackEngine.EffectivePlayMode's doc comment on why it
        // previously had nothing to edit — the enum had no runtime effect at all until it did).
        // Context-sensitive: edits the selected FILE's PlayModeOverride if one is selected, else the
        // selected ACTIVITY's own DefaultPlayMode — see OnEditPlayMode.
        _playModeButton = new ToolStripMenuItem("播放方式...") { Enabled = false };
        _playModeButton.Click += (_, _) => OnEditPlayMode();
        // Only ever enabled for a selected file whose Kind == MediaKind.Audio — see
        // UpdateButtonStates. New the same round PlaybackEngine.PlayStandaloneAudio/ApplyAudioVisual
        // first gave MediaFile.IsBackgroundAudio/BackgroundAudioVisual any runtime effect at all
        // (see this project's README risk #61), following the same "先做行为、再做UI" order
        // PlayMode's editor above already established.
        _audioPropertiesButton = new ToolStripMenuItem("音频属性...") { Enabled = false };
        _audioPropertiesButton.Click += (_, _) => OnEditAudioProperties();
        // Only ever enabled for a selected file whose Kind is Video or Audio — see
        // UpdateButtonStates. New the same round PlaybackEngine.FadeDurationFor first gave
        // MediaFile.FadeDuration/VolumeFollowsFade any runtime effect at all (see this project's
        // README risk #109), same "先做行为、再做UI" order as PlayMode/IsBackgroundAudio/
        // StayDuration before it — except unlike those, the behavior itself is still only half done
        // (fade-in only), which FadeDialog's own caveat label discloses rather than hiding the
        // control until fade-out also exists.
        _fadeButton = new ToolStripMenuItem("淡入淡出...") { Enabled = false };
        _fadeButton.Click += (_, _) => OnEditFade();
        // Only ever enabled for a selected file whose Kind is Image or Document (PLANNING.md §6
        // "停留时长（图片/文档）") — same "先做行为、再做UI" gap as PlayMode/AllowManualSkip/
        // IsBackgroundAudio before it, except MediaFile.StayDuration's own reading behavior
        // (PlaybackEngine.ArmStayDurationTimer) already existed and worked; only this editing entry
        // point was ever missing. Worse than a plain gap: SettingsPanel's own "默认停留时长" help
        // text already claimed a per-file override could be "配置" from "活动面板" before this
        // button existed to do it — see this project's README "已知风险".
        _stayDurationButton = new ToolStripMenuItem("停留时长...") { Enabled = false };
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
        _completionActionButton = new ToolStripMenuItem("完成后动作...") { Enabled = false };
        _completionActionButton.Click += (_, _) => OnEditCompletionAction();
        // PDF / Word / presentation playback settings (reading mode, page range, editor, native/static…).
        _documentSettingsButton = new ToolStripMenuItem("文档播放设置...") { Enabled = false };
        _documentSettingsButton.Click += (_, _) => OnEditDocumentSettings();
        // Enabled once 2+ file nodes are Ctrl+Click multi-selected — see _multiSelectedFileNodes'
        // own doc comment. Unlike every other button in this bar, this one is NOT driven by
        // GetSelection()/_tree.SelectedNode at all.
        _batchPropertiesButton = new ToolStripMenuItem("统一设置属性...") { Enabled = false };
        _batchPropertiesButton.Click += (_, _) => OnEditBatchProperties();
        _removeButton = new ToolStripMenuItem("移除文件") { Enabled = false };
        _removeButton.Click += (_, _) => OnRemoveFile();
        _moveUpButton = new ToolStripMenuItem("上移") { Enabled = false };
        _moveUpButton.Click += (_, _) => MoveSelectedFile(-1);
        _moveDownButton = new ToolStripMenuItem("下移") { Enabled = false };
        _moveDownButton.Click += (_, _) => MoveSelectedFile(1);
        _editMenu = ModernUi.CreateThemedMenu(this);
        // 「进入本环节时的背景音乐」: keep / fade out / switch to a playlist (filled when the menu opens).
        _musicCueItem = new ToolStripMenuItem("进入本环节时的背景音乐") { Enabled = false };
        _musicCueItem.DropDownItems.Add(new ToolStripMenuItem("(placeholder)"));
        _musicCueItem.DropDownOpening += (_, _) => FillMusicCueMenu();
        // 「进入这一项的过场」: 默认 / 直切 / 叠化 / 经黑场 for the selected item.
        _transitionItem = new ToolStripMenuItem("进入这一项的过场") { Enabled = false };
        _transitionItem.DropDownItems.Add(new ToolStripMenuItem("(placeholder)"));
        _transitionItem.DropDownOpening += (_, _) => FillTransitionMenu();
        _editMenu.Items.AddRange(new ToolStripItem[]
        {
            _playSelectedItem, new ToolStripSeparator(),
            _renameActivityItem, _addFileButton, _playModeButton, _musicCueItem, _deleteActivityItem, new ToolStripSeparator(),
            _audioPropertiesButton, _fadeButton, _stayDurationButton, _completionActionButton, _transitionItem, _documentSettingsButton, _batchPropertiesButton,
            new ToolStripSeparator(),
            _moveUpButton, _moveDownButton, _removeButton,
        });
        _editMenu.Opening += (_, _) => UpdateButtonStates();

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
        _tree.AfterSelect += (_, _) => { UpdateButtonStates(); RefreshCardStyles(); RefreshFileDetails(); };
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
        // Never shown: it is only the selection/model adapter behind the ActivityRowView list below.
        _tree.Visible = false;

        // --- 列标题 + 节目单 ---
        var columnHeader = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.Transparent };
        columnHeader.Paint += (_, e) =>
        {
            var c = ActivityRowView.ColumnsFor(columnHeader.Width - SystemInformation.VerticalScrollBarWidth - 2, LogicalToDeviceUnits);
            using var font = new Font(ModernUi.UiFont, 8.5F);
            void Draw(string text, int x) => TextRenderer.DrawText(e.Graphics, text, font, new Point(x, (columnHeader.Height - font.Height) / 2), ModernUi.Muted);
            Draw("序号", c.Number);
            Draw("画面", c.Thumb);
            Draw("名称", c.Name);
            if (c.Wide) { Draw("时长", c.Duration); Draw("结束后", c.After); }
            Draw("状态", c.Status);
        };
        columnHeader.Resize += (_, _) => columnHeader.Invalidate();

        _activityRows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 12),
            BackColor = Color.Transparent,
        };
        _activityRows.Resize += (_, _) => LayoutRows();
        _emptyState = ModernUi.SetForegroundRole(new Label
        {
            Text = "这个方案还没有环节\r\n点右上角「＋ 环节」开始编排流程",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent, Visible = false,
        }, UiForegroundRole.Muted);

        // --- 下一项 run controls (hosted by MainWindow's 监看 column) ---
        _runControls = new Panel { BackColor = Color.Transparent, Height = 128 };
        var nextCaption = new Badge("下一项");
        _nextName = ModernUi.SetForegroundRole(new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 9.5F) }, UiForegroundRole.Text);
        _runNextButton = ActionButton("下一项  ▶", GoNext, primary: true, height: 52);
        _runNextButton.Font = new Font(ModernUi.UiFontSemibold, 13F);
        _runPreviousButton = ActionButton("◀ 上一项", GoPrevious, height: 34);
        _runPreviewButton = ActionButton("预览下一项", PreviewNext, height: 34);
        _toolTip.SetToolTip(_runNextButton, "把下一项放到大屏（空格键）");
        _toolTip.SetToolTip(_runPreviousButton, "回到上一项");
        _toolTip.SetToolTip(_runPreviewButton, "先在预览里看下一项，不上大屏");
        _runControls.Controls.AddRange(new Control[] { nextCaption, _nextName, _runNextButton, _runPreviousButton, _runPreviewButton });
        _runControls.Layout += (_, _) =>
        {
            int w = _runControls.ClientSize.Width, s6 = LogicalToDeviceUnits(6), line = LogicalToDeviceUnits(22);
            int badge = nextCaption.PreferredWidth;
            nextCaption.Bounds = new Rectangle(0, (line - LogicalToDeviceUnits(18)) / 2, badge, LogicalToDeviceUnits(18));
            _nextName.Bounds = new Rectangle(badge + s6, 0, Math.Max(1, w - badge - s6), line);
            _runNextButton.Bounds = new Rectangle(0, line + s6, w, LogicalToDeviceUnits(52));
            int half = (w - LogicalToDeviceUnits(8)) / 2, y = _runNextButton.Bottom + s6;
            _runPreviousButton.Bounds = new Rectangle(0, y, half, LogicalToDeviceUnits(34));
            _runPreviewButton.Bounds = new Rectangle(half + LogicalToDeviceUnits(8), y, w - half - LogicalToDeviceUnits(8), LogicalToDeviceUnits(34));
            int height = _runPreviousButton.Bottom;
            if (_runControls.Height != height) _runControls.Height = height;
        };

        // Repaint when what is live changes (Program advances on its own, or output stops).
        _liveTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _liveTimer.Tick += (_, _) =>
        {
            var live = LiveEngine?.CurrentFile;
            if (ReferenceEquals(live, _lastLiveFile))
            {
                // P-02: only the row containing the live file repaints (its progress/state is
                // what actually moves) — invalidating every row every 400 ms repainted the
                // whole run sheet for one row's progress. If the live file belongs to no row
                // here (e.g. played straight from the 文件 panel), no row is invalidated.
                if (Visible && _lastLiveFile != null
                    && (ProgramEngine?.Invoke() is { State: PlaybackChannelState.Playing or PlaybackChannelState.Paused }
                        || _playback is { State: PlaybackChannelState.Playing or PlaybackChannelState.Paused }))
                {
                    foreach (var row in _activityRows.Controls.OfType<ActivityRowView>())
                    {
                        if (!row.Activity.Files.Contains(_lastLiveFile)) continue;
                        row.Invalidate();
                        break;
                    }
                }
                return;
            }
            _lastLiveFile = live;
            RefreshCardStyles();
        };
        _liveTimer.Start();
        _visibleFileCheckTimer.Tick += (_, _) => _ = RefreshVisibleFileStatesAsync();
        _visibleFileCheckTimer.Start();

        BuildDetailPanel();

        // Dock order: the last Top control added sits highest, so the header goes in last.
        // _detailPanel is added right after _activityRows so that, in the reverse order docking
        // runs in, its Right dock is processed before the list's Fill — it claims the right edge
        // first and _activityRows fills what remains (see ActivitiesPanel.Details.cs).
        Controls.Add(_activityRows);
        Controls.Add(_detailPanel);
        Controls.Add(_emptyState);
        Controls.Add(_tree);
        Controls.Add(columnHeader);
        Controls.Add(header);

        if (_playback != null) _playback.FileStarted += OnFileStarted;
        _stateMachine.StateChanged += OnStateChanged;

        RefreshScenarioCombo();
        RefreshTree();

        RoundedActionButton ActionButton(string text, Action onClick, bool primary = false, int width = 84, int height = 34)
        {
            var button = new RoundedActionButton { Text = text, Width = width, Height = height, Margin = new Padding(3, 2, 3, 0) };
            ModernUi.StyleButton(button, primary: primary);
            button.Click += (_, _) => onClick();
            return button;
        }
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
    /// <summary>Idempotent, and unsubscribes the previous engine first. Today Program.cs only
    /// ever reaches this once (OnDisplaySettingsChanged re-binds only while _overlay is still
    /// null, and _overlay stays non-null once bound), so a second call is currently unreachable
    /// - but FilesPanel.AttachPlaybackEngine already guards the same way, and the moment that
    /// binding guard is relaxed for monitor hot-swap this panel would silently double-subscribe
    /// while FilesPanel stayed correct. Two of three implementations diverging is exactly the
    /// shape that makes "every file plays twice" hard to track down.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        if (ReferenceEquals(_playback, playback)) return;
        if (_playback != null) _playback.FileStarted -= OnFileStarted;
        _playback = playback;
        _playback.FileStarted += OnFileStarted;
    }

    /// <summary>Double-click: run from this item (an activity node runs from its first item).</summary>
    private void OnNodeDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node.Tag is Activity activity)
        {
            if (FirstRunnable(activity, 0) is int first) RunRequested?.Invoke(activity, first);
        }
        else if (e.Node.Tag is MediaFile file && e.Node.Parent?.Tag is Activity owningActivity)
        {
            int index = owningActivity.Files.IndexOf(file);
            if (index >= 0) RunRequested?.Invoke(owningActivity, index);
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
        RefreshCardStyles();
    }

    /// <summary>设置 「单击即上屏」: a plain click on an item runs it (as a double-click does otherwise).</summary>
    public Func<bool>? ClickTakesToAir { get; set; }

    private void OnCardNodeClick(TreeNode node)
    {
        _tree.SelectedNode = node;
        if (node.Tag is MediaFile && Control.ModifierKeys.HasFlag(Keys.Control))
        {
            if (!_multiSelectedFileNodes.Remove(node)) _multiSelectedFileNodes.Add(node);
            UpdateButtonStates();
        }
        else
        {
            ClearMultiSelection();
            UpdateButtonStates();
            if (node.Tag is MediaFile && Control.ModifierKeys == Keys.None && ClickTakesToAir?.Invoke() == true)
                OnNodeDoubleClick(this, new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 1, 0, 0));
        }
        RefreshCardStyles();
    }

    private void OnCardNodeDoubleClick(TreeNode node)
    {
        if (ClickTakesToAir?.Invoke() == true) return; // the first click already put it on air
        OnNodeDoubleClick(this, new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 0, 0));
    }

    /// <summary>Repaints the rows (selection, multi-selection and playing state are read at paint time)
    /// and refreshes the 播控条, which follows the current activity.</summary>
    private void RefreshCardStyles()
    {
        foreach (Control row in _activityRows.Controls) row.Invalidate();
        UpdateRunControls();
    }

    private void PlaySelectedActivity()
    {
        if (_tree.SelectedNode is { } node) OnNodeDoubleClick(this, new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 0, 0));
    }

    internal void RefreshTheme() => RefreshTree();

    /// <summary>Gives every row the list's width and the height that width needs. The scrollbar's width
    /// is always reserved, so a scrollbar appearing can't change the width and re-trigger layout.</summary>
    private void LayoutRows()
    {
        int width = Math.Max(LogicalToDeviceUnits(320),
            _activityRows.Width - SystemInformation.VerticalScrollBarWidth - _activityRows.Padding.Horizontal - 2);
        if (width == _lastRowsLayoutWidth) return; // same width as last fit — nothing to redo (P-07)
        _lastRowsLayoutWidth = width;
        _activityRows.SuspendLayout();
        foreach (var row in _activityRows.Controls.OfType<ActivityRowView>()) row.FitToWidth(width);
        _activityRows.ResumeLayout();
        // The rows were resized while layout was suspended, and ResumeLayout alone left AutoScroll with
        // a stale range (no scrollbar although the rows overflowed) — lay out once more to recompute it.
        _activityRows.PerformLayout();
    }

    PreShowProblem? IActivityRowHost.PreShowProblemOf(MediaFile file) =>
        _preShowProblems.TryGetValue(file, out var problem) ? problem : null;

    private void BeginInvokeWhenReady(Action action)
    {
        if (IsHandleCreated) { BeginInvoke(action); return; }
        void OnCreated(object? sender, EventArgs e)
        {
            HandleCreated -= OnCreated;
            BeginInvoke(action);
        }
        HandleCreated += OnCreated;
    }

    bool? IActivityRowHost.IsUnavailable(MediaFile file) =>
        _thumbnailUnavailable.TryGetValue($"{file.Kind}|{file.SourcePath}", out bool unavailable) ? unavailable : null;

    /// <summary>「保存」: every edit here already saves immediately, so this is an explicit, visible
    /// confirmation — and a way to retry after an earlier save failed — not the only way to save.</summary>
    private void OnSaveScenario()
    {
        try
        {
            _repository.Save(_store); // explicit retry: report, keep the edits in memory
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"方案未保存：{DataDirectory.Describe(ex)}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _activityCount.Text = "✓ 已保存";
        _saveFeedbackTimer.Stop();
        _saveFeedbackTimer.Start();
    }

    /// <summary>「展开全部 / 收起全部」: collapses everything if any activity is expanded, else expands all.</summary>
    private void OnToggleExpandAll()
    {
        var scenario = CurrentScenario;
        if (scenario == null || scenario.Activities.Count == 0) return;
        bool collapse = scenario.Activities.Any(a => !a.IsCollapsed);
        foreach (var activity in scenario.Activities) activity.IsCollapsed = collapse;
        if (!TrySave()) return;
        RefreshTree();
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

    private void UpdateCountLabel()
    {
        var scenario = CurrentScenario;
        int sections = scenario?.Activities.Count ?? 0, items = scenario?.Activities.Sum(a => a.Files.Count) ?? 0;
        _activityCount.Text = $"{sections} 个环节 · {items} 项";
    }

    /// <summary>Call after the scenario store changes from outside this control.</summary>
    public void RefreshTree()
    {
        // 开演前检查 runs by itself whenever a (different) scenario is opened.
        if (CurrentScenario is { } opened && opened.Id != _preShowCheckedScenario)
        {
            _preShowProblems.Clear();
            BeginInvokeWhenReady(() => RunPreShowCheck(requestedByOperator: false));
        }
        _thumbnailGeneration++;
        foreach (var image in _thumbnails.Values) image.Dispose();
        _thumbnails.Clear();
        _thumbnailsRequested.Clear();
        _thumbnailUnavailable.Clear();
        _thumbnailSourceKeys.Clear();
        // Old TreeNode references go stale the moment the tree they belonged to is torn down below —
        // see _multiSelectedFileNodes' own doc comment. No need to reset their BackColor/ForeColor
        // first (they're being discarded, not reused). The selection is carried over by Tag (the
        // Activity/MediaFile object itself) so an edit or a collapse toggle doesn't drop it.
        object? selectedTag = _tree.SelectedNode?.Tag;
        _multiSelectedFileNodes.Clear();
        _tree.Nodes.Clear();
        _activityRows.SuspendLayout();
        var oldRows = _activityRows.Controls.Cast<Control>().ToList();
        _activityRows.Controls.Clear();
        foreach (var row in oldRows) row.Dispose();

        var scenario = CurrentScenario;
        int count = scenario?.Activities.Count ?? 0;
        if (scenario != null)
        {
            for (int i = 0; i < scenario.Activities.Count; i++)
            {
                var activity = scenario.Activities[i];
                var activityNode = new TreeNode(activity.Name) { Tag = activity };
                foreach (var file in activity.Files)
                    activityNode.Nodes.Add(new TreeNode(BuildFileNodeText(file)) { Tag = file });
                if (activity.IsCollapsed) activityNode.Collapse(); else activityNode.Expand();
                _tree.Nodes.Add(activityNode);
                _activityRows.Controls.Add(new ActivityRowView(this, activity, activityNode, i + 1));
            }
        }
        _activityRows.ResumeLayout(true);
        _lastRowsLayoutWidth = -1; // rows were just rebuilt — they must be fitted even at the same width
        LayoutRows();

        UpdateCountLabel();
        _emptyState.Visible = count == 0;
        _activityRows.Visible = count > 0;
        if (selectedTag != null && FindNode(selectedTag) is { } reselect) _tree.SelectedNode = reselect;
        RefreshCardStyles();
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
        if (!TrySave()) return;
    }

    private void OnFileStarted(MediaFile file)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFileStarted(file)); return; }
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
                    RefreshCardStyles();
                    return;
                }
            }
        }

        _tree.SelectedNode = null;
        RefreshCardStyles();
    }

    /// <summary>Output starting/stopping changes the rows' 播放中 state and the 播控条's 当前 line.</summary>
    private void OnStateChanged(OutputState state)
    {
        if (InvokeRequired) { BeginInvoke(() => OnStateChanged(state)); return; }
        RefreshCardStyles();
    }

    /// <summary>L-05: keyboard selection navigation. The visible list is
    /// <see cref="ActivityRowView"/>s driven by the hidden <see cref="_tree"/>, which never has
    /// focus — so without this, moving the selection was mouse-only even though everything the
    /// selection drives (「编辑 ▾」 menu, detail panel) is keyboard-reachable. Plain Up/Down —
    /// when focus is NOT in a control that uses those keys itself (a combo picking a value, a
    /// text box, a seconds spinner, a list, the tree) — moves <see cref="_tree"/>'s selection
    /// one step through the flat activity-then-its-files order, stopping at both ends. A target
    /// file inside a collapsed activity expands that activity first (through the same
    /// AfterExpand path a manual toggle takes, so the collapse state persists the same way),
    /// and the owning activity's row is scrolled into view. This deliberately only fills the
    /// "selection" gap: 上屏 / 下一项 / 上一项 / 急停 already have configurable global hotkeys
    /// handled by MainWindow.ProcessCmdKey, and this panel's own command surface is real buttons
    /// and menus (Tab-reachable, keyboard-operable) — with selection movable, the whole panel is
    /// walkable by keyboard end to end.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Up or Keys.Down
            && FocusedLeafControl() is not (ComboBox or TextBoxBase or NumericUpDown or ListBox or TreeView)
            && MoveSelectionByKey(keyData == Keys.Down ? 1 : -1))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The control that actually has keyboard focus inside this panel, walking down
    /// nested containers the same way MainWindow.ProcessCmdKey's own helper does.</summary>
    private Control? FocusedLeafControl()
    {
        Control? active = ActiveControl;
        while (active is ContainerControl { ActiveControl: { } inner }) active = inner;
        return active;
    }

    /// <summary>Moves the tree selection <paramref name="delta"/> steps (±1) in the flat
    /// activity-then-its-files order. Returns false only when there is nothing to select;
    /// at either end the selection stays put but the key still counts as handled, so the list
    /// doesn't scroll out from under a selection the operator is stepping through.</summary>
    private bool MoveSelectionByKey(int delta)
    {
        var flat = new List<TreeNode>();
        foreach (TreeNode activityNode in _tree.Nodes)
        {
            flat.Add(activityNode);
            foreach (TreeNode fileNode in activityNode.Nodes) flat.Add(fileNode);
        }
        if (flat.Count == 0) return false;

        int index = _tree.SelectedNode is { } selected ? flat.IndexOf(selected) : -1;
        int target = index < 0
            ? (delta > 0 ? 0 : flat.Count - 1)
            : Math.Clamp(index + delta, 0, flat.Count - 1);
        var node = flat[target];
        if (node.Parent is { } parent && !parent.IsExpanded) parent.Expand();
        _tree.SelectedNode = node;
        if (node.Tag is Activity activity) ScrollActivityRowIntoView(activity);
        else if (node.Parent?.Tag is Activity owner) ScrollActivityRowIntoView(owner);
        return true;
    }

    private void ScrollActivityRowIntoView(Activity activity)
    {
        foreach (var row in _activityRows.Controls.OfType<ActivityRowView>())
            if (row.Activity == activity) { _activityRows.ScrollControlIntoView(row); return; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailGeneration++;
            if (_playback != null) _playback.FileStarted -= OnFileStarted;
            _stateMachine.StateChanged -= OnStateChanged;
            _saveFeedbackTimer.Dispose();
            _liveTimer.Dispose();
            _visibleFileCheckTimer.Dispose();
            _editMenu.Dispose();
            _scenarioMenu.Dispose();
            _toolTip.Dispose();
            foreach (var image in _thumbnails.Values) image.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
    }
}
