using EveryStage.Terminal.Audio;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.Tray;

namespace EveryStage.Terminal;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var repository = new ScenarioRepository();
        var store = repository.Load();

        using var context = new TerminalApplicationContext(store, repository);
        Application.Run(context);
    }
}

/// <summary>
/// Wires the Phase 1 framework pieces together: state machine <-> overlay window <-> audio
/// takeover <-> tray icon. This is deliberately not the full Phase 4 UI (four main panels,
/// floating preview) — just the "待机中/扩展屏输出中" loop from PLANNING.md §10 running end to end,
/// so the framework can be exercised (and the local content engine / device-request handling from
/// the rest of Phase 1 can be built against a working state machine) before the real UI exists.
/// </summary>
internal sealed class TerminalApplicationContext : ApplicationContext
{
    private readonly ScenarioStore _store;
    private readonly ScenarioRepository _repository;
    private readonly OutputStateMachine _stateMachine = new();
    private readonly AudioTakeoverService _audioTakeover = new();
    private readonly TrayIconController _tray;
    private readonly DiscoveryService _discovery;
    private OverlayWindow? _overlay;
    private PlaybackEngine? _playback;

    public TerminalApplicationContext(ScenarioStore store, ScenarioRepository repository)
    {
        _store = store;
        _repository = repository;

        var extendedDisplay = MonitorService.GetBoundExtendedDisplay();
        if (extendedDisplay != null)
        {
            _overlay = new OverlayWindow(extendedDisplay);
            _playback = new PlaybackEngine(_stateMachine, _overlay);
        }
        // extendedDisplay == null: no second monitor attached yet. §5/§7 don't specify a "no
        // display bound" UX beyond implying it's a real, visible configuration state — the tray
        // tooltip below reflects it, but there is nothing further to build here until Phase 4 UI
        // exists to surface a proper "未检测到扩展屏" notice. No overlay also means no PlaybackEngine
        // yet; whatever eventually drives "点文件" (Phase 4 UI, or a device-request handler) needs
        // to tolerate _playback being null until a display shows up.

        _stateMachine.StateChanged += OnOutputStateChanged;

        var identity = DeviceIdentity.LoadOrCreate();
        var pairedDevices = new PairedDeviceStore();
        _discovery = new DiscoveryService(identity, pairedDevices, new DeviceConnectionLogger());
        // PairingRequested has no subscriber yet — there is no UI to show the §7 confirmation
        // popup/PIN prompt. Every non-trusted pairing request currently just sits until it times
        // out (DiscoveryService.PruneExpiredPendingRequests), at which point it's logged as a
        // timed-out pairing attempt rather than vanishing without a trace.
        _discovery.Start();

        _tray = new TrayIconController(_stateMachine);
        _tray.ExitRequested += OnExitRequested;
    }

    private void OnOutputStateChanged(OutputState state)
    {
        if (state == OutputState.Active)
        {
            _overlay?.ShowOverlay();
            _ = _audioTakeover.TakeoverAsync(); // fire-and-forget: UI thread must not block on this.
        }
        else
        {
            _overlay?.HideOverlay();
            _audioTakeover.Restore();
        }
    }

    private void OnExitRequested()
    {
        _repository.Save(_store);
        _stateMachine.Disconnect(); // ensure audio is restored / overlay hidden before teardown.

        _tray.Dispose();
        _discovery.Dispose();
        _playback?.Dispose();
        _overlay?.Dispose();
        _audioTakeover.Dispose();

        Application.Exit();
    }
}
