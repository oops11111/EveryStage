using System.Text.Json;

namespace EveryStage.Terminal.Logging;

/// <summary>
/// Shared plumbing for the three physically-separate log categories PLANNING.md §14.4 calls for
/// ("分三类物理独立存储，便于按问题类型定位"): one JSON-lines file per category per day, with old
/// files past the retention window deleted automatically ("按天滚动 + 自动清理旧日志，避免无人值守
/// 设备日志无限增长占满磁盘"). §14.4 leaves the actual storage format open ("结构化JSON vs 纯文本...
/// 待细化") — JSON-lines is chosen here for being both greppable as text and structured enough to
/// build a diagnostic viewer over later without picking a format the doc explicitly still has open.
///
/// Safe to call <see cref="Write"/> from any thread (e.g. the video playback thread) — appends are
/// serialized behind a lock rather than assuming a single-threaded caller.
/// </summary>
public sealed class DailyRollingLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _directory;
    private readonly string _categoryPrefix;
    private readonly TimeSpan _retention;
    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private DateTime _lastCleanupDate;

    // Health across every writer in the process (audit A-5): a log system that has stopped writing must
    // be visible somewhere other than the logs themselves. Shown on the settings page's 诊断 tab.
    private static long s_failedWrites;
    private static string? s_lastFailure;

    /// <summary>Log lines that could not be written, by any writer, since the process started.</summary>
    public static long FailedWriteCount => Interlocked.Read(ref s_failedWrites);

    /// <summary>Why the most recent failed write failed (category and message); null if none failed.</summary>
    public static string? LastFailure => Volatile.Read(ref s_lastFailure);

    public DailyRollingLogWriter(string directory, string categoryPrefix, TimeSpan? retention = null)
        : this(directory, categoryPrefix, retention, null) { }

    /// <summary><paramref name="now"/> replaces the clock (tests).</summary>
    internal DailyRollingLogWriter(string directory, string categoryPrefix, TimeSpan? retention, Func<DateTime>? now)
    {
        _directory = directory;
        _categoryPrefix = categoryPrefix;
        _retention = retention ?? TimeSpan.FromDays(30);
        _now = now ?? (() => DateTime.Now);

        // Bug fixed here: these two calls used to run unprotected, unlike this same class's own
        // Write() a few lines down — an inconsistency within a single class, not just across
        // classes. This constructor is on the most dangerous possible call path: CrashLogger (one
        // of the four categories built on this shared writer) is constructed as literally the FIRST
        // statement of Program.Main(), before Application.ThreadException/AppDomain.
        // UnhandledException/TaskScheduler.UnobservedTaskException are even registered — so an
        // exception here (a permissions problem or a full disk on this brand-new unattended device,
        // or CleanupOldFiles' Directory.EnumerateFiles hitting the same) would crash the whole
        // Terminal with absolutely no record anywhere, not even the "at least it got logged before
        // dying" outcome every other startup-time failure this session has fixed still gets — the
        // one job this specific piece of diagnostic infrastructure exists for. Matches Write()'s own
        // established philosophy ("a write failing ... must never take the Terminal down with it")
        // by simply extending it to construction time: on failure, this writer still comes into
        // existence, just permanently unable to actually write anything — Write()'s own try/catch
        // already tolerates that outcome indefinitely (every future call fails the same way and is
        // silently swallowed), which is a far better failure mode for an unattended device than not
        // starting at all.
        try
        {
            Directory.CreateDirectory(_directory);
            CleanupOldFiles();
        }
        catch (IOException ex) { RecordFailure(ex); }
        catch (UnauthorizedAccessException ex) { RecordFailure(ex); }
    }

    public void Write(string eventType, object? fields = null)
    {
        lock (_gate)
        {
            // Unattended device (§14.4) — a write failing (disk full, file briefly locked by an
            // external log viewer/antivirus scan) must never take the Terminal down with it. Building
            // the line is inside the guard too: a property getter that throws, or a value the
            // serializer cannot handle, used to escape into the caller (audit B-4) — logging must
            // never be the reason a playback or cast path fails.
            try
            {
                DateTime now = _now();
                // Retention runs once a day, not only at construction: the Terminal runs for months
                // without restarting, so a constructor-only cleanup meant logs grew forever (audit A-4).
                if (now.Date != _lastCleanupDate)
                {
                    try { CleanupOldFiles(); }
                    catch (Exception ex) { RecordFailure(ex); } // still write today's line
                }

                var envelope = new Dictionary<string, object?>
                {
                    ["timestamp"] = new DateTimeOffset(now).ToString("O"),
                    ["event"] = eventType,
                };
                if (fields != null)
                {
                    // Flatten the caller's fields object into the same envelope rather than nesting it,
                    // so every line stays a single flat JSON object regardless of category.
                    foreach (var prop in fields.GetType().GetProperties())
                        envelope[prop.Name] = prop.GetValue(fields);
                }
                string line = JsonSerializer.Serialize(envelope, JsonOptions);
                string path = Path.Combine(_directory, $"{_categoryPrefix}-{now:yyyyMMdd}.log");
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch (DirectoryNotFoundException)
                {
                    // The folder was removed (cleanup tool, operator) or never created: create it and retry.
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }
    }

    private void RecordFailure(Exception ex)
    {
        Interlocked.Increment(ref s_failedWrites);
        Volatile.Write(ref s_lastFailure, $"{_categoryPrefix}: {ex.Message}");
    }

    private void CleanupOldFiles()
    {
        DateTime today = _now().Date;
        _lastCleanupDate = today;
        DateTime cutoff = today - _retention;
        if (!Directory.Exists(_directory)) return;

        foreach (var path in Directory.EnumerateFiles(_directory, $"{_categoryPrefix}-*.log"))
        {
            string datePart = Path.GetFileNameWithoutExtension(path).Substring(_categoryPrefix.Length + 1);
            if (!DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate))
                continue; // unrecognized filename shape — leave it alone rather than guess.

            if (fileDate < cutoff)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
