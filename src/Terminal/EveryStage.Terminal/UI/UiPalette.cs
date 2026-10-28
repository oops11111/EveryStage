using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>Semantic design tokens shared by the application frame and its panels.</summary>
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
    public static UiPalette For(AppTheme theme) => theme switch
    {
        AppTheme.Light => new(
            Color.FromArgb(246, 248, 251), Color.FromArgb(250, 252, 255), Color.FromArgb(255, 255, 255),
            Color.FromArgb(249, 251, 254), Color.FromArgb(202, 215, 229), Color.FromArgb(28, 39, 55),
            Color.FromArgb(99, 115, 136), Color.FromArgb(48, 121, 232), Color.FromArgb(206, 61, 72),
            Color.FromArgb(197, 138, 22), Color.FromArgb(27, 156, 103), Color.FromArgb(202, 255, 255, 255)),
        AppTheme.Tech => new(
            Color.FromArgb(5, 16, 30), Color.FromArgb(7, 29, 48), Color.FromArgb(10, 38, 61),
            Color.FromArgb(13, 48, 75), Color.FromArgb(25, 82, 112), Color.FromArgb(220, 244, 255),
            Color.FromArgb(125, 177, 202), Color.FromArgb(0, 190, 255), Color.FromArgb(255, 81, 105),
            Color.FromArgb(255, 196, 61), Color.FromArgb(35, 219, 164), Color.FromArgb(202, 8, 40, 64)),
        _ => new(
            Color.FromArgb(9, 11, 14), Color.FromArgb(14, 17, 21), Color.FromArgb(19, 23, 29),
            Color.FromArgb(27, 33, 40), Color.FromArgb(64, 74, 86), Color.FromArgb(239, 243, 247),
            Color.FromArgb(162, 174, 188), Color.FromArgb(66, 132, 245), Color.FromArgb(255, 93, 93),
            Color.FromArgb(240, 180, 60), Color.FromArgb(55, 222, 120), Color.FromArgb(202, 19, 23, 29)),
    };
}
