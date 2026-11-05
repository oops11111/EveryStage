namespace EveryStage.Discovery;

/// <summary>
/// Where pairing keys and device identities live: %ProgramData%\EveryStage\secure. The installer gives
/// that folder its own ACL (SYSTEM, Administrators and each file's owner only; local Users may list it
/// and create files in it but not read other accounts' files), unlike the rest of %ProgramData%\EveryStage,
/// which local Users may modify. Files from an older build still at the top level are moved in on first
/// use: copied, so the copy takes the folder's ACL, then the old file is deleted.
/// </summary>
public static class SecureDataPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EveryStage");

    public static string Root { get; } = Path.Combine(DataRoot, "secure");

    /// <summary>The path for <paramref name="fileName"/> in <see cref="Root"/>, after moving a legacy copy
    /// in. If the move fails (no permission, file in use) the legacy path is returned so nothing is lost.</summary>
    public static string Resolve(string fileName) => Resolve(fileName, DataRoot);

    /// <summary><see cref="Resolve(string)"/> under another data root (self-tests).</summary>
    public static string Resolve(string fileName, string dataRoot)
    {
        string secure = Path.Combine(dataRoot, "secure");
        string target = Path.Combine(secure, fileName);
        string legacy = Path.Combine(dataRoot, fileName);
        try
        {
            Directory.CreateDirectory(secure);
            if (!File.Exists(target) && File.Exists(legacy))
            {
                string temp = target + ".migrating";
                File.WriteAllBytes(temp, File.ReadAllBytes(legacy));
                File.Move(temp, target);
                File.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(target) && File.Exists(legacy)) return legacy;
        }
        return target;
    }
}
