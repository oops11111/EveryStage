# S3a readonly verifier compatibility continuation

Dependency: accepted product merge9d0438120038274eac776278ca3ed4aa766c6d0c and post45024807010. Product source/run/installer/package remain fixed to that accepted build, not this tooling CI's rebuilt products. Primary released this bounded continuation after actual offline WinPS5 validation found top-level JSON array wrapping.

Scope only scripts/Verify-PresentationDelivery.ps1 Read helper, actual helper fixture scripts/Test-DeliveryVerifier.ps1, new S3a evidence/results/docs. No application version, installer, API, Office, product rebuild or system operation change. Dedicated branch/one PR to drop-prewarm; primary reviews and merges after all applicable CI, then tooling post CI.

Fix explicit enumeration of each parsed top-level JSON array item in the actual Read helper, preserving JSON objects unchanged. Link the real helper into WinPS5/pwsh fixture and assert two distinct loaded-module objects; do not duplicate implementation in the test. Preserve existing8 file/path checks and every strict runtime evidence assertion.

Validation: actual WinPS5/pwsh shared fixture, PowerShell parser and full staged/range diff check. Failed bundle55830cf6 and verify-first.stdout/stderr remain unchanged. After tooling merge/post green, assemble a NEW bundle from original product run450248 and accepted tooling verifier; manifest and final root acceptance bind separate product source/run and tooling commit/PR/CI. Run actual complete VERIFY -AllowPending, then root final seal/default verifier. Stop on any new error, preserve evidence, no bypass or replacement of tested product bytes.
