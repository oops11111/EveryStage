using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

internal static class WindowsAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwaWindowCornerPreference = 33, DwaBorderColor = 34, CornerRound = 2;

    /// <summary>Windows 11 (build 22000+), where DWM offers rounded corners and a coloured outline.</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void UseDarkTitleBar(Form form)
    {
        form.HandleCreated += (_, _) =>
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        };
    }

    /// <summary>Windows 11 frame for the borderless window: rounded corners and a 1px outline in the
    /// border colour (DWM rounds framed windows itself, not borderless ones). This used to request
    /// acrylic blur-behind through the undocumented SetWindowCompositionAttribute; the form paints an
    /// opaque gradient over its whole client area, so the blur was never visible, while that API is
    /// known to make dragging the window stutter on Windows 10 1903+ and Windows 11.</summary>
    public static void EnableGlass(Form form)
    {
        if (!IsWindows11) return;
        void Apply()
        {
            try
            {
                int corner = CornerRound;
                DwmSetWindowAttribute(form.Handle, DwaWindowCornerPreference, ref corner, sizeof(int));
                int border = ColorTranslator.ToWin32(ModernUi.Border);
                DwmSetWindowAttribute(form.Handle, DwaBorderColor, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        form.HandleCreated += (_, _) => Apply();
        if (form.IsHandleCreated) Apply();
    }
}
