using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EveryStage.Terminal.Audio;

/// <summary>Observes the same Windows default render role used by NAudio's WasapiOut constructor.</summary>
internal sealed class DefaultAudioDeviceMonitor : IMMNotificationClient, IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Action<string> _changed;
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private string? _lastId;
    private int _disposed;

    internal DefaultAudioDeviceMonitor(SynchronizationContext ui, Action<string> changed, bool register = true, string? initialId = null)
    {
        _ui = ui;
        _changed = changed;
        _lastId = initialId;
        if (!register) return;
        var enumerator = new MMDeviceEnumerator();
        try
        {
            try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); _lastId = device.ID; }
            catch (System.Runtime.InteropServices.COMException) { _lastId = null; }
            enumerator.RegisterEndpointNotificationCallback(this);
            _enumerator = enumerator;
        }
        catch { enumerator.Dispose(); throw; }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
    {
        if (flow != DataFlow.Render || role != Role.Console || Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (string.Equals(_lastId, deviceId, StringComparison.Ordinal)) return;
            _lastId = deviceId;
        }
        // A null ID means no default exists. Wait until Windows reports an actual replacement.
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        try { _ui.Post(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            lock (_gate)
                if (!string.Equals(_lastId, deviceId, StringComparison.Ordinal)) return;
            _changed(deviceId);
        }, null); }
        catch (InvalidOperationException) { /* The UI may be shutting down. */ }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string deviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var enumerator = Interlocked.Exchange(ref _enumerator, null);
        if (enumerator == null) return;
        try { enumerator.UnregisterEndpointNotificationCallback(this); }
        finally { enumerator.Dispose(); }
    }
}
