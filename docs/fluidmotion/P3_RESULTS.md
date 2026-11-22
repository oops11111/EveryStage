# P3 implementation checkpoint

Installer version0.9.2 and finite component selection are implemented. Local validation is limited to PowerShell AST parsing and WindowsPowerShell STA ValidateOnly compilation of UIA/WinForms/native mapping helpers; both passed. No installer or real Terminal entry was executed on the user machine. Installation, upgrade, ordinary installed UI, actual loaded component mapping, tray exit and uninstall remain NOT TESTED until the disposable CI job returns actual evidence.

The build job preserves all prior checks, produces the component once, and uploads a single immutable input artifact. The smoke job downloads those same bytes and the hash-pinned user0.9.1 baseline; it never rebuilds. Runtime/package inventory records the actual Terminal/Caster dependency union, included .NET runtime metadata, available package and shipped notices. Missing embedded notices are disclosed rather than inferred from a license URL. No exhaustive legal completeness claim or product license is invented.

`pipeline-tested-manifest.json` is conditional on smoke success and explicitly pending primary review/postmerge acceptance. P4 final external acceptance must bind the selected postmerge source and exact same tested bytes. Package-internal candidate metadata remains a truthful build-time state; no artifact is rewritten or publicly published.

CI failure evidence is always uploaded. Installed UI samples are qualitative ordinary actions, not precise duration or guaranteed in-flight reversal; P2 actual-control continuity proof remains separate. WM_CLOSE is never used as successful Terminal shutdown. Unavailable tray/interactive desktop, failed normal exit or identity/hash mismatch stops the job without force kill or alternate shutdown path.
