using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The 大屏 · PGM monitor in the 监看 column: a live copy of exactly what the extended display shows (the
/// local window and the second screen stay in sync — the same output picture, not a separate render).
/// About 8 times a second the output window's screen area is scaled straight into a small bitmap
/// (StretchBlt from the screen, including layered windows such as the transition curtain), so local
/// media, a native PPT show, a device cast and transitions all appear as they really are on air.
/// While on air it has a red flashing border (第二十一轮, instead of the old ON AIR bar).
/// </summary>
internal sealed class ProgramMirror : Control
{
    private const int FrameIntervalMs = 120;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = FrameIntervalMs };
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 500 };
    private Bitmap? _frame;
    private bool _blinkOn = true;

    /// <summary>The output window's screen rectangle while something is on air; null otherwise.</summary>
    public Func<Rectangle?>? SourceBounds { get; set; }

    /// <summary>On air (local media or a device cast): the red border flashes.</summary>
    public bool OnAir
    {
        get => _onAir;
        set
        {
            if (_onAir == value) return;
            _onAir = value;
            _blinkOn = true;
            _blink.Enabled = value;
            Invalidate();
        }
    }
    private bool _onAir;

    /// <summary>Shown over black when nothing is on air (「未连接扩展屏」 / 「大屏未输出」).</summary>
    public string IdleText
    {
        get => _idleText;
        set { if (_idleText != value) { _idleText = value; Invalidate(); } }
    }
    private string _idleText = "大屏未输出";

    public ProgramMirror()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        AccessibleName = "大屏（PGM）画面";
        _timer.Tick += (_, _) => CaptureFrame();
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; Invalidate(); };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _timer.Enabled = Visible;
        if (!Visible) { _frame?.Dispose(); _frame = null; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _timer.Enabled = Visible;
    }

    private Rectangle PictureArea()
    {
        int inset = LogicalToDeviceUnits(3);
        return new Rectangle(inset, inset, Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2));
    }

    /// <summary>One frame: the output's screen area scaled into the picture area (16:9 letterboxed).</summary>
    private void CaptureFrame()
    {
        if (!IsHandleCreated || Width < 8 || Height < 8 || !(Parent?.Visible ?? false)) return;
        // A minimized window still counts as visible: nobody sees the mirror, so do not copy the screen.
        if (FindForm() is { WindowState: FormWindowState.Minimized }) return;
        if (SourceBounds?.Invoke() is not { Width: > 0, Height: > 0 } source)
        {
            if (_frame != null) { _frame.Dispose(); _frame = null; Invalidate(); }
            return;
        }
        var area = PictureArea();
        var fit = Fit(source.Size, area);
        if (fit.Width < 2 || fit.Height < 2) return;
        if (_frame == null || _frame.Size != fit.Size)
        {
            _frame?.Dispose();
            _frame = new Bitmap(fit.Width, fit.Height);
        }
        try
        {
            using var g = Graphics.FromImage(_frame);
            IntPtr target = g.GetHdc();
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(target, 4); // HALFTONE
                SetBrushOrgEx(target, 0, 0, IntPtr.Zero);
                // SRCCOPY, plus CAPTUREBLT only while a transition curtain (a layered window) is up:
                // CAPTUREBLT hides the mouse cursor for the duration of every copy, and at ~8 copies a
                // second per mirror that showed as a flickering cursor on the operator's screen.
                uint rop = 0x00CC0020 | (Display.TransitionCurtain.AnyShowing ? 0x40000000u : 0u);
                StretchBlt(target, 0, 0, fit.Width, fit.Height, screen, source.X, source.Y, source.Width, source.Height, rop);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(target);
            }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException) { return; }
        Invalidate();
    }

    private static Rectangle Fit(Size source, Rectangle area)
    {
        double scale = Math.Min(area.Width / (double)source.Width, area.Height / (double)source.Height);
        int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
        return new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var area = PictureArea();
        if (_frame != null)
        {
            var fit = Fit(_frame.Size, area);
            g.DrawImageUnscaled(_frame, fit.Location);
        }
        else
        {
            using var font = new Font(ModernUi.UiFont, 9F);
            TextRenderer.DrawText(g, _idleText, font, area, Color.FromArgb(150, 160, 175),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        int border = LogicalToDeviceUnits(3);
        var rect = new Rectangle(border / 2, border / 2, Width - border, Height - border);
        if (_onAir && _blinkOn)
        {
            using var pen = new Pen(Color.FromArgb(232, 40, 52), border);
            g.DrawRectangle(pen, rect);
        }
        else
        {
            using var pen = new Pen(Color.FromArgb(_onAir ? 110 : 60, 232, 40, 52), 1);
            g.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _blink.Dispose();
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
}
