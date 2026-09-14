using System.Text.Json;

namespace EveryStage.Terminal.Devices;

/// <summary>
/// This Terminal's own stable identity ("设备指纹" — PLANNING.md §7: "配对后记住设备指纹"). Generated
/// once and persisted so a Caster that paired with this machine yesterday still recognizes it today
/// even if the Terminal restarts — identity must survive a restart for "已配对设备可设信任模式" to
/// mean anything.
/// </summary>
public sealed class DeviceIdentity
{
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = Environment.MachineName;

    public static DeviceIdentity LoadOrCreate(string? pathOverride = null)
    {
        string path = pathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EveryStage", "device-identity.json");

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
