using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryStage.Terminal.Data;

public sealed class ScenarioStore
{
    public List<Scenario> Scenarios { get; init; } = new();
    public Guid? CurrentScenarioId { get; set; }
}

/// <summary>
/// JSON persistence for scenarios/activities/files (PLANNING.md §6). The Terminal is an
/// unattended, always-on device (§14.4), so writes are done atomically (write to a temp file,
/// then replace) rather than overwriting the store file in place — a crash or power loss mid-write
/// must never leave a corrupt store that the next boot can't load.
/// </summary>
public sealed class ScenarioRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _storePath;

    public ScenarioRepository(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "scenarios.json");
    }

    public ScenarioStore Load()
    {
        if (!File.Exists(_storePath))
            return CreateDefaultStore();

        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<ScenarioStore>(stream, JsonOptions) ?? CreateDefaultStore();
        }
        catch (JsonException)
        {
            // Corrupt store on an unattended device: fail safe to an empty default rather than
            // crash-looping the Terminal on every boot. The bad file is kept alongside (renamed)
            // for later diagnosis instead of silently overwritten.
            var corruptBackupPath = _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_storePath, corruptBackupPath, overwrite: true);
            return CreateDefaultStore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Different failure mode from JsonException above, and deliberately NOT treated the same
            // way: File.Exists returning true doesn't mean File.OpenRead can actually succeed —
            // another process can hold an exclusive lock (antivirus scan, backup tool), or a
            // permissions problem can block the read outright. This says nothing about whether the
            // file's content is bad, so renaming it aside like the JsonException branch does would
            // risk permanently hiding perfectly good scenario data under a filename Load() never
            // looks for again just because it was momentarily locked. Fail safe to an empty default
            // for this run only, and leave the file itself untouched so a later restart (once
            // whatever is locking it lets go) has a chance to read it normally.
            return CreateDefaultStore();
        }
    }

    public void Save(ScenarioStore store)
    {
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        var tempPath = _storePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, store, JsonOptions);
        }

        // File.Replace is atomic on the same volume; falls back to Move for the first-ever save
        // when the destination doesn't exist yet.
        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, _storePath);
    }

    private static ScenarioStore CreateDefaultStore() => new();
}
