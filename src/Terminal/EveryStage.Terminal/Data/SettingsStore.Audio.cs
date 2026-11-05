namespace EveryStage.Terminal.Data;

public sealed partial class SettingsStore
{
    internal EveryStage.Rendering.Audio.AudioPlaybackClock OpenAudioOutput(int sampleRate, int channels)
    {
        string? id = Current.AudioOutputDeviceId;
        if (string.IsNullOrWhiteSpace(id)) return new(sampleRate, channels);
        try { return new(sampleRate, channels, id); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            var fallback = new EveryStage.Rendering.Audio.AudioPlaybackClock(sampleRate, channels);
            NotifyAudioOutputFallback("所选音频设备不可用，已切换到系统默认设备；可在设置中重新选择。");
            return fallback;
        }
    }
}
