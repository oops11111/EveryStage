using EveryStage.Caster.Discovery;
using EveryStage.Discovery;

namespace EveryStage.Caster.UI;

/// <summary>
/// The Caster's whole UI (PLANNING.md §12): "单一任务导向，不做复杂功能堆叠" — a standby panel
/// (target terminal list + start button + privacy notice) and, once paired, a second panel showing
/// what's targeted. This implements the discovery + pairing handshake for real; it does NOT
/// implement actual screen capture/encode/transport (Phase 2's capture pipeline — DDA, H.264,
/// RTP — doesn't exist anywhere in this repo yet), so the "paired" panel says so plainly instead of
/// pretending a live stream exists. See this project's README.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly System.Windows.Forms.Timer _listRefreshTimer;

    private readonly Panel _standbyPanel;
    private readonly ListBox _terminalListBox;
    private readonly Button _startButton;

    private readonly Panel _pairedPanel;
    private readonly Label _pairedWithLabel;

    private DiscoveredTerminal? _pairedTerminal;

    public MainForm(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;

        Text = "EveryStage 投屏机";
        ClientSize = new Size(320, 280);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // --- 待机态 (PLANNING.md §12) ---
        _terminalListBox = new ListBox
        {
            Bounds = new Rectangle(12, 12, 296, 160),
            DisplayMember = nameof(DiscoveredTerminal.DeviceName), // else ListBox shows the record's generated ToString().
        };
        _terminalListBox.SelectedIndexChanged += (_, _) => _startButton.Enabled = _terminalListBox.SelectedItem != null;

        var privacyLabel = new Label
        {
            Text = "点击\"开始投屏\"后，将投放整个屏幕（全屏捕获），而不是仅本窗口或某个应用。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 178, 296, 40),
        };

        _startButton = new Button
        {
            Text = "开始投屏",
            Enabled = false,
            Bounds = new Rectangle(12, 226, 296, 32),
        };
        _startButton.Click += OnStartButtonClick;

        _standbyPanel = new Panel { Dock = DockStyle.Fill };
        _standbyPanel.Controls.AddRange(new Control[] { _terminalListBox, privacyLabel, _startButton });

        // --- 投屏中态 (currently: "已配对，等待推流功能实现" — see class doc comment) ---
        _pairedWithLabel = new Label { Bounds = new Rectangle(12, 12, 296, 60) };
        var notImplementedLabel = new Label
        {
            Text = "屏幕捕获与推流尚未实现（阶段2）。此处仅验证了设备发现与配对握手。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 80, 296, 60),
        };
        var backButton = new Button { Text = "返回", Bounds = new Rectangle(12, 226, 296, 32) };
        backButton.Click += (_, _) => ShowStandby();

        _pairedPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _pairedPanel.Controls.AddRange(new Control[] { _pairedWithLabel, notImplementedLabel, backButton });

        Controls.Add(_pairedPanel);
        Controls.Add(_standbyPanel);

        _listRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _listRefreshTimer.Tick += (_, _) => RefreshTerminalList();
        _listRefreshTimer.Start();
    }

    private void RefreshTerminalList()
    {
        // Preserve selection across refreshes by DeviceId rather than list index, since the list is
        // re-sorted by name each time and a device could shuffle position as others come and go.
        var selected = _terminalListBox.SelectedItem as DiscoveredTerminal;
        var terminals = _discoveryClient.GetTerminals();

        _terminalListBox.BeginUpdate();
        _terminalListBox.Items.Clear();
        foreach (var terminal in terminals) _terminalListBox.Items.Add(terminal);
        if (selected != null)
        {
            var stillPresent = terminals.FirstOrDefault(t => t.DeviceId == selected.DeviceId);
            if (stillPresent != null) _terminalListBox.SelectedItem = stillPresent;
        }
        _terminalListBox.EndUpdate();
    }

    private async void OnStartButtonClick(object? sender, EventArgs e)
    {
        if (_terminalListBox.SelectedItem is not DiscoveredTerminal terminal) return;

        _startButton.Enabled = false;
        _startButton.Text = "配对中...";
        try
        {
            var response = await _discoveryClient.RequestPairingAsync(terminal, _identity);
            if (response is { Accepted: true })
            {
                ShowPaired(terminal);
            }
            else
            {
                MessageBox.Show(this,
                    response == null ? "终端机未响应（超时）。" : $"终端机拒绝了配对请求：{response.Reason ?? "未说明原因"}",
                    "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _startButton.Text = "开始投屏";
            _startButton.Enabled = _terminalListBox.SelectedItem != null;
        }
    }

    private void ShowPaired(DiscoveredTerminal terminal)
    {
        _pairedTerminal = terminal;
        _pairedWithLabel.Text = $"已与 \"{terminal.DeviceName}\" 配对。";
        _standbyPanel.Visible = false;
        _pairedPanel.Visible = true;
    }

    private void ShowStandby()
    {
        _pairedTerminal = null;
        _pairedPanel.Visible = false;
        _standbyPanel.Visible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _listRefreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
