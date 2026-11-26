using System.Runtime.InteropServices;

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

    private static readonly HashSet<(string FileName, string DataRoot)> _migrationFailed = new();
    private static readonly object _migrationFailedGate = new();

    /// <summary>The path for <paramref name="fileName"/> in <see cref="Root"/>, after moving a legacy copy
    /// in. DEV-07: the legacy path is NEVER returned — if the move fails (no permission, file in use)
    /// the secure target is returned anyway and the failure is recorded (<see cref="MigrationFailed"/>),
    /// so callers read nothing and write nothing to the unprotected legacy location.</summary>
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
            }
            // The target is authoritative now: a leftover legacy copy (a delete interrupted
            // earlier) must not linger with secrets outside the secure folder.
            if (File.Exists(target) && File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No secure copy could be established while the legacy file is still there: the
            // caller must treat the secret as unavailable, never fall back to the legacy path.
            if (!File.Exists(target) && File.Exists(legacy)) RecordMigrationFailure(fileName, dataRoot);
        }
        // A target owned by anyone outside the trusted set is not trusted either (same failure
        // record, never a legacy fallback). Best-effort and Windows-only: an unreadable ACL
        // skips the check rather than blocking the migration.
        if (File.Exists(target) && !OwnerIsAllowed(target)) RecordMigrationFailure(fileName, dataRoot);
        return target;
    }

    /// <summary>Whether the secure copy of <paramref name="fileName"/> could not be established
    /// (migration failed, or the file is owned by an untrusted account): callers must surface a
    /// visible failure/read-only alert, never write the secret to the legacy path, and disable the
    /// features depending on it until migration succeeds.</summary>
    public static bool MigrationFailed(string fileName) => MigrationFailed(fileName, DataRoot);

    /// <summary><see cref="MigrationFailed(string)"/> under another data root (self-tests).</summary>
    public static bool MigrationFailed(string fileName, string dataRoot)
    {
        lock (_migrationFailedGate) return _migrationFailed.Contains((fileName, dataRoot));
    }

    private static void RecordMigrationFailure(string fileName, string dataRoot)
    {
        lock (_migrationFailedGate) _migrationFailed.Add((fileName, dataRoot));
    }

    /// <summary>Whether <paramref name="path"/> is owned by the current user, SYSTEM or
    /// Administrators. Windows-only (P/Invoke, so the plain-net8.0 build never breaks on Linux);
    /// anything else — non-Windows, an unreadable ACL — skips the check instead of failing it.
    /// Every native allocation is freed before returning; the owner SID lives inside the security
    /// descriptor, so the descriptor is freed last.</summary>
    private static bool OwnerIsAllowed(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        IntPtr descriptor = IntPtr.Zero, systemSid = IntPtr.Zero, adminSid = IntPtr.Zero, userBuffer = IntPtr.Zero;
        try
        {
            if (GetNamedSecurityInfo(path, SeObjectType.SeFileObject, SecurityInformation.Owner, out IntPtr ownerSid,
                    out _, out _, out _, out descriptor) != 0 || ownerSid == IntPtr.Zero)
                return true;
            systemSid = WellKnownSid("S-1-5-18"); // Local System
            adminSid = WellKnownSid("S-1-5-32-544"); // Administrators
            if (EqualSid(ownerSid, systemSid) || EqualSid(ownerSid, adminSid)) return true;
            // The current user: the TOKEN_USER buffer holds the SID inline, so it stays allocated
            // until after the comparison.
            userBuffer = QueryTokenUserBuffer();
            return EqualSid(ownerSid, Marshal.ReadIntPtr(userBuffer));
        }
        catch (Exception) { return true; }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            if (systemSid != IntPtr.Zero) LocalFree(systemSid);
            if (adminSid != IntPtr.Zero) LocalFree(adminSid);
            if (userBuffer != IntPtr.Zero) Marshal.FreeHGlobal(userBuffer);
        }
    }

    private static IntPtr WellKnownSid(string sidString)
    {
        if (!ConvertStringSidToSid(sidString, out IntPtr sid)) throw new InvalidOperationException("Cannot parse well-known SID.");
        return sid;
    }

    /// <summary>The TOKEN_USER buffer for the calling process (caller frees it with
    /// <see cref="Marshal.FreeHGlobal"/>). Its first field is the user SID pointer, valid while
    /// the buffer lives.</summary>
    private static IntPtr QueryTokenUserBuffer()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAccessLevels.Query, out token) || token == IntPtr.Zero)
                throw new InvalidOperationException("Cannot open process token.");
            GetTokenInformation(token, TokenInformationClass.TokenUser, IntPtr.Zero, 0, out uint needed);
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed));
            if (!GetTokenInformation(token, TokenInformationClass.TokenUser, buffer, needed, out _))
            {
                Marshal.FreeHGlobal(buffer);
                throw new InvalidOperationException("Cannot query token user.");
            }
            return buffer;
        }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    // ── advapi32 / kernel32 interop (Windows only, never invoked elsewhere) ─────────────────────

    private enum SeObjectType : uint { SeFileObject = 1 }

    [Flags]
    private enum SecurityInformation : uint { Owner = 0x00000001 }

    private enum TokenInformationClass : uint { TokenUser = 1 }

    [Flags]
    private enum TokenAccessLevels : uint { Query = 0x0008 }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfo(
        string pObjectName, SeObjectType objectType, SecurityInformation securityInfo,
        out IntPtr ppsidOwner, out IntPtr ppsidGroup, out IntPtr ppDacl, out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr pSid1, IntPtr pSid2);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, TokenAccessLevels desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
