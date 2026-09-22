using System.Collections.Concurrent;
using System.Drawing.Imaging;
using EveryStage.Terminal.ContentEngine;

// Run continuations on a single owning thread, as the WinForms renderer callers do.
using var context = new TestContext();
SynchronizationContext.SetSynchronizationContext(context);
Task tests = RunTests();
while (!tests.IsCompleted) context.RunOne();
tests.GetAwaiter().GetResult();
Console.WriteLine("Windows content self-tests passed.");

static async Task RunTests()
{
    TestBoundedQueue();
    TestAspectRatioSizing();
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-WindowsTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        string first = Path.Combine(directory, "first.png");
        string second = Path.Combine(directory, "second.png");
        using (var bitmap = new Bitmap(64, 32)) bitmap.Save(first, ImageFormat.Png);
        using (var bitmap = new Bitmap(32, 64)) bitmap.Save(second, ImageFormat.Png);
        string officePath = Path.Combine(directory, "preview.docx");
        using (var archive = System.IO.Compression.ZipFile.Open(officePath, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("docProps/thumbnail.png").Open())
        using (var source = File.OpenRead(first)) source.CopyTo(entry);
        using (var officePreview = OfficeThumbnailReader.Read(officePath, new Size(240, 150)))
            Require(officePreview?.Size == new Size(240, 120), "Office embedded preview missing or distorted.");
        using (File.Open(officePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var renderer = new ImageContentRenderer();
        await renderer.LoadAsync(first);
        Require(renderer.CurrentFrame?.Size == new Size(64, 32), "Image size changed.");
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Task old = renderer.LoadAsync(first);
        Task latest = renderer.LoadAsync(second);
        await Task.WhenAll(old, latest);
        Require(renderer.CurrentFrame?.Size == new Size(32, 64), "Older load replaced latest frame.");
        string invalid = Path.Combine(directory, "invalid.png");
        File.WriteAllText(invalid, "not an image");
        bool failed = false;
        try { await renderer.LoadAsync(invalid); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { failed = true; }
        Require(failed && renderer.CurrentFrame == null, "Bad image retained stale frame.");
        Task pending = renderer.LoadAsync(first);
        renderer.Dispose();
        await pending;
        Require(renderer.CurrentFrame == null, "Frame published after disposal.");
        Console.WriteLine("PASS: image decode, file release, latest-load wins, corrupt image, disposal during load");
        string pdfPath = Path.Combine(directory, "pages.pdf");
        WritePdfFixture(pdfPath);
        using (var thumbnail = PdfContentRenderer.CreateThumbnail(pdfPath, new Size(240, 150)))
            Require(thumbnail.Size == new Size(200, 150), "PDF thumbnail did not preserve page proportions.");
        using (File.Open(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var pdf = new PdfContentRenderer();
        pdf.SetTargetSize(new Size(320, 240));
        await pdf.LoadAsync(pdfPath);
        Require(pdf.PageCount == 2 && pdf.CurrentFrame?.Size == new Size(320, 240), "PDF initial rendering failed.");
        Require(await pdf.TurnPageAsync(1) && pdf.CurrentPageIndex == 1 && !await pdf.TurnPageAsync(1), "PDF next-page boundary failed.");
        Require(pdf.CurrentFrame?.Size == new Size(180, 240), "Portrait PDF was stretched to the landscape output.");
        Require(await pdf.TurnPageAsync(-1) && pdf.CurrentPageIndex == 0 && !await pdf.TurnPageAsync(-1), "PDF previous-page boundary failed.");
        Require(pdf.CurrentFrame?.Size == new Size(320, 240), "Landscape PDF aspect ratio changed.");
        Task<bool> oldPage = pdf.TurnPageAsync(1);
        Task replacement = pdf.LoadAsync(pdfPath);
        await Task.WhenAll(oldPage, replacement);
        Require(pdf.CurrentPageIndex == 0 && pdf.CurrentFrame?.Size == new Size(320, 240),
            "Old page render replaced a newly loaded PDF.");
        using (var closing = new PdfContentRenderer())
        {
            await closing.LoadAsync(pdfPath);
            Task<bool> turning = closing.TurnPageAsync(1);
            closing.Dispose();
            await turning;
            Require(closing.CurrentFrame == null, "Page render published after disposal.");
        }
        Task pendingPdf = pdf.LoadAsync(pdfPath);
        pdf.Dispose();
        await pendingPdf;
        Require(pdf.CurrentFrame == null, "PDF frame published after disposal.");
        using (File.Open(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Console.WriteLine("PASS: native PDF rendering, async page boundaries, replacement during paging, disposal during paging/load, file release");
        TestAudioLayout(directory);
    }
    finally { Directory.Delete(directory, true); }
}

static void TestBoundedQueue()
{
    var released = new List<int>();
    using var queue = new EveryStage.Transport.BoundedLatestQueue<int>(3, released.Add);
    for (int i = 0; i < 100; i++) queue.Enqueue(i);
    Require(released.SequenceEqual(Enumerable.Range(0, 97)), "Overflow did not release oldest frames exactly once.");
    Require(queue.TryDequeue(out int frame) && frame == 97, "Queue did not retain latest frames in order.");
    queue.Dispose();
    queue.Enqueue(100);
    queue.Dispose();
    Require(released.SequenceEqual(Enumerable.Range(0, 97).Concat(new[] { 98, 99, 100 })),
        "Shutdown leaked or double-released queued/late frames.");
    Require(!queue.TryDequeue(out _), "Disposed queue retained a frame.");
    Console.WriteLine("PASS: live frame queue capacity, latest-frame retention and exactly-once release");
}

static void TestAspectRatioSizing()
{
    var ratio = new Size(4, 3);
    foreach (int dpi in new[] { 96, 120, 144, 192 })
    foreach (var screen in new[] { new Size(1920, 1040), new Size(1366, 728), new Size(800, 560) })
    foreach (bool vertical in new[] { false, true })
    {
        var maximum = new Size(screen.Width - 24 * dpi / 96, screen.Height - 24 * dpi / 96);
        var result = EveryStage.Terminal.UI.AspectRatioSizing.Resize(new Size(1700, 900),
            new Size(800 * dpi / 96, 600 * dpi / 96), maximum, ratio, vertical);
        Require(result.Width * 3 == result.Height * 4, "Window ratio drifted.");
        Require(result.Width <= maximum.Width && result.Height <= maximum.Height, "Window exceeded working area.");
    }
    Console.WriteLine("PASS: 4:3 window sizing across DPI scales, small screens and drag directions");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestAudioLayout(string directory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using (var window = new AspectTestWindow())
            {
                var proposed = Rectangle.FromLTRB(150, 120, 1257, 823);
                for (int edge = 1; edge <= 8; edge++)
                {
                    Rectangle resized = window.ApplySizing(edge, proposed);
                    Require(resized.Width * 3 == resized.Height * 4, $"WM_SIZING edge {edge} broke 4:3.");
                    if (edge is 1 or 4 or 7) Require(resized.Right == proposed.Right, "Left resize moved right anchor.");
                    else Require(resized.Left == proposed.Left, "Right resize moved left anchor.");
                    if (edge is 3 or 4 or 5) Require(resized.Bottom == proposed.Bottom, "Top resize moved bottom anchor.");
                    else Require(resized.Top == proposed.Top, "Bottom resize moved top anchor.");
                }
            }
            Console.WriteLine("PASS: window WM_SIZING handling for all eight edges/corners and fixed anchors");
            var library = new EveryStage.Terminal.Data.FileLibraryStore(Path.Combine(directory, "audio-library.json"));
            var audio = library.Import("layout-test.mp3")!;
            var type = typeof(ImageContentRenderer).Assembly.GetType("EveryStage.Terminal.UI.Panels.AudioPlayerBar", throwOnError: true)!;
            using var bar = (Control)Activator.CreateInstance(type, library,
                new EveryStage.Terminal.Logging.FileOperationLogger(directory), null)!;
            type.GetMethod("SetAudioFiles")!.Invoke(bar, new object[] { new[] { audio } });
            foreach (int width in new[] { 900, 576, 400, 900 })
            {
                bar.Width = width;
                type.GetMethod("RefreshLiveState")!.Invoke(bar, null);
                var regions = new[] { "_playButton", "_progressTrack", "_volumeTrack", "_loopButton" }
                    .Select(name => (Rectangle)type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bar)!).ToList();
                regions.Add(bar.Controls.OfType<Button>().Single().Bounds);
                foreach (var region in regions)
                    Require(bar.ClientRectangle.Contains(region), $"Audio control outside {width}px layout: {region}");
                for (int i = 0; i < regions.Count; i++)
                    for (int j = i + 1; j < regions.Count; j++)
                        Require(!regions[i].IntersectsWith(regions[j]), $"Audio controls overlap at {width}px.");
                using var bitmap = new Bitmap(bar.Width, bar.Height);
                bar.DrawToBitmap(bitmap, bar.ClientRectangle);
            }
            int playRequests = 0;
            Action<EveryStage.Terminal.Data.MediaFile> onPlay = file =>
            {
                Require(file.Id == audio.Id, "Keyboard played a different file.");
                playRequests++;
            };
            type.GetEvent("CastRequested")!.AddEventHandler(bar, onPlay);
            var keyHandler = type.GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var space = new KeyEventArgs(Keys.Space);
            keyHandler.Invoke(bar, new object[] { space });
            Require(playRequests == 1 && space.Handled && space.SuppressKeyPress, "Space did not trigger audio playback.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.Control | Keys.Space) });
            Require(playRequests == 1, "Modified shortcut unexpectedly triggered playback.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.L) });
            var reloaded = new EveryStage.Terminal.Data.FileLibraryStore(Path.Combine(directory, "audio-library.json"));
            Require(reloaded.Files.Single().OnCompletion == EveryStage.Terminal.Data.CompletionAction.Loop,
                "Keyboard loop setting was not persisted.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.L) });
            Require(audio.OnCompletion == EveryStage.Terminal.Data.CompletionAction.NextItem, "Keyboard could not disable looping.");
            type.GetEvent("CastRequested")!.RemoveEventHandler(bar, onPlay);
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Audio layout test did not finish.");
    if (failure != null) throw new InvalidOperationException("Audio layout test failed.", failure);
    Console.WriteLine("PASS: audio bar layout, offscreen painting, keyboard playback, modifier isolation and loop persistence");
}

static void WritePdfFixture(string path)
{
    string[] objects =
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 320 240] /Resources << >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 320] /Resources << >> >>",
    };
    var text = new System.Text.StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (int i = 0; i < objects.Length; i++)
    {
        offsets.Add(text.Length);
        text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    int xref = text.Length;
    text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
    text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
}

sealed class TestContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
    public void RunOne()
    {
        if (_queue.TryTake(out var work, 100)) work.Callback(work.State);
    }
    public void Dispose() => _queue.Dispose();
}

sealed class AspectTestWindow : EveryStage.Terminal.UI.GradientForm
{
    public AspectTestWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        NormalWindowAspectRatio = new Size(4, 3);
        MinimumSize = new Size(800, 600);
    }

    public Rectangle ApplySizing(int edge, Rectangle proposed)
    {
        IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new[] { proposed.Left, proposed.Top, proposed.Right, proposed.Bottom }, 0, memory, 4);
            Message message = Message.Create(Handle, 0x214, new IntPtr(edge), memory);
            base.WndProc(ref message);
            if (message.Result != new IntPtr(1)) throw new InvalidOperationException("Sizing message was not handled.");
            int[] result = new int[4];
            System.Runtime.InteropServices.Marshal.Copy(memory, result, 0, 4);
            return Rectangle.FromLTRB(result[0], result[1], result[2], result[3]);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }
}
