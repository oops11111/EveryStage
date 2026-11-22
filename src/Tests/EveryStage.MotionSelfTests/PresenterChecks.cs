using System.Reflection;
using System.Security.Cryptography;
using EveryStage.Terminal.UI;

internal static partial class Program
{
    private static void StripInput(SlideThumbnailStrip strip, string method, EventArgs args) =>
        typeof(SlideThumbnailStrip).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(strip, new object[] { args });
    private static void Wheel(SlideThumbnailStrip strip, int delta) =>
        StripInput(strip, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 100, 20, delta));
    private static void Key(SlideThumbnailStrip strip, Keys key) => StripInput(strip, "OnKeyDown", new KeyEventArgs(key));
    private static bool ImageDisposed(Bitmap image)
    {
        try { _ = image.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
        catch (ObjectDisposedException) { return true; }
    }
    private static SlideThumbnailStrip NewStrip(FakePreference preference) => new(
        Color.FromArgb(11, 16, 23), Color.FromArgb(20, 28, 38), Color.FromArgb(34, 48, 63),
        Color.FromArgb(236, 241, 247), Color.FromArgb(143, 163, 184), Color.FromArgb(47, 124, 246), preference);
    private static Bitmap Thumb(int slide)
    {
        var image = new Bitmap(144, 81);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(slide % 2 == 0 ? Color.SteelBlue : Color.DarkSlateBlue);
        using var font = new Font("Segoe UI", 18);
        graphics.DrawString(slide.ToString(), font, Brushes.White, 12, 16);
        return image;
    }
    private static void PresenterShot(Form form, SlideThumbnailStrip strip, string output, string name)
    {
        GetWindowThreadProcessId(form.Handle, out uint pid);
        Check(IsWindowVisible(form.Handle) && pid == Environment.ProcessId, "presenter capture uses visible owned synthetic-control window");
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        Check(image.Size == form.Size, "presenter capture includes full form frame");
        string path = Path.Combine(output, name + ".png");
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Screenshots.Add(new { name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            image.Width, image.Height, presented_offset = strip.PresentedOffset, requested_offset = strip.RequestedOffset,
            moving = strip.MotionRunning, hwnd_owner_verified = true, content = "synthetic actual SlideThumbnailStrip; no Office/deck/Terminal Program" });
    }
    private static void PresenterChecks(string output)
    {
        PresenterAdapterChecks();
        var preference = new FakePreference();
        using var form = new Form { Text = "EveryStage actual presenter strip — isolated synthetic harness", ClientSize = new Size(760, 180) };
        var strip = NewStrip(preference);
        strip.Dock = DockStyle.Fill;
        form.Controls.Add(strip);
        form.Show();
        strip.Reset(24, null);
        for (int slide = 1; slide <= 24; slide++) strip.SetImage(slide, Thumb(slide));
        strip.Focus();
        Application.DoEvents();
        PresenterShot(form, strip, output, "presenter-strip-initial");
        Wheel(strip, -120);
        double firstTarget = strip.RequestedOffset;
        Wheel(strip, -120);
        Check(strip.RequestedOffset > firstTarget && strip.PresentedOffset < firstTarget,
            "actual rapid wheels accumulate from pending target rather than lagging visual offset");
        Wait(() => strip.PresentedOffset > 60 && strip.PresentedOffset < strip.RequestedOffset - 60 && strip.MotionRunning,
            "actual presenter strip reaches meaningful intermediate scroll");
        PresenterShot(form, strip, output, "presenter-strip-wheel-middle");
        float beforeReverse = strip.PresentedOffset;
        Key(strip, Keys.Home);
        Check(Math.Abs(strip.PresentedOffset - beforeReverse) < 1 && strip.RequestedOffset == 0,
            "actual keyboard Home retargets from current value without a visual jump");
        Wait(() => strip.PresentedOffset < beforeReverse - 8 && strip.PresentedOffset > 10 && strip.MotionRunning,
            "actual presenter strip reverses through a visible intermediate state");
        PresenterShot(form, strip, output, "presenter-strip-reverse-middle");
        Wait(() => !strip.MotionRunning, "actual keyboard follow reaches idle");
        Check(strip.PresentedOffset == 0 && !strip.MotionTimerEnabled, "presenter idle stops pump at exact target");
        strip.Current = 24;
        Wait(() => !strip.MotionRunning, "actual on-air Current scrolls last slide into view");
        float lastOffset = strip.PresentedOffset;
        Check(lastOffset > 0, "last on-air slide has nonzero actual scroll offset");
        PresenterShot(form, strip, output, "presenter-strip-current-last");
        Wheel(strip, 120);
        Wait(() => strip.PresentedOffset < lastOffset - 80 && strip.MotionRunning, "move before direct-manipulation interruption");
        StripInput(strip, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 150, 20, 0));
        float frozen = strip.PresentedOffset;
        Check(!strip.MotionRunning && !strip.MotionTimerEnabled && strip.Capture, "mouse down freezes presented offset and stops native pump");
        strip.Current = 1;
        Wheel(strip, -120);
        Key(strip, Keys.End);
        Application.DoEvents();
        Check(strip.PresentedOffset == frozen && !strip.MotionRunning,
            "pressed direct manipulation wins over Current, wheel and keyboard auto follow");
        StripInput(strip, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 210, 20, 0));
        Check(Math.Abs(strip.PresentedOffset - (frozen - 60)) < 1, "actual drag follows pointer delta immediately");
        float dragged = strip.PresentedOffset;
        int chosen = 0;
        strip.SlideChosen += _ => chosen++;
        StripInput(strip, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 210, 20, 0));
        Check(!strip.Capture && chosen == 0, "drag release neither keeps capture nor becomes a slide click");
        Wheel(strip, 120);
        Check(Math.Abs(strip.PresentedOffset - dragged) < 1 && strip.RequestedOffset < dragged,
            "post-drag wheel starts at dragged visual, not cancelled native rest");
        Wait(() => strip.PresentedOffset < dragged - 1, "post-drag new native target starts moving from dragged value");
        Check(strip.PresentedOffset <= dragged && strip.PresentedOffset >= strip.RequestedOffset,
            "first observed post-drag frame is on new segment, never old frozen rest");
        Wait(() => !strip.MotionRunning, "post-drag wheel settles");
        StripInput(strip, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 120, 20, 0));
        strip.Capture = false;
        float captureLost = strip.PresentedOffset;
        StripInput(strip, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 240, 20, 0));
        Check(strip.PresentedOffset == captureLost && !strip.MotionRunning, "capture loss safely terminates direct dragging");
        strip.Current = 24;
        form.ClientSize = new Size(1000, 180);
        Wait(() => !strip.MotionRunning, "resize cancels stale native source and follows bounded current slide");
        float resized = strip.PresentedOffset;
        Check(resized >= 0 && resized < lastOffset, "resized actual strip clamps to narrower scroll range");
        PresenterShot(form, strip, output, "presenter-strip-resized");
        Wheel(strip, 120);
        strip.Reset(3, null);
        Application.DoEvents();
        Check(strip.PresentedOffset == 0 && !strip.MotionRunning && !strip.MotionTimerEnabled,
            "Reset rejects old animation writes and restores zero offset");
        strip.Reset(24, null);
        preference.Set(false);
        Application.DoEvents();
        strip.Current = 24;
        Wait(() => !strip.MotionRunning, "injected reduced motion completes presenter navigation");
        Check(strip.PresentedOffset > 0 && !strip.MotionTimerEnabled, "reduced presenter preference preserves final visibility and idle");
        preference.Set(true);
        Application.DoEvents();
        Wheel(strip, 120);
        Task.Run(() => preference.Set(false)).GetAwaiter().GetResult();
        strip.Dispose();
        Application.DoEvents();
        Check(preference.Listeners == 0 && !strip.MotionRunning && !strip.MotionTimerEnabled,
            "presenter Dispose rejects queued preference/native callbacks and releases pump");
        form.Close();
        PresenterLoaderChecks();
    }
    private static void PresenterAdapterChecks()
    {
        using var owner = new Control();
        _ = owner.Handle;
        double value = 0;
        bool frozen = false;
        PresenterOffsetMotion? freeze = null;
        freeze = new PresenterOffsetMotion(owner, v =>
        {
            value = v;
            if (v > 0 && !frozen) { frozen = true; freeze!.Freeze(v); }
        }, () => { }, new FakePreference());
        freeze.MoveTo(100, 0);
        Wait(() => frozen && !freeze.IsRunning, "new offset adapter defers Freeze from actual native apply");
        Check(!freeze.TimerEnabled && freeze.Target == value, "callback Freeze cannot restart a stale frame pump");
        freeze.Dispose();
        var disposePreference = new FakePreference();
        PresenterOffsetMotion? disposal = null;
        disposal = new PresenterOffsetMotion(owner, v => { if (v > 0) disposal!.Dispose(); }, () => { }, disposePreference);
        disposal.MoveTo(100, 0);
        Wait(() => disposal.IsDisposed, "new offset adapter defers Dispose from actual native apply");
        Check(!disposal.TimerEnabled && disposePreference.Listeners == 0, "callback Dispose releases new adapter timer and preference");
        PresenterOffsetMotion? latest = null;
        bool retargeted = false;
        latest = new PresenterOffsetMotion(owner, v =>
        {
            value = v;
            if (v > 0 && !retargeted) { retargeted = true; latest!.MoveTo(20, v); }
        }, () => { }, new FakePreference());
        bool wrongThread = false;
        Task.Run(() => { try { latest.MoveTo(10, 0); } catch (InvalidOperationException) { wrongThread = true; } }).GetAwaiter().GetResult();
        Check(wrongThread, "new offset adapter rejects wrong-thread calls before native access");
        latest.MoveTo(100, 0);
        Wait(() => retargeted && !latest.IsRunning, "actual new-adapter apply queues retarget without native reentry");
        Check(latest.Target == 20 && value == 20 && !latest.TimerEnabled, "queued new offset request reaches its exact final target");
        latest.Dispose();
    }

    private static void PresenterLoaderChecks()
    {
        using var form = new Form { ShowInTaskbar = false, ClientSize = new Size(500, 130) };
        var strip = NewStrip(new FakePreference());
        strip.Dock = DockStyle.Fill;
        form.Controls.Add(strip);
        form.Show();
        int active = 0, maximum = 0, oldCalls = 0, newCalls = 0;
        var oldCompletion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newCompletion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        strip.Reset(24, async _ =>
        {
            oldCalls++; active++; maximum = Math.Max(maximum, active);
            try { return await oldCompletion.Task; }
            finally { active--; }
        });
        strip.Reset(8, async _ =>
        {
            newCalls++; active++; maximum = Math.Max(maximum, active);
            try { return newCalls == 1 ? await newCompletion.Task : Thumb(newCalls); }
            finally { active--; }
        });
        Check(oldCalls == 1 && newCalls == 0 && maximum == 1, "Reset does not overlap a still-running old thumbnail loader");
        var oldImage = Thumb(100);
        oldCompletion.SetResult(oldImage);
        Wait(() => newCalls == 1, "old loader completion continues new-version loader without an extra user event");
        Check(ImageDisposed(oldImage) && active == 1 && maximum == 1, "stale-version bitmap disposed and actual loaders stay serial");
        var firstNew = Thumb(1);
        newCompletion.SetResult(firstNew);
        Wait(() => active == 0, "new thumbnail loader completes its visible queue");
        strip.Current = 8;
        Wait(() => !strip.MotionRunning, "thumbnail loading continues through actual scroll frames");
        Check(newCalls <= 8 && maximum == 1, "animation frames never duplicate per-slide requests or flood concurrent loaders");
        var late = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        strip.Reset(2, async _ => await late.Task);
        strip.Dispose();
        var lateImage = Thumb(99);
        late.SetResult(lateImage);
        Wait(() => ImageDisposed(lateImage), "bitmap returning after real control Dispose is released");
        Check(ImageDisposed(firstNew), "Reset released prior cached thumbnail ownership");
        form.Close();
    }
}
