using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

internal static class WindowsAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwaUseImmersiveDarkMode = 20;
    private const int DwaWindowCornerPreference = 33;
    private const int DwaBorderColor = 34;
    private const int CornerRound = 2, CornerRoundSmall = 3;

    /// <summary>Windows 11 (build 22000+): rounded corners and a coloured window border are available
    /// from DWM. On Windows 10 every Win11-only call below is skipped.</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void UseDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        form.HandleCreated += (_, _) => ApplyTitleBarTheme(form, ModernUi.CurrentTheme != EveryStage.Terminal.Data.AppTheme.Light);
        if (form.IsHandleCreated) ApplyTitleBarTheme(form, ModernUi.CurrentTheme != EveryStage.Terminal.Data.AppTheme.Light);
    }

    public static void ApplyTitleBarTheme(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
        int enabled = dark ? 1 : 0;
        // Windows 10 20H1+ uses 20; older Windows 10 builds use 19.
        if (DwmSetWindowAttribute(form.Handle, DwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        if (IsWindows11)
        {
            // The 1px window outline in the palette's border colour, as Win11 draws it for its own apps.
            int border = ColorTranslator.ToWin32(ModernUi.Border);
            DwmSetWindowAttribute(form.Handle, DwaBorderColor, ref border, sizeof(int));
        }
    }

    /// <summary>Windows 11 rounded corners for a borderless window with its own title bar (DWM rounds
    /// framed windows by itself, but not borderless ones). Never used for the full-screen output
    /// window: a rounded corner on the LED/projector image would cut off content.</summary>
    public static void UseRoundedCorners(Form form)
    {
        if (!IsWindows11) return;
        form.HandleCreated += (_, _) => SetCorner(form.Handle, CornerRound);
        if (form.IsHandleCreated) SetCorner(form.Handle, CornerRound);
    }

    /// <summary>Small Windows 11 corners for a menu window (context menus, submenus, overflow).</summary>
    public static void UseMenuCorners(ToolStripDropDown dropDown)
    {
        if (IsWindows11 && dropDown.IsHandleCreated) SetCorner(dropDown.Handle, CornerRoundSmall);
    }

    private static void SetCorner(IntPtr handle, int preference)
    {
        try { DwmSetWindowAttribute(handle, DwaWindowCornerPreference, ref preference, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Windows "应用模式" (Settings → Personalization → Colors): true for light. Missing value
    /// (older builds) reads as light, Windows' own default.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception) { return true; }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>Native scrollbars (AutoScroll panels, ListView, TreeView) in the palette's lightness:
    /// the <c>DarkMode_Explorer</c> theme class (Windows 10 1809+) on the dark themes; on the light
    /// theme the control's default theme is restored (null), so it looks exactly as before. No-op until
    /// the control has a handle — see <c>ThemeManager.ThemeScrollbars</c>, which re-applies from
    /// HandleCreated.</summary>
    public static void ApplyScrollbarTheme(Control control, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || !control.IsHandleCreated) return;
        try { SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
