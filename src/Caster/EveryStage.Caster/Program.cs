using EveryStage.Caster.Discovery;
using EveryStage.Caster.UI;
using EveryStage.Discovery;

namespace EveryStage.Caster;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Keep the compact fixed-layout caster form crisp and correctly sized on Windows 10 at
        // 125%/150% scaling and when it is moved between monitors with different DPI values.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
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

        DeviceIdentity identity;
        PairedTerminalStore pairedTerminals;
        try
        {
            // Bug fixed here: same "runs before Application.Run(), so ThreadException above never
            // sees it, and this process has no AppDomain.UnhandledException handler either" shape as
            // the TerminalDiscoveryClient catch below — just one step earlier. DeviceIdentity.
            // LoadOrCreate's own two write sites (see that class's WriteAtomic) are NOT wrapped in
            // try/catch on the "no file yet" / "corrupt file, mint fresh" paths (Directory.
            // CreateDirectory + the atomic-write pair) — a permissions problem or full disk on
            // ProgramData\EveryStage would throw straight out of here, before either the "内部错误"
            // MessageBox convention or the discovery-socket catch below ever gets a chance to run.
            // PairedTerminalStore's own Load() is already hardened against the same JsonException/
            // IOException/UnauthorizedAccessException cases (see that class), so it's most likely to
            // stay quiet here — wrapped alongside identity purely so one catch covers both of this
            // app's two JSON-file-backed constructions run this early, not because a concrete failure
            // mode is known for it specifically.
            identity = DeviceIdentity.LoadOrCreate("caster");
            pairedTerminals = new PairedTerminalStore();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法读取或创建设备身份/配对记录文件：\n\n{ex.Message}\n\n" +
                "请检查程序是否有权限读写系统的应用数据目录（ProgramData\\EveryStage），或磁盘空间是否充足。",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        TerminalDiscoveryClient discoveryClient;
        try
        {
            discoveryClient = new TerminalDiscoveryClient(identity, pairedTerminals);
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
