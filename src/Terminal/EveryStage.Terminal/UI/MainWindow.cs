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
    private readonly MusicBar _musicBar;
    private readonly Panel _monitorColumn;
    private readonly RoundedActionButton _expandMonitorButton;
    private bool _monitorCollapsed;

    // Preview channel: always exists, renders into the dock's monitor, independent of the extended
    // display and the cast switch. _playback (above) is the Program channel.
    private readonly PlaybackEngine _preview;
    private readonly DocumentProgramSync _documentSync;
    private readonly VideoSurface? _previewVideoSurface;
    private readonly ToastStack _toastStack;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _declinedMessageTimer;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly SettingsPanel _settingsPanel;

    /// <summary>The palette each page was last themed with. Only the visible page is in the tree when
    /// <see cref="ThemeManager.Apply"/> runs, so <see cref="ShowPanel"/> re-themes a page that missed
    /// a switch (or the saved theme at startup) from this palette.</summary>
    private readonly Dictionary<Control, UiPalette> _panelPalettes = new();

    /// <summary>A problem found at startup that the operator must fix (stays until dismissed).</summary>
    internal void ShowStartupProblem(string message) => _toastStack.Show(message, ToastSeverity.Critical);

    public void ShowPairingRequest(PairingRequest request) => _devicesPanel.ShowPairingRequest(request);

    public void ClearPairingRequest(string requestId) => _devicesPanel.ClearPairingRequest(requestId);
    public Control DeviceCastVideoHost => _previewDock.DeviceVideoHost;
    public event Action? DeviceTakeRequested;
    public event Action? DeviceRevertRequested;
    public event Action<int, int>? DeviceVideoHostResized;

    public MainWindow(
        OutputStateMachine stateMachine, PlaybackEngine? playback, FileLibraryStore library,
        PairedDeviceStore pairedDevices, ScenarioStore scenarioStore, ScenarioRepository scenarioRepository,
        SettingsStore settingsStore, DeviceIdentity identity, DeviceConnectionLogger connectionLog)
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
        var navButtons = new[] { filesButton, activitiesButton, devicesButton, settingsButton };
        rail.Controls.AddRange(navButtons);
        rail.Layout += (_, _) =>
        {
            int width = LogicalToDeviceUnits(60), height = LogicalToDeviceUnits(58), y = LogicalToDeviceUnits(12);
            foreach (var button in navButtons)
            {
                button.Bounds = new Rectangle((rail.ClientSize.Width - width) / 2, y, width, height);
                y += height + LogicalToDeviceUnits(6);
            }
        };
        rail.Paint += (_, e) => DrawEdge(e.Graphics, rail, right: true);

        _statusLabel = new Label
        {
            AutoSize = true, ForeColor = ModernUi.Success, Font = new Font(ModernUi.UiFont, 9F),
            TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 8, 12, 0), BackColor = Color.Transparent,
        };

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
        _monitorColumn.Controls.Add(_previewDock);
        _monitorColumn.Controls.Add(_expandMonitorButton);
        _previewVideoSurface = TryCreateVideoSurface(_previewDock.PreviewSurface);
        _previewDock.PreviewSurface.VideoHostResized += (w, h) =>
        {
            try { _previewVideoSurface?.Resize(w, h); } catch (Exception) { } // a lost device surfaces on the next present
        };
        _preview = new PlaybackEngine(PlaybackChannel.Preview, _previewDock.PreviewSurface, _previewVideoSurface,
            settingsStore, scenarioStore, stateMachine: null);
        _previewDock.BindPreview(_preview);
        _previewDock.AttachProgram(playback);
        _previewDock.TakeRequested += TakePreviewToProgram;
        _previewDock.StopProgramRequested += StopProgramOutput;
        _previewDock.DeviceTakeRequested += () => DeviceTakeRequested?.Invoke();
        _previewDock.DeviceRevertRequested += () => DeviceRevertRequested?.Invoke();
        _previewDock.DeviceVideoHostResized += (w, h) => DeviceVideoHostResized?.Invoke(w, h);
        _previewDock.RemoveFromLibraryRequested += RemoveFailedPreviewFileFromLibrary;
        // PDF / Word / presentation controls and Preview → Program document sync (实时同步 / 冻结 / 更新大屏).
        _documentSync = new DocumentProgramSync(_preview, () => _playback);
        _previewDock.BindDocumentSync(_documentSync);
        // Closing a Word document or presentation with unsaved edits asks here — never a WPS/Word dialog.
        OfficeSessionManager.Shared.ConfirmSaveOnClose = session => (bool)Invoke(() =>
            MessageBox.Show(this, $"“{Path.GetFileName(session.SourcePath)}”有未保存的修改，关闭前是否保存？", "保存文档",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

        // One shared instance rather than a separate `new FileOperationLogger()` per panel: both
        // panels' loggers ultimately append to the same physical file
        // (`file-operations/file-ops-{date}.log`, see `DailyRollingLogWriter`), and that class's own
        // append lock is per-instance — two independent instances writing concurrently would each
        // lock against themselves only, not each other, reopening a small chance of one write
        // failing with a sharing violation right as the other holds the file open. Sharing one
        // instance (and therefore one lock) removes that risk entirely rather than just accepting it.
        _fileOpLog = new FileOperationLogger();

        _filesPanel = new FilesPanel(library, _fileOpLog, scenarioStore, scenarioRepository, _preview);
        _filesPanel.FilePlayRequested += OnFilePlayRequested;
        _filesPanel.TakeRequested += TakeFileToProgram;
        _filesPanel.PlaybackStopRequested += () => _preview.Stop();
        _filesPanel.StopProgramRequested += StopProgramOutput;
        _filesPanel.IsProgramLive = () => _stateMachine.State == OutputState.Active;
        _filesPanel.ProgramFile = () => _stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.LocalMedia
            ? _playback?.CurrentFile : null;

        // Background-music card under the 大屏 section: a second view of the file page's audio bar, so
        // the same track can be controlled from every page. Shown while the column has room for it.
        // Background music: its own channel and the mini bar along the bottom of every page.
        _musicStore = new MusicLibraryStore();
        _music = new MusicChannel(_musicStore)
        {
            // Give way while the big screen plays video with sound (or audio).
            ShouldDuck = () => _stateMachine.State == OutputState.Active && _playback is { State: PlaybackChannelState.Playing, Muted: false } program
                && (program.CurrentVideoHasAudio || program.CurrentFile?.Kind == MediaKind.Audio),
        };
        _musicBar = new MusicBar(_music) { Dock = DockStyle.Bottom };
        _musicBar.PlaylistsRequested += () => OpenPlaylists();
        _contentHost.Controls.Add(_musicBar);
        _filesPanel.AddToPlaylistRequested += files => OpenPlaylists(files.Select(f => f.SourcePath).ToList());

        _devicesPanel = new DevicesPanel(pairedDevices, connectionLog, identity.DeviceName, stateMachine);
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _preview, _fileOpLog, stateMachine)
        {
            ProgramEngine = () => _playback,
        };
        _activitiesPanel.RunRequested += RunActivityItem;
        _settingsPanel = new SettingsPanel(settingsStore, identity);
        foreach (Control page in new Control[] { _filesPanel, _devicesPanel, _activitiesPanel, _settingsPanel })
            _panelPalettes[page] = ModernUi.Palette;
        settingsStore.SettingsChanged += OnSettingsChanged;

        filesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, filesButton); ShowPanel(_filesPanel); };
        activitiesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, activitiesButton); ShowPanel(_activitiesPanel); };
        devicesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, devicesButton); ShowPanel(_devicesPanel); };
        settingsButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, settingsButton); ShowPanel(_settingsPanel); };

        var titleBar = new AppTitleBar(this, "终端机");
        titleBar.AddStatus(_statusLabel);
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
        _toastStack = new ToastStack { RightInset = _monitorColumn.Width };
        Controls.Add(_toastStack);
        if (_playback != null) _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;

        // See PlaybackEngine.PlaybackDeclinedByCastSwitch's own doc comment (README risk #9): a
        // "点文件" click while the cast switch is off used to be a completely silent no-op — this
        // briefly overwrites _statusLabel (visible on every page, since it lives in the title bar)
        // with an acknowledgment, then reverts to the
        // normal state text after a few seconds. One-shot timer per declined click rather than a
        // running one: Interval is reset and the timer restarted on every new decline, so several
        // rapid declined clicks just keep extending the same message instead of racing each other.
        _declinedMessageTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _declinedMessageTimer.Tick += (_, _) =>
        {
            _declinedMessageTimer.Stop();
            UpdateStatusLabel();
        };
        if (_playback != null) _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;

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
        if (_playback != null)
        {
            _playback.PlaybackAbnormallyInterrupted -= OnPlaybackAbnormallyInterrupted;
            _playback.PlaybackDeclinedByCastSwitch -= OnPlaybackDeclinedByCastSwitch;
        }
        _playback = playback;
        _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;
        _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;
        // Files/Activities/audio bar operate the Preview engine; only the dock needs to know Program.
        _previewDock.AttachProgram(playback);
        UpdateStatusLabel();
    }

    public void UpdateDeviceCast(string? deviceName, bool connected, bool onProgram)
        => _previewDock.UpdateDeviceCast(deviceName, connected, onProgram);

    /// <summary>Starts the playlist marked 「启动后自动播放」 (called once by the application at startup).</summary>
    public void StartBackgroundMusic() => _music.AutoPlayOnStartup();

    /// <summary>The 「背景音乐歌单」 window; <paramref name="add"/> are added to the selected playlist first.</summary>
    private void OpenPlaylists(IReadOnlyList<string>? add = null)
    {
        using var dialog = new PlaylistDialog(_music, _library);
        if (add is { Count: > 0 }) dialog.AddTracks(add);
        dialog.ShowDialog(this);
    }

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
        if (_toastStack != null) _toastStack.RightInset = _monitorColumn.Width;
    }

    private void SetMonitorCollapsed(bool collapsed)
    {
        if (_monitorCollapsed == collapsed) return;
        _monitorCollapsed = collapsed;
        _previewDock.Visible = !collapsed;
        _expandMonitorButton.Visible = collapsed;
        ApplyFrameMetrics();
    }

    /// <summary>The 1px divider between the rail / monitor column and the page area.</summary>
    private static void DrawEdge(Graphics g, Control control, bool right)
    {
        using var pen = new Pen(ModernUi.Mix(ModernUi.Rail, ModernUi.Border, 0.45f));
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

        _previewDock.SetRunControls(panel == _activitiesPanel ? _activitiesPanel.RunControls : null);
        if (panel == _filesPanel) _filesPanel.Refresh_();
        else if (panel == _devicesPanel) _devicesPanel.Refresh_();
        else if (panel == _activitiesPanel) _activitiesPanel.RefreshTree();
        else if (panel == _settingsPanel) _settingsPanel.Refresh_();
    }

    /// <summary>Applies a theme to the window and records it for whichever page is hosted right now;
    /// the other pages catch up in <see cref="ShowPanel"/>.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        ThemeManager.Apply(this, theme);
        foreach (Control hosted in _panelHost.Controls) _panelPalettes[hosted] = ModernUi.Palette;
    }

    private void OnStateChanged(OutputState state)
    {
        // Program on air (local media or a device cast) → Preview goes silent so monitor audio can
        // never leak into the venue; it comes back when Program stops.
        _preview.Muted = state == OutputState.Active;
        UpdateStatusLabel();
    }

    /// <summary>Double-click / 播放·预览 / 在本地预览: load and play in the Preview monitor. Always internal —
    /// no extended display or cast switch involved, and never Windows' default application (that is
    /// only the explicit "使用系统默认程序打开" command, see <see cref="ExternalOpener"/>).</summary>
    private void OnFilePlayRequested(MediaFile file) => _preview.RequestPlay(file, PlaybackTrigger.ManualSkip);

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

    /// <summary>"投到屏幕" on a file: show it in Preview and put it on the extended display.</summary>
    private void TakeFileToProgram(MediaFile file)
    {
        if (!ReferenceEquals(_preview.CurrentFile, file)) _preview.RequestPlay(file, PlaybackTrigger.ManualSkip);
        TakeSnapshot(new PlaybackEngine.Snapshot(file, null, -1, TimeSpan.Zero, false));
    }

    /// <summary>流程 「下一项 ▶」 / double-click / space: put this item on the big screen, continuing the
    /// section from there. Without an extended display it plays in Preview instead.</summary>
    private void RunActivityItem(Activity activity, int index)
    {
        if (index < 0 || index >= activity.Files.Count) return;
        if (_playback == null)
        {
            _preview.RequestPlay(activity, index, PlaybackTrigger.ManualSkip);
            return;
        }
        TakeSnapshot(new PlaybackEngine.Snapshot(activity.Files[index], activity, index, TimeSpan.Zero, false));
    }

    /// <summary>"投到屏幕" in the dock: continue the Preview content (file, activity position and play
    /// position) on Program, then hold Preview on that frame/position so it can be re-taken.</summary>
    private void TakePreviewToProgram()
    {
        if (_preview.State is PlaybackChannelState.Loading or PlaybackChannelState.Failed || _preview.CaptureSnapshot() is not { } snapshot)
        {
            _toastStack.Show("预览中没有可以投到屏幕的内容。先在文件或活动里双击一个文件预览。", ToastSeverity.Warning);
            return;
        }
        if (TakeSnapshot(snapshot) && _preview.CanPause) _preview.Pause();
    }

    private bool TakeSnapshot(PlaybackEngine.Snapshot snapshot)
    {
        if (_playback == null)
        {
            _toastStack.Show("未连接扩展屏，无法投到屏幕。连接扩展显示器后即可投出；本地预览不受影响。", ToastSeverity.Warning);
            return false;
        }
        if (_stateMachine.State == OutputState.Active && _stateMachine.Source == ProgramSource.DeviceCast
            && MessageBox.Show(this, "扩展屏正在输出设备来投的画面。\n切换为本地内容后，设备画面将退回设备投屏窗口。是否继续？", "投到屏幕",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return false;
        if (!_stateMachine.CastSwitchOn)
        {
            if (MessageBox.Show(this, "投屏开关已关闭（可在托盘菜单中切换）。\n是否开启投屏开关并投到屏幕？", "投到屏幕",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
            _stateMachine.SetCastSwitch(true);
        }
        return _playback.RequestPlay(snapshot);
    }

    /// <summary>"停止输出": take the extended display off air. Preview keeps its content and position.</summary>
    private void StopProgramOutput() => _stateMachine.Disconnect();

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

    private void OnSettingsChanged(AppSettings settings) => ApplyTheme(settings.Theme);

    private void UpdateStatusLabel()
    {
        bool active = _stateMachine.State == OutputState.Active;
        (_statusLabel.Text, _statusLabel.ForeColor) =
            active && (_playback == null || _stateMachine.Source == ProgramSource.DeviceCast) ? ("● 大屏输出中 · 设备来投", ModernUi.Success)
            : active ? ("● 大屏输出中", ModernUi.Success)
            : _playback == null ? ("○ 未连接扩展屏 · 仅本地预览", ModernUi.Muted)
            : _stateMachine.CastSwitchOn ? ("○ 大屏待机", ModernUi.Muted)
            : ("○ 大屏待机 · 投屏开关已关闭", ModernUi.Warning);
        _filesPanel.SetOutputActive(active);
    }

    private void OnPlaybackDeclinedByCastSwitch(MediaFile file)
    {
        _statusLabel.Text = $"⚠ 投屏开关已关闭 · 未投放：{Path.GetFileName(file.SourcePath)}";
        _statusLabel.ForeColor = ModernUi.Warning;
        _declinedMessageTimer.Stop(); // restart rather than stack — see the timer's own construction comment.
        _declinedMessageTimer.Start();
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
        if (disposing)
        {
            _stateMachine.StateChanged -= OnStateChanged;
            _settingsStore.SettingsChanged -= OnSettingsChanged;
            _settingsPanel.Dispose();
            if (_playback != null)
            {
                _playback.PlaybackDeclinedByCastSwitch -= OnPlaybackDeclinedByCastSwitch;
                _playback.PlaybackAbnormallyInterrupted -= OnPlaybackAbnormallyInterrupted;
            }
            _declinedMessageTimer.Dispose();
            // Preview engine first (it joins its playback threads), then the surface it presents through.
            _preview.Dispose();
            _previewVideoSurface?.Dispose();
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
