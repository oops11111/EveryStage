using System.Runtime.InteropServices;

namespace EveryStage.Poc.WpsComInteropSpike;

/// <summary>
/// PLANNING.md §14.1 flags WPS COM automation as the second-biggest technical risk after Phase 0,
/// specifically calling out: can it run silently (no visible UI, no update/auth popups), and does
/// paging/navigation work through the object model. This spike answers exactly that — nothing
/// more. It is NOT the Content Engine's document renderer; it's a disposable probe meant to be run
/// once per WPS install/version to record what actually happens, per §16 item 9
/// ("WPS COM互操作的静默模式实现与异常弹窗预案需要实测验证").
///
/// Uses late-bound `dynamic` COM (Type.GetTypeFromProgID + Activator.CreateInstance) instead of a
/// strongly-typed interop assembly: this project has never had WPS installed to generate a type
/// library reference against, and WPS's automation model is IDispatch-based (same as Word/Excel/
/// PowerPoint), so late binding is both the pragmatic choice here and the closest thing to how
/// VBA itself would call it.
/// </summary>
internal static class Program
{
    // WPS ships its automation ProgIDs under a "K"-prefixed namespace historically (KWPS/KET/KWPP
    // for Writer/Spreadsheet/Presentation), but this has reportedly varied across WPS editions and
    // versions — this spike's whole job is to find out which one is actually registered on the
    // target machine, so it tries several candidates rather than assuming one.
    private static readonly Dictionary<string, string[]> ProgIdCandidates = new()
    {
        ["writer"] = new[] { "KWPS.Application", "WPS.Application" },
        ["spreadsheet"] = new[] { "KET.Application", "ET.Application" },
        ["presentation"] = new[] { "KWPP.Application", "WPP.Application" },
    };

    private static int Main(string[] args)
    {
        string? app = GetArg(args, "--app");
        string? file = GetArg(args, "--file");
        string? selfTestDir = GetArg(args, "--self-test-dir");

        if (app is null || !ProgIdCandidates.ContainsKey(app) || (file is null && selfTestDir is null))
        {
            Console.WriteLine("Usage: WpsComInteropSpike --app writer|spreadsheet|presentation (--file <path> | --self-test-dir <directory>)");
            return 2;
        }

        return selfTestDir != null
            ? RunCreateEditSaveProbe(app, Path.GetFullPath(selfTestDir))
            : RunProbe(app, Path.GetFullPath(file!));
    }

    private static int RunProbe(string appKind, string filePath)
    {
        object? wpsApp = null;
        object? document = null;

        try
        {
            (wpsApp, string usedProgId) = CreateWpsApplication(appKind);
            Console.WriteLine($"[spike] instantiated via ProgID '{usedProgId}'.");

            dynamic app = wpsApp;

            // First and most important check: can we get it fully silent?
            TrySet(() => app.Visible = false, "Visible = false");
            TrySet(() => app.DisplayAlerts = 0, "DisplayAlerts = 0 (wdAlertsNone-equivalent)");
            TrySet(() => app.AutomationSecurity = 3, "AutomationSecurity = msoAutomationSecurityForceDisable");
            TrySet(() => app.ScreenUpdating = false, "ScreenUpdating = false");

            document = OpenDocument(app, appKind, filePath);
            Console.WriteLine("[spike] document opened.");

            ReportContentAndTryPaging(document, appKind);

            Console.WriteLine("[spike] RESULT: silent open + basic object-model access succeeded.");
            return 0;
        }
        catch (COMException comEx)
        {
            // The interesting failure mode per §16 item 9: an unexpected popup (update prompt,
            // license/activation dialog) often surfaces as the automation call blocking or
            // returning a COM error rather than throwing something more specific.
            Console.WriteLine($"[spike] RESULT: COM error 0x{comEx.HResult:X8}: {comEx.Message}");
            Console.WriteLine("[spike] If WPS actually opened but is sitting on a modal dialog, this");
            Console.WriteLine("        process may have appeared to hang before this error surfaced —");
            Console.WriteLine("        check Task Manager for a lingering wps*.exe and note what the");
            Console.WriteLine("        dialog said, then close it manually before re-running.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] RESULT: failed — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            CloseAndQuit(wpsApp, document, appKind);
        }
    }

    private static int RunCreateEditSaveProbe(string appKind, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string marker = $"EveryStage-WPS-self-test-{Guid.NewGuid():N}";
        string extension = appKind switch { "writer" => ".docx", "spreadsheet" => ".xlsx", _ => ".pptx" };
        string filePath = Path.Combine(outputDirectory, appKind + extension);
        object? wpsApp = null;
        object? document = null;
        try
        {
            (wpsApp, string usedProgId) = CreateWpsApplication(appKind);
            dynamic app = wpsApp;
            Console.WriteLine($"[spike] instantiated via ProgID '{usedProgId}'.");
            TrySet(() => app.Visible = false, "Visible = false");
            TrySet(() => app.DisplayAlerts = 0, "DisplayAlerts = 0");
            TrySet(() => app.ScreenUpdating = false, "ScreenUpdating = false");

            document = CreateAndPopulate(app, appKind, marker);
            SaveDocument(document, appKind, filePath);
            CloseDocument(document, appKind, saveChanges: true);
            Marshal.FinalReleaseComObject(document);
            document = null;

            document = OpenDocument(app, appKind, filePath);
            string actual = ReadMarker(document, appKind);
            if (!actual.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException($"Saved marker was not recovered after reopening. Actual: '{actual}'.");

            Console.WriteLine($"[spike] verified edit/save/reopen: {filePath}");
            Console.WriteLine("[spike] RESULT: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] RESULT: FAIL — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            CloseAndQuit(wpsApp, document, appKind);
        }
    }

    private static object CreateAndPopulate(dynamic app, string appKind, string marker)
    {
        switch (appKind)
        {
            case "writer":
                dynamic writer = app.Documents.Add();
                writer.Content.Text = marker;
                return writer;
            case "spreadsheet":
                dynamic workbook = app.Workbooks.Add();
                workbook.Worksheets[1].Cells[1, 1].Value2 = marker;
                return workbook;
            case "presentation":
                dynamic presentation = app.Presentations.Add();
                dynamic slide = presentation.Slides.Add(1, 12); // ppLayoutBlank
                dynamic shape = slide.Shapes.AddTextbox(1, 10, 10, 600, 80); // msoTextOrientationHorizontal
                shape.TextFrame.TextRange.Text = marker;
                Marshal.FinalReleaseComObject(shape);
                Marshal.FinalReleaseComObject(slide);
                return presentation;
            default: throw new ArgumentOutOfRangeException(nameof(appKind));
        }
    }

    private static void SaveDocument(dynamic document, string appKind, string filePath)
    {
        if (appKind == "writer") document.SaveAs2(filePath);
        else document.SaveAs(filePath);
    }

    private static string ReadMarker(dynamic document, string appKind) => appKind switch
    {
        "writer" => (string)document.Content.Text,
        "spreadsheet" => Convert.ToString(document.Worksheets[1].Cells[1, 1].Value2) ?? "",
        "presentation" => (string)document.Slides[1].Shapes[1].TextFrame.TextRange.Text,
        _ => throw new ArgumentOutOfRangeException(nameof(appKind)),
    };

    private static void CloseDocument(dynamic document, string appKind, bool saveChanges)
    {
        if (appKind == "presentation") document.Close();
        else document.Close(SaveChanges: saveChanges);
    }

    private static (object app, string progId) CreateWpsApplication(string appKind)
    {
        foreach (var progId in ProgIdCandidates[appKind])
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type == null)
            {
                Console.WriteLine($"[spike] ProgID '{progId}' not registered on this machine, trying next candidate.");
                continue;
            }

            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Activator.CreateInstance returned null for '{progId}'.");
            return (instance, progId);
        }

        throw new InvalidOperationException(
            $"None of the candidate ProgIDs for '{appKind}' are registered: [{string.Join(", ", ProgIdCandidates[appKind])}]. " +
            "Confirm WPS is installed and check its actual registered ProgID via regedit (HKEY_CLASSES_ROOT) if this list is wrong.");
    }

    private static object OpenDocument(dynamic app, string appKind, string filePath)
    {
        return appKind switch
        {
            // Named-argument-shaped calls below are how VBA/Office automation typically exposes
            // these Open() overloads; exact parameter names/order should be confirmed against
            // WPS's own object model docs if this throws a missing-member/argument error.
            "writer" => app.Documents.Open(filePath, ReadOnly: true),
            "spreadsheet" => app.Workbooks.Open(filePath),
            "presentation" => app.Presentations.Open(filePath, WithWindow: false),
            _ => throw new ArgumentOutOfRangeException(nameof(appKind)),
        };
    }

    private static void ReportContentAndTryPaging(dynamic document, string appKind)
    {
        switch (appKind)
        {
            case "presentation":
                int slideCount = document.Slides.Count;
                Console.WriteLine($"[spike] slide count: {slideCount}");
                if (slideCount > 1)
                {
                    // "翻页" check: touching a second slide's object proves navigation through the
                    // object model works, without needing a live SlideShowWindow.
                    var secondSlide = document.Slides[2]; // WPS/Office collections are 1-based.
                    Console.WriteLine($"[spike] accessed slide 2 (layout: {TryGetLayoutName(secondSlide)}).");
                }
                break;

            case "writer":
                int pageCount = document.ComputeStatistics(2); // 2 ~ wdStatisticPages-equivalent.
                Console.WriteLine($"[spike] page count (ComputeStatistics): {pageCount}");
                break;

            case "spreadsheet":
                int sheetCount = document.Worksheets.Count;
                Console.WriteLine($"[spike] worksheet count: {sheetCount}");
                break;
        }
    }

    private static string TryGetLayoutName(dynamic slide)
    {
        try { return (string)slide.Layout.ToString(); }
        catch { return "(unavailable)"; }
    }

    private static void TrySet(Action set, string label)
    {
        try { set(); Console.WriteLine($"[spike] set {label} — ok."); }
        catch (Exception ex) { Console.WriteLine($"[spike] set {label} — failed: {ex.Message}"); }
    }

    private static void CloseAndQuit(object? wpsApp, object? document, string appKind)
    {
        try
        {
            if (document != null)
            {
                CloseDocument((dynamic)document, appKind, saveChanges: false);
                Marshal.FinalReleaseComObject(document);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[spike] cleanup: closing document failed: {ex.Message}"); }

        try
        {
            if (wpsApp != null)
            {
                dynamic app = wpsApp;
                app.Quit();
                Marshal.FinalReleaseComObject(wpsApp);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[spike] cleanup: Quit() failed: {ex.Message}"); }

        // Force the RCWs to actually release now rather than waiting for a GC that may not run
        // before the process exits — the classic "orphaned wps.exe survives after this process
        // ends" failure mode this spike exists partly to catch.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Console.WriteLine("[spike] check Task Manager now: no wps*.exe process should remain from this run.");
    }

    private static string? GetArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
