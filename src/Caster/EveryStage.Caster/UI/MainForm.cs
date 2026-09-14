using EveryStage.Caster.Capture;
using EveryStage.Caster.Discovery;
using EveryStage.Discovery;

namespace EveryStage.Caster.UI;

/// <summary>
/// The Caster's whole UI (PLANNING.md §12): "单一任务导向，不做复杂功能堆叠" — a standby panel
/// (target terminal list + start button + privacy notice) and, once paired, a second panel showing
/// what's targeted. This implements the discovery + pairing handshake for real, plus a screen-
/// capture self-test (see <see cref="CaptureSelfTestRunner"/>) that proves Desktop Duplication
/// capture works — but it does NOT implement actual encode/transport (Phase 2's H.264/RTP half
/// doesn't exist anywhere in this repo yet), so the "paired" panel says so plainly instead of
/// pretending a live stream exists. See this project's README.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly CaptureSelfTestRunner _captureSelfTest = new();
    private readonly System.Windows.Forms.Timer _listRefreshTimer;
    private readonly System.Windows.Forms.Timer _captureStatsTimer;

    private readonly Panel _standbyPanel;
    private readonly ListBox _terminalListBox;
    private readonly Button _startButton;

    private readonly Panel _pairedPanel;
    private readonly Label _pairedWithLabel;
    private readonly Button _captureSelfTestButton;
    private readonly Label _captureStatsLabel;

    private DiscoveredTerminal? _pairedTerminal;

    public MainForm(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;

        Text = "EveryStage 投屏机";
        ClientSize = new Size(320, 320);
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

        // --- 投屏中态 (currently: "已配对，等待编码/推流实现" — see class doc comment) ---
        _pairedWithLabel = new Label { Bounds = new Rectangle(12, 12, 296, 40) };
        var notImplementedLabel = new Label
        {
            Text = "H.264编码与RTP推流尚未实现（阶段2）。下面的按钮只验证屏幕捕获本身能不能跑通，\n捕获到的画面不会发送到任何地方。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 54, 296, 56),
        };

        _captureSelfTestButton = new Button { Text = "开始屏幕捕获自检", Bounds = new Rectangle(12, 118, 296, 32) };
        _captureSelfTestButton.Click += OnCaptureSelfTestClick;
        _captureStatsLabel = new Label { Bounds = new Rectangle(12, 156, 296, 60), ForeColor = Color.DimGray };

        var backButton = new Button { Text = "返回", Bounds = new Rectangle(12, 266, 296, 32) };
        backButton.Click += (_, _) => ShowStandby();

        _pairedPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _pairedPanel.Controls.AddRange(new Control[]
        {
            _pairedWithLabel, notImplementedLabel, _captureSelfTestButton, _captureStatsLabel, backButton,
        });

        Controls.Add(_pairedPanel);
        Controls.Add(_standbyPanel);

        _listRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _listRefreshTimer.Tick += (_, _) => RefreshTerminalList();
        _listRefreshTimer.Start();

        _captureStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _captureStatsTimer.Tick += (_, _) => RefreshCaptureStats();
    }

    private void OnCaptureSelfTestClick(object? sender, EventArgs e)
    {
        if (_captureSelfTest.IsRunning)
        {
            _captureSelfTest.Stop();
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            _captureStatsLabel.Text = "";
        }
        else
        {
            _captureSelfTest.Start();
            _captureStatsTimer.Start();
            _captureSelfTestButton.Text = "停止屏幕捕获自检";
            RefreshCaptureStats();
        }
    }

    private void RefreshCaptureStats()
    {
        if (_captureSelfTest.LastError != null)
        {
            _captureStatsLabel.ForeColor = Color.DarkRed;
            _captureStatsLabel.Text = $"捕获出错：{_captureSelfTest.LastError}";
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            return;
        }

        _captureStatsLabel.ForeColor = Color.DimGray;
        _captureStatsLabel.Text =
            $"分辨率: {_captureSelfTest.Width}x{_captureSelfTest.Height}\n" +
            $"已捕获帧数: {_captureSelfTest.FrameCount}    近1秒帧率: {_captureSelfTest.Fps:F1}";
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
        if (_captureSelfTest.IsRunning)
        {
            _captureSelfTest.Stop();
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            _captureStatsLabel.Text = "";
        }

        _pairedTerminal = null;
        _pairedPanel.Visible = false;
        _standbyPanel.Visible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listRefreshTimer.Dispose();
            _captureStatsTimer.Dispose();
            _captureSelfTest.Dispose();
        }
        base.Dispose(disposing);
    }
}
