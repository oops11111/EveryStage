using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

public static class ThemeManager
{
    public static void Apply(Control root, AppTheme theme)
    {
        var palette = theme switch
        {
            AppTheme.Dark => new Palette(ModernUi.Background, ModernUi.Surface, ModernUi.Text, ModernUi.Accent,
                Color.FromArgb(205, 13, 30, 50)),
            AppTheme.Tech => new Palette(Color.FromArgb(5, 16, 30), Color.FromArgb(12, 42, 67), Color.FromArgb(220, 244, 255), Color.FromArgb(0, 190, 255),
                Color.FromArgb(205, 8, 40, 64)),
            _ => new Palette(SystemColors.Control, Color.White, SystemColors.ControlText, Color.FromArgb(0, 120, 215),
                Color.FromArgb(238, 244, 249, 255)),
        };
        ApplyRecursive(root, palette);
        root.Invalidate(true);
    }

    private static void ApplyRecursive(Control control, Palette palette)
    {
        if (!HasSemanticForeground(control)) control.ForeColor = palette.Foreground;
        if (control is GlassPanel glassPanel) glassPanel.GlassTint = palette.GlassTint;
        control.BackColor = control switch
        {
            GlassPanel => Color.Transparent,
            Label or CheckBox or RadioButton => Color.Transparent,
            Button modernButton when modernButton.BackColor == ModernUi.Accent || modernButton.BackColor == ModernUi.Danger => modernButton.BackColor,
            Button => palette.Surface,
            TextBoxBase or ListView or TreeView or ListBox or ComboBox or NumericUpDown => palette.Surface,
            TabPage => palette.Background,
            UserControl userControl when palette.Background == ModernUi.Background && userControl.BackColor == Color.Transparent => Color.Transparent,
            Panel panel when palette.Background == ModernUi.Background && panel.BackColor is var existing
                && (existing == ModernUi.Rail || existing == ModernUi.Surface || existing == ModernUi.SurfaceRaised) => existing,
            _ => palette.Background,
        };
        if (control is Button button) button.FlatStyle = FlatStyle.Flat;
        if (control is Button themedButton) themedButton.FlatAppearance.BorderColor = palette.Accent;
        foreach (Control child in control.Controls) ApplyRecursive(child, palette);
    }

    private static bool HasSemanticForeground(Control control) => control.ForeColor == ModernUi.Muted
        || control.ForeColor == ModernUi.Success
        || control.ForeColor == ModernUi.Danger
        || control.ForeColor == Color.DimGray
        || control.ForeColor == Color.SeaGreen
        || control.ForeColor == Color.DarkRed;

    private readonly record struct Palette(Color Background, Color Surface, Color Foreground, Color Accent, Color GlassTint);
}
