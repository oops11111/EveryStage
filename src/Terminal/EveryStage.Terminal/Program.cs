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
/// discovery, the main window (with its 文件/活动/设备 panels), the floating preview window, and
/// the pairing confirmation dialog. 设置 is still a placeholder inside MainWindow — see that
/// project's README for what's left.
/// </summary>
internal sealed class TerminalApplicationContext : ApplicationContext
{
    private readonly ScenarioStore _store;
    private readonly ScenarioRepository _repository;
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

    public TerminalApplicationContext(ScenarioStore store, ScenarioRepository repository)
    {
        _store = store;
        _repository = repository;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Expected Program.Main to have installed a WindowsFormsSynchronizationContext first.");

        var extendedDisplay = MonitorService.GetBoundExtendedDisplay();
        if (extendedDisplay != null)
        {
            _overlay = new OverlayWindow(extendedDisplay);
            // One D3D11 device/swap chain for the overlay's video HWND, shared between local video
            // playback and a live device cast — see VideoSurface's doc comment for why this can't
            // be two independent ones anymore.
            _videoSurface = new VideoSurface(_overlay.VideoHost.Handle, _overlay.VideoHost.ClientSize.Width, _overlay.VideoHost.ClientSize.Height);
            _playback = new PlaybackEngine(_stateMachine, _overlay, _videoSurface);
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

        var identity = DeviceIdentity.LoadOrCreate("terminal");
        var pairedDevices = new PairedDeviceStore();
        _discovery = new DiscoveryService(identity, pairedDevices, new DeviceConnectionLogger());
        _discovery.PairingRequested += OnPairingRequested;
        _discovery.CastStartRequested += OnCastStartRequested;
        _discovery.CastStopRequested += OnCastStopRequested;
        _discovery.Start();

        var library = new FileLibraryStore();
        _mainWindow = new MainWindow(_stateMachine, _playback, library, pairedDevices, _store, _repository);
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
            // torn down. This does not notify the Caster (no cast_stop goes back) — same one-way
            // acknowledgment limitation as OnLocalPlaybackStarting above and this project's README.
            _castReceiver?.Dispose();
            _castReceiver = null;
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

            _castReceiver?.Dispose();
            _castReceiver = null;
            try
            {
                _castReceiver = new CastReceiver(_videoSurface, info.Width, info.Height, DiscoveryProtocol.VideoRtpPort);
                _castReceiver.Start();
            }
            catch (Exception)
            {
                // Same "don't crash, don't pretend it worked" reasoning as everywhere else in this
                // repo lacking a dedicated cast-session log yet — leaves the Terminal at Idle rather
                // than showing a video surface that will never receive a frame.
                _castReceiver = null;
                return;
            }

            _castingDeviceId = info.DeviceId;
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

            _castReceiver.Dispose();
            _castReceiver = null;
            _stateMachine.Disconnect();
        }, null);
    }

    private void OnLocalPlaybackStarting()
    {
        // PlaybackEngine is about to present local content through the shared VideoSurface — any
        // active device cast must stop first, for the same mutual-exclusion reason as
        // OnCastStartRequested stopping local playback in the other direction. This intentionally
        // does not call _stateMachine.Disconnect(): OutputStateMachine.State stays Active (local
        // content is replacing device content, not ending output), and there is no message back to
        // the Caster telling it its stream is being ignored now — a one-way limitation already
        // recorded in this project's README alongside CastStartRequested/CastStopRequested's own
        // lack of acknowledgment.
        _castReceiver?.Dispose();
        _castReceiver = null;
    }

    private void OnExitRequested()
    {
        _repository.Save(_store);
        _stateMachine.Disconnect(); // ensure audio is restored / overlay hidden before teardown.

        _tray.Dispose();
        _discovery.Dispose();
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
