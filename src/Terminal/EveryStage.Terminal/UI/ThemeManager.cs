using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

public static class ThemeManager
{
    public static void Apply(Control root, AppTheme theme)
    {
        var palette = theme switch
        {
            AppTheme.Dark => new Palette(ModernUi.Background, ModernUi.Surface, ModernUi.Text, ModernUi.Accent),
            AppTheme.Tech => new Palette(Color.FromArgb(5, 16, 30), Color.FromArgb(12, 42, 67), Color.FromArgb(220, 244, 255), Color.FromArgb(0, 190, 255)),
            _ => new Palette(SystemColors.Control, Color.White, SystemColors.ControlText, Color.FromArgb(0, 120, 215)),
        };
        ApplyRecursive(root, palette);
    }

    private static void ApplyRecursive(Control control, Palette palette)
    {
        control.ForeColor = palette.Foreground;
        control.BackColor = control switch
        {
            Button modernButton when modernButton.BackColor == ModernUi.Accent || modernButton.BackColor == ModernUi.Danger => modernButton.BackColor,
            Button => palette.Surface,
            TextBoxBase or ListView or TreeView or ListBox or ComboBox or NumericUpDown => palette.Surface,
            TabPage => palette.Background,
            _ => palette.Background,
        };
        if (control is Button button) button.FlatStyle = FlatStyle.Flat;
        if (control is Button themedButton) themedButton.FlatAppearance.BorderColor = palette.Accent;
        foreach (Control child in control.Controls) ApplyRecursive(child, palette);
    }

    private readonly record struct Palette(Color Background, Color Surface, Color Foreground, Color Accent);
}
