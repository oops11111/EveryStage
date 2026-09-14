using EveryStage.Caster.Discovery;
using EveryStage.Caster.UI;
using EveryStage.Discovery;

namespace EveryStage.Caster;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var identity = DeviceIdentity.LoadOrCreate("caster");

        using var discoveryClient = new TerminalDiscoveryClient();
        discoveryClient.Start();

        using var mainForm = new MainForm(discoveryClient, identity);
        Application.Run(mainForm);
    }
}
