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

    /// <summary>OP-27: no new decoded frame for this long while a device holds Program counts as
    /// a stalled signal — shown long before the 10 s <see cref="CastTimeout"/> ends the session.
    /// (Whether the frozen frame then persists or the session drops is a real-machine question;
    /// the indicator itself is the fix here.)</summary>
    private static readonly TimeSpan SignalStallAfter = TimeSpan.FromSeconds(2);

    private long _lastFramesDecoded;
    private DateTime _lastFrameAt = DateTime.UtcNow;
    private bool _everDecodedFrame;

    private readonly MainWindow _mainWindow;
    private readonly IDeviceCastTransport _transport;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly OutputStateMachine _stateMachine;
    private readonly Guid _terminalDeviceId;
    private readonly Func<OverlayWindow?> _overlay;
    private readonly Func<VideoSurface?> _programSurface;
    private readonly Func<PlaybackEngine?> _playback;
    private readonly Func<VideoSurface, int, int, int, bool, int, int, int, bool, byte, byte, Guid, byte[], byte[], IPAddress, IDeviceCastStream> _streamFactory;
    private readonly Func<DiscoveryService.CastStartInfo, string, bool, OverlayWindow, Task<bool>> _confirmReplacement;
    private PendingReplacement? _pendingReplacement;

    /// <summary>A replacement decision awaiting the operator: its task resolves when the operator
    /// chooses (or dismisses the notice — a dismissal declines). Kept so a superseding connect can
    /// refuse it instead of leaving a stale decision on screen.</summary>
    private sealed record PendingReplacement(TaskCompletionSource<bool> Completion, ToastNotification Toast);

    private IDeviceCastStream? _receiver;
    private VideoSurface? _deviceSurface;
    private Guid _deviceId;
    private string? _deviceName;
    private IPEndPoint? _casterEndPoint;
    private bool _onProgram;
    private PlaybackEngine.Snapshot? _restoreSnapshot;
    private bool _restoreIdle;

    /// <summary>True while a restore attempt's outcome is still unconfirmed (OP-10): the restore
    /// point must survive <see cref="Stop"/> until the restored file genuinely reaches Playing/
    /// Paused, is superseded by a newer take, or is explicitly discarded (operator stop). While
    /// true, <see cref="Stop"/> preserves the point instead of discarding it.</summary>
    private bool _restoreAwaitingConfirmation;

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
        Func<DiscoveryService.CastStartInfo, string, bool, OverlayWindow, Task<bool>>? confirmReplacement = null,
        Func<int, int, EveryStage.Rendering.Audio.AudioPlaybackClock>? audioOutputFactory = null,
        Action<string>? audioRecovered = null)
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
                audioIsAac, payloadType, audioPayloadType, mediaSessionId, videoKey, audioKey, allowedSender, audioOutputFactory, audioRecovered));
        _confirmReplacement = confirmReplacement ?? ShowReplacementToastAsync;
    }

    public bool HasReceiver => _receiver != null;
    public bool IsOnProgram => _onProgram;
    public IPEndPoint? CasterEndPoint => _casterEndPoint;
    public Guid DeviceId => _deviceId;

    public event Action? SessionStarted;
    public event Action? SessionEnded;

    public void OnDefaultAudioOutputChanged() => _receiver?.RequestDefaultAudioMigration();

    public void ResizeDeviceSurface(int width, int height)
    {
        try { _deviceSurface?.Resize(width, height); }
        catch (Exception) { }
    }

    /// <summary>Starts a cast request. DEV-06: the replacement decision is a non-modal notice, so
    /// this awaits the operator instead of blocking the UI thread on a dialog — the current picture
    /// stays until the operator decides, a pending decision never auto-proceeds, and F12 keeps
    /// working throughout. Must be called on the UI thread (Program.cs posts it there); the awaits
    /// resume on that same context, so the control creation below stays on the UI thread.</summary>
    public async Task ConnectAsync(DiscoveryService.CastStartInfo info)
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
            // A decision is already pending for another device: refuse at once instead of stacking
            // a second decision the operator has not seen.
            if (_pendingReplacement != null)
            {
                info.CompleteStart(false, "终端操作员正在确认另一台设备的投屏请求，请稍后再试。", false);
                return;
            }
            info.CompleteStart(false, null, true);
            string current = _deviceName ?? _deviceId.ToString();
            var receiverAtRequest = _receiver;
            bool accepted;
            try { accepted = await _confirmReplacement(info, current, _onProgram, overlay); }
            catch (Exception ex)
            {
                info.CompleteStart(false, ex.Message, false);
                return;
            }
            if (!accepted)
            {
                info.CompleteStart(false, "终端操作员拒绝了投屏请求。", false);
                return;
            }
            // A different receiver took over while the operator decided (a same-device reconnect
            // replaces directly): the request this decision was about is stale — refuse it rather
            // than tearing down the live session.
            if (!ReferenceEquals(_receiver, receiverAtRequest))
            {
                info.CompleteStart(false, "投屏请求已过时，另一路投屏已接管。", false);
                return;
            }
            RestoreProgram();
        }
        else
        {
            // A direct connect (same device, or no current receiver) supersedes any pending
            // decision: refuse it — its request no longer describes the live receiver.
            RefusePendingReplacement();
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
        // Fresh stall sampling for this session (OP-27): the previous session's frame count
        // must not make a new session look stalled before its first frame.
        _lastFramesDecoded = _receiver.FramesDecoded;
        _lastFrameAt = DateTime.UtcNow;
        _everDecodedFrame = false;
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
        _restoreAwaitingConfirmation = false; // a fresh capture replaces any pending point
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
            _restoreAwaitingConfirmation = false;
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
        SampleFrameProgress();
        if (DateTime.UtcNow - _receiver.LastPacketReceivedAt < CastTimeout) return;
        RestoreProgram();
        Stop("caster_silent_" + (int)CastTimeout.TotalSeconds + "s");
    }

    /// <summary>Samples the decoded-frame counter (OP-27): the 1 s liveness tick is the only
    /// regular heartbeat this controller has, so the "last new frame" time is refreshed here.
    /// A session that never decoded a frame is not "stalled" — it is still starting up.</summary>
    private void SampleFrameProgress()
    {
        if (_receiver == null) return;
        long decoded = _receiver.FramesDecoded;
        if (decoded == _lastFramesDecoded) return;
        _lastFramesDecoded = decoded;
        _lastFrameAt = DateTime.UtcNow;
        if (decoded > 0) _everDecodedFrame = true;
    }

    /// <summary>True when a device holds Program but no new frame has been decoded for over
    /// <see cref="SignalStallAfter"/> — the picture on air is frozen, whatever the 10 s
    /// session timeout has yet to decide. Read from the liveness tick, after
    /// <see cref="CheckLiveness"/> has sampled the frame counter.</summary>
    public bool IsSignalStalled => _receiver != null && _onProgram && _everDecodedFrame
        && DateTime.UtcNow - _lastFrameAt >= SignalStallAfter;

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
            KeyframeRequests = _receiver.FrameStatistics.KeyframeRequests,
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
        // A restore whose outcome is unconfirmed keeps its point through a session end (OP-10):
        // the point is the operator's only way back to the pre-cast content, and a failed restore
        // must not silently eat it. An operator stop discards it via OnOutputStateChanged(Idle)'s
        // own DropFromProgram(discardRestorePoint: true) in Program.cs, which runs before this.
        DropFromProgram(discardRestorePoint: !_restoreAwaitingConfirmation);
        if (_receiver != null)
        {
            _connectionLog.LogDisconnected(_deviceId.ToString(), "cast_ended: " + reason);
            _receiver.Dispose();
            _connectionLog.LogCastFrames(_deviceId.ToString(), reason, _receiver.FrameStatistics);
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

    /// <summary>The replacement decision as a sticky notice (DEV-06): 接受 / 拒绝 actions, and
    /// dismissing it without choosing declines. The notice stays until the operator acts, the
    /// current picture stays until then, and no choice ever auto-proceeds.</summary>
    private Task<bool> ShowReplacementToastAsync(DiscoveryService.CastStartInfo info, string currentDeviceName, bool currentOnProgram, OverlayWindow overlay)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string where = currentOnProgram ? "正在大屏上" : "正在预览";
        var toast = _mainWindow.ShowStickyToast(
            $"“{info.DeviceName}”请求投屏，是否替换当前设备“{currentDeviceName}”（{where}）？",
            ToastSeverity.Warning,
            new ToastAction("接受", () => completion.TrySetResult(true)),
            new ToastAction("拒绝", () => completion.TrySetResult(false)));
        var pending = new PendingReplacement(completion, toast);
        _pendingReplacement = pending;
        toast.Dismissed += () =>
        {
            if (ReferenceEquals(_pendingReplacement, pending)) _pendingReplacement = null;
            completion.TrySetResult(false);
        };
        return completion.Task;
    }

    /// <summary>Refuses a pending replacement decision (a direct connect superseded it): dismissing
    /// the notice resolves its task as refused.</summary>
    private void RefusePendingReplacement()
    {
        var pending = _pendingReplacement;
        _pendingReplacement = null;
        pending?.Toast.Dismiss();
    }

    private void RestoreProgram()
    {
        if (!_onProgram) return;

        DropFromProgram(discardRestorePoint: false);
        if (_restoreSnapshot is { } snapshot)
        {
            var playback = _playback();
            if (playback != null && playback.RequestPlay(snapshot, PlaybackTrigger.DeviceRestore))
            {
                // The point is kept until the restore genuinely succeeds (OP-10): RequestPlay's
                // bool is only "accepted for loading", and a load that then fails used to consume
                // the point and leave a black overlay claimed live. The paused state needs no
                // explicit Pause() here — the engine arms it inside RequestPlay (OP-01).
                _restoreAwaitingConfirmation = true;
                ArmRestoreConfirmation(playback, snapshot);
            }
        }
        else if (_restoreIdle)
        {
            _stateMachine.Disconnect();
            _restoreSnapshot = null;
            _restoreIdle = false;
        }
        // NOTE: no unconditional clear of _restoreSnapshot here anymore — it survives until the
        // confirmation handler clears it on success, a newer TakeToProgram replaces it, or an
        // explicit discard (operator stop / Stop with no pending confirmation) drops it.
    }

    /// <summary>Watches the restore attempt's outcome (OP-10). Success (Playing/Paused with the
    /// snapshot's file current) clears the point; a newer take superseding it clears the point
    /// too; a stop detaches without touching the point (the stop path owns the discard decision).
    /// A failure keeps the point and offers a retry that replays the same snapshot — the
    /// generic abnormal-interruption toast fires as well, but it says nothing about the restore
    /// point, so this one names it.</summary>
    private void ArmRestoreConfirmation(PlaybackEngine playback, PlaybackEngine.Snapshot snapshot)
    {
        void OnState(PlaybackChannelState state)
        {
            if (_mainWindow.InvokeRequired)
            {
                _mainWindow.BeginInvoke(new Action<PlaybackChannelState>(OnState), state);
                return;
            }
            if (!ReferenceEquals(playback.CurrentFile, snapshot.File))
            {
                if (state == PlaybackChannelState.Idle) { playback.StateChanged -= OnState; return; }
                // Superseded by a newer take: the point belongs to the old content.
                _restoreSnapshot = null;
                _restoreIdle = false;
                _restoreAwaitingConfirmation = false;
                playback.StateChanged -= OnState;
                return;
            }
            if (state == PlaybackChannelState.Loading) return;
            if (state == PlaybackChannelState.Failed)
            {
                _mainWindow.ShowToast(
                    $"恢复失败：{Path.GetFileName(snapshot.File.SourcePath)}，已保留恢复点，可重试恢复。",
                    ToastSeverity.Warning,
                    new ToastAction("重试恢复", RetryRestore));
                return; // stays armed: a retry replays this same snapshot
            }
            if (state is PlaybackChannelState.Playing or PlaybackChannelState.Paused)
            {
                _restoreSnapshot = null;
                _restoreIdle = false;
                _restoreAwaitingConfirmation = false;
                playback.StateChanged -= OnState;
            }
        }
        playback.StateChanged += OnState;
        // A load that already reached its terminal state synchronously (a static document, a
        // synchronous open failure) fires no further transition — confirm against the state as
        // it stands right now, or the point would linger until the next take.
        OnState(playback.State);
    }

    private void RetryRestore()
    {
        if (_onProgram || _restoreSnapshot is not { } snapshot) return;
        var playback = _playback();
        if (playback == null) return;
        // An attempt already loading this snapshot needs no second one.
        if (playback.State == PlaybackChannelState.Loading && ReferenceEquals(playback.CurrentFile, snapshot.File)) return;
        // No re-arm: the confirmation handler from the original attempt stays attached across
        // a failure and observes this replay's outcome. Re-arming here would stack a second
        // handler and a second failure toast on the next failure. A decline (cast switch off)
        // fires the cast-switch toast instead.
        playback.RequestPlay(snapshot, PlaybackTrigger.DeviceRestore);
    }

    public void Dispose() => Stop("terminal_exit");
}
