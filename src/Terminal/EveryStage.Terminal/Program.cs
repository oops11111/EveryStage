using System.Net;
using EveryStage.Discovery;
using EveryStage.Terminal.Audio;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.Receiving;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.Tray;
using EveryStage.Terminal.UI;

namespace EveryStage.Terminal;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Guarantee a WindowsFormsSynchronizationContext exists on this thread before anything
        // that runs on a background thread (DiscoveryService's UDP loops) needs to marshal work
        // back onto it. WinForms normally installs one automatically the first time a Control is
        // constructed, but that's an implicit ordering detail this code shouldn't depend on —
        // TerminalApplicationContext's constructor may run entirely without creating a Control at
        // all (no extended display -> no OverlayWindow) before it needs to post pairing-dialog work.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var repository = new ScenarioRepository();
        var store = repository.Load();

        using var context = new TerminalApplicationContext(store, repository);
        Application.Run(context);
    }
}

/// <summary>
/// Wires the framework pieces together: state machine, overlay window, audio takeover, device
/// discovery (including the periodic <see cref="SendCastStatus"/> acknowledgment back to whichever
/// Caster is currently casting here — see <c>DiscoveryProtocol.CastStatusMessage</c>), the main
/// window (with its 文件/活动/设备/设置 panels), the floating preview window, and the pairing
/// confirmation dialog.
/// </summary>
internal sealed class TerminalApplicationContext : ApplicationContext
{
    private readonly ScenarioStore _store;
    private readonly ScenarioRepository _repository;
    private readonly SettingsStore _settingsStore;
    private readonly DeviceIdentity _identity;
    private readonly OutputStateMachine _stateMachine = new();
    private readonly AudioTakeoverService _audioTakeover = new();
    private readonly TrayIconController _tray;
    private readonly DiscoveryService _discovery;
    private readonly SynchronizationContext _uiContext;
    private OverlayWindow? _overlay;
    private VideoSurface? _videoSurface;
    private PlaybackEngine? _playback;
    private FloatingPreviewWindow? _previewWindow;
    private readonly MainWindow _mainWindow;
    private CastReceiver? _castReceiver;
    private Guid _castingDeviceId;
    private IPEndPoint? _castingCasterEndPoint;
    private readonly System.Windows.Forms.Timer _castStatusTimer;

    public TerminalApplicationContext(ScenarioStore store, ScenarioRepository repository)
    {
        _store = store;
        _repository = repository;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Expected Program.Main to have installed a WindowsFormsSynchronizationContext first.");

        _settingsStore = new SettingsStore();

        // Applied once, here, at startup — AppSettings.CastSwitchDefaultOn's own doc comment
        // explains why this never runs again after this point (it's a default, not a live toggle).
        _stateMachine.SetCastSwitch(_settingsStore.Current.CastSwitchDefaultOn);

        var extendedDisplay = MonitorService.GetBoundExtendedDisplay(preferredDeviceName: _settingsStore.Current.PreferredMonitorDeviceName);
        if (extendedDisplay != null)
        {
            _overlay = new OverlayWindow(extendedDisplay);
            // One D3D11 device/swap chain for the overlay's video HWND, shared between local video
            // playback and a live device cast — see VideoSurface's doc comment for why this can't
            // be two independent ones anymore.
            _videoSurface = new VideoSurface(_overlay.VideoHost.Handle, _overlay.VideoHost.ClientSize.Width, _overlay.VideoHost.ClientSize.Height);
            _playback = new PlaybackEngine(_stateMachine, _overlay, _videoSurface, _settingsStore);
            _playback.LocalPlaybackStarting += OnLocalPlaybackStarting;
            _previewWindow = new FloatingPreviewWindow(_playback, _stateMachine);
        }
        // extendedDisplay == null: no second monitor attached yet. §5/§7 don't specify a "no
        // display bound" UX beyond implying it's a real, visible configuration state — the tray
        // tooltip below reflects it, but there is nothing further to build here until Phase 4 UI
        // exists to surface a proper "未检测到扩展屏" notice. No overlay also means no
        // VideoSurface/PlaybackEngine/preview window yet, and OnCastStartRequested below already
        // no-ops when _overlay is null; MainWindow's file panel tolerates _playback being null
        // (RequestPlay just never gets called) until a display shows up.

        _stateMachine.StateChanged += OnOutputStateChanged;

        _identity = DeviceIdentity.LoadOrCreate("terminal");
        var pairedDevices = new PairedDeviceStore();
        _discovery = new DiscoveryService(_identity, pairedDevices, new DeviceConnectionLogger());
        _discovery.PairingRequested += OnPairingRequested;
        _discovery.CastStartRequested += OnCastStartRequested;
        _discovery.CastStopRequested += OnCastStopRequested;
        _discovery.Start();

        // Periodic acknowledgment back to whichever Caster is currently casting to this Terminal —
        // see DiscoveryProtocol.CastStatusMessage for why this exists — plus the reverse check
        // (CheckCastLiveness): a Caster that crashes or loses network never gets to send
        // CastStopMessage, and without this a CastReceiver would keep "casting" a frozen last frame
        // forever. CheckDecodeHealth is the third, previously-missing policy check this same timer
        // now also drives (see its own doc comment) — all three started/stopped alongside
        // _castReceiver in StopCasting()/OnCastStartRequested, never left running with nothing to
        // report or check.
        _castStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _castStatusTimer.Tick += (_, _) => { CheckCastLiveness(); CheckDecodeHealth(); SendCastStatus(); };

        var library = new FileLibraryStore();
        _mainWindow = new MainWindow(_stateMachine, _playback, library, pairedDevices, _store, _repository, _settingsStore, _identity);
        _mainWindow.Show();

        _tray = new TrayIconController(_stateMachine);
        _tray.ExitRequested += OnExitRequested;
        _tray.MainWindowRequested += () => { _mainWindow.Show(); _mainWindow.Activate(); };
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state == OutputState.Active)
        {
            _overlay?.ShowOverlay();
            _previewWindow?.ShowForActiveOutput();
            _ = _audioTakeover.TakeoverAsync(); // fire-and-forget: UI thread must not block on this.
        }
        else
        {
            _overlay?.HideOverlay();
            _previewWindow?.HideForIdleOutput();
            _audioTakeover.Restore();
            // "断" must also stop an active device cast — without this, a CastReceiver keeps
            // receiving/decoding/presenting to a now-hidden overlay indefinitely instead of being
            // torn down. This does not send a cast_stop to the Caster — "断" is a local, immediate
            // action and there's no reason to make it wait on a network send; the Caster instead
            // notices via CastStatusMessage reports simply stopping (see LiveCastSession's
            // "terminal went quiet" handling).
            StopCasting();
        }
    }

    private void OnPairingRequested(PairingRequest request)
    {
        // Raised from DiscoveryService's background receive loop — a modal dialog must be shown
        // from the UI thread.
        _uiContext.Post(_ =>
        {
            using var dialog = new PairingConfirmationDialog(request);
            dialog.ShowDialog();
            _discovery.RespondToPairing(request.RequestId, dialog.Accepted, dialog.TrustMode, dialog.AllowCast, dialog.AllowMonitor);
        }, null);
    }

    private void OnCastStartRequested(DiscoveryService.CastStartInfo info)
    {
        // Raised from DiscoveryService's background receive loop — constructing CastReceiver
        // touches the overlay window's HWND and drives the state machine, both of which expect the
        // UI thread.
        _uiContext.Post(_ =>
        {
            if (_overlay == null || _videoSurface == null) return; // no extended display bound — nothing to show a cast on.

            // Stop any local video playback FIRST — it shares _videoSurface's D3D11 device/swap
            // chain with the CastReceiver about to be constructed, and the two must never present
            // concurrently (see VideoSurface's doc comment). This does not touch OutputStateMachine.
            _playback?.StopForDeviceCast();

            StopCasting();
            try
            {
                _castReceiver = new CastReceiver(
                    _videoSurface, info.Width, info.Height, DiscoveryProtocol.VideoRtpPort,
                    info.HasAudio, info.AudioSampleRate, info.AudioChannels, DiscoveryProtocol.AudioRtpPort);
                _castReceiver.Start();
            }
            catch (Exception)
            {
                // Same "don't crash, don't pretend it worked" reasoning as everywhere else in this
                // repo lacking a dedicated cast-session log yet — leaves the Terminal at Idle rather
                // than showing a video surface that will never receive a frame. A pure audio-side
                // failure doesn't reach here — CastReceiver's own constructor already degrades to
                // video-only on an audio construction failure (see its AudioError property), mirroring
                // LiveCastSession's audio-is-best-effort handling on the Caster side; this catch is
                // for video-side failures only, which do end the whole cast (there's no cast without
                // video).
                _castReceiver = null;
                return;
            }

            _castingDeviceId = info.DeviceId;
            _castingCasterEndPoint = info.CasterEndPoint;
            _castStatusTimer.Start();
            _overlay.ShowVideoSurface();
            _stateMachine.AcceptDeviceCastRequest();
        }, null);
    }

    private void OnCastStopRequested(Guid deviceId)
    {
        _uiContext.Post(_ =>
        {
            // Ignore a stop from a device that wasn't the one currently casting — e.g. a stale/
            // duplicated cast_stop arriving after a different device already took over the output.
            if (_castReceiver == null || deviceId != _castingDeviceId) return;

            StopCasting();
            _stateMachine.Disconnect();
        }, null);
    }

    private void OnLocalPlaybackStarting()
    {
        // PlaybackEngine is about to present local content through the shared VideoSurface — any
        // active device cast must stop first, for the same mutual-exclusion reason as
        // OnCastStartRequested stopping local playback in the other direction. This intentionally
        // does not call _stateMachine.Disconnect(): OutputStateMachine.State stays Active (local
        // content is replacing device content, not ending output), and there is no cast_stop sent
        // to the Caster telling it its stream is being ignored now — the Caster instead notices via
        // CastStatusMessage reports simply stopping.
        StopCasting();
    }

    /// <summary>Tears down whatever's currently receiving a device cast, if anything — the one
    /// place that does so, so the status-report timer can never be left running with no
    /// <see cref="_castReceiver"/> to report on (a bug that bit an earlier draft of this class: the
    /// three call sites below used to each repeat "_castReceiver?.Dispose(); _castReceiver = null;"
    /// inline, and it would have been easy to add the timer to two of the three and miss one).</summary>
    private void StopCasting()
    {
        _castStatusTimer.Stop();
        _castingCasterEndPoint = null;
        _castReceiver?.Dispose();
        _castReceiver = null;
    }

    // Picked without any real-network measurement, same as every other timing constant in this
    // repo lacking a Windows machine to tune against (see this project's README) — generous enough
    // to survive a few seconds of network hiccup given there's no jitter buffer on either media
    // stream, short enough that a genuinely-gone Caster doesn't leave a frozen frame on screen for
    // an unreasonable amount of time.
    private static readonly TimeSpan CastTimeout = TimeSpan.FromSeconds(10);

    private void CheckCastLiveness()
    {
        if (_castReceiver == null) return;
        if (DateTime.UtcNow - _castReceiver.LastPacketReceivedAt < CastTimeout) return;

        // Neither video nor audio has produced a single packet in CastTimeout — the Caster is gone
        // (crashed, lost network, or the process was killed before it could send CastStopMessage).
        // Disconnect() also hides the overlay and restores audio takeover via
        // OnOutputStateChanged's Idle branch, same as a manual "断" — a silently frozen last frame
        // with the overlay still up would be worse than falling back to standby.
        StopCasting();
        _stateMachine.Disconnect();
    }

    // Roughly 3 seconds' worth of access units at a typical 30fps encode — this was previously an
    // open product-behavior question this repo explicitly hadn't decided (see this project's
    // README's history): CastReceiver.LastError/AudioError got reported every second via
    // SendCastStatus, but nothing on this side ever acted on persistent failure, so a decoder stuck
    // failing every single frame (a corrupted decoder state, not a one-off bad access unit) would
    // sit there forever showing a frozen/corrupted last frame while dutifully reporting "投屏出错"
    // to a Caster that has no way to fix it remotely. Picked, like CastTimeout above, without any
    // real-network/real-hardware measurement — generous enough that an isolated decode hiccup (a
    // single dropped/corrupt access unit, which CastReceiver's own counters reset back to zero on
    // the very next successful decode) can never trip this, short enough that a genuinely broken
    // decode pipeline doesn't leave a bad frame on screen for an unreasonable amount of time.
    private const int MaxConsecutiveDecodeErrorsBeforeDisconnect = 90;

    private void CheckDecodeHealth()
    {
        if (_castReceiver == null) return;
        if (_castReceiver.ConsecutiveVideoDecodeErrors < MaxConsecutiveDecodeErrorsBeforeDisconnect
            && _castReceiver.ConsecutiveAudioPlaybackErrors < MaxConsecutiveDecodeErrorsBeforeDisconnect)
        {
            return;
        }

        // Same StopCasting()+Disconnect() policy CheckCastLiveness already applies for a Caster
        // that's gone silent — Terminal is meant to run unattended (PLANNING.md's whole framing), so
        // there's nobody watching to notice a stuck decoder and manually hit "断"; falling back to
        // standby on its own is better than staying "connected" to a stream it can no longer render.
        StopCasting();
        _stateMachine.Disconnect();
    }

    private void SendCastStatus()
    {
        if (_castReceiver == null || _castingCasterEndPoint == null) return;

        var status = new DiscoveryProtocol.CastStatusMessage
        {
            DeviceId = _identity.DeviceId,
            FramesDecoded = _castReceiver.FramesDecoded,
            VideoBytesReceived = _castReceiver.BytesReceived,
            VideoError = _castReceiver.LastError,
            HasAudio = _castReceiver.HasAudio,
            AudioBytesReceived = _castReceiver.AudioBytesReceived,
            AudioError = _castReceiver.AudioError,
        };
        _ = _discovery.SendCastStatusAsync(_castingCasterEndPoint, status);
    }

    private void OnExitRequested()
    {
        _repository.Save(_store);
        _stateMachine.Disconnect(); // ensure audio is restored / overlay hidden before teardown.

        _tray.Dispose();
        _discovery.Dispose();
        _castStatusTimer.Dispose();
        _castReceiver?.Dispose();
        _previewWindow?.Dispose();
        _playback?.Dispose();
        // _videoSurface after _playback/_castReceiver, never before — both present through it and
        // must be torn down first (see VideoSurface's doc comment).
        _videoSurface?.Dispose();
        _overlay?.Dispose();
        _audioTakeover.Dispose();
        _mainWindow.Dispose();

        Application.Exit();
    }
}
