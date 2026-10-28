using System.Net;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI;

namespace EveryStage.Terminal.Receiving;

/// <summary>UI-thread coordinator for the device-cast preview window and Program attach/detach flow.</summary>
internal sealed class DeviceCastController : IDisposable
{
    private static readonly TimeSpan CastTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConsecutiveDecodeErrorsBeforeDisconnect = 90;

    private readonly MainWindow _mainWindow;
    private readonly IDeviceCastTransport _transport;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly OutputStateMachine _stateMachine;
    private readonly Guid _terminalDeviceId;
    private readonly Func<OverlayWindow?> _overlay;
    private readonly Func<VideoSurface?> _programSurface;
    private readonly Func<PlaybackEngine?> _playback;
    private readonly Func<VideoSurface, int, int, int, bool, int, int, int, bool, byte, byte, Guid, byte[], byte[], IPAddress, IDeviceCastStream> _streamFactory;
    private readonly Func<DiscoveryService.CastStartInfo, string, bool, OverlayWindow, bool> _confirmReplacement;

    private IDeviceCastStream? _receiver;
    private VideoSurface? _deviceSurface;
    private Guid _deviceId;
    private string? _deviceName;
    private IPEndPoint? _casterEndPoint;
    private bool _onProgram;
    private PlaybackEngine.Snapshot? _restoreSnapshot;
    private bool _restoreIdle;

    public DeviceCastController(
        MainWindow mainWindow,
        Guid terminalDeviceId,
        IDeviceCastTransport transport,
        DeviceConnectionLogger connectionLog,
        OutputStateMachine stateMachine,
        Func<OverlayWindow?> overlay,
        Func<VideoSurface?> programSurface,
        Func<PlaybackEngine?> playback,
        Func<VideoSurface, int, int, int, bool, int, int, int, bool, byte, byte, Guid, byte[], byte[], IPAddress, IDeviceCastStream>? streamFactory = null,
        Func<DiscoveryService.CastStartInfo, string, bool, OverlayWindow, bool>? confirmReplacement = null)
    {
        _mainWindow = mainWindow;
        _terminalDeviceId = terminalDeviceId;
        _transport = transport;
        _connectionLog = connectionLog;
        _stateMachine = stateMachine;
        _overlay = overlay;
        _programSurface = programSurface;
        _playback = playback;
        _streamFactory = streamFactory ?? ((surface, width, height, videoPort, hasAudio, audioSampleRate, audioChannels, audioPort,
            audioIsAac, payloadType, audioPayloadType, mediaSessionId, videoKey, audioKey, allowedSender) =>
            new CastReceiverStream(surface, width, height, videoPort, hasAudio, audioSampleRate, audioChannels, audioPort,
                audioIsAac, payloadType, audioPayloadType, mediaSessionId, videoKey, audioKey, allowedSender));
        _confirmReplacement = confirmReplacement ?? ShowReplacementDialog;
    }

    public bool HasReceiver => _receiver != null;
    public bool IsOnProgram => _onProgram;
    public IPEndPoint? CasterEndPoint => _casterEndPoint;
    public Guid DeviceId => _deviceId;

    public event Action? SessionStarted;
    public event Action? SessionEnded;

    public void ResizeDeviceSurface(int width, int height)
    {
        try { _deviceSurface?.Resize(width, height); }
        catch (Exception) { }
    }

    public void Connect(DiscoveryService.CastStartInfo info)
    {
        var overlay = _overlay();
        var programSurface = _programSurface();
        if (overlay == null || programSurface == null)
        {
            info.CompleteStart(false, "终端机未绑定扩展显示器。", false);
            return;
        }

        bool sameDeviceWasOnProgram = _receiver != null && info.DeviceId == _deviceId && _onProgram;
        var retainedRestoreSnapshot = _restoreSnapshot;
        bool retainedRestoreIdle = _restoreIdle;

        if (_receiver != null && info.DeviceId != _deviceId)
        {
            info.CompleteStart(false, null, true);
            string current = _deviceName ?? _deviceId.ToString();
            if (!_confirmReplacement(info, current, _onProgram, overlay))
            {
                info.CompleteStart(false, "终端操作员拒绝了投屏请求。", false);
                return;
            }
            RestoreProgram();
        }

        Stop("replaced_by_new_cast");
        try
        {
            _deviceSurface?.Dispose();
            _deviceSurface = new VideoSurface(
                programSurface.Gpu,
                _mainWindow.DeviceCastVideoHost.Handle,
                Math.Max(1, _mainWindow.DeviceCastVideoHost.ClientSize.Width),
                Math.Max(1, _mainWindow.DeviceCastVideoHost.ClientSize.Height))
            {
                ScaleMode = VideoScaleMode.Stretch,
                ColorMatrix = VideoColorMatrix.Bt601,
            };
            _receiver = _streamFactory(
                _deviceSurface, info.Width, info.Height, DiscoveryProtocol.VideoRtpPort,
                info.HasAudio, info.AudioSampleRate, info.AudioChannels, DiscoveryProtocol.AudioRtpPort,
                info.AudioIsAac, info.PayloadType, info.AudioPayloadType,
                info.MediaSessionId, info.VideoKey, info.AudioKey, info.CasterEndPoint.Address);
            _receiver.AudioMuted = true;
            _receiver.VideoPacketsMissing += missing =>
                _ = _transport.SendCastNackAsync(info.CasterEndPoint, info.MediaSessionId, missing);
            string castingDevice = info.DeviceId.ToString();
            _receiver.AudioDegraded += reason => _connectionLog.LogCastAudioLost(castingDevice, reason);
            _receiver.Start();
        }
        catch (Exception ex)
        {
            _receiver?.Dispose();
            _receiver = null;
            info.CompleteStart(false, ex.Message, false);
            return;
        }

        _deviceId = info.DeviceId;
        _deviceName = info.DeviceName;
        _casterEndPoint = info.CasterEndPoint;
        _connectionLog.LogConnected(info.DeviceId.ToString());
        SessionStarted?.Invoke();
        if (sameDeviceWasOnProgram)
        {
            _restoreSnapshot = retainedRestoreSnapshot;
            _restoreIdle = retainedRestoreIdle;
            AttachToProgram(programSurface, overlay);
        }
        _mainWindow.UpdateDeviceCast(_deviceName, connected: true, onProgram: _onProgram);
        info.CompleteStart(true, null, false);
    }

    public void StopForDevice(Guid deviceId)
    {
        if (_receiver == null || deviceId != _deviceId) return;
        RestoreProgram();
        Stop("cast_stop_received");
    }

    public void TakeToProgram()
    {
        var programSurface = _programSurface();
        var overlay = _overlay();
        if (_receiver == null || programSurface == null || overlay == null) return;

        _restoreSnapshot = _playback()?.CaptureSnapshot();
        _restoreIdle = _restoreSnapshot == null;
        _playback()?.StopForDeviceCast();
        AttachToProgram(programSurface, overlay);
        _mainWindow.UpdateDeviceCast(_deviceName, connected: true, onProgram: true);
    }

    public void RevertProgram() => RestoreProgram();

    public void DropFromProgram(bool discardRestorePoint)
    {
        if (!_onProgram) return;
        _receiver?.DetachProgram();
        if (_receiver != null) _receiver.AudioMuted = true;
        _onProgram = false;
        if (discardRestorePoint)
        {
            _restoreSnapshot = null;
            _restoreIdle = false;
        }
        _mainWindow.UpdateDeviceCast(_deviceName, connected: _receiver != null, onProgram: false);
    }

    public void RetractForCastSwitchOff()
    {
        if (!_onProgram) return;
        RestoreProgram();
    }

    public void CheckLiveness()
    {
        if (_receiver == null) return;
        if (DateTime.UtcNow - _receiver.LastPacketReceivedAt < CastTimeout) return;
        RestoreProgram();
        Stop("caster_silent_" + (int)CastTimeout.TotalSeconds + "s");
    }

    public void CheckDecodeHealth()
    {
        if (_receiver == null) return;
        if (_receiver.ConsecutiveVideoDecodeErrors < MaxConsecutiveDecodeErrorsBeforeDisconnect
            && !_receiver.PresentLoopFailed
            && !_receiver.DecodeLoopFailed)
        {
            return;
        }

        RestoreProgram();
        Stop(_receiver.PresentLoopFailed ? "present_loop_failed" : _receiver.DecodeLoopFailed ? "decode_loop_failed" : "persistent_video_decode_errors");
    }

    public void SendStatus()
    {
        if (_receiver == null || _casterEndPoint == null) return;
        var status = new DiscoveryProtocol.CastStatusMessage
        {
            DeviceId = _terminalDeviceId,
            SentAtUtc = DateTimeOffset.UtcNow,
            FramesDecoded = _receiver.FramesDecoded,
            OnProgram = _onProgram,
            VideoBytesReceived = _receiver.BytesReceived,
            VideoError = _receiver.LastError,
            HasAudio = _receiver.HasAudio,
            AudioBytesReceived = _receiver.AudioBytesReceived,
            AudioError = _receiver.AudioError,
            PayloadTypeMismatches = _receiver.PayloadTypeMismatches,
            EstimatedPacketLossPercent = _receiver.EstimatedPacketLossPercent,
            VideoPacketsLost = _receiver.VideoPacketsLost,
        };
        _ = _transport.SendCastStatusAsync(_casterEndPoint, status);
    }

    public async Task LogConnectionQualityAsync()
    {
        if (_receiver == null || _casterEndPoint is not { } casterEndPoint) return;
        Guid deviceId = _deviceId;
        var receiver = _receiver;
        TimeSpan? rtt;
        try { rtt = await _transport.PingAsync(casterEndPoint); }
        catch (Exception) { rtt = null; }
        if (_receiver != receiver || _deviceId != deviceId) return;
        _connectionLog.LogQualityMetric(deviceId.ToString(), receiver.EstimatedPacketLossPercent, rtt?.TotalMilliseconds);
    }

    public void Stop(string reason)
    {
        DropFromProgram(discardRestorePoint: true);
        if (_receiver != null)
        {
            _connectionLog.LogDisconnected(_deviceId.ToString(), "cast_ended: " + reason);
            _receiver.Dispose();
        }
        _receiver = null;
        _deviceSurface?.Dispose();
        _deviceSurface = null;
        _deviceName = null;
        _casterEndPoint = null;
        _mainWindow.UpdateDeviceCast(null, connected: false, onProgram: false);
        SessionEnded?.Invoke();
    }

    private void AttachToProgram(VideoSurface programSurface, OverlayWindow overlay)
    {
        programSurface.ScaleMode = VideoScaleMode.Stretch;
        programSurface.ColorMatrix = VideoColorMatrix.Bt601;
        _receiver!.AttachProgram(programSurface);
        _receiver.AudioMuted = false;
        overlay.ShowVideoSurface();
        _stateMachine.AcceptDeviceCastRequest();
        _onProgram = true;
    }

    private bool ShowReplacementDialog(DiscoveryService.CastStartInfo info, string currentDeviceName, bool currentOnProgram, OverlayWindow overlay)
    {
        using var dialog = new DeviceCastRequestDialog(info.DeviceName, currentDeviceName, currentOnProgram);
        OperatorScreen.PlaceOnOperatorScreen(dialog, _mainWindow, overlay.Monitor);
        dialog.ShowDialog(_mainWindow);
        return dialog.Accepted;
    }

    private void RestoreProgram()
    {
        if (!_onProgram) return;

        DropFromProgram(discardRestorePoint: false);
        if (_restoreSnapshot is { } snapshot)
        {
            if (_playback()?.RequestPlay(snapshot, PlaybackTrigger.DeviceRestore) == true && snapshot.Paused)
                _playback()?.Pause();
        }
        else if (_restoreIdle)
        {
            _stateMachine.Disconnect();
        }
        _restoreSnapshot = null;
        _restoreIdle = false;
    }

    public void Dispose() => Stop("terminal_exit");
}
