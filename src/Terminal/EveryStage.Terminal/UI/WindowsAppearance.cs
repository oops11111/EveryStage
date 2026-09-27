using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

internal static class WindowsAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwaUseImmersiveDarkMode = 20;
    private const int DwaSystemBackdropType = 38;
    private const int DwmSystemBackdropMainWindow = 2;

    public static void UseDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        form.HandleCreated += (_, _) => Apply(form.Handle);
        if (form.IsHandleCreated) Apply(form.Handle);
    }

    private static void Apply(IntPtr handle)
    {
        int enabled = 1;
        // Windows 10 20H1+ uses 20; older Windows 10 builds use 19.
        if (DwmSetWindowAttribute(handle, DwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    /// <summary>Requests the system Mica backdrop on Windows 11 22H2+. On older Windows versions,
    /// or if DWM rejects the attribute, the form keeps its normal painted background.</summary>
    public static void EnableMica(GradientForm form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;

        void ApplyMica()
        {
            try
            {
                int backdrop = DwmSystemBackdropMainWindow;
                form.IsMicaBackdropActive = DwmSetWindowAttribute(
                    form.Handle, DwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
            }
            catch (DllNotFoundException) { form.IsMicaBackdropActive = false; }
            catch (EntryPointNotFoundException) { form.IsMicaBackdropActive = false; }
            if (form.IsMicaBackdropActive)
                ThemeManager.Apply(form, ModernUi.CurrentTheme);
            form.Invalidate(true);
        }

        form.HandleCreated += (_, _) => ApplyMica();
        if (form.IsHandleCreated) ApplyMica();
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
