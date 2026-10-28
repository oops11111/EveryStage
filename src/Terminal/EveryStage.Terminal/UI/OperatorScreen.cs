using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.UI;

internal static class OperatorScreen
{
    public static void PlaceOnOperatorScreen(Form form, Form? owner, MonitorInfo? extendedDisplay)
    {
        Screen screen;
        if (owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized)
        {
            screen = Screen.FromControl(owner);
        }
        else
        {
            screen = Screen.PrimaryScreen ?? Screen.AllScreens.First();
            if (extendedDisplay != null && SameScreen(screen, extendedDisplay))
            {
                screen = Screen.AllScreens.FirstOrDefault(candidate => !SameScreen(candidate, extendedDisplay)) ?? screen;
            }
        }

        form.StartPosition = FormStartPosition.Manual;
        Rectangle area = screen.WorkingArea;
        form.Location = new Point(
            area.Left + Math.Max(0, (area.Width - form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - form.Height) / 2));
    }

    private static bool SameScreen(Screen screen, MonitorInfo monitor) =>
        screen.DeviceName == monitor.DeviceName || screen.Bounds == monitor.Bounds;
}
