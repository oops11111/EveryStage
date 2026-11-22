using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Security.Cryptography;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.UI;
using EveryStage.Terminal.UI.Panels;

internal static partial class Program
{
    private static T S2Field<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void S2Call(object owner, string method) => owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
    private static float SurfaceProgress(ContentSurface surface) => S2Field<float>(surface, "_transitionProgress");
    private static Bitmap SurfaceImage(ContentSurface surface)
    {
        var result = new Bitmap(surface.Width, surface.Height);
        surface.DrawToBitmap(result, new Rectangle(Point.Empty, surface.Size));
        return result;
    }
    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        var rectangle = new Rectangle(Point.Empty, first.Size);
        var a = first.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var b = second.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] rowA = new byte[first.Width * 4], rowB = new byte[first.Width * 4];
            for (int y = 0; y < first.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, rowA.Length);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, rowB.Length);
                if (!rowA.AsSpan().SequenceEqual(rowB)) return false;
            }
            return true;
        }
        finally { first.UnlockBits(a); second.UnlockBits(b); }
    }
    private static Bitmap SyntheticSlide(Color back, string title, bool circle)
    {
        var slide = new Bitmap(320, 180);
        using var graphics = Graphics.FromImage(slide);
        graphics.Clear(back);
        using var font = new Font("Segoe UI", 20);
        graphics.DrawString(title, font, Brushes.White, 16, 16);
        if (circle) graphics.FillEllipse(Brushes.LightGreen, 220, 90, 70, 70);
        else graphics.FillRectangle(Brushes.LightYellow, 20, 100, 80, 50);
        return slide;
    }
    private static void S2Shot(Form form, string output, string name, string description)
    {
        GetWindowThreadProcessId(form.Handle, out uint pid);
        Check(pid == Environment.ProcessId && IsWindowVisible(form.Handle), "software transition capture uses visible owned synthetic-control window");
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        Check(image.Size == form.Size, "software transition capture includes full form frame");
        string path = Path.Combine(output, name + ".png");
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Screenshots.Add(new { name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            image.Width, image.Height, description, hwnd_owner_verified = true,
            scope = "synthetic actual ContentSurface/SlideTransitionPicker, no Office/Terminal entry" });
    }
    private sealed class CountingPainter : IContentPainter
    {
        internal int Paints;
        internal Color Color = Color.Orange;
        public void Paint(Graphics graphics, Rectangle bounds)
        {
            Paints++;
            using var brush = new SolidBrush(Color);
            graphics.FillRectangle(brush, bounds);
        }
    }
    private static void SurfaceChecks(string output)
    {
        using var form = new Form { Text = "EveryStage software slide transition — isolated synthetic harness", ClientSize = new Size(320, 180) };
        var preference = new FakePreference();
        var surface = new ContentSurface(preference) { Dock = DockStyle.Fill };
        form.Controls.Add(surface);
        form.Show();
        using var from = SyntheticSlide(Color.FromArgb(220, 30, 30), "Slide A", false);
        using var to = SyntheticSlide(Color.FromArgb(30, 30, 220), "Slide B", true);
        using var third = SyntheticSlide(Color.FromArgb(20, 140, 40), "Slide C", true);
        surface.SetFrame(from);
        Check(preference.Listeners == 0 && S2Field<SlideTransitionTimeline?>(surface, "_transitionTimeline") == null,
            "ordinary content surface is lazy and owns no timeline/preference subscription before a transition");
        using var normalizedFrom = SurfaceImage(surface);
        surface.SetFrame(to);
        using var normalizedTo = SurfaceImage(surface);
        foreach (var effect in Enum.GetValues<SlideTransitionEffect>())
        foreach (var direction in Enum.GetValues<SlideTransitionDirection>())
        {
            surface.SetFrame(from);
            surface.BeginTransition(new SlideTransitionSpec(effect, direction, 400));
            surface.SetFrame(to);
            using (var start = SurfaceImage(surface)) Check(SamePixels(start, normalizedFrom), $"actual {effect}/{direction} begins at exact old picture");
            Wait(() => SurfaceProgress(surface) > 0.25f && SurfaceProgress(surface) < 0.75f && surface.InTransition, $"actual {effect}/{direction} enters linear intermediate progress");
            float raw = SurfaceProgress(surface);
            using var actual = SurfaceImage(surface);
            using var expected = new Bitmap(surface.Width, surface.Height);
            using (var graphics = Graphics.FromImage(expected))
                SlideTransitions.Draw(graphics, new Rectangle(Point.Empty, surface.Size), normalizedFrom, normalizedTo, SlideTransitions.Ease(raw), effect, direction);
            Check(SamePixels(actual, expected), $"actual {effect}/{direction} renderer applies original Ease exactly once");
            if (direction == SlideTransitionDirection.LeftToRight) S2Shot(form, output, "ppt-" + effect + "-middle", $"actual {effect}/{direction}, linear progress {raw:0.000}; synthetic pages");
            Wait(() => !surface.InTransition, $"actual {effect}/{direction} finishes");
            using var end = SurfaceImage(surface);
            Check(SamePixels(end, normalizedTo), $"actual {effect}/{direction} ends at exact new picture");
        }
        surface.SetFrame(from);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.Fade, SlideTransitionDirection.LeftToRight, 600));
        surface.SetFrame(to);
        Wait(() => SurfaceProgress(surface) > 0.25f && SurfaceProgress(surface) < 0.6f, "surface reaches a composite before rapid replacement");
        using (var before = SurfaceImage(surface))
        {
            surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.RainSoak, SlideTransitionDirection.TopToBottom, 400));
            surface.SetFrame(third);
            using var after = SurfaceImage(surface);
            Check(SamePixels(before, after), "mid-transition replacement freezes exact current composite with no first-frame jump");
        }
        S2Shot(form, output, "ppt-fast-replacement-start", "new transition starts from exact prior composite");
        surface.SetFrame(null);
        Check(!surface.InTransition && S2Field<Bitmap?>(surface, "_transitionFrom") == null && S2Field<Bitmap?>(surface, "_transitionTo") == null,
            "SetFrame null cancels timeline and frees both transition pictures");
        Check(!S2Field<SlideTransitionTimeline>(surface, "_transitionTimeline").TimerEnabled, "clear leaves no active transition pump");
        using (var cleared = SurfaceImage(surface)) Check(cleared.GetPixel(cleared.Width / 2, cleared.Height / 2).ToArgb() == Color.Black.ToArgb(), "cleared output contains no stale old slide");
        surface.SetFrame(from);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.PageTurn, SlideTransitionDirection.RightToLeft, 500));
        surface.SetFrame(to);
        surface.SetPainter(null);
        Check(!surface.InTransition && S2Field<Bitmap?>(surface, "_transitionFrom") == null, "SetPainter null also clears transition ownership");
        surface.SetFrame(from);
        var painter = new CountingPainter();
        surface.SetPainter(painter);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.RainSoak, SlideTransitionDirection.LeftToRight, 1000));
        using (var first = SurfaceImage(surface)) { }
        int cachedPaints = painter.Paints;
        Wait(() => SurfaceProgress(surface) > 0.15f && SurfaceProgress(surface) < 0.7f, "static-cache transition advances actual native progress");
        using (var second = SurfaceImage(surface)) { }
        Check(painter.Paints == cachedPaints, "pure timeline invalidation does not repaint cached static target");
        painter.Color = Color.Blue;
        surface.Invalidate();
        using (var changed = SurfaceImage(surface)) { }
        Check(painter.Paints == cachedPaints + 1, "real content invalidation refreshes the static target once");
        surface.SetFrame(null);
        surface.SetFrame(from);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.RainSoak, SlideTransitionDirection.LeftToRight, 500));
        using var live = new Bitmap(320, 180);
        using (var g = Graphics.FromImage(live)) g.Clear(Color.Blue);
        surface.SetFrame(live, live: true);
        using (var initial = SurfaceImage(surface)) { }
        using (var g = Graphics.FromImage(live)) g.Clear(Color.Green);
        using (var next = SurfaceImage(surface)) { }
        Check(S2Field<Bitmap>(surface, "_transitionTo").GetPixel(surface.Width / 2, surface.Height / 2).ToArgb() == Color.Green.ToArgb(),
            "live frame transition target refreshes even without a separate content-dirty signal");
        form.ClientSize = new Size(480, 270);
        Check(!surface.InTransition && S2Field<Bitmap?>(surface, "_transitionFrom") == null, "resize cancels old-size transition buffers");
        surface.SetFrame(to);
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.PageTurn, SlideTransitionDirection.LeftToRight, 500));
        surface.SetFrame(third);
        using (var resized = SurfaceImage(surface))
        {
            Check(S2Field<Bitmap>(surface, "_transitionFrom").Size == surface.ClientSize,
                "new transition after resize freezes current content at new dimensions");
            Check(resized.GetPixel(resized.Width / 2, resized.Height / 2).B > 180,
                "resize then BeginTransition does not blend a mismatched-size stale from picture");
        }
        preference.Set(false);
        Application.DoEvents();
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.RainSoak, SlideTransitionDirection.RightToLeft, 500));
        surface.SetFrame(from);
        Check(!surface.InTransition && !S2Field<SlideTransitionTimeline>(surface, "_transitionTimeline").TimerEnabled,
            "reduced surface skips movement and leaves final caller content with no pump");
        preference.Set(true);
        Application.DoEvents();
        surface.BeginTransition(new SlideTransitionSpec(SlideTransitionEffect.PageTurn, SlideTransitionDirection.LeftToRight, 500));
        surface.SetFrame(to);
        using (var allocated = SurfaceImage(surface)) { }
        var ownedFrom = S2Field<Bitmap>(surface, "_transitionFrom");
        var ownedTo = S2Field<Bitmap>(surface, "_transitionTo");
        surface.Dispose();
        Check(ImageDisposed(ownedFrom) && ImageDisposed(ownedTo) && preference.Listeners == 0, "surface Dispose releases active bitmaps/native timeline/preference");
        Check(!ImageDisposed(from) && !ImageDisposed(to), "surface never disposes caller-owned source frames");
        form.Close();
    }
    private static void PickerChecks(string output, bool diagnosticOnly = false)
    {
        using var form = new Form { Text = "EveryStage PPT transition picker — isolated synthetic harness", ClientSize = new Size(1100, 660) };
        var preference = new FakePreference();
        var picker = new SlideTransitionPicker(SlideTransitionEffect.RainSoak, SlideTransitionDirection.LeftToRight, 500, preference);
        form.Controls.Add(picker);
        picker.FitToWidth(form.ClientSize.Width);
        form.Show();
        picker.FitToWidth(form.ClientSize.Width);
        Check(form.ClientRectangle.Contains(picker.Bounds), "shown picker fits the actual desktop-constrained client bounds");
        int changes = 0;
        picker.SelectionChanged += () => changes++;
        var samples = new List<object>();
        int phase = 0;
        bool middleQueued = false, holdQueued = false, capturedMiddle = false, restarted = false, held = false, valuesCancelled = false, normalDone = false;
        double capturedProgress = -1;
        string? heldTokens = null;
        Exception? observerError = null;
        void Queue(Action action) => picker.BeginInvoke(() => { try { action(); } catch (Exception error) { observerError = error; } });
        InvalidateEventHandler observer = (_, _) =>
        {
            var current = S2Field<SlideTransitionTimeline?>(picker, "_timeline");
            if (current == null) return;
            if (samples.Count < 256) samples.Add(new { phase, current.Progress, current.IsRunning, current.TimerEnabled,
                current.DurationMilliseconds, current.HoldMilliseconds, preview = S2Field<float>(picker, "_previewT") });
            if (diagnosticOnly) return;
            // Observe here, then act outside the native callback. DoEvents can consume an entire run.
            if (phase == 0 && current.Progress > 0.2 && current.Progress < 0.8 && !middleQueued)
            {
                middleQueued = true;
                Queue(() =>
                {
                    if (!current.IsRunning || current.Progress <= 0.2 || current.Progress >= 0.8) { middleQueued = false; return; }
                    capturedProgress = current.Progress;
                    S2Shot(form, output, "ppt-picker-middle", $"actual outer-event preview progress {capturedProgress:0.000}, synthetic pages");
                    capturedMiddle = true;
                    phase = 1;
                    S2Field<TrackBar>(picker, "_duration").Value = 8;
                    restarted = current.DurationMilliseconds == 800 && current.Progress == 0 && changes == 1;
                });
            }
            else if (phase == 1 && current.Progress == 1 && current.IsRunning && !holdQueued)
            {
                holdQueued = true;
                Queue(() =>
                {
                    if (!current.IsRunning || current.Progress != 1) { holdQueued = false; return; }
                    held = current.HoldMilliseconds == 480;
                    heldTokens = current.LastTokens;
                    phase = 2;
                    picker.SetValues(SlideTransitionEffect.PageTurn, SlideTransitionDirection.TopToBottom, 300);
                    valuesCancelled = !current.IsRunning && S2Field<float>(picker, "_previewT") == 0.5f && changes == 1;
                    S2Call(picker, "PlayPreview");
                    phase = 3;
                });
            }
            else if (phase == 3 && !current.IsRunning && S2Field<float>(picker, "_previewT") == 0.5f)
                normalDone = true;
        };
        picker.Invalidated += observer;
        if (!diagnosticOnly) Application.DoEvents(); // Initial layout/paint precedes an ordinary user Play action.
        S2Call(picker, "PlayPreview");
        var timeline = S2Field<SlideTransitionTimeline>(picker, "_timeline");
        try
        {
            if (diagnosticOnly)
                Wait(() => timeline.Progress > 0.2 && timeline.Progress < 0.8, "actual picker plays intermediate native timeline");
            else
            {
                var watchdog = System.Diagnostics.Stopwatch.StartNew();
                while (!normalDone && observerError == null && watchdog.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(1); }
                if (observerError != null) throw new InvalidOperationException("Queued actual picker observation failed", observerError);
                Check(capturedMiddle && capturedProgress > 0.2 && capturedProgress < 0.8, "actual picker captured a real intermediate frame from an outer queued event");
                Check(restarted, "slider changed during actual playback restarts exact800ms at zero");
                Check(held, "actual picker was observed at target during the native0.6duration hold");
                using var tokens = JsonDocument.Parse(heldTokens!);
                var steps = tokens.RootElement.GetProperty("transitions").GetProperty("software.slide.progress").GetProperty("root").GetProperty("steps");
                Check(steps[0].GetProperty("duration").GetString() == "800ms" && steps[1].GetProperty("hold").GetString() == "480ms", "played picker token separates actual duration from exact hold");
                Check(valuesCancelled, "SetValues during actual hold cancels it without SelectionChanged");
                Check(normalDone && !timeline.IsRunning && S2Field<float>(picker, "_previewT") == 0.5f, "fresh normal preview completes then restores halfway still without an old hold callback");
            }
        }
        finally
        {
            picker.Invalidated -= observer;
            File.WriteAllText(Path.Combine(output, "picker-initial-diagnostic.json"), JsonSerializer.Serialize(new
            {
                diagnostic_only = diagnosticOnly, initial_layout_flushed = !diagnosticOnly, samples,
                capturedMiddle, capturedProgress, restarted, held, valuesCancelled, normalDone,
                final_progress = timeline.Progress, timeline.IsRunning, timeline.TimerEnabled, timeline.DurationMilliseconds, timeline.HoldMilliseconds,
                final_preview = S2Field<float>(picker, "_previewT"), preference_enabled = preference.AnimationsEnabled,
                picker.Visible, picker.IsHandleCreated, picker.Width, picker.Height,
                actual_client_width = form.ClientSize.Width, actual_client_height = form.ClientSize.Height,
                picker_bounds = new { picker.Left, picker.Top, picker.Right, picker.Bottom },
                picker_contained = form.ClientRectangle.Contains(picker.Bounds)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (diagnosticOnly) return;
        preference.Set(false);
        Application.DoEvents();
        S2Call(picker, "PlayPreview");
        Check(!timeline.IsRunning && !timeline.TimerEnabled && S2Field<float>(picker, "_previewT") == 1 && timeline.HoldMilliseconds == 0,
            "reduced picker stays on target still with no hold or old reset callback");
        S2Shot(form, output, "ppt-picker-reduced-target", "reduced preview final still, not a motion sample");
        picker.Dispose();
        Check(preference.Listeners == 0, "picker Dispose detaches preference and timeline ownership");
        form.Close();
    }
}
