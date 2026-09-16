namespace EveryStage.Terminal.Logging;

/// <summary>
/// A fourth, ad-hoc log category alongside the three PLANNING.md §14.4 actually names
/// (<see cref="FileOperationLogger"/>/<see cref="PlaybackLogger"/>/<see cref="DeviceConnectionLogger"/>)
/// — deliberately NOT folded into any of those three, and not claimed to be one of them: a generic
/// unhandled exception (a UI event handler throwing, an unobserved <see cref="Task"/> fault, a
/// truly-uncaught exception on some other thread) doesn't cleanly belong to "file operations",
/// "playback", or "device connections" — it could be any of those or none of them — and PLANNING.md
/// itself doesn't say what to do with a failure that doesn't fit its three named buckets. Writing
/// nowhere would be strictly worse than writing somewhere not officially named, for a device
/// PLANNING.md itself calls unattended and therefore in need of "完善的本地日志与自诊断机制" — this
/// exists specifically to back <see cref="Program"/>'s top-level
/// <see cref="Application.ThreadException"/>/<see cref="AppDomain.UnhandledException"/>/
/// <see cref="TaskScheduler.UnobservedTaskException"/> handlers (see that class's doc comment for
/// why those were added and what gap they close), which is the one thing this category needs to
/// record: what crashed/faulted, and (for a real process-terminating <see cref="AppDomain.UnhandledException"/>)
/// that it was about to.
/// </summary>
public sealed class CrashLogger
{
    private readonly DailyRollingLogWriter _writer;

    public CrashLogger(string? logRootOverride = null)
    {
        _writer = new DailyRollingLogWriter(
            Path.Combine(logRootOverride ?? LogPaths.DefaultRoot, "crash"), "crash");
    }

    /// <param name="source">Which of the three top-level handlers caught this — lets the log
    /// distinguish "the app is about to die, this is the last thing we know" (<c>AppDomain</c>) from
    /// "a UI click handler threw but the app is still running" (<c>ThreadException</c>) from "some
    /// fire-and-forget Task's exception was never observed" (<c>UnobservedTaskException</c>), three
    /// very different severities that would otherwise look identical in the log.</param>
    public void LogUnhandledException(string source, Exception? exception) =>
        _writer.Write("unhandled_exception", new
        {
            source,
            exceptionType = exception?.GetType().FullName,
            message = exception?.Message,
            stackTrace = exception?.StackTrace,
        });
}
