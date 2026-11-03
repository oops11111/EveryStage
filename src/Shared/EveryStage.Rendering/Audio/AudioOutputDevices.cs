using NAudio.CoreAudioApi;

namespace EveryStage.Rendering.Audio;

/// <summary>A render endpoint's stable Windows ID and display name, independent of COM lifetime.</summary>
public sealed record AudioOutputDevice(string Id, string Name);

public static class AudioOutputDevices
{
    /// <summary>Returns currently active output endpoints. Enumeration failures are reported to the caller.</summary>
    public static IReadOnlyList<AudioOutputDevice> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioOutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
        }
        return devices;
    }
}
