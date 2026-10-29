using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.Display;

/// <summary>
/// The big-screen transition layer: a click-through, never-activated, top-most window over the output
/// window. When Program is about to change content it shows a still of what is on the big screen right
/// now, the content changes underneath it, and once the new item has its first picture the still is
/// 叠化 (dissolved away) or 经黑场 (faded to black, then the black faded away). Video, documents, captured
/// slide shows and device casts are all covered the same way because the still is taken from the screen.
/// </summary>
internal sealed class TransitionCurtain : Form
{
    private Bitmap? _still;
    private float _black;   // 0..1: how far the still is faded to black

    public TransitionCurtain(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = bounds;
        BackColor = Color.Black;
        // WinForms makes the window layered (and sets its alpha) itself; Opacity then fades it.
        AllowTransparency = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Click-through, never activated, no task-bar / Alt+Tab entry.
            cp.ExStyle |= 0x00000020 | 0x08000000 | 0x00000080;
            return cp;
        }
    }

    /// <summary>Freezes <paramref name="still"/> (owned) on top of the output, fully opaque.</summary>
    public void Cover(Bitmap still, Rectangle bounds)
    {
        _still?.Dispose();
        _still = still;
        _black = 0;
        Bounds = bounds;
        Opacity = 1;
        if (!Visible) Show();
        BringToTop();
        Invalidate();
    }

    public float Black
    {
        get => _black;
        set { _black = Math.Clamp(value, 0f, 1f); Invalidate(); }
    }

    public void Uncover()
    {
        Hide();
        _still?.Dispose();
        _still = null;
    }

    /// <summary>The output window re-asserts top-most every few seconds; keep the curtain above it.</summary>
    public void BringToTop()
    {
        if (IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.Low;
        if (_still != null && _black < 1f) g.DrawImage(_still, ClientRectangle);
        else g.Clear(Color.Black);
        if (_black > 0f && _black < 1f)
        {
            using var shade = new SolidBrush(Color.FromArgb((int)(_black * 255), Color.Black));
            g.FillRectangle(shade, ClientRectangle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _still?.Dispose(); _still = null; }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// Runs the big-screen transitions: on <see cref="PlaybackEngine.ProgramContentSwitching"/> it covers
/// the output with a still of the current picture, waits for the new item's first picture (a native
/// slide show can take seconds; capped at 8 s), then plays the item's transition — the item's own
/// <see cref="MediaFile.TransitionOverride"/>, else the 设置 default. 直切, an output that was not on air
/// yet, and a transition already running (a quick 下一项 下一项) skip straight to the newest content.
/// </summary>
internal sealed class TransitionController : IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly PlaybackEngine _program;
    private readonly OverlayWindow _overlay;
    private readonly SettingsStore _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private TransitionCurtain? _curtain;
    private MediaFile? _target;
    private TransitionStyle _style;
    private int _duration;
    private DateTime _phaseStart;
    private DateTime? _readyAt;
    private Phase _phase = Phase.Idle;

    private enum Phase { Idle, FadingToBlack, Waiting, Revealing }

    public TransitionController(PlaybackEngine program, OverlayWindow overlay, SettingsStore settings)
    {
        _program = program;
        _overlay = overlay;
        _settings = settings;
        _program.ProgramContentSwitching += OnSwitching;
        _timer.Tick += (_, _) => Tick();
    }

    private void OnSwitching(MediaFile next)
    {
        var settings = _settings.Current;
        var style = next.TransitionOverride ?? settings.Transition;
        if (style == TransitionStyle.Cut || !_overlay.Visible || _overlay.WindowState == FormWindowState.Minimized)
        {
            Finish();
            return;
        }
        // A transition already covers the screen: keep its still, retarget to the newest item.
        if (_phase == Phase.Idle)
        {
            Bitmap still;
            try
            {
                var bounds = _overlay.Bounds;
                still = new Bitmap(bounds.Width, bounds.Height);
                using var g = Graphics.FromImage(still);
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            catch (Exception)
            {
                return; // no still (secure desktop, locked session): just cut
            }
            _curtain ??= new TransitionCurtain(_overlay.Bounds);
            _curtain.Cover(still, _overlay.Bounds);
        }
        else if (_curtain != null)
        {
            // Retargeted mid-transition: cover fully again; a dip goes straight to black.
            _curtain.Opacity = 1;
            if (style == TransitionStyle.DipToBlack) _curtain.Black = 1;
        }
        _target = next;
        _style = style;
        _duration = Math.Clamp(settings.TransitionMilliseconds, 100, 3000);
        _readyAt = null;
        _phaseStart = DateTime.UtcNow;
        _phase = _style == TransitionStyle.DipToBlack && _curtain?.Black < 1f ? Phase.FadingToBlack : Phase.Waiting;
        _timer.Start();
    }

    /// <summary>The new item shows its first picture (or failed, so there is nothing to wait for).</summary>
    private bool TargetReady()
    {
        if (!ReferenceEquals(_program.CurrentFile, _target)) return false;
        if (_program.State == PlaybackChannelState.Failed) return true;
        if (_program.State is not (PlaybackChannelState.Playing or PlaybackChannelState.Paused)) return false;
        return _program.DocumentMode != DocumentPlaybackMode.NativeShow || _program.OfficeFrameSequence >= 0;
    }

    private void Tick()
    {
        var curtain = _curtain;
        if (curtain == null || _phase == Phase.Idle) { _timer.Stop(); return; }
        // Output stopped (停止输出 / cast switch off): never leave a still on the monitor.
        if (!_overlay.Visible) { Finish(); return; }
        curtain.BringToTop();
        double half = _style == TransitionStyle.DipToBlack ? _duration / 2.0 : _duration;
        double t = (DateTime.UtcNow - _phaseStart).TotalMilliseconds / half;
        switch (_phase)
        {
            case Phase.FadingToBlack:
                curtain.Black = (float)Math.Min(1, t);
                if (t >= 1) { _phase = Phase.Waiting; _phaseStart = DateTime.UtcNow; }
                break;
            case Phase.Waiting:
                bool ready = TargetReady();
                if (ready) _readyAt ??= DateTime.UtcNow;
                bool timedOut = DateTime.UtcNow - _phaseStart > ReadyTimeout;
                if (timedOut && _style == TransitionStyle.Dissolve) curtain.Black = 1; // no picture: go to black, then reveal
                if ((_readyAt is { } at && DateTime.UtcNow - at >= Settle) || timedOut)
                {
                    _phase = Phase.Revealing;
                    _phaseStart = DateTime.UtcNow;
                }
                break;
            case Phase.Revealing:
                curtain.Opacity = Math.Max(0, 1 - t);
                if (t >= 1) Finish();
                break;
        }
    }

    private void Finish()
    {
        _timer.Stop();
        _phase = Phase.Idle;
        _target = null;
        _curtain?.Uncover();
    }

    public void Dispose()
    {
        _program.ProgramContentSwitching -= OnSwitching;
        _timer.Dispose();
        _curtain?.Dispose();
    }
}
