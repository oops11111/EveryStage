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
using EveryStage.Terminal.UI.Panels;
using StageActivity = EveryStage.Terminal.Data.Activity;

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
    public static void RunOperatorSmoke(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { TestContainedThumbnails(directory); TestThumbnailDiskCache(directory); TestVisibleThumbnails(directory); TestActivityThumbnailAvailability(directory); TestPresenterContentOrdering(); TestDocumentOutputDisposal(directory); TestThemeRoles(); TestAudioOutputSettings(directory); TestDefaultAudioNotifications(); TestHotkeyRegistration(); TestMainWindowStarts(directory); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Operator UI tests did not finish.");
        if (failure != null) throw new InvalidOperationException("Operator UI tests failed.", failure);
    }

    private static void TestContainedThumbnails(string directory)
    {
        string path = Path.Combine(directory, "portrait-thumbnail.png");
        using (var source = new Bitmap(80, 160))
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Lime);
            graphics.FillRectangle(Brushes.Red, 0, 0, 80, 30);
            graphics.FillRectangle(Brushes.Blue, 0, 130, 80, 30);
            source.Save(path, ImageFormat.Png);
        }
        using var thumbnail = (Bitmap)FilesPanel.BuildThumbnail(new MediaFile { SourcePath = path, Kind = MediaKind.Image });
        Require(thumbnail.GetPixel(120, 8).R > 200 && thumbnail.GetPixel(120, 141).B > 200,
            "Portrait thumbnail cropped its top or bottom.");
        Require(thumbnail.GetPixel(10, 75).ToArgb() == Color.FromArgb(20, 37, 58).ToArgb(),
            "Portrait thumbnail did not leave centered side margins.");
        using var landscape = new Bitmap(160, 80);
        using (var graphics = Graphics.FromImage(landscape))
        {
            graphics.Clear(Color.Lime);
            graphics.FillRectangle(Brushes.Red, 0, 0, 30, 80);
            graphics.FillRectangle(Brushes.Blue, 130, 0, 30, 80);
        }
        using var canvas = new Bitmap(100, 100);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            ThumbnailDrawing.DrawContained(graphics, landscape, new Rectangle(0, 0, 100, 100));
        }
        Require(canvas.GetPixel(5, 50).R > 200 && canvas.GetPixel(94, 50).B > 200,
            "Landscape thumbnail cropped its left or right edge.");
        Require(canvas.GetPixel(50, 5).ToArgb() == Color.Black.ToArgb(),
            "Landscape thumbnail did not leave centered top and bottom margins.");
        Console.WriteLine("PASS: portrait and landscape thumbnails retain image edges and centered margins");
    }

    private static void TestVisibleThumbnails(string directory)
    {
        string imagePath = Path.Combine(directory, "viewport.png");
        using (var image = new Bitmap(40, 30)) image.Save(imagePath, ImageFormat.Png);
        var library = new FileLibraryStore(Path.Combine(directory, "viewport-library.json"));
        library.Import(imagePath);
        // The viewport test does not exercise persistence; avoid 34 redundant atomic file replacements.
        var files = (List<MediaFile>)library.Files;
        for (int i = 1; i < 35; i++) files.Add(new MediaFile { SourcePath = imagePath, Kind = MediaKind.Image });
        string missingPath = Path.Combine(directory, "missing-viewport.png");
        var missing = new MediaFile { SourcePath = missingPath, Kind = MediaKind.Image };
        files.Insert(0, missing);
        using var form = new Form { ClientSize = new Size(700, 480), ShowInTaskbar = false, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        using var panel = new FilesPanel(library, new EveryStage.Terminal.Logging.FileOperationLogger(directory),
            new ScenarioStore(), new ScenarioRepository(Path.Combine(directory, "viewport-scenarios.json")), null);
        form.Controls.Add(panel);
        form.Show();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var grid = (MediaGrid)typeof(FilesPanel).GetField("_grid", flags)!.GetValue(panel)!;
        var loaded = (Dictionary<string, Image>)typeof(FilesPanel).GetField("_thumbnailByFileId", flags)!.GetValue(panel)!;
        Require(WaitFor(() => grid.VisibleItems().Count > 0 && grid.VisibleItems().All(item => loaded.ContainsKey(item.File.Id.ToString())), 5000),
            "Visible thumbnails did not load.");
        var missingItem = grid.Items[0];
        Require(missingItem.Unavailable && missingItem.ToolTip.Contains("文件不可用") && missingItem.Meta.Contains("文件不可用"),
            "A missing visible file was not marked unavailable.");
        using (var restored = new Bitmap(40, 30)) restored.Save(missingPath, ImageFormat.Png);
        Require(WaitFor(() => loaded.ContainsKey(missing.Id.ToString()) && !grid.Items[0].Unavailable, 9000)
            && !grid.Items[0].ToolTip.Contains("文件不可用"), "A restored visible file retained its unavailable status.");
        File.Delete(missingPath);
        Require(WaitFor(() => loaded.ContainsKey(missing.Id.ToString()) && grid.Items[0].Unavailable, 9000),
            "A removed visible file was not marked unavailable automatically.");
        using (var restored = new Bitmap(40, 30)) restored.Save(missingPath, ImageFormat.Png);
        Require(WaitFor(() => loaded.ContainsKey(missing.Id.ToString()) && !grid.Items[0].Unavailable, 9000),
            "A visible file did not recover after being restored again.");
        using (var changed = new Bitmap(80, 40))
        using (var graphics = Graphics.FromImage(changed))
        {
            graphics.Clear(Color.Lime);
            changed.Save(missingPath, ImageFormat.Png);
        }
        Require(WaitFor(() => loaded.TryGetValue(missing.Id.ToString(), out var image)
            && ((Bitmap)image).GetPixel(120, 75).G > 200, 9000), "An edited visible image did not refresh its thumbnail.");
        Require(loaded.Count < grid.Items.Count && !loaded.ContainsKey(grid.Items[^1].File.Id.ToString()),
            "Offscreen thumbnails were loaded eagerly.");
        string initial = grid.Items[0].File.Id.ToString();
        grid.EnsureVisible(grid.Items[^1]);
        Require(WaitFor(() => loaded.ContainsKey(grid.Items[^1].File.Id.ToString()), 5000),
            "Scrolling did not load the newly visible thumbnail.");
        Require(!loaded.ContainsKey(initial) && loaded.Keys.All(key => grid.VisibleItems().Any(item => item.File.Id.ToString() == key)),
            "Offscreen thumbnail images were retained after scrolling.");
        grid.EnsureVisible(grid.Items[0]);
        Require(WaitFor(() => loaded.ContainsKey(initial), 5000), "Returning to the first row did not restore its thumbnail.");
        form.ClientSize = new Size(500, 380);
        Require(WaitFor(() => grid.VisibleItems().All(item => loaded.ContainsKey(item.File.Id.ToString()))
            && loaded.Keys.All(key => grid.VisibleItems().Any(item => item.File.Id.ToString() == key)), 5000),
            "Resizing did not update visible thumbnails.");
        Console.WriteLine("PASS: visible thumbnails, scrolling/resizing, offscreen release and automatic missing/restored/edited file updates");
    }

    private static void TestThumbnailDiskCache(string directory)
    {
        string root = Path.Combine(directory, "disk-cache-test");
        string source = Path.Combine(directory, "disk-cache-source.png");
        static void WriteImage(string path, int width, int height, Color color)
        {
            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(color);
            bitmap.Save(path, ImageFormat.Png);
        }
        WriteImage(source, 40, 30, Color.Red);
        var file = new MediaFile { SourcePath = source, Kind = MediaKind.Image };
        var cache = new ThumbnailDiskCache(root);
        using (var image = (Bitmap)cache.GetOrBuild(file)) Require(image.GetPixel(120, 75).R > 200, "Initial thumbnail did not decode.");
        string cached = Directory.GetFiles(root, "*.png").Single();
        WriteImage(cached, 240, 150, Color.Blue);
        using (var image = (Bitmap)new ThumbnailDiskCache(root).GetOrBuild(file))
            Require(image.GetPixel(120, 75).B > 200, "A new cache instance did not read the existing disk thumbnail.");
        File.WriteAllText(cached, "corrupt");
        using (var image = (Bitmap)cache.GetOrBuild(file)) Require(image.GetPixel(120, 75).R > 200, "Corrupt disk cache did not rebuild.");
        WriteImage(source, 80, 40, Color.Lime);
        using (var image = (Bitmap)cache.GetOrBuild(file)) Require(image.GetPixel(120, 75).G > 200, "Changed source reused its old thumbnail.");
        int entries = Directory.GetFiles(root, "*.png").Length;
        var missing = new MediaFile { SourcePath = Path.Combine(directory, "missing-cache.png"), Kind = MediaKind.Image };
        using (cache.GetOrBuild(missing)) { }
        Require(Directory.GetFiles(root, "*.png").Length == entries, "Missing-file fallback was persisted.");
        string notDirectory = Path.Combine(directory, "cache-is-a-file");
        File.WriteAllText(notDirectory, "not a directory");
        using (var image = (Bitmap)new ThumbnailDiskCache(notDirectory).GetOrBuild(file))
            Require(image.GetPixel(120, 75).G > 200, "An unwritable cache blocked source decoding.");
        Console.WriteLine("PASS: disk thumbnail reuse, source changes, corrupt cache, missing files and write failure");
    }

    private static void TestActivityThumbnailAvailability(string directory)
    {
        string path = Path.Combine(directory, "activity-missing.png");
        var file = new MediaFile { SourcePath = path, Kind = MediaKind.Image };
        var activity = new StageActivity { Name = "测试环节" };
        activity.Files.Add(file);
        var scenario = new Scenario { Name = "测试方案" };
        scenario.Activities.Add(activity);
        var store = new ScenarioStore { CurrentScenarioId = scenario.Id };
        store.Scenarios.Add(scenario);
        using var form = new Form { ClientSize = new Size(700, 480), ShowInTaskbar = false, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        using var panel = new ActivitiesPanel(store, new ScenarioRepository(Path.Combine(directory, "activity-scenarios.json")),
            new FileLibraryStore(Path.Combine(directory, "activity-library.json")), null,
            new EveryStage.Terminal.Logging.FileOperationLogger(directory), new OutputStateMachine());
        form.Controls.Add(panel);
        form.Show();
        var host = (IActivityRowHost)panel;
        host.ThumbnailFor(file);
        Require(WaitFor(() => host.IsUnavailable(file) == true, 5000), "Activity did not flag its missing file.");
        using (var image = new Bitmap(40, 30)) image.Save(path, ImageFormat.Png);
        Require(WaitFor(() => host.IsUnavailable(file) == false && host.ThumbnailFor(file) != null, 9000),
            "Activity did not automatically reload its restored visible file.");
        File.Delete(path);
        Require(WaitFor(() => host.IsUnavailable(file) == true, 9000), "Activity did not automatically flag a removed visible file.");
        using (var image = new Bitmap(80, 40))
        using (var graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Lime); image.Save(path, ImageFormat.Png); }
        Require(WaitFor(() => host.IsUnavailable(file) == false && host.ThumbnailFor(file) is Bitmap thumbnail
            && thumbnail.GetPixel(120, 75).G > 200, 9000), "Activity did not update its restored visible thumbnail.");
        File.Delete(path);
        panel.RefreshTree();
        host.ThumbnailFor(file);
        panel.RefreshTree(); // A pending result from the older refresh must not replace the new one.
        using (var image = new Bitmap(40, 30)) image.Save(path, ImageFormat.Png);
        host.ThumbnailFor(file);
        Require(WaitFor(() => host.IsUnavailable(file) == false && host.ThumbnailFor(file) != null, 5000),
            "Activity refresh published stale file availability.");
        Console.WriteLine("PASS: activity visible file changes, refresh recovery and stale-result rejection");
    }

    private static void TestPresenterContentOrdering()
    {
        var loader = new PresenterContentLoader();
        var delayed = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldBitmap = new Bitmap(3, 3);
        Task<Bitmap?> Image(int slide, int width) => slide == 2 ? delayed.Task : Task.FromResult<Bitmap?>(new Bitmap(4, 4));
        Task<string> Notes(int slide) => Task.FromResult($"notes-{slide}");
        var old = loader.LoadAsync(1, 0, Image, Notes);
        var latest = loader.LoadAsync(2, 0, Image, Notes);
        Require(WaitFor(() => latest.IsCompleted, 3000) && latest.Result is { Notes: "notes-2" },
            "Latest presenter request did not complete.");
        delayed.SetResult(oldBitmap);
        Require(WaitFor(() => old.IsCompleted, 3000) && old.Result == null, "An older presenter request was not discarded.");
        bool released = false;
        try { oldBitmap.GetPixel(0, 0); } catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException) { released = true; }
        Require(released, "Discarded presenter image was not disposed.");
        latest.Result!.Value.DisposeImages();
        var pending = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidatedBitmap = new Bitmap(2, 2);
        var invalidated = loader.LoadAsync(1, 0, (_, _) => pending.Task, Notes);
        loader.Invalidate();
        pending.SetResult(invalidatedBitmap);
        Require(WaitFor(() => invalidated.IsCompleted, 3000) && invalidated.Result == null,
            "Retiring a presenter did not invalidate pending data.");
        Console.WriteLine("PASS: presenter data rejects out-of-order and retired requests and releases discarded images");
    }

    private static void TestDocumentOutputDisposal(string directory)
    {
        string pdf = Path.Combine(directory, "closing-output.pdf");
        WriteColoredPdf(pdf, 2);
        foreach (bool closeOutput in new[] { true, false })
        {
            using var form = new Form { ShowInTaskbar = false, Opacity = 0 };
            using var surface = new PreviewSurface { Dock = DockStyle.Fill };
            form.Controls.Add(surface);
            form.Show();
            using var engine = new PlaybackEngine(PlaybackChannel.Preview, surface, null,
                new SettingsStore(Path.Combine(directory, $"closing-{closeOutput}.json")), new ScenarioStore(), null, directory);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var created = new TaskCompletionSource<PdfDocumentView>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool opened = false, latePlaying = false;
            engine.StateChanged += state => { if (state == PlaybackChannelState.Playing) latePlaying = true; };
            engine.OpenPdfView = async (path, interactive, initial) =>
            {
                opened = true;
                await gate.Task;
                var view = await PdfDocumentView.OpenAsync(path, interactive, initial);
                created.SetResult(view);
                return view;
            };
            engine.RequestPlay(new MediaFile { SourcePath = pdf, Kind = MediaKind.Document });
            Require(opened && engine.State == PlaybackChannelState.Loading, "Delayed PDF did not begin loading.");
            if (closeOutput) surface.Dispose(); else engine.Dispose();
            gate.SetResult();
            var disposedField = typeof(PdfDocumentView).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Require(WaitFor(() => created.Task.IsCompletedSuccessfully && (bool)disposedField.GetValue(created.Task.Result)!, 10000),
                $"PDF view was not released after {(closeOutput ? "output" : "engine")} disposal.");
            Require(!latePlaying && engine.PdfView == null, "A PDF published playback after its owner closed.");
        }
        using (var form = new Form { ShowInTaskbar = false, Opacity = 0 })
        using (var surface = new PreviewSurface { Dock = DockStyle.Fill })
        using (var engine = new PlaybackEngine(PlaybackChannel.Preview, surface, null,
            new SettingsStore(Path.Combine(directory, "closing-active.json")), new ScenarioStore(), null, directory))
        {
            form.Controls.Add(surface);
            form.Show();
            engine.RequestPlay(new MediaFile { SourcePath = pdf, Kind = MediaKind.Document });
            Require(WaitFor(() => engine.State == PlaybackChannelState.Playing && engine.PdfView != null, 10000),
                "Active PDF did not open before output disposal.");
            var view = engine.PdfView!;
            surface.Dispose();
            var disposedField = typeof(PdfDocumentView).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Require((bool)disposedField.GetValue(view)! && engine.PdfView == null, "An active PDF was not released when its output closed.");
        }
        Console.WriteLine("PASS: in-flight PDF view is released after output or engine disposal");
    }

    /// <summary>A field of one of the 监看 column's two windows (0 top, 1 bottom).</summary>
    private static T DockWindowField<T>(SharedPreviewDock dock, int window, string name)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var windows = (Array)typeof(SharedPreviewDock).GetField("_windows", flags)!.GetValue(dock)!;
        var item = windows.GetValue(window)!;
        return (T)item.GetType().GetField(name, flags)!.GetValue(item)!;
    }

    private static void TestHotkeyRegistration()
    {
        int failures = 0;
        using var first = new EmergencyStopHotkey(() => { }, _ => failures++);
        using var second = new EmergencyStopHotkey(() => { }, _ => failures++);
        Require(!first.Global && !second.Global, "Nothing should be global before Configure.");
        first.Configure(true);
        if (!first.Registered)
        {
            // F12 is reserved for debuggers and refused on many machines: the keyboard-hook fallback takes over.
            Require(first.HookActive || failures == 1, "Refused registration neither fell back to the hook nor was reported.");
            Console.WriteLine(first.HookActive
                ? "PASS: refused F12 registration falls back to the keyboard hook (still global, no failure shown)"
                : "SKIP: native hotkey ownership test (F12 refused and no hook; failure feedback verified)");
            return;
        }
        second.Configure(true);
        Require(!second.Registered && (second.HookActive || failures == 1), "A hotkey conflict neither fell back to the hook nor was reported.");
        first.Configure(false);
        second.Configure(false);
        second.Configure(true);
        Require(second.Registered && !second.HookActive, "Disabling the first registration did not release the hotkey.");
        Console.WriteLine("PASS: global emergency hotkey registers, falls back to the hook on conflict, releases and retries");
    }

    private static void TestAudioOutputSettings(string directory)
    {
        string path = Path.Combine(directory, "audio-ui-settings.json");
        var settings = new SettingsStore(path);
        var missing = settings.Current.Clone();
        missing.AudioOutputDeviceId = "missing-audio-device";
        settings.Save(missing);
        using var panel = new SettingsPanel(settings, new EveryStage.Discovery.DeviceIdentity { DeviceId = Guid.NewGuid(), DeviceName = "测试" });
        TestSettingsPanelThemeRoles(panel);
        var combo = (ComboBox)typeof(SettingsPanel).GetField("_audioOutputComboBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        Require(combo.SelectedItem?.ToString()?.Contains("不可用") == true && settings.Current.AudioOutputDeviceId == missing.AudioOutputDeviceId,
            "An unavailable selected audio device was silently discarded in settings.");
        combo.SelectedIndex = 0;
        Require(new SettingsStore(path).Current.AudioOutputDeviceId == null, "Selecting the system default did not persist immediately.");
        if (combo.Items.Count > 1 && !combo.Items[1]!.ToString()!.Contains("不可用"))
        {
            combo.SelectedIndex = 1;
            Require(!string.IsNullOrEmpty(new SettingsStore(path).Current.AudioOutputDeviceId), "Selecting an audio device did not persist immediately.");
        }
        Console.WriteLine("PASS: audio settings preserve unavailable selections and save device choices immediately");
    }

    private static void TestSettingsPanelThemeRoles(SettingsPanel panel)
    {
        var labels = Descendants(panel).OfType<Label>().ToArray();
        var about = labels.Single(label => label.Text.StartsWith("EveryStage 终端机 "));
        var slide = labels.Single(label => label.Text == "静态幻灯片");
        var heading = labels.Single(label => label.Text == "设置");
        var name = (TextBox)typeof(SettingsPanel).GetField("_deviceNameTextBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        var probe = ThemeManager.HighContrastProbe;
        var saved = ModernUi.CurrentTheme;
        try
        {
            ThemeManager.HighContrastProbe = () => false;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Tech, AppTheme.System })
            {
                about.ForeColor = slide.ForeColor = heading.ForeColor = name.ForeColor = Color.Magenta;
                name.BackColor = Color.Magenta;
                ThemeManager.Apply(panel, theme);
                var palette = UiPalette.For(theme);
                Require(about.ForeColor == palette.Muted && slide.ForeColor == palette.Text && heading.ForeColor == palette.Text
                    && name.ForeColor == palette.Text && name.BackColor == palette.Surface,
                    $"Settings labels or device name lost explicit colour roles in {theme}.");
            }
            ThemeManager.HighContrastProbe = () => true;
            ThemeManager.Apply(panel, AppTheme.Tech);
            Require(about.ForeColor == SystemColors.WindowText && slide.ForeColor == SystemColors.WindowText
                && heading.ForeColor == SystemColors.WindowText && name.ForeColor == SystemColors.WindowText
                && name.BackColor == SystemColors.Window, "Settings colours did not follow system high contrast.");
            Console.WriteLine("PASS: settings heading, about text, static-slide label and device-name field use explicit theme roles");
        }
        finally
        {
            ThemeManager.HighContrastProbe = probe;
            ThemeManager.Apply(panel, saved);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));
        public void Drain() { while (_pending.TryDequeue(out var item)) item.Callback(item.State); }
    }

    private static void TestDefaultAudioNotifications()
    {
        var ui = new QueuedContext();
        var changes = new List<string>();
        using var monitor = new EveryStage.Terminal.Audio.DefaultAudioDeviceMonitor(ui, changes.Add, register: false, initialId: "a");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console, "ignored");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia, "ignored");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "a");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "b");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "c");
        ui.Drain();
        Require(changes.SequenceEqual(new[] { "c" }), "Default audio notifications were not filtered or coalesced.");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, null!);
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "c");
        ui.Drain();
        Require(changes.SequenceEqual(new[] { "c", "c" }), "Returning after no default endpoint did not trigger a notification.");
        monitor.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console, "d");
        monitor.Dispose();
        ui.Drain();
        Require(changes.Count == 2, "A disposed monitor delivered a queued notification.");
        try
        {
            using var nativeMonitor = new EveryStage.Terminal.Audio.DefaultAudioDeviceMonitor(ui, _ => { });
            Console.WriteLine("PASS: native default audio notification registration and disposal");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Console.WriteLine($"SKIP: native default audio notification registration unavailable: {ex.Message}");
        }
        Console.WriteLine("PASS: default audio endpoint notifications filter role, coalesce and stop after disposal");
    }

    private static void TestThemeRoles()
    {
        var saved = ModernUi.CurrentTheme;
        var originalProbe = ThemeManager.HighContrastProbe;
        ThemeManager.HighContrastProbe = () => false;
        using var form = new Form();
        using var pairingDialog = new PairingConfirmationDialog(new EveryStage.Terminal.Devices.PairingRequest("test", Guid.NewGuid(), "测试设备", "127.0.0.1", "123456"));
        using var fadeDialog = new FadeDialog("淡入淡出", false, null);
        using var audioDialog = new AudioPropertiesDialog("音频", true, AudioVisual.Waveform);
        using var durationDialog = new StayDurationDialog("停留", null);
        using var documentDialog = new DocumentSettingsDialog(new MediaFile { SourcePath = "sample.pdf", Kind = MediaKind.Document });
        using var batchDialog = new BatchPropertiesDialog(new[] { new MediaFile { SourcePath = "sample.pdf", Kind = MediaKind.Document } });
        using var permissionsDialog = new EditPairedDevicePermissionsDialog(new EveryStage.Terminal.Devices.PairedDevice { DeviceId = Guid.NewGuid(), DeviceName = "测试设备" });
        var hintDialogs = new Form[] { audioDialog, durationDialog, documentDialog, batchDialog, permissionsDialog };
        string[] hintTexts = { "背景音轨会独立", "关闭时跟随", "连续滚动按", "只有勾选了", "监看功能本身" };
        var dialogHints = hintDialogs.Select((dialog, index) => Descendants(dialog).OfType<Label>()
            .Single(label => label.Text.Contains(hintTexts[index]))).ToArray();
        var pairingCode = pairingDialog.Controls.OfType<Label>().Single(label => label.Text == "123 456");
        var pairingHint = pairingDialog.Controls.OfType<Label>().Single(label => label.Text.Contains("监看功能本身"));
        var fadeWarning = fadeDialog.Controls.OfType<Label>().Single(label => label.Text.Contains("淡出依赖"));
        using var button = new Button();
        using var warningLabel = ModernUi.SetForegroundRole(new Label(), UiForegroundRole.Warning);
        using var accentLabel = ModernUi.SetForegroundRole(new Label(), UiForegroundRole.Accent);
        using var surfaceLabel = ModernUi.SetBackgroundRole(new Label(), UiBackgroundRole.Surface);
        var requestType = typeof(EveryStage.Terminal.UI.Panels.DevicesPanel).GetNestedType("RequestCard", System.Reflection.BindingFlags.NonPublic)!;
        using var requestCard = (Control)Activator.CreateInstance(requestType, nonPublic: true)!;
        requestCard.Size = new Size(400, 96);
        requestType.GetMethod("Show", new[] { typeof(string), typeof(string) })!.Invoke(requestCard, new object[] { "测试设备", "123 456" });
        using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using var infoToast = new ToastNotification("已上屏", ToastSeverity.Info, null, 300, 50);
        using var warningToast = new ToastNotification("提示", ToastSeverity.Warning, null, 300, 50);
        using var criticalToast = new ToastNotification("错误", ToastSeverity.Critical, null, 300, 50);
        static Panel ToastStripe(ToastNotification toast) => toast.Controls.OfType<Panel>().Single();
        combo.Items.Add("测试");
        combo.SelectedIndex = 0;
        ModernUi.StyleComboBox(combo);
        using var presenter = new PresenterStrip();
        using var dock = new SharedPreviewDock(new OutputStateMachine());
        var menuTemp = Path.Combine(Path.GetTempPath(), "EveryStage-menu-test-" + Guid.NewGuid());
        using var menuMusic = new MusicChannel(new MusicLibraryStore(Path.Combine(menuTemp, "music.json")));
        using var menuDrawer = new MusicDrawer(menuMusic, new FileLibraryStore(Path.Combine(menuTemp, "library.json")));
        form.Controls.Add(menuDrawer);
        using var musicMenu = menuDrawer.BuildLibraryMenu();
        var disabledMenuItem = (ToolStripMenuItem)musicMenu.Items[0];
        var enabledMenuItem = new ToolStripMenuItem("测试菜单") { ForeColor = Color.Magenta };
        musicMenu.Items.Add(enabledMenuItem);
        using var dockMenu = ModernUi.CreateThemedMenu(dock);
        var dockMenuItem = new ToolStripMenuItem("监看菜单") { ForeColor = Color.Magenta };
        dockMenu.Items.Add(dockMenuItem);
        static void OpenMenuForTest(ContextMenuStrip menu) => typeof(ToolStripDropDown)
            .GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
        static Color RenderMenuText(ContextMenuStrip menu, ToolStripItem item)
        {
            using var bitmap = new Bitmap(240, 32);
            using var graphics = Graphics.FromImage(bitmap);
            var args = new ToolStripItemTextRenderEventArgs(graphics, item, item.Text, new Rectangle(0, 0, 240, 32), Color.Magenta, item.Font,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            typeof(ThemedMenuRenderer).GetMethod("OnRenderItemText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(menu.Renderer, new object[] { args });
            return args.TextColor;
        }
        using var documentTips = new ToolTip();
        using var documentBar = new DocumentControlBar(null!, documentTips, 28);
        dock.Controls.Add(documentBar);
        typeof(DocumentControlBar).GetMethod("AddPageBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(documentBar, null);
        var pageBox = documentBar.Controls.OfType<TextBox>().Single();
        var pageCount = documentBar.Controls.OfType<Label>().Single();
        var notes = (Label)typeof(PresenterStrip).GetField("_notes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(presenter)!;
        var setNotes = typeof(PresenterStrip).GetMethod("SetNotes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setNotes.Invoke(presenter, new object[] { "演讲备注" });
        presenter.RefreshTheme();
        Require(notes.ForeColor == UiPalette.Normal(AppTheme.Dark).Text, "Presenter notes lost their normal text role.");
        setNotes.Invoke(presenter, new object[] { "" });
        var showField = typeof(PresenterStrip).GetField("_show", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var versionField = typeof(PresenterStrip).GetField("_version", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var nextSlide = (PictureBox)typeof(PresenterStrip).GetField("_next", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(presenter)!;
        showField.SetValue(presenter, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(showField.FieldType));
        nextSlide.Image = new Bitmap(2, 2);
        presenter.Update(null);
        Require((int)versionField.GetValue(presenter)! == 1 && nextSlide.Image == null && notes.Text == "",
            "Ending a show did not invalidate pending presenter data and clear the previous slide.");
        setNotes.Invoke(presenter, new object[] { "" });
        var take = (Button)typeof(SharedPreviewDock).GetField("_takeButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dock)!;
        var monitorFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        // Two identical 监看 windows (captions) and the big-screen status at the top of the column.
        var previewStatus = DockWindowField<Label>(dock, 0, "_caption");
        var programStatus = DockWindowField<Label>(dock, 1, "_caption");
        var localStatus = (Label)typeof(SharedPreviewDock).GetField("_status", monitorFlags)!.GetValue(dock)!;
        var deviceCard = typeof(SharedPreviewDock).GetField("_deviceCard", monitorFlags)!.GetValue(dock)!;
        var deviceFrame = (Control)deviceCard.GetType().GetField("_videoFrame", monitorFlags)!.GetValue(deviceCard)!;
        var deviceCaption = (Label)deviceCard.GetType().GetField("_caption", monitorFlags)!.GetValue(deviceCard)!;
        var errorCard = DockWindowField<Control>(dock, 0, "_error");
        var errorTitle = (Label)errorCard.GetType().GetField("_title", monitorFlags)!.GetValue(errorCard)!;
        var errorDetail = (Label)errorCard.GetType().GetField("_detail", monitorFlags)!.GetValue(errorCard)!;
        var errorSuggestion = (Label)errorCard.GetType().GetField("_suggestion", monitorFlags)!.GetValue(errorCard)!;
        ModernUi.StyleButton(button, primary: true);
        form.Controls.Add(button);
        form.Controls.Add(warningLabel);
        form.Controls.Add(accentLabel);
        form.Controls.Add(surfaceLabel);
        form.Controls.Add(requestCard);
        form.Controls.Add(combo);
        form.Controls.Add(infoToast);
        form.Controls.Add(warningToast);
        form.Controls.Add(criticalToast);
        form.Controls.Add(presenter);
        form.Controls.Add(dock);
        var runControls = new Panel();
        var runLabel = new Label { Text = "下一项" };
        var runButton = new Button { Text = "播放" };
        ModernUi.StyleButton(runButton, primary: true);
        runControls.Controls.AddRange(new Control[] { runLabel, runButton });
        dock.SetRunControls(runControls);
        var sampleFile = new MediaFile { SourcePath = "sample.pdf", Kind = MediaKind.Document };
        var sampleActivity = new StageActivity { Name = "演示" };
        sampleActivity.Files.Add(sampleFile);
        var activityNode = new TreeNode { Tag = sampleActivity };
        var fileNode = new TreeNode { Tag = sampleFile };
        activityNode.Nodes.Add(fileNode);
        var rowHost = new TestActivityRowHost { State = RunRowState.Next, SelectedNode = fileNode, PreviewFile = sampleFile };
        using var row = new ActivityRowView(rowHost, sampleActivity, activityNode, 1);
        row.FitToWidth(720);
        form.Controls.Add(row);
        ThemeManager.Apply(form, AppTheme.Dark);
        using var secondForm = new Form();
        using var legacyLabel = new Label { ForeColor = UiPalette.Normal(AppTheme.Dark).Muted };
        secondForm.Controls.Add(legacyLabel);
        ThemeManager.Apply(secondForm, AppTheme.Dark);
        var monitorMenuColors = new EveryStage.Terminal.UI.Panels.FilesPanel.FileMenuColorTable(() => ModernUi.PaletteFor(dock));
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Tech, AppTheme.System, AppTheme.Dark })
            {
                warningLabel.ForeColor = Color.Magenta; // role must win even when the old colour is unknown.
                surfaceLabel.BackColor = Color.Magenta;
                ThemeManager.Apply(form, theme);
                ThemeManager.Apply(secondForm, theme);
                ThemeManager.Apply(pairingDialog, theme);
                ThemeManager.Apply(fadeDialog, theme);
                foreach (var hint in dialogHints) hint.ForeColor = Color.Magenta;
                foreach (var dialog in hintDialogs) ThemeManager.Apply(dialog, theme);
                var palette = UiPalette.For(theme);
                Require(dialogHints.All(hint => hint.ForeColor == palette.Muted), $"Dialog help text lost its explicit role under {theme}.");
                OpenMenuForTest(musicMenu);
                OpenMenuForTest(dockMenu);
                Require(musicMenu.Renderer is ThemedMenuRenderer && musicMenu.BackColor == palette.Surface
                    && RenderMenuText(musicMenu, enabledMenuItem) == palette.Text
                    && RenderMenuText(musicMenu, disabledMenuItem) == palette.Muted,
                    $"Music library menu did not use enabled/disabled theme roles under {theme}.");
                Require(pairingCode.ForeColor == palette.Accent && pairingHint.ForeColor == palette.Muted && fadeWarning.ForeColor == palette.Danger,
                    $"Dialog verification code, hint, or warning lost its role under {theme}.");
                Require(legacyLabel.ForeColor == palette.Muted, $"A second window kept a stale foreground under {theme}.");
                Require(warningLabel.ForeColor == palette.Warning && accentLabel.ForeColor == palette.Accent,
                    $"Explicit text roles did not follow {theme}.");
                Require(infoToast.BackColor == palette.SurfaceRaised && ToastStripe(infoToast).BackColor == palette.Accent
                    && ToastStripe(warningToast).BackColor == palette.Warning && ToastStripe(criticalToast).BackColor == palette.Danger,
                    $"Open notification colours did not follow {theme}.");
                Require(surfaceLabel.BackColor == palette.Surface, $"Explicit background role did not follow {theme}.");
                Require(button.BackColor == palette.AccentFill && button.ForeColor == Color.White, "Primary role drifted during theme switching.");
                double Channel(byte value) { double c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                double L(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
                Require(1.05 / (L(palette.AccentFill) + 0.05) >= 4.5 && 1.05 / (L(palette.DangerFill) + 0.05) >= 4.5,
                    $"White button text contrast below 4.5:1 in {theme}.");
                Require(palette.Preview != palette.ProgramBorder && palette.ProgramBorder != palette.Accent, "Output roles share a colour.");
                Require(presenter.BackColor == UiPalette.Normal(AppTheme.Dark).Background, "Presenter chrome changed with the app theme.");
                var monitorPalette = UiPalette.Normal(AppTheme.Dark);
                foreach (var mediaSurface in new Control[] { dock.PreviewSurface, dock.PreviewSurface.VideoHost, dock.PreviewSurface.ContentSurface, deviceFrame, dock.DeviceVideoHost })
                {
                    mediaSurface.BackColor = Color.Magenta;
                    Require(ModernUi.ApplyBackgroundRole(mediaSurface, monitorPalette) && mediaSurface.BackColor == Color.Black,
                        $"Monitor media background lost its black role under {theme}.");
                }
                errorCard.BackColor = deviceCaption.BackColor = Color.Magenta;
                errorTitle.ForeColor = errorDetail.ForeColor = errorSuggestion.ForeColor = deviceCaption.ForeColor = Color.Magenta;
                Require(ModernUi.ApplyBackgroundRole(errorCard, monitorPalette) && errorCard.BackColor == monitorPalette.Surface
                    && ModernUi.ApplyBackgroundRole(deviceCaption, monitorPalette) && deviceCaption.BackColor == monitorPalette.SurfaceRaised
                    && ModernUi.ApplyForegroundRole(errorTitle, monitorPalette) && errorTitle.ForeColor == monitorPalette.Danger
                    && ModernUi.ApplyForegroundRole(errorDetail, monitorPalette) && errorDetail.ForeColor == monitorPalette.Text
                    && ModernUi.ApplyForegroundRole(errorSuggestion, monitorPalette) && errorSuggestion.ForeColor == monitorPalette.Muted
                    && ModernUi.ApplyForegroundRole(deviceCaption, monitorPalette) && deviceCaption.ForeColor == monitorPalette.Text,
                    $"Monitor error or device caption lost explicit roles under {theme}.");
                foreach (var monitorStatus in new[] { previewStatus, programStatus, localStatus })
                {
                    monitorStatus.ForeColor = Color.Magenta;
                    Require(ModernUi.ApplyForegroundRole(monitorStatus, monitorPalette) && monitorStatus.ForeColor == monitorPalette.Muted,
                        $"Monitor idle status did not retain its explicit local-palette role under {theme}.");
                }
                Require(dockMenu.BackColor == monitorPalette.Surface && RenderMenuText(dockMenu, dockMenuItem) == monitorPalette.Text,
                    "A monitor menu followed the page theme instead of its local dark palette.");
                Require(pageBox.BackColor == monitorPalette.Surface && pageBox.ForeColor == monitorPalette.Text
                    && pageCount.ForeColor == monitorPalette.Muted && pageBox.AccessibleName == "页码 / 幻灯片序号",
                    $"Document page controls did not retain monitor roles under {theme}.");
                Require(notes.ForeColor == monitorPalette.Muted, "Empty presenter notes lost their muted role after theme switching.");
                Require(monitorMenuColors.ToolStripDropDownBackground == monitorPalette.Surface,
                    "Monitor menu followed the page palette instead of the monitor palette.");
                Require(dock.BackColor == monitorPalette.Rail && take.BackColor == monitorPalette.AccentFill && take.ForeColor == monitorPalette.AccentFillForeground,
                    $"Monitor chrome did not keep its dark palette under {theme}.");
                Require(runLabel.ForeColor == monitorPalette.Text && runButton.BackColor == monitorPalette.AccentFill,
                    $"Dynamically attached run controls did not use monitor colours under {theme}.");
                Require(dock.PreviewSurface.ContentSurface.BackColor == Color.Black && dock.PreviewSurface.VideoHost.BackColor == Color.Black,
                    "Theme switching changed a media surface away from black.");
            }
            ThemeManager.HighContrastProbe = () => true;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Tech, AppTheme.System })
            {
                ThemeManager.Apply(form, theme);
                ThemeManager.Apply(pairingDialog, theme);
                ThemeManager.Apply(fadeDialog, theme);
                foreach (var dialog in hintDialogs) ThemeManager.Apply(dialog, theme);
                Require(dialogHints.All(hint => hint.ForeColor == SystemColors.WindowText), "Dialog help text did not follow high contrast.");
                OpenMenuForTest(musicMenu);
                Require(musicMenu.BackColor == SystemColors.Window
                    && RenderMenuText(musicMenu, enabledMenuItem) == SystemColors.WindowText
                    && RenderMenuText(musicMenu, disabledMenuItem) == SystemColors.GrayText,
                    "Music library menu did not use high-contrast text and background roles.");
                Require(pairingCode.ForeColor == SystemColors.WindowText && pairingHint.ForeColor == SystemColors.WindowText
                    && fadeWarning.ForeColor == SystemColors.WindowText,
                    "Dialog text did not yield to system high contrast.");
                Require(ModernUi.Palette.IsHighContrast && ModernUi.Background == SystemColors.Window,
                    "System high contrast did not override a configured theme.");
                var contrastPalette = UiPalette.For(AppTheme.Dark);
                Require(ModernUi.ApplyBackgroundRole(errorCard, contrastPalette) && errorCard.BackColor == SystemColors.Window
                    && ModernUi.ApplyForegroundRole(errorTitle, contrastPalette) && errorTitle.ForeColor == SystemColors.WindowText
                    && ModernUi.ApplyBackgroundRole(deviceFrame, contrastPalette) && deviceFrame.BackColor == Color.Black,
                    "Monitor error or media background roles did not respect high contrast.");
                foreach (var monitorStatus in new[] { previewStatus, programStatus, localStatus })
                {
                    monitorStatus.ForeColor = Color.Magenta;
                    Require(ModernUi.ApplyForegroundRole(monitorStatus, UiPalette.For(AppTheme.Dark))
                        && monitorStatus.ForeColor == SystemColors.WindowText, "Monitor status did not keep its high-contrast role.");
                }
                Require(warningLabel.ForeColor == SystemColors.WindowText && accentLabel.ForeColor == SystemColors.WindowText
                    && monitorMenuColors.ToolStripDropDownBackground == SystemColors.Window
                    && monitorMenuColors.MenuItemSelected == SystemColors.Highlight
                    && monitorMenuColors.MenuBorder == SystemColors.WindowText,
                    "Text or monitor menu roles did not yield to high contrast.");
                Require(surfaceLabel.BackColor == SystemColors.Window, "Explicit background role did not yield to high contrast.");
                Require(infoToast.BackColor == SystemColors.Window && ToastStripe(infoToast).BackColor == SystemColors.WindowText
                    && ToastStripe(warningToast).BackColor == SystemColors.WindowText && ToastStripe(criticalToast).BackColor == SystemColors.WindowText,
                    "Open notification colours did not yield to high contrast.");
                using (var requestImage = new Bitmap(requestCard.Width, requestCard.Height))
                {
                    requestCard.DrawToBitmap(requestImage, requestCard.ClientRectangle);
                    Require(requestImage.GetPixel(200, 90).ToArgb() == SystemColors.Window.ToArgb(),
                        "Pairing request card used a blended colour in high contrast.");
                }
                using (var itemImage = new Bitmap(200, 26))
                using (var graphics = Graphics.FromImage(itemImage))
                {
                    var draw = new DrawItemEventArgs(graphics, combo.Font, new Rectangle(0, 0, 200, 26), 0, DrawItemState.Selected);
                    typeof(ComboBox).GetMethod("OnDrawItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(combo, new object[] { draw });
                    Require(itemImage.GetPixel(190, 4).ToArgb() == SystemColors.Highlight.ToArgb(),
                        "Selected combo item did not use the system highlight background.");
                }
                Require(button.BackColor == SystemColors.Highlight && button.ForeColor == SystemColors.HighlightText,
                    "Button did not use the system high-contrast pair.");
                Require(presenter.BackColor == SystemColors.Window, "Fixed-dark presenter blocked system high contrast.");
                Require(pageBox.BackColor == SystemColors.Window && pageBox.ForeColor == SystemColors.WindowText
                    && pageCount.ForeColor == SystemColors.WindowText && notes.ForeColor == SystemColors.WindowText,
                    "Document page controls or empty presenter notes did not use high-contrast roles.");
                Require(dock.BackColor == SystemColors.Control && take.BackColor == SystemColors.Highlight && take.ForeColor == SystemColors.HighlightText,
                    "Monitor did not yield to the system high-contrast palette.");
                Require(runLabel.ForeColor == SystemColors.WindowText && runButton.BackColor == SystemColors.Highlight,
                    "Dynamically attached run controls did not use high-contrast colours.");
                Require(dock.PreviewSurface.ContentSurface.BackColor == Color.Black, "High contrast changed the media output's black background.");
                using (var selectedRow = Capture(row))
                {
                    var columns = ActivityRowView.ColumnsFor(row.Width, value => row.LogicalToDeviceUnits(value));
                    int rowTop = row.LogicalToDeviceUnits(44);
                    Require(selectedRow.GetPixel(columns.Thumb + row.LogicalToDeviceUnits(38), rowTop + row.LogicalToDeviceUnits(11)).ToArgb() == SystemColors.Window.ToArgb(),
                        "Empty activity thumbnail used a mixed colour in high contrast.");
                    Require(selectedRow.GetPixel(row.Width - row.LogicalToDeviceUnits(50), rowTop + row.LogicalToDeviceUnits(7)).ToArgb() == SystemColors.Window.ToArgb(),
                        "Activity row used a mixed background in high contrast.");
                    rowHost.SelectedNode = null;
                    using var unselectedRow = Capture(row);
                    bool selectionVisible = false;
                    int y = rowTop + row.LogicalToDeviceUnits(4);
                    for (int x = row.LogicalToDeviceUnits(12); x < row.Width - row.LogicalToDeviceUnits(12); x++)
                        if (selectedRow.GetPixel(x, y).ToArgb() != unselectedRow.GetPixel(x, y).ToArgb()) { selectionVisible = true; break; }
                    Require(selectionVisible, "Selected activity row was indistinguishable from an unselected row.");
                    rowHost.PreviewFile = null;
                    using (var noPreview = Capture(row))
                    {
                        bool previewVisible = false;
                        for (int py = rowTop + row.LogicalToDeviceUnits(9); py < rowTop + row.LogicalToDeviceUnits(23) && !previewVisible; py++)
                            for (int px = columns.Thumb + row.LogicalToDeviceUnits(4); px < columns.Thumb + row.LogicalToDeviceUnits(32); px++)
                                if (unselectedRow.GetPixel(px, py).ToArgb() != noPreview.GetPixel(px, py).ToArgb()) { previewVisible = true; break; }
                        Require(previewVisible, "Activity preview marker was not visible in high contrast.");
                        rowHost.State = RunRowState.Live;
                        using var liveRow = Capture(row);
                        bool stateBorderDiffers = false;
                        for (int by = rowTop; by <= rowTop + row.LogicalToDeviceUnits(2) && !stateBorderDiffers; by++)
                            for (int bx = row.LogicalToDeviceUnits(20); bx < row.Width - row.LogicalToDeviceUnits(20); bx++)
                                if (liveRow.GetPixel(bx, by).ToArgb() != noPreview.GetPixel(bx, by).ToArgb()) { stateBorderDiffers = true; break; }
                        Require(stateBorderDiffers, "Live and next activity rows had indistinguishable borders in high contrast.");
                    }
                    rowHost.State = RunRowState.Pending;
                    using (var pendingRow = Capture(row))
                        Require(pendingRow.GetPixel(row.Width - row.LogicalToDeviceUnits(50), rowTop + row.LogicalToDeviceUnits(7)).ToArgb() == SystemColors.Window.ToArgb(),
                            "Pending activity row did not use the system background.");
                    rowHost.State = RunRowState.Next;
                    rowHost.PreviewFile = sampleFile;
                    rowHost.SelectedNode = fileNode;
                }
            }
            using var contrastFadeDialog = new FadeDialog("淡入淡出", false, null);
            var contrastWarning = contrastFadeDialog.Controls.OfType<Label>().Single(label => label.Text.Contains("淡出依赖"));
            var contrastPlainLabel = contrastFadeDialog.Controls.OfType<Label>().Single(label => label.Text == "秒");
            ThemeManager.HighContrastProbe = () => false;
            ThemeManager.Apply(form, AppTheme.Tech);
            ThemeManager.Apply(contrastFadeDialog, AppTheme.Tech);
            Require(contrastWarning.ForeColor == UiPalette.Normal(AppTheme.Tech).Danger
                && contrastPlainLabel.ForeColor == UiPalette.Normal(AppTheme.Tech).Text,
                "A dialog created in high contrast could not restore distinct warning and ordinary text roles.");
            Require(!ModernUi.Palette.IsHighContrast && button.BackColor == UiPalette.Normal(AppTheme.Tech).AccentFill,
                "Leaving high contrast did not restore the configured theme.");
            Require(dock.BackColor == UiPalette.Normal(AppTheme.Dark).Rail && take.BackColor == UiPalette.Normal(AppTheme.Dark).AccentFill,
                "Monitor did not restore its dark palette after high contrast.");
            using (var modalOwner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Opacity = 0 })
            {
                modalOwner.Show();
                ThemeManager.Apply(modalOwner, AppTheme.Dark);
                using var modal = new PairingConfirmationDialog(new EveryStage.Terminal.Devices.PairingRequest("modal", Guid.NewGuid(), "测试", "127.0.0.1", "123456"));
                var modalCode = modal.Controls.OfType<Label>().Single(label => label.Text == "123 456");
                var modalHint = modal.Controls.OfType<Label>().Single(label => label.Text.Contains("监看功能本身"));
                Exception? modalFailure = null;
                using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        ThemeManager.Apply(modalOwner, AppTheme.Light);
                        var light = UiPalette.Normal(AppTheme.Light);
                        Require(modalCode.ForeColor == light.Accent && modalHint.ForeColor == light.Muted && modal.BackColor == light.Background,
                            "An open modal dialog did not follow its owner's theme.");
                        ThemeManager.HighContrastProbe = () => true;
                        ThemeManager.Apply(modalOwner, AppTheme.Light);
                        Require(modalCode.ForeColor == SystemColors.WindowText && modal.BackColor == SystemColors.Window,
                            "An open modal dialog did not follow its owner into high contrast.");
                        ThemeManager.HighContrastProbe = () => false;
                        ThemeManager.Apply(modalOwner, AppTheme.Tech);
                        Require(modalCode.ForeColor == UiPalette.Normal(AppTheme.Tech).Accent && ModernUi.CurrentTheme == AppTheme.Tech,
                            "An open modal dialog did not restore its theme or changed the global theme.");
                    }
                    catch (Exception ex) { modalFailure = ex; }
                    finally { ThemeManager.HighContrastProbe = () => false; modal.Close(); }
                };
                timer.Start();
                modal.ShowDialog(modalOwner);
                if (modalFailure != null) throw new InvalidOperationException("Modal theme propagation failed.", modalFailure);
            }
            Console.WriteLine("PASS: role-based primary buttons survive theme switches; primary/danger fill white-text contrast >=4.5:1");
            Console.WriteLine("PASS: simulated system high contrast overrides every app theme and restores; presenter stays dark outside high contrast");
        }
        finally { ThemeManager.HighContrastProbe = originalProbe; ThemeManager.Apply(form, saved); }
    }

    private sealed class TestActivityRowHost : IActivityRowHost
    {
        public TreeNode? SelectedNode { get; set; }
        public RunRowState State { get; set; }
        public MediaFile? PreviewFile { get; set; }
        public bool IsSelected(TreeNode node) => ReferenceEquals(node, SelectedNode);
        public bool IsMultiSelected(TreeNode node) => false;
        public RunRowState StateOf(MediaFile file) => State;
        public string? MusicCueText(StageActivity activity) => null;
        public Image? ThumbnailFor(MediaFile file) => null;
        public bool? IsUnavailable(MediaFile file) => null;
        public PreShowProblem? PreShowProblemOf(MediaFile file) => null;
        public (FilesPanel.CardProgress Progress, bool OnProgram)? ProgressFor(MediaFile file) => null;
        public void RowNodeClicked(TreeNode node) { }
        public void RowNodeDoubleClicked(TreeNode node) { }
        public void RowContextMenu(TreeNode node, Control source, Point location) { }
        public void ToggleCollapsed(StageActivity activity) { }
        public void MoveFile(StageActivity activity, int from, int insertAt) { }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

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
        var outputState = new OutputStateMachine();
        using var window = new MainWindow(outputState, null, new FileLibraryStore(Path.Combine(dir, "library.json")),
            new EveryStage.Terminal.Devices.PairedDeviceStore(Path.Combine(dir, "paired.json")), new ScenarioStore(),
            new ScenarioRepository(Path.Combine(dir, "scenarios.json")), new SettingsStore(Path.Combine(dir, "settings.json")),
            EveryStage.Discovery.DeviceIdentity.LoadOrCreate("selftest", Path.Combine(dir, "identity.json")),
            new EveryStage.Terminal.Logging.DeviceConnectionLogger(dir), new MusicLibraryStore(Path.Combine(dir, "music.json")),
            new EveryStage.Terminal.Logging.FileOperationLogger(dir))
        { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), FirstRunGuideEnabled = false };
        window.Show();
        Pump(400);
        window.Size = new Size(1000, 760);
        Pump(200);
        // Emergency output stop must work even while a text box has keyboard focus.
        using (var text = new TextBox())
        {
            string imagePath = Path.Combine(dir, "emergency-auto.png");
            using (var image = new Bitmap(16, 16)) image.Save(imagePath, ImageFormat.Png);
            var preview = (PlaybackEngine)typeof(MainWindow).GetField("_preview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            preview.RequestPlay(new MediaFile
            {
                SourcePath = imagePath, Kind = MediaKind.Image, StayDuration = TimeSpan.FromSeconds(1),
                OnCompletion = CompletionAction.Loop,
            });
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 3000), "Emergency fixture did not start.");
            window.Controls.Add(text);
            text.Focus();
            outputState.AcceptDeviceCastRequest();
            var command = typeof(MainWindow).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            object[] arguments = { new Message(), Keys.Escape };
            Require((bool)command.Invoke(window, arguments)! && outputState.State == OutputState.Idle,
                "Esc (急停 本程序内) did not stop output with a text box focused.");
            Pump(1200);
            Require(outputState.State == OutputState.Idle && preview.State == PlaybackChannelState.Idle && preview.CurrentFile == null,
                "An automatic timer restarted playback after emergency stop.");
            window.Controls.Remove(text);
        }
        Console.WriteLine("PASS: Esc (急停 本程序内) stops output with text-box focus and leaves it idle");
        var hotkey = (EmergencyStopHotkey)typeof(MainWindow).GetField("_emergencyHotkey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        hotkey.Configure(true);
        if (hotkey.Registered)
        {
            outputState.AcceptDeviceCastRequest();
            using var dialog = new Form { ShowInTaskbar = false, Opacity = 0 };
            using var closeDialog = new System.Windows.Forms.Timer { Interval = 250 };
            closeDialog.Tick += (_, _) => dialog.Close();
            dialog.Shown += (_, _) =>
            {
                Require(PostMessage(hotkey.Handle, 0x312, new IntPtr(0x4553), IntPtr.Zero), "Could not queue the hotkey message.");
                closeDialog.Start();
            };
            dialog.ShowDialog(window);
            hotkey.Configure(false);
            Require(outputState.State == OutputState.Idle, "Delivered hotkey did not stop output inside a modal loop.");
            Console.WriteLine("PASS: delivered WM_HOTKEY stops output while the owner is disabled by a modal dialog (physical key not simulated)");
        }
        using var feedbackOverlay = new OverlayWindow(new MonitorInfo("SELFTEST", new Rectangle(0, 0, 480, 270), false));
        using var feedbackProgram = new PlaybackEngine(PlaybackChannel.Program, feedbackOverlay, null,
            new SettingsStore(Path.Combine(dir, "feedback-settings.json")), new ScenarioStore(), outputState, dir);
        window.AttachPlaybackEngine(feedbackProgram);
        var lastFeedback = typeof(MainWindow).GetField("_lastOnAirFile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        feedbackProgram.RequestPlay(new MediaFile { SourcePath = Path.Combine(dir, "missing.png"), Kind = MediaKind.Image });
        Require(WaitFor(() => feedbackProgram.State == PlaybackChannelState.Failed, 3000) && lastFeedback.GetValue(window) == null,
            "Failed output generated a success notification.");
        var statusDock = (SharedPreviewDock)typeof(MainWindow).GetField("_previewDock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var programStatusLabel = (Label)typeof(SharedPreviewDock).GetField("_status", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(statusDock)!;
        typeof(SharedPreviewDock).GetMethod("RefreshNow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(statusDock, null);
        programStatusLabel.ForeColor = Color.Magenta;
        Require(ModernUi.ApplyForegroundRole(programStatusLabel, UiPalette.For(AppTheme.Dark))
            && programStatusLabel.ForeColor == UiPalette.For(AppTheme.Dark).Danger, "Failed program did not retain its danger text role.");
        string readyImage = Path.Combine(dir, "onair.png");
        using (var bitmap = new Bitmap(32, 24)) bitmap.Save(readyImage, ImageFormat.Png);
        var readyFile = new MediaFile { SourcePath = readyImage, Kind = MediaKind.Image };
        feedbackProgram.RequestPlay(readyFile);
        Require(WaitFor(() => ReferenceEquals(lastFeedback.GetValue(window), readyFile), 3000), "Ready Program output did not generate feedback.");
        typeof(SharedPreviewDock).GetMethod("RefreshNow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(statusDock, null);
        programStatusLabel.ForeColor = Color.Magenta;
        Require(ModernUi.ApplyForegroundRole(programStatusLabel, UiPalette.For(AppTheme.Dark))
            && programStatusLabel.ForeColor == UiPalette.For(AppTheme.Dark).Danger,
            $"Live program status is not shown in the on-air (danger) role (output={outputState.State}, source={outputState.Source}, engine={feedbackProgram.State}, label={programStatusLabel.Text}).");
        var dock = (SharedPreviewDock)typeof(MainWindow).GetField("_previewDock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        Require(WaitFor(() => typeof(SharedPreviewDock).GetField("_flashingCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dock) == null, 2000),
            "On-air flash did not finish.");
        Console.WriteLine("PASS: failed load never announces success; ready Program generates feedback; on-air flash expires");
        string progressPdf = Path.Combine(dir, "progress.pdf");
        WriteColoredPdf(progressPdf, 3);
        var progressFile = new MediaFile { SourcePath = progressPdf, Kind = MediaKind.Document };
        var previewEngine = (PlaybackEngine)typeof(MainWindow).GetField("_preview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var errorCard = DockWindowField<Control>(statusDock, 0, "_error");
        int ownerThread = Environment.CurrentManagedThreadId, failedThread = -1, playingThread = -1;
        previewEngine.StateChanged += state =>
        {
            if (state == PlaybackChannelState.Failed) failedThread = Environment.CurrentManagedThreadId;
            if (state == PlaybackChannelState.Playing) playingThread = Environment.CurrentManagedThreadId;
        };
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            previewEngine.RequestPlay(new MediaFile { SourcePath = readyImage, Kind = MediaKind.Image });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Require(WaitFor(() => previewEngine.State == PlaybackChannelState.Playing, 5000) && playingThread == ownerThread,
            "Image completion did not return to the UI thread without a synchronization context.");
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            previewEngine.RequestPlay(new MediaFile { SourcePath = readyImage, Kind = MediaKind.Image });
            Thread.Sleep(150); // Allow decoding to finish while UI dispatch stays queued.
            previewEngine.Stop();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Pump(250);
        Require(previewEngine.State == PlaybackChannelState.Idle && previewEngine.CurrentFile == null,
            "A queued image completion restarted playback after stop.");
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            previewEngine.RequestPlay(new MediaFile { SourcePath = Path.Combine(dir, "missing-preview.png"), Kind = MediaKind.Image });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Require(WaitFor(() => previewEngine.State == PlaybackChannelState.Failed && errorCard.Parent == statusDock.PreviewSurface, 5000)
            && failedThread == ownerThread, "Image failure or the error card did not return to the UI thread.");
        Require(errorCard.Parent == statusDock.PreviewSurface,
            $"Failed preview did not show the error card (state={previewEngine.State}, error={previewEngine.LastError != null}, parent={errorCard.Parent?.GetType().Name ?? "null"}).");
        statusDock.PreviewSurface.Controls.Remove(errorCard);
        statusDock.PreviewSurface.SetOverlayCard(errorCard);
        Require(errorCard.Parent == statusDock.PreviewSurface, "A detached error card could not be restored on the next update.");
        var errorTitle = (Label)errorCard.GetType().GetField("_title", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(errorCard)!;
        errorTitle.ForeColor = Color.Magenta;
        Require(ModernUi.ApplyForegroundRole(errorTitle, UiPalette.For(AppTheme.Dark)) && errorTitle.ForeColor == UiPalette.For(AppTheme.Dark).Danger,
            "Visible preview error did not retain its danger role.");
        string corruptPdf = Path.Combine(dir, "corrupt-progress.pdf");
        File.WriteAllText(corruptPdf, "not a PDF");
        failedThread = -1;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            previewEngine.RequestPlay(new MediaFile { SourcePath = corruptPdf, Kind = MediaKind.Document });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Require(WaitFor(() => previewEngine.State == PlaybackChannelState.Failed && errorCard.Parent == statusDock.PreviewSurface, 5000)
            && failedThread == ownerThread, "PDF failure did not return to the UI thread without a synchronization context.");
        playingThread = -1;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            previewEngine.RequestPlay(progressFile);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Require(WaitFor(() => previewEngine.State == PlaybackChannelState.Playing && previewEngine.PdfView != null, 10000),
            "Progress PDF did not open in preview.");
        Require(playingThread == ownerThread && ReferenceEquals(SynchronizationContext.Current, previousContext),
            "PDF completion used the wrong thread or changed the caller's synchronization context.");
        Require(errorCard.Parent == null, "Preview error card remained visible after successful playback.");
        Require(FilesPanel.CardPlaybackProgress(progressFile, previewEngine)?.PageLabel == "1/3", "File card did not report the first PDF page.");
        var activityHost = (IActivityRowHost)typeof(MainWindow).GetField("_activitiesPanel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        Require(activityHost.ProgressFor(progressFile) is { OnProgram: false, Progress.PageLabel: "1/3" },
            "Activity row did not report the preview PDF page.");
        Require(activityHost.ProgressFor(progressFile.Clone()) == null, "Another activity item sharing the same path showed preview progress.");
        previewEngine.PdfView!.GoToPage(2);
        Require(FilesPanel.CardPlaybackProgress(progressFile, previewEngine)?.PageLabel == "2/3", "File card did not follow PDF page navigation.");
        Require(activityHost.ProgressFor(progressFile) is { OnProgram: false, Progress.PageLabel: "2/3" },
            "Activity row did not follow preview PDF navigation.");
        feedbackProgram.RequestPlay(progressFile);
        Require(WaitFor(() => feedbackProgram.State == PlaybackChannelState.Playing && feedbackProgram.PdfView != null, 10000),
            "Progress PDF did not open on program.");
        Require(activityHost.ProgressFor(progressFile) is { OnProgram: true, Progress.PageLabel: "1/3" },
            "Activity row did not prefer the live program page to the preview page.");
        feedbackProgram.PdfView!.GoToPage(3);
        Require(activityHost.ProgressFor(progressFile) is { OnProgram: true, Progress.PageLabel: "3/3" },
            "Activity row did not follow the program PDF page.");
        feedbackProgram.Stop();
        Require(activityHost.ProgressFor(progressFile) is { OnProgram: false, Progress.PageLabel: "2/3" },
            "Activity row did not return to preview progress after program stopped.");
        Require(FilesPanel.CardPlaybackProgress(new MediaFile { SourcePath = Path.Combine(dir, "other.pdf"), Kind = MediaKind.Document }, previewEngine) == null,
            "File card showed another file's playback position.");
        previewEngine.Stop();
        Require(FilesPanel.CardPlaybackProgress(progressFile, previewEngine) == null, "Stopped playback retained file card progress.");
        Require(activityHost.ProgressFor(progressFile) == null, "Stopped playback retained activity row progress.");
        Console.WriteLine("PASS: file and activity progress track real PDF pages, item identity and stop");
        CaptureMainWindow(window, dir);
        CapturePresenterWindow();
        var savedProbe = ThemeManager.HighContrastProbe;
        try
        {
            ThemeManager.HighContrastProbe = () => true;
            typeof(MainWindow).GetMethod("ApplyTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { AppTheme.Tech });
            var emergency = (Button)typeof(MainWindow).GetField("_emergencyStop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Require(emergency.BackColor == SystemColors.Highlight && emergency.ForeColor == SystemColors.HighlightText,
                "Emergency button ignored system high contrast.");
            var drawer = (Control)typeof(MainWindow).GetField("_musicDrawer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var tracks = (ListBox)drawer.GetType().GetField("_tracks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(drawer)!;
            using (var selectedRow = new Bitmap(300, 26))
            using (var graphics = Graphics.FromImage(selectedRow))
            {
                var draw = new DrawItemEventArgs(graphics, tracks.Font, new Rectangle(0, 0, 300, 26), 0, DrawItemState.Selected);
                drawer.GetType().GetMethod("DrawRow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(drawer, new object[] { tracks, draw, "歌曲", "文件不存在", true, true });
                Require(selectedRow.GetPixel(295, 3).ToArgb() == SystemColors.Highlight.ToArgb(),
                    "Music selection did not use the high-contrast highlight background.");
                bool foregroundFound = false;
                for (int y = 2; y < 24 && !foregroundFound; y++)
                    for (int x = 8; x < 280; x++)
                        if (selectedRow.GetPixel(x, y).ToArgb() == SystemColors.HighlightText.ToArgb()) { foregroundFound = true; break; }
                Require(foregroundFound, "Selected music row did not draw text with the matching system foreground.");
            }
            drawer.GetType().GetMethod("Open")!.Invoke(drawer, null);
            Pump(300);
            using (var drawerShot = new Bitmap(window.Width, window.Height))
            {
                window.DrawToBitmap(drawerShot, new Rectangle(Point.Empty, window.Size));
                drawerShot.Save(Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots", "simulated-highcontrast-music-drawer.png"), ImageFormat.Png);
            }
            drawer.GetType().GetMethod("Close")!.Invoke(drawer, null);
            Pump(300);
            using var contrastShot = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(contrastShot, new Rectangle(Point.Empty, window.Size));
            contrastShot.Save(Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots", "simulated-system-highcontrast.png"), ImageFormat.Png);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var activities = typeof(MainWindow).GetField("_activitiesPanel", flags)!.GetValue(window)!;
            typeof(MainWindow).GetMethod("ShowPanel", flags)!.Invoke(window, new[] { activities });
            Pump(150);
            using var activityShot = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(activityShot, new Rectangle(Point.Empty, window.Size));
            activityShot.Save(Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots", "simulated-highcontrast-activities.png"), ImageFormat.Png);
        }
        finally
        {
            ThemeManager.HighContrastProbe = savedProbe;
            typeof(MainWindow).GetMethod("ApplyTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { AppTheme.Dark });
        }
        outputState.Disconnect();
        window.Close();
        Console.WriteLine("PASS: main window starts, lays out and closes with the document controls");

        // Background-music playlists persist (mode, tracks, startup flag, mixer settings).
        string musicPath = Path.Combine(dir, "music-roundtrip.json");
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

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
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
    /// <summary>The 演讲者视图 with sample content (a real one needs a running presentation program), at
    /// its default size and at its minimum, saved for layout review; also checks that nothing in the
    /// grid overlaps or leaves the window.</summary>
    private static void CapturePresenterWindow()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots");
        Directory.CreateDirectory(output);
        using var presenter = new PresenterWindow(() => null, () => null, () => null)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false,
        };
        presenter.Show();
        Pump(150);
        presenter.ShowSampleForScreenshot();
        foreach (var size in new[] { new Size(1280, 760), new Size(960, 600) })
        {
            presenter.Size = size;
            presenter.PerformLayout();
            Pump(150);
            var client = presenter.ClientRectangle;
            var visible = presenter.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (var control in visible)
                if (!client.Contains(control.Bounds))
                    throw new InvalidOperationException($"Presenter view at {size.Width}x{size.Height}: {control.GetType().Name} \"{control.Text}\" {control.Bounds} leaves the window {client}.");
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                    if (visible[i].Bounds.IntersectsWith(visible[j].Bounds))
                        throw new InvalidOperationException($"Presenter view at {size.Width}x{size.Height}: \"{visible[i].Text}\" {visible[i].Bounds} overlaps \"{visible[j].Text}\" {visible[j].Bounds}.");
            using var shot = new Bitmap(presenter.Width, presenter.Height);
            presenter.DrawToBitmap(shot, new Rectangle(Point.Empty, presenter.Size));
            shot.Save(Path.Combine(output, $"presenter-view-{size.Width}x{size.Height}.png"), ImageFormat.Png);
        }
        Console.WriteLine("PASS: 演讲者视图 lays out without overlaps at 1280x760 and 960x600 (screenshots saved)");
    }

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
        // Moving the window partly off screen is kept (no bounce back); only a title bar dragged out of
        // reach is brought back. WinForms raises ResizeBegin/ResizeEnd around a move as well.
        {
            var formType = typeof(GradientForm);
            var begin = formType.GetMethod("OnResizeBegin", flags)!;
            var end = formType.GetMethod("OnResizeEnd", flags)!;
            var area = Screen.FromControl(window).WorkingArea;
            var partly = new Point(area.Right - 300, area.Top + 40);
            begin.Invoke(window, new object[] { EventArgs.Empty });
            window.Location = partly;
            end.Invoke(window, new object[] { EventArgs.Empty });
            Require(window.Location == partly, $"A window moved partly off screen bounced back to {window.Location}.");
            begin.Invoke(window, new object[] { EventArgs.Empty });
            window.Location = new Point(area.Right + 4000, area.Bottom + 4000);
            end.Invoke(window, new object[] { EventArgs.Empty });
            Require(Screen.AllScreens.Any(sc => Rectangle.Intersect(new Rectangle(window.Left, window.Top, window.Width, 24), sc.WorkingArea).Width >= 100),
                "A window dragged entirely off screen was not brought back within reach.");
            window.Location = new Point(area.Left + 20, area.Top + 20);
            Console.WriteLine("PASS: window may stay partly off screen; an unreachable title bar is brought back");
        }
        // 第二十一轮 notices: a fixed three-slot area in the 监看 column; a fourth drops the oldest; no ON AIR bar.
        {
            var stack = (ToastStack)type.GetField("_toastStack", flags)!.GetValue(window)!;
            int areaHeight = stack.Height;
            for (int i = 1; i <= 4; i++) stack.Show($"通知 {i}", ToastSeverity.Info);
            Pump(50);
            Require(stack.Notices.Count == ToastStack.MaxVisible && stack.Notices[0].Message == "通知 2" && stack.Height == areaHeight
                && stack.Parent == type.GetField("_monitorColumn", flags)!.GetValue(window),
                $"Notices: {stack.Notices.Count} shown, height {stack.Height} (area {areaHeight}), parent {stack.Parent?.GetType().Name}.");
            Require(WaitFor(() => stack.Notices.Count == 0, 5000), "Notices did not close by themselves after 3 s.");
            Require(type.GetField("_onAir", flags) == null, "The ON AIR bar is still there.");
            Console.WriteLine("PASS: notices sit in the right column (max 3, fixed height, close after 3 s); no ON AIR bar");
        }
        // Two identical 监看 windows: with only the main display, files play in the first free window
        // (both busy: the top one); a file dropped on a window plays in that window.
        {
            // Single-screen routing: run with no Program engine attached (restored afterwards).
            var playbackField = type.GetField("_playback", flags)!;
            var savedProgram = playbackField.GetValue(window);
            playbackField.SetValue(window, null);
            var routingDock = (SharedPreviewDock)type.GetField("_previewDock", flags)!.GetValue(window)!;
            var top = (PlaybackEngine)type.GetField("_preview", flags)!.GetValue(window)!;
            var bottom = (PlaybackEngine)type.GetField("_preview2", flags)!.GetValue(window)!;
            string routingDir = Path.Combine(Path.GetTempPath(), "EveryStage-Routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(routingDir);
            MediaFile Image(string name)
            {
                string path = Path.Combine(routingDir, name);
                using (var bitmap = new Bitmap(32, 18)) bitmap.Save(path, ImageFormat.Png);
                return new MediaFile { SourcePath = path, Kind = MediaKind.Image };
            }
            var click = type.GetMethod("OnFileClicked", flags)!;
            var a1 = Image("a.png"); var b1 = Image("b.png"); var c1 = Image("c.png"); var d1 = Image("d.png");
            click.Invoke(window, new object[] { a1 });
            Require(WaitFor(() => ReferenceEquals(top.CurrentFile, a1) && top.State != PlaybackChannelState.Loading, 3000), "First file did not play in the top window.");
            click.Invoke(window, new object[] { b1 });
            Require(WaitFor(() => ReferenceEquals(bottom.CurrentFile, b1), 3000), "Second file did not play in the free bottom window.");
            click.Invoke(window, new object[] { c1 });
            Require(WaitFor(() => ReferenceEquals(top.CurrentFile, c1), 3000) && ReferenceEquals(bottom.CurrentFile, b1),
                "With both windows busy the top window was not used.");
            var dropped = typeof(SharedPreviewDock).GetField("FileDropped", flags)!.GetValue(routingDock) as Delegate;
            dropped!.DynamicInvoke(1, d1, null);
            Require(WaitFor(() => ReferenceEquals(bottom.CurrentFile, d1), 3000), "A file dropped on the bottom window did not play there.");
            top.Stop();
            bottom.Stop();
            Pump(50);
            playbackField.SetValue(window, savedProgram);
            Console.WriteLine("PASS: 监看 windows: first free window, then the top one; a dropped file plays where it was dropped");
        }
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
                    if (page == "_settingsPanel")
                    {
                        var tags = Descendants(window).OfType<Label>().Where(label => Equals(label.Tag, "effect")).ToArray();
                        var palette = UiPalette.For(theme);
                        // Settings save at once (no 保存设置): no 保存后生效 tag is left; 新播放生效 stays a warning.
                        Require(!tags.Any(label => label.Text.Contains("保存后生效"))
                            && tags.Any(label => label.Text.Contains("新播放生效"))
                            && tags.Where(label => label.Text.Contains("新播放生效")).All(label => label.ForeColor == palette.Warning)
                            && tags.Where(label => label.Text.Contains("即时生效")).All(label => label.ForeColor == palette.Accent),
                            $"Settings effect labels did not use their semantic roles in {theme}.");
                    }
                    if (page == "_filesPanel")
                    {
                        var files = (Control)type.GetField(page, flags)!.GetValue(window)!;
                        var empty = (Label)files.GetType().GetField("_emptyStateLabel", flags)!.GetValue(files)!;
                        var palette = UiPalette.For(theme);
                        Require(empty.BackColor == palette.Surface && empty.ForeColor == palette.Muted,
                            $"File empty state did not keep its surface and text roles in {theme}.");
                        var search = FindByTypeName(files, "SearchBox")!;
                        var input = search.Controls.OfType<TextBox>().Single();
                        Require(input.BackColor == palette.Surface && input.ForeColor == palette.Text && input.AccessibleName == "搜索文件名",
                            $"File search did not keep its field roles in {theme}.");
                    }
                    if (page == "_devicesPanel")
                    {
                        var discoverable = Descendants(window).OfType<Label>().Single(label => label.Text.Contains("可被局域网发现"));
                        Require(discoverable.ForeColor == UiPalette.For(theme).Success,
                            $"Device discovery status did not keep its role in {theme}.");
                    }
                    if (page == "_activitiesPanel")
                    {
                        var activities = (Control)type.GetField(page, flags)!.GetValue(window)!;
                        foreach (var menuField in new[] { "_scenarioMenu", "_editMenu" })
                        {
                            var activityMenu = (ContextMenuStrip)activities.GetType().GetField(menuField, flags)!.GetValue(activities)!;
                            typeof(ToolStripDropDown).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                .Invoke(activityMenu, new object[] { new System.ComponentModel.CancelEventArgs() });
                            Require(activityMenu.Renderer is ThemedMenuRenderer && activityMenu.BackColor == UiPalette.For(theme).Surface,
                                $"{menuField} did not follow the {theme} theme when opened.");
                        }
                        var nextName = (Label)activities.GetType().GetField("_nextName", flags)!.GetValue(activities)!;
                        Require(nextName.ForeColor == UiPalette.Normal(AppTheme.Dark).Text,
                            $"Activity next-item text did not retain the monitor's dark-palette role in {theme}.");
                    }
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

        // 背景音乐歌单 slides up out of the music bar (no window) and back.
        var drawer = (Control)type.GetField("_musicDrawer", flags)!.GetValue(window)!;
        var drawerType = drawer.GetType();
        drawerType.GetMethod("Toggle")!.Invoke(drawer, null);
        Pump(400);
        Require(drawer.Visible && drawer.Height > 150 && (bool)drawerType.GetProperty("IsOpen")!.GetValue(drawer)!, $"背景音乐: the drawer did not slide open (height {drawer.Height}).");
        Require(Application.OpenForms.Cast<Form>().All(f => f == window || f.GetType().Name != "PlaylistDialog"), "背景音乐: a playlist window opened.");
        // Audio dropped on the 文件库 goes to the selected 歌单, not into the library.
        var filesPanel = type.GetField("_filesPanel", flags)!.GetValue(window)!;
        int libraryCount = library.Files.Count;
        string[] songs = { @"C:\music\开场.mp3", @"C:\music\颁奖.mp3", @"C:\music\散场.mp3" };
        filesPanel.GetType().GetMethod("ImportPaths", flags)!.Invoke(filesPanel, new object[] { songs });
        Pump(100);
        Require(library.Files.Count == libraryCount, "背景音乐: dropped audio went into the 文件库.");
        var selectedPlaylist = drawerType.GetProperty("Selected", flags)!;
        List<string> Songs() => ((Playlist)selectedPlaylist.GetValue(drawer)!).Tracks.TakeLast(3).ToList();
        Require(Songs().SequenceEqual(songs), "背景音乐: dropped audio did not join the playlist.");
        // Drag 开场 below 散场, then 散场 back to the top.
        int offset = ((Playlist)selectedPlaylist.GetValue(drawer)!).Tracks.Count - 3;
        var moveTracks = drawerType.GetMethod("MoveTracks", flags)!;
        moveTracks.Invoke(drawer, new object[] { new[] { offset }, offset + 3 });
        Require(Songs().SequenceEqual(new[] { songs[1], songs[2], songs[0] }), $"背景音乐: drag reorder gave {string.Join(",", Songs())}.");
        moveTracks.Invoke(drawer, new object[] { new[] { offset + 1 }, offset });
        Require(Songs().SequenceEqual(new[] { songs[2], songs[1], songs[0] }), $"背景音乐: drag reorder gave {string.Join(",", Songs())}.");
        Pump(100);
        using (var shot = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(shot, new Rectangle(Point.Empty, window.Size));
            shot.Save(Path.Combine(output, "music-drawer.png"), ImageFormat.Png);
        }
        drawerType.GetMethod("Toggle")!.Invoke(drawer, null);
        Pump(400);
        Require(!drawer.Visible && drawer.Height == 0, $"背景音乐: the drawer did not slide closed (height {drawer.Height}).");

        // Drag to reorder ends in FileLibraryStore.Move: the moved files land before the target.
        var order = library.Files.Select(f => f.Id).ToList();
        library.Move(new[] { order[0], order[1] }, order[4]);
        Require(library.Files.Select(f => f.Id).ToList() is var moved && moved[2] == order[0] && moved[3] == order[1] && moved[4] == order[4],
            "文件库 reorder did not put the files before the target.");
        library.Move(new[] { order[0] }, null);
        Require(library.Files[^1].Id == order[0], "文件库 reorder to the end failed.");

        applyTheme.Invoke(window, new object[] { AppTheme.Dark });
        // L-06 三档截图对比的最小两档：上面的主循环按窗口 Size 覆盖 1440×900 与 1024×640 两档，
        // 这里再按 ClientSize 补 960×600（主窗口 MinimumSize 对应的最小客户区）和 1024×640 两轮，
        // 各截流程/文件/设置三页。发布时把这批图与大档截图一起人工对比，及早发现小窗口下控件被
        // 截断或重叠的回归——这类问题自动断言很难逐页兜住，靠的就是改版时三档并排看。
        void CapturePageAtCurrentSize(string pageField, string fileName)
        {
            showPanel.Invoke(window, new[] { type.GetField(pageField, flags)!.GetValue(window)! });
            Pump(150);
            using var shot = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(shot, new Rectangle(Point.Empty, window.Size));
            shot.Save(Path.Combine(output, fileName), ImageFormat.Png);
        }
        var sizeBeforeSmallShots = window.Size;
        try
        {
            foreach (var clientSize in new[] { new Size(960, 600), new Size(1024, 640) })
            {
                window.ClientSize = clientSize;
                Pump(150);
                string suffix = $"{clientSize.Width}x{clientSize.Height}";
                CapturePageAtCurrentSize("_activitiesPanel", $"activities-{suffix}.png");
                CapturePageAtCurrentSize("_filesPanel", $"files-{suffix}.png");
                CapturePageAtCurrentSize("_settingsPanel", $"settings-{suffix}.png");
            }
        }
        finally
        {
            window.Size = sizeBeforeSmallShots;
            Pump(150);
        }
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
        int uiThread = Environment.CurrentManagedThreadId, playingThread = -1;
        void RecordState(PlaybackChannelState state) { if (state == PlaybackChannelState.Playing) playingThread = Environment.CurrentManagedThreadId; }
        preview.StateChanged += RecordState;
        WithoutSynchronizationContext(() => preview.RequestPlay(file));
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.OfficeSession?.Status == OfficeSessionStatus.Ready, 60000),
            $"Word document did not open live: {preview.LastError?.Message ?? preview.CurrentContentNote}");
        preview.StateChanged -= RecordState;
        Require(playingThread == uiThread, "Word opening published playback state off the UI thread.");
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
        int uiThread = Environment.CurrentManagedThreadId, playingThread = -1;
        void RecordState(PlaybackChannelState state) { if (state == PlaybackChannelState.Playing) playingThread = Environment.CurrentManagedThreadId; }
        program.StateChanged += RecordState;
        bool accepted = false;
        WithoutSynchronizationContext(() => accepted = program.RequestPlay(preview.CaptureSnapshot()!.Value));
        Require(accepted, "Program refused the deck.");
        Require(WaitFor(() => program.State == PlaybackChannelState.Playing && program.OfficeSession?.Status == OfficeSessionStatus.Ready, 60000),
            $"Native show did not start on Program: {program.LastError?.Message ?? program.CurrentContentNote}");
        program.StateChanged -= RecordState;
        Require(playingThread == uiThread, "Native PowerPoint opening published playback state off the UI thread.");
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

    private static void WithoutSynchronizationContext(Action action)
    {
        var previous = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(null); action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

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
