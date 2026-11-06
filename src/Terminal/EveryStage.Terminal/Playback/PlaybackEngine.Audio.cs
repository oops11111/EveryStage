using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Playback;

public sealed partial class PlaybackEngine
{
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
            if (ex is EveryStage.Rendering.Decode.MediaOpenException { Stage: EveryStage.Rendering.Decode.MediaFailureStage.Render })
                _waitingAudioOutput = (file, startAt, _contentLoadVersion);
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
        ++_backgroundAudioGeneration;
        _waitingBackgroundOutput = null;
        _backgroundAudioFile = file;
        _backgroundAudioStartedAt = DateTime.UtcNow;
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
            if (ex is EveryStage.Rendering.Decode.MediaOpenException { Stage: EveryStage.Rendering.Decode.MediaFailureStage.Render })
                _waitingBackgroundOutput = (file, TimeSpan.Zero, _backgroundAudioGeneration);
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
        int generation = _backgroundAudioController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Raised from AudioContentController's background playback thread — marshal before touching
        // this class's own state, same reasoning as OnAudioCompleted/OnVideoCompleted.
        _output.Post(new Action(() =>
        {
            if (!ReferenceEquals(file, _backgroundAudioFile) || generation != _backgroundAudioController?.PlaybackGeneration) return;
            _playbackLogger.LogPlaybackEnded(file.Id, file.OnCompletion.ToString());
            if (file.OnCompletion == CompletionAction.Loop)
            {
                // A pass that ended almost at once (empty or header-only file) must not restart
                // immediately: that was a tight loop of decoder opens, threads and log lines saturating
                // the UI thread of an unattended device (audit A-12). Same guard idea as the main
                // sequence's ArmFailureAdvanceTimer: stop, and say why.
                if (DateTime.UtcNow - _backgroundAudioStartedAt < MinBackgroundLoopPass)
                {
                    _backgroundAudioFile = null;
                    const string reason = "背景音频播放后立即结束（文件可能为空或已损坏），已停止循环";
                    _playbackLogger.LogAbnormalInterruption(file.Id, reason);
                    RaisePlaybackAbnormallyInterrupted(file, reason);
                    return;
                }
                PlayBackgroundAudio(file);
            }
            else
                _backgroundAudioFile = null;
        }));
    }

    private void OnBackgroundAudioFailed(Exception ex)
    {
        var file = _backgroundAudioFile;
        int generation = _backgroundAudioGeneration;
        int audioGeneration = _backgroundAudioController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Also raised from the background playback thread.
        _output.Post(new Action(() =>
        {
            if (generation != _backgroundAudioGeneration || audioGeneration != _backgroundAudioController?.PlaybackGeneration
                || !ReferenceEquals(file, _backgroundAudioFile)) return;
            var interruptedPosition = _backgroundAudioController?.CurrentPosition ?? TimeSpan.Zero;
            if (ex.Data.Contains("AudioOutputLost") && _backgroundAudioController?.TryRecoverOnDefault() == true)
            {
                _settingsStore.NotifyAudioOutputFallback("背景音乐的输出设备已断开，已从当前位置切换到系统默认设备。");
                return;
            }
            if (ex.Data.Contains("AudioOutputLost")) _waitingBackgroundOutput = (file, interruptedPosition, generation);
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
        int generation = _audioController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Raised from AudioContentController's background playback thread — same marshal-before-
        // touching-timers/overlay-state reasoning as OnVideoCompleted.
        _output.Post(new Action(() => { if (generation == _audioController?.PlaybackGeneration) HandleCompletion(file); }));
    }

    private void OnAudioFailed(Exception ex)
    {
        var file = _currentFile;
        int generation = _contentLoadVersion;
        int audioGeneration = _audioController?.PlaybackGeneration ?? 0;
        if (file == null) return;
        // Also raised from the background playback thread.
        _output.Post(new Action(() =>
        {
            if (generation != _contentLoadVersion || audioGeneration != _audioController?.PlaybackGeneration
                || !ReferenceEquals(file, _currentFile)) return;
            var interruptedPosition = _audioController?.CurrentPosition ?? TimeSpan.Zero;
            if (ex.Data.Contains("AudioOutputLost") && _audioController?.TryRecoverOnDefault() == true)
            {
                _settingsStore.NotifyAudioOutputFallback("音频输出设备已断开，已从当前位置切换到系统默认设备。");
                return;
            }
            if (ex.Data.Contains("AudioOutputLost")) _waitingAudioOutput = (file, interruptedPosition, generation);
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

    private void OnDefaultAudioOutputChanged(string _)
    {
        bool resumedForeground = false, resumedBackground = false;
        if (_waitingAudioOutput is { } waiting && waiting.Generation == _contentLoadVersion
            && ReferenceEquals(waiting.File, _currentFile) && State == PlaybackChannelState.Failed
            && _audioController?.TryResumeOnDefault(waiting.Position, waiting.File.SourcePath, FadeDurationFor(waiting.File), IsPaused) == true)
        {
            _waitingAudioOutput = null;
            _stayDurationTimer?.Stop();
            _stayDurationTimer?.Dispose();
            _stayDurationTimer = null;
            LastError = null;
            ApplyAudioVisual(waiting.File);
            SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
            _settingsStore.NotifyAudioOutputFallback("音频输出设备已恢复，已从中断位置继续播放。");
            resumedForeground = true;
        }
        if (_waitingBackgroundOutput is { } background && background.Generation == _backgroundAudioGeneration
            && _backgroundAudioFile == null && _backgroundAudioController?.TryResumeOnDefault(background.Position, background.File.SourcePath, FadeDurationFor(background.File)) == true)
        {
            _waitingBackgroundOutput = null;
            _backgroundAudioFile = background.File;
            _settingsStore.NotifyAudioOutputFallback("背景音频输出设备已恢复，已从中断位置继续播放。");
            resumedBackground = true;
        }
        if (!resumedForeground && !IsCompleted && _currentFile?.Kind == MediaKind.Audio && State is (PlaybackChannelState.Playing or PlaybackChannelState.Paused))
            _audioController?.TryMoveToCurrentDefault();
        if (!IsCompleted && _currentFile?.Kind == MediaKind.Video && State is (PlaybackChannelState.Playing or PlaybackChannelState.Paused))
            _videoController?.TryMoveToCurrentDefault();
        if (!resumedBackground && _backgroundAudioFile != null) _backgroundAudioController?.TryMoveToCurrentDefault();
    }
}
