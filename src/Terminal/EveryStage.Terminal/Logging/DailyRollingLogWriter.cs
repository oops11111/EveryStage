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

    public DailyRollingLogWriter(string directory, string categoryPrefix, TimeSpan? retention = null)
    {
        _directory = directory;
        _categoryPrefix = categoryPrefix;
        _retention = retention ?? TimeSpan.FromDays(30);

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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Write(string eventType, object? fields = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O"),
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

        lock (_gate)
        {
            // Unattended device (§14.4) — a write failing (disk full, file briefly locked by an
            // external log viewer/antivirus scan) must never take the Terminal down with it.
            try
            {
                File.AppendAllText(CurrentFilePath(), line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string CurrentFilePath() =>
        Path.Combine(_directory, $"{_categoryPrefix}-{DateTime.Now:yyyyMMdd}.log");

    private void CleanupOldFiles()
    {
        DateTime cutoff = DateTime.Now.Date - _retention;

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
