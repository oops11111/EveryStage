# P2 MusicDrawer formal-component integration

Baseline EveryStage drop-prewarm7a40a8d38d6d1d91786e17d5265037b0d4bd7ddf; branch codex/fluidmotion-music-drawer; PR target drop-prewarm. Framework source is fixed MF44401d3f002b427d828500331a3e9b4ada5881cd, PackageId FluidMotion.L1 exact [0.1.0], full-history source checkout. MF repository remains unchanged.

Dependency: accepted P1 component and full/main-post CI. This application stage implements only existing MusicDrawer behavior, not installer/release P3.

Scope: Terminal csproj/MusicDrawer, necessary private motion/preference adapter, actual linked/control tests and isolated screenshot harness, NuGet source mapping, pinned component preparation/identity scripts, additive CI steps and these docs. No casting/media/pairing/Office/network business changes or other animations.

Build once per pipeline: prepare the pinned MF source, run its existing tools/delivery/build_component.py to one fresh output/local feed; use an isolated job NuGet cache and exact package-source mapping (FluidMotion.L1 only local; existing third-party dependency restore preserved). Record source/pack SHA and compare published managed/native/token assets to package members. Preserve that same package for later pipeline P3; do not commit DLL/nupkg or fall back to historical global DLL/cache.

Control behavior: Open/Close/Toggle/Esc and collapse keep prior intent/bitmap whole-sheet rendering/final height/focus behavior. The motion adapter owns one UI-thread system/handle, outer WinForms Timer OnFrame, callback Apply/queue only, from:null reversal, idle pump stop, system preference read-only plus fake tests, guarded teardown. Parent Resize detach/reattach is changed only as needed for this control's lifecycle. No claim of identical180ms timing or performance.

Acceptance actions: open, collapse/Esc, rapid in-flight reverse, reopen after rest, parent Resize, close/Dispose and stale queued/preference events. Assert real target/rest values, native/managed package identities, no cross-thread/reentrant motion calls, handle/pump/event/bitmap cleanup. Tests link actual adapter/control, not copied model logic. Screenshot evidence must show actual control visible states and be labeled harness versus ordinary application.

Commands: pinned component preparation script; existing per-project dotnet restore then Release -warnaserror build; core/windows self-tests and original screenshots/NSIS CI steps retained; new targeted motion project restore/build/run as specified in its README. No audit/WNAE/toolset/TFM workaround from Z6.

Environment hard boundary: user machine has existing ProgramData/ProgramFiles EveryStage. Do not run real Terminal entry, installer, full Windows/Office/network suite or touch existing stores. First audit all harness store/music dependencies. A local harness may only run if every persistence path is fresh explicit temp and no hardcoded ProgramData/network/audio start; otherwise visible-control/ordinary-application evidence runs only on disposable Windows CI. Harness is not ordinary full-app startup. P3 retains actual installed application/installer acceptance if not covered here.

Delivery: meaningful local isolated checks (when safe), all real CI results/logs/owned-window screenshots and exact consumed component manifest, regular English Conventional commits via -F/push and one PR to drop-prewarm. Root reviews code/evidence/all checks, merges only allgreen and follows target-branch post-CI. No installer execution or release sealing in P2.

Stop: source/ref drift, frozen API change needed, inability to isolate user data or real device/account prerequisite, package/source/hash mismatch, unexpected system mutation or code scope expansion. Preserve first failures; report rather than bypass/upgrade/disable tests. Current step: Plan recorded before business implementation.
