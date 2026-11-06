using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Playback;

public sealed partial class PlaybackEngine
{
    /// <summary>Whether <see cref="Pause"/> does anything for the current content: audio/video always;
    /// a still image/page only while a stay-duration timer is running (there is nothing to freeze
    /// otherwise). The transport disables its pause button when this is false instead of leaving a
    /// button that silently does nothing.</summary>
    public bool CanPause => _currentFile != null && !IsCompleted
        && State is PlaybackChannelState.Playing or PlaybackChannelState.Paused
        && (_currentFile.Kind is MediaKind.Audio or MediaKind.Video || _stayDurationTimer != null || _documentAutoTimer != null || IsPaused);

    /// <summary>Pauses in place: audio/video stop their clock; images/PDF pages freeze the stay-duration
    /// auto-advance timer. A background-audio track keeps playing — it is an overlay independent of the
    /// foreground item (see the class doc comment; audit B-11 asked for this choice to be stated here).</summary>
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

        // Documents with automatic progress (scrolling, page interval, slide steps) pause that.
        if (_currentFile.Kind == MediaKind.Document && PauseDocumentAuto())
        {
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

        if (_currentFile.Kind == MediaKind.Document && ResumeDocumentAuto()) return;

        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
        ArmStayDurationTimer(_currentFile, _remainingOnPause, isFreshStart: false);
    }

    private TimeSpan _remainingOnPause;

    private void OnVideoCompleted()
    {
        var file = _currentFile;
        int generation = _videoController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Raised from VideoContentController's background playback thread — marshal to the UI
        // thread before touching timers/overlay state.
        _output.Post(new Action(() => { if (generation == _videoController?.PlaybackGeneration) HandleCompletion(file); }));
    }

    private void OnVideoFirstFrame()
    {
        var file = _currentFile;
        int generation = _videoController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        _output.Post(() =>
        {
            if (generation != _videoController?.PlaybackGeneration || !ReferenceEquals(file, _currentFile) || State != PlaybackChannelState.Loading) return;
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
            size = new System.Drawing.Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
            // The embedded cover itself, scaled to the preview width like any image (the reader fits within
            // the target, so an unbounded height means "scale by width"). The WPS note goes to the Preview
            // heading (CurrentContentNote) instead of shrinking the cover into a card.
            var cover = OfficeThumbnailReader.Read(file.SourcePath, new System.Drawing.Size(size.Width, size.Width * 8));
            _audioVisualFrame = cover ?? AudioVisualRenderer.CreateCoverCard(size, null, System.IO.Path.GetFileName(file.SourcePath),
                "Office 文档 · 无内嵌封面");
            _output.ContentSurface.SetFrame(_audioVisualFrame);
            SetState(PlaybackChannelState.Playing);
        }
        catch (Exception ex)
        {
            Fail(file, ex);
        }
    }

    /// <summary>The video keeps playing without sound (output device missing or removed mid-play). Logged
    /// once per occurrence; the Preview heading shows the reason through <see cref="CurrentContentNote"/>.
    /// Raised from the UI thread (open) or the playback thread (loss).</summary>
    private void OnVideoAudioOutputLost(string reason)
    {
        var file = _currentFile;
        if (file != null) _playbackLogger.LogAudioOutputLost(file.Id, reason);
    }

    private void OnVideoAudioRecoveryRequested()
    {
        var file = _currentFile;
        int generation = _contentLoadVersion;
        if (file == null) return;
        _output.Post(() =>
        {
            if (generation != _contentLoadVersion || !ReferenceEquals(file, _currentFile)
                || IsCompleted || State is not (PlaybackChannelState.Playing or PlaybackChannelState.Paused)) return;
            if (_videoController?.TryRecoverAudioOnDefault() == true)
                _settingsStore.NotifyAudioOutputFallback("视频音频设备已断开，已从当前位置切换到系统默认设备。");
        });
    }

    /// <summary>This machine decodes the current video slower than real time (frames dropped, picture
    /// still updated every 250 ms at least). Logged once per file; shown via <see cref="CurrentContentNote"/>.</summary>
    private void OnVideoDecodeTooSlow(string note)
    {
        var file = _currentFile;
        if (file != null) _playbackLogger.LogDecodeTooSlow(file.Id, note);
    }

    private void OnVideoFailed(Exception ex)
    {
        var file = _currentFile;
        int generation = _videoController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Also raised from the background playback thread.
        _output.Post(new Action(() =>
        {
            if (generation != _videoController?.PlaybackGeneration || !ReferenceEquals(file, _currentFile)) return;
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
        if (file.Kind == MediaKind.Video && _videoController != null)
            _playbackLogger.LogVideoFrames(file.Id, Channel.ToString(), "completed", _videoController.FramesPresented, _videoController.FramesDropped);

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
            case CompletionAction.BlackScreen:
                // Video uses a separate swap-chain HWND. Switch back to the image surface and clear
                // its frame so both raster and video output end on the same intentional black state.
                _output.ShowImageSurface();
                _output.ContentSurface.SetFrame(null);
                break;
            case CompletionAction.MainKeyVisual:
                // 回主 KV: the 主 KV image from 设置, still on air; black when none is set or it cannot load.
                _output.ShowImageSurface();
                _output.ContentSurface.SetFrame(LoadMainKeyVisual());
                break;
            default:
                // A value outside the enum (hand-edited or damaged store: the JSON converter accepts any
                // number) used to match nothing and stop the activity with no record (audit C-5).
                // Treated as HoldOnLastFrame, and logged.
                _playbackLogger.LogAbnormalInterruption(file.Id, $"未知的完成动作 {(int)file.OnCompletion}，按“停留在最后画面”处理");
                break;
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
}
