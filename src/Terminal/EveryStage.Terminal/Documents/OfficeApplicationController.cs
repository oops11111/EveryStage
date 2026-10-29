using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.Documents;

public enum OfficeSuite { Wps, MicrosoftOffice }

public enum OfficeAppKind { Writer, Presentation }

/// <summary>The automation step that failed, reported to the operator and the document log.</summary>
public enum OfficeStage { StartApplication, OpenFile, GetWindow, StartSlideShow, Control, Save, Close, Convert }

public sealed class OfficeOperationException : Exception
{
    public OfficeOperationException(OfficeStage stage, string suiteName, string? version, string message, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        SuiteName = suiteName;
        Version = version;
    }

    public OfficeStage Stage { get; }
    public string SuiteName { get; }
    public string? Version { get; }

    public static string StageName(OfficeStage stage) => stage switch
    {
        OfficeStage.StartApplication => "启动应用",
        OfficeStage.OpenFile => "打开文件",
        OfficeStage.GetWindow => "获取窗口",
        OfficeStage.StartSlideShow => "启动放映",
        OfficeStage.Control => "放映控制",
        OfficeStage.Save => "保存",
        OfficeStage.Close => "关闭",
        _ => "转换",
    };
}

/// <summary>Which automation servers are registered on this machine.</summary>
public static class OfficeInstallation
{
    public const string Word = "Word.Application", PowerPoint = "PowerPoint.Application";

    // Current WPS registers KWPS/KWPP; older builds (V9 era) register wps/wpp instead. Either reaches
    // the same automation server.
    private static readonly string[] WpsWriterProgIds = { "KWPS.Application", "wps.Application" };
    private static readonly string[] WpsPresentationProgIds = { "KWPP.Application", "wpp.Application" };

    /// <summary>The registered WPS Writer ProgID (the current one when none is registered).</summary>
    public static string WpsWriter => WpsWriterProgIds.FirstOrDefault(IsRegistered) ?? WpsWriterProgIds[0];

    /// <summary>The registered WPS Presentation ProgID (the current one when none is registered).</summary>
    public static string WpsPresentation => WpsPresentationProgIds.FirstOrDefault(IsRegistered) ?? WpsPresentationProgIds[0];

    /// <summary>What to tell the operator when WPS is installed but cannot be automated. The WPS
    /// personal edition — which is what the Microsoft Store installs — does not register its COM
    /// automation interface until this compatibility option is turned on.</summary>
    public const string WpsAutomationDisabledHint =
        "检测到已安装 WPS，但它的 COM 接口未开启（WPS 个人版/微软商店版默认关闭），EveryStage 无法调用它。" +
        "请打开“WPS 配置工具”（开始菜单 → WPS Office → 配置工具，或 WPS 安装目录 office6\\ksomisc.exe），" +
        "在“高级 → 兼容设置”中勾选“WPS Office 兼容第三方系统和软件”，确定后关闭所有 WPS 窗口再重试。" +
        "若已勾选仍无效：在同一工具“高级 → 重置修复 → 重新注册组件”。";

    /// <summary>WPS program files exist on this machine (per-user installs — the personal / Microsoft
    /// Store edition — go to %LocalAppData%\Kingsoft\WPS Office; machine installs to Program Files).</summary>
    public static bool WpsFilesPresent()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            string wpsRoot = Path.Combine(root, "Kingsoft", "WPS Office");
            try
            {
                if (!Directory.Exists(wpsRoot)) continue;
                foreach (var version in Directory.EnumerateDirectories(wpsRoot))
                {
                    string office6 = Path.Combine(version, "office6");
                    if (File.Exists(Path.Combine(office6, "wps.exe")) || File.Exists(Path.Combine(office6, "wpsoffice.exe")))
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>WPS is installed but neither of its automation servers is registered (see
    /// <see cref="WpsAutomationDisabledHint"/>).</summary>
    public static bool WpsInstalledWithoutAutomation =>
        !SimulateNotInstalled
        && !WpsWriterProgIds.Any(IsRegistered) && !WpsPresentationProgIds.Any(IsRegistered)
        && WpsFilesPresent();

    // Test hook: pretend nothing is installed (degradation paths).
    public static bool SimulateNotInstalled { get; set; }

    public static bool IsRegistered(string progId) => !SimulateNotInstalled && Type.GetTypeFromProgID(progId, throwOnError: false) != null;

    public static bool HasWriter => IsRegistered(WpsWriter) || IsRegistered(Word);
    public static bool HasPresentation => IsRegistered(PowerPoint) || IsRegistered(WpsPresentation);

    /// <summary>Editor for a Word document: the requested suite when installed, otherwise the other one.
    /// WPS first by default (the product's primary office suite).</summary>
    public static OfficeSuite? PickWriter(OfficeOpenMode mode)
    {
        bool wps = IsRegistered(WpsWriter), word = IsRegistered(Word);
        return mode switch
        {
            OfficeOpenMode.MicrosoftOffice when word => OfficeSuite.MicrosoftOffice,
            OfficeOpenMode.Wps when wps => OfficeSuite.Wps,
            _ when wps => OfficeSuite.Wps,
            _ when word => OfficeSuite.MicrosoftOffice,
            _ => null,
        };
    }

    /// <summary>Slide-show engine: PowerPoint first — its full-screen show keeps rendering (and can be
    /// captured) while moved behind the Program output; WPS Presentation second.</summary>
    public static OfficeSuite? PickPresentation() =>
        IsRegistered(PowerPoint) ? OfficeSuite.MicrosoftOffice : IsRegistered(WpsPresentation) ? OfficeSuite.Wps : null;
}

/// <summary>
/// Late-bound COM automation of one WPS or Microsoft Office application instance for one document:
/// start, open, save, close, and (for presentations) run and drive a slide show. Every method must run on
/// the owning <see cref="StaDispatcher"/> thread. Failures throw <see cref="OfficeOperationException"/>
/// carrying the stage.
///
/// Process hygiene: the controller remembers which processes of that application already existed before
/// it started. <see cref="Quit"/> only quits the application when no other documents remain open in it,
/// and afterwards terminates any process it started that is still lingering (PowerPoint and Word often
/// keep running for seconds after Quit) — never a process that was already running.
/// </summary>
internal sealed class OfficeApplicationController
{
    private readonly OfficeSuite _suite;
    private readonly OfficeAppKind _kind;
    private HashSet<int> _preexistingPids = new();
    // Processes that appeared while this controller created its application: the ones it may terminate.
    // Null until creation returns (a hung creation falls back to "anything newer than _preexistingPids").
    // Diffing against _preexistingPids alone at cleanup time also matched processes started later by
    // another EveryStage session or by the operator (their own Word window), and killed them.
    private volatile HashSet<int>? _startedPids;
    private dynamic? _app;
    private dynamic? _document;
    private dynamic? _showWindow;
    private bool _openedWithWindow;
    private string? _path;
    private bool? _savedSpellingOption, _savedGrammarOption;

    public OfficeApplicationController(OfficeSuite suite, OfficeAppKind kind)
    {
        _suite = suite;
        _kind = kind;
    }

    public OfficeSuite Suite => _suite;
    public string SuiteName => _suite == OfficeSuite.Wps ? "WPS" : "Microsoft Office";
    public string? Version { get; private set; }
    public bool IsDocumentOpen => _document != null;

    /// <summary>False once the application process is gone (crashed or killed): its COM server no longer
    /// answers. Any other error (busy, dialog open) counts as alive.</summary>
    public bool ApplicationAlive
    {
        get
        {
            if (_app == null) return false;
            try { _ = _app.Name; return true; }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult is unchecked((int)0x800706BA) or unchecked((int)0x80010108) or unchecked((int)0x800401FD))
            { return false; }
            catch (Exception) { return true; }
        }
    }

    private string ProgId => (_suite, _kind) switch
    {
        (OfficeSuite.Wps, OfficeAppKind.Writer) => OfficeInstallation.WpsWriter,
        (OfficeSuite.Wps, _) => OfficeInstallation.WpsPresentation,
        (_, OfficeAppKind.Writer) => OfficeInstallation.Word,
        _ => OfficeInstallation.PowerPoint,
    };

    // WPS in multi-component mode runs wps.exe / wpp.exe; in integrated mode (the personal edition's
    // default, one tabbed window) documents run in wpsoffice.exe. Looking for only one name meant the
    // slide-show window, dialogs and lingering processes were never found in the other mode. Background
    // services (wpscloudsvr, wpscenter…) are deliberately not listed: they are never ours to touch.
    private string[] ProcessNames => (_suite, _kind) switch
    {
        (OfficeSuite.Wps, OfficeAppKind.Writer) => new[] { "wps", "wpsoffice" },
        (OfficeSuite.Wps, _) => new[] { "wpp", "wpsoffice" },
        (_, OfficeAppKind.Writer) => new[] { "WINWORD" },
        _ => new[] { "POWERPNT" },
    };

    private Process[] SuiteProcesses() => ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();

    private OfficeOperationException Fail(OfficeStage stage, string message, Exception? inner = null) =>
        new(stage, SuiteName, Version, message, inner);

    public void Start()
    {
        _preexistingPids = SuiteProcesses().Select(p => p.Id).ToHashSet();
        try
        {
            var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
                ?? throw new InvalidOperationException($"{ProgId} 未注册。");
            _app = Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{ProgId} 无法创建。");
        }
        catch (Exception ex)
        {
            throw Fail(OfficeStage.StartApplication, $"无法启动 {SuiteName}：{ex.Message}", ex);
        }
        _startedPids = SuiteProcesses().Select(p => p.Id).Where(id => !_preexistingPids.Contains(id)).ToHashSet();
        try { Version = $"{_app!.Name} {_app.Version} ({TryGet(() => (object)_app.Build)})"; }
        catch (Exception) { Version = null; }
    }

    private static string? TryGet(Func<object?> get)
    {
        try { return get()?.ToString(); } catch (Exception) { return null; }
    }

    /// <summary>A .docx/.pptx that is an OLE compound file instead of a zip is encrypted (password to
    /// open). Opening it through automation would pop up a password dialog, so refuse it up front.</summary>
    public static bool IsPasswordProtected(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".docx" or ".pptx" or ".ppsx" or ".xlsx")) return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.Read(head) == 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
        }
        catch (IOException) { return false; }
    }

    /// <summary>True when another process holds the file open for writing (so it can only be read).</summary>
    public static bool IsLockedByAnotherProcess(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (UnauthorizedAccessException) { return false; } // read-only attribute / ACL — reported separately
        catch (IOException) { return true; }
    }

    // ── Writer ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a Word document in a visible editor window on the operator's screen.</summary>
    public void OpenWriter(string path, bool readOnly, bool visible = true)
    {
        _path = path;
        try
        {
            _app!.Visible = visible;
            try { _app.DisplayAlerts = 0; } catch (Exception) { }
            // Encrypted .docx files are refused before this call (IsPasswordProtected). A dummy password
            // argument (to make encrypted .doc files fail fast) made Word wait on a dialog even for
            // unencrypted documents, so an encrypted .doc is left to the open timeout instead.
            _document = _app.Documents.Open(path, false, readOnly, false);
        }
        catch (Exception ex)
        {
            string message = ex.Message.Contains("密码") || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "文档受密码保护，无法在 EveryStage 中打开。"
                : $"{SuiteName} 无法打开文档：{ex.Message}";
            throw Fail(OfficeStage.OpenFile, message, ex);
        }
        if (visible) PrepareWriterView();
    }

    /// <summary>Output-friendly view: no formatting marks and no spelling/grammar squiggles. The
    /// squiggle options are application settings, restored when the session closes.</summary>
    private void PrepareWriterView()
    {
        try { _app!.ActiveWindow.View.ShowAll = false; } catch (Exception) { }
        try
        {
            _savedSpellingOption = (bool)_app!.Options.CheckSpellingAsYouType;
            _savedGrammarOption = (bool)_app.Options.CheckGrammarAsYouType;
            _app.Options.CheckSpellingAsYouType = false;
            _app.Options.CheckGrammarAsYouType = false;
        }
        catch (Exception) { _savedSpellingOption = _savedGrammarOption = null; }
    }

    /// <summary>The editor's top-level window.</summary>
    public IntPtr WriterWindow()
    {
        try
        {
            var hwnd = new IntPtr(Convert.ToInt64(_app!.ActiveWindow.Hwnd));
            // Word returns its top-level window, WPS its document pane: normalise to the top-level window.
            var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root != IntPtr.Zero ? root : hwnd;
        }
        catch (Exception ex)
        {
            throw Fail(OfficeStage.GetWindow, $"无法获取 {SuiteName} 编辑窗口：{ex.Message}", ex);
        }
    }

    public bool WriterIsReadOnly => TryBool(() => _document!.ReadOnly);

    /// <summary>The open document (or presentation) has unsaved edits.</summary>
    public bool IsDirty => _document != null && !TryBool(() => _document!.Saved, fallback: true);

    private static bool TryBool(Func<object> get, bool fallback = false)
    {
        try { return Convert.ToBoolean(get()); } catch (Exception) { return fallback; }
    }

    public void SetWriterScrollPercent(double percent)
    {
        try { _app!.ActiveWindow.VerticalPercentScrolled = (int)Math.Clamp(percent, 0, 100); } catch (Exception) { }
    }

    /// <summary>Self-test hook: types text at the start of the document (as an operator edit would).</summary>
    internal void InsertTextAtStart(string text) => _document!.Content.InsertBefore(text);

    /// <summary>Editor zoom: the whole page (true) or the page width (false). wdPageFitFullPage = 1, wdPageFitBestFit = 2.</summary>
    public void SetWriterPageFit(bool wholePage)
    {
        try { _app!.ActiveWindow.View.Zoom.PageFit = wholePage ? 1 : 2; }
        catch (Exception ex) { throw Fail(OfficeStage.Control, $"无法调整显示比例：{ex.Message}", ex); }
    }

    public void Save()
    {
        if (_document == null) throw Fail(OfficeStage.Save, "没有打开的文档。");
        if (_kind == OfficeAppKind.Writer && WriterIsReadOnly) throw Fail(OfficeStage.Save, "文档以只读方式打开（文件只读或被其他程序占用），不能直接保存。请使用“另存为”。");
        try { _document.Save(); }
        catch (Exception ex) { throw Fail(OfficeStage.Save, $"保存失败：{ex.Message}", ex); }
    }

    public void SaveAs(string path)
    {
        if (_document == null) throw Fail(OfficeStage.Save, "没有打开的文档。");
        try
        {
            try { _document.SaveAs2(path); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { _document.SaveAs(path); }
            _path = path;
        }
        catch (Exception ex) { throw Fail(OfficeStage.Save, $"另存为失败：{ex.Message}", ex); }
    }

    /// <summary>Visible dialog windows of the application (save prompts, compatibility or recovery
    /// notices) — the operator has to answer them in the editor; they never reach Program.</summary>
    public bool HasPendingDialog()
    {
        foreach (var process in SuiteProcesses())
            foreach (var hwnd in NativeMethods.TopLevelWindowsOf(process.Id))
            {
                string cls = NativeMethods.ClassName(hwnd);
                if (cls == "#32770" || cls.StartsWith("bosa_sdm", StringComparison.Ordinal) || cls == "NUIDialog") return true;
            }
        return false;
    }

    // ── Presentation ─────────────────────────────────────────────────────────────────────────────

    public void OpenPresentation(string path, bool withWindow)
    {
        _path = path;
        try
        {
            try { _app!.DisplayAlerts = 1; } catch (Exception) { } // ppAlertsNone
            // Arguments: FileName, ReadOnly, Untitled, WithWindow (msoTrue = -1, msoFalse = 0).
            _document = _app!.Presentations.Open(path, 0, 0, withWindow ? -1 : 0);
            _openedWithWindow = withWindow;
        }
        catch (Exception ex)
        {
            throw Fail(OfficeStage.OpenFile, $"{SuiteName} 无法打开演示文稿：{ex.Message}", ex);
        }
    }

    public int SlideCount => _document == null ? 0 : TryInt(() => _document.Slides.Count);

    private static int TryInt(Func<object> get, int fallback = 0)
    {
        try { return Convert.ToInt32(get()); } catch (Exception) { return fallback; }
    }

    public string SlideNotes(int slide)
    {
        try
        {
            dynamic shapes = _document!.Slides(slide).NotesPage.Shapes;
            return ((string)shapes.Placeholders(2).TextFrame.TextRange.Text ?? "").Trim();
        }
        catch (Exception) { return ""; }
    }

    public void ExportSlide(int slide, string path, int width, int height) =>
        _document!.Slides(slide).Export(path, "PNG", width, height);

    /// <summary>Starts a full-screen slide show at <paramref name="startSlide"/> and moves its window to
    /// <paramref name="bounds"/>, below other windows (it is captured, never shown directly). Returns the
    /// show window.</summary>
    public IntPtr StartSlideShow(int startSlide, int? endSlide, bool loop, Rectangle bounds)
    {
        var before = ProcessWindows();
        var operatorWindow = NativeMethods.GetForegroundWindow();
        try
        {
            dynamic settings = _document!.SlideShowSettings;
            settings.ShowType = 1; // ppShowTypeSpeaker: full screen (the windowed "browse" show does not render reliably)
            try { settings.ShowPresenterView = 0; } catch (Exception) { }
            int count = Math.Max(1, SlideCount);
            int first = Math.Clamp(startSlide, 1, count);
            try { settings.RangeType = 2; settings.StartingSlide = first; settings.EndingSlide = Math.Clamp(endSlide ?? count, first, count); } catch (Exception) { } // ppShowSlideRange
            try { settings.LoopUntilStopped = loop ? -1 : 0; } catch (Exception) { }
            _showWindow = settings.Run();
        }
        catch (Exception ex)
        {
            throw Fail(OfficeStage.StartSlideShow, $"无法启动放映：{ex.Message}", ex);
        }
        IntPtr show = IntPtr.Zero;
        var deadline = Stopwatch.StartNew();
        while (show == IntPtr.Zero && deadline.ElapsedMilliseconds < 8000)
        {
            show = ReportedShowWindow();
            if (show == IntPtr.Zero)
            {
                // No handle reported (WPS): the largest new window — once it has outlived a splash or
                // temporary frame the application may create while the show starts.
                var candidate = LargestVisible(ProcessWindows().Except(before));
                if (candidate != IntPtr.Zero)
                {
                    Thread.Sleep(300);
                    if (NativeMethods.IsWindow(candidate) && NativeMethods.IsWindowVisible(candidate)) show = candidate;
                }
            }
            if (show == IntPtr.Zero) Thread.Sleep(100);
        }
        if (show == IntPtr.Zero) throw Fail(OfficeStage.GetWindow, "放映已启动，但找不到放映窗口。");
        PlaceShowWindow(show, bounds);
        // The show grabbed the foreground: hand it back to the operator (EveryStage).
        if (operatorWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(operatorWindow);
        return show;
    }

    /// <summary>The show window the application reports (PowerPoint's SlideShowWindow.HWND).</summary>
    private IntPtr ReportedShowWindow()
    {
        try
        {
            var hwnd = new IntPtr(Convert.ToInt64(_showWindow!.HWND));
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd)) return hwnd;
        }
        catch (Exception) { }
        return IntPtr.Zero;
    }

    private static IntPtr LargestVisible(IEnumerable<IntPtr> windows) =>
        windows.Where(h => NativeMethods.IsWindowVisible(h))
            .OrderByDescending(h => { var r = NativeMethods.WindowRect(h); return (long)r.Width * r.Height; })
            .FirstOrDefault();

    /// <summary>Finds the running show's window again after the captured one went away (the application
    /// recreated it): the reported handle, otherwise the process window covering the show bounds.
    /// Zero when the show is not running.</summary>
    public IntPtr RefindShowWindow(Rectangle bounds)
    {
        if (!ShowRunning) return IntPtr.Zero;
        var show = ReportedShowWindow();
        return show != IntPtr.Zero ? show : LargestVisible(ProcessWindows().Where(h => NativeMethods.WindowRect(h) == bounds));
    }

    public static void PlaceShowWindow(IntPtr show, Rectangle bounds)
    {
        NativeMethods.SetWindowPos(show, NativeMethods.HWND_NOTOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SWP_NOACTIVATE);
        NativeMethods.SetWindowPos(show, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private HashSet<IntPtr> ProcessWindows()
    {
        var set = new HashSet<IntPtr>();
        foreach (var process in SuiteProcesses())
            foreach (var hwnd in NativeMethods.TopLevelWindowsOf(process.Id)) set.Add(hwnd);
        return set;
    }

    public bool ShowRunning
    {
        get
        {
            if (_showWindow == null) return false;
            try { _ = _showWindow.View.State; return true; } catch (Exception) { return false; }
        }
    }

    public (int Slide, int Click, int Clicks, int State) ShowPosition()
    {
        dynamic view = _showWindow!.View;
        int slide = TryInt(() => view.CurrentShowPosition);
        int click = TryInt(() => view.GetClickIndex());
        int clicks = TryInt(() => view.GetClickCount());
        int state = TryInt(() => view.State, 1);
        return (slide, click, clicks, state);
    }

    public void ShowNext() => ShowControl(v => v.Next());
    public void ShowPrevious() => ShowControl(v => v.Previous());
    public void ShowGoTo(int slide) => ShowControl(v => v.GotoSlide(slide));
    /// <summary>ppSlideShowRunning 1, Paused 2, BlackScreen 3, WhiteScreen 4.</summary>
    public void ShowSetState(int state) => ShowControl(v => v.State = state);

    private void ShowControl(Action<dynamic> action)
    {
        if (_showWindow == null) throw Fail(OfficeStage.Control, "放映没有运行。");
        try { action(_showWindow.View); }
        catch (Exception ex) { throw Fail(OfficeStage.Control, $"放映控制失败：{ex.Message}", ex); }
    }

    /// <summary>Ends the show. For a presentation opened without a window this also closes it.</summary>
    public void ExitShow()
    {
        if (_showWindow == null) return;
        try { _showWindow.View.Exit(); } catch (Exception) { }
        _showWindow = null;
        // A presentation opened without a window is closed together with its show.
        if (!_openedWithWindow && _document != null)
        {
            bool stillOpen;
            try { _ = _document!.Slides.Count; stillOpen = true; } catch (Exception) { stillOpen = false; }
            if (!stillOpen) { try { Marshal.FinalReleaseComObject(_document); } catch (Exception) { } _document = null; }
        }
    }

    /// <summary>Saves the presentation as PDF (static slides / conversion cache).</summary>
    public void SaveAsPdf(string pdfPath)
    {
        try
        {
            if (_kind == OfficeAppKind.Presentation) _document!.SaveAs(pdfPath, 32); // ppSaveAsPDF
            else
            {
                try { _document!.ExportAsFixedFormat(pdfPath, 17); } // wdExportFormatPDF
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { _document!.SaveAs2(pdfPath, 17); }
            }
        }
        catch (Exception ex) { throw Fail(OfficeStage.Convert, $"转换为 PDF 失败：{ex.Message}", ex); }
    }

    // ── Close / quit ─────────────────────────────────────────────────────────────────────────────

    public void CloseDocument(bool saveChanges)
    {
        if (_showWindow != null) ExitShow();
        if (_document == null) return;
        try
        {
            if (saveChanges) _document.Save();
            if (_kind == OfficeAppKind.Writer) _document.Close(0); // wdDoNotSaveChanges (after the explicit save)
            else { try { _document.Saved = -1; } catch (Exception) { } _document.Close(); }
        }
        catch (Exception ex) { throw Fail(OfficeStage.Close, $"关闭文档失败：{ex.Message}", ex); }
        finally
        {
            try { Marshal.FinalReleaseComObject(_document); } catch (Exception) { }
            _document = null;
        }
    }

    /// <summary>Quits the application if it holds no other documents, releases COM, and ends any process
    /// this controller started that is still running a few seconds later.</summary>
    public void Quit()
    {
        try { if (_document != null) CloseDocument(saveChanges: false); } catch (Exception) { }
        bool othersOpen = false;
        if (_app != null)
        {
            RestoreWriterOptions();
            othersOpen = _kind == OfficeAppKind.Writer
                ? TryInt(() => _app.Documents.Count) > 0
                : TryInt(() => _app.Presentations.Count) > 0;
            if (!othersOpen)
            {
                try { if (_kind == OfficeAppKind.Writer) _app.Quit(0); else _app.Quit(); } catch (Exception) { }
            }
            try { Marshal.FinalReleaseComObject(_app); } catch (Exception) { }
            _app = null;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (othersOpen) return;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000)
        {
            if (!LingeringProcesses().Any()) return;
            Thread.Sleep(200);
        }
        foreach (var process in LingeringProcesses())
        {
            try { process.Kill(); process.WaitForExit(2000); } catch (Exception) { }
        }
    }

    /// <summary>Ends the processes this controller started (a hung automation call); callable from any thread.</summary>
    public void KillStartedProcesses()
    {
        foreach (var process in LingeringProcesses())
        {
            try { process.Kill(); process.WaitForExit(2000); } catch (Exception) { }
        }
    }

    private IEnumerable<Process> LingeringProcesses()
    {
        var started = _startedPids;
        // Never wpsoffice: in integrated mode that one process also holds the operator's own documents
        // of other types (a presentation next to our Writer session), so it is quit, not killed.
        return SuiteProcesses().Where(p => !p.ProcessName.Equals("wpsoffice", StringComparison.OrdinalIgnoreCase))
            .Where(p => started != null ? started.Contains(p.Id) : !_preexistingPids.Contains(p.Id));
    }

    private void RestoreWriterOptions()
    {
        if (_kind != OfficeAppKind.Writer || _app == null) return;
        try
        {
            if (_savedSpellingOption is { } spelling) _app!.Options.CheckSpellingAsYouType = spelling;
            if (_savedGrammarOption is { } grammar) _app!.Options.CheckGrammarAsYouType = grammar;
        }
        catch (Exception) { }
    }
}
