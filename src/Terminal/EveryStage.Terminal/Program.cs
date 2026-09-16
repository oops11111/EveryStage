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
    private readonly DeviceConnectionLogger _connectionLog = new();

    // How many _castStatusTimer ticks (1s each) between LogConnectionQuality runs — a diagnostic
    // *log* entry every second would spam the on-disk log with what's meant to be a periodic
    // summary, not a per-tick stream; this tick counter piggybacks on the timer that already exists
    // rather than adding a whole second System.Windows.Forms.Timer for one coarser-grained check.
    private const int ConnectionQualityCheckEveryNTicks = 15;
    private int _castStatusTickCount;

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

            // Runtime display-configuration changes (PLANNING.md §5, this project's README "已知
            // 风险" on OverlayWindow.Rebind/VideoSurface.Resize previously existing but never having
            // a caller) — covers "the extended display this Terminal already bound at startup moved
            // or changed resolution" (Rebind/Resize) and "that same display got unplugged entirely"
            // (a clean Disconnect(), see HandleDisplaySettingsChanged's own doc comment for exactly
            // what each branch does and what is still NOT handled — a display being plugged in for
            // the first time when none was bound at startup). Only subscribed when an overlay
            // actually exists — with no bound display at startup there is nothing here for a later
            // display change to rebind anyway (see the "no display bound" case below).
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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

    // How long after the primary send to fire the retransmit copy (see SendCastStatus's own doc
    // comment) — picked to land meaningfully later than the primary send (so a single short-lived
    // loss burst is less likely to take out both copies) while still leaving a comfortable margin
    // before the next tick's own fresh send 1000ms later. Not tuned against any real network,
    // same as every other timing constant in this repo lacking a Windows machine to measure
    // against.
    private static readonly TimeSpan StatusRetransmitDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>See EveryStage.Caster's README risk #34 ("状态回报本身也是尽力而为的UDP、没有重传")
    /// for the problem this addresses: a single lost <c>CastStatusMessage</c> previously meant the
    /// Caster's "终端机确认" reading went stale until the next 1-second tick's report, and staying
    /// unlucky for several consecutive ticks could make a perfectly healthy Terminal show as
    /// "未确认". This sends the exact same status snapshot a second time,
    /// <see cref="StatusRetransmitDelay"/> later — a genuine retransmission (identical payload, not
    /// a fresh independent report) of the kind the risk item's own title asks for, not a new ack/
    /// retry protocol: <c>CastStatusMessage</c> stays exactly as "尽力而为" as before, this just
    /// makes each 1-second report need two independent packet losses instead of one before the
    /// Caster sees a gap. Deliberately NOT a fix for risk #34's other half (network jitter/
    /// congestion affecting the whole discovery socket, not just one packet) — that's a
    /// fundamentally different failure mode this cheap doubling doesn't touch.</summary>
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
        };
        _ = _discovery.SendCastStatusAsync(casterEndPoint, status);
        _ = RetransmitCastStatusAsync(casterEndPoint, status);
    }

    private async Task RetransmitCastStatusAsync(IPEndPoint casterEndPoint, DiscoveryProtocol.CastStatusMessage status)
    {
        await Task.Delay(StatusRetransmitDelay);

        // The cast this was reporting on may have stopped (or a different one started against a
        // different Caster) during the delay — same "re-check before acting on stale captured state"
        // reasoning as LogConnectionQualityAsync above. Comparing the IPEndPoint reference is
        // enough: _castingCasterEndPoint is only ever assigned once per cast (OnCastStartRequested)
        // and cleared to null by StopCasting(), never replaced in place.
        if (_castReceiver == null || _castingCasterEndPoint != casterEndPoint) return;

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

    /// <summary>Two cases for the extended display this Terminal already bound at startup
    /// (<see cref="_overlay"/> non-null): it moved position or changed resolution/orientation
    /// (<see cref="OverlayWindow.Rebind"/>/<see cref="VideoSurface.Resize"/> — this project's README
    /// used to flag both as written but never called by anything), or it got unplugged entirely
    /// (<see cref="OutputStateMachine.Disconnect"/> — the same clean "断" a user clicking it manually
    /// would trigger: restores audio, hides the now-nowhere-to-be-seen overlay, and stops any active
    /// device cast; a no-op if output was already Idle). Disconnecting rather than tearing down
    /// <see cref="_overlay"/>/<see cref="_videoSurface"/>/<see cref="_playback"/> themselves is
    /// deliberate — that graph stays alive so a later replug is just another
    /// <see cref="OnDisplaySettingsChanged"/> firing, handled by whichever of these two branches
    /// applies then (same monitor identity back -> no-op via the structural-equality check below;
    /// different bounds/position -> Rebind/Resize, already covered). Deliberately does NOT handle: a
    /// display being plugged in for the first time after this Terminal already started with none
    /// bound (<see cref="_overlay"/> stays null forever once decided at startup — building the whole
    /// <see cref="_overlay"/>/<see cref="_videoSurface"/>/<see cref="_playback"/>/
    /// <see cref="_previewWindow"/> graph at runtime is a substantially bigger change this round
    /// doesn't attempt). See this project's README "已知风险" for that remaining scope limit being
    /// recorded as intentional, not an oversight.</summary>
    private void HandleDisplaySettingsChanged()
    {
        if (_overlay == null || _videoSurface == null) return;

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

        if (_overlay != null) SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

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
