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
/// The 投屏开关 here is a plain <see cref="CheckBox"/>, not the slide-switch visual PLANNING.md §8.1
/// calls for ("滑动开关，非按钮") — that's a Phase 5 visual-design concern (themes, Acrylic/Mica,
/// consistent control styling), out of scope for getting the underlying behavior wired up correctly.
///
/// Also hosts <see cref="ToastStack"/> (PLANNING.md §11's "右下角Toast通知栈"), pinned on top of
/// whichever panel is currently showing — its only producer today is
/// <see cref="PlaybackEngine.PlaybackAbnormallyInterrupted"/>, see
/// <see cref="OnPlaybackAbnormallyInterrupted"/>.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly OutputStateMachine _stateMachine;
    private readonly PlaybackEngine? _playback;
    private readonly FileLibraryStore _library;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    private readonly FileOperationLogger _fileOpLog;
    private readonly Panel _contentHost;
    private readonly ToastStack _toastStack;
    private readonly CheckBox _castSwitchCheckbox;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _declinedMessageTimer;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly SettingsPanel _settingsPanel;

    public MainWindow(
        OutputStateMachine stateMachine, PlaybackEngine? playback, FileLibraryStore library,
        PairedDeviceStore pairedDevices, ScenarioStore scenarioStore, ScenarioRepository scenarioRepository,
        SettingsStore settingsStore, DeviceIdentity identity, DeviceConnectionLogger connectionLog)
    {
        _stateMachine = stateMachine;
        _playback = playback;
        _library = library;
        _scenarioStore = scenarioStore;
        _scenarioRepository = scenarioRepository;

        Text = "EveryStage 终端机";
        ClientSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var nav = new Panel { Dock = DockStyle.Left, Width = 96, BackColor = Color.FromArgb(30, 30, 30) };

        // A plain checkbox standing in for §8.1's slide-switch visual — see class doc comment.
        _castSwitchCheckbox = new CheckBox
        {
            Text = "投屏开关",
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(8, 12),
            Checked = stateMachine.CastSwitchOn,
        };
        _castSwitchCheckbox.CheckedChanged += (_, _) => stateMachine.SetCastSwitch(_castSwitchCheckbox.Checked);

        var disconnectButton = new Button { Text = "断", Location = new Point(8, 44), Width = 80 };
        disconnectButton.Click += (_, _) => stateMachine.Disconnect();

        var filesButton = MakeNavButton("文件", 90);
        var activitiesButton = MakeNavButton("活动", 130);
        var devicesButton = MakeNavButton("设备", 170);
        var settingsButton = MakeNavButton("设置", 210);

        _statusLabel = new Label { ForeColor = Color.LightGray, AutoSize = true, Location = new Point(8, 520) };

        nav.Controls.AddRange(new Control[]
        {
            _castSwitchCheckbox, disconnectButton, filesButton, activitiesButton, devicesButton, settingsButton, _statusLabel,
        });

        _contentHost = new Panel { Dock = DockStyle.Fill };

        // One shared instance rather than a separate `new FileOperationLogger()` per panel: both
        // panels' loggers ultimately append to the same physical file
        // (`file-operations/file-ops-{date}.log`, see `DailyRollingLogWriter`), and that class's own
        // append lock is per-instance — two independent instances writing concurrently would each
        // lock against themselves only, not each other, reopening a small chance of one write
        // failing with a sharing violation right as the other holds the file open. Sharing one
        // instance (and therefore one lock) removes that risk entirely rather than just accepting it.
        _fileOpLog = new FileOperationLogger();

        _filesPanel = new FilesPanel(library, _fileOpLog, scenarioStore, scenarioRepository);
        _filesPanel.FilePlayRequested += file => _playback?.RequestPlay(file);

        _devicesPanel = new DevicesPanel(pairedDevices, connectionLog);
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _playback, _fileOpLog, stateMachine);
        _settingsPanel = new SettingsPanel(settingsStore, identity);

        filesButton.Click += (_, _) => ShowPanel(_filesPanel);
        activitiesButton.Click += (_, _) => ShowPanel(_activitiesPanel);
        devicesButton.Click += (_, _) => ShowPanel(_devicesPanel);
        settingsButton.Click += (_, _) => ShowPanel(_settingsPanel);

        Controls.Add(_contentHost);
        Controls.Add(nav);

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

    private Button MakeNavButton(string text, int y) => new() { Text = text, Location = new Point(8, y), Width = 80 };

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

    private void UpdateStatusLabel() =>
        _statusLabel.Text = _stateMachine.State == OutputState.Active ? "● 输出中" : "○ 待机中";

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
            _settingsPanel.Dispose();
        }
        base.Dispose(disposing);
    }
}
