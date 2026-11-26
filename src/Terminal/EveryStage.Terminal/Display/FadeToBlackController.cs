using System.Diagnostics;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.Display;

/// <summary>DEV-18: fade-to-black (FTB). One controller, owned by the host window, driving its own
/// <see cref="TransitionCurtain"/> — deliberately NOT the go-live <see cref="TransitionController"/>
/// (上屏淡入) and NOT any item or PPT transition: FTB fades the program picture to black and the
/// program audio with it, keeps the output connected, and restores both together.
///
/// How it works: fading out freezes the current program picture into the curtain (a screen capture
/// of the program screen, like the transition controller's) and ramps the curtain's black shade
/// 0→1 while the engine's <see cref="PlaybackEngine.OutputDim"/> and the music channel's dim ramp
/// 1→0 on the same clock. Restoring re-captures the still first (the show moved on underneath),
/// then ramps back. Duration comes from 设置 「黑场（FTB）时长」 (default 800 ms, 0 = cut, cap
/// 2 s); a toggle mid-ramp reverses from the current level over the proportional remainder, so
/// the picture never jumps. The dim multiplies volume/fade/mute — it never writes a volume or a
/// mute flag — so a stopped channel stays stopped and the restore returns the exact levels.
///
/// Gating lives in the host (<see cref="MainWindow.ToggleFadeToBlack"/>): this controller assumes
/// it is only toggled while local media is on air. <see cref="Reset"/> is synchronous and total:
/// the curtain comes down and both dims return to 1 at once (F12 never waits for a fade).
/// Never a modal, never activated, never topmost-stealing: the curtain is click-through and the
/// controller touches no input handling, so Esc/F12 keep working throughout.</summary>
internal sealed class FadeToBlackController : IDisposable
{
    private readonly Func<PlaybackEngine?> _program;
    private readonly Func<MusicChannel?> _music;
    private readonly Func<Rectangle?> _screenBounds;
    private readonly Func<int> _durationMilliseconds;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _clock = new();
    private TransitionCurtain? _curtain;
    private double _dim = 1.0;       // the level currently applied (1 = full picture, 0 = black)
    private double _startDim = 1.0;  // the ramp's start level (a mid-ramp toggle resumes from here)
    private double _targetDim = 1.0;
    private double _rampMs;          // proportional to the distance left to travel
    private bool _active;            // black is on screen (ramping or held)
    private bool _holding;           // fully black, held: the timer only re-asserts z-order now
    private DateTime _lastTopAssert;
    private bool _disposed;

    /// <summary>True while black is on screen — ramping out, held, or ramping back. The dock
    /// button's checked look and the hotkey's no-op decisions read this.</summary>
    public bool IsActive => _active;

    /// <summary>Raised when the active state changes (fade started / finished / reset).</summary>
    public event Action? Changed;

    public FadeToBlackController(
        Func<PlaybackEngine?> program,
        Func<MusicChannel?> music,
        Func<Rectangle?> screenBounds,
        Func<int> durationMilliseconds)
    {
        _program = program;
        _music = music;
        _screenBounds = screenBounds;
        _durationMilliseconds = durationMilliseconds;
        _timer.Tick += OnTick;
    }

    /// <summary>Fade to black, or restore if black is already up (or on its way). A toggle
    /// mid-ramp reverses from the current level — no jump, no restart from an endpoint: a
    /// toggle during the restore ramp heads back to black, not back to the start of the
    /// restore (the ramp's direction, not just the active flag, decides).</summary>
    public void Toggle()
    {
        if (_disposed) return;
        if (_active && _targetDim < 1.0) Restore();
        else FadeOut();
    }

    private void FadeOut()
    {
        // Cover at the current shade, not 0: a mid-restore reversal must not flash the live
        // picture brighter for a frame before heading back to black.
        if (!CaptureStill(black: (float)(1.0 - _dim))) return; // no screen to fade (bounds unknown): stay visible, dim nothing
        _active = true;
        StartRamp(targetDim: 0.0);
        Changed?.Invoke();
    }

    private void Restore()
    {
        // Re-capture first: the show moved on underneath the frozen still, and the reveal must
        // show the live picture, not the stale one. A failed capture falls back to a cut reveal
        // (curtain down, dim restored at once) rather than a stale frozen frame.
        if (!CaptureStill(black: 1f)) { FinishRestoreCut(); return; }
        StartRamp(targetDim: 1.0);
    }

    /// <summary>Freezes the program screen into the curtain at <paramref name="black"/> shade.
    /// False when there is no program screen (no extended display bound) — the caller then
    /// does nothing rather than dimming audio against a visible picture. A failed capture
    /// still covers with a blank still: the shade alone darkens the live picture, and full
    /// black is still reached.</summary>
    private bool CaptureStill(float black)
    {
        if (_screenBounds() is not { } bounds || bounds.Width < 2 || bounds.Height < 2) return false;
        Bitmap still;
        try
        {
            still = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(still);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        catch (Exception)
        {
            still = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }
        _curtain ??= new TransitionCurtain(bounds);
        _curtain.Cover(still, bounds);
        _curtain.Black = black;
        return true;
    }

    private void StartRamp(double targetDim)
    {
        _holding = false;
        _startDim = _dim;
        _targetDim = targetDim;
        int duration = Math.Clamp(_durationMilliseconds(), 0, 2000);
        _rampMs = duration * Math.Abs(targetDim - _startDim);
        _clock.Restart();
        if (_rampMs <= 0) ApplyTargetImmediately();
        else _timer.Start();
    }

    private void ApplyTargetImmediately()
    {
        _dim = _targetDim;
        ApplyDim();
        FinishRamp();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Held at full black: the output window re-asserts topmost every 2 s, so the curtain
        // must re-assert above it on a slower cadence or the black sinks behind the output
        // while the audio stays dimmed. The timer keeps running for exactly this.
        if (_holding)
        {
            if (DateTime.UtcNow - _lastTopAssert > TimeSpan.FromMilliseconds(1500))
            {
                _curtain?.BringToTop();
                _lastTopAssert = DateTime.UtcNow;
            }
            return;
        }
        double t = _rampMs <= 0 ? 1.0 : Math.Clamp(_clock.Elapsed.TotalMilliseconds / _rampMs, 0.0, 1.0);
        _dim = _startDim + (_targetDim - _startDim) * t;
        ApplyDim();
        if (t >= 1.0) FinishRamp();
    }

    private void ApplyDim()
    {
        if (_curtain != null) _curtain.Black = (float)(1.0 - _dim);
        var program = _program();
        if (program != null) program.OutputDim = _dim;
        var music = _music();
        if (music != null) music.OutputDim = _dim;
    }

    private void FinishRamp()
    {
        if (_targetDim >= 1.0)
        {
            // Restored: the live picture is back at full level — the curtain comes down.
            _timer.Stop();
            _holding = false;
            _curtain?.Uncover();
            _active = false;
            Changed?.Invoke();
            return;
        }
        // Fading out: the curtain stays up at full black, holding the frozen picture dark,
        // and the timer keeps running in hold mode (z-order re-assertion only, see OnTick).
        _holding = true;
        _lastTopAssert = DateTime.UtcNow;
        _curtain?.BringToTop();
    }

    private void FinishRestoreCut()
    {
        _timer.Stop();
        _holding = false;
        _dim = 1.0;
        ApplyDim();
        _curtain?.Uncover();
        _active = false;
        Changed?.Invoke();
    }

    /// <summary>Synchronous, total reset: the curtain comes down and both dims return to full at
    /// once. Called when output stops (including F12 — never waits for a fade), when the state
    /// machine leaves Active, and when the program engine is swapped, so no dim can ever leak
    /// into the next show or the next engine. Safe to call when inactive (a no-op apart from
    /// re-asserting full dim, which is already the case).</summary>
    public void Reset()
    {
        if (_disposed) return;
        bool wasActive = _active;
        _timer.Stop();
        _holding = false;
        _curtain?.Uncover();
        _active = false;
        _dim = 1.0;
        _startDim = 1.0;
        _targetDim = 1.0;
        var program = _program();
        if (program != null) program.OutputDim = 1.0;
        var music = _music();
        if (music != null) music.OutputDim = 1.0;
        if (wasActive) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _curtain?.Dispose();
        _curtain = null;
    }
}
