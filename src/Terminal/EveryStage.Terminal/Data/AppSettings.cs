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
    public const int CurrentSchemaVersion = 3;

    /// <summary>Persisted configuration schema. A missing value denotes the original unversioned
    /// format and is migrated by <see cref="SettingsStore"/> when loaded.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

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
    /// (<c>MonitorService</c>'s original, still-the-fallback behavior).
    ///
    /// This comment used to claim this "only takes effect on the next Terminal start" because
    /// "this repo has no live display-hotplug re-binding yet" — that stopped being accurate once
    /// <c>Program.HandleDisplaySettingsChanged</c> was added: it re-reads this setting's CURRENT
    /// value (not a value cached at startup) every time <c>SystemEvents.DisplaySettingsChanged</c>
    /// fires, and rebinds live if the freshly-preferred monitor differs from whatever is currently
    /// bound. So a change here CAN take effect without a restart — but only opportunistically,
    /// triggered by some actual display reconfiguration event happening to fire afterward (a
    /// monitor being plugged/unplugged, resolution changing, etc.), not immediately the moment this
    /// setting is saved. There is still no way to force an immediate re-bind on save alone.</summary>
    public string? PreferredMonitorDeviceName { get; set; }

    // --- 播放行为 ---

    /// <summary>Fallback stay duration, in seconds, for a file whose own
    /// <c>MediaFile.StayDuration</c> isn't set — null (the default) preserves the existing
    /// behavior of holding indefinitely. A per-file value, when configured, always wins over this.
    /// Applied immediately: the next file played picks it up, no restart needed.</summary>
    public int? DefaultStayDurationSeconds { get; set; }

    // --- 过场 ---

    /// <summary>How the big screen changes from one item to the next (an item can override it).</summary>
    public TransitionStyle Transition { get; set; } = TransitionStyle.DipToBlack;

    /// <summary>Length of the 叠化 / 经黑场 transition.</summary>
    public int TransitionMilliseconds { get; set; } = 800;

    // --- PPT 翻页过场 ---

    /// <summary>True: every presentation page turn uses <see cref="SlideTransition"/> instead of the
    /// deck's own transitions (static slides and the native show alike).</summary>
    public bool UnifySlideTransitions { get; set; } = true;
    public SlideTransitionEffect SlideTransition { get; set; } = SlideTransitionEffect.Fade;
    public SlideTransitionDirection SlideTransitionDirection { get; set; } = SlideTransitionDirection.LeftToRight;
    public int SlideTransitionMilliseconds { get; set; } = 800;

    /// <summary>Slides whose own transition is 平滑 (Morph) keep it: it moves objects between slides.</summary>
    public bool KeepMorphTransitions { get; set; } = true;

    /// <summary>Slides that advance on a timer keep their own transition (the change cannot be anticipated).</summary>
    public bool KeepAutoAdvanceTransitions { get; set; } = true;

    /// <summary>Black / white screen and back also use the slide transition.</summary>
    public bool TransitionOnBlackScreen { get; set; }

    /// <summary>A copy to change and save (every field, so a new setting can never be dropped on save).</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>PPT 翻页过场: 渐隐渐显 / 翻页效果 / 雨点浸润.</summary>
public enum SlideTransitionEffect { Fade, PageTurn, RainSoak }

/// <summary>Which way a slide transition travels.</summary>
public enum SlideTransitionDirection { TopToBottom, BottomToTop, LeftToRight, RightToLeft }

/// <summary>Big-screen transition between items: 直切 / 叠化 / 经黑场.</summary>
public enum TransitionStyle { Cut, Dissolve, DipToBlack }

/// <summary>Stored as its number. <see cref="System"/> (appended, so existing files keep their meaning)
/// follows Windows' app mode and is resolved to Light or Dark by <c>ThemeManager.Resolve</c>.</summary>
public enum AppTheme { Light, Dark, Tech, System }
