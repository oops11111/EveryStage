using System.Net;
using Microsoft.Win32;
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

        // This session found three real bugs sharing one root cause: an exception on a background
        // thread or in a fire-and-forget Task, thrown from a narrower type than whatever caught it
        // expected (or caught by nothing at all), silently killing a whole background loop/callback
        // chain forever with zero visible symptom — see EveryStage.Discovery's
        // DiscoveryProtocol.Decode and this project's own PlaybackEngine.PlayImageAsync/
        // PlayDocumentAsync fixes for two of them (this project's README documents both). Those two
        // are fixed at their own source now, but nothing stopped a THIRD, not-yet-found instance of
        // the same shape from existing somewhere else in this codebase, or from being introduced
        // later — and separately, nothing at all protected the UI thread itself: any WinForms event
        // handler (a button click, a menu item) throwing would hit .NET's default unhandled-exception
        // behavior, which for an unattended device (PLANNING.md §14.4's whole framing) means the
        // Terminal stops functioning until someone physically restarts it — a far worse outcome than
        // any single subsystem getting stuck. These three handlers are this session's answer: not a
        // fix for a specific bug, but the safety net PLANNING.md's own "无人值守" requirement implies
        // should have existed from the start. See CrashLogger's own doc comment for why this writes
        // to a fourth, ad-hoc log category rather than one of PLANNING.md §14.4's three named ones.
        var crashLogger = new CrashLogger();

        // UnhandledExceptionMode.CatchException routes a UI-thread exception (e.g. a Toast action
        // button's callback throwing) to ThreadException below INSTEAD OF .NET's default crash/WER
        // dialog behavior — and critically, the message loop keeps running afterward. Must be set
        // before Application.Run, and ideally before any Control exists at all.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => crashLogger.LogUnhandledException("Application.ThreadException", e.Exception);

        // Last-chance logging for an exception that reaches the very top on some OTHER thread —
        // .NET does NOT let this handler prevent the process from terminating (isTerminating is
        // always true in practice for this event), so this can only make sure the reason gets
        // written down before the Terminal goes dark, not actually keep it running.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            crashLogger.LogUnhandledException("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Catches exactly the PlayImageAsync/PlayDocumentAsync bug's shape (a fire-and-forget Task
        // whose fault nobody ever awaits) for any OTHER instance of it this session's own fixes
        // didn't happen to find. SetObserved() marks the exception handled so it doesn't also
        // trigger a second-chance report — this handler's whole job is to make sure it gets logged
        // instead of vanishing, not to let it crash anything further.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crashLogger.LogUnhandledException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

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

        using var context = new TerminalApplicationContext(store, repository, crashLogger);
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
    private readonly DeviceConnectionLogger _connectionLog = new();
    private readonly CrashLogger _crashLogger;

    // Set once, the first time HandleDisplaySettingsChanged notices a display became available
    // after this Terminal started with none bound — guards the one and only TryBindExtendedDisplay
    // attempt that branch ever makes (see that method's own doc comment for why a failed attempt
    // isn't retried on every subsequent firing) as well as the fallback "please restart" notice for
    // when that attempt fails. Never reset back to false: repeating either notice on every subsequent
    // DisplaySettingsChanged firing (a resolution tweak on some OTHER monitor, for instance) would be
    // pure noise once the operator has already been told once, and once binding SUCCEEDS this whole
    // branch is never reached again anyway (_overlay is non-null from then on).
    private bool _notifiedDisplayAvailableAfterStartup;

    // How many _castStatusTimer ticks (1s each) between LogConnectionQuality runs — a diagnostic
    // *log* entry every second would spam the on-disk log with what's meant to be a periodic
    // summary, not a per-tick stream; this tick counter piggybacks on the timer that already exists
    // rather than adding a whole second System.Windows.Forms.Timer for one coarser-grained check.
    private const int ConnectionQualityCheckEveryNTicks = 15;
    private int _castStatusTickCount;

    public TerminalApplicationContext(ScenarioStore store, ScenarioRepository repository, CrashLogger crashLogger)
    {
        _store = store;
        _repository = repository;
        _crashLogger = crashLogger;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Expected Program.Main to have installed a WindowsFormsSynchronizationContext first.");

        _settingsStore = new SettingsStore();

        // Applied once, here, at startup — AppSettings.CastSwitchDefaultOn's own doc comment
        // explains why this never runs again after this point (it's a default, not a live toggle).
        _stateMachine.SetCastSwitch(_settingsStore.Current.CastSwitchDefaultOn);

        var extendedDisplay = MonitorService.GetBoundExtendedDisplay(preferredDeviceName: _settingsStore.Current.PreferredMonitorDeviceName);
        if (extendedDisplay != null) TryBindExtendedDisplay(extendedDisplay);
        // extendedDisplay == null: no second monitor attached yet (or a GPU-init failure inside
        // TryBindExtendedDisplay just above degraded into this same state). No overlay also means no
        // VideoSurface/PlaybackEngine/preview window yet, and OnCastStartRequested below already
        // no-ops when _overlay is null; MainWindow's file panel tolerates _playback being null
        // (RequestPlay just never gets called) until a display shows up. Bug fixed here (this
        // project's README "已知风险"): this comment used to claim "the tray tooltip below reflects
        // it", but TrayIconController.UpdateTooltip only ever reads
        // OutputStateMachine.State/CastSwitchOn — neither of which knows anything about whether
        // _overlay exists at all, so that claim was never actually true.
        // HandleDisplaySettingsChanged's own "no overlay yet" branch (see its doc comment) now
        // actually tries to build this whole graph live once a display shows up, rather than only
        // ever telling the operator to restart.

        // Runtime display-configuration changes (PLANNING.md §5, this project's README "已知风险" on
        // OverlayWindow.Rebind/VideoSurface.Resize previously existing but never having a caller) —
        // covers "the extended display this Terminal already bound at startup moved or changed
        // resolution" (Rebind/Resize), "that same display got unplugged entirely" (a clean
        // Disconnect()), and "no display was bound at startup, but one showed up since" (a live
        // TryBindExtendedDisplay attempt, falling back to a one-time restart notice only if that
        // fails — see HandleDisplaySettingsChanged's own doc comment for the full story, including why
        // this used to only ever show that notice). Subscribed unconditionally (not just when
        // _overlay already exists) precisely so that third case has an event to fire on in the first
        // place — with _overlay staying null forever otherwise, nothing would ever call
        // HandleDisplaySettingsChanged for it to check.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _stateMachine.StateChanged += OnOutputStateChanged;

        _identity = DeviceIdentity.LoadOrCreate("terminal");
        var pairedDevices = new PairedDeviceStore();
        _discovery = new DiscoveryService(_identity, pairedDevices, _connectionLog);
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
        _castStatusTimer.Tick += (_, _) =>
        {
            CheckCastLiveness();
            CheckDecodeHealth();
            SendCastStatus();
            if (++_castStatusTickCount % ConnectionQualityCheckEveryNTicks == 0) _ = LogConnectionQualityAsync();
        };

        var library = new FileLibraryStore();
        _mainWindow = new MainWindow(_stateMachine, _playback, library, pairedDevices, _store, _repository, _settingsStore, _identity, _connectionLog);
        _mainWindow.PreviewRecallRequested += OnPreviewRecallRequested;
        _mainWindow.Show();

        _tray = new TrayIconController(_stateMachine);
        _tray.ExitRequested += OnExitRequested;
        _tray.MainWindowRequested += () => { _mainWindow.Show(); _mainWindow.Activate(); };
    }

    /// <summary>Builds the whole local-playback object graph — <see cref="_overlay"/>/
    /// <see cref="_videoSurface"/>/<see cref="_playback"/>/<see cref="_previewWindow"/> — for
    /// <paramref name="monitor"/>. Extracted from this constructor's own original inline try/catch
    /// (this project's README used to record that whole block as never having a second call site) so
    /// <see cref="HandleDisplaySettingsChanged"/> can run the exact same path at runtime when a
    /// display shows up after startup with none bound — see that method's own doc comment for why this
    /// used to be a deliberately-deferred, restart-required scope limit and what changed.
    ///
    /// Bug this whole extraction preserves rather than fixes (documented here since it's the reason
    /// this needs to be a try/catch at all): this constructor's own original comment already explains
    /// why a raw, unguarded call here would be worse than degrading gracefully — a GPU/D3D11
    /// construction failure (no compatible hardware/driver — a real, not hypothetical, condition this
    /// repo has flagged repeatedly elsewhere) must never be allowed to crash the whole unattended
    /// Terminal, whether it happens during startup (before <see cref="Application.Run"/> even starts
    /// the message loop, where <see cref="Application.ThreadException"/> can't help at all) or, now,
    /// during a live rebind attempt triggered from a <see cref="SystemEvents.DisplaySettingsChanged"/>
    /// callback well after the Terminal is already running.
    ///
    /// Returns false (and leaves every field null, exactly as if this had never run) on any
    /// construction failure. Does NOT touch <see cref="_mainWindow"/> or anything downstream of it —
    /// at construction time <see cref="_mainWindow"/> doesn't exist yet (it's built afterward, already
    /// reading whatever this method left in <see cref="_playback"/>); at runtime, the caller
    /// (<see cref="HandleDisplaySettingsChanged"/>) is responsible for calling
    /// <see cref="MainWindow.AttachPlaybackEngine"/> on success — this method only ever needs to know
    /// how to build the graph, not who else needs to be told about it.</summary>
    private bool TryBindExtendedDisplay(MonitorInfo monitor)
    {
        try
        {
            _overlay = new OverlayWindow(monitor);
            // One D3D11 device/swap chain for the overlay's video HWND, shared between local video
            // playback and a live device cast — see VideoSurface's doc comment for why this can't be
            // two independent ones anymore.
            _videoSurface = new VideoSurface(_overlay.VideoHost.Handle, _overlay.VideoHost.ClientSize.Width, _overlay.VideoHost.ClientSize.Height);
            _playback = new PlaybackEngine(_stateMachine, _overlay, _videoSurface, _settingsStore, _store);
            _playback.LocalPlaybackStarting += OnLocalPlaybackStarting;
            _previewWindow = new FloatingPreviewWindow(_playback, _stateMachine);
            return true;
        }
        catch (Exception ex)
        {
            _crashLogger.LogUnhandledException("TerminalApplicationContext.ExtendedDisplayInit", ex);
            _previewWindow?.Dispose();
            _previewWindow = null;
            if (_playback != null) _playback.LocalPlaybackStarting -= OnLocalPlaybackStarting;
            _playback?.Dispose();
            _playback = null;
            _videoSurface?.Dispose();
            _videoSurface = null;
            _overlay?.Dispose();
            _overlay = null;
            return false;
        }
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

    private void OnPreviewRecallRequested()
    {
        if (_stateMachine.State != OutputState.Active || _previewWindow == null) return;
        _previewWindow.ShowForActiveOutput();
        _previewWindow.Activate();
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
                    info.HasAudio, info.AudioSampleRate, info.AudioChannels, DiscoveryProtocol.AudioRtpPort,
                    info.AudioIsAac, info.PayloadType, info.AudioPayloadType);
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
            _castStatusTickCount = 0;
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
            && _castReceiver.ConsecutiveAudioPlaybackErrors < MaxConsecutiveDecodeErrorsBeforeDisconnect
            && !_castReceiver.PresentLoopFailed)
        {
            return;
        }

        // Same StopCasting()+Disconnect() policy CheckCastLiveness already applies for a Caster
        // that's gone silent — Terminal is meant to run unattended (PLANNING.md's whole framing), so
        // there's nobody watching to notice a stuck decoder and manually hit "断"; falling back to
        // standby on its own is better than staying "connected" to a stream it can no longer render.
        // PresentLoopFailed alone (with both Consecutive* counters still low) means exactly that:
        // decoding is still succeeding but nothing is presenting it anymore — see that property's own
        // doc comment for why it needs its own un-racy signal instead of piggybacking on either
        // counter above.
        StopCasting();
        _stateMachine.Disconnect();
    }

    private void SendCastStatus()
    {
        if (_castReceiver == null || _castingCasterEndPoint == null) return;
        var casterEndPoint = _castingCasterEndPoint;

        var status = new DiscoveryProtocol.CastStatusMessage
        {
            DeviceId = _identity.DeviceId,
            SentAtUtc = DateTimeOffset.UtcNow,
            FramesDecoded = _castReceiver.FramesDecoded,
            VideoBytesReceived = _castReceiver.BytesReceived,
            VideoError = _castReceiver.LastError,
            HasAudio = _castReceiver.HasAudio,
            AudioBytesReceived = _castReceiver.AudioBytesReceived,
            AudioError = _castReceiver.AudioError,
            PayloadTypeMismatches = _castReceiver.PayloadTypeMismatches,
        };
        _ = _discovery.SendCastStatusAsync(casterEndPoint, status);
    }

    /// <summary>Fills in PLANNING.md §14.4's "连接质量指标（丢包率/延迟）" for
    /// <see cref="DeviceConnectionLogger.LogQualityMetric"/> — a method that has existed since this
    /// project's very first logging round but, until now, had no caller anywhere (see this project's
    /// README): nothing on this side had ever measured either a real latency or a real packet-loss
    /// figure to pass it. Latency comes from <see cref="DiscoveryService.PingAsync"/> (Terminal
    /// actively pinging the Caster it's currently receiving a cast from — the mirror image of
    /// <c>Caster.Casting.LiveCastSession</c>'s own <c>RunPingLoop</c>, which pings this Terminal for
    /// its own UI). Packet loss comes from <see cref="CastReceiver.EstimatedPacketLossPercent"/> —
    /// see that property's own doc comment for why it's an honest approximation, not exact. Called
    /// roughly every <see cref="ConnectionQualityCheckEveryNTicks"/> seconds rather than every
    /// <see cref="SendCastStatus"/> tick — a diagnostic log entry once a second would spam the
    /// on-disk log with what's meant to be a periodic summary.</summary>
    private async Task LogConnectionQualityAsync()
    {
        // The `is not { } casterEndPoint` pattern both null-checks _castingCasterEndPoint and binds
        // it to a properly non-null local in one step — plain `!= null` wouldn't let the compiler
        // carry that narrowing to casterEndPoint below, since it's a field (mutable from elsewhere
        // between statements, in the compiler's more conservative view of fields vs. locals).
        if (_castReceiver == null || _castingCasterEndPoint is not { } casterEndPoint) return;

        // Captured before the ping's own await, in case StopCasting()/a new cast start races in
        // while this is in flight — logging against a Guid.Empty or the wrong device would be worse
        // than not logging at all.
        Guid deviceId = _castingDeviceId;
        var castReceiver = _castReceiver;

        TimeSpan? rtt;
        try
        {
            rtt = await _discovery.PingAsync(casterEndPoint);
        }
        catch (Exception)
        {
            rtt = null; // best-effort, like every other discovery-protocol interaction in this repo.
        }

        // The cast this was measuring may have ended (or a different one started) while the ping was
        // in flight — re-check rather than logging a quality metric for a device this Terminal is no
        // longer casting with (or, worse, attributing it to whatever new device started meanwhile).
        if (_castReceiver != castReceiver || _castingDeviceId != deviceId) return;

        // Logs even a partial reading (only one of the two measured) rather than requiring both —
        // see LogQualityMetric's own doc comment on why a missing measurement is passed as null
        // rather than a misleading 0.
        _connectionLog.LogQualityMetric(deviceId.ToString(), castReceiver.EstimatedPacketLossPercent, rtt?.TotalMilliseconds);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents raises this from its own internal message-pump thread, not necessarily this
        // process's UI thread — same "marshal before touching UI-affine state" reasoning this class
        // already applies to DiscoveryService's background-thread callbacks. _uiContext was captured
        // from the WindowsFormsSynchronizationContext Program.Main guarantees exists before this
        // constructor runs, so Post here always lands back on the UI thread regardless of which
        // thread SystemEvents actually used to raise this on (NOTE: this repo has no way to verify
        // SystemEvents' real threading behavior on an actual Windows machine — Post is used
        // defensively rather than assuming the common case that it's already on the UI thread).
        _uiContext.Post(_ => HandleDisplaySettingsChanged(), null);
    }

    /// <summary>Three cases now. Two are for the extended display this Terminal already bound at
    /// startup (<see cref="_overlay"/> non-null): it moved position or changed resolution/orientation
    /// (<see cref="OverlayWindow.Rebind"/>/<see cref="VideoSurface.Resize"/> — this project's README
    /// used to flag both as written but never called by anything), or it got unplugged entirely
    /// (<see cref="OutputStateMachine.Disconnect"/> — the same clean "断" a user clicking it manually
    /// would trigger: restores audio, hides the now-nowhere-to-be-seen overlay, and stops any active
    /// device cast; a no-op if output was already Idle). Disconnecting rather than tearing down
    /// <see cref="_overlay"/>/<see cref="_videoSurface"/>/<see cref="_playback"/> themselves is
    /// deliberate — that graph stays alive so a later replug is just another
    /// <see cref="OnDisplaySettingsChanged"/> firing, handled by whichever of these two branches
    /// applies then (same monitor identity back -> no-op via the structural-equality check below;
    /// different bounds/position -> Rebind/Resize, already covered).
    ///
    /// The third case — a display being plugged in for the first time after this Terminal already
    /// started with none bound (<see cref="_overlay"/> null) — USED to only show a one-time "please
    /// restart" tray balloon, with this project's README explicitly recording "actually build the
    /// whole graph live" as a deliberately-deferred, substantially-bigger scope limit. Confirmed with
    /// the user this round that it was worth attempting despite the risk (this codebase's own biggest
    /// remaining architecture change, and one this sandbox has no way to verify against real hardware
    /// or the concurrent scenarios PLANNING.md never specifies — see this project's README for the
    /// full disclosure). It now calls <see cref="TryBindExtendedDisplay"/> — the SAME construction
    /// path this constructor itself uses at startup, extracted rather than duplicated — and, on
    /// success, <see cref="MainWindow.AttachPlaybackEngine"/> to propagate the freshly-built
    /// <see cref="PlaybackEngine"/> into the already-showing <see cref="_mainWindow"/> (and,
    /// transitively, <see cref="ActivitiesPanel"/>/<see cref="FilesPanel"/> — see that method's own
    /// doc comment). Concurrency risk this specific case turns out NOT to have, on inspection: nothing
    /// could have been actively playing/casting locally while <see cref="_overlay"/> was still null
    /// (every local-content path requires <see cref="_playback"/> non-null first), so there is no
    /// "torn down mid-playback" race to worry about here — that concern only applies to the OPPOSITE
    /// direction (an ALREADY-bound display being unplugged while something is happening), which the
    /// <see cref="OutputStateMachine.Disconnect"/> branch below already handles and always has.
    /// Attempted at most once per process lifetime, same one-shot guard
    /// (<see cref="_notifiedDisplayAvailableAfterStartup"/>) as before — a construction failure here
    /// is presumably the same persistent GPU/driver problem <see cref="TryBindExtendedDisplay"/>'s own
    /// doc comment describes, not something retrying on every subsequent unrelated
    /// <see cref="OnDisplaySettingsChanged"/> firing would fix, so a failed attempt falls back to the
    /// original "please restart" notice exactly as before, rather than retrying indefinitely.</summary>
    private void HandleDisplaySettingsChanged()
    {
        if (_overlay == null || _videoSurface == null)
        {
            if (_notifiedDisplayAvailableAfterStartup) return;

            var detected = MonitorService.GetBoundExtendedDisplay(preferredDeviceName: _settingsStore.Current.PreferredMonitorDeviceName);
            if (detected == null) return;

            _notifiedDisplayAvailableAfterStartup = true;
            if (TryBindExtendedDisplay(detected))
            {
                _mainWindow.AttachPlaybackEngine(_playback!); // non-null — TryBindExtendedDisplay only returns true after setting it.
                _tray.ShowNotification("检测到扩展屏", "EveryStage 终端机现在可以向扩展屏输出了，无需重启。");
            }
            else
            {
                _tray.ShowNotification("检测到扩展屏", "EveryStage 终端机检测到扩展屏，但初始化失败。请重启终端机以重试。");
            }
            return;
        }

        var updated = MonitorService.GetBoundExtendedDisplay(preferredDeviceName: _settingsStore.Current.PreferredMonitorDeviceName);
        if (updated == null)
        {
            _stateMachine.Disconnect(); // the bound display disappeared entirely — see this method's own doc comment.
            return;
        }
        if (updated == _overlay.Monitor) return; // MonitorInfo is a record — structural equality catches "nothing actually changed".

        _overlay.Rebind(updated);
        _videoSurface.Resize(updated.Bounds.Width, updated.Bounds.Height);
    }

    private void OnExitRequested()
    {
        _repository.Save(_store);
        _stateMachine.Disconnect(); // ensure audio is restored / overlay hidden before teardown.

        // Unconditional now, matching the unconditional subscribe above (this event is subscribed
        // regardless of whether _overlay ever ended up non-null).
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

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
