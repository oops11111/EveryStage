using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Playback;

/// <summary>
/// Ties the Content Engine renderers to the <c>Scenario</c>/<c>Activity</c>/<c>MediaFile</c> data
/// model and the output state machine — the piece PLANNING.md §6/§9 describe but that didn't exist
/// yet: "点文件" -> (cast-switch gated) -> pick the renderer matching the file's kind -> play it ->
/// advance per its completion action.
///
/// All public methods are expected to be called from the UI thread (the eventual Phase 4 UI, or
/// today's ad-hoc test entry points) — <see cref="ImageContentRenderer"/>/<see cref="PdfContentRenderer"/>'s
/// LoadAsync happens to complete synchronously today so this isn't currently a hard requirement,
/// but WinForms' <see cref="System.Windows.Forms.Timer"/> and <see cref="ContentSurface"/> both
/// need UI-thread access, so treat it as one anyway.
///
/// Deliberately out of scope for this first cut (see this project's README "已知风险/待验证事项"):
/// rendering a local-only preview when the cast switch is off (that preview surface belongs to the
/// Phase 4 UI's file/activity panels, which don't exist yet).
///
/// What happens after the last file in an activity under NextItem — PLANNING.md never specifies
/// cross-activity auto-advance — used to be listed here as out of scope too, until the user asked for
/// it directly and confirmed a concrete design before any code was written: see
/// <see cref="TryAdvanceToNextActivity"/>'s own doc comment for exactly what was decided (auto-advance
/// only, never manual skip; no wraparound past the scenario's last activity).
///
/// The *background*-audio half of §6 "音频特殊性" — <see cref="MediaFile.IsBackgroundAudio"/>,
/// PLANNING.md's "叠加在其他视觉内容之上播放，不占用主队列顺序位" — now has real behavior via a
/// second, independent <see cref="AudioContentController"/> (<see cref="BackgroundAudioController"/>),
/// distinct from <see cref="AudioController"/>'s own standalone-audio slot; WASAPI shared mode (see
/// <see cref="AudioPlaybackClock"/>'s constructor) genuinely mixes the two rather than one silently
/// stealing the device from the other. "不占用主队列顺序位" is implemented as literal transparency to
/// navigation: <see cref="TryAdvance"/>/<see cref="RequestPlay(Activity, int, PlaybackTrigger)"/>/
/// <see cref="RequestPlay(MediaFile, PlaybackTrigger)"/> all intercept a background-audio file before
/// it would ever become <see cref="_currentFile"/> — starting (or leaving alone, if already playing)
/// the overlay track as a side effect, then continuing to search in the same direction for a real
/// slot to actually display, exactly as if the background-audio entry were not present in the list at
/// all. PLANNING.md does not specify several things about this feature that this repository decided
/// for itself (see <see cref="PlayBackgroundAudio"/>/<see cref="OnBackgroundAudioCompleted"/>'s own
/// doc comments for the specific choices and reasoning): what <see cref="MediaFile.OnCompletion"/>
/// other than <see cref="CompletionAction.Loop"/> means for a track with no position of its own in
/// the sequence, and whether <see cref="Pause"/>/<see cref="Resume"/> (which this deliberately leaves
/// untouched — they remain scoped to the foreground <see cref="_currentFile"/> only) should also
/// affect it.
///
/// <see cref="MediaFile.PlayModeOverride"/>/<see cref="Activity.DefaultPlayMode"/> (via
/// <see cref="EffectivePlayMode"/>), <see cref="MediaFile.AllowManualSkip"/> (via
/// <see cref="TryAdvance"/>), standalone (non-background) audio playback — see
/// <see cref="PlayStandaloneAudio"/> — and now multi-page Document navigation (via
/// <see cref="NextManual"/>/<see cref="PreviousManual"/>/<see cref="DocumentPageInfo"/>, PLANNING.md
/// §3's PDF "翻页") — DO have real effect here, unlike the properties listed above.
///
/// <see cref="MediaKind.Document"/> now splits into two entirely different renderers depending on
/// the file extension (see <see cref="IsOfficeDocument"/>): a PDF still goes through
/// <see cref="_pdfRenderer"/>'s rasterized-bitmap pipeline exactly as before, but a PPT/Word/Excel
/// file goes through <see cref="WpsDocumentController"/> instead — PLANNING.md §14.1's WPS COM
/// integration, this repository's single highest-remaining, least-verifiable risk. See that class's
/// own doc comment for the full design (why the WPS window stays real/visible/separate rather than
/// rendered into <see cref="_overlay"/>, and the specific product decisions confirmed with the user
/// before writing it) and <see cref="PlayOfficeDocument"/>/<see cref="CloseWpsDocumentAndRestoreOverlay"/>
/// for how it's wired into this class's own lifecycle.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly OutputStateMachine _stateMachine;
    private readonly OverlayWindow _overlay;
    private readonly VideoSurface _videoSurface;
    private readonly SettingsStore _settingsStore;
    // Only ever read by TryAdvanceToNextActivity — see that method's own doc comment for why this
    // class otherwise has no reason to know about Scenarios at all (everything else it does is
    // scoped to a single Activity's file list, handed in via RequestPlay).
    private readonly ScenarioStore _scenarioStore;
    private readonly ImageContentRenderer _imageRenderer = new();
    private readonly PdfContentRenderer _pdfRenderer = new();
    private readonly PlaybackLogger _playbackLogger = new();
    private VideoContentController? _videoController;
    private AudioContentController? _audioController;
    private AudioContentController? _backgroundAudioController;

    // Unlike _videoController/_audioController (lazily created once, reused indefinitely),
    // constructed fresh per open and torn down per close — see WpsDocumentController's own doc
    // comment on why each WPS document gets its own Application instance rather than one shared
    // across unrelated opens. Null whenever no Office document is currently showing.
    private WpsDocumentController? _wpsController;

    // The MediaFile currently (or most recently) handed to _backgroundAudioController — distinct
    // from _currentFile, which a background-audio file never becomes (see class doc comment).
    // Reference-compared in PlayBackgroundAudio/OnBackgroundAudioCompleted/OnBackgroundAudioFailed
    // to tell "still the same overlay track" from "replaced by a different one since" or "already
    // stopped", the same staleness-guard pattern _currentFile itself already uses throughout this
    // class (e.g. OnImageOrDocumentFailed's own ReferenceEquals check).
    private MediaFile? _backgroundAudioFile;

    private Activity? _currentActivity;
    private int _currentFileIndex = -1;
    private MediaFile? _currentFile;
    private System.Windows.Forms.Timer? _stayDurationTimer;
    private DateTime _stayDurationArmedAt;
    private TimeSpan _stayDurationTotal;

    // Backing state for MediaFile.BackgroundAudioVisual == Waveform — see ApplyAudioVisual/
    // RedrawWaveformFrame. _latestAudioLevel is written from AudioContentController's background
    // playback thread (OnAudioLevelChanged) and read from the UI thread (_waveformTimer's Tick);
    // volatile is enough here since a torn/stale read costs nothing worse than one meter frame being
    // one PCM chunk behind, the same reasoning OnAudioLevelChanged's own doc comment gives.
    private volatile float _latestAudioLevel;
    private System.Windows.Forms.Timer? _waveformTimer;
    private System.Drawing.Bitmap? _audioVisualFrame;

    /// <summary>Raised after a file actually starts casting to the extended display (never raised
    /// when the cast switch declined the request).</summary>
    public event Action<MediaFile>? FileStarted;

    /// <summary>Raised instead of <see cref="FileStarted"/> when a "点文件" request reached
    /// <see cref="PlayFile"/> but the cast switch was off (<see cref="OutputStateMachine.RequestLocalFilePlayback"/>
    /// returned false) — exists purely so the UI can acknowledge the click happened (README risk #9:
    /// this used to be a completely silent no-op, which looked indistinguishable from the click not
    /// registering at all). Still doesn't render an actual local preview — that surface doesn't
    /// exist yet — this is only ever a brief "确认收到点击" signal, not a substitute for one.</summary>
    public event Action<MediaFile>? PlaybackDeclinedByCastSwitch;

    /// <summary>Raised right before local playback actually touches the shared
    /// <see cref="VideoSurface"/> (i.e. after the cast-switch check passes, immediately before
    /// showing the overlay's image/video surface) — <c>TerminalApplicationContext</c> (Program.cs)
    /// listens for this to stop any active device-cast <c>CastReceiver</c> first, since the two
    /// share one D3D11 device/swap chain bound to the overlay's video HWND and must never present
    /// concurrently (see <see cref="VideoSurface"/>'s doc comment). Not raised when the cast switch
    /// declines the request, same as <see cref="FileStarted"/> — nothing is about to touch the
    /// surface in that case.</summary>
    public event Action? LocalPlaybackStarting;

    /// <summary>PLANNING.md §11's Toast "涉及播放的异常需带可执行按钮（重试/移除）" — raised (already
    /// marshaled to the UI thread, unlike <see cref="VideoContentController.PlaybackFailed"/>/
    /// <see cref="AudioContentController.PlaybackFailed"/> themselves) whenever the currently-playing
    /// file's decode/render aborts abnormally, right after the same failure is logged via
    /// <see cref="PlaybackLogger.LogAbnormalInterruption"/>. Carries the <see cref="MediaFile"/> that
    /// failed (a "移除" UI needs to know which file, and — via <see cref="CurrentActivity"/> — which
    /// activity, if any, to remove it from) and the exception's message (for display). This is the
    /// first real consumer of this failure signal beyond the log entry itself — previously a decode
    /// failure was logged and then the last frame simply stayed frozen on screen with no operator-
    /// facing indication anything had gone wrong at all.</summary>
    public event Action<MediaFile, string>? PlaybackAbnormallyInterrupted;

    /// <summary>What's currently on the extended display, for the floating preview window
    /// (PLANNING.md §8.3) to show — null when nothing is casting.</summary>
    public MediaFile? CurrentFile => _currentFile;

    /// <summary>The activity <see cref="CurrentFile"/> belongs to, if it was reached via
    /// <see cref="RequestPlay(Activity, int, PlaybackTrigger)"/> — null if it was played with no
    /// activity context (<see cref="RequestPlay(MediaFile, PlaybackTrigger)"/>, e.g. a direct
    /// double-click from the 文件 panel). A Toast's "移除" action (see
    /// <see cref="PlaybackAbnormallyInterrupted"/>) uses this to decide which existing removal
    /// operation applies: remove-from-activity when this is non-null, remove-from-library when it's
    /// null — see this project's README for why there are two different "移除" operations rather
    /// than one, and why a Toast handler has to pick between them rather than this class doing it
    /// internally (it has no reference to <c>FileLibraryStore</c>/<c>ScenarioRepository</c> to act on
    /// either one itself).</summary>
    public Activity? CurrentActivity => _currentActivity;

    /// <summary>Replays whatever <see cref="CurrentFile"/> currently is, in its existing
    /// <see cref="CurrentActivity"/> context (if any) — PLANNING.md §11 Toast "重试". A no-op if
    /// nothing has ever played. Does not touch <see cref="_currentActivity"/>/<see cref="_currentFileIndex"/>
    /// at all (only <see cref="PlayFile"/> is called, the same private method <see cref="TryAdvance"/>
    /// itself calls) — a retry is "try this exact file again", not "move to a different position in
    /// the list", so the existing activity/index bookkeeping is left exactly as it already was.</summary>
    public void RetryCurrentFile()
    {
        if (_currentFile == null) return;
        PlayFile(_currentFile, PlaybackTrigger.ManualSkip);
    }

    public bool IsPaused { get; private set; }

    /// <summary>
    /// A static frame for the floating preview window to mirror (PLANNING.md §8.3's "缩略画面预览").
    /// Only available for image/PDF, whose current frame already exists as an in-memory
    /// <see cref="System.Drawing.Bitmap"/> — for video this returns null rather than a stale or
    /// fake thumbnail, since producing one for real would mean a GPU-downsampled copy out of the
    /// zero-copy pipeline, which doesn't exist yet (see this project's README). Also null for an
    /// Office document (see <see cref="IsOfficeDocument"/>) — <see cref="_pdfRenderer"/> was never
    /// given that file at all (see <see cref="PlayFile"/>'s Document-kind branch), so its
    /// <c>CurrentFrame</c> would otherwise be null or (worse) a stale frame left over from whatever
    /// PDF played before it; there is nothing to preview anyway since the real content is showing in
    /// WPS's own separate window, not anything this process rendered.
    /// </summary>
    public System.Drawing.Bitmap? CurrentThumbnail => _currentFile?.Kind switch
    {
        MediaKind.Image => _imageRenderer.CurrentFrame,
        MediaKind.Document when !IsOfficeDocument(_currentFile) => _pdfRenderer.CurrentFrame,
        _ => null,
    };

    /// <summary>Extension-based check shared by <see cref="CurrentThumbnail"/>/
    /// <see cref="DocumentPageInfo"/>/<see cref="TryTurnDocumentPage"/>/<see cref="PlayFile"/> — see
    /// <see cref="WpsDocumentController.IsOfficeDocument"/>'s own doc comment for why
    /// <see cref="MediaKind.Document"/> alone isn't enough to tell a PDF from a PPT/Word/Excel file.</summary>
    private static bool IsOfficeDocument(MediaFile file) => WpsDocumentController.IsOfficeDocument(file.SourcePath);

    public PlaybackEngine(OutputStateMachine stateMachine, OverlayWindow overlay, VideoSurface videoSurface, SettingsStore settingsStore, ScenarioStore scenarioStore)
    {
        _stateMachine = stateMachine;
        _overlay = overlay;
        _videoSurface = videoSurface;
        _settingsStore = settingsStore;
        _scenarioStore = scenarioStore;
        _stateMachine.StateChanged += OnOutputStateChanged;
    }

    // Created lazily rather than in the constructor: there's no reason to construct a
    // VideoContentController before this app has ever been asked to play a video, even though the
    // shared VideoSurface itself is constructed eagerly by TerminalApplicationContext (a live
    // device cast can need it before any local video ever plays).
    private VideoContentController VideoController =>
        _videoController ??= new VideoContentController(_videoSurface);

    // Same laziness reasoning as VideoController, but there's no shared GPU resource to justify
    // eager construction the way TerminalApplicationContext eagerly builds _videoSurface for a
    // device cast that might arrive before any local video plays — nothing else in this process
    // needs an AudioContentController to exist before the first standalone-audio file is played.
    // Seeded with _pendingAudioVolume on construction (see AudioVolume's own doc comment) so a
    // volume adjustment made before any audio has ever played isn't silently lost the moment this
    // is finally created.
    private AudioContentController AudioController
    {
        get
        {
            _audioController ??= new AudioContentController { Volume = _pendingAudioVolume };
            return _audioController;
        }
    }

    // Same lazy-construction reasoning as AudioController, kept as a genuinely separate
    // AudioContentController instance rather than reusing AudioController itself — see class doc
    // comment on why a shared instance would let a foreground standalone-audio file and the
    // background overlay track fight over the same playback slot (AudioContentController.Play()
    // always stops whatever it was previously playing first) instead of mixing independently.
    // Volume is left at AudioContentController's own default (1.0, full) — no per-background-track
    // volume control is exposed anywhere yet; see this project's README for that being an accepted,
    // documented scope limit rather than an oversight.
    private AudioContentController BackgroundAudioController => _backgroundAudioController ??= new AudioContentController();

    private float _pendingAudioVolume = 1f;

    /// <summary>Standalone (non-background) audio playback volume, 0.0-1.0 — read/written by
    /// <c>FloatingPreviewWindow</c>'s volume buttons. Tracked here rather than only inside
    /// <see cref="AudioContentController"/> so it has a sensible value (and can be set) even before
    /// any audio has ever played, without forcing <see cref="AudioController"/>'s lazy construction
    /// just to read or write a property; once that controller exists, this delegates straight to its
    /// own <see cref="AudioContentController.Volume"/>, which is what actually persists the value
    /// across separate audio files being played in sequence (see that property's own doc comment).</summary>
    public float AudioVolume
    {
        get => _audioController?.Volume ?? _pendingAudioVolume;
        set
        {
            _pendingAudioVolume = Math.Clamp(value, 0f, 1f);
            if (_audioController != null) _audioController.Volume = _pendingAudioVolume;
        }
    }

    /// <summary>Standalone-audio playback position — PLANNING.md §8.2's audio play-bar "进度"
    /// readout. <see cref="TimeSpan.Zero"/> if nothing has ever played, mirroring
    /// <see cref="AudioContentController.CurrentPosition"/>'s own no-op default — unlike
    /// <see cref="AudioVolume"/>, there's no separate "pending" value to track here: a position only
    /// ever means anything once something has actually started playing.</summary>
    public TimeSpan AudioPosition => _audioController?.CurrentPosition ?? TimeSpan.Zero;

    /// <summary>Best-effort standalone-audio total duration — null if nothing has ever played, or if
    /// <see cref="AudioDecodeSource.TryGetDuration"/> failed for the current file (see that method's
    /// own doc comment for how thoroughly unverified it is).</summary>
    public TimeSpan? AudioDuration => _audioController?.TotalDuration;

    /// <summary>Standalone-audio-only "跳转N秒" (PLANNING.md §8.2's "进度") — no-op for anything else
    /// (background audio, image/document/video), mirroring <see cref="AudioVolume"/>'s own
    /// only-meaningful-for-standalone-audio scope; <c>FloatingPreviewWindow</c>'s seek buttons gate
    /// on the same <c>isStandaloneAudio</c> check its volume/pause buttons already use. Best-effort:
    /// see <see cref="AudioContentController.TrySeekTo"/>'s own doc comment for why this can
    /// silently do nothing (no exception, no visible effect) when the underlying
    /// <see cref="AudioDecodeSource.TrySeek"/> call doesn't work on a given file. Re-pauses
    /// afterward if playback was already paused before seeking — <see cref="AudioContentController"/>
    /// itself has no memory of pause state across rebuilding its internal clock for a seek (see that
    /// class's own doc comment), only this class's own <see cref="IsPaused"/> does.</summary>
    public void SeekAudioRelative(TimeSpan delta)
    {
        if (_currentFile == null || _currentFile.Kind != MediaKind.Audio || _currentFile.IsBackgroundAudio) return;
        if (_audioController == null) return; // nothing has ever played — no position to seek from.

        var target = _audioController.CurrentPosition + delta;
        if (_audioController.TrySeekTo(target) && IsPaused) _audioController.Pause();
    }

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
        if (file.IsBackgroundAudio)
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
        if (file.IsBackgroundAudio)
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
        if (TryTurnDocumentPage(_pdfRenderer.NextPage)) return true;
        return TryAdvance(1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Floating-preview-window "上一项" — same page-before-item precedence as
    /// <see cref="NextManual"/>, in reverse. Returns false at the start of the list (and no further
    /// page to turn back to).</summary>
    public bool PreviousManual()
    {
        if (TryTurnDocumentPage(_pdfRenderer.PreviousPage)) return true;
        return TryAdvance(-1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Shared by <see cref="NextManual"/>/<see cref="PreviousManual"/> — attempts one page
    /// turn via <paramref name="turnPage"/> (<see cref="PdfContentRenderer.NextPage"/> or
    /// <see cref="PdfContentRenderer.PreviousPage"/>) when the current file is a Document, and
    /// updates <see cref="OverlayWindow.ContentSurface"/> to match. Returns false (meaning "nothing
    /// handled here, fall through to TryAdvance") both when the current file isn't a Document and
    /// when there's no further page to turn to in the requested direction — <see cref="NextManual"/>/
    /// <see cref="PreviousManual"/>'s own page-before-item precedence relies on both of those cases
    /// looking identical to the caller.
    ///
    /// Bug fixed here: <paramref name="turnPage"/> calls <c>PdfContentRenderer.RenderCurrentPage</c>
    /// internally, which can throw for a corrupt page within an otherwise-valid PDF (see that
    /// method's own doc comment on its own dispose-before-render fix) — previously nothing here
    /// caught that at all, so it propagated as a completely unreported exception out to whatever UI
    /// button triggered <see cref="NextManual"/>/<see cref="PreviousManual"/>. Routing it through
    /// <see cref="OnImageOrDocumentFailed"/> matches how a whole-document load failure is already
    /// reported, and — just as importantly — clears <see cref="OverlayWindow.ContentSurface"/>'s now-
    /// dangling reference to whatever <see cref="System.Drawing.Bitmap"/> that dispose-before-render
    /// step already freed (see <see cref="OnImageOrDocumentFailed"/>'s own doc comment on that). On a
    /// failure this returns true (handled, don't fall through to TryAdvance) — auto-advancing past a
    /// document that just failed to render its next page would silently abandon it instead of
    /// reporting the problem, which is not how any other content failure in this class behaves.
    ///
    /// Also returns false immediately for an Office document (see <see cref="IsOfficeDocument"/>) —
    /// confirmed with the user: 翻页 for a PPT/Word/Excel file happens directly in WPS's own real
    /// window, never through this class, so <paramref name="turnPage"/> (always
    /// <c>PdfContentRenderer.NextPage</c>/<c>PreviousPage</c>) must never be called for one — it would
    /// silently operate on whatever unrelated PDF that renderer last loaded, or nothing at all.
    /// Falling through to false here means <see cref="NextManual"/>/<see cref="PreviousManual"/>
    /// instead fall through to <see cref="TryAdvance"/>, treating 上一项/下一项 as an ordinary
    /// playlist-navigate action for an Office document, same as any other non-Document kind.</summary>
    private bool TryTurnDocumentPage(Func<bool> turnPage)
    {
        if (_currentFile?.Kind != MediaKind.Document || IsOfficeDocument(_currentFile!)) return false;

        bool turned;
        try
        {
            turned = turnPage();
        }
        catch (Exception ex)
        {
            OnImageOrDocumentFailed(_currentFile!, ex);
            return true;
        }

        if (!turned) return false;
        _overlay.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
        return true;
    }

    /// <summary>Null unless the current file is a Document with more than one page — the floating
    /// preview window uses this to show a "第X页/共Y页" indicator only when page-turning is actually
    /// possible right now, since <see cref="NextManual"/>/<see cref="PreviousManual"/> silently
    /// changed meaning for this case (page-turn instead of playlist-advance) and a user watching the
    /// same two buttons do something different without any visible indicator would be confusing.
    /// 1-based for display (<c>CurrentPage</c> starting at 1, not <see cref="IContentRenderer"/>'s
    /// own 0-based <c>CurrentPageIndex</c>). Also null for an Office document — same reasoning as
    /// <see cref="TryTurnDocumentPage"/>'s own Office check: <see cref="_pdfRenderer"/>'s
    /// <c>PageCount</c> would otherwise reflect whatever unrelated PDF it last loaded, not this
    /// file.</summary>
    public (int CurrentPage, int PageCount)? DocumentPageInfo =>
        _currentFile?.Kind == MediaKind.Document && !IsOfficeDocument(_currentFile!) && _pdfRenderer.PageCount > 1
            ? (_pdfRenderer.CurrentPageIndex + 1, _pdfRenderer.PageCount)
            : null;

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

        int next = _currentFileIndex;
        while (true)
        {
            next += delta;
            if (next < 0 || next >= _currentActivity.Files.Count) return false;

            var candidate = _currentActivity.Files[next];
            if (candidate.IsBackgroundAudio)
            {
                StartOrUpdateBackgroundAudio(candidate);
                continue;
            }

            _currentFileIndex = next;
            PlayFile(candidate, trigger);
            return true;
        }
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
    private PlayMode EffectivePlayMode(MediaFile file) =>
        file.PlayModeOverride ?? _currentActivity?.DefaultPlayMode ?? PlayMode.SequentialAuto;

    private void PlayFile(MediaFile file, PlaybackTrigger trigger)
    {
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        IsPaused = false;
        // Stopping any in-flight standalone-audio playback here (rather than only inside the
        // MediaKind.Audio case below) means switching from audio to an image/video/document — or to
        // a different audio file — always tears down the previous one first, the same way the
        // Image/Document/Video cases below each stop _videoController before taking over. Harmless
        // no-op when nothing was playing (AudioContentController.Stop() guards every field with ?.).
        _audioController?.Stop();
        StopWaveformTimer();
        // Same "always tear down whatever was previously showing first" reasoning as
        // _audioController?.Stop() right above — an open Office document is a real, separate WPS
        // window sitting on the extended monitor (see WpsDocumentController's class doc comment), so
        // switching to ANY new file must close it and hand the monitor back to _overlay, exactly like
        // leaving standalone audio always stops it regardless of what plays next.
        CloseWpsDocumentAndRestoreOverlay();

        bool casting = _stateMachine.RequestLocalFilePlayback();
        if (!casting)
        {
            // Cast switch is off: PLANNING.md §9.1 calls for local-preview-only playback here.
            // That preview lives in the Phase 4 UI's file/activity panels, which don't exist yet —
            // there is nothing to render to today, so this intentionally no-ops rather than
            // guessing at a substitute surface. It's no longer a purely SILENT no-op, though (see
            // this project's README risk #9's "彻底无反馈" complaint) — PlaybackDeclinedByCastSwitch
            // at least lets the UI acknowledge the click happened, even with nothing to show for it.
            PlaybackDeclinedByCastSwitch?.Invoke(file);
            return;
        }

        // A device cast (if any) is about to be preempted by local content — see this event's doc
        // comment and VideoSurface's for why this has to happen before ShowImageSurface/
        // ShowVideoSurface/VideoController below touch the shared surface.
        LocalPlaybackStarting?.Invoke();

        _currentFile = file;
        _playbackLogger.LogPlaybackStarted(file.Id, file.SourcePath, trigger);

        switch (file.Kind)
        {
            case MediaKind.Image:
                _videoController?.Stop();
                _overlay.ShowImageSurface();
                _ = PlayImageAsync(file);
                break;

            case MediaKind.Document:
                _videoController?.Stop();
                if (IsOfficeDocument(file))
                {
                    PlayOfficeDocument(file);
                }
                else
                {
                    _overlay.ShowImageSurface();
                    _ = PlayDocumentAsync(file);
                }
                break;

            case MediaKind.Video:
                _overlay.ShowVideoSurface();
                // -= before += on both, every time: avoids stacking subscriptions across plays
                // without needing a separate "first time?" flag.
                VideoController.PlaybackCompleted -= OnVideoCompleted;
                VideoController.PlaybackCompleted += OnVideoCompleted;
                VideoController.PlaybackFailed -= OnVideoFailed;
                VideoController.PlaybackFailed += OnVideoFailed;
                // Bug fixed here: same shape as PlayStandaloneAudio's own fix (see
                // OnImageOrDocumentFailed's doc comment) — VideoController.Play's own
                // `new VideoDecodeSource(path, ...)` is exactly the same "file deleted/moved/
                // corrupted since being added to an activity" real, reachable failure mode, called
                // synchronously right here with nothing catching it. OnVideoFailed (wired just
                // above) only covers a failure AFTER Play() already started successfully, on the
                // background playback thread — it was never reachable for this synchronous
                // construction failure. Reusing OnImageOrDocumentFailed rather than duplicating its
                // two reporting lines: its ContentSurface.SetFrame(null) call is harmless here
                // (ContentSurface isn't the active surface for video — ShowVideoSurface() was just
                // called above — so clearing it has no visible effect either way).
                try
                {
                    VideoController.Play(file.SourcePath, FadeDurationFor(file));
                }
                catch (Exception ex)
                {
                    OnImageOrDocumentFailed(file, ex);
                }
                break;

            case MediaKind.Audio:
                // file.IsBackgroundAudio is never true here — RequestPlay/TryAdvance both intercept
                // a background-audio file before it can ever reach PlayFile (see class doc comment),
                // so this branch only ever sees the standalone (non-background) half of §6 "音频
                // 特殊性" now.
                _videoController?.Stop();
                _overlay.ShowImageSurface();
                PlayStandaloneAudio(file);
                break;
        }

        FileStarted?.Invoke(file);
    }

    private async Task PlayImageAsync(MediaFile file)
    {
        try
        {
            await _imageRenderer.LoadAsync(file.SourcePath);
            _overlay.ContentSurface.SetFrame(_imageRenderer.CurrentFrame);
            ArmStayDurationTimer(file);
        }
        catch (Exception ex)
        {
            OnImageOrDocumentFailed(file, ex);
        }
    }

    private async Task PlayDocumentAsync(MediaFile file)
    {
        try
        {
            _pdfRenderer.SetTargetSize(_overlay.ContentSurface.ClientSize);
            await _pdfRenderer.LoadAsync(file.SourcePath);
            _overlay.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
            ArmStayDurationTimer(file);
        }
        catch (Exception ex)
        {
            OnImageOrDocumentFailed(file, ex);
        }
    }

    /// <summary>The Office half of <see cref="MediaKind.Document"/> — see
    /// <see cref="WpsDocumentController"/>'s own class doc comment for the whole feature's design and
    /// risk disclosure. Deliberately plain/synchronous, NOT <c>async Task</c> like
    /// <see cref="PlayImageAsync"/>/<see cref="PlayDocumentAsync"/> right above: those two are only
    /// "async" in signature (their underlying <c>LoadAsync</c> calls happen to complete synchronously
    /// today per those renderers' own doc comments) — <see cref="WpsDocumentController.Open"/> is
    /// GENUINELY synchronous/blocking (a real, possibly slow COM automation call, launching an entire
    /// external application), and there is no cheap way to move it off the UI thread without
    /// introducing cross-thread marshaling this class has never needed before (every other renderer
    /// here already runs its real work on the UI thread — see class doc comment). Accepted, disclosed
    /// trade-off: opening (or closing, see <see cref="WpsDocumentController.Close"/>'s own doc comment)
    /// an Office document can visibly freeze the whole EveryStage UI for as long as WPS takes, or
    /// indefinitely if WPS is stuck on a dialog — not a new risk this method introduces, the exact
    /// same one <c>Poc/WpsComInteropSpike</c> already flagged for its own synchronous probe.
    ///
    /// Unlike <see cref="PlayImageAsync"/>/<see cref="PlayDocumentAsync"/>, does not call
    /// <see cref="ArmStayDurationTimer(MediaFile)"/> — deliberate: an auto-advance timer silently
    /// switching away mid-edit (which would call <see cref="CloseWpsDocumentAndRestoreOverlay"/>,
    /// itself possibly blocking on WPS's own unsaved-changes prompt — see
    /// <see cref="WpsDocumentController.Close"/>) is a worse outcome than simply never auto-advancing
    /// away from an open Office document at all. It stays open until the operator navigates away
    /// manually (or completion-driven auto-advance, if PLANNING.md ever wants that for this kind, is
    /// deliberately not built here).</summary>
    private void PlayOfficeDocument(MediaFile file)
    {
        try
        {
            _overlay.HideOverlay(); // cede the extended monitor to WPS's own real window — see WpsDocumentController's class doc comment.
            _wpsController = new WpsDocumentController();
            _wpsController.Open(file.SourcePath, _overlay.Monitor.Bounds);
        }
        catch (Exception ex)
        {
            _wpsController?.Close();
            _wpsController = null;
            _overlay.ShowOverlay(); // the open attempt failed — nothing is covering the monitor, so don't leave it hidden.
            OnImageOrDocumentFailed(file, ex);
        }
    }

    /// <summary>Shared by <see cref="PlayImageAsync"/>/<see cref="PlayDocumentAsync"/> — until this
    /// existed, a <see cref="ImageContentRenderer.LoadAsync"/>/<see cref="PdfContentRenderer.LoadAsync"/>
    /// failure (the file was deleted/moved/corrupted since being added to an activity — a real,
    /// reachable condition, not hypothetical: nothing re-verifies a file still exists/decodes at
    /// click-time) had NOTHING catching it at all: <c>PlayFile</c> calls these two methods
    /// fire-and-forget (<c>_ = PlayImageAsync(file);</c>), so the exception vanished into an
    /// unobserved <see cref="Task"/> with no <c>AppDomain.UnhandledException</c>/
    /// <c>TaskScheduler.UnobservedTaskException</c> handler anywhere in this app to even log it. Worse
    /// than that: <see cref="ArmStayDurationTimer(MediaFile)"/> is the ONLY thing that ever arms
    /// <c>HandleCompletion</c> for image/document content, so a failure here left
    /// <see cref="PlayMode.SequentialAuto"/>'s auto-advance silently stalled forever on that file —
    /// the same "queue quietly stops progressing" shape as the background-audio bug this project's
    /// README already documents (risk #84), except that one at least never claimed to have started
    /// anything, where this one already showed the file as playing (<see cref="FileStarted"/> fires
    /// for it below) and then just stopped making progress with zero signal. Mirrors
    /// <see cref="OnVideoFailed"/>/<see cref="OnAudioFailed"/>'s existing
    /// <c>LogAbnormalInterruption</c> + <see cref="PlaybackAbnormallyInterrupted"/> handling exactly —
    /// unlike those two (raised from a genuinely separate background playback thread, hence their own
    /// <c>_overlay.BeginInvoke</c> marshaling), this runs on whatever <c>SynchronizationContext</c>
    /// the initial <c>await</c> captured, which in this WinForms app's message loop is the UI thread
    /// itself (same reason the non-exceptional path above already calls
    /// <see cref="OverlayWindow.ContentSurface"/>/<see cref="ArmStayDurationTimer(MediaFile)"/>
    /// directly with no marshaling of its own) — so no <c>BeginInvoke</c> is needed here either.
    ///
    /// Also called (despite the name) from <see cref="PlayStandaloneAudio"/>'s own try/catch for a
    /// synchronous <c>AudioContentController.Play</c> construction failure — see that method's doc
    /// comment for why this wasn't worth a rename for one more caller whose actual needs (clear
    /// <c>ContentSurface</c>, log, raise <see cref="PlaybackAbnormallyInterrupted"/>) are identical.
    /// Now also called from <see cref="PlayOfficeDocument"/>'s own catch — clearing
    /// <c>ContentSurface</c> there is a harmless no-op (an Office document never draws into it in the
    /// first place), so nothing about this method needed changing for that third caller either.</summary>
    private void OnImageOrDocumentFailed(MediaFile file, Exception ex)
    {
        if (!ReferenceEquals(file, _currentFile)) return; // stale — we've since moved on.

        // Bug fixed here: PdfContentRenderer/ImageContentRenderer's own LoadAsync (see their doc
        // comments) already correctly leaves CurrentFrame as null after a failed load, rather than
        // a dangling reference to the Bitmap it just disposed — but ContentSurface never learns
        // about that on its own. ContentSurface.SetFrame stores whatever reference it was last
        // given directly, independent of whoever created it, and the failing renderer's own
        // CurrentFrame?.Dispose() call at the top of LoadAsync disposes exactly the Bitmap
        // ContentSurface is still holding onto from the last successful SetFrame call — without
        // this, ContentSurface.OnPaint would try to draw that now-disposed Bitmap on its very next
        // repaint (a window move, minimize/restore, anything that invalidates it — not a rare
        // event), throwing repeatedly until some later file loads successfully and calls SetFrame
        // again. Clearing to null here is safe: ContentSurface.OnPaint already treats null as
        // "nothing to draw, just show black".
        _overlay.ContentSurface.SetFrame(null);

        _playbackLogger.LogAbnormalInterruption(file.Id, ex.Message);
        RaisePlaybackAbnormallyInterrupted(file, ex.Message);
    }

    /// <summary>PLANNING.md §6's per-file "淡入/淡出时长 + 音量是否随渐变" — <see cref="MediaFile.VolumeFollowsFade"/>
    /// gates the whole feature (matching its name: volume only "follows" the fade when this file
    /// asks for that), and <see cref="MediaFile.FadeDuration"/> supplies how long. Returns null
    /// (meaning "no fade in/out") whenever either half is missing, so
    /// <see cref="AudioContentController.Play"/>/<see cref="VideoContentController.Play"/> don't
    /// each need to re-derive this same two-field check. Renamed from the original
    /// <c>FadeInDurationFor</c> (dropped "In") once fade-out started being attempted too, from the
    /// exact same duration value — see <see cref="AudioContentController"/>'s class doc comment for
    /// how the two combine, and for fade-out's own considerable, explicitly flagged risk.</summary>
    private static TimeSpan? FadeDurationFor(MediaFile file) =>
        file.VolumeFollowsFade ? file.FadeDuration : null;

    /// <summary>Standalone (non-background) audio playback — <see cref="MediaFile.IsBackgroundAudio"/>
    /// files never reach this method at all (intercepted upstream, see class doc comment and
    /// <see cref="PlayBackgroundAudio"/> for what they get instead). Presents through
    /// <see cref="OverlayWindow.ContentSurface"/>
    /// (the image path, not <see cref="VideoSurface"/>) since <see cref="AudioVisualRenderer"/>
    /// produces plain GDI+ <see cref="System.Drawing.Bitmap"/>s, not D3D11 textures — there's no
    /// zero-copy pipeline to route audio-only content through.</summary>
    private void PlayStandaloneAudio(MediaFile file)
    {
        // -= before += on all three, every time: same "avoid stacking subscriptions across plays"
        // reasoning as the video case above.
        AudioController.PlaybackCompleted -= OnAudioCompleted;
        AudioController.PlaybackCompleted += OnAudioCompleted;
        AudioController.PlaybackFailed -= OnAudioFailed;
        AudioController.PlaybackFailed += OnAudioFailed;
        AudioController.LevelChanged -= OnAudioLevelChanged;
        AudioController.LevelChanged += OnAudioLevelChanged;

        // Bug fixed here: unlike PlayImageAsync/PlayDocumentAsync (each wrapped in their own
        // try/catch specifically for this reason — see OnImageOrDocumentFailed's own doc comment),
        // nothing ever caught a failure from this method's own two calls. AudioController.Play's
        // own `new AudioDecodeSource(path)` is exactly the same "the file was deleted/moved/
        // corrupted since being added to an activity" real, reachable failure mode as
        // ImageContentRenderer/PdfContentRenderer's LoadAsync — the only difference is this method
        // is synchronous, so an uncaught exception here propagated all the way out of PlayFile
        // itself instead of vanishing into an unobserved Task. AudioController.PlaybackFailed
        // (OnAudioFailed, wired above) only covers a failure AFTER Play() already started
        // successfully, on the background playback thread — it was never reachable for a
        // synchronous construction failure like this one. Reusing OnImageOrDocumentFailed here
        // rather than inventing a parallel "audio load failed" path: its actual behavior (clear
        // ContentSurface, log, raise PlaybackAbnormallyInterrupted) is exactly what a standalone-
        // audio load failure needs too, despite the name — same "not worth a rename for one more
        // caller" reasoning EveryStage.Transport.RtpVideoClock's own doc comment already gives for
        // an analogous situation.
        try
        {
            ApplyAudioVisual(file);
            AudioController.Play(file.SourcePath, FadeDurationFor(file));
        }
        catch (Exception ex)
        {
            OnImageOrDocumentFailed(file, ex);
        }
    }

    /// <summary>Entry point for <see cref="RequestPlay(Activity, int, PlaybackTrigger)"/>/
    /// <see cref="RequestPlay(MediaFile, PlaybackTrigger)"/>/<see cref="TryAdvance"/> reaching a
    /// background-audio file — starts it via <see cref="PlayBackgroundAudio"/> unless it's already
    /// the one playing, in which case this is a no-op. That guard matters more than it might look:
    /// <see cref="TryAdvance"/> calls this every time navigation passes back and forth over the same
    /// background-audio slot (e.g. 上一项/下一项 crossing it repeatedly) — without it, the overlay
    /// track would restart from the beginning on every single pass instead of continuing
    /// uninterrupted underneath whatever else changes, which is the entire point PLANNING.md's "叠加
    /// 在其他视觉内容之上播放" is describing.</summary>
    private void StartOrUpdateBackgroundAudio(MediaFile file)
    {
        if (ReferenceEquals(file, _backgroundAudioFile)) return;
        PlayBackgroundAudio(file);
    }

    /// <summary>Actually (re)starts the background-audio overlay track — separated from
    /// <see cref="StartOrUpdateBackgroundAudio"/>'s "only if not already playing" guard because
    /// <see cref="OnBackgroundAudioCompleted"/>'s own <see cref="CompletionAction.Loop"/> handling
    /// needs to restart the SAME file, which that guard would otherwise treat as a no-op.
    ///
    /// <see cref="BackgroundAudioController.Play"/>'s own <c>new AudioDecodeSource(path)</c> is the
    /// same "file deleted/moved/corrupted since being added" real, reachable failure mode this
    /// class's other <c>Play</c> call sites already guard against (see
    /// <see cref="PlayStandaloneAudio"/>'s own doc comment) — caught here the same way, logged and
    /// reported via <see cref="PlaybackAbnormallyInterrupted"/>, except this does NOT route through
    /// <see cref="OnImageOrDocumentFailed"/>: that method also touches
    /// <see cref="OverlayWindow.ContentSurface"/>, which has nothing to do with an overlay audio
    /// track that was never shown on the visual surface at all.</summary>
    private void PlayBackgroundAudio(MediaFile file)
    {
        _backgroundAudioFile = file;
        // -= before += every time: same "avoid stacking subscriptions across plays" reasoning as
        // every other controller this class subscribes to.
        BackgroundAudioController.PlaybackCompleted -= OnBackgroundAudioCompleted;
        BackgroundAudioController.PlaybackCompleted += OnBackgroundAudioCompleted;
        BackgroundAudioController.PlaybackFailed -= OnBackgroundAudioFailed;
        BackgroundAudioController.PlaybackFailed += OnBackgroundAudioFailed;

        try
        {
            BackgroundAudioController.Play(file.SourcePath, FadeDurationFor(file));
        }
        catch (Exception ex)
        {
            _backgroundAudioFile = null;
            _playbackLogger.LogAbnormalInterruption(file.Id, ex.Message);
            RaisePlaybackAbnormallyInterrupted(file, ex.Message);
        }
    }

    /// <summary>PLANNING.md doesn't say what any <see cref="MediaFile.OnCompletion"/> value should
    /// mean for a background-audio file — it was never part of the visible sequence
    /// <see cref="CompletionAction.NextItem"/>/<see cref="CompletionAction.HoldOnLastFrame"/> are
    /// framed around in the first place (see class doc comment). This repository's own choice, made
    /// here rather than left unresolved a second time: <see cref="CompletionAction.Loop"/> restarts
    /// the same file via <see cref="PlayBackgroundAudio"/> (bypassing
    /// <see cref="StartOrUpdateBackgroundAudio"/>'s "already playing" guard on purpose — this is
    /// exactly the one case that needs to restart the same file) — matching what "loop" plainly says
    /// regardless of context, and the one interpretation every other <see cref="MediaFile"/> kind
    /// already gives it. Anything else (<see cref="CompletionAction.NextItem"/>,
    /// <see cref="CompletionAction.HoldOnLastFrame"/>) simply lets the track end and stay silent —
    /// there is no "next" for a file with no position in the sequence, and re-purposing NextItem to
    /// mean something else here would be inventing behavior PLANNING.md never described, not
    /// implementing something it did.</summary>
    private void OnBackgroundAudioCompleted()
    {
        var file = _backgroundAudioFile;
        if (file == null) return;
        // Raised from AudioContentController's background playback thread — marshal before touching
        // this class's own state, same reasoning as OnAudioCompleted/OnVideoCompleted.
        _overlay.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(file, _backgroundAudioFile)) return; // stale — replaced/stopped since.
            _playbackLogger.LogPlaybackEnded(file.Id, file.OnCompletion.ToString());
            if (file.OnCompletion == CompletionAction.Loop)
                PlayBackgroundAudio(file);
            else
                _backgroundAudioFile = null;
        }));
    }

    private void OnBackgroundAudioFailed(Exception ex)
    {
        var file = _backgroundAudioFile;
        if (file == null) return;
        // Also raised from the background playback thread.
        _overlay.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(file, _backgroundAudioFile)) return; // stale — replaced/stopped since.
            _backgroundAudioFile = null;
            _playbackLogger.LogAbnormalInterruption(file.Id, ex.Message);
            RaisePlaybackAbnormallyInterrupted(file, ex.Message);
        }));
    }

    /// <summary>Sets up whichever of the three <see cref="AudioVisual"/> options this file asks for.
    /// <see cref="AudioVisual.Black"/> needs nothing beyond clearing the surface — see
    /// <see cref="AudioVisualRenderer"/>'s doc comment for why the other two are placeholders rather
    /// than a real default-image asset / true scrolling waveform.</summary>
    private void ApplyAudioVisual(MediaFile file)
    {
        _audioVisualFrame?.Dispose();
        _audioVisualFrame = null;

        switch (file.BackgroundAudioVisual)
        {
            case AudioVisual.Black:
                _overlay.ContentSurface.SetFrame(null);
                break;

            case AudioVisual.DefaultBackgroundImage:
                _audioVisualFrame = AudioVisualRenderer.CreateDefaultBackgroundFrame(_overlay.ContentSurface.ClientSize, file.SourcePath);
                _overlay.ContentSurface.SetFrame(_audioVisualFrame);
                break;

            case AudioVisual.Waveform:
                // Redrawn on a fixed UI-thread timer rather than once per LevelChanged callback —
                // LevelChanged fires from AudioContentController's background thread at whatever
                // rate MF hands back PCM chunks (potentially far faster than any display needs),
                // and OnAudioLevelChanged deliberately does nothing but record the latest value for
                // this timer to pick up (see that method's doc comment). ~15fps is plenty for a
                // level meter and keeps GDI+ Bitmap allocation off the hot decode path entirely.
                _latestAudioLevel = 0f;
                _waveformTimer = new System.Windows.Forms.Timer { Interval = 66 };
                _waveformTimer.Tick += (_, _) => RedrawWaveformFrame();
                _waveformTimer.Start();
                RedrawWaveformFrame(); // paint an initial (silent) frame now, not just on the first tick.
                break;
        }
    }

    private void RedrawWaveformFrame()
    {
        // Bug fixed here: same shape as PdfContentRenderer/ImageContentRenderer.LoadAsync's own
        // fix this round (see this project's README) — disposing _audioVisualFrame without also
        // nulling it before the CreateWaveformFrame call below meant a failure there (GDI+
        // Bitmap/Graphics allocation is rare but not impossible to fail, e.g. under real memory
        // pressure) would leave _audioVisualFrame pointing at an already-disposed Bitmap instead of
        // null. Unlike a one-shot LoadAsync, this method reruns on every _waveformTimer tick
        // (~15fps) for as long as a Waveform-visual audio file keeps playing, so a failure here
        // wouldn't be a one-time event — the very next tick would call Dispose() again on the same
        // stale reference (harmless — Bitmap.Dispose() is idempotent) and keep retrying.
        _audioVisualFrame?.Dispose();
        _audioVisualFrame = null;

        // Second bug fixed here, found on a later pass over this exact method: the fix above only
        // protected _audioVisualFrame's OWN field — it left ContentSurface itself still holding a
        // direct reference to the Bitmap just disposed above, from the last successful SetFrame
        // call. If CreateWaveformFrame throws, execution never reaches SetFrame below, so
        // ContentSurface.OnPaint would keep trying to draw that now-disposed Bitmap on every
        // subsequent repaint — same root cause, same fix (see OnImageOrDocumentFailed's own doc
        // comment on this exact "renderer's own state is fixed but a downstream consumer's stale
        // reference isn't" shape), just not applied here the first time around.
        try
        {
            _audioVisualFrame = AudioVisualRenderer.CreateWaveformFrame(_overlay.ContentSurface.ClientSize, _latestAudioLevel);
        }
        catch (Exception)
        {
            _overlay.ContentSurface.SetFrame(null);
            return;
        }

        _overlay.ContentSurface.SetFrame(_audioVisualFrame);
    }

    private void StopWaveformTimer()
    {
        _waveformTimer?.Stop();
        _waveformTimer?.Dispose();
        _waveformTimer = null;
    }

    private void OnAudioCompleted()
    {
        var file = _currentFile;
        if (file == null) return;
        // Raised from AudioContentController's background playback thread — same marshal-before-
        // touching-timers/overlay-state reasoning as OnVideoCompleted.
        _overlay.BeginInvoke(new Action(() => HandleCompletion(file)));
    }

    private void OnAudioFailed(Exception ex)
    {
        var file = _currentFile;
        if (file == null) return;
        // Also raised from the background playback thread.
        _overlay.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(file, _currentFile)) return; // stale — we've since moved on.
            _playbackLogger.LogAbnormalInterruption(file.Id, ex.Message);
            // Same PlaybackAbnormallyInterrupted reasoning as OnVideoFailed.
            RaisePlaybackAbnormallyInterrupted(file, ex.Message);
        }));
    }

    private void OnAudioLevelChanged(float level)
    {
        // Raised from AudioContentController's background playback thread, once per decoded PCM
        // chunk — potentially much more often than any display needs. Deliberately does nothing but
        // record the value: _waveformTimer's UI-thread Tick (see ApplyAudioVisual) is what actually
        // touches Bitmaps/ContentSurface, at a bounded ~15fps regardless of how fast chunks arrive.
        _latestAudioLevel = level;
    }

    private void ArmStayDurationTimer(MediaFile file) => ArmStayDurationTimer(file, file.StayDuration ?? DefaultStayDurationOrZero(), isFreshStart: true);

    /// <summary>The 设置 面板's "默认停留时长" (<c>AppSettings.DefaultStayDurationSeconds</c>), read
    /// fresh every time rather than cached — a change saved through <c>SettingsPanel</c> applies to
    /// the very next file played, no restart needed. Falls back to <see cref="TimeSpan.Zero"/> (this
    /// method's existing meaning: "hold indefinitely", see <see cref="ArmStayDurationTimer(MediaFile, TimeSpan, bool)"/>)
    /// when the setting isn't configured, preserving the pre-settings behavior exactly.</summary>
    private TimeSpan DefaultStayDurationOrZero()
    {
        int? seconds = _settingsStore.Current.DefaultStayDurationSeconds;
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : TimeSpan.Zero;
    }

    private void ArmStayDurationTimer(MediaFile file, TimeSpan duration, bool isFreshStart)
    {
        if (duration <= TimeSpan.Zero)
        {
            if (isFreshStart) return; // no stay duration configured at all — nothing to arm.
            HandleCompletion(file); // resumed with ~0 remaining — it was already due.
            return;
        }

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)duration.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HandleCompletion(file);
        };
        _stayDurationTimer = timer;
        _stayDurationArmedAt = DateTime.UtcNow;
        _stayDurationTotal = duration;
        timer.Start();
    }

    /// <summary>
    /// Floating-preview-window "暂停" (PLANNING.md §8.3), with genuinely different mechanisms
    /// depending on <see cref="_currentFile"/>'s kind: for image/PDF content, freezes the
    /// stay-duration auto-advance clock in place; for standalone (non-background) audio and now
    /// video too, a real pause-in-place via <see cref="AudioContentController.Pause"/> /
    /// <see cref="VideoContentController.Pause"/> (see either method's own doc comment for why this
    /// is safe — video's own pacing loop turns out to be driven entirely by the same
    /// <see cref="AudioPlaybackClock"/> mechanism audio-only playback already used, once someone
    /// actually re-read <see cref="VideoContentController.RunPlaybackLoopCore"/> looking for it,
    /// rather than the "would need real suspend/resume support it doesn't have" assumption this
    /// method's doc comment carried for a long time before that).
    /// </summary>
    public void Pause()
    {
        if (IsPaused || _currentFile == null) return;

        // The !IsBackgroundAudio half of this condition can never actually be false any more —
        // RequestPlay/TryAdvance now intercept a background-audio file before it ever reaches
        // PlayFile at all (see class doc comment), so _currentFile can never legitimately BE one.
        // Left in place anyway as an explicit, harmless invariant check rather than relying on
        // "this can't happen" silently: this method deliberately does NOT touch
        // BackgroundAudioController at all (see class doc comment on why Pause/Resume stay scoped
        // to the foreground _currentFile only), so if that invariant were ever violated by some
        // future change, the correct behavior here is still "do nothing to the overlay track", not
        // an accidental pause of it.
        if (_currentFile.Kind == MediaKind.Audio && !_currentFile.IsBackgroundAudio)
        {
            _audioController?.Pause();
            IsPaused = true;
            return;
        }

        if (_currentFile.Kind == MediaKind.Video)
        {
            _videoController?.Pause();
            IsPaused = true;
            return;
        }

        // Reaches here for Image/Document _currentFile — _stayDurationTimer is null if none was
        // ever armed (e.g. "停留时长" set to hold indefinitely), so this correctly no-ops rather
        // than touching _remainingOnPause/IsPaused for state that was never really counting down.
        if (_stayDurationTimer == null) return;

        TimeSpan elapsed = DateTime.UtcNow - _stayDurationArmedAt;
        TimeSpan remaining = _stayDurationTotal - elapsed;
        _remainingOnPause = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;

        _stayDurationTimer.Stop();
        IsPaused = true;
    }

    /// <summary>Resumes whatever <see cref="Pause"/> paused — a stay-duration countdown from where
    /// it left off, or (for standalone audio/video) real WASAPI-clock-paced playback via
    /// <see cref="AudioContentController.Resume"/> / <see cref="VideoContentController.Resume"/>.
    /// No-op if nothing is paused.</summary>
    public void Resume()
    {
        if (!IsPaused || _currentFile == null) return;
        IsPaused = false;

        if (_currentFile.Kind == MediaKind.Audio && !_currentFile.IsBackgroundAudio)
        {
            _audioController?.Resume();
            return;
        }

        if (_currentFile.Kind == MediaKind.Video)
        {
            _videoController?.Resume();
            return;
        }

        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        ArmStayDurationTimer(_currentFile, _remainingOnPause, isFreshStart: false);
    }

    private TimeSpan _remainingOnPause;

    private void OnVideoCompleted()
    {
        var file = _currentFile;
        if (file == null) return;
        // Raised from VideoContentController's background playback thread — marshal to the UI
        // thread before touching timers/overlay state.
        _overlay.BeginInvoke(new Action(() => HandleCompletion(file)));
    }

    private void OnVideoFailed(Exception ex)
    {
        var file = _currentFile;
        if (file == null) return;
        // Also raised from the background playback thread.
        _overlay.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(file, _currentFile)) return; // stale — we've since moved on.
            _playbackLogger.LogAbnormalInterruption(file.Id, ex.Message);
            // PLANNING.md §11's Toast "涉及播放的异常需带可执行按钮（重试/移除）" — see
            // PlaybackAbnormallyInterrupted's own doc comment for what built that UI on top of this.
            RaisePlaybackAbnormallyInterrupted(file, ex.Message);
        }));
    }

    /// <summary>Bug fixed here: <see cref="PlaybackAbnormallyInterrupted"/> now genuinely has two
    /// subscribers (<c>MainWindow</c>'s Toast handler and <c>FloatingPreviewWindow</c>'s own
    /// refresh — see that window's doc comment on why it needed to subscribe), so a plain
    /// <c>PlaybackAbnormallyInterrupted?.Invoke(...)</c> call is no longer safe: if the first
    /// subscriber invoked throws (e.g. a bug in the Toast UI's own construction), the second one
    /// never runs, AND the exception propagates back out of whichever of this class's own
    /// try/catch blocks called this method (<see cref="OnImageOrDocumentFailed"/>/
    /// <see cref="PlayStandaloneAudio"/>'s and the Video case in <see cref="PlayFile"/>'s own
    /// try/catch, plus <see cref="OnAudioFailed"/>/<see cref="OnVideoFailed"/>'s <c>BeginInvoke</c>
    /// callbacks) — defeating the exact protection those try/catch blocks exist to provide. Same
    /// per-subscriber isolation <see cref="StateMachine.OutputStateMachine.RaiseStateChanged"/>/
    /// <c>RaiseCastSwitchChanged</c> already established in this codebase for the identical
    /// shape.</summary>
    private void RaisePlaybackAbnormallyInterrupted(MediaFile file, string message)
    {
        foreach (var handler in PlaybackAbnormallyInterrupted?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                ((Action<MediaFile, string>)handler)(file, message);
            }
            catch (Exception)
            {
                // Deliberately swallowed, same reasoning as OutputStateMachine's own identical
                // catch: this class has no way to attribute the failure to a specific subscriber to
                // log it usefully, and the one guarantee this method exists to provide is that one
                // subscriber's bug can't cost every other subscriber their notification too.
            }
        }
    }

    private void HandleCompletion(MediaFile file)
    {
        if (!ReferenceEquals(file, _currentFile)) return; // stale callback from a file we've since left.

        _playbackLogger.LogPlaybackEnded(file.Id, file.OnCompletion.ToString());

        switch (file.OnCompletion)
        {
            case CompletionAction.NextItem:
                // PlayMode.ManualSelect means the operator picks what plays next, not
                // CompletionAction.NextItem — so this holds on the current frame exactly like
                // HoldOnLastFrame instead of auto-advancing, until a manual NextManual()/
                // PreviousManual() (from the floating preview window) or a fresh RequestPlay moves
                // on. This is the first real behavior PlayMode/PlayModeOverride/DefaultPlayMode
                // have ever had — previously nothing in this class read them at all.
                // TryAdvanceToNextActivity only when TryAdvance itself found nothing further left in
                // THIS activity — see that method's own doc comment for why it's only ever reached
                // from here (never from NextManual/PreviousManual's ManualSkip trigger).
                if (EffectivePlayMode(file) == PlayMode.SequentialAuto && !TryAdvance(1, PlaybackTrigger.ActivityAuto))
                    TryAdvanceToNextActivity(); // no-op (holds) if nothing further anywhere — see that method's own doc comment.
                break;
            case CompletionAction.Loop:
                PlayFile(file, PlaybackTrigger.ActivityAuto);
                break;
            case CompletionAction.HoldOnLastFrame:
                break; // leave the current frame/video's last frame displayed.
        }
    }

    /// <summary>Stops any current local-file playback so an incoming device cast can safely start
    /// presenting through the shared <see cref="VideoSurface"/> — called by
    /// <c>TerminalApplicationContext</c> before constructing a <c>CastReceiver</c>. Unlike
    /// <see cref="OnOutputStateChanged"/>'s Idle handling, this deliberately does NOT go through
    /// <c>OutputStateMachine</c> at all: PLANNING.md treats an incoming device cast as continuing to
    /// output (§9.1 "设备投屏请求不受此开关影响"), not a transition back to Idle, so the state
    /// machine's state must stay Active — only the video surface's owner is changing. Safe to call
    /// when nothing is currently playing. Also stops the background-audio overlay track, if one is
    /// playing — PLANNING.md's "叠加在其他视觉内容之上播放" only makes sense while local content is
    /// what's actually on the extended display; once a device cast owns that display, there is
    /// nothing left for it to overlay. Also closes any open Office document (see
    /// <see cref="CloseWpsDocumentAndRestoreOverlay"/>) for the same reason, and — unlike
    /// <see cref="OnOutputStateChanged"/>'s Idle branch below — explicitly needs the overlay-restoring
    /// variant: <c>OutputStateMachine.AcceptDeviceCastRequest</c> is a no-op re-affirmation when the
    /// state is already Active (which it is here, or an Office document couldn't have been open in
    /// the first place), so its own <c>StateChanged</c> re-fire that would otherwise re-show
    /// <c>OverlayWindow</c> never happens — this call is the only thing that would.</summary>
    public void StopForDeviceCast()
    {
        if (_currentFile != null)
        {
            _playbackLogger.LogPlaybackEnded(_currentFile.Id, "preempted_by_device_cast");
            _currentFile = null;
        }
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        IsPaused = false;
        _videoController?.Stop();
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        CloseWpsDocumentAndRestoreOverlay();
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state != OutputState.Idle) return;

        // "断": stop actively decoding — nothing is on screen to show it to — but do not tear down
        // the video swap chain/device (PLANNING.md §9.2's "预先创建并常驻" applies to the whole
        // video pipeline, not just the overlay window). Resuming after "断" starts over via a new
        // RequestPlay; there is no documented "resume from where it left off" behavior. Same
        // "nothing left to overlay" reasoning as StopForDeviceCast for stopping the background-audio
        // track too — "断" means nothing is showing on the extended display at all.
        if (_currentFile != null)
        {
            _playbackLogger.LogPlaybackEnded(_currentFile.Id, "disconnected");
            _currentFile = null;
        }
        _stayDurationTimer?.Stop();
        _videoController?.Stop();
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        // CloseWpsDocumentIfOpen, NOT CloseWpsDocumentAndRestoreOverlay — this branch runs as part of
        // "断" going Idle, whose whole point is hiding everything; TerminalApplicationContext's own
        // separate StateChanged subscriber calls OverlayWindow.HideOverlay() for exactly this
        // transition, and re-showing it from here would fight that (order between the two subscribers
        // is not this class's to rely on either way — see this method's own doc comment history).
        CloseWpsDocumentIfOpen();
    }

    private void StopBackgroundAudio()
    {
        if (_backgroundAudioFile == null) return;
        _playbackLogger.LogPlaybackEnded(_backgroundAudioFile.Id, "background_audio_stopped");
        _backgroundAudioController?.Stop();
        _backgroundAudioFile = null;
    }

    /// <summary>Pure cleanup, no <see cref="OverlayWindow"/> interaction — see
    /// <see cref="CloseWpsDocumentAndRestoreOverlay"/>'s own doc comment for when each of this pair is
    /// the right one to call. No-op when nothing is open.</summary>
    private void CloseWpsDocumentIfOpen()
    {
        if (_wpsController is not { IsOpen: true }) return;
        _wpsController.Close();
        _wpsController = null;
    }

    /// <summary><see cref="PlayFile"/>'s own "always tear down whatever was previously showing before
    /// switching to something new" step (mirrors <c>_audioController?.Stop()</c> right next to its
    /// call site) and <see cref="StopForDeviceCast"/>'s equivalent — both cases where local playback
    /// keeps going (just as something else), so the <see cref="OverlayWindow"/> that an open Office
    /// document had hidden (see <see cref="PlayOfficeDocument"/>) needs to come back for whatever
    /// plays next. Deliberately NOT used by <see cref="OnOutputStateChanged"/>'s Idle branch or
    /// <see cref="Dispose"/> — those two want the overlay left however their own, separate teardown
    /// path leaves it (hidden, or simply going away), never re-shown by this class — see
    /// <see cref="CloseWpsDocumentIfOpen"/> for that half.</summary>
    private void CloseWpsDocumentAndRestoreOverlay()
    {
        if (_wpsController is not { IsOpen: true }) return;
        _wpsController.Close();
        _wpsController = null;
        _overlay.ShowOverlay();
    }

    /// <summary>Floating-preview-window "保存" button — PLANNING.md §14.1 names 保存 as one of the
    /// four things "文档可编辑" requires direct WPS object-model control for (打开/翻页/编辑/保存);
    /// 打开 happens in <see cref="PlayOfficeDocument"/>, 翻页/编辑 were confirmed with the user to be
    /// left entirely to WPS's own real, visible window (see <see cref="WpsDocumentController"/>'s
    /// class doc comment), leaving this as the one piece of the four still worth wiring through
    /// EveryStage's own UI. An operator can always use WPS's own Ctrl+S directly too — this is a
    /// convenience, not the only way to save. False (never throws) whenever nothing is open, or on
    /// any <see cref="WpsDocumentController.TrySave"/> failure — see that method's own doc comment for
    /// why this can fail silently against a real WPS install this has never been tested against.</summary>
    public bool TrySaveCurrentOfficeDocument() => _wpsController?.TrySave() ?? false;

    public void Dispose()
    {
        _stateMachine.StateChanged -= OnOutputStateChanged;
        _stayDurationTimer?.Dispose();
        StopWaveformTimer();
        _audioVisualFrame?.Dispose();
        _imageRenderer.Dispose();
        _pdfRenderer.Dispose();
        _videoController?.Dispose();
        _audioController?.Dispose();
        _backgroundAudioController?.Dispose();
        CloseWpsDocumentIfOpen();
    }
}
