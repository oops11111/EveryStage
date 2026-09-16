using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryStage.Discovery;

/// <summary>
/// A device's own stable identity ("设备指纹" — PLANNING.md §7: "配对后记住设备指纹"). Generated once
/// and persisted so a peer that paired with this machine yesterday still recognizes it today even
/// after a restart — identity must survive a restart for "已配对设备可设信任模式" to mean anything.
///
/// Shared between Terminal and Caster (identical logic, just a different file so the two don't
/// collide when both happen to run on the same machine, e.g. during development/testing).
/// </summary>
public sealed class DeviceIdentity
{
    public Guid DeviceId { get; init; }

    /// <summary>Mutable (not <c>init</c>) — Terminal's 设置 panel lets the operator rename this
    /// device; call <see cref="Save"/> after changing it to persist the rename.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    // Where this instance was loaded from/created at — not serialized, so it survives round-trips
    // through JSON only as "whatever path LoadOrCreate used", never read back from the file itself.
    [JsonIgnore]
    private string? _persistedPath;

    /// <param name="fileNameStem">Distinguishes Terminal's identity file from Caster's under the
    /// shared ProgramData\EveryStage\ folder, e.g. "terminal" -> terminal-identity.json.</param>
    public static DeviceIdentity LoadOrCreate(string fileNameStem, string? pathOverride = null)
    {
        string path = pathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", $"{fileNameStem}-identity.json");

        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path));
                if (loaded != null && loaded.DeviceId != Guid.Empty)
                {
                    loaded._persistedPath = path;
                    return loaded;
                }
            }
            catch (JsonException) { } // corrupt file — fall through and mint a fresh identity.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Different failure mode from JsonException above, and deliberately NOT treated the
                // same way: File.Exists returning true doesn't mean File.ReadAllText can actually
                // succeed — another process can hold an exclusive lock (antivirus scan, backup tool),
                // or a permissions problem can block the read outright. This says nothing about
                // whether the file's content is bad, so falling through to the mint-and-immediately-
                // overwrite path below would permanently destroy a perfectly good, already-paired
                // device identity just because reading it happened to race with something else
                // touching the file — every peer that recognized this device by its old DeviceId
                // would silently stop recognizing it after that. Return a fresh in-memory identity
                // for this run only (so pairing during this session still has SOME identity to work
                // with, rather than crashing the whole process at startup — the actual bug this catch
                // exists to fix), but skip persisting it: a later restart, once whatever is locking
                // the file lets go, still gets a chance to read the real one back correctly, instead
                // of today's transient failure getting baked into tomorrow's identity too.
                return new DeviceIdentity { DeviceId = Guid.NewGuid(), DeviceName = Environment.MachineName, _persistedPath = path };
            }
        }

        var identity = new DeviceIdentity { DeviceId = Guid.NewGuid(), DeviceName = Environment.MachineName, _persistedPath = path };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, identity);
        return identity;
    }

    /// <summary>Persists the current <see cref="DeviceName"/> (the only field meant to change after
    /// creation) back to the file this identity was loaded from or created at. Throws if this
    /// instance wasn't obtained via <see cref="LoadOrCreate"/> — there's no other valid path to
    /// write to.</summary>
    public void Save()
    {
        if (_persistedPath == null)
            throw new InvalidOperationException("DeviceIdentity.Save() requires an instance obtained via LoadOrCreate.");
        WriteAtomic(_persistedPath, this);
    }

    /// <summary>Bug fixed here: both call sites above used to write with plain
    /// <see cref="File.WriteAllText(string, string)"/>, unlike every other JSON-backed store in this
    /// codebase (<c>FileLibraryStore</c>/<c>ScenarioRepository</c>/<c>SettingsStore</c>/
    /// <c>PairedDeviceStore</c>/<c>PairedTerminalStore</c>), which all write to a <c>.tmp</c> file
    /// first and only then atomically <see cref="File.Replace"/>/<see cref="File.Move"/> it into
    /// place. A crash or power loss mid-<c>WriteAllText</c> (which truncates the destination file
    /// before writing the new content) can leave this specific file — this device's own permanent
    /// identity, the one file in this whole codebase whose corruption/loss this project's own
    /// <see cref="LoadOrCreate"/> fix (see this class's <c>catch (Exception ex) when (ex is
    /// IOException or UnauthorizedAccessException)</c> branch) already goes out of its way to avoid —
    /// truncated or empty. <see cref="LoadOrCreate"/> would then either hit its <c>JsonException</c>
    /// branch or successfully parse a near-empty object, and in both cases mint a brand-new identity
    /// on the very next boot, permanently losing every peer's trust relationship with this device's
    /// old one. Matching the atomic-write convention every other store in this codebase already uses
    /// removes that risk the same way it already removes it for them.</summary>
    private static void WriteAtomic(string path, DeviceIdentity identity)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(identity));

        if (File.Exists(path))
            File.Replace(tempPath, path, destinationBackupFileName: null);
        else
            File.Move(tempPath, path);
    }
}
