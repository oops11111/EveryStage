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
    private bool _loadFailed;

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
        // Guard mirrors Terminal's PairedDeviceStore: after a failed load (a transient lock the
        // constructor already degraded to an empty list over, not a genuinely empty file), refuse to
        // Save rather than let this empty-plus-one in-memory state atomically overwrite — and destroy
        // — the real on-disk paired-terminal list. See PairedDeviceStore.Upsert's own guard.
        if (_loadFailed) throw new IOException("配对记录读取失败，已阻止覆盖原文件；请先修复文件或重新配对。");
        var snapshot = _terminals.ToList();
        _terminals.RemoveAll(t => t.DeviceId == terminal.DeviceId);
        _terminals.Add(terminal);
        try { Save(); }
        catch { _terminals = snapshot; throw; }
    }

    public void Remove(Guid deviceId)
    {
        if (_loadFailed) throw new IOException("配对记录读取失败，已阻止覆盖原文件；请先修复文件或重新配对。");
        var snapshot = _terminals.ToList();
        _terminals.RemoveAll(t => t.DeviceId == deviceId);
        try { Save(); }
        catch { _terminals = snapshot; throw; }
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
            _loadFailed = true;
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
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, _terminals, JsonOptions);
                // Flushed all the way to the device, not just out of the .NET buffer — same reason as
                // Terminal's PairedDeviceStore.Save (and the four Data stores): File.Replace's rename is
                // a journalled NTFS metadata operation, but the temp file's DATA blocks are not
                // journalled with it, so a power loss in the window where the rename is durable and the
                // contents are not would atomically replace a good file with a truncated/zero-length one.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_storePath))
                File.Replace(tempPath, _storePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _storePath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
