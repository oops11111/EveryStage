using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Playback;

public sealed partial class PlaybackEngine
{
    /// <summary>"点文件" from within an activity's file list — establishes the auto-advance/manual-
    /// skip context that <see cref="NextManual"/>/<see cref="PreviousManual"/> and
    /// <see cref="CompletionAction.NextItem"/> use. A background-audio file (PLANNING.md's "不占用主
    /// 队列顺序位") is intercepted here before it ever reaches <see cref="PlayFile"/> or updates
    /// <see cref="_currentActivity"/>/<see cref="_currentFileIndex"/> — a direct click starts (or
    /// leaves alone) the overlay track exactly like passing over one during <see cref="TryAdvance"/>
    /// does, but doesn't search further for a "real" slot to display, since a direct click is a
    /// one-off action, not a queue walk.</summary>
    public void RequestPlay(Activity activity, int fileIndex, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
        if (fileIndex < 0 || fileIndex >= activity.Files.Count) return;
        var file = activity.Files[fileIndex];
        if (file.IsBackgroundAudio && Channel == PlaybackChannel.Program)
        {
            StartOrUpdateBackgroundAudio(file);
            return;
        }
        _currentActivity = activity;
        _currentFileIndex = fileIndex;
        PlayFile(file, trigger);
    }

    /// <summary>"点文件" with no activity context (e.g. directly from a future 文件 panel). With no
    /// activity list to advance through, <see cref="CompletionAction.NextItem"/> degrades to
    /// holding on the last frame — nothing in PLANNING.md defines "next" without an activity. Same
    /// background-audio interception as the other overload — a library entry can carry
    /// <see cref="MediaFile.IsBackgroundAudio"/> just as well as an activity's copy of it.</summary>
    public void RequestPlay(MediaFile file, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
        if (file.IsBackgroundAudio && Channel == PlaybackChannel.Program)
        {
            StartOrUpdateBackgroundAudio(file);
            return;
        }
        _currentActivity = null;
        _currentFileIndex = -1;
        PlayFile(file, trigger);
    }

    /// <summary>Floating-preview-window "下一项" (PLANNING.md §8.3) — except when the current file
    /// is a multi-page Document, where this means "turn to the next page" first
    /// (<see cref="IContentRenderer.NextPage"/>: exactly the caller its own doc comment describes
    /// but never had until now). Only once the document has no further page to turn to does this
    /// fall through to the normal "advance to the next playlist item" behavior — see this project's
    /// README for why page-turning and playlist-advance share these two buttons rather than getting
    /// their own. Deliberately NOT gated by <see cref="MediaFile.AllowManualSkip"/>: that field's
    /// own doc comment is about skipping past this item in the sequence, not about navigating pages
    /// within it, so <see cref="TryAdvance"/>'s existing check only applies once page-turning is
    /// exhausted. Returns false at the end of the current activity's file list or with no activity
    /// context (and no further page to turn to).</summary>
    public bool NextManual()
    {
        if (TryStepDocument(1)) return true;
        return TryAdvance(1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Floating-preview-window "上一项" — same page-before-item precedence as
    /// <see cref="NextManual"/>, in reverse. Returns false at the start of the list (and no further
    /// page to turn back to).</summary>
    public bool PreviousManual()
    {
        if (TryStepDocument(-1)) return true;
        return TryAdvance(-1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Null unless the current document has more than one page / slide — the floating
    /// preview window shows "第X页/共Y页" only when 上一项/下一项 turn pages. 1-based.</summary>
    public (int CurrentPage, int PageCount)? DocumentPageInfo =>
        Document is { PageCount: > 1 } doc ? (Math.Max(1, doc.Page), doc.PageCount) : null;

    /// <summary>Walks <see cref="_currentActivity"/>'s file list by <paramref name="delta"/> at a
    /// time, transparently passing over any background-audio file it lands on — see class doc
    /// comment for PLANNING.md's "不占用主队列顺序位": each one encountered starts (or leaves alone)
    /// the overlay track via <see cref="StartOrUpdateBackgroundAudio"/> as a side effect, then the
    /// walk continues in the same direction as if that slot were not in the list at all. Returns
    /// false (no visible change) if the walk reaches either end of the list without finding a
    /// non-background-audio file to land on — a list that is ALL background-audio files (or empty
    /// past the current position) simply holds, the same "nothing further, stay put" behavior an
    /// ordinary out-of-range index already had before this method needed to loop at all.</summary>
    private bool TryAdvance(int delta, PlaybackTrigger trigger)
    {
        if (_currentActivity == null) return false;
        // MediaFile.AllowManualSkip gates only the floating-preview-window buttons (ManualSkip) —
        // an activity's own NextItem/auto-advance is a separate trigger and was never meant to be
        // blocked by "don't let the operator skip past this one manually" (see AllowManualSkip's
        // own doc comment: it says nothing about auto-advance, and conflating the two would make a
        // "no manual skip" file also stall CompletionAction.NextItem, which isn't what either
        // property is documented to mean). Checked once, against the file being left, not
        // re-checked per background-audio slot passed over below — this gate is about leaving
        // _currentFile, not about the transparent slots in between.
        if (trigger == PlaybackTrigger.ManualSkip && _currentFile?.AllowManualSkip == false) return false;

        // Resolved by identity, not by the cached index - the activity's file list is edited
        // directly by the UI with no notification to this engine. See ResolveScanOrigin.
        int from = PlaybackQueueNavigator.ResolveScanOrigin(_currentActivity, _currentFile, _currentFileIndex, delta);
        int next = PlaybackQueueNavigator.FindNextPlayable(
            _currentActivity, from, delta, StartOrUpdateBackgroundAudio);
        if (next < 0) return false;

        _currentFileIndex = next;
        PlayFile(_currentActivity.Files[next], trigger);
        return true;
    }

    /// <summary>What happens after <see cref="TryAdvance"/> reaches the end of the current activity's
    /// file list under <see cref="PlayMode.SequentialAuto"/> — this class's own doc comment used to
    /// list this as "deliberately out of scope" (PLANNING.md never says). Confirmed with the user
    /// before writing this: reaching the end of one activity now auto-advances into the NEXT activity
    /// in the same <see cref="Scenario"/> (found via <see cref="_scenarioStore"/> by reference-
    /// equality search — the same "look up the containing collection by identity, not by a
    /// non-unique field like name" approach <c>ActivitiesPanel.TryHighlightPlayingFile</c> already
    /// uses for <see cref="MediaFile"/>), landing on its first playable (non-background-audio) file.
    ///
    /// Only called from <see cref="HandleCompletion"/>'s <see cref="PlaybackTrigger.ActivityAuto"/>
    /// path — <see cref="NextManual"/>/<see cref="PreviousManual"/> (the floating preview window's
    /// 上一项/下一项 buttons, <see cref="PlaybackTrigger.ManualSkip"/>) deliberately do NOT call this:
    /// confirmed with the user that manual skip should stay scoped to the current activity exactly as
    /// before, so it can't ever land the operator somewhere PLANNING.md never described a UI
    /// affordance for jumping straight to (the activity list itself, not just the file within it).
    ///
    /// Reaching the end of the LAST activity in the scenario simply returns false and leaves
    /// <see cref="_currentActivity"/>/<see cref="_currentFileIndex"/> untouched — also confirmed with
    /// the user: no wraparound back to the scenario's first activity, so this never turns into a
    /// silent infinite loop across an entire scenario the way <see cref="CompletionAction.Loop"/>
    /// deliberately does for a single file. An activity with nothing playable in it (empty, or every
    /// file in it is background-audio) is transparent to this search exactly like
    /// <see cref="TryAdvance"/> already treats a lone background-audio slot within one activity — any
    /// background-audio file passed over still gets started via
    /// <see cref="StartOrUpdateBackgroundAudio"/>, then the search keeps walking into the activity
    /// after it, never mutating <see cref="_currentActivity"/>/<see cref="_currentFileIndex"/> unless
    /// it actually finds somewhere real to land.</summary>
    private bool TryAdvanceToNextActivity()
    {
        if (_currentActivity == null) return false;

        var scenario = _scenarioStore.Scenarios.FirstOrDefault(s => s.Activities.Contains(_currentActivity));
        if (scenario == null) return false; // the current activity was removed/replaced out from under playback.

        int activityIndex = scenario.Activities.IndexOf(_currentActivity);
        for (int a = activityIndex + 1; a < scenario.Activities.Count; a++)
        {
            var activity = scenario.Activities[a];
            foreach (var candidate in activity.Files)
            {
                if (candidate.IsBackgroundAudio)
                {
                    StartOrUpdateBackgroundAudio(candidate);
                    continue;
                }

                _currentActivity = activity;
                _currentFileIndex = activity.Files.IndexOf(candidate);
                PlayFile(candidate, PlaybackTrigger.ActivityAuto);
                return true;
            }
        }
        return false;
    }

    /// <summary>A per-file override wins; otherwise falls back to the owning activity's default —
    /// see <see cref="MediaFile.PlayModeOverride"/>/<see cref="Activity.DefaultPlayMode"/>'s own doc
    /// comments. With no activity context at all (<see cref="RequestPlay(MediaFile, PlaybackTrigger)"/>),
    /// there's no default to fall back to beyond <see cref="PlayMode.SequentialAuto"/> itself — which
    /// is moot anyway since <see cref="TryAdvance"/> already refuses to advance with
    /// <see cref="_currentActivity"/> null, regardless of play mode.</summary>
    private PlayMode EffectivePlayMode(MediaFile file)
    {
        var mode = file.PlayModeOverride ?? _currentActivity?.DefaultPlayMode ?? PlayMode.SequentialAuto;
        // A number outside the enum (damaged or hand-edited store) behaves as the default (audit C-5).
        return Enum.IsDefined(mode) ? mode : PlayMode.SequentialAuto;
    }
}
