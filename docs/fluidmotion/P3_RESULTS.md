# P3 implementation checkpoint

Installer version0.9.2 and finite component selection are implemented. Local validation is limited to PowerShell AST parsing and WindowsPowerShell STA ValidateOnly compilation of UIA/WinForms/native mapping helpers; both passed. No installer or real Terminal entry was executed on the user machine. Installation, upgrade, ordinary installed UI, actual loaded component mapping, tray exit and uninstall remain NOT TESTED until the disposable CI job returns actual evidence.

The build job preserves all prior checks, produces the component once, and uploads a single immutable input artifact. The smoke job downloads those same bytes and the hash-pinned user0.9.1 baseline; it never rebuilds. Runtime/package inventory records the actual Terminal/Caster dependency union, included .NET runtime metadata, available package and shipped notices. Missing embedded notices are disclosed rather than inferred from a license URL. No exhaustive legal completeness claim or product license is invented.

`pipeline-tested-manifest.json` is conditional on smoke success and explicitly pending primary review/postmerge acceptance. P4 final external acceptance must bind the selected postmerge source and exact same tested bytes. Package-internal candidate metadata remains a truthful build-time state; no artifact is rewritten or publicly published.

CI failure evidence is always uploaded. Installed UI samples are qualitative ordinary actions, not precise duration or guaranteed in-flight reversal; P2 actual-control continuity proof remains separate. WM_CLOSE is never used as successful Terminal shutdown. Unavailable tray/interactive desktop, failed normal exit or identity/hash mismatch stops the job without force kill or alternate shutdown path.

## CI attempt1: baseline download stopped before installation

Push run45001799932/job137146130843 built successfully but baseline download returned HTTP401 at2026-11-22T08:19:28Z. No installer ran. The downloader incorrectly referenced `github.token` as a JavaScript Octokit property; this is not the workflow expression context. The scoped step environment now carries the existing workflow token to `process.env.BASELINE_DOWNLOAD_TOKEN`, without logging it or changing permissions, source, hashes or installation steps. Original failed run/logs remain authoritative; the concurrent original PR run was not cancelled.

## CI attempt2: shortcut target assertion stopped before ordinary startup

Push run45002365256/job137147756134 downloaded the fixed baseline successfully, then stopped at Test-Installer.ps1 shortcut target assertion after install/upgrade. Ordinary application UI was not reached. The first assertion did not record raw target values, so the installed shortcut has not been adjudicated wrong. The reader now passes the existing .lnk native absolute path to WScript.Shell and records raw expected/actual plus existing long-path canonical targets; missing/incorrect targets remain hard failures. No NSIS target or installation scope is changed. Background silent installer/invalid-input checks now use WindowStyle Hidden, while ordinary Terminal stays visible; installer call receipts are compressed one-object-per-line JSONL. Original failed runs remain preserved.
