using EveryStage.Discovery;
using EveryStage.Terminal.Audio;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
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
/// Wires the Phase 1 framework pieces together: state machine <-> overlay window <-> audio
/// takeover <-> tray icon <-> device discovery. This is deliberately not the full Phase 4 UI (four
/// main panels) — just enough surrounding UI (the floating preview window, a pairing confirmation
/// dialog) to give <c>PlaybackEngine</c> and <c>DiscoveryService</c> real callers instead of sitting
/// completely unused, per PLANNING.md §10's "待机中/扩展屏输出中" loop and §7's pairing flow.
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
    private PlaybackEngine? _playback;
    private FloatingPreviewWindow? _previewWindow;
    private readonly MainWindow _mainWindow;

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
            _playback = new PlaybackEngine(_stateMachine, _overlay);
            _previewWindow = new FloatingPreviewWindow(_playback, _stateMachine);
        }
        // extendedDisplay == null: no second monitor attached yet. §5/§7 don't specify a "no
        // display bound" UX beyond implying it's a real, visible configuration state — the tray
        // tooltip below reflects it, but there is nothing further to build here until Phase 4 UI
        // exists to surface a proper "未检测到扩展屏" notice. No overlay also means no
        // PlaybackEngine/preview window yet; MainWindow's file panel tolerates _playback being null
        // (RequestPlay just never gets called) until a display shows up.

        _stateMachine.StateChanged += OnOutputStateChanged;

        var identity = DeviceIdentity.LoadOrCreate("terminal");
        var pairedDevices = new PairedDeviceStore();
        _discovery = new DiscoveryService(identity, pairedDevices, new DeviceConnectionLogger());
        _discovery.PairingRequested += OnPairingRequested;
        _discovery.Start();

        var library = new FileLibraryStore();
        _mainWindow = new MainWindow(_stateMachine, _playback, library, pairedDevices);
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

    private void OnExitRequested()
    {
        _repository.Save(_store);
        _stateMachine.Disconnect(); // ensure audio is restored / overlay hidden before teardown.

        _tray.Dispose();
        _discovery.Dispose();
        _previewWindow?.Dispose();
        _playback?.Dispose();
        _overlay?.Dispose();
        _audioTakeover.Dispose();
        _mainWindow.Dispose();

        Application.Exit();
    }
}
