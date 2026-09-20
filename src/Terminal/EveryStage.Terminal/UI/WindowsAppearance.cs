using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

internal static class WindowsAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }
}
