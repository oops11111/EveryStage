using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.UI;
using MotionSoftDemo;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Checks = new();
    private static readonly List<object> Screenshots = new();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
        Checks.Add(label);
        Console.WriteLine("PASS " + label);
    }
    private static void Wait(Func<bool> condition, string label)
    {
        var watchdog = Stopwatch.StartNew();
        while (!condition() && watchdog.Elapsed < TimeSpan.FromSeconds(8))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Check(condition(), label + " (functional watchdog, not performance)");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "EveryStage-motion-harness-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(output)) throw new InvalidOperationException("Use a new harness output directory");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var recorder = new LifecycleRecorder();
        try
        {
            AdapterChecks();
            DrawerChecks(output);
            Check(recorder.Created > 0 && recorder.Created == recorder.Released,
                "actual native lifecycle Created equals Released across all control tests");
            using var realPreference = new WindowsAnimationPreference();
            bool observedPreference = realPreference.AnimationsEnabled;
            Check(realPreference.AnimationsEnabled == observedPreference, "real system animation preference only read");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                status = "PASS isolated actual-control harness; not ordinary Terminal startup",
                checks = _checks, labels = Checks, recorder.Created, recorder.Released,
                system_settings_written = false, program_entry_invoked = false,
                media_started = false, network_started = false,
                persistence = "explicit fresh temp overrides, empty music/file libraries",
                isolation_verified_by_explicit_store_fields_and_source_audit = true,
                filesystem_access_traced = false,
                screenshots = Screenshots
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("HARNESS_OUTPUT=" + output);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new
            { error = error.ToString(), checks = _checks, labels = Checks }));
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void AdapterChecks()
    {
        using var owner = new Control();
        _ = owner.Handle;
        var preference = new FakePreference();
        double value = 0;
        int completed = 0;
        using (var motion = new DrawerHeightMotion(owner, v => value = v, () => completed++, preference))
        {
            bool wrongThreadRejected = false;
            Task.Run(() =>
            {
                try { motion.MoveTo(100, 0); }
                catch (InvalidOperationException) { wrongThreadRejected = true; }
            }).GetAwaiter().GetResult();
            Check(wrongThreadRejected, "actual adapter rejects off-owner motion calls before native entry");
            motion.MoveTo(100, 0);
            Wait(() => value > 0 && value < 100, "actual adapter enters intermediate state");
            double before = value;
            motion.MoveTo(0, value);
            Check(Math.Abs(value - before) < 1, "from-null reversal does not replace current visual value");
            Wait(() => !motion.IsRunning, "reverse reaches rest");
            Check(Math.Abs(value) < 0.01 && !motion.TimerEnabled, "rest target and idle timer stopped");
            preference.Set(false);
            Application.DoEvents();
            motion.MoveTo(80, value);
            Wait(() => !motion.IsRunning, "fake reduced preference reaches target");
            Check(value == 80, "reduced preference keeps correct final target");
            preference.Set(true);
            Application.DoEvents();
            motion.MoveTo(20, value);
            Task.Run(() => preference.Set(false)).GetAwaiter().GetResult();
            motion.Dispose();
            double disposedValue = value;
            Application.DoEvents();
            Check(value == disposedValue && motion.IsDisposed && !motion.TimerEnabled && preference.Listeners == 0,
                "queued worker preference after Dispose is harmless and detached");
        }
        var callbackPreference = new FakePreference();
        DrawerHeightMotion? disposeFromApply = null;
        disposeFromApply = new DrawerHeightMotion(owner, _ => disposeFromApply!.Dispose(), () => { }, callbackPreference);
        disposeFromApply.MoveTo(60, 0);
        Wait(() => disposeFromApply.IsDisposed, "actual native apply can dispose without fm reentry");
        Check(callbackPreference.Listeners == 0 && !disposeFromApply.TimerEnabled,
            "deferred native teardown removes preference and timer");

        var completionPreference = new FakePreference();
        DrawerHeightMotion? completion = null;
        bool retargeted = false;
        int settled = 0;
        value = 0;
        completion = new DrawerHeightMotion(owner, v =>
        {
            value = v;
            if (completion is { IsRunning: false } && !retargeted)
            {
                retargeted = true;
                completion.MoveTo(15, v);
            }
        }, () => settled++, completionPreference);
        completion.MoveTo(60, 0);
        Wait(() => retargeted && !completion.IsRunning, "completion apply can start a newer actual animation");
        Check(value == 15 && settled == 1, "stale completion did not settle the newer request");
        completion.Dispose();

        DrawerHeightMotion? latest = null;
        bool queued = false;
        value = 0;
        latest = new DrawerHeightMotion(owner, v =>
        {
            value = v;
            if (latest is { IsRunning: true } && !queued)
            {
                queued = true;
                latest.MoveTo(20, v);
                latest.MoveTo(60, v);
            }
        }, () => { }, new FakePreference());
        latest.MoveTo(60, 0);
        Wait(() => !latest.IsRunning, "callback B then current A drains safely");
        Check(queued && latest.Target == 60 && value == 60, "last request wins and clears stale pending B");
        latest.Dispose();

        var throwing = new FakePreference { ThrowOnRead = true };
        bool rejected = false;
        try { _ = new DrawerHeightMotion(owner, _ => { }, () => { }, throwing); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && throwing.Listeners == 0, "constructor failure unsubscribes preference and releases native state");
    }

    private static void DrawerChecks(string output)
    {
        // Never call Terminal.Program, default stores, settings, Play, or any network/Office entry.
        string data = Path.Combine(output, "isolated-data");
        Directory.CreateDirectory(data);
        var library = new MusicLibraryStore(Path.Combine(data, "music.json"));
        library.Library.Playlists.Add(new Playlist { Name = "Isolated test playlist" });
        var files = new FileLibraryStore(Path.Combine(data, "files.json"));
        Check(Path.GetFullPath((string)typeof(MusicLibraryStore).GetField("_storePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(library)!) == Path.Combine(data, "music.json")
            && Path.GetFullPath((string)typeof(FileLibraryStore).GetField("_storePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(files)!) == Path.Combine(data, "files.json"),
            "actual store instances retain explicit fresh temp paths");
        using var music = new MusicChannel(library, settingsStore: null);
        using var form = new Form
        {
            Text = "EveryStage actual MusicDrawer — isolated control harness",
            ClientSize = new Size(960, 720), ShowInTaskbar = false,
            BackColor = ModernUi.Background
        };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = ModernUi.Background };
        form.Controls.Add(host);
        var pref = new FakePreference();
        var drawer = new MusicDrawer(music, files, pref) { Dock = DockStyle.Bottom };
        host.Controls.Add(drawer);
        form.Show();
        Application.DoEvents();
        void Shot(string name)
        {
            GetWindowThreadProcessId(form.Handle, out uint processId);
            Check(form.Visible && !form.IsDisposed && IsWindowVisible(form.Handle) && processId == Environment.ProcessId,
                "owned harness HWND visible for " + name);
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            Check(image.Size == form.Size, "captured complete owned-frame dimensions for " + name);
            string path = Path.Combine(output, name + ".png");
            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Screenshots.Add(new { name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                image.Width, image.Height, client_width = form.ClientSize.Width, client_height = form.ClientSize.Height,
                frame_width = form.Width, frame_height = form.Height,
                drawer_height = drawer.Height, drawer.IsOpen,
                moving = Motion(drawer).IsRunning, hwnd_owner_verified = true });
        }
        Shot("drawer-closed");
        drawer.Toggle();
        Wait(() => drawer.Height >= 100 && Motion(drawer).IsRunning, "actual drawer reaches a meaningful intermediate height");
        Shot("drawer-opening");
        int beforeReverse = drawer.Height;
        drawer.Toggle();
        Wait(() => drawer.Height <= beforeReverse - 8 && drawer.Height >= 40 && Motion(drawer).IsRunning,
            "reverse reaches a meaningful intermediate height");
        Shot("drawer-reversing");
        Wait(() => !Motion(drawer).IsRunning, "actual drawer quick reverse closes");
        Check(drawer.Height == 0 && !drawer.Visible && BitmapField(drawer) == null,
            "closed rest hides drawer and releases bitmap");
        Shot("drawer-rest-closed");
        drawer.Open();
        Wait(() => !Motion(drawer).IsRunning, "reopen reaches full rest");
        int openHeight = drawer.Height;
        Check(openHeight > 0 && drawer.Controls[0].Visible && BitmapField(drawer) == null,
            "real controls restored after whole-bitmap animation");
        Shot("drawer-open");
        form.ClientSize = new Size(960, 430);
        Wait(() => !Motion(drawer).IsRunning && drawer.Height < openHeight, "parent resize retargets actual drawer");
        Shot("drawer-resized");
        var collapse = drawer.Controls[0].Controls.OfType<Button>().Single(b => b.Text.StartsWith("收起"));
        collapse.PerformClick();
        Wait(() => !Motion(drawer).IsRunning, "actual collapse button closes");
        Check(!drawer.IsOpen && drawer.Height == 0, "collapse final state");
        drawer.Open();
        Wait(() => !Motion(drawer).IsRunning, "open before actual Escape handling");
        object[] key = { Message.Create(drawer.Handle, 0, IntPtr.Zero, IntPtr.Zero), Keys.Escape };
        bool handled = (bool)typeof(MusicDrawer).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(drawer, key)!;
        Check(handled, "actual ProcessCmdKey handles Escape");
        Wait(() => !Motion(drawer).IsRunning, "Escape closes actual drawer");
        drawer.Open();
        Wait(() => drawer.Height > 0, "open before disposal");
        var bitmap = BitmapField(drawer);
        drawer.Dispose();
        form.ClientSize = new Size(900, 500);
        pref.Set(false);
        Application.DoEvents();
        Check(pref.Listeners == 0 && Motion(drawer).IsDisposed && !Motion(drawer).TimerEnabled,
            "actual control disposal detaches preference and stops motion");
        Check(typeof(MusicDrawer).GetField("_resizeParent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(drawer) == null,
            "actual parent resize ownership detached");
        if (bitmap != null)
        {
            bool disposed = false;
            try { _ = bitmap.Width; } catch (ArgumentException) { disposed = true; }
            Check(disposed, "in-flight bitmap disposed with actual drawer");
        }
        form.Close();
        Check(!music.IsPlaying && files.Files.Count == 0 && !Directory.EnumerateFiles(data).Any(),
            "empty explicit stores unchanged and no media started");
    }

    private static DrawerHeightMotion Motion(MusicDrawer drawer) =>
        (DrawerHeightMotion)typeof(MusicDrawer).GetField("_motion", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(drawer)!;
    private static Bitmap? BitmapField(MusicDrawer drawer) =>
        (Bitmap?)typeof(MusicDrawer).GetField("_slideImage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(drawer);

    private sealed class FakePreference : IAnimationPreference
    {
        private Action? _changed;
        private bool _enabled = true;
        internal bool ThrowOnRead { get; init; }
        internal int Listeners => _changed?.GetInvocationList().Length ?? 0;
        public bool AnimationsEnabled => ThrowOnRead ? throw new InvalidOperationException("test preference read failure") : _enabled;
        public event Action? Changed { add => _changed += value; remove => _changed -= value; }
        internal void Set(bool enabled) { _enabled = enabled; _changed?.Invoke(); }
    }

    private sealed class LifecycleRecorder : EventListener
    {
        internal int Created;
        internal int Released;
        internal LifecycleRecorder() => EnableEvents(MotionEtw.Log, EventLevel.Verbose, EventKeywords.All);
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            // Guard before ANY payload getter/count/enumeration; no timing/present fields.
            if (data.EventId is 3 or 103 or 4 || data.EventName is "FrameTick" or "FrameTickAccept" or "PresentStats") return;
            if (data.EventName != "AnimationLifecycle") return;
            var payload = data.Payload;
            if (payload is not { Count: > 4 }) return;
            if (payload[4]?.ToString() == "Created") Created++;
            if (payload[4]?.ToString() == "Released") Released++;
        }
    }
}
