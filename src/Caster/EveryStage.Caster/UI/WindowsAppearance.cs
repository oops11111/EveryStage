using System.Runtime.InteropServices;

namespace EveryStage.Caster.UI;

internal static class WindowsAppearance
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags, Color, Animation; }
    private const int WcaAccentPolicy = 19, AccentEnableBlurBehind = 3, AccentEnableAcrylicBlurBehind = 4;

    public static void UseDarkTitleBar(Form form)
    {
        form.HandleCreated += (_, _) =>
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        };
    }

    public static void EnableGlass(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        form.HandleCreated += (_, _) => ApplyGlass(form.Handle);
        if (form.IsHandleCreated) ApplyGlass(form.Handle);
    }

    private static void ApplyGlass(IntPtr handle)
    {
        var policy = new AccentPolicy { State = AccentEnableAcrylicBlurBehind, Color = unchecked((int)0xB814263B) };
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = WcaAccentPolicy, Data = memory, Size = Marshal.SizeOf<AccentPolicy>() };
            if (SetWindowCompositionAttribute(handle, ref data) == 0)
            {
                policy.State = AccentEnableBlurBehind;
                Marshal.StructureToPtr(policy, memory, false);
                SetWindowCompositionAttribute(handle, ref data);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
