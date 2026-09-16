using System.Text.Json;

namespace EveryStage.Caster.Discovery;

/// <summary>
/// JSON persistence for <see cref="PairedTerminal"/>, mirroring Terminal's own
/// <c>Devices.PairedDeviceStore</c> file-for-file (same atomic write-to-temp-then-replace approach,
/// same corrupt-file-rename-rather-than-silently-discard fallback) — see that class's doc comment for
/// why. Deliberately a separate, independent implementation rather than a shared library type: the
/// two stores persist different record shapes for different reasons (see <see cref="PairedTerminal"/>'s
/// doc comment on why this one is smaller), and this repo already has one instance of "duplicate a
/// small persistence class rather than force a shared abstraction across Caster/Terminal" precedent
/// (<see cref="TerminalDiscoveryClient"/>'s own doc comment on not sharing pending-request tracking
/// with Terminal's <c>DiscoveryService</c>).
///
/// Uses its own file name (<c>caster-paired-terminals.json</c>, not Terminal's
/// <c>paired-devices.json</c>) precisely because both processes could in principle run on the same
/// machine (nothing stops someone from running both the Caster and the Terminal build on one PC for
/// testing) and both default to the same <see cref="Environment.SpecialFolder.CommonApplicationData"/>
/// base directory — a shared file name would have them silently overwrite each other's persisted
/// state despite storing semantically unrelated things.
/// </summary>
public sealed class PairedTerminalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storePath;
    private List<PairedTerminal> _terminals;

    public PairedTerminalStore(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "caster-paired-terminals.json");
        _terminals = Load();
    }

    public IReadOnlyList<PairedTerminal> All => _terminals;

    public PairedTerminal? Find(Guid deviceId) => _terminals.FirstOrDefault(t => t.DeviceId == deviceId);

    public void Upsert(PairedTerminal terminal)
    {
        _terminals.RemoveAll(t => t.DeviceId == terminal.DeviceId);
        _terminals.Add(terminal);
        Save();
    }

    public void Remove(Guid deviceId)
    {
        _terminals.RemoveAll(t => t.DeviceId == deviceId);
        Save();
    }

    private List<PairedTerminal> Load()
    {
        if (!File.Exists(_storePath)) return new List<PairedTerminal>();

        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<List<PairedTerminal>>(stream, JsonOptions) ?? new List<PairedTerminal>();
        }
        catch (JsonException)
        {
            // Same fail-safe as PairedDeviceStore/ScenarioRepository: a corrupt file must not
            // crash-loop the app on every startup. Keep the bad file around (renamed) rather than
            // silently discard it, in case whatever corrupted it is worth investigating later.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new List<PairedTerminal>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Different failure mode from JsonException above, and deliberately NOT treated the same
            // way: File.Exists returning true doesn't mean File.OpenRead can actually succeed —
            // another process can hold an exclusive lock (antivirus scan, backup tool), or a
            // permissions problem can block the read outright. This says nothing about whether the
            // file's content is bad, so renaming it aside like the JsonException branch does would
            // risk permanently hiding a perfectly good paired-terminal list under a filename Load()
            // never looks for again just because it was momentarily locked. Fail safe to an empty
            // list for this run only, and leave the file itself untouched so a later restart (once
            // whatever is locking it lets go) has a chance to read it normally.
            return new List<PairedTerminal>();
        }
    }

    private void Save()
    {
        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _storePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, _terminals, JsonOptions);
        }

        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, _storePath);
    }
}
