namespace EveryStage.Discovery;

/// <summary>
/// Exercises <see cref="DeviceIdentity"/>'s actual on-disk round trip — same role
/// <c>EveryStage.Caster.Discovery.PairedTerminalStoreSelfTest</c> plays for
/// <see cref="EveryStage.Caster.Discovery.PairedTerminalStore"/> (that class isn't referenced
/// directly here since this library doesn't depend on Caster, but the shape of the two self-tests
/// is deliberately identical). Both classes share the exact same JSON-persistence risk profile
/// (fresh-create, reload, corrupt-file fallback, locked-file fallback), and until now
/// <see cref="DeviceIdentity"/> — despite carrying two real bug fixes this same session (the
/// IOException/UnauthorizedAccessException handling in <see cref="DeviceIdentity.LoadOrCreate"/>,
/// and switching both its write sites to the same atomic-write convention every other store in this
/// codebase already uses) — had no self-test of its own verifying any of it actually round-trips
/// correctly. Like <c>PairedTerminalStoreSelfTest</c>, this only needs standard .NET file I/O, no
/// GPU/network/audio hardware — the same caveat that class's own doc comment carries about still
/// never having been executed in this sandbox (no `dotnet` runtime here either) applies here too.
///
/// Uses a fresh per-run temp directory (via <see cref="DeviceIdentity.LoadOrCreate"/>'s own
/// <c>pathOverride</c> parameter, which already existed for exactly this purpose — Terminal/Caster's
/// real identity files live under <see cref="Environment.SpecialFolder.CommonApplicationData"/>,
/// never touched here) and cleans it up in a <c>finally</c> block so a run never leaves files behind,
/// successful or not.
/// </summary>
public static class DeviceIdentitySelfTest
{
    public sealed record Result(bool Success, string? FailureReason);

    public static Result Run()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"everystage-selftest-{Guid.NewGuid():N}");
        string identityPath = Path.Combine(tempDir, "selftest-identity.json");

        try
        {
            // Fresh creation: no file exists yet. LoadOrCreate must mint a real (non-empty) DeviceId
            // and persist it — the file-name-stem argument is irrelevant whenever pathOverride is
            // supplied (see LoadOrCreate's own doc comment), so "selftest" here is just a placeholder.
            var identity1 = DeviceIdentity.LoadOrCreate("selftest", identityPath);
            if (identity1.DeviceId == Guid.Empty)
                return new Result(false, "A freshly-created identity should never have an empty DeviceId.");
            if (!File.Exists(identityPath))
                return new Result(false, "LoadOrCreate should have persisted a new identity to disk, but no file was created.");

            // Reload: a second LoadOrCreate call at the same path must return the SAME identity that
            // was just persisted, not mint an unrelated new one — this is the one thing constructing
            // a single instance and checking its fields can't verify on its own.
            var identity2 = DeviceIdentity.LoadOrCreate("selftest", identityPath);
            if (identity2.DeviceId != identity1.DeviceId)
                return new Result(false, "Reloading an existing identity file should return the same DeviceId, but got a different one.");

            // Rename via Save(): change DeviceName (the one field PLANNING.md §8.2's 设置 panel lets
            // an operator edit after creation — see DeviceIdentity.DeviceName's own doc comment),
            // persist, then reload with a third independent instance to confirm the write actually
            // landed on disk rather than only updating the in-memory object.
            identity2.DeviceName = "自检-改名后的设备";
            identity2.Save();
            var identity3 = DeviceIdentity.LoadOrCreate("selftest", identityPath);
            if (identity3.DeviceId != identity1.DeviceId || identity3.DeviceName != "自检-改名后的设备")
                return new Result(false, "After renaming and saving, reloading should reflect both the same DeviceId and the new DeviceName.");

            // Corrupt-file fallback: overwrite the file with bytes that aren't valid JSON at all.
            // Unlike PairedTerminalStore's equivalent branch, DeviceIdentity.LoadOrCreate's
            // JsonException handling doesn't rename the bad file aside — it mints a fresh identity
            // and immediately overwrites the file with it (see that method's own doc comment on why
            // that's the accepted, deliberate behavior here). This must not throw, and the newly
            // minted identity must genuinely be a NEW DeviceId, not a coincidental reuse of the old one.
            File.WriteAllText(identityPath, "this is not valid JSON { [ ]");
            var identity4 = DeviceIdentity.LoadOrCreate("selftest", identityPath);
            if (identity4.DeviceId == Guid.Empty)
                return new Result(false, "LoadOrCreate against a corrupt file should mint a fresh identity, not an empty one.");
            if (identity4.DeviceId == identity1.DeviceId)
                return new Result(false, "A fresh identity minted after corruption should get a NEW DeviceId, not coincidentally match the original one.");

            // Locked-file fallback (the IOException/UnauthorizedAccessException branch this session
            // added — see this project's README): first restore known-good content, then hold an
            // exclusive lock (FileShare.None) on the file to simulate another process (antivirus
            // scan, backup tool) having it open, and confirm LoadOrCreate degrades to a transient,
            // NOT-persisted identity instead of throwing or somehow reading the locked file's content.
            identity4.DeviceName = "自检-锁定测试";
            identity4.Save();
            long validLength = new FileInfo(identityPath).Length;
            using (new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var identityWhileLocked = DeviceIdentity.LoadOrCreate("selftest", identityPath);
                if (identityWhileLocked.DeviceId == identity4.DeviceId)
                    return new Result(false, "LoadOrCreate while the file is locked should mint a transient identity with a DIFFERENT DeviceId, not somehow read the locked file's real content.");
            }

            // Lock released — the ORIGINAL valid content must still be there, untouched, unlike the
            // corrupt-content case above: this is the assertion that actually distinguishes the two
            // branches (both fail safe to a fresh in-memory identity for THIS call, but only the
            // corrupt-content branch is allowed to have touched the file on disk).
            if (new FileInfo(identityPath).Length != validLength)
                return new Result(false, "The identity file's on-disk content changed as a side effect of loading it while locked — it should have been left completely untouched.");

            var identityAfterUnlock = DeviceIdentity.LoadOrCreate("selftest", identityPath);
            if (identityAfterUnlock.DeviceId != identity4.DeviceId || identityAfterUnlock.DeviceName != "自检-锁定测试")
                return new Result(false, "After the lock was released, the original identity should still load back correctly — the locked-read attempt must not have discarded it.");

            return new Result(true, null);
        }
        catch (Exception ex)
        {
            // Same "a self-test throwing outright is itself a reportable finding" reasoning as every
            // other self-test's catch block in this codebase.
            return new Result(false, $"Self-test threw: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception)
            {
                // Best-effort cleanup only — a leftover temp directory from a failed delete is a
                // minor annoyance in the OS temp folder, not something worth failing this self-test's
                // own result over.
            }
        }
    }
}
