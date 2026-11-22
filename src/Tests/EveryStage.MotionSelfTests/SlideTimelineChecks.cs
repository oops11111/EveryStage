using System.Text.Json;
using EveryStage.Terminal.UI;

internal static partial class Program
{
    private static void SlideTimelineChecks(string output)
    {
        using var owner = new Control();
        _ = owner.Handle;
        var preference = new FakePreference();
        double value = -1;
        int completed = 0;
        bool lastReduced = false;
        using (var timeline = new SlideTransitionTimeline(owner, v => value = v, reduced => { completed++; lastReduced = reduced; }, preference))
        {
            bool wrongThread = false;
            Task.Run(() => { try { timeline.Start(200); } catch (InvalidOperationException) { wrongThread = true; } }).GetAwaiter().GetResult();
            Check(wrongThread, "actual timeline rejects off-owner Start before native entry");
            timeline.Start(500);
            Check(value == 0 && timeline.IsRunning && timeline.TimerEnabled && completed == 0, "normal timeline holds exact zero before its first outer tick");
            using (var tokens = JsonDocument.Parse(timeline.LastTokens!))
            {
                var root = tokens.RootElement.GetProperty("transitions").GetProperty("software.slide.progress").GetProperty("root");
                var step = root.GetProperty("steps")[0];
                Check(root.GetProperty("from").GetDouble() == 0 && step.GetProperty("to").GetDouble() == 1
                    && step.GetProperty("duration").GetString() == "500ms" && step.GetProperty("ease").GetString() == "linear",
                    "actual played private tokens bind specified duration and linear zero-to-one progress");
            }
            Wait(() => value > 0.1 && value < 0.9, "actual native duration timeline reaches an intermediate value");
            timeline.Cancel();
            double cancelled = value;
            Application.DoEvents();
            Check(!timeline.IsRunning && !timeline.TimerEnabled && value == cancelled && completed == 0, "Cancel removes active timeline without a stale completion");
            timeline.Start(100, 120);
            Wait(() => value == 1 && timeline.IsRunning, "normal timeline holds the final value while the native hold remains active");
            Check(timeline.DurationMilliseconds == 100 && timeline.HoldMilliseconds == 120 && completed == 0, "preview hold does not shorten the transition duration");
            Wait(() => !timeline.IsRunning, "normal duration plus hold completes");
            Check(value == 1 && completed == 1 && !lastReduced && !timeline.TimerEnabled, "normal outer completion fires once and stops its pump");
            preference.Set(false);
            Application.DoEvents();
            timeline.Start(300, 180);
            Check(value == 1 && completed == 2 && lastReduced && !timeline.IsRunning && !timeline.TimerEnabled && timeline.HoldMilliseconds == 0,
                "reduced timeline synchronously applies final one without hold or motion");
        }
        Check(preference.Listeners == 0, "timeline disposal removes its preference subscription");
        SlideTransitionTimeline? replacement = null;
        bool restarted = false;
        int replacementsCompleted = 0;
        replacement = new SlideTransitionTimeline(owner, v =>
        {
            if (v > 0 && v < 1 && !restarted) { restarted = true; replacement!.Start(100); }
        }, _ => replacementsCompleted++, new FakePreference());
        replacement.Start(300);
        Wait(() => restarted && !replacement.IsRunning, "actual apply callback queues a fresh timeline without native reentry");
        Check(replacementsCompleted == 1 && replacement.DurationMilliseconds == 100, "old generation cannot complete over replacement timeline");
        replacement.Dispose();
        SlideTransitionTimeline? chained = null;
        int chainedCompletions = 0;
        chained = new SlideTransitionTimeline(owner, _ => { }, _ =>
        {
            if (++chainedCompletions == 1) chained!.Start(70);
        }, new FakePreference());
        chained.Start(70);
        Wait(() => chainedCompletions == 2 && !chained.IsRunning, "outer completion can start the next timeline and drain it fully");
        chained.Dispose();
        var disposalPreference = new FakePreference();
        SlideTransitionTimeline? disposal = null;
        disposal = new SlideTransitionTimeline(owner, v => { if (v > 0) disposal!.Dispose(); }, _ => throw new InvalidOperationException("Disposed timeline completed"), disposalPreference);
        disposal.Start(200);
        Wait(() => disposal.IsDisposed, "timeline Dispose from actual native apply is safely deferred");
        Check(disposalPreference.Listeners == 0 && !disposal.TimerEnabled, "callback timeline Dispose releases native state and static subscriptions");
        var queuedPreference = new FakePreference();
        int queuedApplies = 0;
        var queued = new SlideTransitionTimeline(owner, _ => queuedApplies++, _ => { }, queuedPreference);
        queued.Start(300);
        Task.Run(() => queuedPreference.Set(false)).GetAwaiter().GetResult();
        queued.Dispose();
        int beforeQueue = queuedApplies;
        Application.DoEvents();
        Check(queuedApplies == beforeQueue && queuedPreference.Listeners == 0, "queued preference after timeline Dispose cannot revive callbacks");
        SurfaceChecks(output);
        PickerChecks(output);
    }
}
