using System.Drawing;
using System.Windows.Forms;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Documents;

public enum OfficeSessionStatus { Opening, Ready, Editing, Disconnected, Ended, Failed, Closed }

/// <summary>
/// One WPS/Office document opened for EveryStage: the application instance (driven on its own STA
/// thread), its window, and the capture of that window. Shared by the Preview and Program channels through
/// <see cref="OfficeSessionManager"/> — both show frames from the same capture, so what Program shows is
/// exactly what the operator's editor or slide show shows (unless Program is frozen).
/// </summary>
public abstract class OfficeSession
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(60);
    private protected static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private protected readonly StaDispatcher Dispatcher;
    private protected OfficeApplicationController Controller;
    private protected WindowCaptureSource? Capture;
    private readonly System.Threading.Timer _health;
    private readonly DocumentLogger _log;
    private SharedFrame? _lastFrame; // survives a lost capture (Program keeps the last frame)
    private readonly object _frameGate = new();
    internal int References;

    private protected OfficeSession(string path, DocumentFormat format, OfficeSuite suite, OfficeAppKind kind, DocumentLogger log)
    {
        SourcePath = path;
        Format = format;
        _log = log;
        Dispatcher = new StaDispatcher($"EveryStage {format} automation");
        Controller = new OfficeApplicationController(suite, kind);
        _health = new System.Threading.Timer(_ => CheckHealth(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string SourcePath { get; }
    public DocumentFormat Format { get; }
    public OfficeSuite Suite => Controller.Suite;
    public string SuiteName => Controller.SuiteName;
    public string? Version => Controller.Version;
    public OfficeSessionStatus Status { get; private set; } = OfficeSessionStatus.Opening;
    public string? StatusMessage { get; private set; }
    public OfficeOperationException? Error { get; private set; }
    public WindowCaptureMethod CaptureMethod => Capture?.ActiveMethod ?? WindowCaptureMethod.Auto;
    public string? CaptureFallbackReason => Capture?.FallbackReason;
    public long FrameCount => Capture?.FrameCount ?? 0;

    /// <summary>A WPS/Office dialog is waiting in the editor (answer it there; it never reaches Program).</summary>
    public bool DialogPending { get; private set; }

    /// <summary>Raised on a worker thread when a newer captured frame is available.</summary>
    public event Action? FrameAvailable;

    /// <summary>Raised on any thread when <see cref="Status"/>, the slide position or a flag changes.</summary>
    public event Action? Changed;

    /// <summary>Newest frame with a reference for the caller, or the last frame of a lost capture.</summary>
    public SharedFrame? AcquireLatestFrame()
    {
        var live = Capture?.AcquireLatest();
        if (live != null) return live;
        lock (_frameGate) return _lastFrame?.AddRef();
    }

    private protected void SetStatus(OfficeSessionStatus status, string? message = null)
    {
        Status = status;
        StatusMessage = message;
        RaiseChanged();
    }

    private protected void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }

    private protected void Fail(Exception ex)
    {
        Error = ex as OfficeOperationException
            ?? new OfficeOperationException(OfficeStage.OpenFile, Controller.SuiteName, Controller.Version, ex.Message, ex);
        _log.LogFailure(OfficeOperationException.StageName(Error.Stage), SourcePath, SuiteName, Version, ex);
        SetStatus(OfficeSessionStatus.Failed, Error.Message);
    }

    private protected void LogEvent(string evt, string? detail = null) => _log.LogEvent(evt, SourcePath, SuiteName, Version, detail);

    /// <summary>Starts (or restarts) capturing <paramref name="window"/>, optionally cropped.</summary>
    private protected void StartCapture(IntPtr window, Func<Rectangle?>? region)
    {
        StopCapture(keepLastFrame: true);
        var capture = new WindowCaptureSource(window, region);
        capture.FrameAvailable += () => { try { FrameAvailable?.Invoke(); } catch (Exception) { } };
        capture.Disconnected += reason => OnCaptureLost(capture, reason);
        capture.Start();
        Capture = capture;
        LogEvent("capture_started", $"method={capture.ActiveMethod} fallback={capture.FallbackReason}");
        _health.Change(1000, 1000);
    }

    private protected void StopCapture(bool keepLastFrame)
    {
        var capture = Capture;
        Capture = null;
        if (capture == null) return;
        if (keepLastFrame && capture.AcquireLatest() is { } frame)
        {
            SharedFrame? previous;
            lock (_frameGate) { previous = _lastFrame; _lastFrame = frame; }
            previous?.Release();
        }
        capture.Dispose();
    }

    private void OnCaptureLost(WindowCaptureSource capture, string reason)
    {
        if (!ReferenceEquals(capture, Capture) || Status is OfficeSessionStatus.Closed or OfficeSessionStatus.Editing or OfficeSessionStatus.Ended) return;
        OnWindowLost(reason);
    }

    /// <summary>The captured window went away (application closed, show ended, window recreated).</summary>
    private protected virtual void OnWindowLost(string reason)
    {
        LogEvent("capture_lost", reason);
        SetStatus(OfficeSessionStatus.Disconnected, "文档窗口已断开");
    }

    private void CheckHealth()
    {
        if (Status is OfficeSessionStatus.Closed or OfficeSessionStatus.Failed) return;
        bool dialog = false;
        try { dialog = Controller.HasPendingDialog(); } catch (Exception) { }
        if (dialog != DialogPending)
        {
            DialogPending = dialog;
            RaiseChanged();
        }
        OnHealthTick();
    }

    private protected virtual void OnHealthTick() { }

    /// <summary>The shared open: the first channel starts it, the other awaits the same task (UI thread).</summary>
    public Task EnsureOpenAsync(DocumentPlaybackSettings settings) => OpenTask ??= OpenAsync(settings);

    public Task? OpenTask { get; private set; }

    public async Task OpenAsync(DocumentPlaybackSettings settings)
    {
        try
        {
            if (OfficeApplicationController.IsPasswordProtected(SourcePath))
                throw new OfficeOperationException(OfficeStage.OpenFile, SuiteName, null, "文档受密码保护，无法在 EveryStage 中打开。");
            await Dispatcher.InvokeAsync(() => { Controller.Start(); OpenCore(settings); return true; }, OpenTimeout).ConfigureAwait(false);
            LogEvent("opened");
            await AfterOpenAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex is TimeoutException ? new OfficeOperationException(OfficeStage.OpenFile, SuiteName, Version, ex.Message, ex) : ex);
            StopCapture(keepLastFrame: false);
            _health.Change(Timeout.Infinite, Timeout.Infinite);
            // Leave no application behind: a hung call (a dialog inside WPS/Office) can only be ended by
            // terminating the process this session started; otherwise quit it normally.
            if (ex is TimeoutException) Controller.KillStartedProcesses();
            else
            {
                try { await Dispatcher.InvokeAsync(() => { Controller.Quit(); return true; }, TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
                catch (Exception) { Controller.KillStartedProcesses(); }
            }
            throw Error!;
        }
    }

    /// <summary>Runs on the automation thread: open the document.</summary>
    private protected abstract void OpenCore(DocumentPlaybackSettings settings);

    /// <summary>Runs on any thread after opening: find the window and start capturing.</summary>
    private protected abstract Task AfterOpenAsync(DocumentPlaybackSettings settings);

    /// <summary>Whether the operator can have changed the document in this session. Slide shows are
    /// reported as modified by WPS/PowerPoint just for running or exporting, so only the editor counts.</summary>
    private protected virtual bool MayHaveEdits => true;

    /// <summary>Whether the open document has unsaved edits (false when it cannot be asked).</summary>
    public async Task<bool> IsDirtyAsync()
    {
        if (!MayHaveEdits) return false;
        try { return await Dispatcher.InvokeAsync(() => Controller.IsDirty, TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    /// <summary>Closes the document (saving first when asked), quits the application and stops the
    /// capture. Never throws; failures are logged.</summary>
    public async Task CloseAsync(bool saveChanges)
    {
        if (Status == OfficeSessionStatus.Closed) return;
        _health.Change(Timeout.Infinite, Timeout.Infinite);
        StopCapture(keepLastFrame: false);
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try { Controller.CloseDocument(saveChanges); }
                catch (Exception ex) { _log.LogFailure(OfficeOperationException.StageName(OfficeStage.Close), SourcePath, SuiteName, Version, ex); }
                Controller.Quit();
                return true;
            }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogFailure(OfficeOperationException.StageName(OfficeStage.Close), SourcePath, SuiteName, Version, ex);
            // Same as a hung open: a blocked application (a dialog inside WPS/Office) would otherwise
            // outlive EveryStage and keep the file locked, so the next open comes up read-only.
            if (ex is TimeoutException) Controller.KillStartedProcesses();
        }
        _health.Dispose();
        Dispatcher.Dispose();
        SharedFrame? last;
        lock (_frameGate) { last = _lastFrame; _lastFrame = null; }
        last?.Release();
        LogEvent("closed");
        SetStatus(OfficeSessionStatus.Closed);
    }
}

/// <summary>
/// A Word document in a real WPS Writer / Microsoft Word editor window on the operator's screen. The
/// document area (the "_WwG" canvas: no title bar, ribbon, rulers, scrollbars or status bar) is captured
/// for Preview and Program; the operator scrolls and edits in the editor.
/// </summary>
public sealed class WriterSession : OfficeSession
{
    private IntPtr _window;
    private IntPtr _canvas;

    internal WriterSession(string path, OfficeSuite suite, DocumentLogger log)
        : base(path, DocumentFormat.Word, suite, OfficeAppKind.Writer, log) { }

    public bool ReadOnly { get; private set; }
    public string? ReadOnlyReason { get; private set; }
    public IntPtr EditorWindow => _window;

    private protected override void OpenCore(DocumentPlaybackSettings settings)
    {
        bool attributeReadOnly = new FileInfo(SourcePath).IsReadOnly;
        bool locked = OfficeApplicationController.IsLockedByAnotherProcess(SourcePath);
        ReadOnlyReason = !settings.AllowEdit ? "活动设置为不允许编辑" : attributeReadOnly ? "文件为只读" : locked ? "文件被其他程序占用" : null;
        Controller.OpenWriter(SourcePath, ReadOnlyReason != null);
        ReadOnly = ReadOnlyReason != null || Controller.WriterIsReadOnly;
        if (ReadOnly && ReadOnlyReason == null) ReadOnlyReason = "文档以只读方式打开";
        if (settings.InitialScrollPercent > 0) Controller.SetWriterScrollPercent(settings.InitialScrollPercent);
        _window = Controller.WriterWindow();
    }

    private protected override Task AfterOpenAsync(DocumentPlaybackSettings settings)
    {
        ConnectCapture();
        SetStatus(OfficeSessionStatus.Ready, ReadOnlyReason);
        return Task.CompletedTask;
    }

    private void ConnectCapture()
    {
        _canvas = IntPtr.Zero;
        StartCapture(_window, DocumentArea);
    }

    /// <summary>The document canvas on screen (looked up again when the editor recreates it).</summary>
    private Rectangle? DocumentArea()
    {
        if (_canvas == IntPtr.Zero || !NativeMethods.IsWindow(_canvas) || !NativeMethods.IsWindowVisible(_canvas))
            _canvas = NativeMethods.FindLargestChild(_window, "_WwG");
        if (_canvas == IntPtr.Zero) return null; // no canvas found: the whole window
        var rect = NativeMethods.WindowRect(_canvas);
        return rect.Width > 20 && rect.Height > 20 ? rect : null;
    }

    /// <summary>"打开编辑": brings the editor window to the front on the operator's screen.</summary>
    public void BringEditorToFront()
    {
        if (_window != IntPtr.Zero && NativeMethods.IsWindow(_window)) NativeMethods.BringToFront(_window);
    }

    public Task SaveAsync() => Dispatcher.InvokeAsync(() => { Controller.Save(); return true; }, CallTimeout)
        .ContinueWith(t => { if (t.IsFaulted) throw t.Exception!.InnerException!; LogEvent("saved"); }, TaskScheduler.Default);

    public Task SaveAsAsync(string path) => Dispatcher.InvokeAsync(() => { Controller.SaveAs(path); return true; }, CallTimeout)
        .ContinueWith(t => { if (t.IsFaulted) throw t.Exception!.InnerException!; LogEvent("saved_as"); ReadOnly = false; ReadOnlyReason = null; RaiseChanged(); }, TaskScheduler.Default);

    /// <summary>Self-test hook: an edit made in the editor.</summary>
    internal Task InsertTextForTestAsync(string text) => Dispatcher.InvokeAsync(() => { Controller.InsertTextAtStart(text); return true; }, CallTimeout);

    /// <summary>"适应页面" (true) / "适应宽度" (false): the editor's own zoom, so the captured canvas
    /// shows the whole page or the page width.</summary>
    public Task SetPageFitAsync(bool wholePage) => Dispatcher.InvokeAsync(() => { Controller.SetWriterPageFit(wholePage); return true; }, CallTimeout);

    /// <summary>"重新连接": finds the editor window again (the application may have recreated it) and
    /// restarts the capture. Fails when the application itself is gone.</summary>
    public async Task ReconnectAsync()
    {
        var window = await Dispatcher.InvokeAsync(() => Controller.WriterWindow(), CallTimeout).ConfigureAwait(false);
        _window = window;
        ConnectCapture();
        SetStatus(OfficeSessionStatus.Ready, ReadOnlyReason);
        LogEvent("reconnected");
    }
}

/// <summary>A running slide show state as read from the application.</summary>
public readonly record struct SlideShowPosition(int Slide, int SlideCount, int Click, int Clicks, SlideShowScreen Screen);

public enum SlideShowScreen { Running = 1, Paused = 2, Black = 3, White = 4, Done = 5 }

/// <summary>
/// A presentation played as a native PowerPoint / WPS slide show (animations, transitions, embedded video
/// and audio). The show runs full screen at the Program resolution in a window placed on the Program
/// display below the EveryStage output window (or parked behind the operator's windows when there is no
/// extended display); its window is captured and shown by EveryStage, so Program never shows an editor,
/// title bar, task bar or dialog. Edit mode ends the show and opens the editor; reload restarts the show
/// at the slide it was on.
/// </summary>
public sealed class SlideShowSession : OfficeSession
{
    private IntPtr _showWindow;
    private Rectangle _bounds;
    private int _editSlide = 1;
    private readonly Dictionary<(int Slide, int Width), string> _slideImages = new();
    private readonly string _imageDirectory = Path.Combine(Path.GetTempPath(), "EveryStage", "slides", Guid.NewGuid().ToString("N"));

    internal SlideShowSession(string path, OfficeSuite suite, DocumentLogger log)
        : base(path, DocumentFormat.Presentation, suite, OfficeAppKind.Presentation, log) { }

    public int SlideCount { get; private set; }
    public SlideShowPosition Position { get; private set; }
    public bool ShowRunning { get; private set; }
    public IntPtr ShowWindow => _showWindow;

    /// <summary>Where the show window is placed before the first frame (set by the manager).</summary>
    internal Rectangle InitialBounds { get; set; }
    internal int InitialSlide { get; set; } = 1;
    internal int? EndSlide { get; set; }
    internal bool Loop { get; set; }

    private protected override void OpenCore(DocumentPlaybackSettings settings)
    {
        Controller.OpenPresentation(SourcePath, withWindow: false);
        SlideCount = Controller.SlideCount;
        if (SlideCount <= 0) throw new OfficeOperationException(OfficeStage.OpenFile, SuiteName, Version, "演示文稿没有幻灯片。");
    }

    private protected override async Task AfterOpenAsync(DocumentPlaybackSettings settings) =>
        await StartShowAsync(InitialSlide, InitialBounds).ConfigureAwait(false);

    private async Task StartShowAsync(int slide, Rectangle bounds)
    {
        _bounds = bounds;
        var window = await Dispatcher.InvokeAsync(() => Controller.StartSlideShow(slide, EndSlide, Loop, bounds), CallTimeout).ConfigureAwait(false);
        _showWindow = window;
        ShowRunning = true;
        StartCapture(window, null);
        await RefreshPositionAsync().ConfigureAwait(false);
        SetStatus(OfficeSessionStatus.Ready);
        LogEvent("show_started", $"slide={slide} bounds={bounds}");
    }

    /// <summary>Moves the show to new output bounds (Program attached / detached).</summary>
    public void PlaceShow(Rectangle bounds)
    {
        _bounds = bounds;
        if (_showWindow != IntPtr.Zero && NativeMethods.IsWindow(_showWindow)) OfficeApplicationController.PlaceShowWindow(_showWindow, bounds);
    }

    private protected override void OnHealthTick() => _ = RefreshPositionAsync();

    private async Task RefreshPositionAsync()
    {
        if (!ShowRunning || Status is OfficeSessionStatus.Editing or OfficeSessionStatus.Closed) return;
        try
        {
            var (slide, click, clicks, state) = await Dispatcher.InvokeAsync(() => Controller.ShowRunning ? Controller.ShowPosition() : (0, 0, 0, Controller.ApplicationAlive ? 5 : -1), CallTimeout).ConfigureAwait(false);
            if (state < 0) { OnApplicationExited(); return; }
            var position = new SlideShowPosition(slide, SlideCount, click, clicks, (SlideShowScreen)Math.Clamp(state, 1, 5));
            bool ended = position.Screen == SlideShowScreen.Done;
            if (position != Position)
            {
                // Slide 0 is reported for a moment during a transition; keep the previous slide number.
                Position = position.Slide == 0 && !ended ? position with { Slide = Position.Slide } : position;
                RaiseChanged();
            }
            if (ended && Status == OfficeSessionStatus.Ready) SetStatus(OfficeSessionStatus.Ended, "放映已结束");
        }
        catch (Exception) { }
    }

    private int _reattaching;

    private protected override void OnWindowLost(string reason)
    {
        // The application can recreate the show window while the show keeps running: follow the new
        // window once before reporting the show as gone.
        if (Status == OfficeSessionStatus.Ready && Interlocked.Exchange(ref _reattaching, 1) == 0)
        {
            _ = ReattachAsync(reason);
            return;
        }
        ShowRunning = false;
        base.OnWindowLost(reason);
        if (Status == OfficeSessionStatus.Disconnected) SetStatusMessage("放映窗口已关闭");
    }

    /// <summary>The presentation application died during the show (crash, killed): not an ordinary end —
    /// the session fails so playback can fall back to static slides.</summary>
    private void OnApplicationExited()
    {
        if (Status is OfficeSessionStatus.Failed or OfficeSessionStatus.Closed) return;
        ShowRunning = false;
        StopCapture(keepLastFrame: true);
        Fail(new OfficeOperationException(OfficeStage.Control, SuiteName, Version, $"{SuiteName} 演示程序意外退出"));
    }

    private async Task ReattachAsync(string reason)
    {
        IntPtr window = IntPtr.Zero;
        try
        {
            var bounds = _bounds;
            bool alive;
            (window, alive) = await Dispatcher.InvokeAsync(() => (Controller.RefindShowWindow(bounds), Controller.ApplicationAlive), CallTimeout).ConfigureAwait(false);
            if (!alive) { OnApplicationExited(); return; }
            if (window != IntPtr.Zero && window != _showWindow && Status == OfficeSessionStatus.Ready)
            {
                _showWindow = window;
                OfficeApplicationController.PlaceShowWindow(window, bounds);
                StartCapture(window, null);
                LogEvent("show_window_reattached", reason);
                return;
            }
        }
        catch (Exception) { }
        finally { Volatile.Write(ref _reattaching, 0); }
        if (Status != OfficeSessionStatus.Ready) return;
        ShowRunning = false;
        base.OnWindowLost(reason);
        SetStatusMessage("放映窗口已关闭");
    }

    private void SetStatusMessage(string message) => SetStatus(Status, message);

    private Task Control(Action<OfficeApplicationController> action) =>
        Dispatcher.InvokeAsync(() => { action(Controller); return true; }, CallTimeout)
            .ContinueWith(async t => { if (t.IsFaulted) throw t.Exception!.InnerException!; await RefreshPositionAsync().ConfigureAwait(false); }, TaskScheduler.Default).Unwrap();

    /// <summary>"下一步": the next animation of the current slide, then the next slide.</summary>
    public Task NextStepAsync() => Control(c => c.ShowNext());
    public Task PreviousStepAsync() => Control(c => c.ShowPrevious());
    public Task NextSlideAsync() => Control(c => { var p = c.ShowPosition(); if (p.Slide < SlideCount) c.ShowGoTo(p.Slide + 1); });
    public Task PreviousSlideAsync() => Control(c => { var p = c.ShowPosition(); if (p.Slide > 1) c.ShowGoTo(p.Slide - 1); });
    public Task GoToSlideAsync(int slide) => Control(c => c.ShowGoTo(Math.Clamp(slide, 1, SlideCount)));

    /// <summary>Black / white / pause toggle; the same screen again returns to the running show.</summary>
    public Task ToggleScreenAsync(SlideShowScreen screen) => Control(c =>
    {
        var current = (SlideShowScreen)c.ShowPosition().State;
        c.ShowSetState(current == screen ? (int)SlideShowScreen.Running : (int)screen);
    });

    public async Task EndShowAsync()
    {
        StopCapture(keepLastFrame: true);
        ShowRunning = false;
        await Dispatcher.InvokeAsync(() => { Controller.ExitShow(); return true; }, CallTimeout).ConfigureAwait(false);
        SetStatus(OfficeSessionStatus.Ended, "放映已结束");
        LogEvent("show_ended");
    }

    /// <summary>"编辑": ends the show (Program keeps the last frame) and opens the presentation in the
    /// editor on the operator's screen.</summary>
    public async Task EnterEditAsync()
    {
        _editSlide = Math.Max(1, Position.Slide);
        _editorOpened = true;
        StopCapture(keepLastFrame: true);
        ShowRunning = false;
        SetStatus(OfficeSessionStatus.Editing, "正在编辑演示文稿（大屏保持最后画面）");
        await Dispatcher.InvokeAsync(() =>
        {
            Controller.ExitShow();
            if (Controller.IsDocumentOpen) Controller.CloseDocument(saveChanges: false);
            Controller.OpenPresentation(SourcePath, withWindow: true);
            return true;
        }, CallTimeout).ConfigureAwait(false);
        LogEvent("edit_started", $"slide={_editSlide}");
    }

    private bool _editorOpened;
    private protected override bool MayHaveEdits => _editorOpened;

    /// <summary>Whether the editor has unsaved changes (ask before reloading).</summary>
    public Task<bool> EditorIsDirtyAsync() => IsDirtyAsync();

    /// <summary>"重新载入放映": closes the editor (saving when asked) and restarts the show near the slide
    /// it was on.</summary>
    public async Task ReloadAsync(bool saveEdits)
    {
        int slide = Status == OfficeSessionStatus.Editing ? _editSlide : Math.Max(1, Position.Slide);
        SetStatus(OfficeSessionStatus.Opening, "正在重新载入放映…");
        await Dispatcher.InvokeAsync(() =>
        {
            Controller.ExitShow();
            if (Controller.IsDocumentOpen) Controller.CloseDocument(saveEdits);
            Controller.OpenPresentation(SourcePath, withWindow: false);
            SlideCount = Controller.SlideCount;
            return true;
        }, CallTimeout).ConfigureAwait(false);
        lock (_slideImages) _slideImages.Clear();
        await StartShowAsync(Math.Clamp(slide, 1, Math.Max(1, SlideCount)), _bounds).ConfigureAwait(false);
        LogEvent("reloaded", $"slide={slide}");
    }

    /// <summary>A rendered image of <paramref name="slide"/> (1-based) at about <paramref name="width"/>
    /// pixels wide, cached per session. Used for the presenter's "下一张" and for browsing slides in Preview
    /// while Program is frozen.</summary>
    public async Task<Bitmap?> GetSlideImageAsync(int slide, int width)
    {
        if (slide < 1 || slide > SlideCount) return null;
        width = Math.Clamp(width, 160, 1920);
        string? path;
        lock (_slideImages) _slideImages.TryGetValue((slide, width), out path);
        if (path == null)
        {
            Directory.CreateDirectory(_imageDirectory);
            string target = Path.Combine(_imageDirectory, $"{slide}_{width}.png");
            try
            {
                await Dispatcher.InvokeAsync(() => { Controller.ExportSlide(slide, target, width, width * 9 / 16); return true; }, CallTimeout).ConfigureAwait(false);
            }
            catch (Exception) { return null; }
            if (!File.Exists(target)) return null;
            lock (_slideImages) _slideImages[(slide, width)] = path = target;
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception) { return null; }
    }

    public Task<string> NotesAsync(int slide) =>
        slide < 1 || slide > SlideCount ? Task.FromResult("") : Dispatcher.InvokeAsync(() => Controller.SlideNotes(slide), CallTimeout);

    internal void DeleteImages()
    {
        try { if (Directory.Exists(_imageDirectory)) Directory.Delete(_imageDirectory, true); } catch (Exception) { }
    }
}

/// <summary>
/// Shares WPS/Office sessions between the Preview and Program channels: the same document opened by both
/// is one editor (or one slide show) and one capture. A session closes when neither channel uses it any
/// more; unsaved edits are confirmed with the operator first (<see cref="ConfirmSaveOnClose"/>), so a
/// WPS/Word "save changes?" dialog never appears. Also decides where a slide show window lives.
/// </summary>
public sealed class OfficeSessionManager
{
    private readonly List<OfficeSession> _sessions = new();
    private readonly DocumentLogger _log;
    private Rectangle? _programBounds;

    public OfficeSessionManager(string? logRootOverride = null) => _log = new DocumentLogger(logRootOverride);

    /// <summary>The app-wide instance used by both channels.</summary>
    public static OfficeSessionManager Shared { get; set; } = new();

    public DocumentLogger Log => _log;

    /// <summary>Asked (on the UI thread) when a session with unsaved edits closes: true = save,
    /// false = discard. Unset (tests) discards.</summary>
    public Func<OfficeSession, bool>? ConfirmSaveOnClose { get; set; }

    public IReadOnlyList<OfficeSession> Sessions => _sessions;

    /// <summary>Screen area where slide shows run when no Program display holds them: the primary screen,
    /// behind every other window.</summary>
    public static Rectangle ParkingBounds => Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

    public WriterSession AcquireWriter(string path, OfficeSuite suite) => Acquire(path, DocumentFormat.Word, () => new WriterSession(path, suite, _log));

    public SlideShowSession AcquireSlideShow(string path, OfficeSuite suite, int startSlide, int? endSlide, bool loop, bool forProgram, Rectangle programBounds)
    {
        var session = Acquire(path, DocumentFormat.Presentation, () => new SlideShowSession(path, suite, _log)
        {
            InitialSlide = startSlide,
            EndSlide = endSlide,
            Loop = loop,
            InitialBounds = forProgram ? programBounds : ParkingBounds,
        });
        if (forProgram) BindProgram(session, programBounds);
        return session;
    }

    private T Acquire<T>(string path, DocumentFormat format, Func<T> create) where T : OfficeSession
    {
        var existing = _sessions.OfType<T>().FirstOrDefault(s => s.Format == format
            && string.Equals(s.SourcePath, path, StringComparison.OrdinalIgnoreCase)
            && s.Status is not (OfficeSessionStatus.Closed or OfficeSessionStatus.Failed));
        if (existing != null)
        {
            existing.References++;
            return existing;
        }
        var session = create();
        session.References = 1;
        _sessions.Add(session);
        return session;
    }

    /// <summary>Program now shows this slide show: move it onto the Program display (behind the output).</summary>
    public void BindProgram(SlideShowSession session, Rectangle programBounds)
    {
        _programBounds = programBounds;
        session.PlaceShow(programBounds);
    }

    /// <summary>Program no longer shows it: park it behind the operator's windows.</summary>
    public void UnbindProgram(SlideShowSession session) => session.PlaceShow(ParkingBounds);

    /// <summary>Drops one channel's use of <paramref name="session"/>; the last one closes it.</summary>
    public Task ReleaseAsync(OfficeSession session)
    {
        if (--session.References > 0) return Task.CompletedTask;
        _sessions.Remove(session);
        if (session.Status == OfficeSessionStatus.Closed) return Task.CompletedTask;
        var closing = CloseAsync(session);
        lock (_closing) _closing.Add(closing);
        _ = closing.ContinueWith(t => { lock (_closing) _closing.Remove(t); }, TaskScheduler.Default);
        return closing;
    }

    private readonly List<Task> _closing = new();

    private async Task CloseAsync(OfficeSession session)
    {
        bool save = false;
        if (session.Status is not (OfficeSessionStatus.Failed or OfficeSessionStatus.Closed) && ConfirmSaveOnClose != null)
        {
            bool dirty = false;
            try { dirty = await session.IsDirtyAsync().ConfigureAwait(true); } catch (Exception) { }
            if (dirty)
            {
                try { save = ConfirmSaveOnClose(session); } catch (Exception) { save = false; }
            }
        }
        await session.CloseAsync(save).ConfigureAwait(false);
        if (session is SlideShowSession show) show.DeleteImages();
    }

    /// <summary>Closes every session (including ones already closing) — application exit. Unsaved edits
    /// are discarded only when no confirm callback is set.</summary>
    public Task CloseAllAsync()
    {
        foreach (var session in _sessions.ToList())
        {
            session.References = 1;
            _ = ReleaseAsync(session);
        }
        Task[] pending;
        lock (_closing) pending = _closing.ToArray();
        return Task.WhenAll(pending);
    }

    /// <summary>Waits on the UI thread (pumping messages, so save prompts still work) until every
    /// session has closed or <paramref name="timeout"/> passes.</summary>
    public void CloseAllAndWait(TimeSpan timeout)
    {
        var all = CloseAllAsync();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!all.IsCompleted && deadline.Elapsed < timeout)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }
}

/// <summary>Frame checks for captured windows.</summary>
public static class CaptureFrames
{
    /// <summary>True when every sampled pixel is (nearly) black — a window whose content the capture API
    /// cannot see (some GPU-rendered slide shows) rather than a black slide, when it stays so for seconds.</summary>
    public static bool IsBlank(System.Drawing.Bitmap bitmap)
    {
        int stepX = Math.Max(1, bitmap.Width / 24), stepY = Math.Max(1, bitmap.Height / 24);
        for (int y = stepY / 2; y < bitmap.Height; y += stepY)
            for (int x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.R + c.G + c.B > 24) return false;
            }
        return true;
    }
}
