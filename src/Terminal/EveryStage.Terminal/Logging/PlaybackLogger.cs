namespace EveryStage.Terminal.Logging;

/// <summary>How a playback start was triggered — recorded so the log can answer "why did this show
/// up on screen" (PLANNING.md §14.4's "触发方式（开关/活动自动）").</summary>
public enum PlaybackTrigger
{
    /// <summary>Direct file click with the cast switch on.</summary>
    CastSwitch,
    /// <summary>Activity's sequential-auto advance (completion action, not a fresh user click).</summary>
    ActivityAuto,
    /// <summary>Accepted device cast request — bypasses the cast switch (PLANNING.md §9.1).</summary>
    DeviceRequest,
    /// <summary>Manual next/previous from the floating preview window (PLANNING.md §8.3).</summary>
    ManualSkip,
}

/// <summary>
/// 播放/投屏记录 (PLANNING.md §14.4): "每次播放开始/结束时间、播放内容、触发方式、异常中断及原因" —
/// for reconstructing what was actually shown during some past time window, as distinct from
/// <see cref="FileOperationLogger"/> (content library edits) or <see cref="DeviceConnectionLogger"/>
/// (network/pairing state).
/// </summary>
public sealed class PlaybackLogger
{
    private readonly DailyRollingLogWriter _writer;

    public PlaybackLogger(string? logRootOverride = null)
    {
        _writer = new DailyRollingLogWriter(
            Path.Combine(logRootOverride ?? LogPaths.DefaultRoot, "playback"), "playback");
    }

    public void LogPlaybackStarted(Guid mediaFileId, string sourcePath, PlaybackTrigger trigger) =>
        _writer.Write("playback_started", new { mediaFileId, sourcePath, trigger = trigger.ToString() });

    public void LogPlaybackEnded(Guid mediaFileId, string reason) =>
        _writer.Write("playback_ended", new { mediaFileId, reason });

    /// <summary>An unexpected stop — a decode error, a crashed WPS process, etc. — as opposed to
    /// the ordinary completion-action paths <see cref="LogPlaybackEnded"/> covers.</summary>
    public void LogAbnormalInterruption(Guid mediaFileId, string reason) =>
        _writer.Write("playback_interrupted", new { mediaFileId, reason });
}
