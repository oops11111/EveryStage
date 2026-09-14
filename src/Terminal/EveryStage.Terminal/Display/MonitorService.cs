using System.Drawing;
using System.Windows.Forms;

namespace EveryStage.Terminal.Display;

public sealed record MonitorInfo(string DeviceName, Rectangle Bounds, bool IsPrimary);

/// <summary>
/// Enumerates monitors for picking the bound "扩展屏" (PLANNING.md §5 "多显示器精确定位
/// (EnumDisplayMonitors)"). Uses WinForms' <see cref="Screen"/>, which wraps the same
/// EnumDisplayMonitors/GetMonitorInfo Win32 calls the doc names — no reason to hand-roll the
/// P/Invoke when the framework already exposes it reliably.
/// </summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetAll() =>
        Screen.AllScreens
            .Select(s => new MonitorInfo(s.DeviceName, s.Bounds, s.Primary))
            .ToList();

    /// <summary>
    /// The Terminal's designated output display: the first non-primary monitor, matching "绑定
    /// 扩展屏" — the Terminal drives a dedicated display, not the primary desktop. Returns null on
    /// a single-monitor machine, which the caller must treat as a real configuration state (no
    /// extended display attached yet), not an error to swallow.
    /// </summary>
    public static MonitorInfo? GetBoundExtendedDisplay(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= GetAll();
        return monitors.FirstOrDefault(m => !m.IsPrimary);
    }
}
