param(
    [string]$VerificationRoot = "C:\Users\admin\everystage\verify-b15-operator-full",
    [string]$OutputRoot = "C:\Users\admin\everystage\b15-acceptance-package",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

function Copy-Directory([string]$Source, [string]$Destination) {
    if (!(Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required directory not found: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Copy-RequiredFile([string]$Source, [string]$DestinationDirectory) {
    if (!(Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file not found: $Source"
    }
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Copy-Item -LiteralPath $Source -Destination $DestinationDirectory -Force
}

function Get-PackageRelativePath([string]$Root, [string]$FullPath) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($FullPath)
    if (!$pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File is outside package root: $FullPath"
    }
    return $pathFull.Substring($rootFull.Length)
}

$repoRoot = Resolve-FullPath (Join-Path $PSScriptRoot "..")
$verificationRootFull = Resolve-FullPath $VerificationRoot
$outputRootFull = Resolve-FullPath $OutputRoot

$terminalSource = Join-Path $verificationRootFull "EveryStage.Terminal"
$casterSource = Join-Path $verificationRootFull "EveryStage.Caster"
$verificationLog = Join-Path $verificationRootFull "verification.log"

if (!(Test-Path -LiteralPath (Join-Path $terminalSource "EveryStage.Terminal.exe") -PathType Leaf)) {
    throw "Terminal build is missing from verification root: $terminalSource"
}
if (!(Test-Path -LiteralPath (Join-Path $casterSource "EveryStage.Caster.exe") -PathType Leaf)) {
    throw "Caster build is missing from verification root: $casterSource"
}
if (!(Test-Path -LiteralPath $verificationLog -PathType Leaf)) {
    throw "Verification log is missing: $verificationLog"
}

if (Test-Path -LiteralPath $outputRootFull) {
    Remove-Item -LiteralPath $outputRootFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $outputRootFull "manual-evidence") | Out-Null
@"
Place field evidence here before running scripts\Collect-B15AcceptanceEvidence.ps1:

- Wireshark .pcapng files
- Screenshots
- Screen recordings
- Any notes exported from the acceptance run
"@ | Set-Content -LiteralPath (Join-Path $outputRootFull "manual-evidence\README.txt") -Encoding UTF8

Copy-Directory $terminalSource (Join-Path $outputRootFull "Terminal")
Copy-Directory $casterSource (Join-Path $outputRootFull "Caster")

$docsOut = Join-Path $outputRootFull "docs"
Copy-RequiredFile (Join-Path $repoRoot "docs\WINDOWS_ACCEPTANCE.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\AUDIT-REMEDIATION.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\B15-ACCEPTANCE-RESULTS.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\B15-FIELD-HANDOFF.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\PREVIEW-PROGRAM.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\REMAINING_TASKS.md") $docsOut
Copy-RequiredFile (Join-Path $repoRoot "docs\EVERYSTAGE-AUDIT-STATE-MEMORY-DRAFT.md") $docsOut

$scriptsOut = Join-Path $outputRootFull "scripts"
Copy-RequiredFile (Join-Path $repoRoot "scripts\Reset-B15PairingState.ps1") $scriptsOut
Copy-RequiredFile (Join-Path $repoRoot "scripts\Run-B15LocalVerification.ps1") $scriptsOut
Copy-RequiredFile (Join-Path $repoRoot "scripts\Prepare-B15AcceptancePackage.ps1") $scriptsOut
Copy-RequiredFile (Join-Path $repoRoot "scripts\Verify-B15AcceptancePackage.ps1") $scriptsOut
Copy-RequiredFile (Join-Path $repoRoot "scripts\Collect-B15AcceptanceEvidence.ps1") $scriptsOut
Copy-RequiredFile (Join-Path $repoRoot "scripts\Test-B15AcceptanceArchive.ps1") $scriptsOut

Copy-RequiredFile $verificationLog $outputRootFull

$head = ""
$dirty = $null
try {
    $head = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
    $dirty = (& git -C $repoRoot status --porcelain 2>$null | Measure-Object).Count
}
catch {
    $head = ""
    $dirty = $null
}

$manifest = [ordered]@{
    package = "EveryStage B-15 acceptance package"
    createdAt = (Get-Date).ToString("o")
    repository = $repoRoot
    gitHead = $head
    gitDirtyEntryCount = $dirty
    verificationRoot = $verificationRootFull
    verificationLog = "verification.log"
    terminalExe = "Terminal\EveryStage.Terminal.exe"
    casterExe = "Caster\EveryStage.Caster.exe"
    resetPairingScript = "scripts\Reset-B15PairingState.ps1"
    fieldHandoffDocument = "docs\B15-FIELD-HANDOFF.md"
    acceptanceDocument = "docs\WINDOWS_ACCEPTANCE.md"
    acceptanceResultSheet = "docs\B15-ACCEPTANCE-RESULTS.md"
    evidenceCollectionScript = "scripts\Collect-B15AcceptanceEvidence.ps1"
    packageVerificationScript = "scripts\Verify-B15AcceptancePackage.ps1"
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRootFull "manifest.json") -Encoding UTF8

$readmeLines = @(
    '# EveryStage B-15 Acceptance Package',
    '',
    'Generated from:',
    '',
    ('- Local verification root: ' + $verificationRootFull),
    '- Verification log: verification.log',
    '- Terminal executable: Terminal\EveryStage.Terminal.exe',
    '- Caster executable: Caster\EveryStage.Caster.exe',
    '',
    '## 1. Verify the zip before extraction',
    '',
    'After copying b15-acceptance-package.zip, b15-acceptance-package.zip.sha256, and b15-acceptance-package.verify.ps1, run:',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\admin\everystage\b15-acceptance-package.verify.ps1 -ZipPath C:\Users\admin\everystage\b15-acceptance-package.zip',
    '```',
    '',
    'Pass condition: the output includes `OK zip SHA256` and `B-15 acceptance package verification passed`.',
    '',
    '## 2. Verify the extracted package directory',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Verify-B15AcceptancePackage.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package',
    '```',
    '',
    'Pass condition: every file is reported as `OK`.',
    '',
    '## 3. Deploy and run',
    '',
    '1. Copy Terminal\ to the Terminal machine and run EveryStage.Terminal.exe.',
    '2. Copy Caster\ to the Caster machine and run EveryStage.Caster.exe.',
    '3. On both machines, first dry-run pairing-state cleanup:',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Reset-B15PairingState.ps1',
    '```',
    '',
    'After confirming the listed targets are correct, run:',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Reset-B15PairingState.ps1 -Apply',
    '```',
    '',
    '## 4. Run B-15 acceptance',
    '',
    'Start with docs\B15-FIELD-HANDOFF.md, then follow the B-15 table in docs\WINDOWS_ACCEPTANCE.md:',
    '',
    '- First-pairing six-digit comparison',
    '- UDP 47990 packet capture: pair_response must not contain a plaintext key',
    '- Paired reconnect',
    '- Device preview, take to Program, and revert',
    '- Second-device confirmation',
    '- Same-device reconnect',
    '- Cast-switch boundary behavior',
    '- Operator-screen dialog placement',
    '- Old-version compatibility',
    '',
    'Fill docs\B15-ACCEPTANCE-RESULTS.md as the final archived result table.',
    '',
    '## 5. Collect field evidence',
    '',
    'Place manual evidence such as .pcapng files, screenshots, and recordings in manual-evidence\.',
    '',
    'On the Terminal machine:',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Collect-B15AcceptanceEvidence.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package -MachineRole terminal',
    '```',
    '',
    'On the Caster machine:',
    '',
    '```powershell',
    'powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Collect-B15AcceptanceEvidence.ps1 -PackageRoot C:\Users\admin\everystage\b15-acceptance-package -MachineRole caster',
    '```',
    '',
    'If machine policy blocks system-info collection, add `-SkipSystemInfo`.',
    '',
    'The evidence zip contains:',
    '',
    '- package manifest / SHA256SUMS / package-verify.txt',
    '- local system information',
    '- EveryStage ProgramData logs and pairing-state files',
    '- pcaps, screenshots, and recordings from manual-evidence\'
)
$readmeLines | Set-Content -LiteralPath (Join-Path $outputRootFull "README.md") -Encoding UTF8

$manifest.packageFileCount = (Get-ChildItem -LiteralPath $outputRootFull -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Measure-Object).Count
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRootFull "manifest.json") -Encoding UTF8

$hashLines = Get-ChildItem -LiteralPath $outputRootFull -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = Get-PackageRelativePath $outputRootFull $_.FullName
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$hashLines | Set-Content -LiteralPath (Join-Path $outputRootFull "SHA256SUMS.txt") -Encoding ASCII

if (!$NoZip) {
    $zipPath = $outputRootFull.TrimEnd('\', '/') + ".zip"
    $zipHashPath = $zipPath + ".sha256"
    $zipVerifierPath = $outputRootFull.TrimEnd('\', '/') + ".verify.ps1"
    $zipReadmePath = $outputRootFull.TrimEnd('\', '/') + ".README.txt"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath $zipHashPath) {
        Remove-Item -LiteralPath $zipHashPath -Force
    }
    if (Test-Path -LiteralPath $zipVerifierPath) {
        Remove-Item -LiteralPath $zipVerifierPath -Force
    }
    if (Test-Path -LiteralPath $zipReadmePath) {
        Remove-Item -LiteralPath $zipReadmePath -Force
    }
    Compress-Archive -Path (Join-Path $outputRootFull "*") -DestinationPath $zipPath -Force
    if (!(Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Zip package was not created: $zipPath"
    }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $([System.IO.Path]::GetFileName($zipPath))" |
        Set-Content -LiteralPath $zipHashPath -Encoding ASCII
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\Test-B15AcceptanceArchive.ps1") -Destination $zipVerifierPath -Force
    @(
        "EveryStage B-15 acceptance package",
        "",
        "Files to distribute together:",
        "- b15-acceptance-package.zip",
        "- b15-acceptance-package.zip.sha256",
        "- b15-acceptance-package.verify.ps1",
        "",
        "Before extraction, verify the archive:",
        "powershell -NoProfile -ExecutionPolicy Bypass -File .\b15-acceptance-package.verify.ps1 -ZipPath .\b15-acceptance-package.zip",
        "",
        "After extraction, read README.md and docs\WINDOWS_ACCEPTANCE.md.",
        "Start field work from docs\B15-FIELD-HANDOFF.md.",
        "Use docs\B15-ACCEPTANCE-RESULTS.md as the field result sheet."
    ) | Set-Content -LiteralPath $zipReadmePath -Encoding ASCII
    Write-Host "Package: $outputRootFull"
    Write-Host "Zip:     $zipPath"
    Write-Host "Zip SHA: $zipHashPath"
    Write-Host "Verify:  $zipVerifierPath"
    Write-Host "Readme:  $zipReadmePath"
}
else {
    Write-Host "Package: $outputRootFull"
}
