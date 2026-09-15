using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 设置面板: "二级分类：通用 / 显示 / 播放行为 / 网络与设备 / 关于". PLANNING.md
/// names only these five category labels — the specific fields inside each are this repository's
/// own choices (see <see cref="AppSettings"/>'s doc comment for where they came from), not a
/// PLANNING.md spec. Replaces the honest <c>NotImplementedPanel</c> placeholder this used to be.
///
/// One "保存设置" button at the bottom applies 通用/显示/播放行为 together (all three live in one
/// <see cref="AppSettings"/> object persisted via <see cref="SettingsStore"/>) — 设备名称 has its
/// own separate "保存设备名称" button because it persists through <see cref="DeviceIdentity.Save"/>
/// instead, a different store entirely; conflating the two under one button would misrepresent what
/// actually gets written where.
/// </summary>
public sealed class SettingsPanel : UserControl
{
    private readonly SettingsStore _settingsStore;
    private readonly DeviceIdentity _identity;

    private readonly CheckBox _castSwitchDefaultCheckbox;
    private readonly ComboBox _monitorComboBox;
    private readonly CheckBox _defaultStayDurationEnabledCheckbox;
    private readonly NumericUpDown _defaultStayDurationSeconds;
    private readonly TextBox _deviceNameTextBox;
    private readonly Label _savedLabel;

    // Sentinel for the ComboBox's "自动选择" item — no real MonitorInfo.DeviceName is ever an empty
    // string, so this can't collide with an actual monitor.
    private const string AutoSelectMonitor = "";

    public SettingsPanel(SettingsStore settingsStore, DeviceIdentity identity)
    {
        _settingsStore = settingsStore;
        _identity = identity;
        Dock = DockStyle.Fill;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var settings = _settingsStore.Current;

        // --- 通用 ---
        _castSwitchDefaultCheckbox = new CheckBox
        {
            Text = "终端机启动时，投屏开关默认开启",
            AutoSize = true,
            Location = new Point(16, 16),
            Checked = settings.CastSwitchDefaultOn,
        };
        var generalNote = new Label
        {
            Text = "更改后需要重启终端机才能生效 —— 这是启动时的默认值，不会改变当前正在运行的开关状态。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(16, 44),
        };
        var generalTab = new TabPage("通用");
        generalTab.Controls.AddRange(new Control[] { _castSwitchDefaultCheckbox, generalNote });

        // --- 显示 ---
        var monitorLabel = new Label { Text = "扩展屏选择：", AutoSize = true, Location = new Point(16, 20) };
        _monitorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 44), Width = 360 };
        PopulateMonitorComboBox(settings.PreferredMonitorDeviceName);
        var displayNote = new Label
        {
            Text = "更改后需要重启终端机才能生效。下面的列表只在终端机主界面本次启动后第一次打开本面板时\n" +
                   "枚举一次——MainWindow把每个面板实例都长期复用，之后不会重新枚举，所以如果启动后又\n" +
                   "插拔了显示器，需要重启整个终端机主界面才能在这里看到最新的显示器列表。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(16, 76),
        };
        var displayTab = new TabPage("显示");
        displayTab.Controls.AddRange(new Control[] { monitorLabel, _monitorComboBox, displayNote });

        // --- 播放行为 ---
        _defaultStayDurationEnabledCheckbox = new CheckBox
        {
            Text = "为没有单独设置停留时长的文件启用默认停留时长",
            AutoSize = true,
            Location = new Point(16, 16),
            Checked = settings.DefaultStayDurationSeconds.HasValue,
        };
        _defaultStayDurationSeconds = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3600,
            Value = Math.Clamp(settings.DefaultStayDurationSeconds ?? 10, 1, 3600),
            Location = new Point(36, 44),
            Width = 80,
            Enabled = settings.DefaultStayDurationSeconds.HasValue,
        };
        var secondsLabel = new Label { Text = "秒", AutoSize = true, Location = new Point(122, 47) };
        _defaultStayDurationEnabledCheckbox.CheckedChanged += (_, _) =>
            _defaultStayDurationSeconds.Enabled = _defaultStayDurationEnabledCheckbox.Checked;
        var playbackNote = new Label
        {
            Text = "关闭时保留原有行为：没有单独设置停留时长的图片/文档会一直停留，直到手动切换到下一项。\n" +
                   "单个文件自己设置的停留时长（活动面板里配置）始终优先于这里的默认值。立即生效，无需重启。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(16, 80),
        };
        var playbackTab = new TabPage("播放行为");
        playbackTab.Controls.AddRange(new Control[]
        {
            _defaultStayDurationEnabledCheckbox, _defaultStayDurationSeconds, secondsLabel, playbackNote,
        });

        // --- 网络与设备 ---
        var deviceNameLabel = new Label { Text = "设备名称（其他设备发现/配对本机时看到的名字）：", AutoSize = true, Location = new Point(16, 20) };
        _deviceNameTextBox = new TextBox { Text = _identity.DeviceName, Location = new Point(16, 44), Width = 280 };
        var saveDeviceNameButton = new Button { Text = "保存设备名称", Location = new Point(304, 43), Width = 100 };
        saveDeviceNameButton.Click += OnSaveDeviceNameClick;
        var networkNote = new Label
        {
            Text = "重命名会立即生效——下一次广播的beacon就会带上新名字，不需要重启。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(16, 76),
        };
        var networkTab = new TabPage("网络与设备");
        networkTab.Controls.AddRange(new Control[] { deviceNameLabel, _deviceNameTextBox, saveDeviceNameButton, networkNote });

        // --- 关于 ---
        var aboutLabel = new Label
        {
            Text = $"EveryStage 终端机\n\n设备ID：{_identity.DeviceId}\n\n" +
                   "所有发现/配对/投屏数据仅在局域网内传输，不上传任何遥测或使用数据。",
            AutoSize = true,
            Location = new Point(16, 16),
        };
        var aboutTab = new TabPage("关于");
        aboutTab.Controls.Add(aboutLabel);

        tabs.TabPages.AddRange(new[] { generalTab, displayTab, playbackTab, networkTab, aboutTab });

        var saveSettingsButton = new Button { Text = "保存设置", Dock = DockStyle.Left, Width = 120, Height = 32 };
        saveSettingsButton.Click += OnSaveSettingsClick;
        _savedLabel = new Label { ForeColor = Color.SeaGreen, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        bottomBar.Controls.Add(_savedLabel);
        bottomBar.Controls.Add(saveSettingsButton);

        Controls.Add(tabs);
        Controls.Add(bottomBar);
    }

    private void PopulateMonitorComboBox(string? preferredDeviceName)
    {
        _monitorComboBox.Items.Clear();
        _monitorComboBox.Items.Add(new MonitorComboItem(AutoSelectMonitor, "自动选择（第一个非主屏）"));

        foreach (var monitor in MonitorService.GetAll().Where(m => !m.IsPrimary))
        {
            string label = $"{monitor.DeviceName} ({monitor.Bounds.Width}x{monitor.Bounds.Height} @ {monitor.Bounds.X},{monitor.Bounds.Y})";
            _monitorComboBox.Items.Add(new MonitorComboItem(monitor.DeviceName, label));
        }

        int selectedIndex = 0;
        for (int i = 0; i < _monitorComboBox.Items.Count; i++)
        {
            if (((MonitorComboItem)_monitorComboBox.Items[i]!).DeviceName == (preferredDeviceName ?? AutoSelectMonitor))
            {
                selectedIndex = i;
                break;
            }
        }
        _monitorComboBox.SelectedIndex = selectedIndex;
    }

    private void OnSaveSettingsClick(object? sender, EventArgs e)
    {
        var selectedMonitor = (MonitorComboItem?)_monitorComboBox.SelectedItem;
        var updated = new AppSettings
        {
            CastSwitchDefaultOn = _castSwitchDefaultCheckbox.Checked,
            PreferredMonitorDeviceName = selectedMonitor is { DeviceName: AutoSelectMonitor } or null
                ? null
                : selectedMonitor.DeviceName,
            DefaultStayDurationSeconds = _defaultStayDurationEnabledCheckbox.Checked
                ? (int)_defaultStayDurationSeconds.Value
                : null,
        };

        _settingsStore.Save(updated);
        _savedLabel.Text = "已保存。";
    }

    private void OnSaveDeviceNameClick(object? sender, EventArgs e)
    {
        string name = _deviceNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "设备名称不能为空。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _identity.DeviceName = name;
        _identity.Save();
        _savedLabel.Text = "设备名称已保存。";
    }

    private sealed record MonitorComboItem(string DeviceName, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
