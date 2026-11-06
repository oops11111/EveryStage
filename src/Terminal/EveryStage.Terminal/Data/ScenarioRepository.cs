using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryStage.Terminal.Data;

public sealed class ScenarioStore
{
    /// <summary>The schema this build writes and understands, mirroring
    /// <c>AppSettings.CurrentSchemaVersion</c>'s role for the settings file. Files written before
    /// versioning existed have no SchemaVersion field at all; the property default makes them
    /// deserialize as the current version, which is exactly how they were always interpreted.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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
    private bool _loadFailed;

    public ScenarioRepository(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "scenarios.json");
    }

    public ScenarioStore Load()
    {
        _loadFailed = false;
        try
        {
            var loaded = DeserializeStore(_storePath);
            if (loaded == null) return CreateDefaultStore();
            // Written by a newer EveryStage: same treatment as the settings file's newer-schema
            // guard. The contents may use fields/meanings this build cannot interpret, so run on
            // an empty default for this session, leave the file exactly as it is, and let Save's
            // _loadFailed check refuse to overwrite data a newer build owns.
            if (loaded.SchemaVersion > ScenarioStore.CurrentSchemaVersion)
            {
                _loadFailed = true;
                return CreateDefaultStore();
            }
            // Older or unversioned file: interpret it as the current schema (any future migration
            // steps belong here, before the version is normalised).
            loaded.SchemaVersion = ScenarioStore.CurrentSchemaVersion;
            return loaded;
        }
        catch (FileNotFoundException) { return CreateDefaultStore(); }
        catch (DirectoryNotFoundException) { return CreateDefaultStore(); }
        catch (JsonException)
        {
            // Corrupt store on an unattended device: fail safe rather than crash-looping the
            // Terminal on every boot. The bad file is kept alongside (copied) for later diagnosis
            // instead of silently overwritten.
            var corruptBackupPath = _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_storePath, corruptBackupPath, overwrite: true);
            // Before giving up to an empty default, try the previous version Save kept as .bak:
            // a single torn/corrupted write then costs nothing — the operator gets the store as
            // of the save before it, not an empty library. The .bak gets the same schema guard
            // as the main file; a .bak that is itself corrupt or newer-schema is no better than
            // no .bak at all.
            if (TryDeserializeBackup(out var backup)) return backup;
            return CreateDefaultStore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
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
        if (_loadFailed)
            throw new IOException("方案读取失败，已禁止保存以保护原数据。请解除文件占用或权限问题后重新加载。");
        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        var tempPath = _storePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, store, JsonOptions);
            // Flushed all the way to the device, not just out of the .NET buffer. File.Replace's rename is a
            // journalled NTFS metadata operation, but the temp file's DATA blocks are not journalled with it -
            // closing the stream only hands them to the OS cache. Lose power in the window where the rename is
            // durable and the contents are not, and a zero-length or truncated file has atomically replaced a
            // good one: exactly the outcome this class's atomic-write comment promises cannot happen. One
            // FlushFileBuffers closes that window.
            stream.Flush(flushToDisk: true);
        }

        // File.Replace is atomic on the same volume; falls back to Move for the first-ever save
        // when the destination doesn't exist yet. The replaced file is kept as .bak (rather than
        // discarded) so Load can fall back to the previous version if this one is later found
        // corrupt — see the JsonException branch in Load.
        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: _storePath + ".bak");
        else
            File.Move(tempPath, _storePath);
    }

    private static ScenarioStore? DeserializeStore(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ScenarioStore>(stream, JsonOptions);
    }

    /// <summary>Reads the .bak left by the previous <see cref="Save"/>, applying the same
    /// newer-schema guard as <see cref="Load"/> itself. False when there is no usable backup —
    /// missing, unreadable, corrupt, or written by a newer build (in which case _loadFailed is
    /// set, so a recovered-in-name-only session still cannot overwrite the newer data).</summary>
    private bool TryDeserializeBackup(out ScenarioStore backup)
    {
        backup = null!;
        try
        {
            var loaded = DeserializeStore(_storePath + ".bak");
            if (loaded == null) return false;
            if (loaded.SchemaVersion > ScenarioStore.CurrentSchemaVersion)
            {
                _loadFailed = true;
                return false;
            }
            loaded.SchemaVersion = ScenarioStore.CurrentSchemaVersion;
            backup = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static ScenarioStore CreateDefaultStore() => new();

    /// <summary>Replaces <paramref name="store"/>'s contents, in place, with what is on disk — the undo
    /// after a failed save, so the screen never shows edits that were not saved. False (store left as it
    /// is) when the file cannot be read.</summary>
    public bool TryReloadInto(ScenarioStore store)
    {
        try
        {
            ScenarioStore? loaded;
            using (var stream = File.OpenRead(_storePath))
                loaded = JsonSerializer.Deserialize<ScenarioStore>(stream, JsonOptions);
            if (loaded == null) return false;
            if (loaded.SchemaVersion > ScenarioStore.CurrentSchemaVersion) return false;
            store.Scenarios.Clear();
            store.Scenarios.AddRange(loaded.Scenarios);
            store.CurrentScenarioId = loaded.CurrentScenarioId;
            store.SchemaVersion = ScenarioStore.CurrentSchemaVersion;
            _loadFailed = false; // memory now matches the real file again
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
