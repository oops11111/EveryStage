using System.Diagnostics;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The ONLY code path in EveryStage that hands a media file to Windows' default application
/// ("使用系统默认程序打开"). It is reachable solely from explicit operator commands — the file context
/// menu and the Preview error card — never from normal playback: previewing, playing and taking to
/// the screen all stay inside EveryStage's own Preview/Program engines.
///
/// <see cref="InvocationCount"/> and <see cref="TestHook"/> let the self-tests prove that playback
/// scenarios never reach here.
/// </summary>
public static class ExternalOpener
{
    private static int _invocationCount;

    /// <summary>Times an external open was requested since process start.</summary>
    public static int InvocationCount => Volatile.Read(ref _invocationCount);

    /// <summary>When set (tests only), called instead of launching anything.</summary>
    public static Func<string, bool>? TestHook { get; set; }

    /// <summary>Explicitly opens <paramref name="path"/> with the application Windows associates with it.
    /// Shows a message and returns false if the file is missing or nothing can open it.</summary>
    public static bool OpenWithDefaultApp(IWin32Window? owner, string path)
    {
        Interlocked.Increment(ref _invocationCount);
        if (TestHook is { } hook) return hook(path);
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在或已被移动。", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"无法使用系统默认程序打开该文件：\n\n{ex.Message}", "打开失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
