using System.Windows.Forms;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Tray;

/// <summary>
/// Tray presence for the always-on-background Terminal service: proves the process is alive,
/// toggles the cast switch, reopens the main window (<c>UI/MainWindow</c>) if it's been hidden, and
/// exits — "关闭程序...是待机状态下的次要分支（托盘菜单主动退出）" (§10) is explicitly the only
/// planned way to quit a device meant to run unattended.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _castSwitchItem;
    private readonly OutputStateMachine _stateMachine;

    public event Action? ExitRequested;
    public event Action? MainWindowRequested;

    public TrayIconController(OutputStateMachine stateMachine)
    {
        _stateMachine = stateMachine;

        var openMainWindowItem = new ToolStripMenuItem("打开主界面", null, (_, _) => MainWindowRequested?.Invoke());

        _castSwitchItem = new ToolStripMenuItem("投屏开关") { CheckOnClick = true, Checked = stateMachine.CastSwitchOn };
        _castSwitchItem.CheckedChanged += (_, _) => stateMachine.SetCastSwitch(_castSwitchItem.Checked);

        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(openMainWindowItem);
        menu.Items.Add(_castSwitchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, // placeholder; replace with product icon in Phase 4/5.
            Text = "EveryStage 终端机",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => MainWindowRequested?.Invoke();

        stateMachine.StateChanged += OnOutputStateChanged;
        stateMachine.CastSwitchChanged += OnCastSwitchChanged;
        UpdateTooltip();
    }

    private void OnOutputStateChanged(OutputState state) => UpdateTooltip();

    private void OnCastSwitchChanged(bool on)
    {
        if (_castSwitchItem.Checked != on) _castSwitchItem.Checked = on;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        string stateText = _stateMachine.State == OutputState.Active ? "扩展屏输出中" : "待机中";
        string switchText = _stateMachine.CastSwitchOn ? "开关: 开" : "开关: 关";
        // NotifyIcon.Text is capped at 63 chars on older Windows; this stays well under that.
        _notifyIcon.Text = $"EveryStage 终端机 - {stateText} / {switchText}";
    }

    public void Dispose()
    {
        _stateMachine.StateChanged -= OnOutputStateChanged;
        _stateMachine.CastSwitchChanged -= OnCastSwitchChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
