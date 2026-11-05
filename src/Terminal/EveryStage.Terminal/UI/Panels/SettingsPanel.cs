using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 设置面板: "二级分类：通用 / 显示 / 播放行为 / 网络与设备 / 关于". PLANNING.md
/// names only these five category labels — the specific fields inside each are this repository's
/// own choices (see <see cref="AppSettings"/>'s doc comment for where they came from), not a
/// PLANNING.md spec. Replaces the honest <c>NotImplementedPanel</c> placeholder this used to be.
///
/// Every change is written at once (no 「保存设置」): a control's change saves <see cref="AppSettings"/>
/// through <see cref="SettingsStore"/>; a failed save is reported and the page shows the stored value
/// again. Risky items (扩展屏, 音频输出设备, the global 急停 key) ask first and then offer 撤销 for 10 s.
/// 设备名称 is saved on leaving the box or Enter through <see cref="DeviceIdentity.Save"/> — a different
/// store. 「恢复默认」 resets AppSettings and the hotkeys (not the device name or pairings); settings can be
/// exported to / imported from a file (设置 + 热键; no media, pairings or scenarios).
/// </summary>
public sealed class SettingsPanel : UserControl
{
    private readonly SettingsStore _settingsStore;
    private readonly DeviceIdentity _identity;

    private readonly CheckBox _castSwitchDefaultCheckbox;
    private readonly ComboBox _monitorComboBox;
    private readonly ComboBox _audioOutputComboBox;
    private bool _refreshingAudioOutputs;
    private readonly CheckBox _defaultStayDurationEnabledCheckbox;
    private readonly NumericUpDown _defaultStayDurationSeconds;
    private readonly TextBox _deviceNameTextBox;
    private readonly Label _savedLabel;
    private readonly ComboBox _themeComboBox;
    private readonly Label _diagnosticsLabel;
    private readonly ComboBox _transitionComboBox;
    private readonly NumericUpDown _transitionMilliseconds;
    private readonly ComboBox _slideTransitionSource;
    private readonly SlideTransitionPicker _slideTransitionPicker;
    private readonly CheckBox _keepMorph;
    private readonly CheckBox _keepAutoAdvance;
    private readonly CheckBox _transitionOnBlack;
    private readonly ComboBox _completionComboBox;
    private readonly CheckBox _httpControlCheckbox;
    private static readonly (CompletionAction Action, string Label)[] CompletionChoices =
    {
        (CompletionAction.HoldOnLastFrame, "停在最后画面（定格）"),
        (CompletionAction.Loop, "循环播放"),
        (CompletionAction.NextItem, "自动下一项"),
        (CompletionAction.BlackScreen, "切黑场"),
    };
    private readonly Dictionary<HotkeyAction, HotkeyCaptureButton> _hotkeyButtons = new();
    private CheckBox? _globalEmergency;
    private bool _loading;

    /// <summary>Shows a toast in the main window (the 10 s 撤销 offer after a risky change).</summary>
    public Func<string, ToastSeverity, ToastAction[], ToastNotification>? ShowToast { get; set; }

    // Sentinel for the ComboBox's "自动选择" item — no real MonitorInfo.DeviceName is ever an empty
    // string, so this can't collide with an actual monitor.
    private const string AutoSelectMonitor = "";

    public SettingsPanel(SettingsStore settingsStore, DeviceIdentity identity)
    {
        _settingsStore = settingsStore;
        _identity = identity;
        Dock = DockStyle.Fill;

        var settings = _settingsStore.Current;
        // The last save result, beside the page title (there is no 「保存设置」: every change saves at once).
        _savedLabel = ModernUi.SetForegroundRole(new Label { AutoSize = true, Location = new Point(84, 18), BackColor = Color.Transparent }, UiForegroundRole.Success);

        // One scrolling page of sections (design): 外观 · 通用 · 输出与播放 · 网络与设备 · 诊断 · 关于.
        // Each row: title + description on the left, its control(s) on the right, an effect tag
        // (即时生效 / 保存后生效 / 重启后生效) beside the title.
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 16),
        };
        var sections = new List<SettingsSection>();
        SettingsSection Section(string title, string? effect = null, UiForegroundRole effectRole = UiForegroundRole.Muted)
        {
            var section = new SettingsSection(title, effect, effectRole);
            sections.Add(section);
            page.Controls.Add(section);
            return section;
        }
        page.Resize += (_, _) =>
        {
            int width = Math.Max(LogicalToDeviceUnits(420), page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            page.SuspendLayout();
            foreach (var section in sections) section.FitToWidth(width);
            page.ResumeLayout();
            page.PerformLayout();
        };

        // --- 外观 ---
        _themeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        _themeComboBox.Items.AddRange(new object[] { "浅色", "深色", "舞台", "跟随系统" });
        _themeComboBox.SelectedIndex = Math.Clamp((int)settings.Theme, 0, _themeComboBox.Items.Count - 1);
        var themeCards = new FlowLayoutPanel { Height = 112, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        foreach (var (name, theme) in new[] { ("跟随系统", AppTheme.System), ("浅色", AppTheme.Light), ("深色", AppTheme.Dark), ("舞台", AppTheme.Tech) })
        {
            var card = new ThemePreviewCard(name, (int)theme, () => _themeComboBox.SelectedIndex, index =>
            {
                SelectTheme(index);
                foreach (Control control in themeCards.Controls) control.Invalidate();
            })
            {
                Size = new Size(148, 104),
                Margin = new Padding(0, 0, 12, 0),
            };
            themeCards.Controls.Add(card);
        }
        var appearance = Section("外观", "即时生效", UiForegroundRole.Accent);
        appearance.AddBlock(themeCards);
        appearance.AddNote("「跟随系统」跟随 Windows 应用的浅色或深色模式，切换时即时生效；不跟随系统强调色。");
        appearance.Controls.Add(_themeComboBox);

        // --- 通用 ---
        _castSwitchDefaultCheckbox = new CheckBox { Text = "开启", AutoSize = true, Checked = settings.CastSwitchDefaultOn };
        Section("通用").AddRow("启动时投屏开关默认开启", "只设置终端机启动时的默认值，不改变当前开关（当前开关在「设备」页和托盘菜单）",
            "重启后生效", UiForegroundRole.Muted, _castSwitchDefaultCheckbox);
        BuildHotkeySection(Section, settings);

        // --- 输出与播放 ---
        _monitorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        ModernUi.StyleComboBox(_monitorComboBox);
        PopulateMonitorComboBox(settings.PreferredMonitorDeviceName);
        _defaultStayDurationEnabledCheckbox = new CheckBox { Text = "启用", AutoSize = true, Checked = settings.DefaultStayDurationSeconds.HasValue };
        _defaultStayDurationSeconds = new NumericUpDown
        {
            Minimum = 1, Maximum = 3600, Width = 70,
            Value = Math.Clamp(settings.DefaultStayDurationSeconds ?? 10, 1, 3600),
            Enabled = settings.DefaultStayDurationSeconds.HasValue,
        };
        var secondsLabel = ModernUi.SetForegroundRole(new Label { Text = "秒", AutoSize = true }, UiForegroundRole.Muted);
        _defaultStayDurationEnabledCheckbox.CheckedChanged += (_, _) =>
            _defaultStayDurationSeconds.Enabled = _defaultStayDurationEnabledCheckbox.Checked;
        var output = Section("输出与播放");
        output.AddRow("扩展屏", "终端机输出到哪个显示器；列表每次打开本页时重新枚举。更改前会确认，10 秒内可撤销", "重启后生效", UiForegroundRole.Muted, _monitorComboBox);
        _audioOutputComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = "音频输出设备" };
        ModernUi.StyleComboBox(_audioOutputComboBox);
        PopulateAudioOutputs(settings.AudioOutputDeviceId);
        _audioOutputComboBox.SelectedIndexChanged += OnAudioOutputChanged;
        output.AddRow("音频输出设备", "视频声音和背景音乐共用设备；新播放生效，当前播放不会切换。设备不可用时，新播放回退系统默认设备。更改前会确认，10 秒内可撤销。",
            "新播放生效", UiForegroundRole.Warning, _audioOutputComboBox);
        output.AddRow("图片 / 文档默认停留时长", "没有单独设置停留时长的项目停留多久后进入下一项；关闭则一直停留到手动切换",
            "下一项生效", UiForegroundRole.Accent, _defaultStayDurationEnabledCheckbox, _defaultStayDurationSeconds, secondsLabel);
        _completionComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "播完后（默认）" };
        _completionComboBox.Items.AddRange(CompletionChoices.Select(c => (object)c.Label).ToArray());
        _completionComboBox.SelectedIndex = Math.Max(0, Array.FindIndex(CompletionChoices, c => c.Action == settings.DefaultCompletionAction));
        ModernUi.StyleComboBox(_completionComboBox);
        output.AddRow("播完后（默认）", "新导入的文件播完后怎么办：停在最后画面、循环、自动下一项或切黑场（黑场时大屏仍在输出）。" +
            "已有文件和流程保持原来的设置；每个文件仍可在属性里单独设置",
            "新导入生效", UiForegroundRole.Accent, _completionComboBox);
        _transitionComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        _transitionComboBox.Items.AddRange(new object[] { "直切", "叠化", "经黑场" });
        _transitionComboBox.SelectedIndex = (int)settings.Transition;
        ModernUi.StyleComboBox(_transitionComboBox);
        _transitionMilliseconds = new NumericUpDown { Minimum = 100, Maximum = 3000, Increment = 100, Width = 70, Value = Math.Clamp(settings.TransitionMilliseconds, 100, 3000) };
        output.AddRow("大屏过场", "大屏从一项切到下一项时的过渡；PPT 放映启动较慢时，过场会等到放映第一帧出现再显示。流程里每一项可以单独设置",
            "下次切换生效", UiForegroundRole.Accent, _transitionComboBox, _transitionMilliseconds, ModernUi.SetForegroundRole(new Label { Text = "毫秒", AutoSize = true }, UiForegroundRole.Muted));
        output.AddRow("PPT 没有扩展屏时", "原生放映（含动画）只在扩展屏上进行，避免占用本机屏幕；没有扩展屏时以静态幻灯片预览", null, null,
            ModernUi.SetForegroundRole(new Label { Text = "静态幻灯片", AutoSize = true }, UiForegroundRole.Text));

        // --- PPT 翻页过场 ---
        _slideTransitionSource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        _slideTransitionSource.Items.AddRange(new object[] { "统一过场（推荐）", "保留 PPT 原转场" });
        _slideTransitionSource.SelectedIndex = settings.UnifySlideTransitions ? 0 : 1;
        ModernUi.StyleComboBox(_slideTransitionSource);
        _slideTransitionPicker = new SlideTransitionPicker(settings.SlideTransition, settings.SlideTransitionDirection, settings.SlideTransitionMilliseconds);
        _slideTransitionSource.SelectedIndexChanged += (_, _) => _slideTransitionPicker.Enabled = _slideTransitionSource.SelectedIndex == 0;
        _slideTransitionPicker.Enabled = settings.UnifySlideTransitions;
        _keepMorph = new CheckBox { Text = "保留", AutoSize = true, Checked = settings.KeepMorphTransitions };
        _keepAutoAdvance = new CheckBox { Text = "保留", AutoSize = true, Checked = settings.KeepAutoAdvanceTransitions };
        _transitionOnBlack = new CheckBox { Text = "使用", AutoSize = true, Checked = settings.TransitionOnBlackScreen };
        var slides = Section("PPT 翻页过场", "下次翻页生效", UiForegroundRole.Accent);
        slides.AddRow("转场来源", "统一后，所有演示文稿的翻页动画一致（静态幻灯片和大屏原生放映都适用）；单个文件仍可在「文档播放设置」里改回原转场",
            null, null, _slideTransitionSource);
        slides.AddBlock(_slideTransitionPicker);
        slides.AddRow("「平滑（Morph）」转场的页面", "平滑转场让同一物体在两页之间移动，属于内容本身，替换后画面会跳变", null, null, _keepMorph);
        slides.AddRow("自动换片的页面", "PPT 里设置了「N 秒后自动换片」的页，换页时机无法提前得知，保留原转场更自然", null, null, _keepAutoAdvance);
        slides.AddRow("黑屏 / 白屏切换", "按黑屏、白屏和恢复时，也用所选动画过渡", null, null, _transitionOnBlack);
        slides.AddNote("页内动画（逐条出现的文字、图表）不受影响，仍按原 PPT 播放。");

        // --- 网络与设备 ---
        _deviceNameTextBox = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(
            new TextBox { Text = _identity.DeviceName, Width = 220 }, UiForegroundRole.Text), UiBackgroundRole.Surface);
        _deviceNameTextBox.Leave += (_, _) => SaveDeviceName();
        _deviceNameTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SaveDeviceName();
        };
        // 设置 · 网络 = configuration; the 设备 page shows what is happening now (paired devices, casts).
        var network = Section("网络");
        network.AddRow("设备名称", "投屏机列表中显示的名字；离开输入框或按 Enter 即保存，下一次广播就会带上新名字", "即时生效", UiForegroundRole.Accent,
            _deviceNameTextBox);
        network.AddRow("投屏端口", $"UDP {EveryStage.Discovery.DiscoveryProtocol.Port}（发现与控制）、{EveryStage.Discovery.DiscoveryProtocol.VideoRtpPort}（视频）、" +
            $"{EveryStage.Discovery.DiscoveryProtocol.AudioRtpPort}（音频）。固定不可修改；安装程序已添加防火墙规则", null, null,
            ModernUi.SetForegroundRole(new Label { Text = "只读", AutoSize = true }, UiForegroundRole.Muted));
        _httpControlCheckbox = new CheckBox { Text = "启用", AutoSize = true, Checked = settings.HttpControlEnabled, AccessibleName = "HTTP 外控" };
        _httpControlCheckbox.CheckedChanged += (_, _) => Commit(s => s.HttpControlEnabled = _httpControlCheckbox.Checked, "HTTP 外控");
        network.AddRow("HTTP 外控（仅本机）", $"供本机的中控软件使用：http://127.0.0.1:{EveryStage.Terminal.Remote.HttpControlServer.DefaultPort}，" +
            "GET /status 查询状态，POST /emergency-stop 急停（等同「停止输出」，没有撤销）。每个请求都要带令牌（Authorization: Bearer）；只监听本机，局域网无法访问",
            "即时生效", UiForegroundRole.Accent, _httpControlCheckbox);
        var copyToken = new RoundedActionButton { Text = "复制令牌", Width = 92, Height = 30 };
        ModernUi.StyleButton(copyToken);
        copyToken.Click += (_, _) =>
        {
            try { Clipboard.SetText(EveryStage.Terminal.Remote.HttpControlToken.Current()); _savedLabel.Text = "令牌已复制"; }
            catch (System.Runtime.InteropServices.ExternalException) { _savedLabel.Text = "剪贴板被占用，请重试"; }
        };
        var resetToken = new RoundedActionButton { Text = "重置令牌", Width = 92, Height = 30 };
        ModernUi.StyleButton(resetToken);
        resetToken.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "重置后旧令牌立即失效，正在使用它的中控需要换成新令牌。继续？", "重置令牌",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            EveryStage.Terminal.Remote.HttpControlToken.Reset();
            _savedLabel.Text = "令牌已重置";
        };
        network.AddRow("访问令牌", "令牌保存在受限的数据目录中，只显示不保存在设置文件里；泄露时请重置", null, null, copyToken, resetToken);
        network.AddNote("投屏的媒体内容经过设备认证，但没有加密：局域网内的其他人可能截获画面和声音。请只在受信任的局域网中使用。");

        // --- 诊断 ---
        _diagnosticsLabel = ModernUi.SetForegroundRole(new Label { AutoSize = true, BackColor = Color.Transparent }, UiForegroundRole.Muted);
        var refreshDiagnosticsButton = new RoundedActionButton { Text = "刷新诊断", Width = 96, Height = 30 };
        ModernUi.StyleButton(refreshDiagnosticsButton);
        refreshDiagnosticsButton.Click += (_, _) => RefreshDiagnostics();
        var exportLogsButton = new RoundedActionButton { Text = "导出日志...", Width = 104, Height = 30 };
        ModernUi.StyleButton(exportLogsButton);
        exportLogsButton.Click += OnExportLogsClick;
        var diagnostics = Section("诊断");
        diagnostics.AddBlock(_diagnosticsLabel);
        diagnostics.AddButtons(refreshDiagnosticsButton, exportLogsButton);
        RefreshDiagnostics();

        // --- 备份与恢复 ---
        var restoreDefaults = new RoundedActionButton { Text = "恢复默认", Width = 96, Height = 30 };
        ModernUi.StyleButton(restoreDefaults);
        restoreDefaults.Click += (_, _) => RestoreDefaults();
        var exportSettings = new RoundedActionButton { Text = "导出设置...", Width = 104, Height = 30 };
        ModernUi.StyleButton(exportSettings);
        exportSettings.Click += (_, _) => ExportSettings();
        var importSettings = new RoundedActionButton { Text = "导入设置...", Width = 104, Height = 30 };
        ModernUi.StyleButton(importSettings);
        importSettings.Click += (_, _) => ImportSettings();
        var backup = Section("备份与恢复");
        backup.AddNote("所有设置改动都会立即保存。「恢复默认」把设置和热键恢复为出厂值（设备名称、配对设备不变）。" +
            "「导出设置」只包含设置和热键，不含媒体文件、配对和方案；导入时保留本机的扩展屏和音频设备选择。");
        backup.AddButtons(restoreDefaults, exportSettings, importSettings);

        // --- 关于 ---
        var openDataButton = new RoundedActionButton { Text = "打开数据目录", Width = 116, Height = 30 };
        ModernUi.StyleButton(openDataButton);
        openDataButton.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDirectory.Root}\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, $"无法打开数据目录：{ex.Message}", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var about = Section("关于");
        about.AddBlock(ModernUi.SetForegroundRole(new Label
        {
            AutoSize = true, BackColor = Color.Transparent,
            Text = $"EveryStage 终端机 {Application.ProductVersion}\n设备 ID：{_identity.DeviceId}\n数据目录：{DataDirectory.Root}\n" +
                   "所有发现 / 配对 / 投屏数据只在局域网内传输，不上传任何遥测或使用数据。\n\n" +
                   "安全说明：配对后，投屏控制消息和媒体数据都用设备密钥认证（防伪造、防重放），但媒体内容本身没有加密，" +
                   "同一局域网内能抓包的人可以看到画面、听到声音。请只在受信任的局域网中使用。",
        }, UiForegroundRole.Muted));
        about.AddButtons(openDataButton);

        var heading = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        heading.Controls.Add(ModernUi.SetForegroundRole(new Label
        {
            Text = "设置", AutoSize = true, Location = new Point(0, 8),
            Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent,
        }, UiForegroundRole.Text));
        heading.Controls.Add(_savedLabel);

        // Dock order: the last Top control added sits highest, so heading goes in last.
        Controls.Add(page);
        Controls.Add(heading);
        WireInstantSave();
    }

    /// <summary>Every control saves its own field at once.</summary>
    private void WireInstantSave()
    {
        _castSwitchDefaultCheckbox.CheckedChanged += (_, _) =>
            Commit(s => s.CastSwitchDefaultOn = _castSwitchDefaultCheckbox.Checked, "启动时投屏开关默认值");
        _monitorComboBox.SelectedIndexChanged += (_, _) =>
        {
            var selected = _monitorComboBox.SelectedItem as MonitorComboItem;
            string? name = selected is { DeviceName: AutoSelectMonitor } or null ? null : selected.DeviceName;
            string? previous = _settingsStore.Current.PreferredMonitorDeviceName;
            if (name == previous) return;
            CommitDangerous(s => s.PreferredMonitorDeviceName = name, s => s.PreferredMonitorDeviceName = previous, "扩展屏",
                $"把扩展屏改为「{selected?.ToString() ?? "自动选择"}」？\n大屏会在显示设置变化或重启后绑定到新的显示器。");
        };
        _defaultStayDurationEnabledCheckbox.CheckedChanged += (_, _) => CommitStayDuration();
        _defaultStayDurationSeconds.ValueChanged += (_, _) => CommitStayDuration();
        _completionComboBox.SelectedIndexChanged += (_, _) =>
            Commit(s => s.DefaultCompletionAction = CompletionChoices[Math.Max(0, _completionComboBox.SelectedIndex)].Action, "默认「播完后」");
        _transitionComboBox.SelectedIndexChanged += (_, _) =>
            Commit(s => s.Transition = (TransitionStyle)Math.Max(0, _transitionComboBox.SelectedIndex), "大屏过场");
        _transitionMilliseconds.ValueChanged += (_, _) =>
            Commit(s => s.TransitionMilliseconds = (int)_transitionMilliseconds.Value, "大屏过场时长");
        _slideTransitionSource.SelectedIndexChanged += (_, _) =>
            Commit(s => s.UnifySlideTransitions = _slideTransitionSource.SelectedIndex == 0, "PPT 转场来源");
        _slideTransitionPicker.SelectionChanged += () => Commit(s =>
        {
            s.SlideTransition = _slideTransitionPicker.Effect;
            s.SlideTransitionDirection = _slideTransitionPicker.Direction;
            s.SlideTransitionMilliseconds = _slideTransitionPicker.Milliseconds;
        }, "PPT 翻页过场");
        _keepMorph.CheckedChanged += (_, _) => Commit(s => s.KeepMorphTransitions = _keepMorph.Checked, "平滑转场设置");
        _keepAutoAdvance.CheckedChanged += (_, _) => Commit(s => s.KeepAutoAdvanceTransitions = _keepAutoAdvance.Checked, "自动换片设置");
        _transitionOnBlack.CheckedChanged += (_, _) => Commit(s => s.TransitionOnBlackScreen = _transitionOnBlack.Checked, "黑屏 / 白屏过场");
    }

    private void CommitStayDuration()
    {
        _defaultStayDurationSeconds.Enabled = _defaultStayDurationEnabledCheckbox.Checked;
        Commit(s => s.DefaultStayDurationSeconds = _defaultStayDurationEnabledCheckbox.Checked ? (int)_defaultStayDurationSeconds.Value : null, "默认停留时长");
    }

    /// <summary>Saves one change at once. On failure the operator is told and the page shows the stored
    /// values again. No-op while the page itself is loading values.</summary>
    private bool Commit(Action<AppSettings> change, string what)
    {
        if (_loading) return true;
        var updated = _settingsStore.Current.Clone();
        change(updated);
        try { _settingsStore.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"{what}未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ReloadFromSettings();
            return false;
        }
        _savedLabel.Text = $"{what}已保存";
        return true;
    }

    /// <summary>A risky change (扩展屏, 音频输出设备, the global 急停 key): ask first, save, then offer 撤销
    /// for 10 seconds (<paramref name="undo"/> puts back only this field).</summary>
    private bool CommitDangerous(Action<AppSettings> change, Action<AppSettings> undo, string what, string question)
    {
        if (_loading) return true;
        if (MessageBox.Show(this, question, what, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes
            || !Commit(change, what))
        {
            ReloadFromSettings();
            return false;
        }
        if (ShowToast is { } show)
        {
            var toast = show($"{what}已更改。10 秒内可撤销。", ToastSeverity.Info, new[]
            {
                new ToastAction("撤销", () =>
                {
                    if (Commit(undo, what)) _savedLabel.Text = $"{what}已撤销";
                    ReloadFromSettings();
                }),
            });
            var expiry = new System.Windows.Forms.Timer { Interval = 10_000 };
            expiry.Tick += (_, _) => { expiry.Dispose(); toast.Dismiss(); };
            expiry.Start();
        }
        return true;
    }

    /// <summary>Puts the stored settings back into every control (after a failed save, 撤销, 恢复默认 or 导入)
    /// without saving anything.</summary>
    public void ReloadFromSettings()
    {
        var settings = _settingsStore.Current;
        _loading = true;
        try
        {
            _themeComboBox.SelectedIndex = Math.Clamp((int)settings.Theme, 0, _themeComboBox.Items.Count - 1);
            _castSwitchDefaultCheckbox.Checked = settings.CastSwitchDefaultOn;
            PopulateMonitorComboBox(settings.PreferredMonitorDeviceName);
            PopulateAudioOutputs(settings.AudioOutputDeviceId);
            _defaultStayDurationEnabledCheckbox.Checked = settings.DefaultStayDurationSeconds.HasValue;
            _defaultStayDurationSeconds.Value = Math.Clamp(settings.DefaultStayDurationSeconds ?? 10, 1, 3600);
            _defaultStayDurationSeconds.Enabled = settings.DefaultStayDurationSeconds.HasValue;
            _completionComboBox.SelectedIndex = Math.Max(0, Array.FindIndex(CompletionChoices, c => c.Action == settings.DefaultCompletionAction));
            _transitionComboBox.SelectedIndex = (int)settings.Transition;
            _transitionMilliseconds.Value = Math.Clamp(settings.TransitionMilliseconds, 100, 3000);
            _slideTransitionSource.SelectedIndex = settings.UnifySlideTransitions ? 0 : 1;
            _slideTransitionPicker.SetValues(settings.SlideTransition, settings.SlideTransitionDirection, settings.SlideTransitionMilliseconds);
            _slideTransitionPicker.Enabled = settings.UnifySlideTransitions;
            _keepMorph.Checked = settings.KeepMorphTransitions;
            _keepAutoAdvance.Checked = settings.KeepAutoAdvanceTransitions;
            _transitionOnBlack.Checked = settings.TransitionOnBlackScreen;
            _httpControlCheckbox.Checked = settings.HttpControlEnabled;
            foreach (var (action, button) in _hotkeyButtons) button.Keys = settings.HotkeyFor(action);
            if (_globalEmergency != null) _globalEmergency.Checked = settings.GlobalEmergencyStopEnabled;
        }
        finally { _loading = false; }
        Invalidate(true);
    }

    /// <summary>「恢复默认」: product defaults for every setting and hotkey; the device name, pairings and
    /// this machine's 扩展屏 / 音频设备 choice are kept.</summary>
    private void RestoreDefaults()
    {
        if (MessageBox.Show(this, "把所有设置和热键恢复为默认值？\n设备名称、配对设备、扩展屏和音频设备的选择不变。", "恢复默认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var current = _settingsStore.Current;
        var defaults = new AppSettings
        {
            PreferredMonitorDeviceName = current.PreferredMonitorDeviceName,
            AudioOutputDeviceId = current.AudioOutputDeviceId,
        };
        try { _settingsStore.Save(defaults); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"未能恢复默认：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ReloadFromSettings();
        _savedLabel.Text = "已恢复默认设置";
    }

    private const string SettingsFileFormat = "EveryStage.Settings";

    private sealed class SettingsFile
    {
        public string Format { get; set; } = SettingsFileFormat;
        public string AppVersion { get; set; } = Application.ProductVersion;
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        public AppSettings? Settings { get; set; }
    }

    private static readonly System.Text.Json.JsonSerializerOptions SettingsFileJson = new() { WriteIndented = true };

    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "EveryStage 设置 (*.json)|*.json",
            FileName = $"EveryStage-设置-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(new SettingsFile { Settings = _settingsStore.Current }, SettingsFileJson));
            _savedLabel.Text = "设置已导出";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog { Filter = "EveryStage 设置 (*.json)|*.json|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AppSettings? imported;
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(dialog.FileName));
            imported = file is { Format: SettingsFileFormat } ? file.Settings : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"无法读取设置文件：{ex.Message}", "导入设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (imported == null)
        {
            MessageBox.Show(this, "这不是 EveryStage 导出的设置文件。", "导入设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (imported.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            MessageBox.Show(this, "这个设置文件来自更新版本的 EveryStage，本版本无法正确读取。请先升级。", "导入设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, "用这个文件替换当前的设置和热键？\n本机的扩展屏和音频设备选择保持不变。", "导入设置",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var current = _settingsStore.Current;
        imported.SchemaVersion = AppSettings.CurrentSchemaVersion;
        imported.PreferredMonitorDeviceName = current.PreferredMonitorDeviceName;
        imported.AudioOutputDeviceId = current.AudioOutputDeviceId;
        imported.Hotkeys ??= new(HotkeyDefaults.Codes);
        try { _settingsStore.Save(imported); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导入未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ReloadFromSettings();
        _savedLabel.Text = "设置已导入";
    }

    /// <summary>One settings section: a rounded surface card with a title (and optional effect tag),
    /// then rows / blocks stacked top to bottom. Laid out by hand in <see cref="FitToWidth"/>.</summary>
    /// <summary>设置 · 热键: the minimum show set, each recorded by pressing it; conflicts are refused,
    /// every change is saved at once.</summary>
    private void BuildHotkeySection(Func<string, string?, UiForegroundRole, SettingsSection> section, AppSettings settings)
    {
        var hotkeys = section("热键", "即时生效", UiForegroundRole.Accent);
        var globalEmergency = new CheckBox { Text = "全局注册", AutoSize = true, Checked = settings.GlobalEmergencyStopEnabled, AccessibleName = "全局急停热键" };
        _globalEmergency = globalEmergency;
        var buttons = _hotkeyButtons;
        bool reverting = false;

        bool SaveWith(Action<AppSettings> change, string what)
        {
            var updated = _settingsStore.Current.Clone();
            change(updated);
            try { _settingsStore.Save(updated); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"{what}未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        void Assign(HotkeyAction action, Keys keys)
        {
            var button = buttons[action];
            if (!Hotkeys.IsAssignable(keys))
            {
                MessageBox.Show(this, $"{Hotkeys.Format(keys)} 不能用作热键（Tab、Space、Delete、退格和单独的修饰键保留给系统与编辑）。", "热键", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var current = _settingsStore.Current;
            var map = Enum.GetValues<HotkeyAction>().ToDictionary(a => a, current.HotkeyFor);
            if (Hotkeys.ConflictWith(map, action, keys) is { } other)
            {
                MessageBox.Show(this, $"{Hotkeys.Format(keys)} 已被「{Hotkeys.Label(other)}」使用，未修改。请先给那一项换一个键。", "热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (action == HotkeyAction.EmergencyStopGlobal)
            {
                int previous = current.HotkeyCode(action);
                if (CommitDangerous(s => s.Hotkeys[action] = (int)keys, s => s.Hotkeys[action] = previous, "全局急停键",
                        $"把全局急停键改为 {Hotkeys.Format(keys)}？\n演出中请确认新键不会被误按，也没有被其他程序占用。"))
                    button.Keys = keys;
                return;
            }
            if (SaveWith(s => s.Hotkeys[action] = (int)keys, "热键")) button.Keys = keys;
        }

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var button = new HotkeyCaptureButton(settings.HotkeyFor(action)) { AccessibleName = Hotkeys.Label(action) };
            buttons[action] = button;
            button.KeyCaptured += keys => Assign(action, keys);
            var reset = new RoundedActionButton { Text = "恢复默认", Width = 84, Height = 30 };
            ModernUi.StyleButton(reset);
            reset.Click += (_, _) => Assign(action, Hotkeys.Defaults[action]);
            string description = action switch
            {
                HotkeyAction.EmergencyStopGlobal => "停止输出：大屏断开、预览与背景音乐停止，没有撤销。勾选「全局注册」后，其他程序在前台时也有效；被其他程序占用时只在本程序内有效。",
                HotkeyAction.EmergencyStopLocal => "EveryStage 窗口在前台时停止输出；歌单面板展开时先收起面板。",
                HotkeyAction.Take => "把预览中的内容（含播放位置）投到大屏。",
                HotkeyAction.PauseProgram => "暂停或继续大屏上的本地媒体。在输入框里打字时不生效。",
                HotkeyAction.PreviousItem => "流程：回到当前环节的上一项。",
                _ => "流程：当前环节的下一项；环节结束后需要先选中下一个环节。",
            };
            if (action == HotkeyAction.EmergencyStopGlobal)
                hotkeys.AddRow(Hotkeys.Label(action), description, null, null, button, globalEmergency, reset);
            else
                hotkeys.AddRow(Hotkeys.Label(action), description, null, null, button, reset);
        }
        globalEmergency.CheckedChanged += (_, _) =>
        {
            if (reverting || _loading) return;
            bool on = globalEmergency.Checked;
            bool saved = on
                ? SaveWith(s => s.GlobalEmergencyStopEnabled = true, "急停设置")
                : CommitDangerous(s => s.GlobalEmergencyStopEnabled = false, s => s.GlobalEmergencyStopEnabled = true, "全局急停",
                    "关闭全局急停？\n关闭后，只有 EveryStage 窗口在前台时急停键才有效。");
            if (saved) return;
            reverting = true;
            try { globalEmergency.Checked = _settingsStore.Current.GlobalEmergencyStopEnabled; }
            finally { reverting = false; }
        };
        var resetAll = new RoundedActionButton { Text = "全部恢复默认", Width = 120, Height = 30 };
        ModernUi.StyleButton(resetAll);
        resetAll.Click += (_, _) =>
        {
            if (!SaveWith(s => { s.Hotkeys = new(HotkeyDefaults.Codes); s.GlobalEmergencyStopEnabled = true; }, "热键")) return;
            foreach (var (action, button) in buttons) button.Keys = Hotkeys.Defaults[action];
            globalEmergency.Checked = true;
        };
        hotkeys.AddNote("Space（空格）按页面区分：在「流程」页是下一项，其他页面不处理；不能改。PDF 翻页和音频播放条自己的按键也固定，不在此列。");
        hotkeys.AddButtons(resetAll);
    }

    private sealed class SettingsSection : Panel
    {
        private readonly string _title;
        private readonly string? _effect;
        private readonly UiForegroundRole _effectRole;
        private readonly List<(Control Item, int Kind)> _items = new(); // 0 block, 1 row, 2 note, 3 buttons
        private readonly List<int> _dividers = new();

        public SettingsSection(string title, string? effect, UiForegroundRole effectRole)
        {
            _title = title;
            _effect = effect;
            _effectRole = effectRole;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            Margin = new Padding(0, 0, 0, 12);
        }

        private int S(int v) => LogicalToDeviceUnits(v);

        public void AddBlock(Control control)
        {
            Controls.Add(control);
            _items.Add((control, 0));
        }

        public void AddNote(string text) => AddBlockOf(ModernUi.SetForegroundRole(new Label { Text = text, AutoSize = false, BackColor = Color.Transparent }, UiForegroundRole.Muted), 2);

        public void AddButtons(params Control[] buttons)
        {
            var row = new FlowLayoutPanel { WrapContents = false, BackColor = Color.Transparent, Height = 36, Margin = Padding.Empty };
            foreach (var button in buttons) { button.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(button); }
            AddBlockOf(row, 3);
        }

        public void AddRow(string title, string description, string? effect, UiForegroundRole? effectRole, params Control[] controls) =>
            AddBlockOf(new SettingRow(title, description, effect, effectRole ?? UiForegroundRole.Muted, controls), 1);

        private void AddBlockOf(Control control, int kind)
        {
            Controls.Add(control);
            _items.Add((control, kind));
        }

        public void FitToWidth(int width)
        {
            Width = width;
            int x = S(18), inner = width - S(36), y = S(48);
            _dividers.Clear();
            foreach (var (item, kind) in _items)
            {
                switch (kind)
                {
                    case 1:
                        if (y > S(48)) _dividers.Add(y);
                        item.Bounds = new Rectangle(x, y, inner, item.Height);
                        ((SettingRow)item).FitToWidth(inner);
                        y += item.Height;
                        break;
                    case 2:
                        item.Bounds = new Rectangle(x, y + S(4), inner, TextRenderer.MeasureText(item.Text, item.Font, new Size(inner, 0), TextFormatFlags.WordBreak).Height + S(4));
                        y += item.Height + S(8);
                        break;
                    default:
                        item.Location = new Point(x, y + S(4));
                        if (kind == 3) item.Width = inner;
                        if (item is SlideTransitionPicker picker) picker.FitToWidth(inner);
                        if (item is FlowLayoutPanel { WrapContents: true } wrapping)
                            wrapping.Size = new Size(inner, wrapping.GetPreferredSize(new Size(inner, 0)).Height);
                        y += item.Height + S(12);
                        break;
                }
            }
            Height = y + S(8);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            var palette = ModernUi.PaletteFor(this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ActivityRowView.RoundedBox(new Rectangle(0, 0, Width - 1, Height - 1), S(10)))
            using (var fill = new SolidBrush(palette.Surface))
            using (var border = new Pen(palette.IsHighContrast ? palette.Border : ModernUi.Mix(palette.Surface, palette.Border, 0.45f)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var titleFont = new Font(ModernUi.UiFontSemibold, 11.5F);
            var titleSize = TextRenderer.MeasureText(_title, titleFont);
            TextRenderer.DrawText(g, _title, titleFont, new Point(S(18), S(14)), palette.Text);
            if (_effect != null)
                using (var effectFont = new Font(ModernUi.UiFont, 8.5F))
                    TextRenderer.DrawText(g, "● " + _effect, effectFont, new Point(S(18) + titleSize.Width + S(6), S(18)), ModernUi.Foreground(_effectRole, palette));
            using var divider = new Pen(palette.IsHighContrast ? palette.Border : ModernUi.Mix(palette.Surface, palette.Border, 0.35f));
            foreach (int y in _dividers) g.DrawLine(divider, S(18), y, Width - S(18), y);
        }
    }

    /// <summary>A settings row: title (+ effect tag) and a muted description on the left, controls
    /// right-aligned on the right. Taller when the description wraps.</summary>
    private sealed class SettingRow : Panel
    {
        private readonly Label _title;
        private readonly Label _description;
        private readonly Control[] _controls;

        public SettingRow(string title, string description, string? effect, UiForegroundRole effectRole, Control[] controls)
        {
            BackColor = Color.Transparent;
            _title = ModernUi.SetForegroundRole(new Label { Text = title, AutoSize = true, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 10F) }, UiForegroundRole.Text);
            _description = ModernUi.SetForegroundRole(new Label { Text = description, AutoSize = false, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8.5F) }, UiForegroundRole.Muted);
            Controls.Add(_title);
            Controls.Add(_description);
            if (effect != null)
                Controls.Add(ModernUi.SetForegroundRole(new Label { Text = "● " + effect, AutoSize = true, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8F), Tag = "effect" }, effectRole));
            _controls = controls;
            Controls.AddRange(controls);
            Height = 64;
        }

        private int S(int v) => LogicalToDeviceUnits(v);

        public void FitToWidth(int width)
        {
            int gap = S(8), right = width;
            int controlsWidth = _controls.Sum(c => c.Width + gap);
            int textWidth = Math.Max(S(160), width - controlsWidth - S(24));
            _title.Location = new Point(0, S(12));
            if (Controls.Cast<Control>().FirstOrDefault(c => Equals(c.Tag, "effect")) is { } tag)
                tag.Location = new Point(_title.Right + S(8), S(14));
            int descriptionHeight = TextRenderer.MeasureText(_description.Text, _description.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height;
            _description.Bounds = new Rectangle(0, _title.Bottom + S(2), textWidth, descriptionHeight);
            Height = Math.Max(S(64), _description.Bottom + S(12));
            int cy = Height / 2;
            for (int i = _controls.Length - 1; i >= 0; i--)
            {
                var control = _controls[i];
                right -= control.Width;
                control.Location = new Point(right, cy - control.Height / 2);
                right -= gap;
            }
        }
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
        // Every change is already saved, so the stored value is what the lists show again.
        _loading = true;
        try
        {
            PopulateMonitorComboBox(_settingsStore.Current.PreferredMonitorDeviceName);
            PopulateAudioOutputs(_settingsStore.Current.AudioOutputDeviceId);
        }
        finally { _loading = false; }
        RefreshDiagnostics();
    }

    private void PopulateAudioOutputs(string? preferredId)
    {
        _refreshingAudioOutputs = true;
        try
        {
            _audioOutputComboBox.Items.Clear();
            _audioOutputComboBox.AccessibleDescription = null;
            _audioOutputComboBox.Items.Add(new AudioOutputItem(null, "跟随系统默认设备"));
            try
            {
                foreach (var device in EveryStage.Rendering.Audio.AudioOutputDevices.Enumerate())
                    _audioOutputComboBox.Items.Add(new AudioOutputItem(device.Id, device.Name));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                _audioOutputComboBox.AccessibleDescription = "设备列表暂不可用，请稍后重新打开设置";
            }
            int index = 0;
            if (!string.IsNullOrEmpty(preferredId))
            {
                index = _audioOutputComboBox.Items.Cast<AudioOutputItem>().ToList().FindIndex(item => item.Id == preferredId);
                if (index < 0)
                {
                    index = _audioOutputComboBox.Items.Add(new AudioOutputItem(preferredId, "已选择的设备不可用（将使用系统默认）"));
                }
            }
            _audioOutputComboBox.SelectedIndex = index;
        }
        finally { _refreshingAudioOutputs = false; }
    }

    private void OnAudioOutputChanged(object? sender, EventArgs e)
    {
        if (_loading || _refreshingAudioOutputs || _audioOutputComboBox.SelectedItem is not AudioOutputItem item) return;
        string? previous = _settingsStore.Current.AudioOutputDeviceId;
        if (item.Id == previous) return;
        CommitDangerous(s => s.AudioOutputDeviceId = item.Id, s => s.AudioOutputDeviceId = previous, "音频输出设备",
            $"把音频输出改为「{item}」？\n之后开始的播放（视频声音和背景音乐）从新设备发声，当前播放不切换。");
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

    private void SelectTheme(int index)
    {
        if (index == _themeComboBox.SelectedIndex) return;
        var updated = _settingsStore.Current.Clone();
        updated.Theme = (AppTheme)index;
        try { _settingsStore.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"主题未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _themeComboBox.SelectedIndex = index;
        _savedLabel.Text = "主题已保存，即时生效。";
    }

    private void RefreshDiagnostics()
    {
        long? logBytes = TryGetLogBytes();
        using var process = Process.GetCurrentProcess();
        long failedLogWrites = DailyRollingLogWriter.FailedWriteCount;
        string logHealth = failedLogWrites == 0
            ? "正常"
            : $"⚠ {failedLogWrites} 条未能写入（最近：{DailyRollingLogWriter.LastFailure}）";
        _diagnosticsLabel.Text =
            $"应用版本：{Application.ProductVersion}\n" +
            $"运行时：{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
            $"操作系统：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"进程运行：{DateTime.Now - process.StartTime:g}\n" +
            $"显示器数量：{Screen.AllScreens.Length}\n" +
            $"日志占用：{(logBytes.HasValue ? $"{logBytes.Value / 1024d / 1024d:F2} MB" : "无法读取")}\n" +
            $"日志写入：{logHealth}\n" +
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
            MessageBox.Show(this, $"导出失败：{DataDirectory.Describe(ex)}", "日志导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>设备名称: saved on leaving the box or Enter (an unchanged or empty name is not saved; an
    /// empty box gets the stored name back).</summary>
    private void SaveDeviceName()
    {
        string name = _deviceNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            _deviceNameTextBox.Text = _identity.DeviceName;
            _savedLabel.Text = "设备名称不能为空，已恢复原名称";
            return;
        }
        if (name == _identity.DeviceName) return;

        // Mutate then persist, but roll the in-memory name back if the save fails, rather than leaving
        // the new name live (and broadcast in the next beacon) while disk still holds the old one and no
        // error is shown (audit C-25). With DeviceIdentity's degraded-load guard, Save() also throws
        // IOException when this identity was minted over a momentarily-locked file — caught here too.
        string previousName = _identity.DeviceName;
        _identity.DeviceName = name;
        try { _identity.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _identity.DeviceName = previousName;
            MessageBox.Show(this, $"设备名称未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _savedLabel.Text = "设备名称已保存。";
    }

    private sealed class ThemePreviewCard : Control
    {
        private readonly string _title;
        private readonly int _index;
        private readonly Func<int> _selectedIndex;
        private readonly Action<int> _select;

        public ThemePreviewCard(string title, int index, Func<int> selectedIndex, Action<int> select)
        {
            _title = title;
            _index = index;
            _selectedIndex = selectedIndex;
            _select = select;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _select(_index);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            bool selected = _selectedIndex() == _index;
            var theme = UiPalette.For((AppTheme)_index);
            var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            using var cardFill = new SolidBrush(Color.FromArgb(202, ModernUi.Surface));
            using var border = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 2F : 1F);
            using var cardPath = new GraphicsPath();
            int radius = 12;
            int diameter = radius * 2;
            cardPath.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            cardPath.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            cardPath.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            cardPath.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            cardPath.CloseFigure();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(cardFill, cardPath);
            g.DrawPath(border, cardPath);

            var preview = new Rectangle(10, 10, Width - 20, 57);
            if ((AppTheme)_index == AppTheme.System)
            {
                // 跟随系统: the light preview with its right half in dark, split on the diagonal.
                DrawThemePreview(g, preview, UiPalette.For(AppTheme.Light));
                using var darkHalf = new GraphicsPath();
                darkHalf.AddPolygon(new[] { new Point(preview.Right, preview.Top), new Point(preview.Right, preview.Bottom),
                    new Point(preview.Left + preview.Width / 3, preview.Bottom), new Point(preview.Left + preview.Width * 2 / 3, preview.Top) });
                var clip = g.Clip;
                g.SetClip(darkHalf, CombineMode.Intersect);
                DrawThemePreview(g, preview, UiPalette.For(AppTheme.Dark));
                g.Clip = clip;
            }
            else DrawThemePreview(g, preview, theme);

            TextRenderer.DrawText(g, _title, Font, new Rectangle(10, 72, Width - 20, 20),
                ModernUi.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private static void DrawThemePreview(Graphics g, Rectangle preview, UiPalette theme)
        {
            using (var previewFill = new SolidBrush(theme.Background)) g.FillRectangle(previewFill, preview);
            using (var railFill = new SolidBrush(theme.Rail)) g.FillRectangle(railFill, preview.X, preview.Y, 7, preview.Height);
            using (var glassFill = new SolidBrush(theme.GlassTint))
                g.FillRectangle(glassFill, preview.X + 13, preview.Y + 7, preview.Width - 20, preview.Height - 14);
            using (var textBrush = new SolidBrush(theme.Text))
            {
                // Proportional, so the mock text never runs into the accent swatch on the narrower cards.
                g.FillRectangle(textBrush, preview.X + 17, preview.Y + 12, preview.Width * 35 / 100, 5);
                g.FillRectangle(textBrush, preview.X + 17, preview.Y + 24, preview.Width * 45 / 100, 4);
            }
            using (var accentBrush = new SolidBrush(theme.Accent))
                g.FillRectangle(accentBrush, preview.Right - 43, preview.Y + 17, 31, 17);
        }
    }

    private sealed record AudioOutputItem(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MonitorComboItem(string DeviceName, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
