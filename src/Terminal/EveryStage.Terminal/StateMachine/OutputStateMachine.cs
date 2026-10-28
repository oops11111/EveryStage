namespace EveryStage.Terminal.StateMachine;

/// <summary>What is currently on the extended display. Local media and device casts are mutually
/// exclusive (PLANNING §9).</summary>
public enum ProgramSource
{
    None,
    LocalMedia,
    DeviceCast,
}

public enum OutputState { Idle, Active }

/// <summary>
/// The Terminal's core output state machine (PLANNING.md §9, §10):
///
/// - <see cref="OutputState"/> (待机中 / 扩展屏输出中) and the "投屏开关" (cast switch) are
///   deliberately independent axes, not one combined state — §9.1 is explicit that disconnecting
///   ("断") does not turn the switch off, and turning the switch off does not disconnect an
///   already-active output.
/// - Clicking a local file only starts output when the switch is on. Device-cast requests are gated
///   by the receiver/controller before they reach this class; once admitted, the raw state
///   transition below stays independent from the switch. The application layer listens to the switch
///   and retracts any device cast already on Program without disconnecting the device preview.
/// - "断" is the only path back to Idle, and it hands the screen back to Windows rather than
///   moving to some app-owned standby visual (§9.2) — that behavior lives in the overlay
///   window/content engine, this class only tracks *that* the transition happened.
///
/// Not thread-affine by itself; callers driving this from multiple threads (e.g. a UI thread and
/// a network-request handler) should serialize their calls (a single dispatcher/lock), since the
/// state read + transition here isn't atomic against concurrent callers on its own.
///
/// <see cref="StateChanged"/> has several independent subscribers by the time
/// <c>TerminalApplicationContext</c> finishes constructing (in registration order:
/// <c>PlaybackEngine</c>, this class's own <c>Program.OnOutputStateChanged</c> — which calls
/// <c>AudioTakeoverService.Restore()</c> then <c>StopCasting()</c> on "断" — <c>MainWindow</c>/
/// <c>ActivitiesPanel</c>, then <c>TrayIconController</c>). A plain <c>StateChanged?.Invoke(...)</c>
/// runs every subscriber as one multicast call: if an earlier one throws, .NET does not continue on
/// to the rest — every subscriber registered after the one that threw silently never sees this
/// state change at all. <see cref="RaiseStateChanged"/>/<see cref="RaiseCastSwitchChanged"/> invoke
/// each subscriber individually specifically so that one subscriber's bug can never take the others
/// down with it — see their own doc comments.
/// </summary>
public sealed class OutputStateMachine
{
    private readonly object _gate = new();

    public OutputState State { get; private set; } = OutputState.Idle;

    /// <summary>What is on the extended display while <see cref="State"/> is Active — local media or a
    /// device cast (mutually exclusive); <see cref="ProgramSource.None"/> when Idle.
    /// Updated even when already Active, e.g. local content preempting a device cast.</summary>
    public ProgramSource Source { get; private set; } = ProgramSource.None;

    /// <summary>投屏开关. Defaults to on — PLANNING.md doesn't specify a default; adjust here if
    /// product wants the Terminal to start with local-file casting disabled until a user opts in.</summary>
    public bool CastSwitchOn { get; private set; } = true;

    public event Action<OutputState>? StateChanged;
    public event Action<bool>? CastSwitchChanged;

    /// <summary>Told about a subscriber that threw (the others still run). Subscriber exceptions used to be
    /// swallowed with no record anywhere — exactly where a broken "断" cleanup would hide (audit C-21).
    /// Program sets this to the crash logger.</summary>
    public Action<string, Exception>? SubscriberFailed { get; set; }

    private void ReportSubscriberFailure(string eventName, Exception ex)
    {
        try { SubscriberFailed?.Invoke("OutputStateMachine." + eventName, ex); }
        catch (Exception) { } // the reporter itself must not break notification
    }

    public void SetCastSwitch(bool on)
    {
        bool cutLocal;
        lock (_gate)
        {
            if (CastSwitchOn == on) return;
            cutLocal = !on && State == OutputState.Active && Source == ProgramSource.LocalMedia;
            CastSwitchOn = on;
        }
        RaiseCastSwitchChanged(on);

        // The switch directly governs local Program output: turning it off takes local media off the
        // extended display (Preview is a separate channel and keeps its content). Device-cast requests
        // are not rejected here; Program wires CastSwitchChanged to the device-cast controller so an
        // already-on-Program device stream is retracted to the device preview and its restore point is
        // applied without disconnecting the device.
        if (cutLocal) Disconnect();
    }

    /// <summary>Local file click. Returns true if this call actually started (or was already
    /// driving) output to the extended display — false means it played back locally only because
    /// the cast switch is off.</summary>
    public bool RequestLocalFilePlayback()
    {
        // The switch is checked inside the same lock as the transition (audit B-16): read-then-transition
        // let a switch-off in between still start local output on the extended display.
        return TransitionToActive(ProgramSource.LocalMedia, requireCastSwitch: true);
    }

    /// <summary>Restores local media that was already on Program before a device cast took over.
    /// This is not a fresh local投屏 request, so it intentionally bypasses the current cast-switch
    /// position while unwinding the device cast back to its saved restore point.</summary>
    public bool RestoreLocalFilePlayback() => TransitionToActive(ProgramSource.LocalMedia);

    /// <summary>Device cast accepted by the receiver/controller. Independent from the cast switch:
    /// the switch prevents local content from taking over the extended display, not network casters
    /// from requesting a Terminal-side device cast.</summary>
    public bool AcceptDeviceCastRequest() => TransitionToActive(ProgramSource.DeviceCast);

    /// <summary>"断". No-op if already idle. Never changes <see cref="CastSwitchOn"/>.</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (State == OutputState.Idle) return;
            State = OutputState.Idle;
            Source = ProgramSource.None;
        }
        RaiseStateChanged(OutputState.Idle);
    }

    private bool TransitionToActive(ProgramSource source, bool requireCastSwitch = false)
    {
        lock (_gate)
        {
            if (requireCastSwitch && !CastSwitchOn) return false;
            Source = source;
            if (State == OutputState.Active) return true;
            State = OutputState.Active;
        }
        RaiseStateChanged(OutputState.Active);
        return true;
    }

    /// <summary>Invokes each <see cref="StateChanged"/> subscriber one at a time instead of as a
    /// single multicast call, so that one subscriber throwing doesn't also silently skip every
    /// subscriber registered after it for this same state change — see this class's own doc comment
    /// for the concrete subscriber list and why that matters here specifically (the subscriber that
    /// runs <c>StopCasting()</c> on "断" is not the first one registered).</summary>
    private void RaiseStateChanged(OutputState state)
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                ((Action<OutputState>)handler)(state);
            }
            catch (Exception ex)
            {
                ReportSubscriberFailure("StateChanged", ex);
                // Deliberately swallowed here rather than re-thrown after the loop: this class has
                // no logger of its own (SubscriberFailed reports it), and PLANNING.md's "无人
                // 值守" framing already means every subscriber above is written to treat its own
                // failures as best-effort (see e.g. AudioTakeoverService's own doc comment) — the
                // one new guarantee this method adds is that a failure in one no longer costs every
                // OTHER subscriber their notification too.
            }
        }
    }

    /// <summary>Same per-subscriber isolation as <see cref="RaiseStateChanged"/>, for the same
    /// reason — currently only <c>TrayIconController</c> subscribes to <see cref="CastSwitchChanged"/>,
    /// but there is no guarantee that stays true, and the two events sharing one raising convention
    /// is easier to keep correct than special-casing "this one only has one subscriber today".</summary>
    private void RaiseCastSwitchChanged(bool on)
    {
        foreach (var handler in CastSwitchChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                ((Action<bool>)handler)(on);
            }
            catch (Exception ex)
            {
                ReportSubscriberFailure("CastSwitchChanged", ex);
            }
        }
    }
}
