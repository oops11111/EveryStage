namespace EveryStage.Terminal.Data;

/// <summary>
/// The Terminal's own configuration (PLANNING.md §8.2's 设置 panel: 通用/显示/播放行为/网络与设备/
/// 关于). PLANNING.md names only the five category labels, not any specific field within either —
/// the fields here are this repository's own choices, picked from real "...随手定的，没有依据"
/// values already flagged in other files' README risk lists, rather than invented from nothing.
/// See <see cref="SettingsStore"/> for persistence and <c>UI/Panels/SettingsPanel</c> for the UI.
/// </summary>
public sealed class AppSettings
{
    // --- 通用 ---

    /// <summary><c>OutputStateMachine.CastSwitchOn</c>'s value when the Terminal starts up.
    /// PLANNING.md doesn't specify a default (see this project's README "已知风险") — exposing it
    /// here beats permanently baking in a guess. Only applied at startup: changing this setting
    /// while running does not retroactively flip the switch that's already on screen, since
    /// "default" and "current" are different things.</summary>
    public bool CastSwitchDefaultOn { get; set; } = true;

    // --- 显示 ---

    /// <summary><see cref="Display.MonitorInfo.DeviceName"/> of the monitor to bind as the
    /// extended display, e.g. <c>"\\.\DISPLAY2"</c> — null means "first non-primary monitor found"
    /// (<c>MonitorService</c>'s original, still-the-fallback behavior). Only takes effect on the
    /// next Terminal start: <c>MonitorService.GetBoundExtendedDisplay</c> is called once at startup
    /// and this repo has no live display-hotplug re-binding yet.</summary>
    public string? PreferredMonitorDeviceName { get; set; }

    // --- 播放行为 ---

    /// <summary>Fallback stay duration, in seconds, for a file whose own
    /// <c>MediaFile.StayDuration</c> isn't set — null (the default) preserves the existing
    /// behavior of holding indefinitely. A per-file value, when configured, always wins over this.
    /// Applied immediately: the next file played picks it up, no restart needed.</summary>
    public int? DefaultStayDurationSeconds { get; set; }
}
