using EveryStage.Discovery;

namespace EveryStage.Caster.Discovery;

/// <summary>
/// Exercises <see cref="PairedTerminalStore"/>'s actual on-disk round trip — unlike every other
/// self-test in this project (which need real GPU/network/audio hardware this sandbox doesn't have),
/// this one only needs standard .NET file I/O, which in principle this sandbox could run without
/// Windows at all. NOTE: despite that, this class has still NOT actually been executed here — this
/// repository has no `dotnet` runtime available in this sandbox either (see this project's README),
/// so even a check with no Windows-specific dependency at all has only ever been reasoned through by
/// code review, the same caveat every other self-test/fix in this repository already carries. The
/// difference from the GPU/network-dependent self-tests is narrower than it sounds: this one merely
/// doesn't need a *second* unavailable thing (Windows/hardware) on top of the first (a .NET runtime).
///
/// This project's README "已知风险" flagged <see cref="PairedTerminalStore"/> and Terminal's own
/// <c>Devices.PairedDeviceStore</c> as "两份完全独立、从未互相验证过的JSON持久化实现" where a
/// byte-level bug (e.g. <see cref="File.Replace(string, string, string?)"/>'s behavior when the
/// destination doesn't exist yet) "只有两边都在真机上跑过才可能发现" — that framing undersold how much
/// of this is actually testable without either side's real environment at all: nothing about
/// save-then-reload-and-compare, or corrupt-file-triggers-fallback, requires Windows, a GPU, or a
/// network — only a temp directory. Deliberately does NOT get a symmetric self-test on Terminal's
/// <c>PairedDeviceStore</c> in this same round: Terminal has no self-test UI infrastructure at all
/// (every self-test in this repo lives in <see cref="UI.MainForm"/>, which is Caster-only), and adding
/// one from scratch just to host this one small check would be a much larger, out-of-scope UI change
/// relative to the risk it closes — see this project's README for this asymmetry being a deliberate,
/// documented choice, not an oversight.
///
/// Uses <see cref="Path.GetTempPath"/> plus a fresh per-run <see cref="Guid"/> subdirectory (via
/// <see cref="PairedTerminalStore"/>'s own <c>storePathOverride</c> constructor parameter, added
/// precisely to make this kind of test possible without touching the real
/// <see cref="Environment.SpecialFolder.CommonApplicationData"/> location) rather than the real app
/// data path — never touches anything the real app would read from or write to, and cleans up its own
/// temp directory in a <c>finally</c> block so a run never leaves files behind, successful or not.
/// </summary>
public static class PairedTerminalStoreSelfTest
{
    public sealed record Result(bool Success, string? FailureReason);

    public static Result Run()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"everystage-selftest-{Guid.NewGuid():N}");
        string storePath = Path.Combine(tempDir, "caster-paired-terminals.json");

        try
        {
            var terminalA = new PairedTerminal(Guid.NewGuid(), "自检-TerminalA", DateTimeOffset.UtcNow.AddDays(-1), PairingSecurity.GenerateKey());
            var terminalB = new PairedTerminal(Guid.NewGuid(), "自检-TerminalB", DateTimeOffset.UtcNow, PairingSecurity.GenerateKey());

            // A fresh store at a path that doesn't exist yet: Load() must handle "no file" (return
            // empty, not throw), and Save() must handle "no existing file to File.Replace" (fall back
            // to File.Move — see PairedTerminalStore.Save's own branch for this).
            var store1 = new PairedTerminalStore(storePath);
            if (store1.All.Count != 0)
                return new Result(false, $"A brand-new store (no file yet) should start empty, but All.Count was {store1.All.Count}.");

            store1.Upsert(terminalA);
            store1.Upsert(terminalB);

            // A second, independent PairedTerminalStore instance pointed at the same path — simulates
            // the real scenario this class exists for: the Caster process restarting and reloading
            // whatever the previous run persisted. This is the one thing the two hardcoded stub values
            // above can't verify on their own: that Save() actually wrote something Load() can read
            // back, not just that the in-memory List<PairedTerminal> looks right immediately after Upsert.
            var store2 = new PairedTerminalStore(storePath);
            if (store2.All.Count != 2)
                return new Result(false, $"Expected 2 entries after reloading from disk, got {store2.All.Count}.");
            if (store2.Find(terminalA.DeviceId) is not { } reloadedA || reloadedA != terminalA)
                return new Result(false, "Reloaded entry for terminalA doesn't match what was saved (record equality failed).");
            if (store2.Find(terminalB.DeviceId) is not { } reloadedB || reloadedB != terminalB)
                return new Result(false, "Reloaded entry for terminalB doesn't match what was saved (record equality failed).");

            // Remove, then reload again with a third independent instance — Save()'s File.Replace
            // branch this time (the file already exists), not the File.Move branch store1's very
            // first Upsert exercised above.
            store2.Remove(terminalA.DeviceId);
            var store3 = new PairedTerminalStore(storePath);
            if (store3.All.Count != 1 || store3.Find(terminalA.DeviceId) != null || store3.Find(terminalB.DeviceId) == null)
                return new Result(false, "After removing terminalA and reloading, expected only terminalB to remain.");

            // Corrupt-file fallback (PairedTerminalStore.Load's catch block): overwrite the file with
            // bytes that aren't valid JSON at all, then construct a fresh store over it. This must not
            // throw, must return an empty list rather than propagate the JsonException, and must leave
            // the original bytes recoverable in a renamed ".corrupt-*" sibling file rather than
            // silently deleting them.
            File.WriteAllText(storePath, "this is not valid JSON { [ ]");
            var store4 = new PairedTerminalStore(storePath);
            if (store4.All.Count != 0)
                return new Result(false, $"A store loaded from a corrupt file should start empty, but All.Count was {store4.All.Count}.");

            bool corruptCopyExists = Directory.GetFiles(tempDir, "*.corrupt-*").Length > 0;
            if (!corruptCopyExists)
                return new Result(false, "Loading a corrupt file should have left a renamed '.corrupt-*' copy of it behind, but none was found.");

            // Locked-file fallback (PairedTerminalStore.Load's OTHER catch branch, added in a later
            // round than the JsonException one above — see this project's README on why it needed a
            // separate branch: a locked-but-otherwise-fine file must fail safe WITHOUT the ".corrupt-*"
            // rename the JsonException branch does, since renaming a perfectly good file aside just
            // because it was momentarily unreadable would permanently hide it from every future Load().
            // First restore the file to known-good, valid content (overwriting the corrupt bytes
            // store4 left behind above), then hold an exclusive lock on it (FileShare.None — no other
            // handle, including a read-only one, is allowed while this Stream is open) to simulate
            // another process (antivirus scan, backup tool) having it open, and confirm a
            // PairedTerminalStore constructed against the locked path fails safe to empty rather than
            // throwing IOException out of its constructor.
            store2.Upsert(terminalB); // re-save valid content (terminalB is still what store2 holds in memory).
            long validFileLength = new FileInfo(storePath).Length;
            using (new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var storeWhileLocked = new PairedTerminalStore(storePath);
                if (storeWhileLocked.All.Count != 0)
                    return new Result(false, $"A store loaded while the file is locked should fail safe to empty (this run's own file lock, not the file's own content, is what should trigger the fallback), but All.Count was {storeWhileLocked.All.Count}.");
            }

            // Lock released — the ORIGINAL valid content must still be there, untouched and
            // unrenamed, unlike the JsonException/corrupt-content case above. This is the assertion
            // that actually distinguishes the two branches: both fail safe to an empty in-memory list
            // for THIS load attempt, but only one of them is allowed to have touched the file on disk.
            if (new FileInfo(storePath).Length != validFileLength)
                return new Result(false, "The file's on-disk content changed as a side effect of loading it while locked — it should have been left completely untouched.");
            var storeAfterUnlock = new PairedTerminalStore(storePath);
            if (storeAfterUnlock.All.Count != 1 || storeAfterUnlock.Find(terminalB.DeviceId) == null)
                return new Result(false, "After the lock was released, the original valid content should still load back correctly — the locked-read attempt must not have corrupted or discarded it.");

            return new Result(true, null);
        }
        catch (Exception ex)
        {
            // Same "a self-test throwing outright is itself a reportable finding" reasoning as every
            // other self-test's UI-side catch block in this project — surfaced here as a Result instead
            // so this class stays consistent with using a Result record rather than needing its own
            // caller to also wrap it in try/catch.
            return new Result(false, $"Self-test threw: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception)
            {
                // Best-effort cleanup only — a leftover temp directory from a failed delete is a minor
                // annoyance in the OS temp folder, not something worth failing (or further
                // complicating) this self-test's own result over.
            }
        }
    }
}
