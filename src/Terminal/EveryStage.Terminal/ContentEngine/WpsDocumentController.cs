using System.Runtime.InteropServices;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// PLANNING.md §14.1/§16第9项's WPS COM互操作 — the single highest-remaining risk in this repository
/// (second only to Phase 0 itself per §14.1's own risk ranking), and the one PLANNING.md explicitly
/// says needs a dedicated person validating against a real WPS install early (§16第9项 "WPS COM互操作
/// 的静默模式实现与异常弹窗预案需要实测验证"). This sandbox has no Windows, no WPS, and no dotnet at
/// all — nothing here has ever been compiled, let alone run against a real WPS process. Every COM
/// call below goes through <c>dynamic</c> for the ENTIRE call (method/property name included, not
/// just result extraction) — the same risk-mitigation pattern this repo's <c>AudioDecodeSource</c>
/// already established for its own never-installed-package Media Foundation calls, and the same late-
/// bound <c>Type.GetTypeFromProgID</c>/<c>Activator.CreateInstance</c> approach
/// <c>Poc/WpsComInteropSpike</c> already used to probe this exact same automation surface. A wrong
/// method/property name guess throws <c>RuntimeBinderException</c> at RUNTIME (caught, degrades to a
/// reported failure) instead of failing to compile — but unlike every other <c>dynamic</c> use in this
/// repo, a WRONG guess here can't even be observed failing in THIS sandbox; it can only be found by
/// someone running this against a real WPS install, exactly as PLANNING.md's own risk callout says.
///
/// Deliberately NOT the same shape as <c>Poc/WpsComInteropSpike</c>, despite reusing its ProgID/late-
/// binding technique: that spike's whole point was proving WPS CAN run fully silent
/// (<c>Visible = false</c>) for a read-only preview. This class exists for the opposite requirement —
/// PLANNING.md's "文档可编辑" (document must be editable) — and a rasterized bitmap (the way
/// <c>PdfContentRenderer</c> shows a PDF today) can never satisfy that: an edit has to land in a real,
/// interactive WPS window, not a frame this process copied out of one. So <c>Visible</c> stays true
/// here, and — confirmed with the user before writing this (the alternative, actually re-parenting
/// WPS's HWND into <c>OverlayWindow</c> via <c>SetParent</c>, was not chosen: cross-process top-level-
/// window re-parenting is its own unverified risk, arguably worse than the COM calls themselves) —
/// the WPS window is left as its own real top-level window, just resized to exactly cover the
/// extended monitor (<see cref="Open"/>'s <c>targetBounds</c> parameter) for the same visual effect as
/// maximizing it there. Getting that window's HWND at all is itself a best-effort <c>dynamic</c>
/// guess (<c>Application.Hwnd</c>, mirroring Word/Excel/PowerPoint's own automation model) — if WPS's
/// actual object model doesn't expose it under that name, this degrades to "WPS opens, just wherever
/// Windows puts it" rather than throwing, since a document opening in the wrong place is far less bad
/// than failing to open at all over a purely cosmetic positioning miss.
///
/// Also confirmed with the user: 翻页 (paging) is NOT wired through this class at all — no COM call
/// here turns a page/slide/sheet. Once the window is positioned, the operator pages and edits directly
/// in WPS's own real UI, the same as they would with WPS running standalone. The one piece of
/// PLANNING.md's "打开/翻页/编辑/保存" still worth automating from EveryStage's own UI is 保存
/// (<see cref="TrySave"/>) — see <c>PlaybackEngine.TrySaveCurrentOfficeDocument</c> for why.
///
/// <b>Biggest undisclosed-until-now risk of this whole class</b>: <see cref="Close"/> calls the
/// document's own <c>Close()</c> WITHOUT specifying <c>SaveChanges</c> either way — deliberately, so
/// that if the operator edited the document and didn't explicitly save, WPS's own native "保存更改?"
/// prompt appears on its own now-visible window and the operator answers it directly, instead of this
/// class silently picking "discard" (data loss) or "save" (an unreviewed overwrite) on their behalf.
/// But <c>PlaybackEngine</c> is expected to run on the UI thread (see its own class doc comment), and
/// a COM automation call that triggers a modal dialog BLOCKS until that dialog is dismissed (the exact
/// "may appear to hang" failure mode <c>Poc/WpsComInteropSpike</c> already flagged for its own,
/// supposedly-silent probe) — so switching away from an edited-but-unsaved Office document can freeze
/// the entire EveryStage UI until the operator responds to a dialog on a screen most Terminals aren't
/// physically supervised in front of. This is an accepted, disclosed consequence of "编辑" requiring a
/// real interactive WPS window at all, not a bug introduced by this class — see this project's README
/// for how prominently this needs to be called out to whoever validates this against real hardware.
/// </summary>
public sealed class WpsDocumentController : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // Same "WPS ships under a K-prefixed ProgID historically, but this has reportedly varied across
    // editions/versions" uncertainty Poc/WpsComInteropSpike already documented — duplicated here
    // rather than shared with that project: this is a disposable POC vs. a real (if unverified)
    // product class, and this repo's own established convention is to keep each independently-
    // verifiable copy rather than introduce a shared dependency between the two for a handful of
    // string literals (see AudioDecodeSource/VideoDecodeSource's own duplicated TryGetDuration for
    // the same reasoning applied elsewhere).
    private static readonly Dictionary<string, string[]> ProgIdCandidates = new()
    {
        ["writer"] = new[] { "KWPS.Application", "WPS.Application" },
        ["spreadsheet"] = new[] { "KET.Application", "ET.Application" },
        ["presentation"] = new[] { "KWPP.Application", "WPP.Application" },
    };

    private object? _wpsApp;
    private object? _document;
    private string? _appKind;

    public bool IsOpen => _document != null;

    /// <summary>Extension-based routing between this class and <c>PdfContentRenderer</c> — PLANNING.md
    /// treats PPT/Word/Excel and PDF as the same <c>MediaKind.Document</c> "kind" at the data-model
    /// level (both are "文档"), but they need entirely different rendering strategies (this class's
    /// real, visible, editable WPS window vs. PdfContentRenderer's rasterized bitmap), so
    /// <c>PlaybackEngine</c> re-derives which one applies from the file extension every time rather
    /// than adding a second enum value — see <c>MediaKind</c>'s own doc comment for why it stays a
    /// coarse PLANNING.md-defined "文件类型", not a renderer-selection key.</summary>
    public static bool IsOfficeDocument(string path) => AppKindFor(path) != null;

    private static string? AppKindFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" => "writer",
        ".xls" or ".xlsx" => "spreadsheet",
        ".ppt" or ".pptx" => "presentation",
        _ => null,
    };

    /// <summary>Opens <paramref name="filePath"/> in a fresh WPS Application instance (never reused
    /// across calls — each <see cref="Open"/> call is expected to follow a prior <see cref="Close"/>,
    /// matching <c>Poc/WpsComInteropSpike</c>'s own "one Application per probe run" shape rather than
    /// keeping one long-lived WPS process around across unrelated documents/appKinds) and resizes its
    /// main window to exactly cover <paramref name="targetBounds"/> (the extended monitor's bounds —
    /// same visual effect as maximizing it there; see class doc comment for why this is a resize, not
    /// a literal Win32 maximize). Throws on any failure (ProgID not registered, COM error, or the
    /// document.Open call itself failing) — callers are expected to wrap this the same way
    /// <c>PlaybackEngine.PlayDocumentAsync</c> already wraps <c>PdfContentRenderer.LoadAsync</c>.
    /// Positioning failure (the <c>Application.Hwnd</c> guess not panning out) is NOT one of those
    /// throwing failures — see this method's own inline comment on that specific step.</summary>
    public void Open(string filePath, System.Drawing.Rectangle targetBounds)
    {
        string? appKind = AppKindFor(filePath);
        if (appKind == null)
            throw new NotSupportedException($"'{Path.GetExtension(filePath)}' is not a supported Office document extension.");

        _appKind = appKind;
        (_wpsApp, _) = CreateWpsApplication(appKind);

        dynamic app = _wpsApp;
        // Deliberately Visible = true (opposite of Poc/WpsComInteropSpike's silent probe) — see class
        // doc comment for why "文档可编辑" requires a real, visible window. DisplayAlerts is still
        // suppressed, best-effort: this is a quality-of-life setting for an unattended kiosk terminal
        // (avoid an unrelated "apply this template globally?"-style prompt interrupting an operator
        // mid-edit for a reason that has nothing to do with THIS document), not a silence requirement
        // — unlike the Poc spike, failure to suppress alerts here does not indicate this class itself
        // is broken, so it's wrapped and ignored rather than treated as a real error.
        try { app.Visible = true; } catch { /* best-effort — see comment above. */ }
        try { app.DisplayAlerts = 0; } catch { /* best-effort — see comment above. */ }

        _document = OpenDocument(app, appKind, filePath);

        // Positioning is cosmetic, not correctness-critical — a document that opens in the wrong
        // place on screen is a far smaller problem than one that fails to open at all, so any failure
        // here (Application.Hwnd not existing under that name on this WPS build, SetWindowPos itself
        // failing) is swallowed rather than propagated. This is the single least-verified step in this
        // entire class: Application.Hwnd is a guess based on how Word/Excel/PowerPoint's own
        // automation model exposes their main window handle, carried over to WPS on the assumption
        // its object model mirrors that (same assumption this repo's whole WPS integration already
        // rests on) — nobody has ever confirmed WPS actually exposes it under this exact name.
        try
        {
            dynamic hwndValue = app.Hwnd;
            var hwnd = new IntPtr(checked((long)hwndValue));
            SetWindowPos(hwnd, HWND_TOP, targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height, SWP_SHOWWINDOW);
        }
        catch
        {
            // Swallowed — see this method's own doc comment on positioning being best-effort.
        }
    }

    private static (object app, string progId) CreateWpsApplication(string appKind)
    {
        foreach (var progId in ProgIdCandidates[appKind])
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type == null) continue;

            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Activator.CreateInstance returned null for '{progId}'.");
            return (instance, progId);
        }

        throw new InvalidOperationException(
            $"None of the candidate ProgIDs for '{appKind}' are registered: [{string.Join(", ", ProgIdCandidates[appKind])}]. " +
            "WPS may not be installed, or ships a different ProgID on this machine/version than the ones this class knows about.");
    }

    private static object OpenDocument(dynamic app, string appKind, string filePath) => appKind switch
    {
        // Unlike Poc/WpsComInteropSpike's own OpenDocument (ReadOnly: true / WithWindow: false — a
        // read-only, windowless probe), these deliberately open writable and with a real window —
        // the entire point of this class per its own doc comment.
        "writer" => app.Documents.Open(filePath, ReadOnly: false),
        "spreadsheet" => app.Workbooks.Open(filePath),
        "presentation" => app.Presentations.Open(filePath, WithWindow: true),
        _ => throw new ArgumentOutOfRangeException(nameof(appKind)),
    };

    /// <summary>PLANNING.md §14.1's "编辑/保存" — the one piece of "打开/翻页/编辑/保存" still worth
    /// automating from EveryStage's own UI even though 翻页/编辑 themselves are left entirely to WPS's
    /// own real window (see class doc comment): an operator can always use WPS's own Ctrl+S directly
    /// too, this is a convenience wired to a "保存" button, not the only way to save. Returns false
    /// (never throws) on any failure — same best-effort convention as
    /// <c>AudioDecodeSource.TrySeek</c>/<c>TryGetDuration</c>, and for the same reason: the exact
    /// property/method shape (<c>ActiveDocument.Save()</c> guessed here) has never been verified
    /// against a real WPS install.</summary>
    public bool TrySave()
    {
        if (_document == null) return false;
        try
        {
            dynamic doc = _document;
            doc.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes the document and quits this WPS Application instance — see class doc comment
    /// for why <c>Close()</c> is called without a <c>SaveChanges</c> argument (letting WPS's own now-
    /// visible window prompt the operator directly for an edited-but-unsaved document, at the cost of
    /// this call being able to block on that prompt). Safe to call more than once / when nothing is
    /// open (no-ops). Mirrors <c>Poc/WpsComInteropSpike</c>'s own <c>CloseAndQuit</c> cleanup shape —
    /// explicit <c>Marshal.ReleaseComObject</c> + forced GC rather than waiting for a GC that might
    /// not run before this process needs the WPS process gone, to avoid the same "orphaned wps.exe
    /// survives after this process ends" failure mode that spike exists partly to catch.</summary>
    public void Close()
    {
        try
        {
            if (_document != null)
            {
                dynamic doc = _document;
                switch (_appKind)
                {
                    case "writer": doc.Close(); break;
                    case "spreadsheet": doc.Close(); break;
                    case "presentation": doc.Close(); break;
                }
                Marshal.ReleaseComObject(_document);
            }
        }
        catch { /* see class doc comment — a stuck modal dialog is the disclosed risk here, not a bug this catch is meant to paper over. */ }
        finally { _document = null; }

        try
        {
            if (_wpsApp != null)
            {
                dynamic app = _wpsApp;
                app.Quit();
                Marshal.ReleaseComObject(_wpsApp);
            }
        }
        catch { /* best-effort cleanup — see Close()'s own doc comment. */ }
        finally { _wpsApp = null; }

        _appKind = null;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public void Dispose() => Close();
}
