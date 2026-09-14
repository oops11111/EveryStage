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
        CastSwitchChanged?.Invoke(on);
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
        StateChanged?.Invoke(OutputState.Idle);
    }

    private bool TransitionToActive()
    {
        lock (_gate)
        {
            if (State == OutputState.Active) return true;
            State = OutputState.Active;
        }
        StateChanged?.Invoke(OutputState.Active);
        return true;
    }
}
