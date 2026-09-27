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
            Color.FromArgb(239, 243, 249), Color.FromArgb(248, 250, 253), Color.White,
            Color.FromArgb(244, 247, 251), Color.FromArgb(213, 222, 233), Color.FromArgb(28, 39, 55),
            Color.FromArgb(99, 115, 136), Color.FromArgb(48, 121, 232), Color.FromArgb(206, 61, 72),
            Color.FromArgb(197, 138, 22), Color.FromArgb(27, 156, 103), Color.FromArgb(255, 244, 255, 255)),
        AppTheme.Tech => new(
            Color.FromArgb(5, 16, 30), Color.FromArgb(7, 29, 48), Color.FromArgb(10, 38, 61),
            Color.FromArgb(13, 48, 75), Color.FromArgb(25, 82, 112), Color.FromArgb(220, 244, 255),
            Color.FromArgb(125, 177, 202), Color.FromArgb(0, 190, 255), Color.FromArgb(255, 81, 105),
            Color.FromArgb(255, 196, 61), Color.FromArgb(35, 219, 164), Color.FromArgb(255, 8, 40, 64)),
        _ => new(
            Color.FromArgb(7, 28, 51), Color.FromArgb(8, 31, 57), Color.FromArgb(12, 36, 64),
            Color.FromArgb(12, 38, 80), Color.FromArgb(30, 54, 88), Color.FromArgb(234, 241, 248),
            Color.FromArgb(150, 170, 196), Color.FromArgb(10, 112, 254), Color.FromArgb(255, 93, 93),
            Color.FromArgb(240, 180, 60), Color.FromArgb(55, 222, 120), Color.FromArgb(255, 10, 30, 54)),
    };
}
