using System.Diagnostics;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 扩展屏向导 (第二十一轮 Q6: keep 投屏开关, add a Win+P guide): explains what the big screen needs — Windows in
/// 「扩展」 mode, not 「复制」 — and offers the same switch as Win+P → 扩展 (DisplaySwitch.exe /extend), the
/// Windows 显示设置 page and the plain Win+P menu.
/// </summary>
internal static class ExtendDisplayWizard
{
    public static void Show(IWin32Window? owner)
    {
        var extend = new TaskDialogButton("切换为「扩展」模式");
        var settings = new TaskDialogButton("打开显示设置");
        var page = new TaskDialogPage
        {
            Caption = "扩展屏向导",
            Heading = "让第二块屏（LED / 投影）成为扩展屏",
            Text = "EveryStage 把节目输出到「扩展」的第二块屏上。如果电脑处于「复制」或「仅电脑屏幕」模式，大屏会显示桌面或无画面。\n\n" +
                   "1. 确认第二块屏已接好并通电。\n" +
                   "2. 按 Win+P，选择「扩展」；或点下面的「切换为扩展模式」，效果相同。\n" +
                   "3. 屏幕闪一下后，EveryStage 会自动认出扩展屏；没有认出时，请在「设置 · 扩展屏」里选择它。\n\n" +
                   "「投屏开关」仍在「设备」页和托盘菜单里，控制是否允许输出到大屏，与这里的显示模式无关。",
            Icon = TaskDialogIcon.Information,
            Buttons = { extend, settings, TaskDialogButton.Close },
            DefaultButton = extend,
        };
        var result = owner == null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        try
        {
            if (result == extend) Process.Start(new ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = true });
            else if (result == settings) Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, $"无法打开：{ex.Message}\n请直接按 Win+P 选择「扩展」。", "扩展屏向导", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
