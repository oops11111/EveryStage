# P3 implementation checkpoint

Installer version0.9.2 and finite component selection are implemented. Local validation is limited to PowerShell AST parsing and WindowsPowerShell STA ValidateOnly compilation of UIA/WinForms/native mapping helpers; both passed. No installer or real Terminal entry was executed on the user machine. Installation, upgrade, ordinary installed UI, actual loaded component mapping, tray exit and uninstall remain NOT TESTED until the disposable CI job returns actual evidence.

The build job preserves all prior checks, produces the component once, and uploads a single immutable input artifact. The smoke job downloads those same bytes and the hash-pinned user0.9.1 baseline; it never rebuilds. Runtime/package inventory records the actual Terminal/Caster dependency union, included .NET runtime metadata, available package and shipped notices. Missing embedded notices are disclosed rather than inferred from a license URL. No exhaustive legal completeness claim or product license is invented.

`pipeline-tested-manifest.json` is conditional on smoke success and explicitly pending primary review/postmerge acceptance. P4 final external acceptance must bind the selected postmerge source and exact same tested bytes. Package-internal candidate metadata remains a truthful build-time state; no artifact is rewritten or publicly published.

CI failure evidence is always uploaded. Installed UI samples are qualitative ordinary actions, not precise duration or guaranteed in-flight reversal; P2 actual-control continuity proof remains separate. WM_CLOSE is never used as successful Terminal shutdown. Unavailable tray/interactive desktop, failed normal exit or identity/hash mismatch stops the job without force kill or alternate shutdown path.

## CI attempt1: baseline download stopped before installation

Push run45001799932/job137146130843 built successfully but baseline download returned HTTP401 at2026-11-22T08:19:28Z. No installer ran. The downloader incorrectly referenced `github.token` as a JavaScript Octokit property; this is not the workflow expression context. The scoped step environment now carries the existing workflow token to `process.env.BASELINE_DOWNLOAD_TOKEN`, without logging it or changing permissions, source, hashes or installation steps. Original failed run/logs remain authoritative; the concurrent original PR run was not cancelled.

## CI attempt2: shortcut target assertion stopped before ordinary startup

Push run45002365256/job137147756134 downloaded the fixed baseline successfully, then stopped at Test-Installer.ps1 shortcut target assertion after install/upgrade. Ordinary application UI was not reached. The first assertion did not record raw target values, so the installed shortcut has not been adjudicated wrong. The reader now passes the existing .lnk native absolute path to WScript.Shell and records raw expected/actual plus existing long-path canonical targets; missing/incorrect targets remain hard failures. No NSIS target or installation scope is changed. Background silent installer/invalid-input checks now use WindowStyle Hidden, while ordinary Terminal stays visible; installer call receipts are compressed one-object-per-line JSONL. Original failed runs remain preserved.

## CI attempt3: empty shortcut target, bounded evidence collection

Push run45002953888/job137149544380 baseline and upgraded installers exited0, but the existing Start Menu Terminal shortcut returned an empty COM TargetPath. The strict canonical-target gate stopped before ordinary app startup. The downloaded smoke archive SHA was bd55325a64ba5684391e16696d6e0ca6b4582ccd9c90a2ded41175a3485a4a91; it did not contain original .lnk bytes. Root read the actual expected/raw receipt. Local harmless Chinese-named links returned correct targets under both PowerShell7 and WindowsPowerShell5, with either path separator, so neither shell version nor separators are an established cause.

The next attempt only adds original baseline-before-upgrade and upgraded-before-assertion link byte copies, length/SHA/header flags, and independent read-only COM metadata from both shells. Empty or incorrect targets remain hard failures; no NSIS change or skip is made. Sentinel content hashes now prove exact upgrade/default-uninstall/log retention, rather than existence alone. No user-machine app or installer ran.

## CI attempt4: native reader establishes actual stored targets

PR run45003808125/job137151929696 retained original baseline/upgraded links in artifact14747200609 (archive SHA54ac0e8977f7b6c004ca78af0574676470e4e72ebde6bd66d38043abf9818af4). Root independently parsed their documented Shell Link structures and found the correct stored target. The new metadata-only IShellLinkW/IPersistFile reader returned S_OK and the exact expected target for all five original links; before/after SHA remained equal. An owned harmless positive passed and an invalid link was rejected. See P3_NATIVE_SHORTCUT_PROBE.json.

This establishes a limitation of the previous WScript reader on these observed originals, not an NSIS defect or a general Windows/WSH bug. NSIS bytes/targets are unchanged. Acceptance now uses native raw path plus strict existing-target canonical comparison, retaining WScript empty values as diagnostics. The next ordinary application launch uses the already-verified installed Start Menu shortcut, records link SHA/native target/time and verifies a new held exact executable PID. No direct-executable fallback is permitted. Actual shortcut launch, ordinary UI, tray exit and remaining uninstall gates are still pending CI.

## CI attempt5: ordinary shortcut launch reached real first-run guide

Push run45004589416/job137154524054 passed native shortcut validation and ordinary shortcut launch to a verified new Terminal PID. Its sole installed-closed.png visibly contains the existing first-run `开始使用` welcome modal; it is an occluded initial capture, not unobstructed main-window acceptance. Later foreground and tray-menu assertions stopped. The actual image supports onboarding interference; it does not establish console stealing as the cause.

The bounded helper change dismisses only the held-PID exact source welcome guide through its unique normal OK button, confirms disappearance, then requires enabled main window/foreground for accepted captures. Hidden direct-worker invocation avoids extra visible helper consoles, records separate stdout/stderr, and retains original versus exit errors. Owned-window PrintWindow failure captures are explicitly diagnostic, never acceptance screenshots. Tray button coordinates and bounded owned-menu observations are retained. Temporary uninstaller observation now includes Un.exe with PID/path/creation metadata; no processes are killed. No product UI or shutdown behavior changed.

## Baseline retention preparation

The original baseline artifact expires2026-11-25T02:45:33Z. The workflow now proposes unchanged original ZIP retention in the same repository's Actions artifacts, requesting90 days and recording the actual expiry/digest separately. Only original HTTP404/410 permits selection of an unexpired exact hash-bound mirror name; authentication errors stop. Downloaded mirror wrappers must contain only baseline.zip with the original length, ZIP SHA and sole installer SHA. No public release, repository binary, new credential or retention policy setting is introduced.

Local metadata-only validation exercised the retained-wrapper extraction path using an owned wrapper around the archived original ZIP: exact original ZIP76614... and installer c6219... both passed, without execution. This is not proof of remote retention; an actual uploaded mirror must still be downloaded and verified before acceptance. Actions retention remains finite, with explicit stop if no verified source remains; local original archive is retained for offline handoff.
