using System.Drawing.Drawing2D;
using EveryStage.Rendering;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 「监看」— the right-hand column beside every main page.
///
/// Top row: the big screen's status, 「停止输出」 and 「收起」. Below it two identical monitor windows (no
/// role labels). Each window has its own preview engine and the same compact transport, and either plays
/// its own content locally or follows the big screen: then it shows exactly what the extended display
/// shows (<see cref="ProgramMirror"/>, red flashing border) and its transport drives the big screen.
/// MainWindow decides which window does what (<see cref="ProgramWindow"/>, <see cref="FreeWindow"/>):
/// a cast goes on air and the window already following the big screen keeps following it, else the
/// first free window starts to, else the top one; a file dropped on a window plays in that window.
/// Between the windows: 切换 / 淡入淡出 / ⇄ 对调, which put the active local window on air.
///
/// When a window's engine fails, an error card covers it (重试 / 跳过 / 从文件库移除 / 用系统程序打开).
/// Laid out by hand (<see cref="OnLayout"/>): the two windows share whatever height is left.
/// </summary>
internal sealed class SharedPreviewDock : Panel
{
    internal UiPalette? AppliedPalette { get; set; }
    private static UiPalette MonitorPalette => UiPalette.For(AppTheme.Dark);
    private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 150 };
    private Control? _flashingCard;

    /// <summary>A short red outline on the device card when a device cast goes on air (the window that
    /// follows the big screen already flashes on its own).</summary>
    public void FlashProgram(bool device)
    {
        if (!device) return;
        _flashingCard?.Invalidate();
        _flashingCard = _deviceCard.Card;
        _flashingCard.Paint -= PaintOnAirFeedback;
        _flashingCard.Paint += PaintOnAirFeedback;
        _feedbackTimer.Tick -= EndOnAirFeedback;
        _feedbackTimer.Tick += EndOnAirFeedback;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
        _flashingCard.Invalidate();
    }
    private void PaintOnAirFeedback(object? sender, PaintEventArgs e)
    {
        if (!ReferenceEquals(sender, _flashingCard) || sender is not Control card) return;
        using var pen = new Pen(MonitorPalette.ProgramBorder, 3);
        var rect = new Rectangle(2, 2, Math.Max(1, card.Width - 5), Math.Max(1, card.Height - 5));
        float d = Math.Min(12, Math.Min(rect.Width, rect.Height));
        using var outline = new GraphicsPath();
        outline.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        outline.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        outline.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        outline.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        outline.CloseFigure();
        e.Graphics.DrawPath(pen, outline);
    }
    private void EndOnAirFeedback(object? sender, EventArgs e)
    {
        _feedbackTimer.Stop();
        var card = _flashingCard;
        _flashingCard = null;
        card?.Invalidate();
    }

    private const int ButtonHeight = 30;
    private const int SourceCardHeight = 46;
    private const int Gap = 10;
    private const string IconFont = "Segoe MDL2 Assets";

    private readonly OutputStateMachine _stateMachine;
    private readonly Label _status;
    private readonly RoundedActionButton _stopOutputButton;
    private readonly RoundedActionButton _collapseButton;
    private readonly MonitorWindow[] _windows;
    private readonly RoundedActionButton _takeButton;
    private readonly RoundedActionButton _fadeButton;
    private readonly RoundedActionButton _swapButton;
    private readonly DeviceCastCard _deviceCard;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolTip _toolTip = new();
    private PlaybackEngine? _program;
    private Bitmap? _placeholder;
    private string? _deviceName;
    private bool _deviceConnected;
    private bool _deviceOnProgram;
    private DocumentControlBar? _documentBar;
    private DocumentProgramSync? _documentSync;
    // Which window the document bar sits under (0 = top, 1 = bottom), and which engine its
    // DocumentChanged subscription is on — both follow the operator's window (see RefreshDocumentBar).
    private int _documentBarWindow;
    private PlaybackEngine? _documentBarPreview;
    // 演讲者视图 is never part of this column: it is its own window that opens when a presentation goes on
    // air on the big screen (once per show; closing it keeps it closed until the next show, or reopen it
    // from the status row's right-click menu) and closes when the show ends.
    private PresenterWindow? _presenterWindow;
    private object? _presenterShow;
    private bool _documentBarShown;
    private Control? _footer;
    private Control? _runControls;
    private int _activeWindow;

    /// <summary>The top window's surface (MainWindow builds the first preview engine on it).</summary>
    public PreviewSurface PreviewSurface => _windows[0].Surface;

    /// <summary>The bottom window's surface (MainWindow builds the second preview engine on it).</summary>
    public PreviewSurface SecondSurface => _windows[1].Surface;

    /// <summary>The active local window on air: 「切换」 passes <see cref="TransitionStyle.Cut"/>, 「淡入淡出」
    /// <see cref="TransitionStyle.Dissolve"/>.</summary>
    public event Action<TransitionStyle>? TakeRequested;

    /// <summary>「⇄ 对调」: the active local window goes on air with a fade, what was on air comes back into
    /// the window that was following the big screen, paused.</summary>
    public event Action? SwapRequested;

    /// <summary>A file (from the library) or a path (from Explorer) was dropped on window 0 / 1.</summary>
    public event Action<int, MediaFile?, string?>? FileDropped;

    /// <summary>The extended display's screen rectangle while something is on air (copied into the window
    /// that follows the big screen).</summary>
    public Func<Rectangle?>? ProgramScreenBounds
    {
        get => _windows[0].Mirror.SourceBounds;
        set { foreach (var window in _windows) window.Mirror.SourceBounds = value; }
    }

    /// <summary>"停止输出": take whatever is on the extended display off air.</summary>
    public event Action? StopProgramRequested;

    /// <summary>「收起 ›」: MainWindow hides the column and gives the page the full width.</summary>
    public event Action? CollapseRequested;
    public event Action? DeviceTakeRequested;
    public event Action? DeviceRevertRequested;
    public event Action<int, int>? DeviceVideoHostResized;

    /// <summary>Error card "从文件库移除".</summary>
    public event Action<MediaFile>? RemoveFromLibraryRequested;
    public Control DeviceVideoHost => _deviceCard.VideoHost;

    public SharedPreviewDock(OutputStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
        Padding = new Padding(14, 12, 14, 12);
        BackColor = Color.Transparent;
        DoubleBuffered = true;

        // Top row: big-screen status · 停止输出 · 收起.
        _status = ModernUi.SetForegroundRole(new Label
        {
            Text = "○ 大屏待机", AutoSize = false, AutoEllipsis = true, Font = new Font(ModernUi.UiFontSemibold, 9.5F),
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "大屏状态",
        }, UiForegroundRole.Muted);
        _stopOutputButton = TextButton("■ 停止输出", "停止扩展屏上的输出（急停 F12）");
        ModernUi.StyleButton(_stopOutputButton, danger: true);
        _stopOutputButton.AccessibleName = "停止输出（急停 F12）";
        _collapseButton = TextButton("收起 ›", "收起监看栏，文件区占满整个宽度");
        _collapseButton.Font = new Font(ModernUi.UiFont, 8.5F);
        _collapseButton.Click += (_, _) => CollapseRequested?.Invoke();

        _windows = new[] { new MonitorWindow(this, 0), new MonitorWindow(this, 1) };

        _takeButton = TextButton("切换 ↓", "把正在本地播放的窗口直接切到大屏");
        ModernUi.StyleButton(_takeButton, primary: true);
        _takeButton.Font = new Font(ModernUi.UiFontSemibold, 10F);
        _takeButton.AccessibleName = "切换到大屏";
        _fadeButton = TextButton("淡入淡出 ↓", "把正在本地播放的窗口淡入淡出切到大屏，时长同「设置 · 大屏过场」");
        ModernUi.StyleButton(_fadeButton, primary: true);
        _fadeButton.Font = new Font(ModernUi.UiFontSemibold, 10F);
        _fadeButton.AccessibleName = "淡入淡出到大屏";
        _swapButton = TextButton("⇄ 对调", "本地窗口的内容淡入淡出上屏，原大屏内容回到另一个窗口（暂停）");

        _deviceCard = new DeviceCastCard();
        _deviceCard.TakeRequested += () => DeviceTakeRequested?.Invoke();
        _deviceCard.RevertRequested += () => DeviceRevertRequested?.Invoke();
        _deviceCard.VideoHost.SizeChanged += (_, _) => DeviceVideoHostResized?.Invoke(
            Math.Max(1, _deviceCard.VideoHost.ClientSize.Width), Math.Max(1, _deviceCard.VideoHost.ClientSize.Height));

        var statusMenu = new ContextMenuStrip();
        var presenterItem = statusMenu.Items.Add("打开演讲者视图", null, (_, _) => OpenPresenterView());
        statusMenu.Opening += (_, _) => presenterItem.Enabled = CurrentShow != null;
        _status.ContextMenuStrip = statusMenu;

        Controls.AddRange(new Control[] { _status, _stopOutputButton, _collapseButton, _takeButton, _fadeButton, _swapButton, _deviceCard.Card });
        foreach (var window in _windows) window.AddTo(this);

        _takeButton.Click += (_, _) => TakeRequested?.Invoke(TransitionStyle.Cut);
        _fadeButton.Click += (_, _) => TakeRequested?.Invoke(TransitionStyle.Dissolve);
        _swapButton.Click += (_, _) => SwapRequested?.Invoke();
        _stopOutputButton.Click += (_, _) => StopProgramRequested?.Invoke();

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        foreach (var window in _windows) ShowIdleSplash(window);
        RefreshNow();
    }

    private RoundedActionButton IconButton(string glyph, string tip)
    {
        var button = new RoundedActionButton { Text = glyph, Height = ButtonHeight, Margin = Padding.Empty, Padding = Padding.Empty };
        ModernUi.StyleButton(button);
        button.Font = new Font(IconFont, 10F);
        _toolTip.SetToolTip(button, tip);
        return button;
    }

    private RoundedActionButton TextButton(string text, string tip)
    {
        var button = new RoundedActionButton { Text = text, Height = ButtonHeight, Margin = Padding.Empty, Padding = Padding.Empty };
        ModernUi.StyleButton(button);
        _toolTip.SetToolTip(button, tip);
        return button;
    }

    // --- Window roles (MainWindow decides; see the class comment) ---

    /// <summary>The window that follows the big screen, or null.</summary>
    public int? ProgramWindow => Array.FindIndex(_windows, w => w.SyncedToProgram) is var i and >= 0 ? i : null;

    /// <summary>The window the take buttons put on air (the last local window used).</summary>
    public int ActiveWindow
    {
        get => _activeWindow;
        set { _activeWindow = Math.Clamp(value, 0, 1); RefreshNow(); }
    }

    public PlaybackEngine? EngineOf(int window) => _windows[window].Engine;

    /// <summary>The first window with nothing in it; with both in use the top one (or, for local playback,
    /// the one that does not follow the big screen).</summary>
    public int FreeWindow(bool forLocalPlayback)
    {
        for (int i = 0; i < _windows.Length; i++) if (!_windows[i].Occupied) return i;
        if (forLocalPlayback && _windows[0].SyncedToProgram) return 1;
        return 0;
    }

    /// <summary>Window <paramref name="window"/> follows the big screen (its own playback stops); any other
    /// window that did is released.</summary>
    public void SyncWindowToProgram(int window)
    {
        for (int i = 0; i < _windows.Length; i++)
        {
            bool sync = i == window;
            if (sync && !_windows[i].SyncedToProgram) _windows[i].Engine?.Stop();
            _windows[i].SyncedToProgram = sync;
        }
        if (_activeWindow == window) _activeWindow = 1 - window;
        RefreshNow();
    }

    /// <summary>No window follows the big screen any more (output stopped).</summary>
    public void ReleaseProgramWindow()
    {
        foreach (var window in _windows) window.SyncedToProgram = false;
        RefreshNow();
    }

    /// <summary>Plays locally in <paramref name="window"/>: it stops following the big screen if it did.</summary>
    public void UseForLocalPlayback(int window)
    {
        _windows[window].SyncedToProgram = false;
        _activeWindow = window;
        RefreshNow();
    }

    /// <summary>The 流程 page's 「下一项 ▶」 controls, shown at the bottom of the column while that page is
    /// visible (null removes them). They take the footer's place.</summary>
    public void SetRunControls(Control? controls)
    {
        if (ReferenceEquals(controls, _runControls)) return;
        if (_runControls != null) Controls.Remove(_runControls);
        _runControls = controls;
        if (controls != null)
        {
            Controls.Add(controls);
            // These controls are owned by the activities page and can be attached after a theme change.
            var palette = MonitorPalette;
            foreach (Control child in controls.Controls)
            {
                if (child is Button button) ModernUi.ApplyButtonRole(button, palette);
                else if (child is Label label && !ModernUi.ApplyForegroundRole(label, palette)) label.ForeColor = palette.Text;
            }
        }
        PerformLayout();
    }

    /// <summary>A control pinned at the bottom (the background-music card), shown while it fits.</summary>
    public void SetFooter(Control footer)
    {
        if (_footer != null) Controls.Remove(_footer);
        _footer = footer;
        Controls.Add(footer);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_windows == null) return; // raised from the base constructor / Padding setter
        int S(int v) => LogicalToDeviceUnits(v);
        var area = DisplayRectangle;
        area = new Rectangle(area.Left + S(Padding.Left) - Padding.Left, area.Top + S(Padding.Top) - Padding.Top,
            Math.Max(1, area.Width - (S(Padding.Horizontal) - Padding.Horizontal)), Math.Max(1, area.Height - (S(Padding.Vertical) - Padding.Vertical)));
        int x = area.Left, w = area.Width, gap = S(Gap), button = S(ButtonHeight);

        int deviceHeight = _deviceConnected ? Math.Max(S(SourceCardHeight), w * 9 / 16) : 0;
        int documentHeight = _documentBarShown ? button + S(6) : 0;
        int windowChrome = MonitorWindow.ChromeHeight(S);
        // FL-03: the (_runControls != null ...) term below is normally 0 now — the 流程 page's
        // run controls live at the bottom of that page itself, and MainWindow no longer lends
        // them to this column. The term stays because SetRunControls is still API (the
        // self-tests embed their own controls through it) and the formula must stay correct
        // for them; in production the monitors get the full height back.
        int fixedHeight = button + gap                                   // status row
            + windowChrome * 2 + gap                                     // captions + transports
            + documentHeight
            + S(36) + gap                                                // take row
            + (deviceHeight > 0 ? deviceHeight + gap : 0)
            + (_runControls != null ? _runControls.Height + gap : 0);
        int fullMonitor = (w - S(4)) * 9 / 16 + S(4);
        // FL-04: the floor is 2×120 logical px (one 120 px minimum per window) — the old 48
        // crushed each window to 24 px in a short column, which reads as broken. When the column
        // is shorter than fixedHeight + floor, the windows keep their minimum and the bottom
        // items clip (existing behaviour — the footer already hides itself when it doesn't fit);
        // the dock deliberately does not scroll, so this hand-rolled OnLayout never has to
        // chase a scroll offset.
        int monitorsTotal = Math.Clamp(area.Height - fixedHeight, S(240), fullMonitor * 2);
        int first = Math.Min(fullMonitor, monitorsTotal / 2), second = Math.Min(fullMonitor, monitorsTotal - first);

        int y = area.Top;
        int collapseWidth = S(64), stopWidth = S(96);
        _collapseButton.Bounds = new Rectangle(area.Right - collapseWidth, y, collapseWidth, button);
        _stopOutputButton.Bounds = new Rectangle(_collapseButton.Left - S(6) - stopWidth, y, stopWidth, button);
        _status.Bounds = new Rectangle(x, y, Math.Max(1, _stopOutputButton.Left - S(8) - x), button);
        y += button + gap;

        y = _windows[0].Layout(x, y, w, first, S);
        if (_documentBar != null && _documentBarShown && _documentBarWindow == 0)
        {
            _documentBar.Bounds = new Rectangle(x, y, w, button);
            y += button + S(6);
        }

        int swapWidth = S(72), takeWidth = (w - swapWidth - S(12)) / 2;
        _takeButton.Bounds = new Rectangle(x, y, takeWidth, S(36));
        _fadeButton.Bounds = new Rectangle(x + takeWidth + S(6), y, takeWidth, S(36));
        _swapButton.Bounds = new Rectangle(area.Right - swapWidth, y, swapWidth, S(36));
        y += S(36) + gap;

        y = _windows[1].Layout(x, y, w, second, S);
        if (_documentBar != null && _documentBarShown && _documentBarWindow == 1)
        {
            _documentBar.Bounds = new Rectangle(x, y, w, button);
            y += button + S(6);
        }
        y += gap;

        _deviceCard.Card.Visible = deviceHeight > 0;
        if (deviceHeight > 0)
        {
            _deviceCard.Card.Bounds = new Rectangle(x, y, w, deviceHeight);
            y += deviceHeight + gap;
        }

        if (_runControls != null)
        {
            _runControls.Width = w; // its own layout sets the height for this width
            _runControls.Bounds = new Rectangle(x, y, w, _runControls.Height);
            y += _runControls.Height + gap;
        }

        if (_footer != null)
        {
            int footerHeight = _footer.Height;
            _footer.Visible = _runControls == null && area.Bottom - y >= footerHeight;
            _footer.Bounds = new Rectangle(x, area.Bottom - footerHeight, w, footerHeight);
        }
    }

    /// <summary>Binds the top window's engine (built by MainWindow on <see cref="PreviewSurface"/>).</summary>
    public void BindPreview(PlaybackEngine preview) => _windows[0].Bind(preview);

    /// <summary>Binds the bottom window's engine (built on <see cref="SecondSurface"/>).</summary>
    public void BindSecondPreview(PlaybackEngine preview) => _windows[1].Bind(preview);

    /// <summary>The Program engine, or null while no extended display is bound.</summary>
    public void AttachProgram(PlaybackEngine? program)
    {
        _program = program;
        if (program == null) ReleaseProgramWindow();
        RefreshNow();
    }

    public void UpdateDeviceCast(string? deviceName, bool connected, bool onProgram)
    {
        _deviceName = deviceName;
        bool relayout = _deviceConnected != connected;
        _deviceConnected = connected;
        _deviceOnProgram = onProgram;
        RefreshNow();
        if (relayout) PerformLayout();
    }

    /// <summary>Idle windows show the branded splash rather than a bare dark box.</summary>
    private void ShowIdleSplash(MonitorWindow window)
    {
        _placeholder ??= PreviewPlaceholder.Create(800, 450);
        window.Surface.ShowImageSurface();
        window.Surface.ContentSurface.SetFrame(_placeholder);
    }

    internal void RefreshTheme()
    {
        var palette = MonitorPalette;
        BackColor = palette.Rail;
        foreach (var window in _windows) window.RefreshTheme(palette);
        RefreshNow();
    }

    /// <summary>Adds the document control row (PDF / Word / presentation controls and Program sync).
    /// It sits under the window the operator is driving — see <see cref="RefreshDocumentBar"/> —
    /// never two bars, one per window.</summary>
    public void BindDocumentSync(DocumentProgramSync sync)
    {
        _documentSync = sync;
        _documentBar = new DocumentControlBar(sync, _toolTip, ButtonHeight) { Visible = false };
        _documentBar.StopProgramRequested += () => StopProgramRequested?.Invoke();
        _documentBar.Changed += RefreshNow;
        Controls.Add(_documentBar);
        _documentBarPreview = sync.Preview;
        _documentBarPreview.DocumentChanged += RefreshNow;
        RefreshNow();
    }

    private void RefreshDocumentBar()
    {
        if (_documentBar == null || _documentSync == null) return;
        // The bar follows the operator's window: the active window when it shows a document, else
        // the other window when it does — the active window wins, a document in the other window
        // is second. (Both windows are previews; the bar is one row, and the sync's Preview
        // channel is re-pointed at the same engine so page turns and "更新大屏" follow it too.)
        // Runs on the 200 ms refresh tick, so a window switch or a document opening moves the bar
        // within one tick; the DocumentChanged subscription moves with the sync for immediacy.
        var active = EngineOf(_activeWindow);
        var other = EngineOf(1 - _activeWindow);
        var target = HasDocument(active) ? active : HasDocument(other) ? other : active;
        if (target != null && !ReferenceEquals(target, _documentSync.Preview))
        {
            if (_documentBarPreview != null) _documentBarPreview.DocumentChanged -= RefreshNow;
            _documentSync.SetPreview(target);
            _documentBarPreview = target;
            target.DocumentChanged += RefreshNow;
        }
        int window = target != null && ReferenceEquals(target, other) ? 1 - _activeWindow : _activeWindow;
        _documentBar.RefreshState();
        bool show = _documentBar.HasDocument;
        if (show == _documentBarShown && window == _documentBarWindow) return;
        _documentBarShown = show;
        _documentBarWindow = window;
        _documentBar.Visible = show;
        PerformLayout();
    }

    private static bool HasDocument(PlaybackEngine? engine) =>
        engine?.Document is { Mode: not (DocumentPlaybackMode.None or DocumentPlaybackMode.Cover) };

    private EveryStage.Terminal.Documents.SlideShowSession? CurrentShow =>
        _program?.DocumentMode == DocumentPlaybackMode.NativeShow ? _program.OfficeSession as EveryStage.Terminal.Documents.SlideShowSession : null;

    /// <summary>演讲者视图: a normal, resizable and maximizable window (like a presentation program's own
    /// presenter view) for the show running on the big screen.</summary>
    private void OpenPresenterView()
    {
        if (_presenterWindow is { IsDisposed: false })
        {
            if (_presenterWindow.WindowState == FormWindowState.Minimized) _presenterWindow.WindowState = FormWindowState.Normal;
            _presenterWindow.Activate();
            return;
        }
        var owner = FindForm();
        if (owner == null || !owner.Visible) return;
        _presenterWindow = new PresenterWindow(() => CurrentShow, () => ProgramScreenBounds?.Invoke(), () => _program);
        // Open on the operator's screen, never on top of the big screen.
        var work = Screen.FromControl(owner).WorkingArea;
        _presenterWindow.StartPosition = FormStartPosition.Manual;
        _presenterWindow.Size = new Size(Math.Min(_presenterWindow.Width, work.Width), Math.Min(_presenterWindow.Height, work.Height));
        _presenterWindow.Location = new Point(work.Left + (work.Width - _presenterWindow.Width) / 2, work.Top + (work.Height - _presenterWindow.Height) / 2);
        _presenterWindow.Show(owner);
    }

    private void RefreshNow()
    {
        if (IsDisposed) return;
        var palette = MonitorPalette;
        _documentSync?.FollowProgramShow();
        RefreshDocumentBar();
        var show = CurrentShow;
        if (!ReferenceEquals(show, _presenterShow))
        {
            _presenterShow = show;
            if (show != null) OpenPresenterView();
            else if (_presenterWindow is { IsDisposed: false }) _presenterWindow.Close();
        }

        bool programLive = _stateMachine.State == OutputState.Active;
        var source = programLive ? _stateMachine.Source : ProgramSource.None;
        var programState = ProgramStatus.Compute(_program != null, programLive, _program?.State);
        (string statusText, UiForegroundRole statusRole) = programState switch
        {
            ProgramOutputState.Disconnected => ("○ 未连接扩展屏 · 仅本机播放", UiForegroundRole.Muted),
            ProgramOutputState.Failed => ("● 大屏播放失败", UiForegroundRole.Danger),
            ProgramOutputState.Live when source == ProgramSource.DeviceCast => ("● 大屏输出中 · 设备来投", UiForegroundRole.Danger),
            ProgramOutputState.Live => ("● 大屏输出中" + (_program?.CurrentFile is { } pf ? " · " + Path.GetFileName(pf.SourcePath) : "")
                + (_program?.IsPaused == true ? " · 已暂停" : ""), UiForegroundRole.Danger),
            _ => (_stateMachine.CastSwitchOn ? "○ 大屏待机" : "○ 大屏待机 · 投屏开关已关闭", _stateMachine.CastSwitchOn ? UiForegroundRole.Muted : UiForegroundRole.Warning),
        };
        SetTextRole(_status, statusText, statusRole);
        _stopOutputButton.Enabled = programLive;

        foreach (var window in _windows) window.Refresh(palette, _program, programLive, source, window.Index == _activeWindow);

        var active = _windows[_activeWindow];
        var activeState = active.Engine?.State ?? PlaybackChannelState.Idle;
        bool canTake = _program != null && !active.SyncedToProgram && active.Engine?.CurrentFile != null
            && activeState is not (PlaybackChannelState.Idle or PlaybackChannelState.Failed or PlaybackChannelState.Loading);
        _takeButton.Enabled = _fadeButton.Enabled = _swapButton.Enabled = canTake;

        _deviceCard.Update(_deviceConnected, _deviceName ?? "设备来投", _deviceOnProgram,
            _program == null ? "未连接扩展屏" : "等待设备来投", _program != null);
    }

    private static void SetText(Control control, string text, Color? color)
    {
        if (control.Text != text) control.Text = text;
        if (color is { } c && control.ForeColor != c) control.ForeColor = c;
    }

    private static void SetTextRole(Control control, string text, UiForegroundRole role)
    {
        if (control.Text != text) control.Text = text;
        ModernUi.SetForegroundRole(control, role);
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _feedbackTimer.Dispose();
            _timer.Stop();
            _timer.Dispose();
            _toolTip.Dispose();
            foreach (var window in _windows) window.Dispose();
            _placeholder?.Dispose();
            _presenterWindow?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// One of the two identical monitor windows: a frame with the window's own preview surface and, on top
    /// of it while it follows the big screen, the <see cref="ProgramMirror"/>; a caption line; and the
    /// compact transport (⏮ ⏯ ⏹ ⏭ · progress · time) that drives this window's engine — or the big screen
    /// while the window follows it. Files can be dropped on it.
    /// </summary>
    private sealed class MonitorWindow
    {
        private readonly SharedPreviewDock _dock;
        private readonly Panel _frame;
        private readonly Label _caption;
        private readonly RoundedActionButton _previous, _playPause, _stop, _next;
        private readonly MediaTrack _progress;
        private readonly Label _time;
        private readonly ErrorCard _error;
        private bool _synced;
        private bool _sawLive;          // the output has been on air since this window started following it
        private DateTime _syncedAt;

        public int Index { get; }
        public PreviewSurface Surface { get; }
        public ProgramMirror Mirror { get; } = new() { Dock = DockStyle.Fill, Visible = false };
        public PlaybackEngine? Engine { get; private set; }
        private PlaybackEngine? _program;

        public bool SyncedToProgram
        {
            get => _synced;
            set
            {
                if (_synced == value) return;
                _synced = value;
                _syncedAt = DateTime.UtcNow;
                _sawLive = false;
                Mirror.Visible = value;
                if (value) Mirror.BringToFront();
            }
        }

        public bool Occupied => _synced || Engine is { State: not PlaybackChannelState.Idle };

        /// <summary>The engine the transport drives: the big screen while following it.</summary>
        private PlaybackEngine? Controlled => _synced ? _program : Engine;

        public MonitorWindow(SharedPreviewDock dock, int index)
        {
            _dock = dock;
            Index = index;
            _frame = new Panel { BackColor = ModernUi.Border, Padding = new Padding(2) };
            Surface = ModernUi.SetBackgroundRole(new PreviewSurface { Dock = DockStyle.Fill, Margin = Padding.Empty }, UiBackgroundRole.MediaBlack);
            ModernUi.SetBackgroundRole(Surface.VideoHost, UiBackgroundRole.MediaBlack);
            ModernUi.SetBackgroundRole(Surface.ContentSurface, UiBackgroundRole.MediaBlack);
            _frame.Controls.Add(Surface);
            _frame.Controls.Add(Mirror);
            Mirror.Click += (_, _) => { if (_program == null) ExtendDisplayWizard.Show(_dock.FindForm()); };
            _caption = ModernUi.SetForegroundRole(new Label { Font = new Font(ModernUi.UiFont, 8.5F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, UiForegroundRole.Muted);
            _previous = dock.IconButton("", "上一项 / 上一页");
            _playPause = dock.IconButton("", "播放 / 暂停");
            _stop = dock.IconButton("", "停止");
            _next = dock.IconButton("", "下一项 / 下一页");
            _progress = new MediaTrack();
            _time = ModernUi.SetForegroundRole(new Label { Text = "--:-- / --:--", Font = new Font(ModernUi.UiFont, 8.5F), TextAlign = ContentAlignment.MiddleRight }, UiForegroundRole.Muted);
            _error = new ErrorCard();
            _error.Retry += () => Engine?.RetryCurrentFile();
            _error.Skip += () => { if (Engine != null && !Engine.NextManual()) Engine.Stop(); };
            _error.Remove += file => dock.RemoveFromLibraryRequested?.Invoke(file);
            _error.OpenExternally += file => ExternalOpener.OpenWithDefaultApp(dock.FindForm(), file.SourcePath);

            _previous.Click += (_, _) => { Activate(); Controlled?.PreviousManual(); };
            _next.Click += (_, _) => { Activate(); Controlled?.NextManual(); };
            _playPause.Click += (_, _) =>
            {
                Activate();
                var engine = Controlled;
                if (engine == null) return;
                if (engine.IsPaused || engine.IsCompleted) engine.Resume(); else if (engine.CanPause) engine.Pause();
            };
            // Stop: a local window stops its own playback; the window following the big screen stops the output.
            _stop.Click += (_, _) =>
            {
                if (_synced) dock.StopProgramRequested?.Invoke();
                else Engine?.Stop();
            };
            _progress.ValueCommitted += fraction =>
            {
                var engine = Controlled;
                if (engine?.Duration is { } duration) engine.SeekTo(TimeSpan.FromTicks((long)(duration.Ticks * fraction)));
            };

            // Drop a library card (in-process MediaFile) or Explorer files on the window: play them here.
            foreach (var target in new Control[] { _frame, Surface, Surface.VideoHost, Surface.ContentSurface, Mirror })
            {
                target.AllowDrop = true;
                target.DragEnter += (_, e) => e.Effect = Dropped(e.Data) is { } ? DragDropEffects.Copy : DragDropEffects.None;
                target.DragDrop += (_, e) =>
                {
                    if (Dropped(e.Data) is not { } drop) return;
                    dock.FileDropped?.Invoke(Index, drop.File, drop.Path);
                };
            }
        }

        private static (MediaFile? File, string? Path)? Dropped(IDataObject? data)
        {
            if (data?.GetData(Panels.MediaGrid.DragFormat) is MediaFile file) return (file, null);
            if (data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && FileLibraryStore.InferKind(paths[0]) is not null and not MediaKind.Audio)
                return (null, paths[0]);
            return null;
        }

        private void Activate()
        {
            if (!_synced) _dock._activeWindow = Index;
        }

        public void AddTo(SharedPreviewDock dock) => dock.Controls.AddRange(new Control[] { _frame, _caption, _previous, _playPause, _stop, _next, _progress, _time });

        public void Bind(PlaybackEngine engine)
        {
            if (Engine != null) Engine.StateChanged -= OnStateChanged;
            Engine = engine;
            Engine.StateChanged += OnStateChanged;
        }

        private void OnStateChanged(PlaybackChannelState state)
        {
            if (_dock.InvokeRequired) { _dock.BeginInvoke(() => OnStateChanged(state)); return; }
            if (state == PlaybackChannelState.Idle) _dock.ShowIdleSplash(this);
            if (state is PlaybackChannelState.Loading or PlaybackChannelState.Playing && !_synced) _dock._activeWindow = Index;
            _dock.RefreshNow();
        }

        /// <summary>Height of everything but the picture: caption + transport row.</summary>
        public static int ChromeHeight(Func<int, int> s) => s(20) + s(4) + s(ButtonHeight) + s(6);

        public int Layout(int x, int y, int w, int pictureHeight, Func<int, int> S)
        {
            _frame.Bounds = new Rectangle(x, y, w, pictureHeight);
            y += pictureHeight + S(4);
            _caption.Bounds = new Rectangle(x, y, w, S(20));
            y += S(20) + S(4);
            int icon = S(32), step = icon + S(4), button = S(ButtonHeight);
            var row = new[] { _previous, _playPause, _stop, _next };
            for (int i = 0; i < row.Length; i++) row[i].Bounds = new Rectangle(x + i * step, y, icon, button);
            int timeWidth = TextRenderer.MeasureText("00:00 / 00:00", _time.Font).Width + S(4);
            int progressLeft = x + row.Length * step + S(4);
            _time.Bounds = new Rectangle(x + w - timeWidth, y, timeWidth, button);
            _progress.Bounds = new Rectangle(progressLeft, y + (button - S(24)) / 2, Math.Max(1, _time.Left - S(6) - progressLeft), S(24));
            return y + button + S(6);
        }

        public void Refresh(UiPalette palette, PlaybackEngine? program, bool programLive, ProgramSource source, bool active)
        {
            _program = program;
            // Output stopped (after having come on air — going on air can take a moment, e.g. a native show):
            // the window is free again. Never on air within 15 s: give up following.
            if (_synced && programLive) _sawLive = true;
            if (_synced && !programLive && (_sawLive || DateTime.UtcNow - _syncedAt > TimeSpan.FromSeconds(15))) SyncedToProgram = false;
            Mirror.OnAir = _synced && programLive;
            Mirror.IdleText = program == null ? "未连接扩展屏\n点此打开扩展屏向导（Win+P）" : "大屏未输出";
            var engine = Controlled;
            var file = engine?.CurrentFile;
            var state = engine?.State ?? PlaybackChannelState.Idle;

            string name = _synced && source == ProgramSource.DeviceCast ? "设备来投"
                : file == null ? "" : Path.GetFileName(file.SourcePath) + (engine?.CurrentDecodeLabel is { } decode ? " · " + decode : "");
            string caption = _synced
                ? (programLive ? "● 大屏 · " + name + (engine?.IsPaused == true ? " · 已暂停" : "") : "大屏未输出")
                : state switch
                {
                    PlaybackChannelState.Loading => name + " · 加载中",
                    PlaybackChannelState.Playing => name,
                    PlaybackChannelState.Paused when engine!.IsCompleted => name + " · 播放完成",
                    PlaybackChannelState.Paused => name + " · 已暂停",
                    PlaybackChannelState.Failed => name + " · 播放失败",
                    _ => "空闲 · 把文件拖到这里，或单击文件播放",
                };
            if (!_synced && engine?.Muted == true && state is PlaybackChannelState.Playing or PlaybackChannelState.Paused) caption += " · 静音（大屏输出中）";
            SetTextRole(_caption, caption, _synced && programLive ? UiForegroundRole.Danger
                : state == PlaybackChannelState.Failed ? UiForegroundRole.Danger : file == null ? UiForegroundRole.Muted : UiForegroundRole.Text);

            // Frame: red while following the big screen (the mirror flashes itself), accent for the active
            // local window, plain otherwise.
            bool hasContent = state is not (PlaybackChannelState.Idle or PlaybackChannelState.Failed);
            var frameColor = _synced ? palette.ProgramBorder : active && hasContent ? palette.Accent : palette.Border;
            if (_frame.BackColor != frameColor) _frame.BackColor = frameColor;

            bool hasFile = file != null && state != PlaybackChannelState.Idle;
            bool controllable = _synced ? programLive && source == ProgramSource.LocalMedia : hasFile;
            _previous.Enabled = _next.Enabled = controllable && state != PlaybackChannelState.Loading;
            _stop.Enabled = _synced ? programLive : hasFile;
            bool canToggle = controllable && engine != null && (engine.CanPause || engine.IsPaused || engine.IsCompleted);
            _playPause.Enabled = canToggle;
            SetText(_playPause, engine != null && (engine.IsPaused || engine.IsCompleted || state != PlaybackChannelState.Playing) ? "" : "", null);
            var duration = engine?.Duration;
            var position = engine?.Position ?? TimeSpan.Zero;
            bool timeline = controllable && engine?.CanSeek == true;
            SetText(_time, timeline ? $"{Format(position)} / {(duration is { } d ? Format(d) : "--:--")}" : "--:-- / --:--", null);
            _progress.Enabled = timeline && duration.HasValue;
            if (!_progress.IsDragging) _progress.Value = timeline && duration is { Ticks: > 0 } dd ? (float)(position.Ticks / (double)dd.Ticks) : 0f;

            if (!_synced && Engine?.State == PlaybackChannelState.Failed && Engine.LastError is { } error)
            {
                _error.Show(error);
                Surface.SetOverlayCard(_error);
            }
            else Surface.SetOverlayCard(null);

            // FL-05: a stopped engine clears its frame (PlaybackEngine.Stop → SetFrame(null)) but
            // the branded idle splash is only re-mounted on the Idle state-change event — and a
            // Take has already moved the engine to Idle, so SetState's dedup guard swallows the
            // event at Stop() time and the splash never comes back. This Refresh runs on the
            // 200 ms timer, so re-mount it here whenever the window is idle, unsynced and
            // frameless; the CurrentFrame check keeps it a one-shot (ShowIdleSplash sets the
            // frame, so the next tick skips it) instead of repainting every tick.
            if (!_synced && (Engine is null || Engine.State == PlaybackChannelState.Idle)
                && Surface.ContentSurface.CurrentFrame == null)
                _dock.ShowIdleSplash(this);
        }

        public void RefreshTheme(UiPalette palette)
        {
            Surface.BackColor = Surface.VideoHost.BackColor = Surface.ContentSurface.BackColor = Color.Black;
            _time.ForeColor = palette.Muted;
            _error.RefreshTheme(palette);
        }

        public void Dispose()
        {
            if (Engine != null) Engine.StateChanged -= OnStateChanged;
            Surface.ContentSurface.SetFrame(null);
            _error.Dispose();
        }
    }

    private sealed class DeviceCastCard
    {
        private readonly Label _title;
        private readonly Label _state;
        private readonly Label _caption;
        private readonly RoundedActionButton _actionButton;
        private readonly Panel _videoFrame;
        private readonly Panel _labels;
        private bool _onProgram;

        public GlassPanel Card { get; }
        public Control VideoHost { get; }
        public event Action? TakeRequested;
        public event Action? RevertRequested;

        public DeviceCastCard()
        {
            Card = new GlassPanel
            {
                Height = SourceCardHeight, Margin = Padding.Empty,
                Padding = new Padding(6, 5, 8, 5), CornerRadius = 9, GlassTint = ModernUi.SurfaceRaised,
            };

            _videoFrame = ModernUi.SetBackgroundRole(new Panel { Dock = DockStyle.Fill, Visible = false }, UiBackgroundRole.MediaBlack);
            VideoHost = ModernUi.SetBackgroundRole(new Control { Dock = DockStyle.Fill }, UiBackgroundRole.MediaBlack);
            _ = VideoHost.Handle;
            _caption = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(new Label
            {
                Dock = DockStyle.Bottom, Height = 30, Font = new Font(ModernUi.UiFont, 8.5F),
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 76, 0),
                AutoEllipsis = true,
            }, UiForegroundRole.Text), UiBackgroundRole.SurfaceRaised);
            _actionButton = new RoundedActionButton
            {
                Text = "投屏", Width = 62, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            ModernUi.StyleButton(_actionButton, primary: true);
            _actionButton.Click += (_, _) =>
            {
                if (_onProgram) RevertRequested?.Invoke(); else TakeRequested?.Invoke();
            };
            _videoFrame.Controls.Add(VideoHost);
            _videoFrame.Controls.Add(_caption);
            _videoFrame.Controls.Add(_actionButton);
            _videoFrame.Resize += (_, _) => _actionButton.Location = new Point(
                Math.Max(4, _videoFrame.ClientSize.Width - _actionButton.Width - 8),
                Math.Max(4, _videoFrame.ClientSize.Height - _actionButton.Height - 8));

            _labels = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(6, 0, 0, 0) };
            _title = ModernUi.SetForegroundRole(new Label
            {
                Text = "设备来投", Dock = DockStyle.Top, Height = 19,
                Font = new Font(ModernUi.UiFontSemibold, 9F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            }, UiForegroundRole.Text);
            _state = new Label { Dock = DockStyle.Fill, Font = new Font(ModernUi.UiFont, 8F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _labels.Controls.Add(_state);
            _labels.Controls.Add(_title);
            Card.Controls.Add(_labels);
            Card.Controls.Add(_videoFrame);
        }

        public void Update(bool connected, string title, bool onProgram, string idleStatus, bool displayBound)
        {
            var palette = MonitorPalette;
            _onProgram = onProgram;
            _videoFrame.Visible = connected;
            _labels.Visible = !connected;
            _title.Text = connected ? title : "设备来投";
            _caption.Text = connected
                ? $"{title} · {(onProgram ? "大屏中" : "预览中")}"
                : "";
            _state.Text = connected
                ? (onProgram ? "● 大屏输出中" : "● 预览中（未上屏）")
                : "● " + idleStatus;
            ModernUi.SetForegroundRole(_state, connected ? (onProgram ? UiForegroundRole.Danger : UiForegroundRole.Success) : UiForegroundRole.Muted);
            _title.ForeColor = _caption.ForeColor = palette.Text;
            _caption.BackColor = palette.SurfaceRaised;
            _videoFrame.BackColor = VideoHost.BackColor = Color.Black;
            _actionButton.Text = onProgram ? "回退" : "投屏";
            _actionButton.Enabled = connected && (displayBound || onProgram);
            ModernUi.StyleButton(_actionButton, primary: !onProgram, danger: onProgram);
            _actionButton.BringToFront();
            Card.GlassTint = onProgram
                ? Color.FromArgb(202, Panels.ActivityRowView.Blend(palette.GlassTint, palette.ProgramBorder, 0.19f))
                : palette.GlassTint;
            Card.Invalidate(true);
        }
    }

    /// <summary>The Preview error card: what failed, where, and what to do about it.</summary>
    private sealed class ErrorCard : Panel
    {
        private readonly Label _title;
        private readonly Label _detail;
        private readonly Label _suggestion;
        private PlaybackError? _error;

        public event Action? Retry;
        public event Action? Skip;
        public event Action<MediaFile>? Remove;
        public event Action<MediaFile>? OpenExternally;

        public ErrorCard()
        {
            ModernUi.SetBackgroundRole(this, UiBackgroundRole.Surface);
            Padding = new Padding(14, 8, 14, 8);
            var smallFont = new Font(ModernUi.UiFont, 8.5F);
            _title = ModernUi.SetForegroundRole(new Label { Dock = DockStyle.Top, Height = 22, Font = new Font(ModernUi.UiFontSemibold, 10F), AutoEllipsis = true }, UiForegroundRole.Danger);
            _detail = ModernUi.SetForegroundRole(new Label { Dock = DockStyle.Top, Height = 34, Font = smallFont, AutoEllipsis = true }, UiForegroundRole.Text);
            _suggestion = ModernUi.SetForegroundRole(new Label { Dock = DockStyle.Fill, Font = smallFont, AutoEllipsis = true }, UiForegroundRole.Muted);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty };
            actions.Controls.Add(Action("重试", () => Retry?.Invoke(), primary: true));
            actions.Controls.Add(Action("跳过", () => Skip?.Invoke()));
            actions.Controls.Add(Action("从文件库移除", () => { if (_error != null) Remove?.Invoke(_error.File); }));
            actions.Controls.Add(Action("用系统程序打开", () => { if (_error != null) OpenExternally?.Invoke(_error.File); }));
            Controls.Add(_suggestion);
            Controls.Add(actions);
            Controls.Add(_detail);
            Controls.Add(_title);

            static RoundedActionButton Action(string text, Action onClick, bool primary = false)
            {
                var button = new RoundedActionButton { Text = text, AutoSize = false, Width = TextRenderer.MeasureText(text, new Font(ModernUi.UiFont, 9F)).Width + 26, Height = 28, Margin = new Padding(0, 2, 8, 0), Padding = Padding.Empty };
                ModernUi.StyleButton(button, primary: primary);
                button.Click += (_, _) => onClick();
                return button;
            }
        }

        public void Show(PlaybackError error)
        {
            if (ReferenceEquals(error, _error)) return;
            _error = error;
            _title.Text = $"⚠ 无法播放：{error.FileName}";
            string codecs = string.Join(" / ", new[] { error.VideoCodec, error.AudioCodec }.Where(c => !string.IsNullOrEmpty(c)));
            _detail.Text = $"失败阶段：{error.Stage}　·　格式：{(error.Extension.Length > 0 ? error.Extension : "未知")}"
                + (codecs.Length > 0 ? $"　·　编码：{codecs}" : "") + $"　·　{error.Message}";
            _suggestion.Text = "建议：" + error.Suggestion;
            RefreshTheme(MonitorPalette);
        }

        public void RefreshTheme(UiPalette palette)
        {
            ModernUi.ApplyBackgroundRole(this, palette);
            ModernUi.ApplyForegroundRole(_title, palette);
            ModernUi.ApplyForegroundRole(_detail, palette);
            ModernUi.ApplyForegroundRole(_suggestion, palette);
            void ApplyButtons(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if (child is Button button) ModernUi.ApplyButtonRole(button, palette);
                    ApplyButtons(child);
                }
            }
            ApplyButtons(this);
        }
    }
}

/// <summary>Derives <see cref="ProgramOutputState"/> from what the Terminal knows.</summary>
internal static class ProgramStatus
{
    public static ProgramOutputState Compute(bool displayBound, bool outputActive, PlaybackChannelState? programEngineState)
    {
        if (!displayBound) return ProgramOutputState.Disconnected;
        if (programEngineState == PlaybackChannelState.Failed) return ProgramOutputState.Failed;
        return outputActive ? ProgramOutputState.Live : ProgramOutputState.Standby;
    }
}

/// <summary>A thin rounded track with a knob (progress / volume), click or drag to set.
/// <see cref="ValueCommitted"/> fires on mouse-up; <see cref="ValueChanging"/> while dragging.</summary>
internal sealed class MediaTrack : Control
{
    private float _value;
    public bool IsDragging { get; private set; }
    public event Action<float>? ValueCommitted;
    public event Action<float>? ValueChanging;

    public float Value
    {
        get => _value;
        set { float v = Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f); if (Math.Abs(v - _value) > 0.0005f) { _value = v; Invalidate(); } }
    }

    public MediaTrack()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 24;
    }

    private float FractionAt(int x) => Math.Clamp((x - 6) / (float)Math.Max(1, Width - 12), 0f, 1f);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        IsDragging = true;
        Capture = true;
        Value = FractionAt(e.X);
        ValueChanging?.Invoke(Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsDragging) return;
        Value = FractionAt(e.X);
        ValueChanging?.Invoke(Value);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsDragging) return;
        IsDragging = false;
        Capture = false;
        Value = FractionAt(e.X);
        ValueCommitted?.Invoke(Value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        var track = new Rectangle(6, cy - 2, Math.Max(1, Width - 12), 4);
        var palette = ModernUi.PaletteFor(this);
        using (var bg = new SolidBrush(palette.Border)) g.FillRectangle(bg, track);
        if (!Enabled) return;
        int filled = (int)(track.Width * _value);
        using (var fg = new SolidBrush(palette.Accent)) g.FillRectangle(fg, track.X, track.Y, filled, track.Height);
        using var knob = new SolidBrush(palette.IsHighContrast ? SystemColors.HighlightText : Color.White);
        g.FillEllipse(knob, track.X + filled - 6, cy - 6, 12, 12);
    }
}

/// <summary>The small 「预览」 / 「大屏」 tag over a monitor: filled in the channel colour (accent for
/// Preview, danger for Program) while that channel shows something, outlined and muted otherwise.</summary>
internal sealed class Badge : Control
{
    private bool _active;
    public bool Danger { get; init; }

    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    public Badge(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = new Font(ModernUi.UiFontSemibold, 8F);
    }

    public int PreferredWidth => TextRenderer.MeasureText(Text, Font).Width + LogicalToDeviceUnits(8);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int d = LogicalToDeviceUnits(8);
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        var palette = ModernUi.PaletteFor(this);
        Color channel = Danger ? palette.DangerFill : palette.Preview;
        Color fore;
        if (_active)
        {
            using var fill = new SolidBrush(channel);
            g.FillPath(fill, path);
            fore = Danger ? palette.DangerFillForeground : palette.PreviewForeground;
        }
        else
        {
            using var pen = new Pen(palette.Border);
            g.DrawPath(pen, path);
            fore = palette.Muted;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// Presenter information while the big screen runs a native slide show: elapsed show time, slide and
/// animation position, the next slide's picture and the current slide's speaker notes. The picture and
/// notes are fetched from the show's application when the slide changes.
/// </summary>
internal sealed class PresenterStrip : Panel
{
    public const int LogicalHeight = 118;
    private readonly Label _status;
    private readonly PictureBox _next;
    private readonly Label _nextCaption;
    private readonly Label _notes;
    private EveryStage.Terminal.Documents.SlideShowSession? _show;
    private DateTime _started;
    private int _slide = -1;
    private int _version;
    private bool _hasNotes;

    public PresenterStrip()
    {
        BackColor = Color.Transparent;
        _status = new Label { ForeColor = ModernUi.Text, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFontSemibold, 9F), AutoEllipsis = true };
        _next = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        _nextCaption = new Label { ForeColor = ModernUi.Muted, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 8F), Text = "下一张" };
        _notes = new Label { ForeColor = ModernUi.Text, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 9F), AutoEllipsis = true };
        Controls.AddRange(new Control[] { _status, _next, _nextCaption, _notes });
        RefreshTheme();
    }

    private UiPalette ChromePalette => ThemeManager.HighContrast ? UiPalette.For(EveryStage.Terminal.Data.AppTheme.Dark) : UiPalette.Normal(EveryStage.Terminal.Data.AppTheme.Dark);
    internal void RefreshTheme()
    {
        var palette = ChromePalette;
        BackColor = palette.Background;
        _status.ForeColor = palette.Text;
        _nextCaption.ForeColor = palette.Muted;
        _notes.ForeColor = _hasNotes ? palette.Text : palette.Muted;
        _next.BackColor = palette.IsHighContrast ? SystemColors.Window : Color.Black;
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_notes == null) return;
        int w = ClientSize.Width;
        _status.Bounds = new Rectangle(0, 0, w, S(20));
        int thumbWidth = w * 42 / 100, thumbHeight = thumbWidth * 9 / 16;
        _next.Bounds = new Rectangle(0, S(24), thumbWidth, thumbHeight);
        _nextCaption.Bounds = new Rectangle(0, _next.Bottom + S(1), thumbWidth, S(16));
        _notes.Bounds = new Rectangle(thumbWidth + S(10), S(24), Math.Max(1, w - thumbWidth - S(10)), ClientSize.Height - S(24));
    }

    public void Update(EveryStage.Terminal.Documents.SlideShowSession? show)
    {
        RefreshTheme();
        if (!ReferenceEquals(show, _show))
        {
            ++_version; // Invalidate notes and images still loading for the previous show.
            _show = show;
            _started = DateTime.Now;
            _slide = -1;
            _status.Text = "";
            _notes.Text = "";
            _hasNotes = false;
            _notes.ForeColor = ChromePalette.Muted;
            var old = _next.Image;
            _next.Image = null;
            old?.Dispose();
            _nextCaption.Text = "下一张";
        }
        if (show == null) return;
        var position = show.Position;
        var elapsed = DateTime.Now - _started;
        string clicks = position.Clicks > 0 ? $" · 动画 {position.Click} / {position.Clicks}" : "";
        string screen = position.Screen switch
        {
            EveryStage.Terminal.Documents.SlideShowScreen.Black => " · 黑屏",
            EveryStage.Terminal.Documents.SlideShowScreen.White => " · 白屏",
            EveryStage.Terminal.Documents.SlideShowScreen.Paused => " · 已暂停",
            EveryStage.Terminal.Documents.SlideShowScreen.Done => " · 放映结束",
            _ => "",
        };
        string text = $"大屏放映 {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2} · 第 {Math.Max(1, position.Slide)} / {show.SlideCount} 页{clicks}{screen}";
        if (_status.Text != text) _status.Text = text;
        if (position.Slide >= 1 && position.Slide != _slide)
        {
            _slide = position.Slide;
            _ = LoadAsync(show, position.Slide, ++_version);
        }
    }

    private async Task LoadAsync(EveryStage.Terminal.Documents.SlideShowSession show, int slide, int version)
    {
        string notes = "";
        Bitmap? next = null;
        try { notes = await show.NotesAsync(slide); } catch (Exception) { }
        if (slide < show.SlideCount)
            try { next = await show.GetSlideImageAsync(slide + 1, 480); } catch (Exception) { }
        if (IsDisposed || version != _version) { next?.Dispose(); return; }
        SetNotes(notes);
        var old = _next.Image;
        _next.Image = next;
        old?.Dispose();
        _nextCaption.Text = slide < show.SlideCount ? $"下一张 · 第 {slide + 1} 页" : "已是最后一页";
    }

    private void SetNotes(string notes)
    {
        _hasNotes = !string.IsNullOrWhiteSpace(notes);
        _notes.Text = _hasNotes ? notes.Trim() : "（这一页没有备注）";
        _notes.ForeColor = _hasNotes ? ChromePalette.Text : ChromePalette.Muted;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _next.Image?.Dispose(); _next.Image = null; }
        base.Dispose(disposing);
    }
}
