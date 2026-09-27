using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI.Panels;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §8's operator-facing main window: fixed left nav (connection status card + the four
/// panel icons) + a right content area that swaps between panels. Shown on
/// whatever display the operator is actually sitting at — this is NOT the <c>OverlayWindow</c>,
/// which covers the bound extended display with actual content output.
///
/// All four §8.2 panels are real now: 文件/活动/设备 from earlier rounds, and 设置
/// (<see cref="SettingsPanel"/>) from this one — see that class and this project's README for what
/// its fields are and aren't (PLANNING.md only names the five category labels, not any field
/// within them).
///
/// The nav deliberately doesn't repeat what the title bar and the shared preview dock
/// (<see cref="SharedPreviewDock"/>) already show: no brand block (the title bar already reads
/// "EveryStage Terminal"), no 投屏开关/断开投屏 pair (the dock has 断开输出; the cast switch itself
/// stays reachable from the tray menu, <c>TrayIconController</c>), and no 显示预览窗 recall button
/// (the dock's local preview covers it).
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
    private readonly Panel _panelHost;
    private readonly SharedPreviewDock _previewDock;
    private readonly ToastStack _toastStack;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _declinedMessageTimer;
    private readonly FilesPanel _filesPanel;
    private readonly DevicesPanel _devicesPanel;
    private readonly ActivitiesPanel _activitiesPanel;
    private readonly SettingsPanel _settingsPanel;

    /// <summary>The palette each page was last themed with. Only the visible page is in the tree when
    /// <see cref="ThemeManager.Apply"/> runs, so <see cref="ShowPanel"/> re-themes a page that missed
    /// a switch (or the saved theme at startup) from this palette.</summary>
    private readonly Dictionary<Control, UiPalette> _panelPalettes = new();

    public void ShowPairingRequest(PairingRequest request) => _devicesPanel.ShowPairingRequest(request);

    public void ClearPairingRequest(string requestId) => _devicesPanel.ClearPairingRequest(requestId);

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
        Icon = ProductIcon.LoadIcon();
        NormalWindowAspectRatio = new Size(4, 3);
        ClientSize = new Size(960, 720);
        MinimumSize = new Size(800, 600);
        BackColor = ModernUi.Background;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        WindowsAppearance.UseDarkTitleBar(this);
        WindowsAppearance.EnableMica(this);
        // Keep the client area fully app-painted. Windows 10's legacy acrylic/blur accent policy
        // composites through transparent WinForms children as a flat gray surface (not the design
        // palette), even when each GlassPanel has an opaque tint. GlassPanel supplies the visual
        // treatment itself, so enabling DWM blur here only undermines the UI and thumbnail canvas.

        var nav = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(0), CornerRadius = 2, AutoScroll = false,
            GlassTint = Color.FromArgb(178, 8, 23, 40),
        };

        // 状态卡（对齐效果图）：导航栏顶部一张圆角卡，主屏幕输出 + 已连接状态。品牌区已去掉——标题栏已显示「EveryStage Terminal」。
        var statusCard = new GlassPanel
        {
            Bounds = new Rectangle(16, 16, 218, 58), CornerRadius = 12, AutoScroll = false,
            GlassTint = Color.FromArgb(240, 12, 40, 82),
        };
        var statusDot = new Label
        {
            Text = "●", ForeColor = ModernUi.Success, Font = new Font("Segoe UI", 8F),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(14, 10, 14, 18),
        };
        var statusTitle = new Label
        {
            Text = "主屏幕输出", ForeColor = ModernUi.Text, Font = new Font("Segoe UI Semibold", 10.5F),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(30, 8, 160, 22),
        };
        var statusChevron = new Label
        {
            Text = "›", ForeColor = ModernUi.Muted, Font = new Font("Segoe UI", 12F),
            AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(180, 6, 24, 22),
        };
        _statusLabel = new Label
        {
            ForeColor = ModernUi.Success, Font = new Font("Segoe UI", 8.5F),
            AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(16, 32, 190, 17),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        statusCard.Controls.AddRange(new Control[] { statusDot, statusTitle, statusChevron, _statusLabel });

        var filesButton = ModernUi.NavButton(NavIcon.Files, "文件", 92);
        var activitiesButton = ModernUi.NavButton(NavIcon.Activities, "活动", 142);
        var devicesButton = ModernUi.NavButton(NavIcon.Devices, "设备", 192);
        var settingsButton = ModernUi.NavButton(NavIcon.Settings, "设置", 242);
        var navButtons = new[] { filesButton, activitiesButton, devicesButton, settingsButton };

        // 底部品牌标语（对齐效果图），停靠栏底。用 Dock 而不是「固定坐标 + Anchor=Bottom」：Anchor 的
        // 底边距是在控件加入 nav 那一刻按 nav 当时的尺寸算的，而那时 nav 还是 WinForms 默认尺寸（约 100px 高），
        // 于是 y=632 的标语被永久钉在可视区下方几百像素处、从未显示。Dock=Bottom 与父容器尺寸和 DPI 无关。
        var tagline = new Label
        {
            Text = "让每个阶段\r\n都精彩呈现",
            ForeColor = ModernUi.Muted,
            Font = new Font("Segoe UI", 9.5F),
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(18, 0, 0, 16),
        };

        nav.Controls.AddRange(new Control[]
        {
            statusCard,
            filesButton, activitiesButton, devicesButton, settingsButton,
            tagline,
        });

        _contentHost = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), CornerRadius = 12,
            GlassTint = Color.FromArgb(160, 15, 35, 58),
        };

        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Padding.Empty,
        };
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 214F));
        _panelHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _previewDock = new SharedPreviewDock(stateMachine, playback) { Margin = new Padding(0, 10, 0, 0) };
        contentLayout.Controls.Add(_panelHost, 0, 0);
        contentLayout.Controls.Add(_previewDock, 0, 1);
        _contentHost.Controls.Add(contentLayout);

        // 预览坞随窗口变高：效果图里「本地预览 / 播控窗口 + 信号源窗口」约占主区一半，预览画面是视觉主体。
        // 原先固定 214px 时，窗口多出来的高度全给了页面，预览画面只剩约 50px；现在预览按内容区高度的
        // 比例增长（下限 96px、上限 300px），其余交给页面。
        contentLayout.SizeChanged += (_, _) =>
        {
            int chrome = _previewDock.ChromeHeight + _previewDock.Margin.Vertical;
            int preview = Math.Clamp((int)(contentLayout.ClientSize.Height * 0.44) - chrome, 96, 300);
            float rowHeight = chrome + preview;
            if (contentLayout.RowStyles[1].Height != rowHeight) contentLayout.RowStyles[1].Height = rowHeight;
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
        _filesPanel.PlaybackStopRequested += _stateMachine.Disconnect;

        _devicesPanel = new DevicesPanel(pairedDevices, connectionLog);
        _activitiesPanel = new ActivitiesPanel(
            scenarioStore, scenarioRepository, library, _playback, _fileOpLog, stateMachine);
        _settingsPanel = new SettingsPanel(settingsStore, identity);
        foreach (Control page in new Control[] { _filesPanel, _devicesPanel, _activitiesPanel, _settingsPanel })
            _panelPalettes[page] = ModernUi.Palette;
        settingsStore.SettingsChanged += OnSettingsChanged;

        filesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, filesButton); ShowPanel(_filesPanel); };
        activitiesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, activitiesButton); ShowPanel(_activitiesPanel); };
        devicesButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, devicesButton); ShowPanel(_devicesPanel); };
        settingsButton.Click += (_, _) => { ModernUi.SetNavActive(navButtons, settingsButton); ShowPanel(_settingsPanel); };

        // Keep the rail close to the window edge, like the reference UI, while reserving a compact
        // content gutter. A table layout is more stable than competing Left/Fill docking children
        // when DPI scaling is enabled.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 12, 12),
            ColumnCount = 3,
            RowCount = 1,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
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
        ApplyTheme(settingsStore.Current.Theme);
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
    /// <summary>Idempotent, and unsubscribes the previous engine first - the same guard
    /// FilesPanel.AttachPlaybackEngine already had and ActivitiesPanel has now. Program.cs only
    /// reaches this once today (OnDisplaySettingsChanged re-binds only while _overlay is still
    /// null, and _overlay stays non-null once bound), so a second call is currently unreachable;
    /// the guard is here so that relaxing that binding rule for monitor hot-swap cannot quietly
    /// turn every playback notification into two.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        if (ReferenceEquals(_playback, playback)) return;
        if (_playback != null)
        {
            _playback.PlaybackAbnormallyInterrupted -= OnPlaybackAbnormallyInterrupted;
            _playback.PlaybackDeclinedByCastSwitch -= OnPlaybackDeclinedByCastSwitch;
        }
        _playback = playback;
        _playback.PlaybackAbnormallyInterrupted += OnPlaybackAbnormallyInterrupted;
        _playback.PlaybackDeclinedByCastSwitch += OnPlaybackDeclinedByCastSwitch;
        _activitiesPanel.AttachPlaybackEngine(playback);
        _filesPanel.AttachPlaybackEngine(playback);
        _previewDock.AttachPlaybackEngine(playback);
        UpdateStatusLabel();
    }

    private void ShowPanel(Control panel)
    {
        _panelHost.Controls.Clear();
        panel.Dock = DockStyle.Fill;
        _panelHost.Controls.Add(panel);
        if (_panelPalettes.TryGetValue(panel, out var themedWith)) ThemeManager.Reapply(panel, themedWith);
        _panelPalettes[panel] = ModernUi.Palette;

        if (panel == _filesPanel) _filesPanel.Refresh_();
        else if (panel == _devicesPanel) _devicesPanel.Refresh_();
        else if (panel == _activitiesPanel) _activitiesPanel.RefreshTree();
        else if (panel == _settingsPanel) _settingsPanel.Refresh_();
    }

    /// <summary>Applies a theme to the window and records it for whichever page is hosted right now;
    /// the other pages catch up in <see cref="ShowPanel"/>.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        ThemeManager.Apply(this, theme);
        foreach (Control hosted in _panelHost.Controls) _panelPalettes[hosted] = ModernUi.Palette;
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

    private void OnSettingsChanged(AppSettings settings) => ApplyTheme(settings.Theme);

    private void UpdateStatusLabel()
    {
        if (_playback == null)
        {
            _statusLabel.Text = "● 本机预览模式 · 未连接扩展屏"; // 单行：状态卡只有一行高（多行会被截掉第二行）。
            _filesPanel.SetOutputActive(false);
            return;
        }
        bool active = _stateMachine.State == OutputState.Active;
        _statusLabel.Text = active ? "● 输出中" : "○ 待机中";
        _filesPanel.SetOutputActive(active);
    }

    private void OnPlaybackDeclinedByCastSwitch(MediaFile file)
    {
        _statusLabel.Text = $"⚠ 投屏开关已关闭 · 未投放：{Path.GetFileName(file.SourcePath)}"; // 单行，过长由 AutoEllipsis 截断。
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
        int index = activity.Files.IndexOf(file);
        if (index < 0) return;
        activity.Files.RemoveAt(index);
        // Save before logging/refreshing, and roll the in-memory removal back if it fails — otherwise the
        // tree refreshes as "removed" while disk still has the file and no error is shown, since the
        // Terminal's ThreadException handler only logs (audit C-25). Mirrors FilesPanel/ActivitiesPanel.
        try { _scenarioRepository.Save(_scenarioStore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            activity.Files.Insert(index, file);
            MessageBox.Show(this, $"移除未保存：{ex.Message}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (scenario != null) _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        _activitiesPanel.RefreshTree();
    }

    /// <summary>Same operation as <c>FilesPanel.OnRemoveClick</c> (library removal + log + grid
    /// refresh), reached from a Toast instead of that panel's own "移除" button.</summary>
    private void RemoveFileFromLibrary(MediaFile file)
    {
        try { _library.Remove(file.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"移除未保存：{ex.Message}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
