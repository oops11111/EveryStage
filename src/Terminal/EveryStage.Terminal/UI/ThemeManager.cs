using System.Runtime.CompilerServices;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

public static class ThemeManager
{
    public static void Apply(Control root, AppTheme theme)
    {
        var previous = ModernUi.Palette;
        var palette = UiPalette.For(theme);
        ModernUi.SetPalette(palette, theme);
        int glassOpacity = root.FindForm() is GradientForm { IsMicaBackdropActive: true } ? 224 : 255;
        ApplyRecursive(root, palette, previous, glassOpacity);
        RefreshThemeAwareControls(root);
        ThemeScrollbars(root);
        root.Invalidate(true);
    }

    /// <summary>Brings a control tree that wasn't attached when <see cref="Apply"/> ran up to the current
    /// palette, mapping from <paramref name="themedWith"/> — the palette it was last themed with.
    /// MainWindow keeps only the visible page in its tree, so the other pages miss both the startup
    /// theme and any theme switch made on 设置 until they are shown.</summary>
    internal static void Reapply(Control root, UiPalette themedWith)
    {
        // Before the early return: a page shown for the first time under the startup palette still
        // needs its scrollbar hooks even though no colour mapping is due.
        ThemeScrollbars(root);
        var palette = ModernUi.Palette;
        if (themedWith == palette) return;
        int glassOpacity = root.FindForm() is GradientForm { IsMicaBackdropActive: true } ? 224 : 255;
        ApplyRecursive(root, palette, themedWith, glassOpacity);
        RefreshThemeAwareControls(root);
        root.Invalidate(true);
    }

    private static readonly ConditionalWeakTable<Control, object> s_scrollbarHooks = new();

    /// <summary>Native scrollbars that match the palette on every scrolling control under
    /// <paramref name="root"/>: applied now where a handle exists, and from HandleCreated for controls
    /// (hidden pages, unselected tab pages) whose handle comes later or gets recreated.</summary>
    private static void ThemeScrollbars(Control root)
    {
        if (root is ScrollableControl { AutoScroll: true } or ListView or TreeView or ListBox)
        {
            if (!s_scrollbarHooks.TryGetValue(root, out _))
            {
                s_scrollbarHooks.Add(root, new object());
                root.HandleCreated += (_, _) => WindowsAppearance.ApplyScrollbarTheme(root, ModernUi.CurrentTheme != AppTheme.Light);
            }
            WindowsAppearance.ApplyScrollbarTheme(root, ModernUi.CurrentTheme != AppTheme.Light);
        }
        foreach (Control child in root.Controls) ThemeScrollbars(child);
    }

    private static void RefreshThemeAwareControls(Control control)
    {
        if (control is EveryStage.Terminal.UI.Panels.ActivitiesPanel activities) activities.RefreshTheme();
        foreach (Control child in control.Controls) RefreshThemeAwareControls(child);
    }

    private static void ApplyRecursive(Control control, UiPalette palette, UiPalette previous, int glassOpacity)
    {
        control.ForeColor = MapForeground(control.ForeColor, palette, previous);
        if (control is GlassPanel glassPanel)
            glassPanel.GlassTint = Color.FromArgb(glassOpacity, palette.GlassTint);
        control.BackColor = control switch
        {
            PillButton => Color.Transparent,
            ToggleSwitch => Color.Transparent,
            GlassPanel => Color.Transparent,
            Label or CheckBox or RadioButton => Color.Transparent,
            Button modernButton when modernButton.BackColor == previous.Accent => palette.Accent,
            Button modernButton when modernButton.BackColor == previous.Danger => palette.Danger,
            Button modernButton when modernButton.BackColor == previous.Rail => palette.Rail,
            Button modernButton when modernButton.BackColor == previous.SurfaceRaised => palette.SurfaceRaised,
            Button => palette.Surface,
            ListView => palette.Background,
            TextBoxBase or TreeView or ListBox or ComboBox or NumericUpDown => palette.Surface,
            TabPage => palette.Background,
            UserControl userControl when userControl.BackColor == Color.Transparent => Color.Transparent,
            Panel panel when panel.BackColor == Color.Transparent => Color.Transparent,
            Panel panel when panel.BackColor == previous.Rail => palette.Rail,
            Panel panel when panel.BackColor == previous.Surface => palette.Surface,
            Panel panel when panel.BackColor == previous.SurfaceRaised => palette.SurfaceRaised,
            Control other when other.BackColor == previous.Accent => palette.Accent,
            Control other when other.BackColor == previous.Danger => palette.Danger,
            Control other when other.BackColor == previous.Warning => palette.Warning,
            Control other when other.BackColor == previous.Rail => palette.Rail,
            Control other when other.BackColor == previous.SurfaceRaised => palette.SurfaceRaised,
            Control other when other.BackColor == previous.Surface => palette.Surface,
            _ => palette.Background,
        };
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = button.FlatAppearance.BorderColor == previous.Danger
                ? palette.Danger
                : button.FlatAppearance.BorderColor == previous.Border ? palette.Border : palette.Accent;
        }
        foreach (Control child in control.Controls) ApplyRecursive(child, palette, previous, glassOpacity);
    }

    private static Color MapForeground(Color current, UiPalette palette, UiPalette previous)
    {
        if (current == previous.Muted || current == Color.DimGray) return palette.Muted;
        if (current == previous.Success || current == Color.SeaGreen) return palette.Success;
        if (current == previous.Danger || current == Color.DarkRed) return palette.Danger;
        if (current == previous.Warning || current == Color.Goldenrod) return palette.Warning;
        if (current == previous.Accent) return palette.Accent;
        return palette.Text;
    }
}
