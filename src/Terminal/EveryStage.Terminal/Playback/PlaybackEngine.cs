using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
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
/// today's ad-hoc test entry points). Image decoding runs asynchronously; its completion and error
/// handling are posted to the output's owning UI thread before touching controls or raising state.
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
/// <see cref="MediaKind.Document"/> is played by the document half of this class
/// (<c>PlaybackEngine.Documents.cs</c>): PDF in the internal continuous / single-page viewer
/// (<see cref="Documents.PdfDocumentView"/>), Word in a live WPS/Word editor captured onto the output,
/// presentations as a captured native slide show or as static slides — see that file's doc comment.
/// </summary>
public sealed partial class PlaybackEngine : IDisposable
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
    private int _backgroundAudioGeneration;
    private (MediaFile File, TimeSpan Position, int Generation)? _waitingAudioOutput;
    private (MediaFile File, TimeSpan Position, int Generation)? _waitingBackgroundOutput;
    private readonly PlaybackLogger _playbackLogger;
    private VideoContentController? _videoController;
    private AudioContentController? _audioController;
    private AudioContentController? _backgroundAudioController;

    // The MediaFile currently (or most recently) handed to _backgroundAudioController — distinct
    // from _currentFile, which a background-audio file never becomes (see class doc comment).
    // Reference-compared in PlayBackgroundAudio/OnBackgroundAudioCompleted/OnBackgroundAudioFailed
    // to tell "still the same overlay track" from "replaced by a different one since" or "already
    // stopped", the same staleness-guard pattern _currentFile itself already uses throughout this
    // class (e.g. OnImageOrDocumentFailed's own ReferenceEquals check).
    private MediaFile? _backgroundAudioFile;
    private DateTime _backgroundAudioStartedAt;
    // A looping background track whose pass was shorter than this is treated as broken (audit A-12).
    private static readonly TimeSpan MinBackgroundLoopPass = TimeSpan.FromSeconds(1);

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

    /// <summary>Program only, raised synchronously just before the output switches to <c>file</c>
    /// (after the cast switch allowed it): the transition layer captures the current picture here.</summary>
    public event Action<MediaFile>? ProgramContentSwitching;

    /// <summary>One-shot transition for the next Program switch (监看 「切换」 / 「淡入淡出」 / 「对调」);
    /// the transition controller takes it and clears it. Null: the item's own or 设置's transition.</summary>
    public TransitionStyle? PendingTransition { get; set; }

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
    /// A static frame for the floating preview window to mirror (PLANNING.md §8.3's "缩略画面预览"):
    /// the image, the PDF page on screen, or the latest captured editor / slide-show frame. Null for
    /// video (no CPU copy of the zero-copy pipeline exists). Borrowed: copy it before the next UI tick.
    /// </summary>
    public System.Drawing.Bitmap? CurrentThumbnail => _currentFile?.Kind switch
    {
        MediaKind.Image => _imageRenderer.CurrentFrame,
        MediaKind.Document => _pdfView?.CurrentPageBitmap ?? _officeFrame?.Bitmap ?? _audioVisualFrame,
        _ => null,
    };

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

    /// <summary>Short note for the Preview heading: "仅显示首帧/首页" (animated GIF / multi-page TIFF), why a
    /// video plays without sound, or the document's state (static-slide limits, read-only, editor dialog,
    /// window disconnected…); else null.</summary>
    public string? CurrentContentNote => _currentFile switch
    {
        { Kind: MediaKind.Image } => _imageRenderer.FirstFrameOnlyNote,
        { Kind: MediaKind.Video } => JoinNotes(_videoController?.AudioNote, _videoController?.PerformanceNote),
        { Kind: MediaKind.Document } => Document?.Notice,
        _ => null,
    };

    /// <summary>Video playback diagnostics (A/V sync self-test).</summary>
    public (long Presented, long Dropped) VideoFrameStats =>
        _videoController == null ? (0, 0) : (_videoController.FramesPresented, _videoController.FramesDropped);

    private static string? JoinNotes(string? first, string? second) =>
        first == null ? second : second == null ? first : first + "；" + second;

    /// <summary>Jumps within the current audio/video file; keeps the paused/playing state. A failure to
    /// restart after the jump (e.g. the audio output vanished meanwhile) ends in an error card, never in
    /// an exception escaping to the UI handler that asked for the seek (audit B-21).</summary>
    public bool SeekTo(TimeSpan position)
    {
        if (!CanSeek || _currentFile == null) return false;
        IsCompleted = false;
        var file = _currentFile;
        try
        {
            return file.Kind == MediaKind.Video
                ? _videoController?.TrySeekTo(position) ?? false
                : _audioController?.TrySeekTo(position) ?? false;
        }
        catch (Exception ex)
        {
            Fail(file, ex);
            return false;
        }
    }

    public bool SeekRelative(TimeSpan delta) => SeekTo(Position + delta);

    /// <summary>What Take needs to continue this channel's content on the other channel: for documents
    /// also the PDF view position (page + offset + zoom + mode) and the slide.</summary>
    public readonly record struct Snapshot(MediaFile File, Activity? Activity, int FileIndex, TimeSpan Position, bool Paused,
        PdfViewportState? PdfState = null, int? Slide = null);

    public Snapshot? CaptureSnapshot() =>
        _currentFile == null ? null : new Snapshot(_currentFile, _currentActivity, _currentFileIndex, Position, IsPaused,
            _pdfView?.State, _officeSession is SlideShowSession show ? BrowsedSlide
                : _documentMode == DocumentPlaybackMode.StaticSlides && _pdfView != null ? _pdfView.CurrentPage : null);

    /// <summary>Starts <paramref name="snapshot"/>'s content on this channel at its position (Take).
    /// Returns false if this channel refused it (Program with output disabled).</summary>
    public bool RequestPlay(Snapshot snapshot, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
        _pendingStartPosition = snapshot.Position;
        _pendingPdfState = snapshot.PdfState;
        _pendingSlide = snapshot.Slide;
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
            _pendingPdfState = null;
            _pendingSlide = null;
        }
        return ReferenceEquals(_currentFile, snapshot.File);
    }

    // Start offset for the next PlayFile — set only for the duration of a Take.
    private TimeSpan _pendingStartPosition;

    /// <summary>Stops everything on this channel and returns to Idle (Preview "停止预览").</summary>
    public void Stop()
    {
        ++_contentLoadVersion;
        _waitingAudioOutput = null;
        var stoppedFile = _currentFile;
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
        if (stoppedFile?.Kind == MediaKind.Video && _videoController != null)
            _playbackLogger.LogVideoFrames(stoppedFile.Id, Channel.ToString(), "stopped", _videoController.FramesPresented, _videoController.FramesDropped);
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        _output.ShowImageSurface();
        _output.ContentSurface.SetFrame(null);
        ReleaseDocumentNow();
        LastError = null;
        SetState(PlaybackChannelState.Idle);
        RaiseDocumentChanged();
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
        ReleaseDocumentNow();
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
        _documentUiContext = new PlaybackOutputSynchronizationContext();
        _output.ContentSurface.Disposed += OnDocumentOutputDisposed;
        _videoSurface = videoSurface;
        // Preview fills its width (vertical overflow cropped); the extended display shows the whole frame.
        _videoScaleMode = channel == PlaybackChannel.Preview ? EveryStage.Rendering.VideoScaleMode.FitWidth : EveryStage.Rendering.VideoScaleMode.Fit;
        _settingsStore = settingsStore;
        _settingsStore.DefaultAudioOutputChanged += OnDefaultAudioOutputChanged;
        _scenarioStore = scenarioStore;
        if (_stateMachine != null) _stateMachine.StateChanged += OnOutputStateChanged;
    }

    // Created lazily rather than in the constructor: there's no reason to construct a
    // VideoContentController before this app has ever been asked to play a video, even though the
    // shared VideoSurface itself is constructed eagerly by TerminalApplicationContext (a live
    // device cast can need it before any local video ever plays).
    private VideoContentController VideoController =>
        _videoController ??= new VideoContentController(_videoSurface
            ?? throw new InvalidOperationException("视频呈现不可用：显卡（D3D11）初始化失败，无法在此窗口播放视频。")) { AudioOutputFactory = _settingsStore.OpenAudioOutput };

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
            _audioController ??= new AudioContentController { Volume = _pendingAudioVolume, Muted = _muted, AudioOutputFactory = _settingsStore.OpenAudioOutput };
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
    private AudioContentController BackgroundAudioController => _backgroundAudioController ??= new AudioContentController { Muted = _muted, AudioOutputFactory = _settingsStore.OpenAudioOutput };

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
        try
        {
            if (_audioController.TrySeekTo(target) && IsPaused) _audioController.Pause();
        }
        catch (Exception ex)
        {
            Fail(_currentFile, ex); // never escape to the play bar's click handler (audit B-21)
        }
    }

    private void PlayFile(MediaFile file, PlaybackTrigger trigger)
    {
        ++_contentLoadVersion;
        _waitingAudioOutput = null;
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

        // Only the Program channel is gated: the cast switch controls what reaches the extended display,
        // never whether the operator can preview ("投屏开关只控制 Program 输出").
        if (Channel == PlaybackChannel.Program
            && !(trigger == PlaybackTrigger.DeviceRestore
                ? _stateMachine!.RestoreLocalFilePlayback()
                : _stateMachine!.RequestLocalFilePlayback()))
        {
            PlaybackDeclinedByCastSwitch?.Invoke(file);
            return;
        }

        // Program only: the picture is about to change — the transition layer freezes what is on the
        // big screen now (before anything below touches the output) and reveals the new item when ready.
        if (Channel == PlaybackChannel.Program) ProgramContentSwitching?.Invoke(file);

        // The previous document (PDF view, editor / slide-show session, captured frame) keeps drawing
        // until the next picture is ready; a next document releases it when its first frame is on screen,
        // anything else releases it right away (below).
        RetireDocument();

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
        if (file.Kind != MediaKind.Document) CompleteDocumentRetirement();

        switch (file.Kind)
        {
            case MediaKind.Image:
                _videoController?.Stop();
                _output.ShowImageSurface();
                _ = PlayImageAsync(file);
                break;

            case MediaKind.Document:
                _videoController?.Stop();
                WithDocumentUiContext(() => PlayDocument(file));
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
                    VideoController.AudioOutputLost -= OnVideoAudioOutputLost;
                    VideoController.AudioOutputLost += OnVideoAudioOutputLost;
                    VideoController.AudioOutputRecoveryRequested -= OnVideoAudioRecoveryRequested;
                    VideoController.AudioOutputRecoveryRequested += OnVideoAudioRecoveryRequested;
                    VideoController.DecodeTooSlow -= OnVideoDecodeTooSlow;
                    VideoController.DecodeTooSlow += OnVideoDecodeTooSlow;
                    VideoController.Volume = _pendingAudioVolume;
                    VideoController.Muted = _muted;
                    // Local media keeps its aspect ratio (Fit/Fill); device cast mirroring on the shared
                    // Program surface sets Stretch when it starts.
                    _videoSurface!.ScaleMode = _videoScaleMode;
                    _videoSurface.ColorMatrix = EveryStage.Rendering.VideoColorMatrix.Auto; // a cast may have left BT.601
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
        Exception? failure = null;
        try
        {
            _output.ContentSurface.SetFrame(null);
            await _imageRenderer.LoadAsync(file.SourcePath).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            _output.Post(() =>
            {
                if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile)) return;
                if (failure != null) { OnImageOrDocumentFailed(file, failure); return; }
                try
                {
                    _output.ContentSurface.SetFrame(_imageRenderer.CurrentFrame);
                    SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
                    ArmStayDurationTimer(file);
                }
                catch (Exception ex) { OnImageOrDocumentFailed(file, ex); }
            });
        }
        catch (InvalidOperationException) { } // output closed while the decoder was finishing
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
    /// Also the failure path of every document pipeline (PDF, conversion, WPS/Office session).</summary>
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

    private Bitmap? _mainKeyVisual;
    private string? _mainKeyVisualPath;

    /// <summary>The 主 KV bitmap (cached per path; the file is read into memory, not kept locked).</summary>
    private Bitmap? LoadMainKeyVisual()
    {
        string? path = _settingsStore.Current.MainKeyVisualPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_mainKeyVisual != null && string.Equals(path, _mainKeyVisualPath, StringComparison.OrdinalIgnoreCase)) return _mainKeyVisual;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream);
            var bitmap = new Bitmap(image);
            _output.ContentSurface.SetFrame(null);
            _mainKeyVisual?.Dispose();
            _mainKeyVisual = bitmap;
            _mainKeyVisualPath = path;
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            _playbackLogger.LogAbnormalInterruption(Guid.Empty, $"主 KV 无法加载：{ex.Message}");
            return null;
        }
    }

    /// <summary>Stops any current local-file playback so an incoming device cast can safely start
    /// presenting through the shared <see cref="VideoSurface"/> — called by
    /// <c>TerminalApplicationContext</c> before constructing a <c>CastReceiver</c>. Unlike
    /// <see cref="OnOutputStateChanged"/>'s Idle handling, this deliberately does NOT go through
    /// <c>OutputStateMachine</c> at all: the device-cast controller has already accepted this request,
    /// so the state machine's state must stay Active — only the video surface's owner is changing.
    /// Safe to call
    /// when nothing is currently playing. Also stops the background-audio overlay track, if one is
    /// playing — PLANNING.md's "叠加在其他视觉内容之上播放" only makes sense while local content is
    /// what's actually on the extended display; once a device cast owns that display, there is
    /// nothing left for it to overlay. Also releases the current document for the same reason, and — unlike
    /// <see cref="OnOutputStateChanged"/>'s Idle branch below — explicitly needs the overlay-restoring
    /// variant: <c>OutputStateMachine.AcceptDeviceCastRequest</c> is a no-op re-affirmation when the
    /// state is already Active (which it is here, or an Office document couldn't have been open in
    /// the first place), so its own <c>StateChanged</c> re-fire that would otherwise re-show
    /// <c>OverlayWindow</c> never happens — this call is the only thing that would.</summary>
    public void StopForDeviceCast()
    {
        ++_contentLoadVersion;
        _waitingAudioOutput = null;
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
        ReleaseDocumentNow();
        RaiseDocumentChanged();
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state != OutputState.Idle) return;
        // Same cleanup as StopForDeviceCast (audit A-10): bump the load version so in-flight loads
        // cannot draw afterwards, drop the frame the surface holds (its owner may dispose it), and
        // dispose the stay timer rather than only stopping it.
        ++_contentLoadVersion;
        _waitingAudioOutput = null;
        _output.ContentSurface.SetFrame(null);
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
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        _videoController?.Stop();
        _audioController?.Stop();
        StopBackgroundAudio();
        StopWaveformTimer();
        // Program is off air: release its document (a slide show still used by Preview stays open there).
        ReleaseDocumentNow();
        RaiseDocumentChanged();
    }

    private void StopBackgroundAudio()
    {
        bool wasWaiting = _waitingBackgroundOutput != null;
        _waitingBackgroundOutput = null;
        if (_backgroundAudioFile == null)
        {
            if (wasWaiting) ++_backgroundAudioGeneration;
            return;
        }
        ++_backgroundAudioGeneration;
        _playbackLogger.LogPlaybackEnded(_backgroundAudioFile.Id, "background_audio_stopped");
        _backgroundAudioController?.Stop();
        _backgroundAudioFile = null;
    }


    public void Dispose()
    {
        ++_contentLoadVersion;
        _output.ContentSurface.Disposed -= OnDocumentOutputDisposed;
        _settingsStore.DefaultAudioOutputChanged -= OnDefaultAudioOutputChanged;
        if (_stateMachine != null) _stateMachine.StateChanged -= OnOutputStateChanged;
        _stayDurationTimer?.Dispose();
        StopWaveformTimer();
        _audioVisualFrame?.Dispose();
        _imageRenderer.Dispose();
        _videoController?.Dispose();
        _audioController?.Dispose();
        _backgroundAudioController?.Dispose();
        ReleaseDocumentNow();
        if (_mainKeyVisual != null)
        {
            if (!_output.ContentSurface.IsDisposed) _output.ContentSurface.SetFrame(null);
            _mainKeyVisual.Dispose();
            _mainKeyVisual = null;
        }
    }
}
