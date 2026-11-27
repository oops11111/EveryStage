using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI.Panels;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §8's operator-facing main window: a 72px icon rail (文件 / 流程 / 设备 / 设置) on the
/// left, the selected page in the middle, and the collapsible 「监看」 column on the right
/// (<see cref="SharedPreviewDock"/>: Preview monitor, 投到大屏, what is on the extended display). The
/// output status sits in the title bar. Shown on whatever display the operator is actually sitting at —
/// this is NOT the <c>OverlayWindow</c>, which covers the bound extended display with actual content output.
///
/// The window has no fixed aspect ratio: extra width and height go to the page.
///
/// Also hosts <see cref="ToastStack"/> (PLANNING.md §11's "右下角Toast通知栈"), pinned on top of
/// whichever panel is currently showing — its only producer today is
/// <see cref="PlaybackEngine.PlaybackAbnormallyInterrupted"/>, see
/// <see cref="OnPlaybackAbnormallyInterrupted"/>.
/// </summary>
public sealed class MainWindow : GradientForm
{
    private readonly OutputStateMachine _stateMachine;
    private readonly SettingsStore _settingsStore;
    // Not readonly, unlike every other field this constructor sets once and never touches again — see
    // AttachPlaybackEngine's own doc comment for why: PLANNING.md §5's runtime monitor-hot-plug
    // binding (this project's README risk on it) needs to replace this from null to a real
    // PlaybackEngine well after this window is already constructed and showing.
    private PlaybackEngine? _playback;
    private readonly FileLibraryStore _library;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    private readonly FileOperationLogger _fileOpLog;
    private readonly Panel _contentHost;
    private readonly Panel _panelHost;
    private readonly SharedPreviewDock _previewDock;
    private readonly Panel _rail;
    private readonly MusicLibraryStore _musicStore;
    private readonly MusicChannel _music;
    /// <summary>DEV-18: the fade-to-black controller (its own curtain + dims). Built once; it
    /// reads the current program engine, music channel, screen bounds and duration through
    /// delegates, so engine swaps and setting changes need no re-wiring.</summary>
    private readonly FadeToBlackController _ftbController;
    private readonly MusicBar _musicBar;
    private readonly MusicDrawer _musicDrawer;
    private readonly Panel _monitorColumn;
    private readonly RoundedActionButton _expandMonitorButton;
    private readonly RoundedActionButton _collapseStopButton; // DEV-22: red stop entry, collapsed column only
    private readonly Label _stickyBadge; // DEV-10: sticky-notice count, collapsed column only
    private bool _monitorCollapsed;

    // Preview channel: always exists, renders into the dock's monitor, independent of the extended
    // display and the cast switch. _playback (above) is the Program channel.
    private readonly PlaybackEngine _preview;
    private readonly DocumentProgramSync _documentSync;
    private readonly VideoSurface? _previewVideoSurface;
    // The bottom 监看 window's own engine (the top one is _preview); the two windows work the same way.
    private readonly PlaybackEngine _preview2;
    private readonly VideoSurface? _preview2VideoSurface;
    private readonly ToastStack _toastStack;
    /// <summary>A Take decision awaiting the operator (DEV-06): dismissed by a full stop, so a
    /// choice made after stopping can never take anything back on air.</summary>
    private ToastNotification? _pendingTakeToast;
    private readonly Label _statusLabel;
    private readonly RoundedActionButton _emergencyStop;
    private readonly EmergencyStopHotkey _emergencyHotkey;
    /// <summary>A device is connected but held in the preview window only (not on Program). When
    /// the monitor column is collapsed the device card is invisible, so the title-bar status
    /// carries the fact instead (OP-29) — the column's expand button is the way back to it.</summary>
    private bool _deviceInPreview;

    /// <summary>No new decoded frame for over ~2 s while a device holds Program (OP-27).</summary>
    private bool _deviceSignalStalled;

    private MediaFile? _lastOnAirFile;

    /// <summary>Program output failed but the state machine still claims Active (OP-04/OP-09):
    /// a decode failure leaves the engine Failed, and a GPU device-removed leaves it stopped —
    /// in both cases the title-bar label must say "failed", not keep claiming the output is
    /// live. Set by <see cref="OnProgramPlaybackStateChanged"/> (Failed) and by
    /// <see cref="ReportProgramFailure"/> (GPU loss); cleared by <see cref="UpdateStatusLabel"/>
    /// whenever the machine is Idle or the engine is playing/paused again.</summary>
    private bool _programFailure;
    private bool _deviceFeedbackLive;
    /// <summary>DEV-15: the in-page first-run empty-state banner (see
    /// <see cref="RefreshFirstRunBanner"/>). Run-scoped dismiss: closing it hides it until the
    /// library / activities become non-empty and empty again (a library clear or a new config
    /// may bring it back). Never modal, never blocks F12/Esc.</summary>
    private Panel? _firstRunBanner;
    private Label? _firstRunBannerLabel;
    private bool _firstRunBannerDismissed;
    /// <summary>自检里打开的主窗口不能显示首启引导（没人点，测试会卡住）。Kill-switch for the
    /// DEV-15 empty-state banner; the tests set it false.</summary>
    internal bool FirstRunGuideEnabled { get; init; } = true;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly SettingsPanel _settingsPanel;

    /// <summary>The palette each page was last themed with. Only the visible page is in the tree when
    /// <see cref="ThemeManager.Apply"/> runs, so <see cref="ShowPanel"/> re-themes a page that missed
    /// a switch (or the saved theme at startup) from this palette.</summary>
    private readonly Dictionary<Control, UiPalette> _panelPalettes = new();

    /// <summary>A problem found at startup that the operator must fix. The toast carries a
    /// dismiss action, so it lives 10 s (see <see cref="ToastStack"/>) instead of vanishing
    /// after 3 s, and the operator can close it at once. No "open settings" action: none of
    /// the four call sites has an in-app destination (the settings one wants a newer version
    /// installed).</summary>
    internal void ShowStartupProblem(string message, params ToastAction[] actions)
    {
        var all = new List<ToastAction>(actions) { new ToastAction("知道了", () => { }) };
        _toastStack.Show(message, ToastSeverity.Critical, all.ToArray());
    }

    public void ShowPairingRequest(PairingRequest request) => _devicesPanel.ShowPairingRequest(request);

    /// <summary>Address / last contact / casting state of paired devices, for the 设备 page.</summary>
    public Func<Guid, (string? Address, DateTimeOffset? LastSeen, bool Casting)>? DevicePresence
    {
        set => _devicesPanel.Presence = value;
    }

    public void ClearPairingRequest(string requestId) => _devicesPanel.ClearPairingRequest(requestId);
    public Control DeviceCastVideoHost => _previewDock.DeviceVideoHost;
    /// <summary>设备页: a device was unpaired or lost 「允许投放」 (stop its cast now if it is casting).</summary>
    public event Action<Guid>? DeviceAccessRevoked;
    public event Action? DeviceRefreshRequested;
    /// <summary>The extended display's screen rectangle while it is on air (the PGM monitor copies it).</summary>
    public Func<Rectangle?>? ProgramScreenBounds
    {
        get => _previewDock.ProgramScreenBounds;
        set => _previewDock.ProgramScreenBounds = value;
    }

    public event Action? DeviceTakeRequested;
    public event Action? DeviceRevertRequested;
    public event Action<int, int>? DeviceVideoHostResized;

    public MainWindow(
        OutputStateMachine stateMachine, PlaybackEngine? playback, FileLibraryStore library,
        PairedDeviceStore pairedDevices, ScenarioStore scenarioStore, ScenarioRepository scenarioRepository,
        SettingsStore settingsStore, DeviceIdentity identity, DeviceConnectionLogger connectionLog,
        MusicLibraryStore? musicStore = null, FileOperationLogger? fileOperationLogger = null)
    {
        _stateMachine = stateMachine;
        _settingsStore = settingsStore;
        _playback = playback;
        _library = library;
        _scenarioStore = scenarioStore;
        _scenarioRepository = scenarioRepository;

        Text = "EveryStage Terminal";
        Icon = ProductIcon.LoadIcon();
        // Free aspect ratio: the page area grows with the window (the file grid gains columns, the run
        // sheet gains rows). FitToCurrentWorkingArea shrinks the default to small screens on show.
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(960, 600);
        BackColor = ModernUi.Background;
        Font = new Font(ModernUi.UiFont, 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        WindowsAppearance.UseDarkTitleBar(this);
        WindowsAppearance.UseRoundedCorners(this);
        // Keep the client area fully app-painted. Windows 10's legacy acrylic/blur accent policy
        // composites through transparent WinForms children as a flat gray surface (not the design
        // palette), so no DWM blur here.

        // 72px icon rail: 文件 / 流程 / 设备 / 设置. Output status lives in the title bar.
        var rail = new Panel { Dock = DockStyle.Left, BackColor = ModernUi.Rail };
        var filesButton = ModernUi.RailButton(NavIcon.Files, "文件");
        var activitiesButton = ModernUi.RailButton(NavIcon.Activities, "流程");
        var devicesButton = ModernUi.RailButton(NavIcon.Devices, "设备");
        var settingsButton = ModernUi.RailButton(NavIcon.Settings, "设置");
        // 快捷键速查 (L-04): not a page nav item — it lives at the rail's bottom and opens a dialog.
        var hotkeysButton = ModernUi.RailButton(NavIcon.Keyboard, "快捷键");
        var navButtons = new[] { filesButton, activitiesButton, devicesButton, settingsButton };
        rail.Controls.AddRange(navButtons);
        rail.Controls.Add(hotkeysButton);
        rail.Layout += (_, _) =>
        {
            int width = LogicalToDeviceUnits(60), height = LogicalToDeviceUnits(58), y = LogicalToDeviceUnits(12);
            foreach (var button in navButtons)
            {
                button.Bounds = new Rectangle((rail.ClientSize.Width - width) / 2, y, width, height);
                y += height + LogicalToDeviceUnits(6);
            }
            hotkeysButton.Bounds = new Rectangle((rail.ClientSize.Width - width) / 2,
                rail.ClientSize.Height - height - LogicalToDeviceUnits(12), width, height);
        };
        rail.Paint += (_, e) => DrawEdge(e.Graphics, rail, right: true);

        _statusLabel = ModernUi.SetForegroundRole(new Label
        {
            AutoSize = true, Font = new Font(ModernUi.UiFont, 9F),
            TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 8, 12, 0), BackColor = Color.Transparent,
        }, UiForegroundRole.Muted);

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(20, 16, 20, 12) };
        _panelHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _contentHost.Controls.Add(_panelHost);

        // 「监看」 right column: Preview monitor + transport, 投到大屏, Program status and controls.
        // Collapses to a thin strip so the page can use the full width.
        _monitorColumn = new Panel { Dock = DockStyle.Right, BackColor = ModernUi.Rail };
        _monitorColumn.Paint += (_, e) => DrawEdge(e.Graphics, _monitorColumn, right: false);
        _previewDock = new SharedPreviewDock(stateMachine) { Dock = DockStyle.Fill };
        _expandMonitorButton = new RoundedActionButton { Text = "‹", Dock = DockStyle.Top, Visible = false };
        ModernUi.StyleButton(_expandMonitorButton);
        new ToolTip().SetToolTip(_expandMonitorButton, "展开监看栏");
        _expandMonitorButton.Click += (_, _) => SetMonitorCollapsed(false);
        _previewDock.CollapseRequested += () => SetMonitorCollapsed(true);
        _stickyBadge = new Label
        {
            Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(26),
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
            Font = new Font(ModernUi.UiFontSemibold, 10F),
            Cursor = Cursors.Hand,
        };
        new ToolTip().SetToolTip(_stickyBadge, "有需要处理的消息，点一下展开监看栏");
        _stickyBadge.Click += (_, _) => SetMonitorCollapsed(false);
        // DEV-22: the collapsed column keeps its own red stop entry — the same action and the same
        // StopAvailable rule as the title-bar button, reachable without expanding the column first.
        _collapseStopButton = new RoundedActionButton
        {
            Text = "■", AccessibleName = "停止输出（急停 F12）", Dock = DockStyle.Top,
            Height = LogicalToDeviceUnits(34), Visible = false,
            BackColor = Color.FromArgb(180, 40, 48), ForeColor = Color.White,
        };
        ModernUi.StyleButton(_collapseStopButton, primary: true, danger: true);
        new ToolTip().SetToolTip(_collapseStopButton, "停止输出（急停 F12）");
        _collapseStopButton.Click += (_, _) => StopProgramOutput();
        _monitorColumn.Controls.Add(_previewDock);
        _monitorColumn.Controls.Add(_stickyBadge);
        _monitorColumn.Controls.Add(_collapseStopButton);
        _monitorColumn.Controls.Add(_expandMonitorButton);
        _previewVideoSurface = TryCreateVideoSurface(_previewDock.PreviewSurface);
        _previewDock.PreviewSurface.VideoHostResized += (w, h) =>
        {
            try { _previewVideoSurface?.Resize(w, h); } catch (Exception) { } // a lost device surfaces on the next present
        };
        _preview = new PlaybackEngine(PlaybackChannel.Preview, _previewDock.PreviewSurface, _previewVideoSurface,
            settingsStore, scenarioStore, stateMachine: null);
        _previewDock.BindPreview(_preview);
        _preview2VideoSurface = TryCreateVideoSurface(_previewDock.SecondSurface);
        _previewDock.SecondSurface.VideoHostResized += (w, h) =>
        {
            try { _preview2VideoSurface?.Resize(w, h); } catch (Exception) { }
        };
        _preview2 = new PlaybackEngine(PlaybackChannel.Preview, _previewDock.SecondSurface, _preview2VideoSurface,
            settingsStore, scenarioStore, stateMachine: null);
        _previewDock.BindSecondPreview(_preview2);
        // The title-bar stop button shares the dock's StopAvailable rule (OP-04): a preview-only
        // play or a music start makes stopping meaningful even while the state machine is Idle,
        // so the preview engines and the music channel must refresh the same label/button too —
        // not just state-machine transitions.
        _preview.StateChanged += OnPreviewEngineStateChanged;
        _preview2.StateChanged += OnPreviewEngineStateChanged;
        _previewDock.FileDropped += (window, file, path) =>
        {
            var dropped = file ?? (path != null && FileLibraryStore.InferKind(path) is { } kind ? new MediaFile { SourcePath = path, Kind = kind } : null);
            if (dropped == null) return;
            // A drop on the window following the big screen leaves the mirror alone: the file
            // previews in the other window instead. Ctrl held during the drop keeps the old
            // behavior (preview here, un-mirror). The drop event carries no key state, so the
            // modifier is read here, at drop time, while Ctrl is still held — keeping the
            // three-argument event untouched for its other raisers (including the self-tests).
            bool forceHere = Control.ModifierKeys.HasFlag(Keys.Control);
            if (_previewDock.ProgramWindow == window && !forceHere)
            {
                _toastStack?.Show("播出窗不动，已在预览窗预览。", ToastSeverity.Info);
                PlayInWindow(dropped, 1 - window);
                return;
            }
            // Dropping on the window that follows the big screen quietly un-mirrors it
            // (UseForLocalPlayback, inside PlayInWindow) — say so, so the operator is not
            // surprised when the big screen stops being mirrored in this window.
            if (_previewDock.ProgramWindow == window)
                _toastStack?.Show("已在该窗预览，大屏不再镜像此窗。", ToastSeverity.Info);
            PlayInWindow(dropped, window);
        };
        _previewDock.AttachProgram(playback);
        _previewDock.TakeRequested += style => TakePreviewToProgram(style);
        _previewDock.SwapRequested += SwapPreviewAndProgram;
        _previewDock.StopProgramRequested += StopProgramOutput;
        _previewDock.TogglePauseRequested += TogglePlayPause; // DEV-11: presenter-window Space
        _previewDock.DeviceTakeRequested += () => DeviceTakeRequested?.Invoke();
        _previewDock.DeviceRevertRequested += () => DeviceRevertRequested?.Invoke();
        _previewDock.ViewProgramRequested += ViewProgramFromDeviceCard;
        _previewDock.DeviceVideoHostResized += (w, h) => DeviceVideoHostResized?.Invoke(w, h);
        _previewDock.RemoveFromLibraryRequested += RemoveFailedPreviewFileFromLibrary;
        // PDF / Word / presentation controls and Preview → Program document sync (实时同步 / 冻结 / 更新大屏).
        _documentSync = new DocumentProgramSync(_preview, () => _playback);
        _previewDock.BindDocumentSync(_documentSync);
        // DEV-06: reload decisions ask on the sticky-notice surface, never a modal dialog.
        _previewDock.ConfirmAsync = (message, options) => ShowDecisionToastAsync(message, options);
        // Closing a Word document or presentation with unsaved edits asks here — never a WPS/Word dialog.
        // DEV-06: a sticky notice with 保存 / 丢弃, never a modal — the close waits for the answer,
        // and dismissing without choosing aborts the close (the manager keeps the session open).
        OfficeSessionManager.Shared.ConfirmSaveOnClose = session => AskSaveBeforeCloseAsync(session);

        // One shared instance rather than a separate `new FileOperationLogger()` per panel: both
        // panels' loggers ultimately append to the same physical file
        // (`file-operations/file-ops-{date}.log`, see `DailyRollingLogWriter`), and that class's own
        // append lock is per-instance — two independent instances writing concurrently would each
        // lock against themselves only, not each other, reopening a small chance of one write
        // failing with a sharing violation right as the other holds the file open. Sharing one
        // instance (and therefore one lock) removes that risk entirely rather than just accepting it.
        _fileOpLog = fileOperationLogger ?? new FileOperationLogger();

        _filesPanel = new FilesPanel(library, _fileOpLog, scenarioStore, scenarioRepository, _preview);
        _filesPanel.FilePlayRequested += file => PlayInWindow(file);
        _filesPanel.FileClicked += OnFileClicked;
        _filesPanel.TakeRequested += TakeFileToProgram;
        // The library's "停止预览" stops both preview engines — the two windows are the same,
        // and this matches StopProgramOutput's "both windows stop" rule. (The FL-05 fallback
        // re-mounts the idle splash in both windows afterwards.)
        _filesPanel.PlaybackStopRequested += () => { _preview.Stop(); _preview2.Stop(); };
        _filesPanel.StopProgramRequested += StopProgramOutput;
        _filesPanel.IsProgramLive = () => _stateMachine.State == OutputState.Active;
        _filesPanel.ProgramFile = () => _stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            ? _playback?.CurrentFile : null;
        _filesPanel.ProgramPlayback = () => _stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            ? _playback : null;

        // Background-music card under the 大屏 section: a second view of the file page's audio bar, so
        // the same track can be controlled from every page. Shown while the column has room for it.
        // Background music: its own channel and the mini bar along the bottom of every page.
        _musicStore = musicStore ?? new MusicLibraryStore();
        _music = new MusicChannel(_musicStore, _settingsStore)
        {
            // Give way while the big screen plays video with sound (or audio).
            ShouldDuck = () => _stateMachine.State == OutputState.Active && _playback is { State: PlaybackChannelState.Playing, Muted: false } program
                && (program.CurrentVideoHasAudio || program.CurrentFile?.Kind == MediaKind.Audio),
        };
        _previewDock.AdditionalStopCondition = () => _music.IsPlaying;
        _music.Changed += OnMusicChanged; // music start/stop changes StopAvailable (OP-04)
        // DEV-18: the FTB controller reads everything through delegates (the program engine
        // is swapped by AttachPlaybackEngine, the duration is a live setting), so it is built
        // once here. The dock button and Ctrl+B both funnel through ToggleFadeToBlack below.
        _ftbController = new FadeToBlackController(
            () => _playback,
            () => _music,
            () => ProgramScreenBounds?.Invoke(),
            () => _settingsStore.Current.FadeToBlackMilliseconds);
        _previewDock.FadeToBlackRequested += ToggleFadeToBlack;
        _previewDock.FadeToBlackActive = () => _ftbController.IsActive;
        _musicBar = new MusicBar(_music) { Dock = DockStyle.Bottom };
        _musicDrawer = new MusicDrawer(_music, _library) { Dock = DockStyle.Bottom };
        _musicBar.PlaylistsRequested += _musicDrawer.Toggle;
        _musicDrawer.OpenChanged += open => _musicBar.DrawerOpen = open;
        // Dock order (outermost first = highest index): the bar on the bottom edge, the drawer sliding up
        // from it, the page filling the rest.
        _contentHost.Controls.Add(_musicDrawer);
        _contentHost.Controls.Add(_musicBar);
        _contentHost.Controls.SetChildIndex(_panelHost, 0);
        _contentHost.Controls.SetChildIndex(_musicDrawer, 1);
        _contentHost.Controls.SetChildIndex(_musicBar, 2);
        _filesPanel.AudioDropped += paths => _musicDrawer.OpenAndAdd(paths);
        // Audio dropped on the music bar joins the selected playlist too.
        _musicBar.AllowDrop = true;
        _musicBar.DragEnter += (_, e) => e.Effect = MusicDrawer.AudioPaths(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        _musicBar.DragDrop += (_, e) => _musicDrawer.OpenAndAdd(MusicDrawer.AudioPaths(e.Data));

        _devicesPanel = new DevicesPanel(pairedDevices, connectionLog, identity.DeviceName, stateMachine, identity.IsLoadDegraded);
        _devicesPanel.DeviceAccessRevoked += id => DeviceAccessRevoked?.Invoke(id);
        _devicesPanel.RefreshRequested += () => DeviceRefreshRequested?.Invoke();
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _preview, _fileOpLog, stateMachine)
        {
            ProgramEngine = () => _playback,
            Playlists = () => _music.Library.Playlists,
        };
        _activitiesPanel.RunRequested += RunActivityItem;
        _activitiesPanel.ClickTakesToAir = () => _settingsStore.Current.ClickTakesToAir;
        _activitiesPanel.GoLoadsPreviewFirst = () => _settingsStore.Current.GoToPreviewFirst; // DEV-24(b)
        // DEV-15: library / activity changes re-check the empty-state banner.
        _filesPanel.ContentChanged = RefreshFirstRunBanner;
        _activitiesPanel.ContentChanged = RefreshFirstRunBanner;
        _filesPanel.ClickTakesToAir = () => _settingsStore.Current.ClickTakesToAir;
        _library.DefaultCompletion = settingsStore.Current.DefaultCompletionAction;
        _settingsPanel = new SettingsPanel(settingsStore, identity);
        foreach (Control page in new Control[] { _filesPanel, _devicesPanel, _activitiesPanel, _settingsPanel })
            _panelPalettes[page] = ModernUi.Palette;
        settingsStore.SettingsChanged += OnSettingsChanged;
        settingsStore.AudioOutputFallback += OnAudioOutputFallback;

        filesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, filesButton); ShowPanel(_filesPanel); };
        activitiesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, activitiesButton); ShowPanel(_activitiesPanel); };
        devicesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, devicesButton); ShowPanel(_devicesPanel); };
        settingsButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, settingsButton); ShowPanel(_settingsPanel); };
        // DEV-05: the 详情面板's "统一翻页过场设置..." entry jumps to the existing global
        // setting (same nav state + panel switch as the settings button, then the section
        // reveal inside the panel). No new setting, no dialog.
        _activitiesPanel.ShowTransitionSettingsRequested = () =>
        {
            ModernUi.SetNavActive(navButtons, settingsButton);
            ShowPanel(_settingsPanel);
            _settingsPanel.RevealSlideTransition();
        };
        hotkeysButton.Click += (_, _) => { using var dialog = new HotkeyListDialog(_settingsStore.Current); dialog.ShowDialog(this); };

        var titleBar = new AppTitleBar(this, "终端机");
        // Three stop buttons share one rule (SharedPreviewDock.StopAvailable): this one in the title
        // bar, reachable from every page, the dock's own button beside the monitors (U1-06), and the
        // collapsed column's red entry (DEV-22).
        _emergencyStop = new RoundedActionButton
        {
            Text = "■ 停止输出", AccessibleName = "停止输出（急停 F12）", Width = 140,
            BackColor = Color.FromArgb(180, 40, 48), ForeColor = Color.White,
        };
        ModernUi.StyleButton(_emergencyStop, primary: true, danger: true);
        _emergencyStop.Click += (_, _) => StopProgramOutput();
        titleBar.AddAction(_emergencyStop);
        titleBar.AddStatus(_statusLabel); // U1-01: the output status sits in the title bar

        // WinForms docks the back-most control first: the title bar takes the full top edge, then the
        // rail and the monitor column their sides, and the page area (front-most) fills the rest.
        Controls.Add(_contentHost);
        Controls.Add(_monitorColumn);
        Controls.Add(rail);
        Controls.Add(titleBar);
        _rail = rail;
        ApplyFrameMetrics();
        DpiChanged += (_, _) => ApplyFrameMetrics();

        // PLANNING.md §11 "异常提示"："右下角Toast通知栈" — added to Controls last (and pinned via
        // its own OnParentChanged/SizeChanged handling, see ToastStack's doc comment) so it renders
        // on top of _contentHost regardless of which panel is currently showing.
        // Event notices: a fixed three-slot area at the bottom of the 监看 column (第二十一轮).
        _toastStack = new ToastStack { Dock = DockStyle.Bottom };
        _settingsPanel.ShowToast = (message, severity, actions) => _toastStack.Show(message, severity, actions);
        _activitiesPanel.ShowToast = (message, severity, actions) => _toastStack.Show(message, severity, actions);
        _filesPanel.ShowToast = (message, severity, actions) => _toastStack.Show(message, severity, actions);
        _monitorColumn.Controls.Add(_toastStack);
        _monitorColumn.Controls.SetChildIndex(_previewDock, 0);
        _emergencyHotkey = new EmergencyStopHotkey(StopProgramOutput, message => _toastStack.Show(message, ToastSeverity.Critical));
        _emergencyHotkey.GlobalStateChanged += UpdateStatusLabel; // DEV-10: persistent status line
        _emergencyHotkey.Configure(settingsStore.Current.GlobalEmergencyStopEnabled, settingsStore.Current.HotkeyFor(HotkeyAction.EmergencyStopGlobal));
        _toastStack.NoticesChanged += UpdateStickyBadge; // DEV-10: collapsed-column count badge
        if (_playback != null) _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;

        // See PlaybackEngine.PlaybackDeclinedByCastSwitch's own doc comment (README risk #9): a
        // "点文件" click while the cast switch is off used to be a completely silent no-op —
        // OnPlaybackDeclinedByCastSwitch below answers it with a toast instead.
        if (_playback != null) _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;
        // A direct Take of a background-audio file is reinterpreted (overlay track, no screen
        // change — OP-05): the interception is only visible via this toast, same pattern as the
        // cast-switch decline above. The queue-walk skip itself needs no toast: the flow-page
        // row text already marks these files ("[背景音频叠加播放…]").
        if (_playback != null) _playback.BackgroundAudioTakeIntercepted += OnBackgroundAudioTakeIntercepted;
        if (_playback != null) _playback.FileStarted += OnProgramFileStarted;
        if (_playback != null) _playback.StateChanged += OnProgramPlaybackStateChanged;

        _stateMachine.StateChanged += OnStateChanged;
        UpdateStatusLabel();
        ShowPanel(_filesPanel);
        ApplyTheme(settingsStore.Current.Theme);
        ThemeManager.FollowSystemTheme(this, () => settingsStore.Current.Theme, () => ApplyTheme(settingsStore.Current.Theme));
        ModernUi.SetNavActive(navButtons, filesButton);

        // Terminal is meant to run unattended in the background (PLANNING.md's whole framing) —
        // closing this operator window shouldn't end the process, only hide it. Reopening it today
        // needs another way in (e.g. a tray menu item); see Program.cs for how it's actually shown.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // 首启空态引导：窗口第一次真正显示出来后再判断（构造时窗口还没就位）。
        Shown += (_, _) => RefreshFirstRunBanner();
    }

    /// <summary>DEV-15: the first-run empty state, in page and non-modal (the old MessageBox
    /// guide is gone). When — and only when — the library is empty and no scenario has any
    /// activity, a banner across the top of the hosted page carries the same three steps;
    /// it never shows while output is Active, never blocks F12/Esc, and never depends on
    /// the unimplemented show lock. The judgement only reads data this window already holds
    /// (<see cref="_library"/> and <see cref="_scenarioStore"/>), with no persisted
    /// "already seen" flag: once the operator imports a file or creates an activity the
    /// condition stops holding. Note the default scenario is the empty one ActivitiesPanel's
    /// <c>EnsureAtLeastOneScenario</c> builds (zero activities), so the judgement counts
    /// activities, not scenarios. Dismissing is run-scoped and forgotten as soon as content
    /// exists again, so the banner may reappear after a library clear / new config.</summary>
    private void RefreshFirstRunBanner()
    {
        if (InvokeRequired) { try { BeginInvoke(RefreshFirstRunBanner); } catch (InvalidOperationException) { } return; }
        bool empty = FirstRunGuideEnabled && _stateMachine.State != OutputState.Active
            && _library.Files.Count == 0 && !_scenarioStore.Scenarios.Any(scenario => scenario.Activities.Count > 0);
        if (!empty)
        {
            _firstRunBannerDismissed = false;
            if (_firstRunBanner != null) _firstRunBanner.Visible = false;
            return;
        }
        if (_firstRunBannerDismissed) return;
        _firstRunBanner ??= BuildFirstRunBanner();
        // Re-apply the roles: the banner survives panel switches (ShowPanel clears the host),
        // and a theme change while it is hidden must not leave it in stale colours.
        ModernUi.SetBackgroundRole(_firstRunBanner, UiBackgroundRole.SurfaceRaised);
        if (_firstRunBannerLabel != null) ModernUi.SetForegroundRole(_firstRunBannerLabel, UiForegroundRole.Text);
        if (!_panelHost.Controls.Contains(_firstRunBanner))
            _panelHost.Controls.Add(_firstRunBanner); // added after the Fill page: later additions dock first, so the Top banner claims the top edge
        _firstRunBanner.Visible = true;
        _firstRunBanner.BringToFront();
    }

    private Panel BuildFirstRunBanner()
    {
        var banner = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(12, 8, 12, 8) };
        ModernUi.SetBackgroundRole(banner, UiBackgroundRole.SurfaceRaised);
        var label = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            Text = "欢迎使用 EveryStage。文件库还是空的、也还没有活动，可以按三步开始：\n"
                + "1. 在「文件」页点「导入」，把图片、视频、文档等素材加入文件库；\n"
                + "2. 在「流程」页新建活动，把文件加入活动并排好播放顺序；\n"
                + "3. 选好扩展屏后即可播放，也可以等待投屏机连接投屏。",
        };
        ModernUi.SetForegroundRole(label, UiForegroundRole.Text);
        var dismiss = new Button { Text = "知道了", Dock = DockStyle.Right, Width = 76, Height = 30 };
        ModernUi.StyleButton(dismiss);
        dismiss.Click += (_, _) =>
        {
            _firstRunBannerDismissed = true;
            banner.Visible = false;
        };
        banner.Controls.Add(label);
        banner.Controls.Add(dismiss);
        _firstRunBannerLabel = label;
        return banner;
    }

    /// <summary>PLANNING.md §5's runtime monitor-hot-plug binding — called (at most once, from
    /// <c>TerminalApplicationContext.HandleDisplaySettingsChanged</c>) when a display shows up after
    /// this Terminal already started with none bound, well after this window and everything inside it
    /// was already constructed with <see cref="_playback"/> null. Sets the now-mutable
    /// <see cref="_playback"/> field and performs the exact same conditional event subscriptions this
    /// constructor already does for it (<see cref="PlaybackEngine.PlaybackAbnormallyInterrupted"/>/
    /// <see cref="PlaybackEngine.PlaybackDeclinedByCastSwitch"/>), now unconditionally since
    /// <paramref name="playback"/> is guaranteed non-null here — then propagates the same instance into
    /// <see cref="_activitiesPanel"/>/<see cref="_filesPanel"/>, the two child panels that independently
    /// captured their own <c>PlaybackEngine?</c> reference from this window's constructor and therefore
    /// need the exact same live update, not just this window's own field.
    ///
    /// <c>_filesPanel.FilePlayRequested += file => _playback?.RequestPlay(file);</c> above needs no
    /// equivalent fix-up: that lambda closes over the <see cref="_playback"/> FIELD (via the implicit
    /// <c>this</c> capture), so it already reads whatever the field currently holds on each invocation
    /// — this method updating the field is all that closure needs.
    ///
    /// Idempotent, and unsubscribes the previous engine first - the same guard
    /// FilesPanel.AttachPlaybackEngine already had and ActivitiesPanel has now. Program.cs only
    /// reaches this once today (OnDisplaySettingsChanged re-binds only while _overlay is still
    /// null, and _overlay stays non-null once bound), so a second call is currently unreachable;
    /// the guard is here so that relaxing that binding rule for monitor hot-swap cannot quietly
    /// turn every playback notification into two.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        if (ReferenceEquals(_playback, playback)) return;
        // DEV-18: reset while _playback still points at the old engine, so a dim left on it
        // cannot leak into the swapped-in engine (or survive into the next show).
        _ftbController.Reset();
        if (_playback != null)
        {
            _playback.PlaybackAbnormallyInterrupted -= OnPlaybackAbnormallyInterrupted;
            _playback.PlaybackDeclinedByCastSwitch -= OnPlaybackDeclinedByCastSwitch;
            _playback.BackgroundAudioTakeIntercepted -= OnBackgroundAudioTakeIntercepted;
            _playback.FileStarted -= OnProgramFileStarted;
            _playback.StateChanged -= OnProgramPlaybackStateChanged;
        }
        _playback = playback;
        _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;
        _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;
        _playback.BackgroundAudioTakeIntercepted += OnBackgroundAudioTakeIntercepted;
        _playback.FileStarted += OnProgramFileStarted;
        _playback.StateChanged += OnProgramPlaybackStateChanged;
        // Files/Activities/audio bar operate the Preview engine; only the dock needs to know Program.
        _previewDock.AttachProgram(playback);
        UpdateStatusLabel();
    }

    /// <summary>OP-06 "查看大屏": sync a window to the device-held Program for read-only viewing.
    /// The window's own playback stops if it had any — the operator asked for the mirror, and
    /// the toast says what was given up. Never touches Program itself: the device keeps it.</summary>
    private void ViewProgramFromDeviceCard()
    {
        if (_previewDock.ProgramWindow != null) return;
        int window = _previewDock.FreeWindow(forLocalPlayback: false);
        var engine = WindowEngine(window);
        bool stopped = engine.State is not PlaybackChannelState.Idle;
        _previewDock.SyncWindowToProgram(window);
        if (stopped)
            _toastStack.Show("本地预览已停，该窗口开始跟随大屏（只看，不抢占）。", ToastSeverity.Info);
    }

    public void UpdateDeviceCast(string? deviceName, bool connected, bool onProgram)
    {
        _previewDock.UpdateDeviceCast(deviceName, connected, onProgram);
        _deviceInPreview = connected && !onProgram;
        if (!connected) SetDeviceSignalStalled(false);
        // OP-06: a device taking Program with a window standing idle gets its PGM mirror at
        // once — an idle window loses nothing by following. Busy windows are left alone; the
        // device card's "查看大屏" summons the mirror explicitly instead.
        if (onProgram && _previewDock.ProgramWindow == null)
        {
            int free = _previewDock.FreeWindow(forLocalPlayback: false);
            if (WindowEngine(free).State == PlaybackChannelState.Idle) _previewDock.SyncWindowToProgram(free);
        }
        UpdateStatusLabel(); // the collapsed-column indicator (OP-29) depends on this
        if (connected && onProgram && !_deviceFeedbackLive)
        {
            _previewDock.FlashProgram(true);
            _toastStack.Show($"已上屏：{deviceName ?? "设备来投"}", ToastSeverity.Info);
        }
        _deviceFeedbackLive = connected && onProgram;
    }

    /// <summary>OP-27: no new decoded frame for over ~2 s while a device holds Program. Set from
    /// the cast liveness tick; drives the device card's "信号中断" and the title-bar suffix.</summary>
    internal void SetDeviceSignalStalled(bool stalled)
    {
        if (_deviceSignalStalled == stalled) return;
        _deviceSignalStalled = stalled;
        _previewDock.SetDeviceSignalStalled(stalled);
        UpdateStatusLabel();
    }

    /// <summary>Shows a toast on the monitor column's stack, for collaborators that own no
    /// toast surface of their own (the device-cast controller's restore retry, the presenter
    /// window's on-air guard). Must be called on the UI thread.</summary>
    internal void ShowToast(string message, ToastSeverity severity, params ToastAction[] actions) =>
        _toastStack.Show(message, severity, actions);

    /// <summary>A decision notice (DEV-06): stays until the operator chooses or dismisses it, and is
    /// never evicted — the returned notice's <c>Dismissed</c> event resolves the pending decision.</summary>
    internal ToastNotification ShowStickyToast(string message, ToastSeverity severity, params ToastAction[] actions) =>
        _toastStack.ShowSticky(message, severity, actions);

    /// <summary>Save / discard decision for a closing Office session (DEV-06): a sticky notice, so
    /// the close waits without blocking F12. Choosing resolves true/false; dismissing without
    /// choosing resolves null (the manager aborts the close and keeps the session).</summary>
    private async Task<bool?> AskSaveBeforeCloseAsync(OfficeSession session)
    {
        int? choice = await ShowDecisionToastAsync(
            $"“{Path.GetFileName(session.SourcePath)}”有未保存的修改，关闭前是否保存？", ["保存", "丢弃"]).ConfigureAwait(true);
        return choice switch { 0 => true, 1 => false, _ => null };
    }

    /// <summary>A decision on the sticky-notice surface (DEV-06): one action per option, staying
    /// until the operator chooses; dismissing without choosing resolves null (never a default
    /// choice). The notice may be requested from a background thread, so it is marshaled to the
    /// UI thread.</summary>
    private Task<int?> ShowDecisionToastAsync(string message, string[] options)
    {
        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            var actions = options.Select((label, i) => new ToastAction(label, () => completion.TrySetResult(i))).ToArray();
            var toast = ShowStickyToast(message, ToastSeverity.Warning, actions);
            toast.Dismissed += () => completion.TrySetResult(null);
        }
        if (InvokeRequired) BeginInvoke(new Action(Show));
        else Show();
        return completion.Task;
    }

    /// <summary>Starts the playlist marked 「启动后自动播放」 (called once by the application at startup).</summary>
    public void StartBackgroundMusic() => _music.AutoPlayOnStartup();

    /// <summary>Flat page background (the design has no gradient behind the pages).</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new SolidBrush(ModernUi.Background);
        e.Graphics.FillRectangle(background, ClientRectangle);
    }

    /// <summary>Rail and monitor-column widths in device pixels for the current DPI.</summary>
    private void ApplyFrameMetrics()
    {
        _rail.Width = LogicalToDeviceUnits(72);
        _monitorColumn.Width = LogicalToDeviceUnits(_monitorCollapsed ? 36 : 360);
        _monitorColumn.Padding = _monitorCollapsed ? new Padding(LogicalToDeviceUnits(4), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4), 0) : Padding.Empty;
        _expandMonitorButton.Height = LogicalToDeviceUnits(30);
        if (_emergencyStop != null) _emergencyStop.Width = Math.Max(LogicalToDeviceUnits(72), _emergencyStop.PreferredContentWidth());
        if (_toastStack != null) PlaceToastStack();
    }

    /// <summary>Notices live at the bottom of the 监看 column; while it is collapsed to its strip they float
    /// over the window's bottom-right corner instead.</summary>
    private void PlaceToastStack()
    {
        Control parent = _monitorCollapsed ? this : _monitorColumn;
        if (!ReferenceEquals(_toastStack.Parent, parent))
        {
            _toastStack.Parent?.Controls.Remove(_toastStack);
            parent.Controls.Add(_toastStack);
            if (!_monitorCollapsed) _monitorColumn.Controls.SetChildIndex(_previewDock, 0);
        }
        _toastStack.Floating = _monitorCollapsed;
        _toastStack.RightInset = _monitorColumn.Width;
        _toastStack.BottomInset = _musicBar?.Height ?? 0; // never over the 背景音乐 bar
    }

    private void SetMonitorCollapsed(bool collapsed)
    {
        if (_monitorCollapsed == collapsed) return;
        _monitorCollapsed = collapsed;
        _previewDock.Visible = !collapsed;
        _expandMonitorButton.Visible = collapsed;
        _collapseStopButton.Visible = collapsed;
        UpdateStickyBadge();
        ApplyFrameMetrics();
        UpdateStatusLabel(); // the collapsed-column device indicator (OP-29) depends on this
    }

    /// <summary>DEV-10: the collapsed column keeps a count of the sticky notices (Critical and
    /// decision toasts) that never evict — visible only while the column is collapsed.</summary>
    private void UpdateStickyBadge()
    {
        if (InvokeRequired) { BeginInvoke(UpdateStickyBadge); return; }
        int count = _toastStack.StickyCount;
        _stickyBadge.Visible = _monitorCollapsed && count > 0;
        _stickyBadge.Text = $"⚠ {count}";
        ModernUi.SetForegroundRole(_stickyBadge, UiForegroundRole.Danger);
    }

    /// <summary>The 1px divider between the rail / monitor column and the page area.</summary>
    private static void DrawEdge(Graphics g, Control control, bool right)
    {
        using var pen = new Pen(ModernUi.Palette.IsHighContrast ? ModernUi.Border : ModernUi.Mix(ModernUi.Rail, ModernUi.Border, 0.45f));
        int x = right ? control.ClientSize.Width - 1 : 0;
        g.DrawLine(pen, x, 0, x, control.ClientSize.Height);
    }

    private void ShowPanel(Control panel)
    {
        _panelHost.Controls.Clear();
        panel.Dock = DockStyle.Fill;
        _panelHost.Controls.Add(panel);
        if (_panelPalettes.TryGetValue(panel, out var themedWith)) ThemeManager.Reapply(panel, themedWith);
        _panelPalettes[panel] = ModernUi.Palette;

        // FL-03: the 流程 page's 「下一项」 controls live at the bottom of that page itself now,
        // so the monitor column no longer borrows them (and the monitors keep their full height).
        // SharedPreviewDock.SetRunControls stays as API — the self-tests use it — it just has
        // no production caller any more.
        if (panel == _filesPanel) _filesPanel.Refresh_();
        else if (panel == _devicesPanel) _devicesPanel.Refresh_();
        else if (panel == _activitiesPanel) _activitiesPanel.RefreshTree();
        else if (panel == _settingsPanel) _settingsPanel.Refresh_();
        // DEV-15: ShowPanel cleared the host, so the banner (if due) must be re-added above
        // the newly hosted page. The panel refreshes above may already have fired
        // ContentChanged; this call is idempotent.
        RefreshFirstRunBanner();
    }

    /// <summary>Applies a theme to the window and records it for whichever page is hosted right now;
    /// the other pages catch up in <see cref="ShowPanel"/>.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        ThemeManager.Apply(this, theme);
        // Safety colours are stable across themes; they do not follow the old-colour remapping.
        _emergencyStop.BackColor = ModernUi.Palette.DangerFill;
        _emergencyStop.ForeColor = ModernUi.Palette.DangerFillForeground;
        _collapseStopButton.BackColor = ModernUi.Palette.DangerFill;
        _collapseStopButton.ForeColor = ModernUi.Palette.DangerFillForeground;
        foreach (Control hosted in _panelHost.Controls) _panelPalettes[hosted] = ModernUi.Palette;
    }

    private void OnStateChanged(OutputState state)
    {
        // Program on air (local media or a device cast) → Preview goes silent so monitor audio can
        // never leak into the venue; it comes back when Program stops.
        _preview.Muted = _preview2.Muted = state == OutputState.Active;
        UpdateStatusLabel();
        // DEV-18: leaving Active resets the FTB state (a disconnect mid-fade must not leave a
        // dim or a curtain behind for the next show). Entering Active needs no action.
        if (state != OutputState.Active) _ftbController.Reset();
        // DEV-15: output stopping may make the empty state showable again (the banner
        // never shows while output is Active — RefreshFirstRunBanner hides it then).
        RefreshFirstRunBanner();
    }

    /// <summary>Double-click / 播放·预览 / 在本地预览: load and play in the Preview monitor. Always internal —
    /// no extended display or cast switch involved, and never Windows' default application (that is
    /// only the explicit "使用系统默认程序打开" command, see <see cref="ExternalOpener"/>).</summary>
    /// <summary>A click on a file: 预览 (PVW), or straight to the big screen with 设置 「单击即上屏」.</summary>
    private void OnFileClicked(MediaFile file)
    {
        if (_settingsStore.Current.ClickTakesToAir) CastFile(file);
        else PlayInWindow(file);
    }

    private PlaybackEngine WindowEngine(int window) => window == 0 ? _preview : _preview2;

    /// <summary>Plays <paramref name="file"/> locally in a 监看 window: <paramref name="window"/>, or the
    /// first free one (both in use: the one not following the big screen, else the top one).</summary>
    private void PlayInWindow(MediaFile file, int? window = null)
    {
        int target = window ?? _previewDock.FreeWindow(forLocalPlayback: true);
        _previewDock.UseForLocalPlayback(target);
        WindowEngine(target).RequestPlay(file, PlaybackTrigger.ManualSkip);
    }

    /// <summary>The 投屏 click (单击 or 双击, 设置 · 投屏键): straight onto the big screen; the window that
    /// follows the big screen keeps following it, else the first free window starts to, else the top one.
    /// With only the main display it plays in a window instead.</summary>
    private async void CastFile(MediaFile file)
    {
        await CastSnapshotAsync(new PlaybackEngine.Snapshot(file, null, -1, TimeSpan.Zero, false),
            engine => engine.RequestPlay(file, PlaybackTrigger.ManualSkip));
    }

    private async Task<bool> CastSnapshotAsync(PlaybackEngine.Snapshot snapshot, Action<PlaybackEngine> playLocally)
    {
        if (_playback == null)
        {
            int local = _previewDock.FreeWindow(forLocalPlayback: true);
            _previewDock.UseForLocalPlayback(local);
            playLocally(WindowEngine(local));
            return false;
        }
        int target = _previewDock.ProgramWindow ?? _previewDock.FreeWindow(forLocalPlayback: false);
        if (await TakeSnapshotAsync(snapshot) != true) return false;
        _previewDock.SyncWindowToProgram(target);
        return true;
    }

    private static VideoSurface? TryCreateVideoSurface(PreviewSurface surface)
    {
        try
        {
            return new VideoSurface(surface.VideoHost.Handle, Math.Max(1, surface.VideoHost.ClientSize.Width), Math.Max(1, surface.VideoHost.ClientSize.Height));
        }
        catch (Exception)
        {
            // No usable D3D11 device: images/documents/audio still preview; video shows an error card.
            return null;
        }
    }

    /// <summary>Double-click / Enter on a file (B1: straight to the big screen): Program plays it from
    /// the start, and the window that follows the big screen keeps following it (or the first free
    /// window starts to — see <see cref="CastFile"/>). Nothing is paused: the old "pause Preview
    /// in the same step" behaviour died with the <see cref="PausePreview"/> calls (OP-24), and the
    /// operator avoids hearing/watching it twice only because the following window's own playback
    /// is stopped by the sync, not by a pause. Only the dock's 「投到大屏」 continues from the
    /// Preview position.</summary>
    private void TakeFileToProgram(MediaFile file)
    {
        // Double-click: the 投屏 key unless 设置 made it the single click (then the click already cast it).
        if (!_settingsStore.Current.ClickTakesToAir) CastFile(file);
    }

    /// <summary>Pause Preview right after something went on air (see <see cref="TakeFileToProgram"/>).
    /// CURRENTLY UNCALLED (OP-24): the dual-monitor rework dropped the three calls that used to
    /// follow TakeSnapshot, and the take paths now rely on SyncWindowToProgram stopping the
    /// following window's engine instead — no preview is paused after a take today. The method
    /// is kept (re-pointed, not deleted) so the pause-after-take behaviour can come back on the
    /// right window if that is ever decided; do not re-add the calls without re-checking which
    /// window holds the taken file, because SyncWindowToProgram flips the active window to the
    /// other one. Pauses the active window's engine; if it has nothing pausable, nothing is
    /// paused: the other window's local playback is unrelated content and is left alone.</summary>
    private void PausePreview()
    {
        var engine = WindowEngine(_previewDock.ActiveWindow);
        if (engine.State is not PlaybackChannelState.Loading)
        {
            if (engine.CanPause && !engine.IsPaused) engine.Pause();
            return;
        }
        // Still loading: pause as soon as it starts — unless Preview has moved on to something else.
        var file = engine.CurrentFile;
        void OnState(PlaybackChannelState state)
        {
            if (InvokeRequired) { BeginInvoke(() => OnState(state)); return; }
            if (state == PlaybackChannelState.Loading) return;
            engine.StateChanged -= OnState;
            if (state == PlaybackChannelState.Playing && ReferenceEquals(engine.CurrentFile, file) && engine.CanPause) engine.Pause();
        }
        engine.StateChanged += OnState;
    }

    /// <summary>流程 「下一项 ▶」 / double-click / space: put this item on the big screen, continuing the
    /// section from there. Without an extended display it plays in Preview instead.</summary>
    private async void RunActivityItem(Activity activity, int index)
    {
        if (index < 0 || index >= activity.Files.Count) return;
        bool cast = await CastSnapshotAsync(new PlaybackEngine.Snapshot(activity.Files[index], activity, index, TimeSpan.Zero, false),
            engine => engine.RequestPlay(activity, index, PlaybackTrigger.ManualSkip));
        if (!cast && _playback == null) EnterSection(activity);
    }

    private Activity? _liveSection;

    /// <summary>The show entered <paramref name="activity"/> (big screen started one of its items, by
    /// 下一项 or by auto-advance): apply the section's background-music cue once.</summary>
    private void EnterSection(Activity? activity)
    {
        if (ReferenceEquals(activity, _liveSection)) return;
        _liveSection = activity;
        if (activity == null) return;
        if (activity.MusicFadeOut) _music.FadeOutStop();
        else if (activity.MusicPlaylistId is { } id && _music.Library.Playlists.FirstOrDefault(p => p.Id == id) is { Tracks.Count: > 0 } playlist
                 && !(_music.IsPlaying && ReferenceEquals(_music.Playlist, playlist)))
            _music.Play(playlist);
    }

    private void OnProgramFileStarted(MediaFile file)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProgramFileStarted(file)); return; }
        EnterSection(_playback?.CurrentActivity);
        NotifyProgramReady();
        // DEV-03: the program engine advanced (Take, auto-advance) with no state change —
        // an unlocked NEXT item may just have gone on air; re-lock the detail panel.
        _activitiesPanel.RefreshDetailProtection();
    }

    private void OnProgramPlaybackStateChanged(PlaybackChannelState state)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProgramPlaybackStateChanged(state)); return; }
        if (state is PlaybackChannelState.Loading or PlaybackChannelState.Idle or PlaybackChannelState.Failed)
            _lastOnAirFile = null;
        UpdateStatusLabel(); // a failure must flip the title-bar label the moment it happens (OP-04)
        NotifyProgramReady();
    }

    private void NotifyProgramReady()
    {
        if (_stateMachine.State != OutputState.Active || _stateMachine.Source != ProgramSource.LocalMedia
            || _playback?.State is not (PlaybackChannelState.Playing or PlaybackChannelState.Paused)
            || _playback.CurrentFile is not { } file || ReferenceEquals(file, _lastOnAirFile)) return;
        _lastOnAirFile = file;
        _filesPanel.FlashOnAir(file);
        _previewDock.FlashProgram(false);
        _toastStack.Show($"已上屏：{Path.GetFileName(file.SourcePath)}", ToastSeverity.Info);
    }

    /// <summary>"投到大屏" in the dock: continue the Preview content (file, activity position and play
    /// position) on Program. The taken-from window then FOLLOWS the big screen — its own playback
    /// is stopped by the sync and it shows the Program mirror (OP-24: it does not "hold Preview
    /// on that frame"; re-taking from it is refused by the Take guard). The other window keeps
    /// whatever it was playing.</summary>
    /// <param name="style">切换 / 淡入淡出 from the dock; null (Ctrl+Enter) uses the item's or 设置's transition.</param>
    private async void TakePreviewToProgram(TransitionStyle? style = null)
    {
        int window = _previewDock.ActiveWindow;
        var engine = WindowEngine(window);
        // Four distinct refusals, four distinct messages (OP-18): the old single toast
        // ("本地窗口里没有可以投到大屏的内容") lied for three of them — a window following the
        // big screen, one still loading, and one that failed are not "no content".
        if (_previewDock.ProgramWindow == window)
        {
            _toastStack.Show("这个窗口正在跟随大屏，内容已在大屏上。", ToastSeverity.Warning);
            return;
        }
        if (engine.State == PlaybackChannelState.Loading)
        {
            _toastStack.Show("本地窗口正在加载，请稍候再投。", ToastSeverity.Warning);
            return;
        }
        if (engine.State == PlaybackChannelState.Failed)
        {
            // OP-18: name the failure, not just the state — the engine keeps the reason.
            string reason = engine.LastError?.Message is { Length: > 0 } message
                ? $"加载失败：{message}，请先重试或换一个文件。"
                : "本地窗口播放失败，请先重试或换一个文件。";
            _toastStack.Show(reason, ToastSeverity.Warning);
            return;
        }
        if (engine.CaptureSnapshot() is not { } snapshot)
        {
            _toastStack.Show("本地窗口里没有可以投到大屏的内容。", ToastSeverity.Warning);
            return;
        }
        if (_playback != null) _playback.PendingTransition = style;
        // The window taken from now follows the big screen (its own playback stops: no double audio).
        // DEV-06: null means another Take decision is already pending — leave PendingTransition
        // alone, the pending decision owns the take.
        bool? took = await TakeSnapshotAsync(snapshot);
        if (took == true) _previewDock.SyncWindowToProgram(window);
        else if (took == false && _playback != null) _playback.PendingTransition = null;
    }

    /// <summary>「⇄ 对调」: the active window's content goes on air with a fade, and what was
    /// on air comes back into the window that followed the big screen, forced paused (OP-01)
    /// — it does NOT keep playing, so pressing it again swaps back. With nothing on air
    /// there is nothing to swap back: the Take still happens and a toast says so (OP-28).</summary>
    private async void SwapPreviewAndProgram()
    {
        var onAir = _stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            ? _playback?.CaptureSnapshot() : null;
        int window = _previewDock.ActiveWindow;
        int? programWindow = _previewDock.ProgramWindow;
        var engine = WindowEngine(window);
        if (programWindow == window || engine.State is PlaybackChannelState.Loading or PlaybackChannelState.Failed
            || engine.CaptureSnapshot() is not { } next)
        {
            _toastStack.Show("本地窗口里没有可以对调的内容。", ToastSeverity.Warning);
            return;
        }
        if (_playback != null) _playback.PendingTransition = TransitionStyle.Dissolve;
        // DEV-06: null means another Take decision is already pending — leave PendingTransition
        // alone, the pending decision owns the take.
        bool? took = await TakeSnapshotAsync(next);
        if (took != true)
        {
            if (took == false && _playback != null) _playback.PendingTransition = null;
            return;
        }
        _previewDock.SyncWindowToProgram(window);
        // What was on air comes back, paused, into the window that followed the big screen.
        // The button greys out when there is nothing on air (OP-28), but if Swap is invoked
        // anyway, say plainly that this was a Take, not a swap — never silently degenerate.
        if (onAir == null)
        {
            _toastStack.Show("大屏上没有可换回的内容，这次只投不换。", ToastSeverity.Warning);
            return;
        }
        if (programWindow is not { } back)
        {
            _toastStack.Show("没有播出窗，换回的内容无处可放。", ToastSeverity.Warning);
            return;
        }
        _previewDock.UseForLocalPlayback(back);
        WindowEngine(back).RequestPlay(onAir.Value with { Paused = true }, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Puts a Preview snapshot on Program. DEV-06: the two confirmations (a device cast is
    /// on air; the cast switch is off) ask on the sticky-notice surface instead of a modal dialog —
    /// the take waits for the operator, the current picture stays, and F12 keeps working throughout.
    /// Returns true when the snapshot was accepted for Program, false when refused/cancelled/
    /// dismissed, and null when another Take decision is already pending (the caller must then do
    /// nothing — the pending decision owns the take, including any pending transition).</summary>
    private async Task<bool?> TakeSnapshotAsync(PlaybackEngine.Snapshot snapshot)
    {
        if (_playback == null)
        {
            _toastStack.Show("未连接扩展屏，无法投到屏幕。连接扩展显示器后即可投出；本地预览不受影响。", ToastSeverity.Warning);
            return false;
        }
        if (_stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.DeviceCast)
        {
            if (_pendingTakeToast != null)
            {
                _toastStack.Show("已有待确认的投屏决定，请先处理。", ToastSeverity.Warning);
                return null;
            }
            int? choice = await AskTakeDecisionAsync(
                "扩展屏正在输出设备来投的画面。\n切换为本地内容后，设备画面将退回设备投屏窗口。是否继续？");
            if (choice != 0) return false;
        }
        if (!_stateMachine.CastSwitchOn)
        {
            if (_pendingTakeToast != null)
            {
                _toastStack.Show("已有待确认的投屏决定，请先处理。", ToastSeverity.Warning);
                return null;
            }
            int? choice = await AskTakeDecisionAsync(
                "投屏开关已关闭（可在托盘菜单中切换）。\n是否开启投屏开关并投到屏幕？");
            if (choice != 0) return false;
            _stateMachine.SetCastSwitch(true);
        }
        // Re-taking the file already on air would silently restart it from its start position
        // (OP-16): refuse with a toast. A failed or still-loading file is NOT refused — the
        // operator may be retrying it. A paused on-air file is refused too: resuming it is
        // Space's job, and a Take would restart it.
        if (_stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            && _playback.State is PlaybackChannelState.Playing or PlaybackChannelState.Paused
            && ReferenceEquals(_playback.CurrentFile, snapshot.File))
        {
            _toastStack.Show("已在播出", ToastSeverity.Warning);
            return false;
        }
        return _playback.RequestPlay(snapshot);
    }

    /// <summary>A Take decision on the sticky-notice surface (DEV-06): 继续 / 取消, staying until
    /// the operator chooses; dismissing without choosing cancels the take.</summary>
    private Task<int?> AskTakeDecisionAsync(string message)
    {
        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toast = ShowStickyToast(message, ToastSeverity.Warning,
            new ToastAction("继续", () => completion.TrySetResult(0)),
            new ToastAction("取消", () => completion.TrySetResult(1)));
        toast.Dismissed += () =>
        {
            if (ReferenceEquals(_pendingTakeToast, toast)) _pendingTakeToast = null;
            completion.TrySetResult(null);
        };
        _pendingTakeToast = toast;
        return completion.Task;
    }

    /// <summary>"停止输出": take the extended display off air. Both preview windows stop as well.</summary>
    /// <summary>急停 from outside the window (HTTP 外控): the same as 「■ 停止输出」.</summary>
    internal void EmergencyStop() => StopProgramOutput();

    /// <summary>DEV-18: FTB — the single choke point (dock 黑场 button + Ctrl+B). Only while local
    /// media is on air: a device cast owns the program picture, and with nothing on air there
    /// is nothing to black out. Never a global hotkey, never a stop: the output stays
    /// connected, and F12 still stops at once (StopProgramOutput resets the controller first).</summary>
    private void ToggleFadeToBlack()
    {
        if (_stateMachine.State != OutputState.Active || _stateMachine.Source != ProgramSource.LocalMedia || _playback == null) return;
        _ftbController.Toggle();
    }

    private void StopProgramOutput()
    {
        // DEV-18: F12 / 停止输出 never waits for a fade — the FTB state resets first, synchronously,
        // so the curtain is down and both dims are full before anything else stops.
        _ftbController.Reset();
        // DEV-06: a pending Take decision dies with the stop — dismissing its notice resolves it
        // as cancelled, so a choice made after stopping can never take anything back on air.
        _pendingTakeToast?.Dismiss();
        _pendingTakeToast = null;
        _stateMachine.Disconnect();
        _playback?.Stop();
        _preview.Stop();
        _preview2.Stop();
        _music.Stop();
        // DEV-01: full stop only — release the parked slideshow session AFTER the engines stop, so a
        // parked session can never be adopted again after the operator has stopped everything. The
        // park slot is singular in the shared session manager (not per engine), and releasing it is
        // idempotent. This must not live inside PlaybackEngine.Stop(): an ordinary stop or file
        // change parks the session on purpose (PR #14 warm re-Take), and releasing there would
        // cold-start it.
        OfficeSessionManager.Shared.ReleaseParkedSession();
        _liveSection = null;
    }

    /// <summary>设置 · 热键 inside the main window. The global 急停 key is handled by
    /// <see cref="_emergencyHotkey"/> (also inside dialogs). 急停 (本程序内, Esc) first closes an open
    /// 歌单 drawer — unless a text box has focus, in which case Esc is left to the text box
    /// (FP-02: the Files search box clears its text); plain-letter and Ctrl+arrow keys are
    /// left to a text box that has focus.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var settings = _settingsStore.Current;
        if (keyData == settings.HotkeyFor(HotkeyAction.EmergencyStopLocal))
        {
            // FP-02: focus inside a text box means the operator is editing, not stopping the
            // show — let the key reach the box (the Files search box clears on Esc) instead of
            // eating it here. The drawer is not closed and output is not stopped either: the
            // stop buttons and the other keys still stop output. Falling through (rather than
            // clearing here) is the same mechanism the typing exemption below already relies
            // on for plain-letter keys, and the global-key message filter only watches the
            // global key, so it cannot defeat this exemption.
            if (FocusIsInTextBox(this)) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Escape && _musicDrawer.IsOpen) _musicDrawer.Close();
            else StopProgramOutput();
            return true;
        }
        // One typing判定 for every hotkey below (OP-20): FocusIsInTextBox walks the real focus
        // chain, including text boxes nested in plain wrappers. An editing key while typing
        // belongs to the control: plain/shift keys, any arrow/home/end navigation, and Enter in
        // any modifier combination (Ctrl+Enter is a text-editing key in a multiline box, not a
        // Take — it used to fire Take from inside the search box).
        bool typing = FocusIsInTextBox(this);
        bool editingKey = (keyData & Keys.Modifiers) is Keys.None or Keys.Shift
            || (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
            || (keyData & Keys.KeyCode) == Keys.Enter;
        if (!(typing && editingKey))
        {
            if (keyData == settings.HotkeyFor(HotkeyAction.Take)) { TakePreviewToProgram(); return true; }
            if (keyData == settings.HotkeyFor(HotkeyAction.PauseProgram)) { TogglePlayPause(); return true; }
            if (keyData == settings.HotkeyFor(HotkeyAction.NextItem)) { _activitiesPanel.RunNext(); return true; }
            if (keyData == settings.HotkeyFor(HotkeyAction.PreviousItem)) { _activitiesPanel.RunPrevious(); return true; }
            // DEV-18: FTB (default Ctrl+B, rebindable, app-only). Inactive while typing — Ctrl+B
            // is Bold in a text box, and the box must keep it (the typing+editingKey exemption
            // above does not cover Ctrl+letter keys, so the !typing gate is explicit here) — and
            // while an embedded Office/WPS document is being edited on Program, whose own window
            // owns the picture. Real-machine check needed for the edit-state detection.
            if (!typing && keyData == settings.HotkeyFor(HotkeyAction.FadeToBlack)
                && _playback?.OfficeSession?.Status != OfficeSessionStatus.Editing)
            {
                ToggleFadeToBlack();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Whether keyboard focus is inside a text-editing control under
    /// <paramref name="root"/> — the focused control itself, or an editing control nested in a
    /// plain wrapper control. The second case matters: the Files search box is a plain
    /// <c>Control</c> holding an inner <c>TextBox</c>, so an <c>ActiveControl</c> walk that stops
    /// at a non-container wrapper would miss it entirely. Walks the <c>ContainsFocus</c> chain
    /// instead, which follows real focus through any nesting. This is the ONE typing判定 for
    /// every hotkey dispatch in this window (OP-20): the emergency-stop key and the four
    /// action hotkeys below all consult it, never a second focus walk. "Text-editing" here
    /// means <c>TextBoxBase</c>, an editable <c>ComboBox</c>, or a <c>NumericUpDown</c> — the
    /// three controls whose keys type into a value.</summary>
    internal static bool FocusIsInTextBox(Control root)
    {
        // Descend to the deepest control holding focus, then judge THAT control: Focused
        // itself is wrong here — a ComboBox's inner edit window holds the native focus, so
        // ComboBox.Focused is false while the operator is typing in it, and the edit is not a
        // WinForms child to descend into. ContainsFocus follows native child windows, so the
        // walk still lands on the ComboBox. (A NumericUpDown's inner edit IS a child TextBox,
        // so it lands on the edit and matches TextBoxBase.)
        Control current = root;
        while (true)
        {
            Control? next = null;
            foreach (Control child in current.Controls)
                if (child.ContainsFocus) { next = child; break; }
            if (next == null) break;
            current = next;
        }
        return current is TextBoxBase
            or ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList } or NumericUpDown;
    }

    /// <summary>播放 / 暂停 (Space): the big screen while local media is on air (same as 「暂停大屏」),
    /// otherwise 预览 (same as its ⏯).</summary>
    private void TogglePlayPause()
    {
        var engine = _playback != null && _stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            ? _playback : WindowEngine(_previewDock.ActiveWindow);
        if (engine.CurrentFile == null)
        {
            // Space with a device cast on air and nothing in the preview window used to do
            // nothing at all (OP-19): say that pausing lives on the device side.
            if (_stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.DeviceCast)
                _toastStack.Show("设备投屏中，暂停请在设备端操作。", ToastSeverity.Warning);
            return;
        }
        if (engine.IsPaused || engine.IsCompleted) engine.Resume();
        else if (engine.CanPause) engine.Pause();
    }

    private void RemoveFailedPreviewFileFromLibrary(MediaFile file)
    {
        var libraryEntry = _library.Files.FirstOrDefault(f => f.Id == file.Id);
        if (libraryEntry == null)
        {
            _toastStack.Show("该文件不在文件库中（可能来自活动），请在活动页中移除。", ToastSeverity.Warning);
            return;
        }
        if (RemoveFileFromLibrary(libraryEntry)) _preview.Stop();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _library.DefaultCompletion = settings.DefaultCompletionAction;
        ApplyTheme(settings.Theme);
        _emergencyHotkey.Configure(settings.GlobalEmergencyStopEnabled, settings.HotkeyFor(HotkeyAction.EmergencyStopGlobal));
    }

    private void OnAudioOutputFallback(string message)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnAudioOutputFallback(message)); }
            catch (InvalidOperationException) { }
            return;
        }
        _toastStack.Show(message, ToastSeverity.Warning);
    }

    /// <summary>The single refresh for the title-bar output status and BOTH stop buttons'
    /// availability (OP-04): the label here and the dock column's stop button both read the
    /// same <see cref="SharedPreviewDock.StopAvailable"/> rule, and this method runs on every
    /// output-state transition — state-machine changes, Program engine state changes, preview
    /// engine changes, and music start/stop — so the collapsed-column stop button (the title
    /// bar's) can never disagree with the column's own. A distinct failure state is shown while
    /// the machine claims Active but the Program content has failed or been lost.</summary>
    private void UpdateStatusLabel()
    {
        if (InvokeRequired) { BeginInvoke(UpdateStatusLabel); return; }
        bool active = _stateMachine.State == OutputState.Active;
        if (!active || _playback?.State is PlaybackChannelState.Playing or PlaybackChannelState.Paused)
            _programFailure = false;
        bool failed = _programFailure
            || (active && _stateMachine.Source == ProgramSource.LocalMedia && _playback?.State == PlaybackChannelState.Failed);
        _emergencyStop.Enabled = _previewDock.StopAvailable;
        _collapseStopButton.Enabled = _previewDock.StopAvailable; // DEV-22: same rule as the title-bar button
        var (message, role) =
            failed ? ("● 大屏播放失败", UiForegroundRole.Danger)
            : active && (_playback == null || _stateMachine.Source == ProgramSource.DeviceCast) ? ("● 大屏输出中 · 设备来投", UiForegroundRole.Danger)
            : active ? ("● 大屏输出中", UiForegroundRole.Danger)
            : _playback == null ? ("○ 未连接扩展屏 · 仅本地预览", UiForegroundRole.Muted)
            : _stateMachine.CastSwitchOn ? ("○ 大屏待机", UiForegroundRole.Muted)
            : ("○ 大屏待机 · 投屏开关已关闭", UiForegroundRole.Warning);
        // Collapsed column: the device card is hidden, so the status says a device is waiting in
        // preview; the ‹ button expands the column back to it (OP-29). (The presenter window is a
        // window of its own and stays visible when collapsed, so it needs no indicator here.)
        if (_monitorCollapsed && _deviceInPreview) message += " · 1 台设备在预览";
        // Stalled device signal (OP-27): the frozen picture must not keep passing for live.
        if (_deviceSignalStalled) message += " · 信号中断";
        // DEV-10: a global 急停 key that is switched on but cannot work globally stays on the
        // status line until it is fixed — the one-time Critical toast alone is too easy to miss.
        if (_emergencyHotkey.GloballyIneffective) message += " · ⚠ 全局急停未生效";
        _statusLabel.Text = message;
        ModernUi.SetForegroundRole(_statusLabel, role);
        _filesPanel.SetOutputActive(active);
    }

    /// <summary>Program output was lost without the state machine leaving Active (OP-09: the GPU
    /// device was removed and the local engine was stopped, so the overlay stays up black). The
    /// label switches to the failure state and the retry affordance lives in the problem toast
    /// the caller shows; this only records the state (here and in the dock's chip).</summary>
    internal void ReportProgramFailure()
    {
        _programFailure = true;
        _previewDock.SetProgramFailed(true);
        UpdateStatusLabel();
    }

    private void OnPreviewEngineStateChanged(PlaybackChannelState _) => UpdateStatusLabel();

    private void OnMusicChanged() => UpdateStatusLabel();

    private void OnPlaybackDeclinedByCastSwitch(MediaFile file)
    {
        _toastStack.Show($"投屏开关已关闭，未投放：{Path.GetFileName(file.SourcePath)}", ToastSeverity.Warning);
    }

    private void OnBackgroundAudioTakeIntercepted(MediaFile file)
    {
        _toastStack.Show($"《{Path.GetFileName(file.SourcePath)}》是背景音乐，不占用大屏画面", ToastSeverity.Info);
    }

    /// <summary>PLANNING.md §11 Toast "涉及播放的异常需带可执行按钮（重试/移除）" — this is that
    /// requirement's first real implementation; previously a decode/render failure was only logged
    /// (see <see cref="PlaybackEngine.PlaybackAbnormallyInterrupted"/>'s own doc comment), with
    /// nothing shown to whoever might actually be standing at this Terminal. "移除" picks between two
    /// already-existing removal operations depending on <see cref="PlaybackEngine.CurrentActivity"/>:
    /// <see cref="RemoveFileFromActivity"/> (mirrors <c>ActivitiesPanel.OnRemoveFile</c>) when the
    /// failing file was reached through an activity, <see cref="RemoveFileFromLibrary"/> (mirrors
    /// <c>FilesPanel.OnRemoveClick</c>) when it was played directly with no activity context.
    /// Deliberately skips the confirmation dialog those two panel buttons show before removing —
    /// clicking a named action button on an error notification is already the deliberate act a
    /// confirmation dialog exists to double-check for an ambient browsing click, not something this
    /// needs a second prompt for.</summary>
    private void OnPlaybackAbnormallyInterrupted(MediaFile file, string errorMessage)
    {
        string fileName = Path.GetFileName(file.SourcePath);
        var owningActivity = _playback?.CurrentActivity;

        var actions = new List<ToastAction>
        {
            new("重试", () => _playback?.RetryCurrentFile()),
            owningActivity != null
                ? new ToastAction("移除", () => RemoveFileFromActivity(owningActivity, file))
                : new ToastAction("移除", () => RemoveFileFromLibrary(file)),
        };

        _toastStack.Show($"播放异常：{fileName}\n{errorMessage}", ToastSeverity.Critical, actions.ToArray());
    }

    /// <summary>Same operation as <c>ActivitiesPanel.OnRemoveFile</c> (file-list mutation + log +
    /// save + tree refresh), reached from a Toast instead of that panel's own "移除文件" button.
    /// **Residual risk, not fixed here**: this only removes the file from the activity's list — it
    /// does not also advance <see cref="PlaybackEngine"/> away from it or otherwise reconcile its
    /// internal <c>_currentFileIndex</c> against the now-shorter list, so a subsequent auto-advance
    /// could land on a different file than expected until the operator manually navigates (floating
    /// preview window) or reconnects. See this project's README for why that reconciliation wasn't
    /// attempted this round.</summary>
    private void RemoveFileFromActivity(Activity activity, MediaFile file)
    {
        var scenario = _scenarioStore.Scenarios.FirstOrDefault(s => s.Activities.Contains(activity));
        int index = activity.Files.IndexOf(file);
        if (index < 0) return;
        activity.Files.RemoveAt(index);
        // Save before logging/refreshing, and roll the in-memory removal back if it fails — otherwise the
        // tree refreshes as "removed" while disk still has the file and no error is shown, since the
        // Terminal's ThreadException handler only logs (audit C-25). Mirrors FilesPanel/ActivitiesPanel.
        try { _scenarioRepository.Save(_scenarioStore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            activity.Files.Insert(index, file);
            MessageBox.Show(this, $"移除未保存：{DataDirectory.Describe(ex)}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (scenario != null) _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _activitiesPanel.RefreshTree();
    }

    /// <summary>Same operation as <c>FilesPanel.OnRemoveClick</c> (library removal + log + grid
    /// refresh), reached from a Toast instead of that panel's own "移除" button.</summary>
    private bool RemoveFileFromLibrary(MediaFile file)
    {
        try { _library.Remove(file.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"移除未保存：{DataDirectory.Describe(ex)}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _fileOpLog.LogFileRemoved(file.Id, file.SourcePath);
        _filesPanel.Refresh_();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _emergencyHotkey?.Dispose();
        if (disposing)
        {
            _stateMachine.StateChanged -= OnStateChanged;
            _settingsStore.SettingsChanged -= OnSettingsChanged;
            _settingsStore.AudioOutputFallback -= OnAudioOutputFallback;
            _music.Changed -= OnMusicChanged;
            _settingsPanel.Dispose();
            if (_playback != null)
            {
                _playback.PlaybackDeclinedByCastSwitch -= OnPlaybackDeclinedByCastSwitch;
                _playback.BackgroundAudioTakeIntercepted -= OnBackgroundAudioTakeIntercepted;
                _playback.PlaybackAbnormallyInterrupted -= OnPlaybackAbnormallyInterrupted;
                _playback.FileStarted -= OnProgramFileStarted;
                _playback.StateChanged -= OnProgramPlaybackStateChanged;
            }
            // Preview engine first (it joins its playback threads), then the surface it presents through.
            _preview.Dispose();
            _previewVideoSurface?.Dispose();
            _preview2.Dispose();
            _preview2VideoSurface?.Dispose();
            _music.Dispose();

            // ShowPanel() only ever keeps the *currently active* panel inside _contentHost.Controls
            // (Clear() detaches the rest without disposing them) — the inactive three would
            // otherwise never get Dispose()d, and ActivitiesPanel in particular unsubscribes
            // PlaybackEngine/OutputStateMachine event handlers in its own Dispose override, so
            // skipping this would leak those subscriptions for the process's lifetime.
            _filesPanel.Dispose();
            _devicesPanel.Dispose();
            _activitiesPanel.Dispose();
        }
        base.Dispose(disposing);
    }
}
