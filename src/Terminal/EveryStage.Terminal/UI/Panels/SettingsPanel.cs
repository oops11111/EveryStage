using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using System.Diagnostics;

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
    private readonly ComboBox _themeComboBox;
    private readonly Label _diagnosticsLabel;

    // Sentinel for the ComboBox's "自动选择" item — no real MonitorInfo.DeviceName is ever an empty
    // string, so this can't collide with an actual monitor.
    private const string AutoSelectMonitor = "";

    public SettingsPanel(SettingsStore settingsStore, DeviceIdentity identity)
    {
        _settingsStore = settingsStore;
        _identity = identity;
        Dock = DockStyle.Fill;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = ModernUi.Background,
            ForeColor = ModernUi.Text,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(112, 34),
            SizeMode = TabSizeMode.Fixed,
        };
        tabs.DrawItem += (_, e) =>
        {
            bool selected = e.Index == tabs.SelectedIndex;
            var bounds = tabs.GetTabRect(e.Index);
            using var background = new SolidBrush(selected ? ModernUi.Accent : ModernUi.SurfaceRaised);
            e.Graphics.FillRectangle(background, bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, bounds,
                selected ? Color.White : ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        var settings = _settingsStore.Current;

        // --- 通用 ---
        _castSwitchDefaultCheckbox = new CheckBox
        {
            Text = "终端机启动时，投屏开关默认开启",
            AutoSize = true,
            Location = new Point(16, 16),
            Checked = settings.CastSwitchDefaultOn,
        };
        var themeLabel = new Label { Text = "界面主题：", AutoSize = true, Location = new Point(16, 78) };
        _themeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 100), Width = 180 };
        ModernUi.StyleComboBox(_themeComboBox);
        _themeComboBox.Items.AddRange(new object[] { "白色系", "黑色系", "高科技系" });
        _themeComboBox.SelectedIndex = Math.Clamp((int)settings.Theme, 0, _themeComboBox.Items.Count - 1);
        var generalNote = new Label
        {
            Text = "更改后需要重启终端机才能生效 —— 这是启动时的默认值，不会改变当前正在运行的开关状态。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Location = new Point(16, 44),
        };
        var generalTab = new TabPage("通用");
        generalTab.Controls.AddRange(new Control[] { _castSwitchDefaultCheckbox, generalNote, themeLabel, _themeComboBox });

        // --- 显示 ---
        var monitorLabel = new Label { Text = "扩展屏选择：", AutoSize = true, Location = new Point(16, 20) };
        _monitorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 44), Width = 360 };
        ModernUi.StyleComboBox(_monitorComboBox);
        PopulateMonitorComboBox(settings.PreferredMonitorDeviceName);
        var displayNote = new Label
        {
            Text = "选择本身更改后需要重启终端机才能生效；但下面的显示器列表现在每次切换到本面板都会重新\n" +
                   "枚举一次（见 Refresh_()），不再需要重启整个终端机主界面才能看到刚插拔的显示器。",
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
        ModernUi.StyleButton(saveDeviceNameButton);
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

        // --- 诊断 ---
        _diagnosticsLabel = new Label { AutoSize = true, Location = new Point(16, 16) };
        var refreshDiagnosticsButton = new Button { Text = "刷新诊断", Location = new Point(16, 180), Width = 100 };
        ModernUi.StyleButton(refreshDiagnosticsButton);
        refreshDiagnosticsButton.Click += (_, _) => RefreshDiagnostics();
        var exportLogsButton = new Button { Text = "导出日志...", Location = new Point(124, 180), Width = 100 };
        ModernUi.StyleButton(exportLogsButton);
        exportLogsButton.Click += OnExportLogsClick;
        var diagnosticsTab = new TabPage("诊断");
        diagnosticsTab.Controls.AddRange(new Control[] { _diagnosticsLabel, refreshDiagnosticsButton, exportLogsButton });
        RefreshDiagnostics();

        tabs.TabPages.AddRange(new[] { generalTab, displayTab, playbackTab, networkTab, diagnosticsTab, aboutTab });
        foreach (TabPage page in tabs.TabPages)
        {
            page.BackColor = ModernUi.Background;
            page.ForeColor = ModernUi.Text;
            page.Padding = new Padding(2);
        }

        var saveSettingsButton = new Button { Text = "保存设置", Dock = DockStyle.Left, Width = 120, Height = 32 };
        ModernUi.StyleButton(saveSettingsButton, primary: true);
        saveSettingsButton.Click += OnSaveSettingsClick;
        _savedLabel = new Label { ForeColor = Color.SeaGreen, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = ModernUi.Background };
        bottomBar.Controls.Add(_savedLabel);
        bottomBar.Controls.Add(saveSettingsButton);

        Controls.Add(tabs);
        Controls.Add(bottomBar);
    }

    /// <summary>Call whenever this panel becomes the visible one (<c>MainWindow.ShowPanel</c>) — same
    /// convention as <c>FilesPanel.Refresh_()</c>/<c>DevicesPanel.Refresh_()</c> (trailing underscore
    /// to avoid colliding with <see cref="Control.Refresh"/>, which repaints rather than reloads
    /// data). Only the "显示"标签页's monitor list actually needs this: it's the one piece of this
    /// panel's UI state built from something other than <see cref="_settingsStore"/>/<see cref="_identity"/>
    /// (live hardware, via <see cref="MonitorService.GetAll"/>), so it's the only part that could ever
    /// go stale purely from time passing while this panel instance sits reused-but-not-visible — see
    /// this class's history for why "枚举一次，永不刷新" was a real bug, not a hypothetical one.
    ///
    /// Re-enumerating doesn't just re-run <see cref="PopulateMonitorComboBox"/> with the last-SAVED
    /// preference (that would silently discard whatever the user has picked in this dropdown but not
    /// yet clicked "保存设置" for, every single time they navigate away and back) — it re-populates
    /// around whatever is CURRENTLY selected in the combo box instead, so an unsaved in-progress
    /// choice survives a refresh as long as that monitor is still connected, and only resets to
    /// "自动选择" if the selected monitor genuinely disappeared.
    ///
    /// NOTE: this assumes <see cref="MonitorService.GetAll"/> (backed by WinForms'
    /// <see cref="Screen.AllScreens"/>) actually returns freshly-enumerated hardware on each call
    /// rather than some internal cache that only invalidates on a real display-change notification —
    /// .NET's own docs describe <c>Screen</c> as listening for <c>WM_DISPLAYCHANGE</c> to invalidate
    /// its cache, which should make repeated calls correct in a normal WinForms message-pump app like
    /// this one, but this sandbox has no way to verify that behavior against a real monitor
    /// unplug/replug on an actual Windows machine.</summary>
    public void Refresh_()
    {
        string? currentSelection = (_monitorComboBox.SelectedItem as MonitorComboItem)?.DeviceName;
        PopulateMonitorComboBox(currentSelection ?? _settingsStore.Current.PreferredMonitorDeviceName);
        RefreshDiagnostics();
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
            Theme = (AppTheme)Math.Max(0, _themeComboBox.SelectedIndex),
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

    private void RefreshDiagnostics()
    {
        long? logBytes = TryGetLogBytes();
        _diagnosticsLabel.Text =
            $"应用版本：{Application.ProductVersion}\n" +
            $"运行时：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
            $"操作系统：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"进程运行：{DateTime.Now - Process.GetCurrentProcess().StartTime:g}\n" +
            $"显示器数量：{Screen.AllScreens.Length}\n" +
            $"日志占用：{(logBytes.HasValue ? $"{logBytes.Value / 1024d / 1024d:F2} MB" : "无法读取")}\n" +
            $"日志目录：{LogPaths.DefaultRoot}";
    }

    private static long? TryGetLogBytes()
    {
        try
        {
            return Directory.Exists(LogPaths.DefaultRoot)
                ? Directory.EnumerateFiles(LogPaths.DefaultRoot, "*.log", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnExportLogsClick(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"EveryStage-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            int count = LogExporter.Export(dialog.FileName);
            _savedLabel.Text = $"已导出 {count} 个日志文件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "日志导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
