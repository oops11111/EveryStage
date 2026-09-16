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
/// fades, volume-follows-fade, the *background*-audio half of §6 "音频特殊性" (an audio file playing
/// concurrently with other visual content, overlaid rather than occupying the main queue's
/// sequential slot — that needs a genuinely concurrent playback-track model this class doesn't
/// attempt), what happens after the last file in an activity under NextItem (cross-activity
/// auto-advance isn't specified anywhere in PLANNING.md), and rendering a local-only preview when the
/// cast switch is off (that preview surface belongs to the Phase 4 UI's file/activity panels, which
/// don't exist yet).
///
/// <see cref="MediaFile.PlayModeOverride"/>/<see cref="Activity.DefaultPlayMode"/> (via
/// <see cref="EffectivePlayMode"/>), <see cref="MediaFile.AllowManualSkip"/> (via
/// <see cref="TryAdvance"/>), standalone (non-background) audio playback — see
/// <see cref="PlayStandaloneAudio"/> — and now multi-page Document navigation (via
/// <see cref="NextManual"/>/<see cref="PreviousManual"/>/<see cref="DocumentPageInfo"/>, PLANNING.md
/// §3's PDF "翻页") — DO have real effect here, unlike the properties listed above.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly OutputStateMachine _stateMachine;
    private readonly OverlayWindow _overlay;
    private readonly VideoSurface _videoSurface;
    private readonly SettingsStore _settingsStore;
    private readonly ImageContentRenderer _imageRenderer = new();
    private readonly PdfContentRenderer _pdfRenderer = new();
    private readonly PlaybackLogger _playbackLogger = new();
    private VideoContentController? _videoController;
    private AudioContentController? _audioController;

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

    /// <summary>What's currently on the extended display, for the floating preview window
    /// (PLANNING.md §8.3) to show — null when nothing is casting.</summary>
    public MediaFile? CurrentFile => _currentFile;

    public bool IsPaused { get; private set; }

    /// <summary>
    /// A static frame for the floating preview window to mirror (PLANNING.md §8.3's "缩略画面预览").
    /// Only available for image/PDF, whose current frame already exists as an in-memory
    /// <see cref="System.Drawing.Bitmap"/> — for video this returns null rather than a stale or
    /// fake thumbnail, since producing one for real would mean a GPU-downsampled copy out of the
    /// zero-copy pipeline, which doesn't exist yet (see this project's README).
    /// </summary>
    public System.Drawing.Bitmap? CurrentThumbnail => _currentFile?.Kind switch
    {
        MediaKind.Image => _imageRenderer.CurrentFrame,
        MediaKind.Document => _pdfRenderer.CurrentFrame,
        _ => null,
    };

    public PlaybackEngine(OutputStateMachine stateMachine, OverlayWindow overlay, VideoSurface videoSurface, SettingsStore settingsStore)
    {
        _stateMachine = stateMachine;
        _overlay = overlay;
        _videoSurface = videoSurface;
        _settingsStore = settingsStore;
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
    private AudioContentController AudioController =>
        _audioController ??= new AudioContentController();

    /// <summary>"点文件" from within an activity's file list — establishes the auto-advance/manual-
    /// skip context that <see cref="NextManual"/>/<see cref="PreviousManual"/> and
    /// <see cref="CompletionAction.NextItem"/> use.</summary>
    public void RequestPlay(Activity activity, int fileIndex, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
        if (fileIndex < 0 || fileIndex >= activity.Files.Count) return;
        _currentActivity = activity;
        _currentFileIndex = fileIndex;
        PlayFile(activity.Files[fileIndex], trigger);
    }

    /// <summary>"点文件" with no activity context (e.g. directly from a future 文件 panel). With no
    /// activity list to advance through, <see cref="CompletionAction.NextItem"/> degrades to
    /// holding on the last frame — nothing in PLANNING.md defines "next" without an activity.</summary>
    public void RequestPlay(MediaFile file, PlaybackTrigger trigger = PlaybackTrigger.CastSwitch)
    {
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
        if (_currentFile?.Kind == MediaKind.Document && _pdfRenderer.NextPage())
        {
            _overlay.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
            return true;
        }
        return TryAdvance(1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Floating-preview-window "上一项" — same page-before-item precedence as
    /// <see cref="NextManual"/>, in reverse. Returns false at the start of the list (and no further
    /// page to turn back to).</summary>
    public bool PreviousManual()
    {
        if (_currentFile?.Kind == MediaKind.Document && _pdfRenderer.PreviousPage())
        {
            _overlay.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
            return true;
        }
        return TryAdvance(-1, PlaybackTrigger.ManualSkip);
    }

    /// <summary>Null unless the current file is a Document with more than one page — the floating
    /// preview window uses this to show a "第X页/共Y页" indicator only when page-turning is actually
    /// possible right now, since <see cref="NextManual"/>/<see cref="PreviousManual"/> silently
    /// changed meaning for this case (page-turn instead of playlist-advance) and a user watching the
    /// same two buttons do something different without any visible indicator would be confusing.
    /// 1-based for display (<c>CurrentPage</c> starting at 1, not <see cref="IContentRenderer"/>'s
    /// own 0-based <c>CurrentPageIndex</c>).</summary>
    public (int CurrentPage, int PageCount)? DocumentPageInfo =>
        _currentFile?.Kind == MediaKind.Document && _pdfRenderer.PageCount > 1
            ? (_pdfRenderer.CurrentPageIndex + 1, _pdfRenderer.PageCount)
            : null;

    private bool TryAdvance(int delta, PlaybackTrigger trigger)
    {
        if (_currentActivity == null) return false;
        // MediaFile.AllowManualSkip gates only the floating-preview-window buttons (ManualSkip) —
        // an activity's own NextItem/auto-advance is a separate trigger and was never meant to be
        // blocked by "don't let the operator skip past this one manually" (see AllowManualSkip's
        // own doc comment: it says nothing about auto-advance, and conflating the two would make a
        // "no manual skip" file also stall CompletionAction.NextItem, which isn't what either
        // property is documented to mean).
        if (trigger == PlaybackTrigger.ManualSkip && _currentFile?.AllowManualSkip == false) return false;
        int next = _currentFileIndex + delta;
        if (next < 0 || next >= _currentActivity.Files.Count) return false;
        _currentFileIndex = next;
        PlayFile(_currentActivity.Files[_currentFileIndex], trigger);
        return true;
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
                _overlay.ShowImageSurface();
                _ = PlayDocumentAsync(file);
                break;

            case MediaKind.Video:
                _overlay.ShowVideoSurface();
                // -= before += on both, every time: avoids stacking subscriptions across plays
                // without needing a separate "first time?" flag.
                VideoController.PlaybackCompleted -= OnVideoCompleted;
                VideoController.PlaybackCompleted += OnVideoCompleted;
                VideoController.PlaybackFailed -= OnVideoFailed;
                VideoController.PlaybackFailed += OnVideoFailed;
                VideoController.Play(file.SourcePath);
                break;

            case MediaKind.Audio:
                if (file.IsBackgroundAudio)
                {
                    // The concurrent-overlay half of §6 "音频特殊性" — playing on top of whatever
                    // else is on screen, without occupying the main queue's sequential slot — needs
                    // a genuinely concurrent playback-track model this class doesn't have (see this
                    // class's doc comment). Left unhandled rather than silently mis-rendering it
                    // through the image path. The non-background half (below) has real behavior now.
                    return;
                }
                _videoController?.Stop();
                _overlay.ShowImageSurface();
                PlayStandaloneAudio(file);
                break;
        }

        FileStarted?.Invoke(file);
    }

    private async Task PlayImageAsync(MediaFile file)
    {
        await _imageRenderer.LoadAsync(file.SourcePath);
        _overlay.ContentSurface.SetFrame(_imageRenderer.CurrentFrame);
        ArmStayDurationTimer(file);
    }

    private async Task PlayDocumentAsync(MediaFile file)
    {
        _pdfRenderer.SetTargetSize(_overlay.ContentSurface.ClientSize);
        await _pdfRenderer.LoadAsync(file.SourcePath);
        _overlay.ContentSurface.SetFrame(_pdfRenderer.CurrentFrame);
        ArmStayDurationTimer(file);
    }

    /// <summary>Standalone (non-background) audio playback — see this class's doc comment and
    /// <see cref="MediaFile.IsBackgroundAudio"/>'s caller in <see cref="PlayFile"/> for what this
    /// deliberately does not cover. Presents through <see cref="OverlayWindow.ContentSurface"/>
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

        ApplyAudioVisual(file);
        AudioController.Play(file.SourcePath);
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
        _audioVisualFrame?.Dispose();
        _audioVisualFrame = AudioVisualRenderer.CreateWaveformFrame(_overlay.ContentSurface.ClientSize, _latestAudioLevel);
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
            // Same "no documented recovery behavior" reasoning as OnVideoFailed.
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
    /// Floating-preview-window "暂停" (PLANNING.md §8.3) for image/PDF content: freezes the
    /// stay-duration auto-advance clock in place. Deliberately does nothing for video — pausing
    /// video mid-frame and resuming from that exact position would need <c>VideoContentController</c>
    /// to support suspend/resume-in-place, which it doesn't (its <c>Stop()</c> tears the decode
    /// source down entirely). Rather than fake a "pause" that actually restarts the video from the
    /// beginning, this is a documented no-op for that case until real pause/resume exists.
    /// </summary>
    public void Pause()
    {
        if (IsPaused || _currentFile == null || _stayDurationTimer == null) return;

        TimeSpan elapsed = DateTime.UtcNow - _stayDurationArmedAt;
        TimeSpan remaining = _stayDurationTotal - elapsed;
        _remainingOnPause = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;

        _stayDurationTimer.Stop();
        IsPaused = true;
    }

    /// <summary>Resumes a stay-duration countdown paused by <see cref="Pause"/>, from where it left
    /// off. No-op if nothing is paused.</summary>
    public void Resume()
    {
        if (!IsPaused || _currentFile == null) return;
        IsPaused = false;
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
            // No documented recovery behavior for a decode failure (retry? skip to the next item?
            // PLANNING.md doesn't say) — leave whatever's on screen as-is rather than guess at one.
        }));
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
                if (EffectivePlayMode(file) == PlayMode.SequentialAuto)
                    TryAdvance(1, PlaybackTrigger.ActivityAuto); // no-op (holds) if nothing further — see class doc comment.
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
    /// when nothing is currently playing.</summary>
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
        StopWaveformTimer();
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state != OutputState.Idle) return;

        // "断": stop actively decoding — nothing is on screen to show it to — but do not tear down
        // the video swap chain/device (PLANNING.md §9.2's "预先创建并常驻" applies to the whole
        // video pipeline, not just the overlay window). Resuming after "断" starts over via a new
        // RequestPlay; there is no documented "resume from where it left off" behavior.
        if (_currentFile != null)
        {
            _playbackLogger.LogPlaybackEnded(_currentFile.Id, "disconnected");
            _currentFile = null;
        }
        _stayDurationTimer?.Stop();
        _videoController?.Stop();
        _audioController?.Stop();
        StopWaveformTimer();
    }

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
    }
}
