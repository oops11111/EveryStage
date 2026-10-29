namespace EveryStage.Terminal.Data;

/// <summary>
/// %ProgramData%\EveryStage, where every Terminal store (library, scenarios, settings, paired devices,
/// logs, conversion cache) lives. Windows' default ProgramData permissions let a standard user create
/// files there but only change files they own, so files first written by another account — or by
/// EveryStage running elevated, e.g. started from the installer's finish page — later fail with
/// "Access to the path is denied". The installer grants local Users Modify on this folder; these helpers
/// detect the problem and tell the operator how to fix it.
/// </summary>
public static class DataDirectory
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EveryStage");

    public const string PermissionHint =
        "EveryStage 的数据目录（%ProgramData%\\EveryStage）中有文件属于其他账户或管理员，当前账户无法修改。" +
        "常见原因：安装完成后直接从安装程序启动了 EveryStage，或曾以管理员身份运行。\n" +
        "修复：重新运行 EveryStage 安装程序（会自动修复权限）；或在管理员命令提示符中执行：\n" +
        "icacls \"%ProgramData%\\EveryStage\" /grant *S-1-5-32-545:(OI)(CI)M /T";

    /// <summary><paramref name="ex"/>'s message, plus <see cref="PermissionHint"/> when it is an access
    /// denial inside the data directory.</summary>
    public static string Describe(Exception ex) =>
        IsPermissionProblem(ex) ? ex.Message + "\n\n" + PermissionHint : ex.Message;

    public static bool IsPermissionProblem(Exception ex) =>
        ex is UnauthorizedAccessException
        && ex.Message.Contains(Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first path in the data directory this account cannot write (the folder itself, or a
    /// top-level store file), or null when everything is writable or does not exist yet.</summary>
    public static string? FindUnwritable()
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            string probe = Path.Combine(Root, $".write-probe-{Environment.ProcessId}");
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException) { return Root; }
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (UnauthorizedAccessException) { return file; }
                catch (IOException) { } // in use elsewhere, not a permission problem
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
