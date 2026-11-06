using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.Playback;

public sealed partial class PlaybackEngine
{
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
        // Replace, never orphan, a timer already running (audit C-7) — the failure-advance timer above
        // already does this; an orphaned timer would still fire HandleCompletion for this file.
        _stayDurationTimer?.Stop();
        _stayDurationTimer?.Dispose();
        _stayDurationTimer = null;
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
}
