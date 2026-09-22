using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI.Panels;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §8's operator-facing main window: fixed left nav (§8.1: cast switch, "断", the four
/// panel icons, connection status) + a right content area that swaps between panels. Shown on
/// whatever display the operator is actually sitting at — this is NOT the <c>OverlayWindow</c>,
/// which covers the bound extended display with actual content output.
///
/// All four §8.2 panels are real now: 文件/活动/设备 from earlier rounds, and 设置
/// (<see cref="SettingsPanel"/>) from this one — see that class and this project's README for what
/// its fields are and aren't (PLANNING.md only names the five category labels, not any field
/// within them).
///
/// The 投屏开关 is rendered by the shared <see cref="ToggleSwitch"/> control and keeps the
/// underlying state-machine behavior in the same place as the prototype interaction.
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
    private readonly ToastStack _toastStack;
    private readonly CheckBox _castSwitchCheckbox;
    private readonly Label _statusLabel;
    private readonly Button _recallPreviewButton;
    private readonly System.Windows.Forms.Timer _declinedMessageTimer;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly SettingsPanel _settingsPanel;

    public event Action? PreviewRecallRequested;

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
        NormalWindowAspectRatio = new Size(4, 3);
        ClientSize = new Size(960, 720);
        MinimumSize = new Size(800, 600);
        BackColor = ModernUi.Background;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        WindowsAppearance.UseDarkTitleBar(this);
        WindowsAppearance.EnableGlass(this);

        var nav = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(0), CornerRadius = 14, AutoScroll = false,
            GlassTint = Color.FromArgb(178, 8, 23, 40),
        };

        var brandMark = new BrandMark { Bounds = new Rectangle(14, 16, 28, 28) };

        // A plain checkbox standing in for §8.1's slide-switch visual — see class doc comment.
        _castSwitchCheckbox = new ToggleSwitch
        {
            Location = new Point(14, 54),
            Checked = stateMachine.CastSwitchOn,
        };
        var switchLabel = new Label
        {
            Text = "投屏开关",
            ForeColor = ModernUi.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(8, 84, 64, 20),
        };
        _castSwitchCheckbox.CheckedChanged += (_, _) => stateMachine.SetCastSwitch(_castSwitchCheckbox.Checked);

        var disconnectButton = new Button { Text = "断", Bounds = new Rectangle(12, 112, 56, 44) };
        ModernUi.StyleButton(disconnectButton, danger: true);
        disconnectButton.FlatAppearance.BorderColor = ModernUi.Danger;
        disconnectButton.FlatAppearance.BorderSize = 1;
        disconnectButton.ForeColor = ModernUi.Danger;
        disconnectButton.BackColor = Color.FromArgb(40, 60, 22, 28);
        disconnectButton.Font = new Font("Segoe UI Semibold", 10F);
        disconnectButton.Click += (_, _) => stateMachine.Disconnect();

        var filesButton = ModernUi.NavButton(NavIcon.Files, "文件", 180);
        var activitiesButton = ModernUi.NavButton(NavIcon.Activities, "活动", 234);
        var devicesButton = ModernUi.NavButton(NavIcon.Devices, "设备", 288);
        var settingsButton = ModernUi.NavButton(NavIcon.Settings, "设置", 342);
        var navButtons = new[] { filesButton, activitiesButton, devicesButton, settingsButton };

        _recallPreviewButton = new Button { Text = "预览", Bounds = new Rectangle(12, 410, 56, 36) };
        ModernUi.StyleButton(_recallPreviewButton);
        _recallPreviewButton.Click += (_, _) => PreviewRecallRequested?.Invoke();

        _statusLabel = new Label
        {
            ForeColor = ModernUi.Success,
            AutoSize = false,
            Bounds = new Rectangle(8, 650, 64, 40),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };

        nav.Controls.AddRange(new Control[]
        {
            brandMark, switchLabel, _castSwitchCheckbox, disconnectButton,
            filesButton, activitiesButton, devicesButton, settingsButton,
            _recallPreviewButton, _statusLabel,
        });

        _contentHost = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), CornerRadius = 14,
            GlassTint = Color.FromArgb(160, 15, 35, 58),
        };

        // One shared instance rather than a separate `new FileOperationLogger()` per panel: both
        // panels' loggers ultimately append to the same physical file
        // (`file-operations/file-ops-{date}.log`, see `DailyRollingLogWriter`), and that class's own
        // append lock is per-instance — two independent instances writing concurrently would each
        // lock against themselves only, not each other, reopening a small chance of one write
        // failing with a sharing violation right as the other holds the file open. Sharing one
        // instance (and therefore one lock) removes that risk entirely rather than just accepting it.
        _fileOpLog = new FileOperationLogger();

        _filesPanel = new FilesPanel(library, _fileOpLog, scenarioStore, scenarioRepository, _playback);
        _filesPanel.FilePlayRequested += OnFilePlayRequested;

        _devicesPanel = new DevicesPanel(pairedDevices, connectionLog);
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _playback, _fileOpLog, stateMachine);
        _settingsPanel = new SettingsPanel(settingsStore, identity);
        settingsStore.SettingsChanged += OnSettingsChanged;

        filesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, filesButton); ShowPanel(_filesPanel); };
        activitiesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, activitiesButton); ShowPanel(_activitiesPanel); };
        devicesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, devicesButton); ShowPanel(_devicesPanel); };
        settingsButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, settingsButton); ShowPanel(_settingsPanel); };

        // Keep the prototype's 24px outer margin and 14px rail/content gap explicit. A table layout
        // is more stable than competing Left/Fill docking children when DPI scaling is enabled.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(24),
            ColumnCount = 3,
            RowCount = 1,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.Controls.Add(nav, 0, 0);
        body.Controls.Add(new Panel { BackColor = Color.Transparent }, 1, 0);
        body.Controls.Add(_contentHost, 2, 0);
        var titleBar = new AppTitleBar(this, "Terminal");
        Controls.Add(body);
        Controls.Add(titleBar);
        // Observed WinForms docking: the BACK-most control docks FIRST and claims its edge; the
        // FRONT-most gets the innermost remainder. So the title bar (top strip) must be sent to the
        // back to own the full top edge, and the body brought to front to fill everything below it.
        titleBar.SendToBack();
        body.BringToFront();

        // PLANNING.md §11 "异常提示"："右下角Toast通知栈" — added to Controls last (and pinned via
        // its own OnParentChanged/SizeChanged handling, see ToastStack's doc comment) so it renders
        // on top of _contentHost regardless of which panel is currently showing.
        _toastStack = new ToastStack();
        Controls.Add(_toastStack);
        if (_playback != null) _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;

        // See PlaybackEngine.PlaybackDeclinedByCastSwitch's own doc comment (README risk #9): a
        // "点文件" click while the cast switch is off used to be a completely silent no-op — this
        // briefly overwrites _statusLabel (already visible on every panel, since it lives in the nav
        // sidebar rather than inside any one panel) with an acknowledgment, then reverts to the
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
        ThemeManager.Apply(this, settingsStore.Current.Theme);
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
    /// — this method updating the field is all that closure needs.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        _playback = playback;
        _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;
        _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;
        _activitiesPanel.AttachPlaybackEngine(playback);
        _filesPanel.AttachPlaybackEngine(playback);
        UpdateStatusLabel();
    }

    private void ShowPanel(Control panel)
    {
        _contentHost.Controls.Clear();
        panel.Dock = DockStyle.Fill;
        _contentHost.Controls.Add(panel);

        if (panel == _filesPanel) _filesPanel.Refresh_();
        else if (panel == _devicesPanel) _devicesPanel.Refresh_();
        else if (panel == _activitiesPanel) _activitiesPanel.RefreshTree();
        else if (panel == _settingsPanel) _settingsPanel.Refresh_();
    }

    private void OnStateChanged(OutputState state) => UpdateStatusLabel();

    private void OnFilePlayRequested(MediaFile file)
    {
        // PLANNING.md §9.1: turning the cast switch off means local preview, not a silent
        // rejection. The managed output graph is deliberately not touched in this mode; let
        // Windows' registered handler play/open the file on the operator display instead.
        if (_playback != null && !_stateMachine.CastSwitchOn)
        {
            OpenLocalPreview(file);
            return;
        }

        if (_playback != null)
        {
            _playback.RequestPlay(file);
            return;
        }

        // A dedicated output display is not available, so the D3D-backed output graph cannot be
        // created. Do not silently ignore the click: open the media through Windows' registered
        // local application so operators can still verify the imported file on a single-monitor
        // setup. Once an extended display is attached, the same action automatically returns to
        // EveryStage's managed output path through AttachPlaybackEngine.
        OpenLocalPreview(file);
    }

    private void OpenLocalPreview(MediaFile file)
    {
        try
        {
            if (!File.Exists(file.SourcePath))
                throw new FileNotFoundException("文件不存在或已被移动。", file.SourcePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.SourcePath) { UseShellExecute = true });
            _statusLabel.Text = $"● 本机预览：{Path.GetFileName(file.SourcePath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法使用系统默认程序预览该文件：\n\n{ex.Message}",
                "本机预览失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnSettingsChanged(AppSettings settings) => ThemeManager.Apply(this, settings.Theme);

    private void UpdateStatusLabel()
    {
        if (_playback == null)
        {
            _statusLabel.Text = "● 本机预览模式\n未连接扩展屏";
            _recallPreviewButton.Enabled = false;
            _filesPanel.SetOutputActive(false);
            return;
        }
        bool active = _stateMachine.State == OutputState.Active;
        _statusLabel.Text = active ? "● 输出中" : "○ 待机中";
        _recallPreviewButton.Enabled = active;
        _filesPanel.SetOutputActive(active);
    }

    private void OnPlaybackDeclinedByCastSwitch(MediaFile file)
    {
        _statusLabel.Text = $"⚠ 投屏开关已关闭\n未投放：{Path.GetFileName(file.SourcePath)}";
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
        activity.Files.Remove(file);
        if (scenario != null) _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _scenarioRepository.Save(_scenarioStore);
        _activitiesPanel.RefreshTree();
    }

    /// <summary>Same operation as <c>FilesPanel.OnRemoveClick</c> (library removal + log + grid
    /// refresh), reached from a Toast instead of that panel's own "移除" button.</summary>
    private void RemoveFileFromLibrary(MediaFile file)
    {
        _library.Remove(file.Id);
        _fileOpLog.LogFileRemoved(file.Id, file.SourcePath);
        _filesPanel.Refresh_();
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
