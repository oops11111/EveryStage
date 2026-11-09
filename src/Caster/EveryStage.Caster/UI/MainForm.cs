using EveryStage.Caster.Capture;
using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;

namespace EveryStage.Caster.UI;

/// <summary>
/// The Caster's window (PLANNING.md §12, "单一任务导向"): terminals on the left, the selected one on
/// the right — which screen to cast, sound on/off, 开始投屏. A first cast to a terminal shows the
/// pairing code in the same place; problems show what went wrong and what to do; 诊断 holds the
/// one-click check and the individual self-tests. While casting, this window steps aside for the
/// small <see cref="CastBar"/> at the top of the screen. Light and dark themes (or follow Windows).
///
/// The left list merges two sources (PLANNING.md §12 "已配对直显"): terminals
/// <see cref="TerminalDiscoveryClient"/> currently hears beacons from (castable — the address is
/// known), and terminals in <see cref="PairedTerminalStore"/> that paired before but are not
/// broadcasting now (shown as offline; never given a cached address, see <see cref="PairedTerminal"/>).
/// </summary>
public sealed partial class MainForm : GradientForm
{
    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly PairedTerminalStore _pairedTerminals;
    private readonly CasterSettings _settings;
    private readonly CaptureSelfTestRunner _captureSelfTest = new();
    private readonly EncodeSelfTestRunner _encodeSelfTest = new();
    private readonly AudioCaptureSelfTestRunner _audioCaptureSelfTest = new();
    private readonly AacEncodeSelfTestRunner _aacEncodeSelfTest = new();
    private readonly System.Windows.Forms.Timer _listRefreshTimer;
    private readonly System.Windows.Forms.Timer _captureStatsTimer;
    private readonly System.Windows.Forms.Timer _encodeStatsTimer;
    private readonly System.Windows.Forms.Timer _audioCaptureStatsTimer;
    private readonly System.Windows.Forms.Timer _aacEncodeStatsTimer;
    private readonly System.Windows.Forms.Timer _liveCastStatsTimer;

    private const int SidebarWidth = 280;
    private readonly AppTitleBar _titleBar;
    private readonly ThemedSurface _sidebar = new() { Kind = SurfaceKind.Sidebar };
    private readonly ThemedLabel _sidebarTitle;
    private readonly ListBox _terminalListBox;
    private readonly ActionButton _diagnosticsButton;
    private readonly ActionButton _themeButton;
    private readonly ThemedSurface _content = new();
    private readonly EmptyView _emptyView = new();
    private readonly StandbyView _standbyView = new();
    private readonly PairingView _pairingView = new();
    private readonly ProblemView _problemView = new();
    private readonly DiagnosticsView _diagnosticsView;
    private CasterView? _currentView;

    // The individual self-tests (诊断 › 单项自检).
    private readonly Button _captureSelfTestButton;
    private readonly Label _captureStatsLabel;
    private readonly Button _encodeSelfTestButton;
    private readonly Label _encodeStatsLabel;
    private readonly Button _transportSelfTestButton;
    private readonly Label _transportStatsLabel;
    private readonly Button _audioCaptureSelfTestButton;
    private readonly Label _audioCaptureStatsLabel;
    private readonly Button _aacEncodeSelfTestButton;
    private readonly Label _aacEncodeStatsLabel;
    private readonly Button _rawTransportSelfTestButton;
    private readonly Label _rawTransportStatsLabel;
    private readonly Button _discoverySelfTestButton;
    private readonly Label _discoveryStatsLabel;
    private readonly Button _pairedTerminalStoreSelfTestButton;
    private readonly Label _pairedTerminalStoreStatsLabel;
    private readonly Button _deviceIdentitySelfTestButton;
    private readonly Label _deviceIdentityStatsLabel;

    private DiscoveredTerminal? _pairedTerminal;
    private LiveCastSession? _liveCastSession;
    private CastBar? _castBar;

    /// <summary>One row of the terminal list — a live, currently reachable terminal (<see cref="Live"/>
    /// set, from a recent beacon) or a previously paired terminal that is not broadcasting right now
    /// (<see cref="Live"/> null). Deliberately carries no address for the offline case.</summary>
    private sealed record TerminalListEntry(Guid DeviceId, string DeviceName, DiscoveredTerminal? Live, DateTimeOffset? PairedAt)
    {
        public string DisplayText => Live != null ? DeviceName : $"{DeviceName}（离线）";
    }

    public MainForm(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, PairedTerminalStore pairedTerminals)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _pairedTerminals = pairedTerminals;
        _settings = CasterSettings.Load();
        ModernUi.SetTheme(_settings.Theme);

        Text = "EveryStage 投屏器";
        ClientSize = new Size(800, 520);
        MinimumSize = new Size(700, 480);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font(ModernUi.UiFont, 10F);
        WindowsAppearance.EnableGlass(this);

        // --- left: terminals ---
        _sidebarTitle = new ThemedLabel { Text = "附近的终端", Font = new Font(ModernUi.UiFont, 9F) }.With(TextRole.Muted);
        _terminalListBox = new ListBox
        {
            DisplayMember = nameof(TerminalListEntry.DisplayText),
            Font = new Font(ModernUi.UiFont, 10.5F),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = LogicalToDeviceUnits(58), // owner-drawn: not touched by auto-scaling
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
        };
        // Owner-drawn rows flicker without double buffering; ListBox only exposes it as protected.
        typeof(ListBox).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_terminalListBox, true);
        _terminalListBox.DrawItem += DrawTerminalRow;
        _terminalListBox.SelectedIndexChanged += (_, _) => { if (!_refreshingList) OnTerminalSelected(); };
        _diagnosticsButton = new ActionButton { Text = "诊断", Kind = ButtonKind.Ghost, Glyph = ButtonGlyph.Gear, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(ModernUi.UiFont, 9.5F) };
        _diagnosticsButton.Click += (_, _) => ShowView(_diagnosticsView!);
        _themeButton = new ActionButton { Kind = ButtonKind.Ghost, Glyph = ButtonGlyph.Theme, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(ModernUi.UiFont, 9.5F) };
        _themeButton.Click += (_, _) => CycleTheme();
        UpdateThemeButton();
        _sidebar.Controls.AddRange(new Control[] { _sidebarTitle, _terminalListBox, _diagnosticsButton, _themeButton });
        _sidebar.Layout += (_, _) => LayoutSidebar();
        _sidebar.Paint += (_, e) =>
        {
            using var line = new Pen(ModernUi.Palette.Border);
            e.Graphics.DrawLine(line, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
        };

        // --- right: pages ---
        _standbyView.StartClicked += () => _ = StartCastAsync();
        _standbyView.RemovePairingClicked += OnRemovePairingClick;
        _standbyView.IncludeAudio = _settings.IncludeAudio;
        _standbyView.AudioChanged += include => { _settings.IncludeAudio = include; _settings.Save(); };
        _pairingView.CancelClicked += CancelPairing;
        _problemView.BackClicked += ShowSelectedTerminal;
        _problemView.RetryClicked += () => { if (_retry is { } retry) _ = retry(); else ShowSelectedTerminal(); };

        _captureSelfTestButton = SelfTestButton("屏幕捕获自检", OnCaptureSelfTestClick);
        _captureStatsLabel = SelfTestLabel();
        _encodeSelfTestButton = SelfTestButton("视频编码自检（捕获 → H.264）", OnEncodeSelfTestClick);
        _encodeStatsLabel = SelfTestLabel();
        _transportSelfTestButton = SelfTestButton("视频传输自检（本机回环）", OnTransportSelfTestClick);
        _transportStatsLabel = SelfTestLabel();
        _audioCaptureSelfTestButton = SelfTestButton("声音采集自检", OnAudioCaptureSelfTestClick);
        _audioCaptureStatsLabel = SelfTestLabel();
        _aacEncodeSelfTestButton = SelfTestButton("声音编解码自检（AAC）", OnAacEncodeSelfTestClick);
        _aacEncodeStatsLabel = SelfTestLabel();
        _rawTransportSelfTestButton = SelfTestButton("声音传输自检（本机回环）", OnRawTransportSelfTestClick);
        _rawTransportStatsLabel = SelfTestLabel();
        _discoverySelfTestButton = SelfTestButton("发现协议自检（本机回环）", OnDiscoverySelfTestClick);
        _discoveryStatsLabel = SelfTestLabel();
        _pairedTerminalStoreSelfTestButton = SelfTestButton("配对列表存储自检", OnPairedTerminalStoreSelfTestClick);
        _pairedTerminalStoreStatsLabel = SelfTestLabel();
        _deviceIdentitySelfTestButton = SelfTestButton("设备身份存储自检", OnDeviceIdentitySelfTestClick);
        _deviceIdentityStatsLabel = SelfTestLabel();
        _diagnosticsView = new DiagnosticsView(new Control[]
        {
            _captureSelfTestButton, _captureStatsLabel,
            _encodeSelfTestButton, _encodeStatsLabel,
            _transportSelfTestButton, _transportStatsLabel,
            _audioCaptureSelfTestButton, _audioCaptureStatsLabel,
            _aacEncodeSelfTestButton, _aacEncodeStatsLabel,
            _rawTransportSelfTestButton, _rawTransportStatsLabel,
            _discoverySelfTestButton, _discoveryStatsLabel,
            _pairedTerminalStoreSelfTestButton, _pairedTerminalStoreStatsLabel,
            _deviceIdentitySelfTestButton, _deviceIdentityStatsLabel,
        });
        _diagnosticsView.BackClicked += ShowSelectedTerminal;
        _diagnosticsView.RunClicked += () => _ = RunChecksAsync();
        _diagnosticsView.ExportClicked += ExportReport;

        foreach (var view in new CasterView[] { _emptyView, _standbyView, _pairingView, _problemView, _diagnosticsView })
        {
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            _content.Controls.Add(view);
        }

        // Docking: the BACK-most control docks first. Title bar across the top, sidebar on the left,
        // pages fill the rest.
        _titleBar = new AppTitleBar(this) { SidebarWidth = LogicalToDeviceUnits(SidebarWidth) };
        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = SidebarWidth;
        _content.Dock = DockStyle.Fill;
        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_titleBar);
        _titleBar.SendToBack();
        _content.BringToFront();

        RefreshMonitorList();
        // Displays plugged in or removed while this window waits: DXGI output indices are positional, so
        // a stale list could make the chosen entry capture a different physical screen (audit A-13).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _listRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _listRefreshTimer.Tick += (_, _) => RefreshTerminalList();
        _listRefreshTimer.Start();
        // A brand-new terminal shows up at once instead of on the next tick (the timer still handles expiry).
        _discoveryClient.TerminalListChanged += OnTerminalListChanged;

        _captureStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _captureStatsTimer.Tick += (_, _) => RefreshCaptureStats();
        _encodeStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _encodeStatsTimer.Tick += (_, _) => RefreshEncodeStats();
        _audioCaptureStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioCaptureStatsTimer.Tick += (_, _) => RefreshAudioCaptureStats();
        _aacEncodeStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _aacEncodeStatsTimer.Tick += (_, _) => RefreshAacEncodeStats();
        _liveCastStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _liveCastStatsTimer.Tick += (_, _) => RefreshLiveCastStats();

        ApplyThemeEverywhere();
        RefreshTerminalList();
        ShowSelectedTerminal();

        // Every bound above is in 96-DPI pixels: declare the design DPI so the window scales once here.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        ResumeLayout(true);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, workingArea.Width), Math.Min(MinimumSize.Height, workingArea.Height));
        Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));
    }

    private static ActionButton SelfTestButton(string text, EventHandler onClick)
    {
        var button = new ActionButton { Text = text, Kind = ButtonKind.Secondary, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(ModernUi.UiFont, 9.5F) };
        button.Click += onClick;
        return button;
    }

    private static ThemedLabel SelfTestLabel() => new ThemedLabel { Font = new Font(ModernUi.UiFont, 9F) }.With(TextRole.Muted);

    private void LayoutSidebar()
    {
        int s(int v) => LogicalToDeviceUnits(v);
        int w = _sidebar.ClientSize.Width - s(1);
        _sidebarTitle.Bounds = new Rectangle(s(20), s(10), w - s(32), s(22));
        int bottom = _sidebar.ClientSize.Height - s(12);
        _themeButton.Bounds = new Rectangle(s(12), bottom - s(34), w - s(24), s(34));
        _diagnosticsButton.Bounds = new Rectangle(s(12), _themeButton.Top - s(4) - s(34), w - s(24), s(34));
        _terminalListBox.Bounds = new Rectangle(s(12), s(36), w - s(24), Math.Max(s(40), _diagnosticsButton.Top - s(10) - s(36)));
    }

    private void ShowView(CasterView view)
    {
        if (_currentView == view) return;
        view.Visible = true;
        view.BringToFront();
        if (_currentView != null) _currentView.Visible = false;
        _currentView = view;
    }

    private TerminalListEntry? SelectedEntry => _terminalListBox.SelectedItem as TerminalListEntry;

    private void OnTerminalSelected()
    {
        // A pairing or a start in progress keeps its page; anything else follows the selection.
        if (_currentView == _pairingView || _starting) return;
        ShowSelectedTerminal();
    }

    /// <summary>The right-hand page for whatever is selected on the left.</summary>
    private void ShowSelectedTerminal()
    {
        if (SelectedEntry is not { } entry)
        {
            _emptyView.SetFound(_terminalListBox.Items.Count > 0);
            ShowView(_emptyView);
            return;
        }
        UpdateStandbyDetails(entry);
        ShowView(_standbyView);
    }

    private void UpdateStandbyDetails(TerminalListEntry entry)
    {
        bool paired = _pairedTerminals.Find(entry.DeviceId) != null;
        string status = entry.Live is { } live
            ? $"在线 · {(paired ? "已配对" : "未配对")} · {live.Address}"
            : "离线 · 已配对";
        string hint = entry.Live == null
            ? "这台终端现在不在线。终端机开机并连上这个局域网后，会自动变成可投屏。"
            : paired ? "" : "第一次投到这台终端，需要在终端机上核对配对码并点「接受」。只需一次。";
        _standbyView.SetTerminal(entry.DeviceName, status, canStart: entry.Live != null, canRemovePairing: paired, hint);
    }

    // ---- theme ----

    private void CycleTheme()
    {
        _settings.Theme = _settings.Theme switch
        {
            CasterTheme.System => CasterTheme.Light,
            CasterTheme.Light => CasterTheme.Dark,
            _ => CasterTheme.System,
        };
        _settings.Save();
        ModernUi.SetTheme(_settings.Theme);
        UpdateThemeButton();
        ApplyThemeEverywhere();
    }

    private void UpdateThemeButton()
    {
        _themeButton.Text = $"主题：{ModernUi.ThemeName(_settings.Theme)}";
        _themeButton.AccessibleName = $"切换主题，当前{ModernUi.ThemeName(_settings.Theme)}";
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme != CasterTheme.System || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                var before = ModernUi.Palette;
                ModernUi.SetTheme(CasterTheme.System);
                if (!ReferenceEquals(before, ModernUi.Palette)) ApplyThemeEverywhere();
            }));
        }
        catch (InvalidOperationException) { } // handle destroyed meanwhile (closing)
    }

    private void ApplyThemeEverywhere()
    {
        _terminalListBox.BackColor = ModernUi.Palette.Sidebar;
        ModernUi.ApplyTheme(this);
        foreach (var view in new CasterView[] { _emptyView, _standbyView, _pairingView, _problemView, _diagnosticsView }) view.OnThemeChanged();
        _castBar?.ApplyTheme();
        WindowsAppearance.UpdateBorder(this);
        Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _terminalListBox.ItemHeight = LogicalToDeviceUnits(58);
        _titleBar.SidebarWidth = LogicalToDeviceUnits(SidebarWidth);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // MainForm doesn't own _discoveryClient (Program.cs's `using` does) — unsubscribing just keeps
            // a late TerminalListChanged from BeginInvoke-ing onto a form mid-teardown.
            _discoveryClient.TerminalListChanged -= OnTerminalListChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // static events: would keep this form alive
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _pairingCts?.Cancel();
            _listRefreshTimer.Dispose();
            _captureStatsTimer.Dispose();
            _captureSelfTest.Dispose();
            _encodeStatsTimer.Dispose();
            _encodeSelfTest.Dispose();
            _audioCaptureStatsTimer.Dispose();
            _audioCaptureSelfTest.Dispose();
            _aacEncodeStatsTimer.Dispose();
            _aacEncodeSelfTest.Dispose();
            _liveCastStatsTimer.Dispose();
            _liveCastSession?.Dispose();
            _castBar?.Dispose();
        }
        base.Dispose(disposing);
    }
}
