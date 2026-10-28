using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace EveryStage.Terminal.Documents;

/// <summary>Win32 calls used by the document sessions (window discovery, placement, capture).</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr destination, IntPtr source, uint length);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public const uint GA_ROOT = 2;
    public const uint GW_OWNER = 4;
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static Rectangle WindowRect(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;

    /// <summary>The window's visible frame: GetWindowRect minus the invisible resize borders Windows 10
    /// adds around top-level windows. This is what Graphics Capture's frames cover.</summary>
    public static Rectangle GetVisibleFrame(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return WindowRect(hwnd);
    }

    /// <summary>Largest visible descendant of <paramref name="root"/> whose class is one of
    /// <paramref name="classNames"/> (e.g. Word's and WPS's document canvas "_WwG").</summary>
    public static IntPtr FindLargestChild(IntPtr root, params string[] classNames)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(root, (child, _) =>
        {
            if (!IsWindowVisible(child) || !classNames.Contains(ClassName(child), StringComparer.Ordinal)) return true;
            var rect = WindowRect(child);
            long area = (long)rect.Width * rect.Height;
            if (area > bestArea) { bestArea = area; best = child; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Visible top-level windows owned by <paramref name="processId"/>.</summary>
    public static List<IntPtr> TopLevelWindowsOf(int processId)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId && IsWindowVisible(hwnd)) result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Restores (if minimised) and activates a window on the operator's screen.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
    }
}
