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

        // Same root-cause gap this session's Terminal-side fixes found and closed there (see
        // EveryStage.Discovery's DiscoveryProtocol.Decode and Terminal's own Program.cs for the full
        // reasoning) — this app had no top-level protection against a UI event handler throwing
        // (default .NET behavior: crash the whole process) or a fire-and-forget Task's exception
        // vanishing unobserved. Scoped down from Terminal's version deliberately: this app is
        // actively operated by a person watching the screen (unlike the Terminal, which PLANNING.md
        // frames as unattended), and has no existing persistent-log infrastructure at all — inventing
        // one just for this would be a bigger addition than the risk justifies. A MessageBox the
        // operator sees immediately, letting the app keep running afterward rather than crashing an
        // active cast over one bad click, gets nearly all the same value here.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"发生了未预期的错误，但程序会尝试继续运行：\n\n{e.Exception.Message}",
                "内部错误", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // No MessageBox here on purpose: this fires on whatever thread triggers GC finalization of a
        // faulted, never-awaited Task — not necessarily the UI thread, so showing UI from it would
        // risk the exact cross-thread-control-access problem this whole file is careful to avoid
        // elsewhere (see MainForm's own BeginInvoke-marshaling comments). Honestly, SetObserved()
        // alone doesn't record anything either — this app has no log to write it to (see above) — its
        // only real effect is suppressing the "second chance" exception .NET raises for an unobserved
        // fault; the actual value here is future-proofing (if a new fire-and-forget Task with an
        // unguarded exception gets introduced later, at least it can't compound into anything worse
        // than what it already is) rather than fixing a currently-known gap the way the Terminal-side
        // CrashLogger does.
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        var identity = DeviceIdentity.LoadOrCreate("caster");
        var pairedTerminals = new PairedTerminalStore();

        TerminalDiscoveryClient discoveryClient;
        try
        {
            discoveryClient = new TerminalDiscoveryClient();
        }
        catch (Exception ex)
        {
            // Bug fixed here: binding the discovery UDP socket to its fixed port
            // (DiscoveryProtocol.Port) can fail with SocketException if that port is already in use
            // — a real scenario, not hypothetical: a stale Caster process that didn't shut down
            // cleanly still holding it, or two Caster instances launched on the same machine (this
            // project's own EveryStage.Discovery README already flags exactly that kind of
            // same-machine collision as something worth testing for). This happens before
            // Application.Run() even starts the message loop, so Application.ThreadException
            // (registered above) never sees it — and unlike the Terminal side, this process has no
            // AppDomain.UnhandledException handler either (see this method's own comment above on
            // why a persistent crash log wasn't added here), so without this catch the operator
            // would see nothing but a raw, unstyled .NET crash dialog with no indication of what
            // actually went wrong, instead of this app's own established "MessageBox, not a crash"
            // convention every other unexpected failure in this process already gets.
            MessageBox.Show(
                $"无法启动设备发现（监听UDP端口{DiscoveryProtocol.Port}失败）：\n\n{ex.Message}\n\n" +
                "可能是另一个EveryStage投屏机实例正在运行，或者该端口被其他程序占用。",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (discoveryClient)
        {
            discoveryClient.Start();

            using var mainForm = new MainForm(discoveryClient, identity, pairedTerminals);
            Application.Run(mainForm);
        }
    }
}
