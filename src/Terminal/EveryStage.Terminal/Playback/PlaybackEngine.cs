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
/// When the cast switch is off, <see cref="MainWindow"/> routes a direct file click to the local
/// Windows file handler; this engine therefore only owns the managed extended-display path.
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
/// rendered into <see cref="_output"/>, and the specific product decisions confirmed with the user
/// before writing it) and <see cref="PlayOfficeDocument"/>/<see cref="CloseWpsDocumentAndRestoreOverlay"/>
/// for how it's wired into this class's own lifecycle.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly OutputStateMachine? _stateMachine; // Program channel only
    private readonly IPlaybackOutput _output;
    // Null when the GPU/video surface couldn't be created (Preview on a machine without usable D3D11):
    // images, documents and audio still preview; video fails with a clear error card.
    private readonly VideoSurface? _videoSurface;
    private EveryStage.Rendering.VideoScaleMode _videoScaleMode = EveryStage.Rendering.VideoScaleMode.Fit;
    private readonly SettingsStore _settingsStore;
    // Only ever read by TryAdvanceToNextActivity — see that method's own doc comment for why this
    // class otherwise has no reason to know about Scenarios at all (everything else it does is
    // scoped to a single Activity's file list, handed in via RequestPlay).
    private readonly ScenarioStore _scenarioStore;
    private readonly ImageContentRenderer _imageRenderer = new();
    private int _contentLoadVersion;
    private readonly PdfContentRenderer _pdfRenderer = new();
    private readonly PlaybackLogger _playbackLogger;
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
        MediaKind.Document when !IsOfficeDocument(_currentFile!) => _pdfRenderer.CurrentFrame,
        _ => null,
    };

    /// <summary>Extension-based check shared by <see cref="CurrentThumbnail"/>/
    /// <see cref="DocumentPageInfo"/>/<see cref="TryTurnDocumentPage"/>/<see cref="PlayFile"/> — see
    /// <see cref="WpsDocumentController.IsOfficeDocument"/>'s own doc comment for why
    /// <see cref="MediaKind.Document"/> alone isn't enough to tell a PDF from a PPT/Word/Excel file.</summary>
    private static bool IsOfficeDocument(MediaFile file) => WpsDocumentController.IsOfficeDocument(file.SourcePath);

    // ───────────── Channel & shared state model (Preview / Program) ─────────────

    /// <summary>Which channel this instance drives. Preview has no output gate and no state machine;
    /// Program only plays while <see cref="OutputStateMachine"/> allows local output.</summary>
    public PlaybackChannel Channel { get; }

    public PlaybackChannelState State { get; private set; } = PlaybackChannelState.Idle;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<PlaybackChannelState>? StateChanged;

    /// <summary>Details of the most recent failure (cleared when a new file starts).</summary>
    public PlaybackError? LastError { get; private set; }

    /// <summary>True once the current file finished with nothing further to play (held on its last
    /// frame). <see cref="Resume"/> then replays it from the start.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>Loop the current file regardless of its saved completion action (the transport bar's
    /// loop toggle; not persisted).</summary>
    public bool LoopCurrent { get; set; }

    private bool _muted;

    /// <summary>Silences this channel's audio without stopping it. Preview is muted while Program is live
    /// so preview audio never reaches the venue.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_audioController != null) _audioController.Muted = value;
            if (_videoController != null) _videoController.Muted = value;
            if (_backgroundAudioController != null) _backgroundAudioController.Muted = value;
        }
    }

    /// <summary>Video display mode for this channel (Fit keeps the whole frame, Fill covers the output).</summary>
    public EveryStage.Rendering.VideoScaleMode VideoScaleMode
    {
        get => _videoScaleMode;
        set
        {
            _videoScaleMode = value;
            // Only while local video is on screen: the Program surface is shared with device cast
            // mirroring, which keeps its own (Stretch) mode.
            if (_currentFile?.Kind == MediaKind.Video && _videoSurface != null) _videoSurface.ScaleMode = value;
        }
    }

    /// <summary>Position of the current audio/video file (zero for images/documents).</summary>
    public TimeSpan Position => _currentFile?.Kind switch
    {
        MediaKind.Video => _videoController?.Position ?? TimeSpan.Zero,
        MediaKind.Audio => _audioController?.CurrentPosition ?? TimeSpan.Zero,
        _ => TimeSpan.Zero,
    };

    /// <summary>Duration of the current audio/video file when known.</summary>
    public TimeSpan? Duration => _currentFile?.Kind switch
    {
        MediaKind.Video => _videoController?.Duration,
        MediaKind.Audio => _audioController?.TotalDuration,
        _ => null,
    };

    /// <summary>True when the current file has a timeline (audio/video) that <see cref="SeekTo"/> can move.</summary>
    public bool CanSeek => _currentFile?.Kind is MediaKind.Video or MediaKind.Audio
        && State is not (PlaybackChannelState.Idle or PlaybackChannelState.Failed or PlaybackChannelState.Loading);

    /// <summary>Codec label / audio presence of the current video, for the Preview status line.</summary>
    public string? CurrentVideoCodec => _currentFile?.Kind == MediaKind.Video ? _videoController?.VideoCodec : null;
    public bool CurrentVideoHasAudio => _currentFile?.Kind == MediaKind.Video && (_videoController?.HasAudio ?? false);

    /// <summary>"仅显示首帧" / "仅显示首页" for an animated GIF / multi-page TIFF currently shown, else null.</summary>
    public string? CurrentImageNote => _currentFile?.Kind == MediaKind.Image ? _imageRenderer.FirstFrameOnlyNote : null;

    /// <summary>Video playback diagnostics (A/V sync self-test).</summary>
    public (long Presented, long Dropped) VideoFrameStats =>
        _videoController == null ? (0, 0) : (_videoController.FramesPresented, _videoController.FramesDropped);

    /// <summary>Jumps within the current audio/video file; keeps the paused/playing state.</summary>
    public bool SeekTo(TimeSpan position)
    {
        if (!CanSeek || _currentFile == null) return false;
        IsCompleted = false;
        return _currentFile.Kind == MediaKind.Video
            ? _videoController?.TrySeekTo(position) ?? false
            : _audioController?.TrySeekTo(position) ?? false;
    }

    public bool SeekRelative(TimeSpan delta) => SeekTo(Position + delta);

    /// <summary>What Take needs to continue this channel's content on the other channel.</summary>
    public readonly record struct Snapshot(MediaFile File, Activity? Activity, int FileIndex, TimeSpan Position, bool Paused);

    public Snapshot? CaptureSnapshot() =>
        _currentFile == null ? null : new Snapshot(_currentFile, _currentActivity, _currentFileIndex, Position, IsPaused);

    /// <summary>Starts <paramref name="snapshot"/>'s content on this channel at its position (Take).
    /// Returns false if this channel refused it (Program with output disabled).</summary>
    public bool RequestPlay(Snapshot snapshot, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
        _pendingStartPosition = snapshot.Position;
        try
        {
            if (snapshot.Activity != null && snapshot.FileIndex >= 0 && snapshot.FileIndex < snapshot.Activity.Files.Count
                && ReferenceEquals(snapshot.Activity.Files[snapshot.FileIndex], snapshot.File))
                RequestPlay(snapshot.Activity, snapshot.FileIndex, trigger);
            else
                RequestPlay(snapshot.File, trigger);
        }
        finally
        {
            _pendingStartPosition = TimeSpan.Zero;
        }
        return ReferenceEquals(_currentFile, snapshot.File);
    }

    // Start offset for the next PlayFile — set only for the duration of a Take.
    private TimeSpan _pendingStartPosition;

    /// <summary>Stops everything on this channel and returns to Idle (Preview "停止预览").</summary>
    public void Stop()
    {
        ++_contentLoadVersion;
        if (_currentFile != null) _playbackLogger.LogPlaybackEnded(_currentFile.Id, "stopped");
        _currentFile = null;
        _currentActivity = null;
        _currentFileIndex = -1;
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        IsPaused = false;
        IsCompleted = false;
        _videoController?.Stop();
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        CloseWpsDocumentAndRestoreOverlay();
        _output.ShowImageSurface();
        _output.ContentSurface.SetFrame(null);
        LastError = null;
        SetState(PlaybackChannelState.Idle);
    }

    private void SetState(PlaybackChannelState state)
    {
        if (State == state) return;
        State = state;
        foreach (var handler in StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<PlaybackChannelState>)handler)(state); }
            catch (Exception) { } // one bad subscriber must not break the others or playback
        }
    }

    /// <summary>Records a failure for the current file: Failed state + error details for the Preview
    /// card, a structured log entry (file id, channel, stage, extension, codecs — no file content), and
    /// the abnormal-interruption event (Program shows a Toast).</summary>
    private void Fail(MediaFile file, Exception ex)
    {
        var error = PlaybackError.From(file, ex);
        LastError = error;
        string codecs = string.Join("/", new[] { error.VideoCodec, error.AudioCodec }.Where(c => c != null));
        _playbackLogger.LogAbnormalInterruption(file.Id,
            $"[{Channel}] stage={error.Stage} ext={error.Extension}{(codecs.Length > 0 ? " codec=" + codecs : "")}: {ex.Message}");
        _output.ContentSurface.SetFrame(null);
        _output.ShowImageSurface();
        SetState(PlaybackChannelState.Failed);
        RaisePlaybackAbnormallyInterrupted(file, ex.Message);
        // Program is unattended: keep the show moving. Preview has an operator looking at the error card
        // (重试 / 跳过 / 移除 / 系统程序打开), so it stays on the failed file.
        if (Channel == PlaybackChannel.Program) ArmFailureAdvanceTimer(file);
    }

    public PlaybackEngine(PlaybackChannel channel, IPlaybackOutput output, VideoSurface? videoSurface,
        SettingsStore settingsStore, ScenarioStore scenarioStore, OutputStateMachine? stateMachine,
        string? logRootOverride = null)
    {
        _playbackLogger = new PlaybackLogger(logRootOverride); // self-tests log to a temp folder, never ProgramData
        if (channel == PlaybackChannel.Program && stateMachine == null)
            throw new ArgumentNullException(nameof(stateMachine), "The Program channel needs the output state machine.");
        Channel = channel;
        _stateMachine = channel == PlaybackChannel.Program ? stateMachine : null;
        _output = output;
        _videoSurface = videoSurface;
        _settingsStore = settingsStore;
        _scenarioStore = scenarioStore;
        if (_stateMachine != null) _stateMachine.StateChanged += OnOutputStateChanged;
    }

    // Created lazily rather than in the constructor: there's no reason to construct a
    // VideoContentController before this app has ever been asked to play a video, even though the
    // shared VideoSurface itself is constructed eagerly by TerminalApplicationContext (a live
    // device cast can need it before any local video ever plays).
    private VideoContentController VideoController =>
        _videoController ??= new VideoContentController(_videoSurface
            ?? throw new InvalidOperationException("视频呈现不可用：显卡（D3D11）初始化失败，无法在此窗口播放视频。"));

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
            _audioController ??= new AudioContentController { Volume = _pendingAudioVolume, Muted = _muted };
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
    private AudioContentController BackgroundAudioController => _backgroundAudioController ??= new AudioContentController { Muted = _muted };

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
            if (_videoController != null) _videoController.Volume = _pendingAudioVolume;
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
        if (_currentFile == null || _currentFile.Kind != MediaKind.Audio) return;
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
        if (TryTurnDocumentPageAsync(1)) return true;
        return TryAdvance(1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Floating-preview-window "上一项" — same page-before-item precedence as
    /// <see cref="NextManual"/>, in reverse. Returns false at the start of the list (and no further
    /// page to turn back to).</summary>
    public bool PreviousManual()
    {
        if (TryTurnDocumentPageAsync(-1)) return true;
        return TryAdvance(-1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Starts a background PDF page turn before considering playlist navigation.
    /// Repeated clicks while rendering are handled without skipping the document. Office files
    /// retain their external WPS navigation. Completion is accepted only for the same playback generation.</summary>
    private bool TryTurnDocumentPageAsync(int direction)
    {
        if (_currentFile?.Kind != MediaKind.Document || IsOfficeDocument(_currentFile)) return false;
        if (_pdfRenderer.IsTurningPage) return true;
        int target = _pdfRenderer.CurrentPageIndex + direction;
        if (target < 0 || target >= _pdfRenderer.PageCount) return false;
        _ = TurnDocumentPageAsync(direction, _currentFile, _contentLoadVersion);
        return true;
    }

    private async Task TurnDocumentPageAsync(int direction, MediaFile file, int version)
    {
        try
        {
            bool turned = await _pdfRenderer.TurnPageAsync(direction);
            if (turned && version == _contentLoadVersion && ReferenceEquals(file, _currentFile))
                _output.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
        }
        catch (Exception ex)
        {
            if (version == _contentLoadVersion && ReferenceEquals(file, _currentFile))
                OnImageOrDocumentFailed(file, ex);
        }
    }

    /// <summary>Null unless the current file is a Document with more than one page — the floating
    /// preview window uses this to show a "第X页/共Y页" indicator only when page-turning is actually
    /// possible right now, since <see cref="NextManual"/>/<see cref="PreviousManual"/> silently
    /// changed meaning for this case (page-turn instead of playlist-advance) and a user watching the
    /// same two buttons do something different without any visible indicator would be confusing.
    /// 1-based for display (<c>CurrentPage</c> starting at 1, not <see cref="IContentRenderer"/>'s
    /// own 0-based <c>CurrentPageIndex</c>). Also null for an Office document — same reasoning as
    /// <see cref="TryTurnDocumentPageAsync"/>'s own Office check: <see cref="_pdfRenderer"/>'s
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
    private PlayMode EffectivePlayMode(MediaFile file) =>
        file.PlayModeOverride ?? _currentActivity?.DefaultPlayMode ?? PlayMode.SequentialAuto;

    private void PlayFile(MediaFile file, PlaybackTrigger trigger)
    {
        ++_contentLoadVersion;
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
        // switching to ANY new file must close it and hand the monitor back to _output, exactly like
        // leaving standalone audio always stops it regardless of what plays next.
        CloseWpsDocumentAndRestoreOverlay();

        // Only the Program channel is gated: the cast switch controls what reaches the extended display,
        // never whether the operator can preview ("投屏开关只控制 Program 输出").
        if (Channel == PlaybackChannel.Program && !_stateMachine!.RequestLocalFilePlayback())
        {
            PlaybackDeclinedByCastSwitch?.Invoke(file);
            return;
        }

        // Program only: a device cast (if any) is about to be preempted by local content — see this
        // event's doc comment and VideoSurface's for why this has to happen before ShowImageSurface/
        // ShowVideoSurface/VideoController below touch the shared surface. Preview has its own surface
        // and never touches the extended display, so device casts are unaffected by previewing.
        if (Channel == PlaybackChannel.Program) LocalPlaybackStarting?.Invoke();

        _currentFile = file;
        LastError = null;
        IsCompleted = false;
        TimeSpan startAt = _pendingStartPosition;
        _playbackLogger.LogPlaybackStarted(file.Id, file.SourcePath, trigger);
        SetState(PlaybackChannelState.Loading);

        switch (file.Kind)
        {
            case MediaKind.Image:
                _videoController?.Stop();
                _output.ShowImageSurface();
                _ = PlayImageAsync(file);
                break;

            case MediaKind.Document:
                _videoController?.Stop();
                if (IsOfficeDocument(file))
                {
                    // Only the extended display can hand its monitor to WPS's real window; Preview
                    // shows the document's embedded cover instead.
                    if (_output.CanHostExternalDocumentWindow) PlayOfficeDocument(file);
                    else ShowOfficeDocumentPreview(file);
                }
                else
                {
                    _output.ShowImageSurface();
                    _ = PlayDocumentAsync(file);
                }
                break;

            case MediaKind.Video:
                _output.ShowVideoSurface();
                try
                {
                    // Everything that touches VideoController stays inside the try: the getter throws when
                    // this channel has no video surface (no usable GPU video processing), and that must end
                    // in Fail() — an error card — not an exception escaping to the UI thread.
                    // -= before += every time: avoids stacking subscriptions across plays.
                    VideoController.PlaybackCompleted -= OnVideoCompleted;
                    VideoController.PlaybackCompleted += OnVideoCompleted;
                    VideoController.PlaybackFailed -= OnVideoFailed;
                    VideoController.PlaybackFailed += OnVideoFailed;
                    VideoController.FirstFramePresented -= OnVideoFirstFrame;
                    VideoController.FirstFramePresented += OnVideoFirstFrame;
                    VideoController.Volume = _pendingAudioVolume;
                    VideoController.Muted = _muted;
                    // Local media keeps its aspect ratio (Fit/Fill); device cast mirroring on the shared
                    // Program surface sets Stretch when it starts.
                    _videoSurface!.ScaleMode = _videoScaleMode;
                    // Opening the file happens synchronously here (missing file, unsupported codec);
                    // OnVideoFailed only covers failures after playback has started.
                    VideoController.Play(file.SourcePath, FadeDurationFor(file), startAt);
                }
                catch (Exception ex)
                {
                    Fail(file, ex);
                }
                break;

            case MediaKind.Audio:
                // Program never gets here with a background-audio file (RequestPlay/TryAdvance hand
                // those to the overlay track). Preview does, when the operator auditions one directly.
                _videoController?.Stop();
                _output.ShowImageSurface();
                PlayStandaloneAudio(file, startAt);
                break;
        }

        FileStarted?.Invoke(file);
    }

    private async Task PlayImageAsync(MediaFile file)
    {
        int version = _contentLoadVersion;
        try
        {
            _output.ContentSurface.SetFrame(null);
            await _imageRenderer.LoadAsync(file.SourcePath);
            if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile)) return;
            _output.ContentSurface.SetFrame(_imageRenderer.CurrentFrame);
            SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
            ArmStayDurationTimer(file);
        }
        catch (Exception ex)
        {
            if (version == _contentLoadVersion) OnImageOrDocumentFailed(file, ex);
        }
    }

    private async Task PlayDocumentAsync(MediaFile file)
    {
        int version = _contentLoadVersion;
        try
        {
            _output.ContentSurface.SetFrame(null);
            _pdfRenderer.SetTargetSize(_output.ContentSurface.ClientSize);
            await _pdfRenderer.LoadAsync(file.SourcePath);
            if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile)) return;
            _output.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
            SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
            ArmStayDurationTimer(file);
        }
        catch (Exception ex)
        {
            if (version == _contentLoadVersion) OnImageOrDocumentFailed(file, ex);
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
            _output.YieldToExternalWindow(); // cede the extended monitor to WPS's own real window — see WpsDocumentController's class doc comment.
            _wpsController = new WpsDocumentController();
            _wpsController.Open(file.SourcePath, _output.ExternalDocumentBounds);
            SetState(PlaybackChannelState.Playing);
        }
        catch (Exception ex)
        {
            _wpsController?.Close();
            _wpsController = null;
            _output.ReclaimFromExternalWindow(); // the open attempt failed — nothing is covering the monitor, so don't leave it hidden.
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
    /// <c>_output.BeginInvoke</c> marshaling), this runs on whatever <c>SynchronizationContext</c>
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
        Fail(file, ex);
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
    private void PlayStandaloneAudio(MediaFile file, TimeSpan startAt = default)
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
            AudioController.Muted = _muted;
            AudioController.Play(file.SourcePath, FadeDurationFor(file), startAt);
            SetState(PlaybackChannelState.Playing);
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
        _output.Post(new Action(() =>
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
        _output.Post(new Action(() =>
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

        // Preview never shows a blank black monitor for audio: "纯黑" is a Program-side choice, the
        // operator still needs to see what is loaded (file name card with the level/progress around it).
        var visual = Channel == PlaybackChannel.Preview && file.BackgroundAudioVisual == AudioVisual.Black
            ? AudioVisual.DefaultBackgroundImage
            : file.BackgroundAudioVisual;
        switch (visual)
        {
            case AudioVisual.Black:
                _output.ContentSurface.SetFrame(null);
                break;

            case AudioVisual.DefaultBackgroundImage:
                _audioVisualFrame = AudioVisualRenderer.CreateDefaultBackgroundFrame(_output.ContentSurface.ClientSize, file.SourcePath);
                _output.ContentSurface.SetFrame(_audioVisualFrame);
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
            _audioVisualFrame = AudioVisualRenderer.CreateWaveformFrame(_output.ContentSurface.ClientSize, _latestAudioLevel);
        }
        catch (Exception)
        {
            _output.ContentSurface.SetFrame(null);
            return;
        }

        _output.ContentSurface.SetFrame(_audioVisualFrame);
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
        _output.Post(new Action(() => HandleCompletion(file)));
    }

    private void OnAudioFailed(Exception ex)
    {
        var file = _currentFile;
        if (file == null) return;
        // Also raised from the background playback thread.
        _output.Post(new Action(() =>
        {
            if (!ReferenceEquals(file, _currentFile)) return; // stale — we've since moved on.
            Fail(file, ex);
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

    /// <summary>How long a failed file stays on screen before the queue moves past it. Long enough
    /// that the Toast raised alongside it is actually readable by anyone standing there, short
    /// enough that an unattended Terminal is not showing black for a noticeable stretch.</summary>
    private static readonly TimeSpan FailedContentAdvanceDelay = TimeSpan.FromSeconds(3);

    /// <summary>Moves the queue past a file that failed to play, after a short delay.
    ///
    /// Until this existed, every failure handler (OnImageOrDocumentFailed, OnVideoFailed,
    /// OnAudioFailed) logged the failure and raised PlaybackAbnormallyInterrupted - and stopped.
    /// ArmStayDurationTimer is the only route from image/document content to HandleCompletion, and
    /// a failure never reached it, so SequentialAuto auto-advance stalled on the failing file
    /// permanently. One deleted or corrupted file froze the whole playlist. The designed recovery
    /// was the Toast's 重试/移除 buttons, which needs somebody standing at the Terminal - and this
    /// product is specified unattended (PLANNING.md 14.4, the same framing Program.CheckDecodeHealth
    /// cites when it auto-disconnects a stuck cast rather than waiting for a human).
    ///
    /// Deliberately does NOT go through HandleCompletion, and so deliberately ignores the file's own
    /// CompletionAction: Loop would re-play a file already known to be broken every few seconds,
    /// forever, and HoldOnLastFrame would hold on a frame that does not exist (the failure path
    /// clears ContentSurface to black). Neither is a meaningful completion semantic for content that
    /// never played. PlayMode is still honoured, because ManualSelect means an operator is choosing
    /// what comes next and is by definition present.
    ///
    /// Reuses _stayDurationTimer rather than adding a second timer field so that every existing
    /// cancellation point (PlayFile, Stop, Dispose) already covers it. The one behavioural seam: a
    /// pause landing inside this 3-second window resumes through ArmStayDurationTimer, which does go
    /// via HandleCompletion - narrow, requires an operator (so not the unattended case this fixes),
    /// and degrades to the CompletionAction behaviour that was there before.</summary>
    private void ArmFailureAdvanceTimer(MediaFile file)
    {
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();

        var timer = new System.Windows.Forms.Timer { Interval = (int)FailedContentAdvanceDelay.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(file, _currentFile)) return; // moved on already - nothing to advance past.
            if (EffectivePlayMode(file) != PlayMode.SequentialAuto) return;
            if (!TryAdvance(1, PlaybackTrigger.ActivityAuto)) TryAdvanceToNextActivity();
        };
        _stayDurationTimer = timer;
        _stayDurationArmedAt = DateTime.UtcNow;
        _stayDurationTotal = FailedContentAdvanceDelay;
        timer.Start();
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

    /// <summary>Whether <see cref="Pause"/> does anything for the current content: audio/video always;
    /// a still image/page only while a stay-duration timer is running (there is nothing to freeze
    /// otherwise). The transport disables its pause button when this is false instead of leaving a
    /// button that silently does nothing.</summary>
    public bool CanPause => _currentFile != null && !IsCompleted
        && State is PlaybackChannelState.Playing or PlaybackChannelState.Paused
        && (_currentFile.Kind is MediaKind.Audio or MediaKind.Video || _stayDurationTimer != null || IsPaused);

    /// <summary>Pauses in place: audio/video stop their clock; images/PDF pages freeze the stay-duration
    /// auto-advance timer.</summary>
    public void Pause()
    {
        if (IsPaused || _currentFile == null) return;
        if (State is not PlaybackChannelState.Playing) return;

        // Audio (including a background-audio file auditioned on Preview — Program never holds one as
        // its current file) and video pause in place.
        if (_currentFile.Kind == MediaKind.Audio)
        {
            _audioController?.Pause();
            IsPaused = true;
            SetState(PlaybackChannelState.Paused);
            return;
        }

        if (_currentFile.Kind == MediaKind.Video)
        {
            _videoController?.Pause();
            IsPaused = true;
            SetState(PlaybackChannelState.Paused);
            return;
        }

        // Images / PDF pages: freeze the stay-duration clock.
        if (_stayDurationTimer == null) return;

        TimeSpan elapsed = DateTime.UtcNow - _stayDurationArmedAt;
        TimeSpan remaining = _stayDurationTotal - elapsed;
        _remainingOnPause = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;

        _stayDurationTimer.Stop();
        IsPaused = true;
        SetState(PlaybackChannelState.Paused);
    }

    /// <summary>Continues paused content; after the file has finished (<see cref="IsCompleted"/>)
    /// replays it from the start.</summary>
    public void Resume()
    {
        if (_currentFile == null) return;
        if (IsCompleted)
        {
            IsPaused = false;
            RetryCurrentFile();
            return;
        }
        if (!IsPaused) return;
        IsPaused = false;
        SetState(PlaybackChannelState.Playing);

        if (_currentFile.Kind == MediaKind.Audio)
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
        _output.Post(new Action(() => HandleCompletion(file)));
    }

    private void OnVideoFirstFrame()
    {
        var file = _currentFile;
        if (file == null) return;
        _output.Post(() =>
        {
            if (!ReferenceEquals(file, _currentFile) || State != PlaybackChannelState.Loading) return;
            SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
        });
    }

    /// <summary>Preview can't host WPS's own window, so an Office document shows its embedded cover
    /// (or a title card when it has none) with a note that it opens in WPS once taken to the screen.</summary>
    private void ShowOfficeDocumentPreview(MediaFile file)
    {
        _output.ShowImageSurface();
        _audioVisualFrame?.Dispose();
        _audioVisualFrame = null;
        try
        {
            var size = _output.ContentSurface.ClientSize;
            size = new System.Drawing.Size(Math.Max(320, size.Width), Math.Max(240, size.Height));
            using var cover = OfficeThumbnailReader.Read(file.SourcePath, size);
            _audioVisualFrame = AudioVisualRenderer.CreateCoverCard(size, cover, System.IO.Path.GetFileName(file.SourcePath),
                "Office 文档 · 投到屏幕后在 WPS 中打开并编辑");
            _output.ContentSurface.SetFrame(_audioVisualFrame);
            SetState(PlaybackChannelState.Playing);
        }
        catch (Exception ex)
        {
            Fail(file, ex);
        }
    }

    private void OnVideoFailed(Exception ex)
    {
        var file = _currentFile;
        if (file == null) return;
        // Also raised from the background playback thread.
        _output.Post(new Action(() =>
        {
            if (!ReferenceEquals(file, _currentFile)) return; // stale — we've since moved on.
            Fail(file, ex);
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

        // The transport's loop toggle overrides the file's saved completion action (not persisted).
        if (LoopCurrent)
        {
            PlayFile(file, PlaybackTrigger.ActivityAuto);
            return;
        }

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

        // Nothing further started (no next item / hold): the file is finished and held on screen.
        // Surface that as Paused + IsCompleted so the transport shows ▶, and ▶ replays it.
        if (ReferenceEquals(file, _currentFile) && State != PlaybackChannelState.Loading)
        {
            IsCompleted = true;
            IsPaused = true;
            SetState(PlaybackChannelState.Paused);
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
        ++_contentLoadVersion;
        if (_currentFile != null)
        {
            _playbackLogger.LogPlaybackEnded(_currentFile.Id, "preempted_by_device_cast");
            _currentFile = null;
        }

        // Every other path that stops showing a file clears this (PlayImageAsync/PlayDocumentAsync
        // before loading, OnImageOrDocumentFailed, the audio-visual paths); this one did not.
        // ContentSurface holds whatever Bitmap reference it was last handed and owns none of them,
        // so leaving the preempted file's frame in place has two consequences. The visible one: if
        // CastReceiver construction then fails, Program.OnCastStartRequested returns without ever
        // calling ShowVideoSurface, so the extended display keeps painting a local file the engine
        // has already abandoned while the state machine reads Idle. The sharp one: a page turn in
        // flight when the cast preempts will, on completion, dispose exactly the Bitmap held here
        // (PdfContentRenderer.TurnPageAsync swaps then disposes the previous frame) while
        // TurnDocumentPageAsync's version guard skips the matching SetFrame - leaving OnPaint to
        // draw a disposed Bitmap and throw on every repaint thereafter. Same failure shape
        // OnImageOrDocumentFailed already documents and clears for.
        _output.ContentSurface.SetFrame(null);
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        IsPaused = false;
        IsCompleted = false;
        SetState(PlaybackChannelState.Idle);
        _videoController?.Stop();
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        CloseWpsDocumentAndRestoreOverlay();
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state != OutputState.Idle) return;
        IsPaused = false;
        IsCompleted = false;
        SetState(PlaybackChannelState.Idle);

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
        _output.ReclaimFromExternalWindow();
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
        ++_contentLoadVersion;
        if (_stateMachine != null) _stateMachine.StateChanged -= OnOutputStateChanged;
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
