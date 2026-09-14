using System.Runtime.InteropServices;
using System.Windows.Forms;

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

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = monitor.Bounds;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;

        _topMostReasserter = new System.Windows.Forms.Timer { Interval = 2000 };
        _topMostReasserter.Tick += (_, _) => ReassertTopMost();
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
        SetWindowPos(Handle, HWND_TOPMOST, Monitor.Bounds.X, Monitor.Bounds.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _topMostReasserter.Dispose();
        base.Dispose(disposing);
    }
}
