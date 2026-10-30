using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI;

/// <summary>
/// End-to-end document playback on the real pipeline: the PDF viewer (continuous scrolling, page cache,
/// Preview/Program viewports and sync), and — where WPS / Microsoft Office are installed — a live Word
/// editor captured onto Preview and Program (edit, freeze, update, close without leftover processes), a
/// native PowerPoint slide show (animation steps, black screen, end, cleanup), static slides from the
/// conversion cache (with cache invalidation), and the degraded paths when no office suite is available.
/// The Program overlay is never shown; slide shows run behind the operator's windows.
/// </summary>
internal static class DocumentTests
{
    public static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunCore(directory); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(8))) throw new TimeoutException("Document tests did not finish.");
        if (failure != null) throw new InvalidOperationException("Document tests failed: " + failure.Message, failure);
    }

    private static void RunCore(string directory)
    {
        string docs = Path.Combine(directory, "documents");
        string logs = Path.Combine(directory, "doclogs");
        Directory.CreateDirectory(docs);
        int externalOpens = 0;
        ExternalOpener.TestHook = _ => { externalOpens++; return true; };

        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(640, 400) };
        var previewSurface = new PreviewSurface { Dock = DockStyle.Fill };
        form.Controls.Add(previewSurface);
        _ = form.Handle;
        form.PerformLayout();
        var settings = new SettingsStore(Path.Combine(directory, "doc-settings.json"));
        var scenarios = new ScenarioStore();
        var sessions = new OfficeSessionManager(logs);
        var cache = new DocumentConversionCache(Path.Combine(directory, "convcache"), sessions.Log);
        var machine = new OutputStateMachine();
        using var overlay = new OverlayWindow(new MonitorInfo("SELFTEST", new Rectangle(0, 0, 480, 270), false));
        _ = overlay.Handle; // never shown
        using var preview = new PlaybackEngine(PlaybackChannel.Preview, previewSurface, null, settings, scenarios, null, logs)
        { OfficeSessions = sessions, ConversionCache = cache, Muted = true };
        using var program = new PlaybackEngine(PlaybackChannel.Program, overlay, null, settings, scenarios, machine, logs)
        { OfficeSessions = sessions, ConversionCache = cache, Muted = true };
        var sync = new DocumentProgramSync(preview, () => program);

        try
        {
            TestPdf(docs, previewSurface, overlay, preview, program, sync, machine);
            TestOffice(docs, preview, program, sync, machine, cache);
        }
        finally
        {
            preview.Stop();
            program.Stop();
            sessions.CloseAllAndWait(TimeSpan.FromSeconds(60));
            OfficeInstallation.SimulateNotInstalled = false;
            ExternalOpener.TestHook = null;
        }
        Require(externalOpens == 0, "A document path launched an external application.");
        TestMainWindowStarts(directory);
    }

    /// <summary>The real main window (preview dock, document control row, side bar) constructs, shows,
    /// lays out and disposes — catches start-up crashes that engine-level tests cannot see.</summary>
    private static void TestMainWindowStarts(string directory)
    {
        string dir = Path.Combine(directory, "mainwindow");
        Directory.CreateDirectory(dir);
        using var window = new MainWindow(new OutputStateMachine(), null, new FileLibraryStore(Path.Combine(dir, "library.json")),
            new EveryStage.Terminal.Devices.PairedDeviceStore(Path.Combine(dir, "paired.json")), new ScenarioStore(),
            new ScenarioRepository(Path.Combine(dir, "scenarios.json")), new SettingsStore(Path.Combine(dir, "settings.json")),
            EveryStage.Discovery.DeviceIdentity.LoadOrCreate("selftest", Path.Combine(dir, "identity.json")),
            new EveryStage.Terminal.Logging.DeviceConnectionLogger(dir))
        { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        window.Show();
        Pump(400);
        window.Size = new Size(1000, 760);
        Pump(200);
        CaptureMainWindow(window, dir);
        window.Close();
        Console.WriteLine("PASS: main window starts, lays out and closes with the document controls");

        // Background-music playlists persist (mode, tracks, startup flag, mixer settings).
        string musicPath = Path.Combine(dir, "music.json");
        var music = new MusicLibraryStore(musicPath);
        music.Library.Playlists.Add(new Playlist { Name = "开场歌单", Tracks = { "a.mp3", "b.mp3" }, Mode = PlaylistMode.Shuffle, AutoPlayOnStartup = true });
        music.Library.Volume = 0.5f;
        music.Library.DuckForVideo = false;
        music.Save();
        var reloaded = new MusicLibraryStore(musicPath).Library;
        Require(reloaded.Playlists.Single() is { Name: "开场歌单", Mode: PlaylistMode.Shuffle, AutoPlayOnStartup: true, Tracks.Count: 2 }
            && Math.Abs(reloaded.Volume - 0.5f) < 0.001 && !reloaded.DuckForVideo, "Playlists did not round-trip.");
        Console.WriteLine("PASS: background-music playlists persist");

        TestSlideTransitions();
    }

    private static Control? FindByTypeName(Control root, string name)
    {
        foreach (Control child in root.Controls)
        {
            if (child.GetType().Name == name) return child;
            if (FindByTypeName(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>PPT 翻页过场: every effect × direction starts on the old page, ends on the new one and
    /// shows both halfway; the surface's transition layer paints and finishes.</summary>
    private static void TestSlideTransitions()
    {
        const int w = 160, h = 90;
        using var from = new Bitmap(w, h);
        using var to = new Bitmap(w, h);
        using (var g = Graphics.FromImage(from)) g.Clear(Color.FromArgb(220, 30, 30));
        using (var g = Graphics.FromImage(to)) g.Clear(Color.FromArgb(30, 30, 220));
        bool IsFrom(Color c) => c.R > 180 && c.B < 70;
        bool IsTo(Color c) => c.B > 180 && c.R < 70;
        foreach (SlideTransitionEffect effect in Enum.GetValues<SlideTransitionEffect>())
            foreach (SlideTransitionDirection direction in Enum.GetValues<SlideTransitionDirection>())
            {
                using var frame = new Bitmap(w, h);
                Color Sample(float t, int x, int y)
                {
                    using (var g = Graphics.FromImage(frame)) SlideTransitions.Draw(g, new Rectangle(0, 0, w, h), from, to, t, effect, direction);
                    return frame.GetPixel(x, y);
                }
                Require(IsFrom(Sample(0f, w / 2, h / 2)), $"{effect}/{direction}: t=0 is not the old page.");
                Require(IsTo(Sample(1f, w / 2, h / 2)), $"{effect}/{direction}: t=1 is not the new page.");
                using (var g = Graphics.FromImage(frame)) SlideTransitions.Draw(g, new Rectangle(0, 0, w, h), from, to, 0.5f, effect, direction);
                // Halfway: the start edge already shows the new page, the far edge still the old one.
                (int sx, int sy, int ex, int ey) = direction switch
                {
                    SlideTransitionDirection.TopToBottom => (w / 2, 2, w / 2, h - 3),
                    SlideTransitionDirection.BottomToTop => (w / 2, h - 3, w / 2, 2),
                    SlideTransitionDirection.LeftToRight => (2, h / 2, w - 3, h / 2),
                    _ => (w - 3, h / 2, 2, h / 2),
                };
                if (effect != SlideTransitionEffect.PageTurn)
                    Require(IsTo(frame.GetPixel(sx, sy)) && IsFrom(frame.GetPixel(ex, ey)),
                        $"{effect}/{direction}: halfway does not travel from the start edge ({frame.GetPixel(sx, sy)} / {frame.GetPixel(ex, ey)}).");
                else
                    Require(IsTo(frame.GetPixel(sx, sy)) && IsFrom(frame.GetPixel(ex, ey)),
                        $"{effect}/{direction}: halfway the old page is not pushed toward the far edge.");
            }

        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(w, h) };
        var surface = new ContentSurface { Dock = DockStyle.Fill };
        form.Controls.Add(surface);
        form.Show();
        surface.SetFrame(from);
        Pump(50);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.RainSoak, SlideTransitionDirection.LeftToRight, 200));
        surface.SetFrame(to);
        Require(surface.InTransition, "The surface did not start a transition.");
        using (var shot = new Bitmap(w, h)) surface.DrawToBitmap(shot, new Rectangle(0, 0, w, h));
        Require(WaitFor(() => { surface.Refresh(); return !surface.InTransition; }, 3000), "The transition did not finish.");
        form.Close();
        Console.WriteLine("PASS: PPT page transitions (渐隐渐显 / 翻页效果 / 雨点浸润 × 4 directions) and the surface transition layer");
    }

    /// <summary>Renders every page in each theme at a large and a small window size into
    /// artifacts/screenshots (uploaded by CI) — the layout review for the UI redesign — and checks that
    /// the 监看 column fits its controls at both sizes.</summary>
    private static void CaptureMainWindow(MainWindow window, string dir)
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots");
        Directory.CreateDirectory(output);
        var type = typeof(MainWindow);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var library = (FileLibraryStore)type.GetField("_library", flags)!.GetValue(window)!;
        for (int i = 0; i < 9; i++)
        {
            string image = Path.Combine(dir, $"示例图片 {i + 1}.jpg");
            using (var bitmap = new Bitmap(320, 200))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(30 + i * 20, 80 + i * 10, 160 - i * 10));
                bitmap.Save(image, ImageFormat.Jpeg);
            }
            library.Import(image);
        }
        // A small run sheet, so the 流程 screenshots show sections, numbered rows and the 下一项 cue.
        var scenarios = (ScenarioStore)type.GetField("_scenarioStore", flags)!.GetValue(window)!;
        var scenario = scenarios.Scenarios.FirstOrDefault(s => s.Id == scenarios.CurrentScenarioId) ?? scenarios.Scenarios.First();
        scenario.Activities.Clear();
        var images = library.Files.Where(f => f.Kind == MediaKind.Image).ToList();
        foreach (var (name, range) in new[] { ("开场", (0, 3)), ("产品发布", (3, 4)), ("签约仪式", (7, 2)) })
        {
            var section = new EveryStage.Terminal.Data.Activity { Name = name };
            foreach (var file in images.Skip(range.Item1).Take(range.Item2)) section.Files.Add(file.Clone());
            scenario.Activities.Add(section);
        }
        scenario.Activities[1].Files[0].StayDuration = TimeSpan.FromSeconds(15);
        var showPanel = type.GetMethod("ShowPanel", flags)!;
        var applyTheme = type.GetMethod("ApplyTheme", flags)!;
        var dock = type.GetField("_previewDock", flags)!.GetValue(window)!;
        var stopOutput = (Control)dock.GetType().GetField("_stopOutputButton", flags)!.GetValue(dock)!;
        string[] pages = { "_filesPanel", "_activitiesPanel", "_devicesPanel", "_settingsPanel" };
        foreach (var size in new[] { new Size(1440, 900), new Size(1024, 640) })
        {
            window.Size = size;
            Pump(150);
            Require(stopOutput.Bottom <= ((Control)dock).ClientSize.Height,
                $"监看 column controls overflow at {size.Width}×{size.Height}.");
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Tech })
            {
                applyTheme.Invoke(window, new object[] { theme });
                foreach (string page in pages)
                {
                    showPanel.Invoke(window, new[] { type.GetField(page, flags)!.GetValue(window)! });
                    Pump(150);
                    using var shot = new Bitmap(window.Width, window.Height);
                    window.DrawToBitmap(shot, new Rectangle(Point.Empty, window.Size));
                    shot.Save(Path.Combine(output, $"{size.Width}x{size.Height}-{theme}-{page.TrimStart('_').Replace("Panel", "")}.png"), ImageFormat.Png);
                    if (page == "_settingsPanel" && FindByTypeName(window, "SlideTransitionPicker") is { } picker
                        && picker.Parent?.Parent is ScrollableControl scroller)
                    {
                        // The PPT 翻页过场 section: scroll it into view for its own screenshot.
                        scroller.ScrollControlIntoView(picker.Parent);
                        Pump(150);
                        using var slides = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(slides, new Rectangle(Point.Empty, window.Size));
                        slides.Save(Path.Combine(output, $"{size.Width}x{size.Height}-{theme}-settings-transitions.png"), ImageFormat.Png);
                        scroller.AutoScrollPosition = Point.Empty;
                    }
                }
            }
        }
        // 文件库 card sizes: cards never overlap, keep their set width, the gap is the same at every size.
        showPanel.Invoke(window, new[] { type.GetField("_filesPanel", flags)!.GetValue(window)! });
        var grid = FindByTypeName(window, "MediaGrid")!;
        var gridType = grid.GetType();
        var columnsSeen = new List<int>();
        var gapsSeen = new List<int>();
        foreach (var (label, width) in new[] { ("small", 85), ("medium", 102), ("large", 122) })
        {
            gridType.GetProperty("CardWidth")!.SetValue(grid, width);
            Pump(100);
            int count = ((System.Collections.ICollection)gridType.GetProperty("Items")!.GetValue(grid)!).Count;
            var bounds = Enumerable.Range(0, count).Select(i => (Rectangle)gridType.GetMethod("CardBounds")!.Invoke(grid, new object[] { i })!).ToList();
            for (int i = 0; i < bounds.Count; i++)
                for (int j = i + 1; j < bounds.Count; j++)
                    Require(!bounds[i].IntersectsWith(bounds[j]), $"文件库 {label}: cards {i} and {j} overlap ({bounds[i]} / {bounds[j]}).");
            int columns = bounds.Count(b => b.Top == bounds[0].Top);
            columnsSeen.Add(columns);
            Require(bounds.All(b => b.Width == grid.LogicalToDeviceUnits(width)), $"文件库 {label}: cards are not {width} wide ({bounds[0].Width}).");
            if (bounds.Count > 1 && columns > 1)
            {
                Require(bounds[1].Left - bounds[0].Right == bounds[columns - 1].Left - bounds[columns - 2].Right, $"文件库 {label}: uneven gaps.");
                gapsSeen.Add(bounds[1].Left - bounds[0].Right);
            }
            Require(bounds.All(b => b.Right <= grid.ClientSize.Width), $"文件库 {label}: a card runs past the right edge.");
            using var shot = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(shot, new Rectangle(Point.Empty, window.Size));
            shot.Save(Path.Combine(output, $"files-card-size-{label}.png"), ImageFormat.Png);
        }
        Require(columnsSeen[0] > columnsSeen[2], $"文件库: 小 and 大 give the same number of columns ({string.Join("/", columnsSeen)}).");
        Require(gapsSeen.Distinct().Count() <= 1, $"文件库: the gap changes with the card size ({string.Join("/", gapsSeen)}).");
        gridType.GetProperty("CardWidth")!.SetValue(grid, 180);

        // Drag to reorder ends in FileLibraryStore.Move: the moved files land before the target.
        var order = library.Files.Select(f => f.Id).ToList();
        library.Move(new[] { order[0], order[1] }, order[4]);
        Require(library.Files.Select(f => f.Id).ToList() is var moved && moved[2] == order[0] && moved[3] == order[1] && moved[4] == order[4],
            "文件库 reorder did not put the files before the target.");
        library.Move(new[] { order[0] }, null);
        Require(library.Files[^1].Id == order[0], "文件库 reorder to the end failed.");

        applyTheme.Invoke(window, new object[] { AppTheme.Dark });
        Console.WriteLine($"INFO: main window screenshots in {output}");
    }

    // ── PDF ──────────────────────────────────────────────────────────────────────────────────────

    private static Color PageColor(int page) => Color.FromArgb(40 + page % 50 * 4, 220 - page % 50 * 3, 60 + page / 50 * 70);

    private static void TestPdf(string docs, PreviewSurface previewSurface, OverlayWindow overlay, PlaybackEngine preview,
        PlaybackEngine program, DocumentProgramSync sync, OutputStateMachine machine)
    {
        const int pages = 120;
        string pdf = Path.Combine(docs, "long.pdf");
        WriteColoredPdf(pdf, pages);
        var file = new MediaFile { SourcePath = pdf, Kind = MediaKind.Document };

        preview.RequestPlay(file);
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.PdfView != null, 10000), "120-page PDF did not open in Preview.");
        var view = preview.PdfView!;
        Require(view.PageCount == pages && view.Interactive, "Preview PDF view has the wrong page count or is not interactive.");
        Require(WaitFor(() => view.CachedPages > 0, 5000), "No PDF page was rendered.");
        Pump(400);
        Require(view.CachedPages <= 8, $"{view.CachedPages} pages rendered for one screen — the viewer must render only visible + preloaded pages.");
        Require(SampleCenter(previewSurface.ContentSurface).Near(PageColor(0), 12), "Preview does not show page 1.");

        // Smooth scrolling across the whole document: never black, reaches the end, memory bounded.
        var sw = Stopwatch.StartNew();
        int steps = 0;
        while (!view.AtEnd && steps < 6000)
        {
            view.ScrollBy(180);
            if (steps++ % 25 == 0) { Application.DoEvents(); }
        }
        Pump(600);
        Require(view.AtEnd, "Scrolling did not reach the end of a 120-page PDF.");
        Require(view.CachedBytes < 200L << 20, $"Page cache grew to {view.CachedBytes >> 20} MB while scrolling.");
        Require(SampleCenter(previewSurface.ContentSurface) is var end && (end.Near(PageColor(pages - 1), 12) || end.Near(PageColor(pages - 2), 12)),
            "Preview does not show the last pages after scrolling to the end.");
        Console.WriteLine($"PASS: PDF continuous scroll through {pages} pages ({steps} steps, {sw.ElapsedMilliseconds} ms), visible-only rendering, cache {view.CachedBytes >> 20} MB");

        // Keyboard / page navigation.
        view.OnKeyDown(Keys.Home);
        Require(view.State.PageIndex == 0 && view.State.PageOffset == 0, "Home did not return to the first page.");
        view.OnKeyDown(Keys.PageDown);
        Require(view.State.PageIndex > 0 || view.State.PageOffset > 0, "PageDown did not scroll.");
        view.GoToPage(30);
        Require(view.State.PageIndex == 29, "Page jump did not land on page 30.");
        Require(preview.NextManual() && view.State.PageIndex == 30, "下一项 did not turn the PDF page.");

        // Resize keeps the same content position (page + offset), not the pixel offset.
        view.ApplyState(view.State with { PageIndex = 44, PageOffset = 0.37 });
        var before = view.State;
        previewSurface.Parent!.ClientSize = new Size(900, 300);
        Pump(300);
        Require(view.State == before, $"Resizing the preview moved the PDF from {before} to {view.State}.");
        previewSurface.Parent.ClientSize = new Size(640, 400);
        Pump(200);

        // Program takes Preview's exact position.
        machine.SetCastSwitch(true);
        Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Program refused the PDF.");
        Require(WaitFor(() => program.State == PlaybackChannelState.Playing && program.PdfView != null, 10000), "PDF did not open on Program.");
        var output = program.PdfView!;
        Require(!output.Interactive && overlay.ContentSurface.HideCursor, "Program PDF view shows chrome or a mouse pointer.");
        Require(output.State == view.State, $"Program position {output.State} differs from Preview {view.State} after Take.");
        Require(sync.ProgramShowsSameDocument, "Sync does not see the same document on both channels.");

        // Live sync: Preview moves → Program follows exactly.
        view.GoToPage(50);
        view.ScrollBy(123);
        Require(output.State == view.State, "Live sync: Program did not follow Preview's scroll position.");

        // Freeze: Preview browses, Program holds; 更新大屏 copies the exact position.
        sync.ToggleFreeze();
        var held = output.State;
        view.GoToPage(80);
        view.ScrollBy(321);
        Require(output.State == held && program.DocumentFrozen, "Frozen Program moved while Preview browsed.");
        sync.UpdateProgram();
        Require(output.State == view.State && !program.DocumentFrozen, "更新大屏 did not bring Program to Preview's page and offset.");

        // Live sync off: same as frozen until 更新大屏.
        sync.SetLiveSync(false);
        view.GoToPage(100);
        Require(output.State.PageIndex != 99, "Program followed Preview with live sync off.");
        sync.UpdateProgram();
        Require(output.State == view.State, "更新大屏 with live sync off did not sync.");
        sync.SetLiveSync(true);

        // Program pixels: the synced page, no scrollbar or page box.
        overlay.ClientSize = new Size(480, 270);
        overlay.ContentSurface.Bounds = overlay.ClientRectangle;
        Require(WaitFor(() => { Application.DoEvents(); return SampleCenter(overlay.ContentSurface).Near(PageColor(output.CurrentPage - 1), 12); }, 5000),
            "Program does not show the synced page.");
        using (var shot = Capture(overlay.ContentSurface))
        {
            var edge = shot.GetPixel(shot.Width - 4, shot.Height / 2);
            Require(!edge.Near(Color.FromArgb(20, 22, 26), 4) && !edge.Near(Color.FromArgb(110, 125, 145), 4), "Program output shows a scrollbar.");
        }

        // Single-page mode keeps the other channel independent.
        view.SetDisplayMode(PdfDisplayMode.SinglePage);
        Require(view.State.DisplayMode == PdfDisplayMode.SinglePage && output.State.DisplayMode == PdfDisplayMode.SinglePage, "Single-page mode did not sync.");
        program.Stop();
        Require(preview.State == PlaybackChannelState.Playing && preview.PdfView == view, "Stopping Program affected Preview.");
        Console.WriteLine("PASS: PDF Preview/Program viewports: Take at exact position, live sync, freeze + 更新大屏, sync off, resize keeps position, no chrome/pointer on Program");

        // A corrupt PDF fails inside EveryStage.
        string bad = Path.Combine(docs, "bad.pdf");
        File.WriteAllText(bad, "%PDF-1.4 not really");
        preview.RequestPlay(new MediaFile { SourcePath = bad, Kind = MediaKind.Document });
        Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 8000), "A corrupt PDF did not fail.");
        Require(preview.LastError != null, "A corrupt PDF failed without error details.");
        preview.Stop();
        Console.WriteLine("PASS: corrupt PDF shows an internal error (no external viewer)");
    }

    // ── WPS / Office ─────────────────────────────────────────────────────────────────────────────

    private static void TestOffice(string docs, PlaybackEngine preview, PlaybackEngine program, DocumentProgramSync sync,
        OutputStateMachine machine, DocumentConversionCache cache)
    {
        bool word = OfficeInstallation.IsRegistered(OfficeInstallation.Word) || OfficeInstallation.IsRegistered(OfficeInstallation.WpsWriter);
        bool powerPoint = OfficeInstallation.IsRegistered(OfficeInstallation.PowerPoint);
        Console.WriteLine($"INFO: Word/WPS writer {(word ? "available" : "UNAVAILABLE")}, PowerPoint {(powerPoint ? "available" : "UNAVAILABLE")}");
        machine.SetCastSwitch(true);
        string docx = Path.Combine(docs, "live.docx");
        string pptx = Path.Combine(docs, "deck.pptx");
        bool haveDocx = word && TryMakeDocx(docx);
        bool havePptx = powerPoint && TryMakePptx(pptx);

        if (haveDocx) TestWordLive(docx, preview, program, sync);
        else Console.WriteLine("SKIP: Word live editing (no WPS/Word)");

        if (havePptx)
        {
            TestNativeShow(pptx, preview, program);
            TestStaticSlides(pptx, preview, cache);
            TestShowApplicationExit(pptx, program);
        }
        else Console.WriteLine("SKIP: PowerPoint native show / static slides (no PowerPoint)");

        // No office suite at all: PDF and cached static slides keep working; Word degrades with a message.
        OfficeInstallation.SimulateNotInstalled = true;
        try
        {
            if (havePptx)
            {
                var deck = new MediaFile { SourcePath = pptx, Kind = MediaKind.Document };
                preview.RequestPlay(deck);
                Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.PdfView != null, 15000), "Cached static slides did not play without an office suite.");
                Require(preview.DocumentMode == DocumentPlaybackMode.StaticSlides, "Without an office suite the presentation was not played as static slides.");
            }
            if (haveDocx)
            {
                File.SetLastWriteTimeUtc(docx, DateTime.UtcNow.AddMinutes(1)); // no conversion cached for this version
                preview.RequestPlay(new MediaFile { SourcePath = docx, Kind = MediaKind.Document });
                Require(WaitFor(() => preview.State is PlaybackChannelState.Failed or PlaybackChannelState.Playing, 15000), "Word without an editor did not settle.");
                string why = preview.LastError?.Message ?? preview.CurrentContentNote ?? "";
                Require(why.Contains("未安装"), $"Word without an editor did not explain the missing component: '{why}'.");
            }
            preview.Stop();
            Console.WriteLine("PASS: without WPS/Office: cached static slides still play; Word explains the missing editor");
        }
        finally { OfficeInstallation.SimulateNotInstalled = false; }
    }

    private static void TestWordLive(string docx, PlaybackEngine preview, PlaybackEngine program, DocumentProgramSync sync)
    {
        var baseline = OfficeProcessIds();
        // Automatic editor choice (WPS first, then Word) — the product default.
        var file = new MediaFile { SourcePath = docx, Kind = MediaKind.Document, Document = new DocumentPlaybackSettings { OpenMode = OfficeOpenMode.Auto } };
        preview.RequestPlay(file);
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.OfficeSession?.Status == OfficeSessionStatus.Ready, 60000),
            $"Word document did not open live: {preview.LastError?.Message ?? preview.CurrentContentNote}");
        var session = (WriterSession)preview.OfficeSession!;
        Require(WaitFor(() => session.FrameCount > 3, 8000), "The editor window is not being captured.");
        Console.WriteLine($"INFO: {session.SuiteName} {session.Version}, capture {session.CaptureMethod}");

        Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Program refused the Word document.");
        Require(WaitFor(() => program.State == PlaybackChannelState.Playing && program.OfficeFrameSequence >= 0, 15000), "Program shows no editor frame.");
        Require(ReferenceEquals(program.OfficeSession, session), "Program opened a second editor instead of sharing Preview's.");

        // Live: an edit reaches Program.
        long seq = program.OfficeFrameSequence;
        Require(Await(session.InsertTextForTestAsync("【实时】"), 20000), "Editing through automation timed out.");
        Require(WaitFor(() => program.OfficeFrameSequence > seq, 5000), "An edit did not reach Program in live sync.");

        // Frozen: edits continue in the editor, Program keeps its picture; 更新大屏 shows the latest.
        sync.ToggleFreeze();
        long frozen = program.OfficeFrameSequence;
        Require(Await(session.InsertTextForTestAsync("【冻结中修改】"), 20000), "Editing through automation timed out.");
        Require(WaitFor(() => preview.OfficeFrameSequence > frozen + 2, 5000), "Preview did not keep receiving editor frames.");
        Pump(500);
        Require(program.OfficeFrameSequence == frozen, "A frozen Program picked up an edit.");
        sync.UpdateProgram();
        Require(program.OfficeFrameSequence > frozen && !program.DocumentFrozen, "更新大屏 did not show the latest editor frame.");

        // Save (a read-only document must refuse with a clear reason instead), then close everything: no
        // editor process may survive.
        var captureMethod = session.CaptureMethod;
        var save = preview.SaveDocumentAsync();
        Require(Await(save, 20000), "Save did not finish.");
        if (session.ReadOnly)
            Require(!save.Result && (preview.CurrentContentNote ?? "").Contains("只读"), $"Saving a read-only document did not explain why: '{preview.CurrentContentNote}'.");
        else Require(save.Result, $"Save failed: {preview.CurrentContentNote}");
        program.Stop();
        Require(session.Status == OfficeSessionStatus.Ready, "Stopping Program closed the editor Preview still uses.");
        preview.Stop();
        Require(WaitFor(() => session.Status == OfficeSessionStatus.Closed, 30000), "The Word session did not close.");
        Require(WaitFor(() => !OfficeProcessIds().Except(baseline).Any(), 15000), "A WPS/Word process outlived the session: " + string.Join(",", OfficeProcessIds().Except(baseline)));
        Console.WriteLine($"PASS: Word live editing ({session.SuiteName}, capture {captureMethod}{(session.ReadOnly ? ", opened read-only" : "")}): shared editor capture on Preview and Program, live edits, freeze + 更新大屏, save, no leftover process");
    }

    private static void TestNativeShow(string pptx, PlaybackEngine preview, PlaybackEngine program)
    {
        var baseline = OfficeProcessIds();
        var file = new MediaFile { SourcePath = pptx, Kind = MediaKind.Document };
        Require(PresentationInspector.Inspect(pptx) is { NeedsNativeShow: true, HasAnimations: true, SlideCount: 3 }, "The inspector did not see the deck's animations.");
        // Preview browses static slides (no show window on the operator screen); Take starts the native
        // show on Program at the previewed slide.
        preview.RequestPlay(file);
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.PdfView != null, 90000),
            $"Preview did not show the deck as static slides: {preview.LastError?.Message ?? preview.CurrentContentNote}");
        Require(preview.DocumentMode == DocumentPlaybackMode.StaticSlides && preview.OfficeSession == null, "Preview started a native show.");
        Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Program refused the deck.");
        Require(WaitFor(() => program.State == PlaybackChannelState.Playing && program.OfficeSession?.Status == OfficeSessionStatus.Ready, 60000),
            $"Native show did not start on Program: {program.LastError?.Message ?? program.CurrentContentNote}");
        Require(program.DocumentMode == DocumentPlaybackMode.NativeShow, "An animated deck was not played as a native show on Program.");
        var show = (SlideShowSession)program.OfficeSession!;
        Require(WaitFor(() => show.FrameCount > 3, 8000), "The slide show is not being captured.");
        Console.WriteLine($"INFO: {show.SuiteName} {show.Version}, capture {show.CaptureMethod}{(show.CaptureFallbackReason is { } why ? " (WGC: " + why.Trim() + ")" : "")}");
        Require(WaitFor(() =>
        {
            var frame = show.AcquireLatestFrame();
            try { return frame != null && !CaptureFrames.IsBlank(frame.Bitmap); }
            finally { frame?.Release(); }
        }, 8000), "The captured slide show is blank.");

        Require(WaitFor(() => program.OfficeFrameSequence >= 0, 15000), "Program shows no slide-show frame.");

        void Step(Func<SlideShowSession, Task> command) { Require(Await(program.SlideCommandAsync(command, "test"), 20000), "Slide command timed out."); Pump(400); }
        Require(WaitFor(() => show.Position.Slide == 1, 5000), "The show did not start at slide 1.");
        Step(s => s.NextStepAsync());
        Require(show.Position.Slide == 2 && show.Position.Click == 0 && show.Position.Clicks == 2, $"Next step from slide 1: {show.Position}.");
        Step(s => s.NextStepAsync());
        Require(show.Position.Slide == 2 && show.Position.Click == 1, $"下一步 did not play the first animation before leaving slide 2: {show.Position}.");
        Step(s => s.NextStepAsync());
        Step(s => s.NextStepAsync());
        Require(show.Position.Slide == 3, $"After both animations 下一步 did not move to slide 3: {show.Position}.");
        Step(s => s.GoToSlideAsync(1));
        Require(show.Position.Slide == 1, "Jump to slide 1 failed.");
        Step(s => s.ToggleScreenAsync(SlideShowScreen.Black));
        Require(show.Position.Screen == SlideShowScreen.Black, "Black screen did not engage.");
        Step(s => s.ToggleScreenAsync(SlideShowScreen.Black));
        Require(show.Position.Screen == SlideShowScreen.Running, "Black screen did not release.");
        Require(Await(program.EndShowAsync(), 20000) && WaitFor(() => show.Status == OfficeSessionStatus.Ended, 5000), "End show failed.");
        Require(program.OfficeFrameSequence >= 0 && program.CurrentThumbnail != null, "Program lost its last frame when the show ended.");

        program.Stop();
        preview.Stop();
        Require(WaitFor(() => show.Status == OfficeSessionStatus.Closed, 30000), "The slide-show session did not close.");
        Require(WaitFor(() => !OfficeProcessIds().Except(baseline).Any(), 15000), "A PowerPoint/WPS process outlived the show.");
        Console.WriteLine("PASS: PowerPoint: static slides in Preview, native show only on Program, 下一步 plays animations before the next slide, jump, black screen, end keeps last frame, no leftover process");
    }

    private static void TestStaticSlides(string pptx, PlaybackEngine preview, DocumentConversionCache cache)
    {
        var file = new MediaFile { SourcePath = pptx, Kind = MediaKind.Document, Document = new DocumentPlaybackSettings { PresentationMode = PresentationMode.Static } };
        preview.RequestPlay(file);
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.PdfView != null, 90000),
            $"Static slides did not play: {preview.LastError?.Message ?? preview.CurrentContentNote}");
        var view = preview.PdfView!;
        Require(view.PageCount == 3 && view.State.DisplayMode == PdfDisplayMode.SinglePage, "Static slides are not 3 single pages.");
        Require((preview.CurrentContentNote ?? "").Contains("不含动画"), "Static mode does not warn that animations are not kept.");
        Require(preview.NextManual() && view.State.PageIndex == 1, "下一项 did not advance a static slide.");
        Require(cache.TryGet(pptx) != null, "The conversion was not cached.");
        preview.Stop();
        Console.WriteLine("PASS: static slides from the conversion cache (single pages, next slide, limitation notice)");
    }

    /// <summary>The presentation application dies mid-show (crash, killed): the show degrades to static
    /// slides at the same slide with the reason on screen, and nothing is left running.</summary>
    private static void TestShowApplicationExit(string pptx, PlaybackEngine program)
    {
        var preview = program; // the native show runs only on Program
        var baseline = OfficeProcessIds();
        preview.RequestPlay(new MediaFile { SourcePath = pptx, Kind = MediaKind.Document });
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.OfficeSession?.Status == OfficeSessionStatus.Ready, 60000),
            $"Native show did not start: {preview.LastError?.Message ?? preview.CurrentContentNote}");
        foreach (int id in OfficeProcessIds().Except(baseline))
            try { System.Diagnostics.Process.GetProcessById(id).Kill(); } catch (Exception) { }
        Require(WaitFor(() => preview.DocumentMode == DocumentPlaybackMode.StaticSlides && preview.PdfView != null, 60000),
            $"A crashed show did not fall back to static slides: {preview.DocumentMode} / {preview.CurrentContentNote}");
        Require((preview.CurrentContentNote ?? "").Contains("意外退出"), $"The fallback does not say why: {preview.CurrentContentNote}");
        Require(preview.State == PlaybackChannelState.Playing && preview.LastError == null, "The crash surfaced as a playback failure.");
        preview.Stop();
        Require(WaitFor(() => !OfficeProcessIds().Except(baseline).Any(), 15000), "A PowerPoint/WPS process outlived the crashed show.");
        Console.WriteLine("PASS: presentation application exits mid-show: falls back to static slides with the reason, no leftover process");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A minimal WordprocessingML package written directly (no Office needed to create it).</summary>
    private static bool TryMakeDocx(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            void Entry(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false));
                writer.Write(xml);
            }
            Entry("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Entry("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            var body = new System.Text.StringBuilder();
            for (int i = 1; i <= 60; i++) body.Append($"<w:p><w:r><w:t>第 {i} 段：EveryStage 文档投屏自检。</w:t></w:r></w:p>");
            Entry("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                + body + "</w:body></w:document>");
            return true;
        }
        catch (Exception ex) { Console.WriteLine("INFO: could not create a .docx fixture: " + ex.Message); return false; }
    }

    private static bool TryMakePptx(string path)
    {
        var baseline = OfficeProcessIds();
        dynamic? app = null;
        try
        {
            app = Activator.CreateInstance(Type.GetTypeFromProgID(OfficeInstallation.PowerPoint)!)!;
            dynamic pres = app.Presentations.Add(0);
            for (int i = 1; i <= 3; i++)
            {
                dynamic slide = pres.Slides.Add(i, 12);
                dynamic box = slide.Shapes.AddTextbox(1, 60, 60, 600, 100);
                box.TextFrame.TextRange.Text = $"幻灯片 {i}";
                slide.NotesPage.Shapes.Placeholders(2).TextFrame.TextRange.Text = $"备注 {i}";
                if (i == 2)
                {
                    slide.TimeLine.MainSequence.AddEffect(slide.Shapes.AddShape(1, 100, 250, 200, 120), 10, 0, 1);
                    slide.TimeLine.MainSequence.AddEffect(slide.Shapes.AddShape(9, 400, 250, 200, 120), 10, 0, 1);
                }
            }
            pres.SaveAs(path);
            pres.Close();
            return File.Exists(path);
        }
        catch (Exception ex) { Console.WriteLine("INFO: could not create a .pptx fixture: " + ex.Message); return false; }
        finally
        {
            try { app?.Quit(); } catch (Exception) { }
            if (app != null) Marshal.FinalReleaseComObject(app);
            KillNew(baseline);
        }
    }

    private static HashSet<int> OfficeProcessIds() =>
        new[] { "WINWORD", "POWERPNT", "wps", "wpp" }.SelectMany(Process.GetProcessesByName).Select(p => p.Id).ToHashSet();

    private static void KillNew(HashSet<int> baseline)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var deadline = Stopwatch.StartNew();
        while (OfficeProcessIds().Except(baseline).Any() && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(200);
        foreach (int id in OfficeProcessIds().Except(baseline))
        {
            try { Process.GetProcessById(id).Kill(); } catch (Exception) { }
        }
    }

    /// <summary>A PDF whose every page is filled with its own colour (page index readable from a pixel);
    /// every third page is landscape.</summary>
    private static void WriteColoredPdf(string path, int pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        for (int i = 0; i < pages; i++)
        {
            bool landscape = i % 3 == 2;
            int w = landscape ? 842 : 595, h = landscape ? 595 : 842;
            var c = PageColor(i);
            string stream = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{c.R / 255.0:0.###} {c.G / 255.0:0.###} {c.B / 255.0:0.###} rg 0 0 {w} {h} re f");
            int pageObj = objects.Count + 1, contentObj = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << >> /Contents {contentObj} 0 R >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
            kids.Add($"{pageObj} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pages} >>";
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static Color SampleCenter(Control control)
    {
        using var shot = Capture(control);
        return shot.GetPixel(shot.Width / 2, shot.Height / 2);
    }

    private static bool Near(this Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(15);
        }
        return condition();
    }

    /// <summary>Waits for a task while pumping messages (its continuations may need this UI thread).</summary>
    private static bool Await(Task task, int timeoutMs) => WaitFor(() => task.IsCompleted, timeoutMs) && !task.IsFaulted;

    private static void Pump(int ms) => WaitFor(() => false, ms);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
