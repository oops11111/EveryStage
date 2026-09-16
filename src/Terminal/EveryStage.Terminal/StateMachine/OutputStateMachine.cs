namespace EveryStage.Terminal.StateMachine;

public enum OutputState { Idle, Active }

/// <summary>
/// The Terminal's core output state machine (PLANNING.md §9, §10):
///
/// - <see cref="OutputState"/> (待机中 / 扩展屏输出中) and the "投屏开关" (cast switch) are
///   deliberately independent axes, not one combined state — §9.1 is explicit that disconnecting
///   ("断") does not turn the switch off, and turning the switch off does not disconnect an
///   already-active output.
/// - Clicking a local file only starts output when the switch is on; a device cast request always
///   can, switch or no switch (§9.1: "设备投屏请求不受此开关影响").
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

    /// <summary>投屏开关. Defaults to on — PLANNING.md doesn't specify a default; adjust here if
    /// product wants the Terminal to start with local-file casting disabled until a user opts in.</summary>
    public bool CastSwitchOn { get; private set; } = true;

    public event Action<OutputState>? StateChanged;
    public event Action<bool>? CastSwitchChanged;

    public void SetCastSwitch(bool on)
    {
        lock (_gate)
        {
            if (CastSwitchOn == on) return;
            CastSwitchOn = on;
        }
        RaiseCastSwitchChanged(on);
    }

    /// <summary>Local file click. Returns true if this call actually started (or was already
    /// driving) output to the extended display — false means it played back locally only because
    /// the cast switch is off.</summary>
    public bool RequestLocalFilePlayback()
    {
        bool switchOn;
        lock (_gate) { switchOn = CastSwitchOn; }
        return switchOn && TransitionToActive();
    }

    /// <summary>Device cast request. Always allowed, independent of the cast switch (§9.1).</summary>
    public bool AcceptDeviceCastRequest() => TransitionToActive();

    /// <summary>"断". No-op if already idle. Never changes <see cref="CastSwitchOn"/>.</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (State == OutputState.Idle) return;
            State = OutputState.Idle;
        }
        RaiseStateChanged(OutputState.Idle);
    }

    private bool TransitionToActive()
    {
        lock (_gate)
        {
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
            catch (Exception)
            {
                // Deliberately swallowed here rather than re-thrown after the loop: this class has
                // no logger of its own to record which subscriber failed, and PLANNING.md's "无人
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
            catch (Exception)
            {
            }
        }
    }
}
