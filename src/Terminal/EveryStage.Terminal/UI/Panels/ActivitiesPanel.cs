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
public sealed class ActivitiesPanel : UserControl, IActivityRowHost
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
    private readonly Label _activityCount;
    private readonly Label _emptyState;
    private readonly System.Windows.Forms.Timer _saveFeedbackTimer;

    /// <summary>Every selection-dependent edit, opened from the list header's「编辑 ▾」and as the
    /// right-click menu of a row or file. The *Button field names below predate this: they were
    /// always-visible buttons until the reference-design rework, and kept their names so the comments
    /// and handlers that refer to them stay valid.</summary>
    private readonly ContextMenuStrip _editMenu;
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

    /// <summary>Run this item now: MainWindow puts it on the big screen (or in Preview without one).</summary>
    public event Action<Activity, int>? RunRequested;

    /// <summary>「下一项 ▶」 and friends, for MainWindow's 监看 column.</summary>
    public Control RunControls => _runControls;

    /// <summary>Row thumbnails, keyed by kind + source path, built off the UI thread by
    /// <see cref="FilesPanel.BuildThumbnail"/> (the same readers and fallback tiles the 文件 page uses).</summary>
    private readonly Dictionary<string, Image> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thumbnailsRequested = new(StringComparer.OrdinalIgnoreCase);

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

        // --- 页头：流程 · 方案 [▾] · N 个环节 …… ＋ 环节 · ＋ 添加文件 · ⋯ ---
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        var title = new Label { Text = "流程", AutoSize = true, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent };
        var scenarioLabel = new Label { Text = "方案", AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent };
        _scenarioCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        ModernUi.StyleComboBox(_scenarioCombo);
        _scenarioCombo.SelectedIndexChanged += OnScenarioComboChanged;
        _activityCount = new Label { AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent };
        _saveFeedbackTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _saveFeedbackTimer.Tick += (_, _) => { _saveFeedbackTimer.Stop(); UpdateCountLabel(); };
        var addSectionButton = ActionButton("＋ 环节", OnNewActivity, width: 84);
        var addFileButton = ActionButton("＋ 添加文件", OnAddFile, width: 104);
        var scenarioMenu = new ContextMenuStrip { ShowImageMargin = false, BackColor = ModernUi.Surface, ForeColor = ModernUi.Text, Renderer = new ThemedMenuRenderer() };
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
        header.Controls.AddRange(new Control[] { title, scenarioLabel, _scenarioCombo, _activityCount, addSectionButton, addFileButton, moreButton });
        header.Resize += (_, _) =>
        {
            int cy = (header.ClientSize.Height - 6) / 2, gap = 10;
            title.Location = new Point(0, cy - title.Height / 2);
            scenarioLabel.Location = new Point(title.Right + 16, cy - scenarioLabel.Height / 2);
            int right = header.ClientSize.Width;
            foreach (var button in new[] { moreButton, addFileButton, addSectionButton })
            {
                right -= button.Width;
                button.Location = new Point(right, cy - button.Height / 2);
                right -= gap;
            }
            int comboLeft = scenarioLabel.Right + 8;
            _scenarioCombo.Width = Math.Clamp(right - comboLeft - _activityCount.PreferredWidth - 2 * gap, 120, 240);
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
        _editMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = ModernUi.Surface,
            ForeColor = ModernUi.Text,
            Renderer = new ThemedMenuRenderer(),
        };
        // 「进入本环节时的背景音乐」: keep / fade out / switch to a playlist (filled when the menu opens).
        _musicCueItem = new ToolStripMenuItem("进入本环节时的背景音乐") { Enabled = false };
        _musicCueItem.DropDownItems.Add(new ToolStripMenuItem("(placeholder)"));
        _musicCueItem.DropDownOpening += (_, _) => FillMusicCueMenu();
        _editMenu.Items.AddRange(new ToolStripItem[]
        {
            _playSelectedItem, new ToolStripSeparator(),
            _renameActivityItem, _addFileButton, _playModeButton, _musicCueItem, _deleteActivityItem, new ToolStripSeparator(),
            _audioPropertiesButton, _fadeButton, _stayDurationButton, _completionActionButton, _documentSettingsButton, _batchPropertiesButton,
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
        _tree.AfterSelect += (_, _) => { UpdateButtonStates(); RefreshCardStyles(); };
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
        _emptyState = new Label
        {
            Text = "这个方案还没有环节\r\n点右上角「＋ 环节」开始编排流程",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = ModernUi.Muted,
            BackColor = Color.Transparent, Visible = false,
        };

        // --- 下一项 run controls (hosted by MainWindow's 监看 column) ---
        _runControls = new Panel { BackColor = Color.Transparent, Height = 128 };
        var nextCaption = new Badge("下一项");
        _nextName = new Label { ForeColor = ModernUi.Text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 9.5F) };
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
            if (ReferenceEquals(live, _lastLiveFile)) return;
            _lastLiveFile = live;
            RefreshCardStyles();
        };
        _liveTimer.Start();

        // Dock order: the last Top control added sits highest, so the header goes in last.
        Controls.Add(_activityRows);
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

    private void EnsureAtLeastOneScenario()
    {
        if (_store.Scenarios.Count > 0) return;

        var scenario = new Scenario { Name = "默认方案" };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        // First run, or a store that could not be read (the repository then refuses to save, protecting
        // the real file): the default scenario lives in memory until a save succeeds.
        try { _repository.Save(_store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Scenario? CurrentScenario =>
        _store.Scenarios.FirstOrDefault(s => s.Id == _store.CurrentScenarioId) ?? _store.Scenarios.FirstOrDefault();

    /// <summary>Saves an edit already applied to <see cref="_store"/>. On failure (file locked, no write
    /// access, or the store could not be read at startup) the operator is told, and the panel goes back to
    /// what is on disk. Every edit used to call Save bare: a failure reached only the crash log while the
    /// screen showed the change as done — a deleted activity came back after a restart (audit A-7).</summary>
    private bool TrySave()
    {
        try
        {
            _repository.Save(_store);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bool reverted = _repository.TryReloadInto(_store);
            EnsureAtLeastOneScenario();
            MessageBox.Show(this,
                reverted ? $"修改未能保存，已恢复为保存前的内容。\n{DataDirectory.Describe(ex)}" : $"修改未能保存，重启后将丢失。\n{ex.Message}",
                "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshScenarioCombo();
            RefreshTree();
            return false;
        }
    }

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
        if (!TrySave()) return;
        RefreshTree();
    }

    private void OnNewScenario()
    {
        string? name = TextInputDialog.Prompt(this, "新建方案", "方案名称：");
        if (name == null) return;

        var scenario = new Scenario { Name = name };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        if (!TrySave()) return;
        _fileOpLog.LogScenarioCreated(scenario.Id, scenario.Name);
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
        if (!TrySave()) return;
        _fileOpLog.LogScenarioCreated(clone.Id, clone.Name);
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
            MusicPlaylistId = source.MusicPlaylistId,
            MusicFadeOut = source.MusicFadeOut,
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
        if (!TrySave()) return;
        _fileOpLog.LogScenarioDeleted(current.Id, current.Name);
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
        if (!TrySave()) return;
        _fileOpLog.LogActivityCreated(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

    private void OnRenameActivity()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        string? name = TextInputDialog.Prompt(this, "重命名活动", "活动名称：", activity.Name);
        if (name == null) return;

        activity.Name = name;
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
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
        if (!TrySave()) return;
        _fileOpLog.LogActivityDeleted(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

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
        }
        RefreshCardStyles();
    }

    private void OnCardNodeDoubleClick(TreeNode node)
    {
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
        _activityRows.SuspendLayout();
        foreach (var row in _activityRows.Controls.OfType<ActivityRowView>()) row.FitToWidth(width);
        _activityRows.ResumeLayout();
        // The rows were resized while layout was suspended, and ResumeLayout alone left AutoScroll with
        // a stale range (no scrollbar although the rows overflowed) — lay out once more to recompute it.
        _activityRows.PerformLayout();
    }

    private TreeNode? FindNode(object tag)
    {
        foreach (TreeNode activityNode in _tree.Nodes)
        {
            if (activityNode.Tag == tag) return activityNode;
            foreach (TreeNode fileNode in activityNode.Nodes)
                if (fileNode.Tag == tag) return fileNode;
        }
        return null;
    }

    /// <summary>The engine whose current item is 「大屏播放中」: Program when bound, else Preview.</summary>
    private PlaybackEngine? LiveEngine => ProgramEngine?.Invoke() ?? _playback;

    /// <summary>Every runnable item of the current scenario in run order (background music is
    /// layered under the show and never becomes 「下一项」).</summary>
    private List<(Activity Activity, int Index)> RunOrder()
    {
        var order = new List<(Activity, int)>();
        if (CurrentScenario is not { } scenario) return order;
        foreach (var activity in scenario.Activities)
            for (int i = 0; i < activity.Files.Count; i++)
                if (!activity.Files[i].IsBackgroundAudio) order.Add((activity, i));
        return order;
    }

    private static int? FirstRunnable(Activity activity, int from)
    {
        for (int i = from; i < activity.Files.Count; i++)
            if (!activity.Files[i].IsBackgroundAudio) return i;
        return null;
    }

    /// <summary>Position of the live item in <see cref="RunOrder"/>, or -1.</summary>
    private int LivePosition(List<(Activity Activity, int Index)> order)
    {
        var live = LiveEngine is { State: not PlaybackChannelState.Idle } engine ? engine.CurrentFile : null;
        if (live == null) return -1;
        return order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], live));
    }

    /// <summary>「下一项」: the item after the live one; with nothing live, the selected item (or the
    /// first of the selected section), else the very first item.</summary>
    private int NextPosition(List<(Activity Activity, int Index)> order)
    {
        int live = LivePosition(order);
        if (live >= 0) return live + 1 < order.Count ? live + 1 : -1;
        if (_tree.SelectedNode?.Tag is MediaFile file)
        {
            int selected = order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], file));
            if (selected >= 0) return selected;
        }
        if (_tree.SelectedNode?.Tag is Activity activity)
        {
            int first = order.FindIndex(o => ReferenceEquals(o.Activity, activity));
            if (first >= 0) return first;
        }
        return order.Count > 0 ? 0 : -1;
    }

    RunRowState IActivityRowHost.StateOf(MediaFile file)
    {
        var order = RunOrder();
        int index = order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], file));
        if (index < 0) return RunRowState.Pending;
        int live = LivePosition(order);
        if (index == live) return RunRowState.Live;
        if (index == NextPosition(order)) return RunRowState.Next;
        return live >= 0 && index < live ? RunRowState.Done : RunRowState.Pending;
    }

    MediaFile? IActivityRowHost.PreviewFile => _playback is { State: not PlaybackChannelState.Idle } preview ? preview.CurrentFile : null;

    /// <summary>「下一项 ▶」 / space: put the next item on the big screen.</summary>
    private void GoNext()
    {
        var order = RunOrder();
        int next = NextPosition(order);
        if (next < 0) return;
        RunRequested?.Invoke(order[next].Activity, order[next].Index);
    }

    /// <summary>「◀ 上一项」: back to the item before the live one.</summary>
    private void GoPrevious()
    {
        var order = RunOrder();
        int live = LivePosition(order);
        if (live <= 0) return;
        RunRequested?.Invoke(order[live - 1].Activity, order[live - 1].Index);
    }

    /// <summary>「预览下一项」: load the next item in the Preview monitor only.</summary>
    private void PreviewNext()
    {
        var order = RunOrder();
        int next = NextPosition(order);
        if (next < 0 || _playback == null) return;
        _playback.RequestPlay(order[next].Activity, order[next].Index, PlaybackTrigger.ManualSkip);
    }

    private void UpdateRunControls()
    {
        var order = RunOrder();
        int next = NextPosition(order), live = LivePosition(order);
        var scenario = CurrentScenario;
        string NumberOf((Activity Activity, int Index) item) =>
            $"{(scenario?.Activities.IndexOf(item.Activity) ?? 0) + 1}.{item.Index + 1}";
        _nextName.Text = next < 0
            ? (live >= 0 ? "已是最后一项" : "流程里还没有可播放的文件")
            : $"{NumberOf(order[next])}  {Path.GetFileName(order[next].Activity.Files[order[next].Index].SourcePath)}";
        _nextName.ForeColor = next < 0 ? ModernUi.Muted : ModernUi.Text;
        _runNextButton.Enabled = next >= 0;
        _runPreviewButton.Enabled = next >= 0 && _playback != null;
        _runPreviousButton.Enabled = live > 0;
    }

    /// <summary>Space runs 「下一项」 while this page has focus (not while typing in a text box or the
    /// scenario list).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && ActiveControlIsNotTextInput())
        {
            GoNext();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);

        bool ActiveControlIsNotTextInput()
        {
            Control? active = ActiveControl;
            while (active is ContainerControl { ActiveControl: { } inner }) active = inner;
            return active is not (TextBoxBase or ComboBox);
        }
    }

    bool IActivityRowHost.IsSelected(TreeNode node) => ReferenceEquals(_tree.SelectedNode, node);
    bool IActivityRowHost.IsMultiSelected(TreeNode node) => _multiSelectedFileNodes.Contains(node);
    void IActivityRowHost.RowNodeClicked(TreeNode node) => OnCardNodeClick(node);
    void IActivityRowHost.RowNodeDoubleClicked(TreeNode node) => OnCardNodeDoubleClick(node);

    void IActivityRowHost.RowContextMenu(TreeNode node, Control source, Point location)
    {
        // Right-clicking one file of an existing Ctrl+click multi-selection keeps that selection, so
        // 统一设置属性... is reachable from the context menu as well as from 编辑 ▾.
        if (!_multiSelectedFileNodes.Contains(node)) OnCardNodeClick(node);
        _editMenu.Show(source, location);
    }

    void IActivityRowHost.ToggleCollapsed(Activity activity)
    {
        // Deferred: this runs inside the row's own MouseDown, and RefreshTree disposes that row.
        BeginInvoke(() =>
        {
            activity.IsCollapsed = !activity.IsCollapsed;
            if (!TrySave()) return;
            RefreshTree();
        });
    }

    Image? IActivityRowHost.ThumbnailFor(MediaFile file)
    {
        string key = $"{file.Kind}|{file.SourcePath}";
        if (_thumbnails.TryGetValue(key, out var cached)) return cached;
        if (_thumbnailsRequested.Add(key)) _ = LoadThumbnailAsync(file, key);
        return null;
    }

    private async Task LoadThumbnailAsync(MediaFile file, string key)
    {
        Image? thumbnail = null;
        try { thumbnail = await Task.Run(() => FilesPanel.BuildThumbnail(file)); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Activity thumbnail failed: {0}", ex); }
        if (thumbnail == null) return;
        if (IsDisposed) { thumbnail.Dispose(); return; }
        _thumbnails[key] = thumbnail;
        foreach (Control row in _activityRows.Controls) row.Invalidate();
    }

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
            bool active = e.Node.IsSelected || activity.Files.Any(file => ReferenceEquals(file, _playback?.CurrentFile));
            using var path = RoundedRect(row, 12);
            using var fill = new SolidBrush(active ? ModernUi.SurfaceRaised : ModernUi.Surface);
            using var border = new Pen(active ? ModernUi.Accent : ModernUi.Border, active ? 1.4F : 1F);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            int numberSize = 22;
            var number = new Rectangle(row.Left + 14, row.Top + 12, numberSize, numberSize);
            using var numberFill = new SolidBrush(active ? ModernUi.Accent : ModernUi.SurfaceRaised);
            g.FillEllipse(numberFill, number);
            // Disposed rather than left to the finalizer: this is a TreeView owner-draw callback, so
            // it runs once per node per repaint. An undisposed Font here leaks a GDI handle every
            // time the tree is hovered, scrolled or reactivated, and Font objects are far too small
            // to create the memory pressure that would trigger a collection - the per-process GDI
            // handle limit is reached long before the finalizer ever runs. On an unattended Terminal
            // that is a slow death rather than a crash anyone sees happen.
            using var numberFont = new Font(ModernUi.UiFontSemibold, 9F);
            TextRenderer.DrawText(g, (e.Node.Index + 1).ToString(), numberFont, number,
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using var titleFont = new Font(ModernUi.UiFontSemibold, 10F);
            TextRenderer.DrawText(g, activity.Name, titleFont,
                new Rectangle(number.Right + 12, row.Top + 8, row.Width - 190, 22), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string mode = activity.DefaultPlayMode == PlayMode.SequentialAuto ? "顺序自动" : "手动点选";
            TextRenderer.DrawText(g, $"{activity.Files.Count} 个文件 · {mode}", Font,
                new Rectangle(number.Right + 12, row.Top + 30, row.Width - 190, 18), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (activity.Files.Any(file => ReferenceEquals(file, _playback?.CurrentFile)))
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
            var chip = new Rectangle(row.Left + 28, row.Top + 10, Math.Min(360, row.Width - 56), 42);
            using var chipPath = RoundedRect(chip, 8);
            using var chipFill = new SolidBrush(selected ? ModernUi.Surface : ModernUi.SurfaceRaised);
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
            using var badgeFont = new Font(ModernUi.UiFontSemibold, badge.Length > 1 ? 6.5F : 9F);
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
            // Same per-repaint GDI handle leak as numberFont above.
            using var metaFont = new Font(ModernUi.UiFont, 8F);
            TextRenderer.DrawText(g, meta, metaFont,
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

    private void UpdateCountLabel()
    {
        var scenario = CurrentScenario;
        int sections = scenario?.Activities.Count ?? 0, items = scenario?.Activities.Sum(a => a.Files.Count) ?? 0;
        _activityCount.Text = $"{sections} 个环节 · {items} 项";
    }

    /// <summary>Call after the scenario store changes from outside this control.</summary>
    public void RefreshTree()
    {
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_playback != null) _playback.FileStarted -= OnFileStarted;
            _stateMachine.StateChanged -= OnStateChanged;
            _saveFeedbackTimer.Dispose();
            _liveTimer.Dispose();
            _editMenu.Dispose();
            _toolTip.Dispose();
            foreach (var image in _thumbnails.Values) image.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
    }
}
