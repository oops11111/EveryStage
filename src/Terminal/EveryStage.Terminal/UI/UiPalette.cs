using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>Semantic design tokens shared by the application frame and its panels.
/// Text/icon colours and filled-button colours have separate roles. Preview, Program and selection
/// use independent tokens so live output cannot be confused with a selected item.</summary>
internal sealed record UiPalette(
    Color Background,
    Color Rail,
    Color Surface,
    Color SurfaceRaised,
    Color Border,
    Color Text,
    Color Muted,
    Color Accent,
    Color Danger,
    Color Warning,
    Color Success,
    Color GlassTint)
{
    public Color AccentFill { get; init; } = Color.FromArgb(40, 90, 190);
    public Color DangerFill { get; init; } = Color.FromArgb(180, 40, 48);
    public Color ProgramBorder { get; init; } = Color.FromArgb(198, 66, 94);
    public Color Preview { get; init; } = Color.FromArgb(34, 197, 94);
    public Color PreviewForeground { get; init; } = Color.Black;
    public bool IsHighContrast { get; init; }
    public Color AccentFillForeground { get; init; } = Color.White;
    public Color DangerFillForeground { get; init; } = Color.White;
    public Color Focus { get; init; } = Color.FromArgb(66, 132, 245);
    public static UiPalette For(AppTheme theme) => ThemeManager.HighContrast ? SystemContrast() : Normal(theme);
    internal static UiPalette Normal(AppTheme theme) => ThemeManager.Resolve(theme) switch
    {
        AppTheme.Light => new(
            Color.FromArgb(246, 248, 251), Color.FromArgb(250, 252, 255), Color.FromArgb(255, 255, 255),
            Color.FromArgb(249, 251, 254), Color.FromArgb(202, 215, 229), Color.FromArgb(28, 39, 55),
            Color.FromArgb(99, 115, 136), Color.FromArgb(43, 109, 209), Color.FromArgb(206, 61, 72),
            Color.FromArgb(148, 104, 16), Color.FromArgb(22, 125, 82), Color.FromArgb(202, 255, 255, 255))
            { AccentFill = Color.FromArgb(43, 109, 209), Preview = Color.FromArgb(22, 125, 82), PreviewForeground = Color.White },
        AppTheme.Tech => new(
            Color.FromArgb(5, 16, 30), Color.FromArgb(7, 29, 48), Color.FromArgb(10, 38, 61),
            Color.FromArgb(13, 48, 75), Color.FromArgb(25, 82, 112), Color.FromArgb(220, 244, 255),
            Color.FromArgb(125, 177, 202), Color.FromArgb(0, 190, 255), Color.FromArgb(230, 73, 94),
            Color.FromArgb(255, 196, 61), Color.FromArgb(35, 219, 164), Color.FromArgb(202, 8, 40, 64))
            { AccentFill = Color.FromArgb(11, 94, 142), DangerFill = Color.FromArgb(180, 40, 56) },
        _ => new(
            Color.FromArgb(9, 11, 14), Color.FromArgb(14, 17, 21), Color.FromArgb(19, 23, 29),
            Color.FromArgb(27, 33, 40), Color.FromArgb(64, 74, 86), Color.FromArgb(239, 243, 247),
            Color.FromArgb(162, 174, 188), Color.FromArgb(63, 125, 233), Color.FromArgb(232, 104, 127),
            Color.FromArgb(240, 180, 60), Color.FromArgb(55, 222, 120), Color.FromArgb(202, 19, 23, 29)),
    };

    private static UiPalette SystemContrast() => new(
        SystemColors.Window, SystemColors.Control, SystemColors.Window, SystemColors.Control,
        SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText,
        SystemColors.Highlight, SystemColors.WindowText, SystemColors.WindowText,
        SystemColors.WindowText, SystemColors.Window)
    {
        IsHighContrast = true,
        AccentFill = SystemColors.Highlight, AccentFillForeground = SystemColors.HighlightText,
        DangerFill = SystemColors.Highlight, DangerFillForeground = SystemColors.HighlightText,
        ProgramBorder = SystemColors.WindowText, Preview = SystemColors.WindowText,
        PreviewForeground = SystemColors.Window, Focus = SystemColors.Highlight,
    };
}
