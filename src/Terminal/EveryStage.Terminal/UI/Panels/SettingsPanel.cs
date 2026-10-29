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

        var settings = _settingsStore.Current;
        var amberEffect = Color.FromArgb(243, 178, 76);

        // One scrolling page of sections (design): 外观 · 通用 · 输出与播放 · 网络与设备 · 诊断 · 关于.
        // Each row: title + description on the left, its control(s) on the right, an effect tag
        // (即时生效 / 保存后生效 / 重启后生效) beside the title.
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 16),
        };
        var sections = new List<SettingsSection>();
        SettingsSection Section(string title, string? effect = null, Color? effectColor = null)
        {
            var section = new SettingsSection(title, effect, effectColor ?? ModernUi.Muted);
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
        _themeComboBox.Items.AddRange(new object[] { "白色", "黑色", "蓝色", "跟随系统" });
        _themeComboBox.SelectedIndex = Math.Clamp((int)settings.Theme, 0, _themeComboBox.Items.Count - 1);
        var themeCards = new FlowLayoutPanel { Height = 112, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        string[] themeNames = { "白色", "黑色", "蓝色", "跟随系统" };
        for (int i = 0; i < themeNames.Length; i++)
        {
            var card = new ThemePreviewCard(themeNames[i], i, () => _themeComboBox.SelectedIndex, index =>
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
        var appearance = Section("外观", "即时生效", ModernUi.Accent);
        appearance.AddBlock(themeCards);
        appearance.AddNote("「跟随系统」在 Windows 浅色模式下使用白色，深色模式下使用黑色，切换系统模式时实时跟随。");
        appearance.Controls.Add(_themeComboBox);

        // --- 通用 ---
        _castSwitchDefaultCheckbox = new CheckBox { Text = "开启", AutoSize = true, Checked = settings.CastSwitchDefaultOn };
        Section("通用").AddRow("启动时投屏开关默认开启", "只设置终端机启动时的默认值，不改变当前开关（当前开关在「设备」页和托盘菜单）",
            "重启后生效", ModernUi.Muted, _castSwitchDefaultCheckbox);

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
        var secondsLabel = new Label { Text = "秒", AutoSize = true, ForeColor = ModernUi.Muted };
        _defaultStayDurationEnabledCheckbox.CheckedChanged += (_, _) =>
            _defaultStayDurationSeconds.Enabled = _defaultStayDurationEnabledCheckbox.Checked;
        var output = Section("输出与播放");
        output.AddRow("扩展屏", "终端机输出到哪个显示器；列表每次打开本页时重新枚举", "重启后生效", ModernUi.Muted, _monitorComboBox);
        output.AddRow("图片 / 文档默认停留时长", "没有单独设置停留时长的项目停留多久后进入下一项；关闭则一直停留到手动切换",
            "保存后生效", amberEffect, _defaultStayDurationEnabledCheckbox, _defaultStayDurationSeconds, secondsLabel);
        output.AddRow("PPT 没有扩展屏时", "原生放映（含动画）只在扩展屏上进行，避免占用本机屏幕；没有扩展屏时以静态幻灯片预览", null, null,
            new Label { Text = "静态幻灯片", AutoSize = true, ForeColor = ModernUi.Text });

        // --- 网络与设备 ---
        _deviceNameTextBox = new TextBox { Text = _identity.DeviceName, Width = 220 };
        var saveDeviceNameButton = new RoundedActionButton { Text = "保存", Width = 72, Height = 30 };
        ModernUi.StyleButton(saveDeviceNameButton);
        saveDeviceNameButton.Click += OnSaveDeviceNameClick;
        Section("网络与设备").AddRow("设备名称", "投屏机列表中显示的名字；下一次广播就会带上新名字", "即时生效", ModernUi.Accent,
            _deviceNameTextBox, saveDeviceNameButton);

        // --- 诊断 ---
        _diagnosticsLabel = new Label { AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent };
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

        // --- 关于 ---
        var openDataButton = new RoundedActionButton { Text = "打开数据目录", Width = 116, Height = 30 };
        ModernUi.StyleButton(openDataButton);
        openDataButton.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDirectory.Root}\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, $"无法打开数据目录：{ex.Message}", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var about = Section("关于");
        about.AddBlock(new Label
        {
            AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent,
            Text = $"EveryStage 终端机 {Application.ProductVersion}\n设备 ID：{_identity.DeviceId}\n数据目录：{DataDirectory.Root}\n" +
                   "所有发现 / 配对 / 投屏数据只在局域网内传输，不上传任何遥测或使用数据。",
        });
        about.AddButtons(openDataButton);

        // Right-aligned 保存设置 with the result beside it.
        var saveSettingsButton = new RoundedActionButton { Text = "保存设置", Width = 120, Height = 34, Margin = new Padding(0, 7, 0, 0) };
        ModernUi.StyleButton(saveSettingsButton, primary: true);
        saveSettingsButton.Click += OnSaveSettingsClick;
        _savedLabel = new Label { ForeColor = ModernUi.Success, AutoSize = true, Margin = new Padding(12, 16, 6, 0) };
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Color.Transparent };
        var saveRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = Padding.Empty, BackColor = Color.Transparent,
        };
        saveRow.Controls.Add(saveSettingsButton);   // RightToLeft: first added is right-most.
        saveRow.Controls.Add(_savedLabel);
        bottomBar.Controls.Add(saveRow);

        var heading = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        heading.Controls.Add(new Label
        {
            Text = "设置", AutoSize = true, Location = new Point(0, 8), ForeColor = ModernUi.Text,
            Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent,
        });

        // Dock order: the last Top control added sits highest, so heading goes in last.
        Controls.Add(page);
        Controls.Add(bottomBar);
        Controls.Add(heading);
    }

    /// <summary>One settings section: a rounded surface card with a title (and optional effect tag),
    /// then rows / blocks stacked top to bottom. Laid out by hand in <see cref="FitToWidth"/>.</summary>
    private sealed class SettingsSection : Panel
    {
        private readonly string _title;
        private readonly string? _effect;
        private readonly Color _effectColor;
        private readonly List<(Control Item, int Kind)> _items = new(); // 0 block, 1 row, 2 note, 3 buttons
        private readonly List<int> _dividers = new();

        public SettingsSection(string title, string? effect, Color effectColor)
        {
            _title = title;
            _effect = effect;
            _effectColor = effectColor;
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

        public void AddNote(string text) => AddBlockOf(new Label { Text = text, AutoSize = false, ForeColor = ModernUi.Muted, BackColor = Color.Transparent }, 2);

        public void AddButtons(params Control[] buttons)
        {
            var row = new FlowLayoutPanel { WrapContents = false, BackColor = Color.Transparent, Height = 36, Margin = Padding.Empty };
            foreach (var button in buttons) { button.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(button); }
            AddBlockOf(row, 3);
        }

        public void AddRow(string title, string description, string? effect, Color? effectColor, params Control[] controls) =>
            AddBlockOf(new SettingRow(title, description, effect, effectColor ?? ModernUi.Muted, controls), 1);

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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ActivityRowView.RoundedBox(new Rectangle(0, 0, Width - 1, Height - 1), S(10)))
            using (var fill = new SolidBrush(ModernUi.Surface))
            using (var border = new Pen(ModernUi.Mix(ModernUi.Surface, ModernUi.Border, 0.45f)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var titleFont = new Font(ModernUi.UiFontSemibold, 11.5F);
            var titleSize = TextRenderer.MeasureText(_title, titleFont);
            TextRenderer.DrawText(g, _title, titleFont, new Point(S(18), S(14)), ModernUi.Text);
            if (_effect != null)
                using (var effectFont = new Font(ModernUi.UiFont, 8.5F))
                    TextRenderer.DrawText(g, "● " + _effect, effectFont, new Point(S(18) + titleSize.Width + S(6), S(18)), _effectColor);
            using var divider = new Pen(ModernUi.Mix(ModernUi.Surface, ModernUi.Border, 0.35f));
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

        public SettingRow(string title, string description, string? effect, Color effectColor, Control[] controls)
        {
            BackColor = Color.Transparent;
            _title = new Label { Text = title, AutoSize = true, ForeColor = ModernUi.Text, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 10F) };
            _description = new Label { Text = description, AutoSize = false, ForeColor = ModernUi.Muted, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8.5F) };
            Controls.Add(_title);
            Controls.Add(_description);
            if (effect != null)
                Controls.Add(new Label { Text = "● " + effect, AutoSize = true, ForeColor = effectColor, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8F), Tag = "effect" });
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

    private void SelectTheme(int index)
    {
        if (index == _themeComboBox.SelectedIndex) return;
        var current = _settingsStore.Current;
        var updated = new AppSettings
        {
            SchemaVersion = current.SchemaVersion,
            Theme = (AppTheme)index,
            CastSwitchDefaultOn = current.CastSwitchDefaultOn,
            PreferredMonitorDeviceName = current.PreferredMonitorDeviceName,
            DefaultStayDurationSeconds = current.DefaultStayDurationSeconds,
        };
        try { _settingsStore.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"主题未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _themeComboBox.SelectedIndex = index;
        _savedLabel.Text = "主题已保存，即时生效。";
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

        // Same guard+message convention as FilesPanel's own saves: on the Terminal an unguarded save
        // throw only reaches the ThreadException handler, which logs to CrashLogger without any dialog —
        // so the operator would see neither "已保存" nor an error. Surface it here instead (audit C-25).
        try { _settingsStore.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"设置未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _savedLabel.Text = "已保存。";
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

    private void OnSaveDeviceNameClick(object? sender, EventArgs e)
    {
        string name = _deviceNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "设备名称不能为空。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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

    private sealed record MonitorComboItem(string DeviceName, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
