using System.Text.Json;

namespace EveryStage.Terminal.Data;

/// <summary>
/// JSON persistence for <see cref="AppSettings"/>, the same atomic-write pattern as
/// <see cref="ScenarioRepository"/>/<see cref="FileLibraryStore"/>/<c>Devices.PairedDeviceStore</c>:
/// temp file then <see cref="File.Replace(string, string, string?)"/>, so a crash mid-write can't
/// corrupt the settings an unattended Terminal reads at every startup.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storePath;
    private bool _loadFailed;

    /// <summary>The live settings object. Callers that only read a value (e.g.
    /// <c>PlaybackEngine</c>'s stay-duration fallback) can hold onto this <see cref="SettingsStore"/>
    /// and read <see cref="Current"/> fresh each time, so a change made through the 设置 panel is
    /// visible immediately without re-wiring anything. Mutate a copy and pass it to <see cref="Save"/>
    /// rather than mutating this instance in place, so a half-edited settings object is never what
    /// another reader sees mid-edit.</summary>
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? SettingsChanged;

    public SettingsStore(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "settings.json");
        Current = Load();
    }

    /// <summary>Replaces and persists the settings. Takes the whole object rather than a partial
    /// update — <see cref="AppSettings"/> is small enough that "load, copy, edit, save" (the
    /// pattern <c>UI/Panels/SettingsPanel</c> uses) doesn't need a more granular API yet.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_loadFailed) throw new IOException("设置文件读取失败，已阻止覆盖原文件；请先修复文件或重启后重试。");

        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _storePath + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
                JsonSerializer.Serialize(stream, settings, JsonOptions);

            if (File.Exists(_storePath)) File.Replace(tempPath, _storePath, destinationBackupFileName: null);
            else File.Move(tempPath, _storePath);
            Current = settings;
            SettingsChanged?.Invoke(Current);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private AppSettings Load()
    {
        if (!File.Exists(_storePath)) return new AppSettings();

        try
        {
            using var stream = File.OpenRead(_storePath);
            using var document = JsonDocument.Parse(stream);
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out _))
                settings.SchemaVersion = 0;
            return Migrate(settings);
        }
        catch (JsonException)
        {
            // Same fail-safe as ScenarioRepository/PairedDeviceStore: an unattended device must not
            // crash-loop on a corrupt settings file. Keep the bad file around (renamed) rather than
            // silently discard it, and fall back to defaults instead.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            // Different failure mode from JsonException above, and deliberately NOT treated the same
            // way: File.Exists returning true doesn't mean File.OpenRead can actually succeed —
            // another process can hold an exclusive lock (antivirus scan, backup tool), or a
            // permissions problem can block the read outright. This says nothing about whether the
            // file's content is bad, so renaming it aside like the JsonException branch does would
            // risk permanently hiding perfectly good settings under a filename Load() never looks for
            // again just because it was momentarily locked. Fail safe to defaults for this run only,
            // and leave the file itself untouched so a later restart (once whatever is locking it lets
            // go) has a chance to read it normally.
            return new AppSettings();
        }
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        // Version 3 makes the product's designed dark appearance the default. Earlier builds wrote
        // Light even when the user had never made a theme choice, which caused the modern shell to
        // be recolored back to stock WinForms gray on first launch.
        if (settings.SchemaVersion < 3)
            settings.Theme = AppTheme.Dark;

        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        // A newer application may have written fields this build cannot understand. Preserve the
        // file on disk and use safe defaults for this run instead of partially interpreting it.
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            return new AppSettings();

        return settings;
    }
}
