using System.Runtime.InteropServices;
using System.Windows.Forms;
using EveryStage.Terminal.ContentEngine;

namespace EveryStage.Terminal.Display;

/// <summary>
/// The borderless, always-on-top window that covers the bound extended display
/// (PLANNING.md §5 "无边框全屏窗口" + §9.2). Created once and kept alive for the app's lifetime —
/// "投/断" only hides or shows it, it is never destroyed/recreated, so a future D3D11 swap chain
/// attached to <see cref="Handle"/> (see the Phase 0 demo's SwapChainPresenter) stays valid across
/// every toggle instead of paying swap-chain setup cost on each "投".
/// </summary>
public sealed class OverlayWindow : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Other applications (notably other TOPMOST windows, or an app entering exclusive full-screen
    // on this same monitor) can steal top-of-z-order out from under us. Re-assert periodically
    // rather than trusting WinForms' one-time TopMost=true to hold forever — this is the same
    // "keep nagging the z-order" pattern most always-on-top overlay apps use.
    private readonly System.Windows.Forms.Timer _topMostReasserter;

    public MonitorInfo Monitor { get; private set; }

    /// <summary>
    /// Where image/document renderers' frames land (PLANNING.md §3: image/PDF are rendered
    /// directly by the Terminal's Content Engine, not screen-captured like Caster content).
    /// </summary>
    public ContentSurface ContentSurface { get; }

    /// <summary>
    /// A separate child window that <see cref="VideoContentController"/> attaches its D3D11 swap
    /// chain to. Video needs its own HWND rather than sharing <see cref="ContentSurface"/>'s: a
    /// GPU swap chain and GDI+ <c>Control.Paint</c> can't usefully composite onto the same window
    /// surface, and routing video through GDI+ would defeat the zero-copy pipeline entirely. Use
    /// <see cref="ShowVideoSurface"/>/<see cref="ShowImageSurface"/> to switch which one is on top
    /// when content changes.
    /// </summary>
    public Control VideoHost { get; }

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = monitor.Bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;

        VideoHost = new Control { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.Black };
        ContentSurface = new ContentSurface();
        // Both added up front — Control.Handle (needed to attach a swap chain) only exists once a
        // control has been created, and adding it to the form's Controls collection is what
        // triggers that, well before any content actually needs to display.
        Controls.Add(VideoHost);
        Controls.Add(ContentSurface);

        _topMostReasserter = new System.Windows.Forms.Timer { Interval = 2000 };
        _topMostReasserter.Tick += (_, _) => ReassertTopMost();
    }

    /// <summary>Brings the image/PDF surface to the front, hiding the video host.</summary>
    public void ShowImageSurface()
    {
        VideoHost.Visible = false;
        ContentSurface.Visible = true;
        ContentSurface.BringToFront();
    }

    /// <summary>Brings the video host to the front, hiding the image/PDF surface.</summary>
    public void ShowVideoSurface()
    {
        ContentSurface.Visible = false;
        VideoHost.Visible = true;
        VideoHost.BringToFront();
    }

    /// <summary>Rebinds this (already-created) overlay to a different physical monitor, e.g. after
    /// a display-configuration change is detected. Does not hide/show — caller decides that.</summary>
    public void Rebind(MonitorInfo monitor)
    {
        Monitor = monitor;
        Bounds = monitor.Bounds;
    }

    /// <summary>"投" — show the overlay and start defending its z-order.</summary>
    public void ShowOverlay()
    {
        if (!Visible) Show();
        ReassertTopMost();
        _topMostReasserter.Start();
    }

    /// <summary>
    /// "断" — hand the screen back to Windows. This only hides the window; whatever was on this
    /// monitor before (desktop, wallpaper, another app's window) was never touched, so it is
    /// simply revealed again (PLANNING.md §9.2).
    /// </summary>
    public void HideOverlay()
    {
        _topMostReasserter.Stop();
        Hide();
    }

    private void ReassertTopMost()
    {
        if (!IsHandleCreated) return;
        // Bug fixed here: SWP_NOMOVE was declared (above) but never actually included in these
        // flags — every 2-second tick was therefore an unconditional move-to-(X,Y) as well as a
        // z-order reassert, even though this method's whole job (per its own doc comment) is "keep
        // nagging the z-order", not repositioning. Harmless in the common case since X/Y already
        // match Monitor.Bounds (nothing else ever moves this borderless overlay), but a real, if
        // silent, deviation from what the code clearly intended — the unused constant sitting right
        // there is the tell. Now genuinely a pure z-order-only reassert, matching SWP_NOSIZE's own
        // "don't touch dimensions either" treatment right next to it.
        SetWindowPos(Handle, HWND_TOPMOST, Monitor.Bounds.X, Monitor.Bounds.Y, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _topMostReasserter.Dispose();
        base.Dispose(disposing);
    }
}
