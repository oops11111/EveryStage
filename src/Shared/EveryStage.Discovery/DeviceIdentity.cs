using System.Text.Json;

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
    public string DeviceName { get; init; } = Environment.MachineName;

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
                if (loaded != null && loaded.DeviceId != Guid.Empty) return loaded;
            }
            catch (JsonException) { } // corrupt file — fall through and mint a fresh identity.
        }

        var identity = new DeviceIdentity { DeviceId = Guid.NewGuid(), DeviceName = Environment.MachineName };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(identity));
        return identity;
    }
}
