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
/// 文件、活动、设备 are real (§8.2) — all three had (or, for 活动, needed only) a working data
/// source to hang UI on. 设置 is an honest placeholder (<see cref="NotImplementedPanel"/>); see
/// this project's README for what's missing there and why.
///
/// The 投屏开关 here is a plain <see cref="CheckBox"/>, not the slide-switch visual PLANNING.md §8.1
/// calls for ("滑动开关，非按钮") — that's a Phase 5 visual-design concern (themes, Acrylic/Mica,
/// consistent control styling), out of scope for getting the underlying behavior wired up correctly.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly OutputStateMachine _stateMachine;
    private readonly PlaybackEngine? _playback;
    private readonly Panel _contentHost;
    private readonly CheckBox _castSwitchCheckbox;
    private readonly Label _statusLabel;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly NotImplementedPanel _settingsPanel;

    public MainWindow(
        OutputStateMachine stateMachine, PlaybackEngine? playback, FileLibraryStore library,
        PairedDeviceStore pairedDevices, ScenarioStore scenarioStore, ScenarioRepository scenarioRepository)
    {
        _stateMachine = stateMachine;
        _playback = playback;

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

        _filesPanel = new FilesPanel(library);
        _filesPanel.FilePlayRequested += file => _playback?.RequestPlay(file);

        _devicesPanel = new DevicesPanel(pairedDevices);
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _playback, new FileOperationLogger(), stateMachine);
        _settingsPanel = new NotImplementedPanel("设置", "通用/显示/播放行为/网络与设备等分类设置尚未实现。");

        filesButton.Click += (_, _) => ShowPanel(_filesPanel);
        activitiesButton.Click += (_, _) => ShowPanel(_activitiesPanel);
        devicesButton.Click += (_, _) => ShowPanel(_devicesPanel);
        settingsButton.Click += (_, _) => ShowPanel(_settingsPanel);

        Controls.Add(_contentHost);
        Controls.Add(nav);

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
    }

    private void OnStateChanged(OutputState state) => UpdateStatusLabel();

    private void UpdateStatusLabel() =>
        _statusLabel.Text = _stateMachine.State == OutputState.Active ? "● 输出中" : "○ 待机中";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stateMachine.StateChanged -= OnStateChanged;

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
