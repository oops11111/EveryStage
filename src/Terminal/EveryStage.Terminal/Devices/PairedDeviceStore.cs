using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryStage.Terminal.Devices;

/// <summary>
/// JSON persistence for the paired-device list (PLANNING.md §7), mirroring
/// <c>Data/ScenarioRepository</c>'s atomic-write-on-an-unattended-device approach: write to a temp
/// file then replace, so a crash mid-write can't corrupt the trust list an unattended Terminal
/// depends on to keep auto-accepting only the devices it's actually supposed to.
/// </summary>
public sealed class PairedDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _storePath;
    private List<PairedDevice> _devices;

    public PairedDeviceStore(string? storePathOverride = null)
    {
        _storePath = storePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "paired-devices.json");
        _devices = Load();
    }

    public PairedDevice? Find(Guid deviceId) => _devices.FirstOrDefault(d => d.DeviceId == deviceId);

    public IReadOnlyList<PairedDevice> All => _devices;

    public void Upsert(PairedDevice device)
    {
        _devices.RemoveAll(d => d.DeviceId == device.DeviceId);
        _devices.Add(device);
        Save();
    }

    public void Remove(Guid deviceId)
    {
        _devices.RemoveAll(d => d.DeviceId == deviceId);
        Save();
    }

    private List<PairedDevice> Load()
    {
        if (!File.Exists(_storePath)) return new List<PairedDevice>();

        try
        {
            using var stream = File.OpenRead(_storePath);
            return JsonSerializer.Deserialize<List<PairedDevice>>(stream, JsonOptions) ?? new List<PairedDevice>();
        }
        catch (JsonException)
        {
            // Same fail-safe as ScenarioRepository: an unattended device must not crash-loop on a
            // corrupt trust list. Keep the bad file around (renamed) rather than silently discard it.
            File.Copy(_storePath, _storePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new List<PairedDevice>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Different failure mode from JsonException above, and deliberately NOT treated the same
            // way: File.Exists returning true doesn't mean File.OpenRead can actually succeed —
            // another process can hold an exclusive lock (antivirus scan, backup tool), or a
            // permissions problem can block the read outright. This says nothing about whether the
            // file's content is bad, so renaming it aside like the JsonException branch does would
            // risk permanently hiding a perfectly good trust list under a filename Load() never looks
            // for again just because it was momentarily locked. Fail safe to an empty list for this
            // run only, and leave the file itself untouched so a later restart (once whatever is
            // locking it lets go) has a chance to read it normally.
            return new List<PairedDevice>();
        }
    }

    private void Save()
    {
        string directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _storePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, _devices, JsonOptions);
        }

        if (File.Exists(_storePath))
            File.Replace(tempPath, _storePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, _storePath);
    }
}
