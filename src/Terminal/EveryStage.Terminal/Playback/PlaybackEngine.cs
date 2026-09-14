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
/// fades, volume-follows-fade, background-audio overlay content (§6 "音频特殊性"), what happens
/// after the last file in an activity under NextItem (cross-activity auto-advance isn't specified
/// anywhere in PLANNING.md), and rendering a local-only preview when the cast switch is off (that
/// preview surface belongs to the Phase 4 UI's file/activity panels, which don't exist yet).
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly OutputStateMachine _stateMachine;
    private readonly OverlayWindow _overlay;
    private readonly ImageContentRenderer _imageRenderer = new();
    private readonly PdfContentRenderer _pdfRenderer = new();
    private readonly PlaybackLogger _playbackLogger = new();
    private VideoContentController? _videoController;

    private Activity? _currentActivity;
    private int _currentFileIndex = -1;
    private MediaFile? _currentFile;
    private System.Windows.Forms.Timer? _stayDurationTimer;

    /// <summary>Raised after a file actually starts casting to the extended display (never raised
    /// when the cast switch declined the request).</summary>
    public event Action<MediaFile>? FileStarted;

    public PlaybackEngine(OutputStateMachine stateMachine, OverlayWindow overlay)
    {
        _stateMachine = stateMachine;
        _overlay = overlay;
        _stateMachine.StateChanged += OnOutputStateChanged;
    }

    // Created lazily rather than in the constructor: needs a real HWND (VideoHost.Handle forces
    // one into existence), and there's no reason to spin up a D3D11 device before this app has
    // ever been asked to play a video.
    private VideoContentController VideoController =>
        _videoController ??= new VideoContentController(_overlay.VideoHost.Handle, _overlay.VideoHost.ClientSize);

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

    /// <summary>Floating-preview-window "下一项" (PLANNING.md §8.3). Returns false at the end of
    /// the current activity's file list or with no activity context.</summary>
    public bool NextManual() => TryAdvance(1, PlaybackTrigger.ManualSkip);

    /// <summary>Floating-preview-window "上一项". Returns false at the start of the list.</summary>
    public bool PreviousManual() => TryAdvance(-1, PlaybackTrigger.ManualSkip);

    private bool TryAdvance(int delta, PlaybackTrigger trigger)
    {
        if (_currentActivity == null) return false;
        int next = _currentFileIndex + delta;
        if (next < 0 || next >= _currentActivity.Files.Count) return false;
        _currentFileIndex = next;
        PlayFile(_currentActivity.Files[_currentFileIndex], trigger);
        return true;
    }

    private void PlayFile(MediaFile file, PlaybackTrigger trigger)
    {
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;

        bool casting = _stateMachine.RequestLocalFilePlayback();
        if (!casting)
        {
            // Cast switch is off: PLANNING.md §9.1 calls for local-preview-only playback here.
            // That preview lives in the Phase 4 UI's file/activity panels, which don't exist yet —
            // there is nothing to render to today, so this intentionally no-ops rather than
            // guessing at a substitute surface.
            return;
        }

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
                // Background/standalone audio (§6 "音频特殊性") needs its own mixing + overlay
                // visual (waveform/背景图/黑屏) that isn't built yet. Left unhandled rather than
                // silently mis-rendering it through the image path.
                return;
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

    private void ArmStayDurationTimer(MediaFile file)
    {
        if (file.StayDuration is not { } stay || stay <= TimeSpan.Zero) return;

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)stay.TotalMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HandleCompletion(file);
        };
        _stayDurationTimer = timer;
        timer.Start();
    }

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
                TryAdvance(1, PlaybackTrigger.ActivityAuto); // no-op (holds) if nothing further — see class doc comment.
                break;
            case CompletionAction.Loop:
                PlayFile(file, PlaybackTrigger.ActivityAuto);
                break;
            case CompletionAction.HoldOnLastFrame:
                break; // leave the current frame/video's last frame displayed.
        }
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
    }

    public void Dispose()
    {
        _stateMachine.StateChanged -= OnOutputStateChanged;
        _stayDurationTimer?.Dispose();
        _imageRenderer.Dispose();
        _pdfRenderer.Dispose();
        _videoController?.Dispose();
    }
}
