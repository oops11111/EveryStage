param(
    [string]$PackageRoot = "C:\Users\admin\everystage\b15-acceptance-package",
    [string]$OutputRoot = "C:\Users\admin\everystage\b15-acceptance-evidence",
    [string]$MachineRole = "unknown",
    [switch]$SkipSystemInfo
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

function Copy-IfExists([string]$Source, [string]$Destination) {
    if (!(Test-Path -LiteralPath $Source)) {
        return $false
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
    return $true
}

$packageRootFull = Resolve-FullPath $PackageRoot
$outputRootFull = Resolve-FullPath $OutputRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$safeRole = ($MachineRole -replace '[^A-Za-z0-9_.-]', '_')
if ([string]::IsNullOrWhiteSpace($safeRole)) { $safeRole = "unknown" }
$computerName = $env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($computerName)) { $computerName = [Environment]::MachineName }
if ([string]::IsNullOrWhiteSpace($computerName)) { $computerName = "unknown-computer" }
$safeComputerName = ($computerName -replace '[^A-Za-z0-9_.-]', '_')
$evidenceRoot = Join-Path $outputRootFull "$stamp-$safeRole-$safeComputerName"

if (Test-Path -LiteralPath $evidenceRoot) {
    Remove-Item -LiteralPath $evidenceRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

$summary = [ordered]@{
    createdAt = (Get-Date).ToString("o")
    computerName = $computerName
    userName = $env:USERNAME
    machineRole = $MachineRole
    packageRoot = $packageRootFull
    evidenceRoot = $evidenceRoot
}

$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot "summary.json") -Encoding UTF8

if ($SkipSystemInfo) {
    "System info collection skipped by -SkipSystemInfo." |
        Set-Content -LiteralPath (Join-Path $evidenceRoot "system-info.txt") -Encoding UTF8
}
else {
    try {
        $systemInfo = @()
        foreach ($className in @("Win32_ComputerSystem", "Win32_OperatingSystem", "Win32_VideoController")) {
            $systemInfo += "===== $className ====="
            $systemInfo += (Get-CimInstance -ClassName $className | Format-List * | Out-String -Width 240)
        }
        $systemInfo | Set-Content -LiteralPath (Join-Path $evidenceRoot "system-info.txt") -Encoding UTF8
    }
    catch {
        "Failed to collect system info: $($_.Exception.Message)" |
            Set-Content -LiteralPath (Join-Path $evidenceRoot "system-info.txt") -Encoding UTF8
    }
}

$packageOut = Join-Path $evidenceRoot "package"
Copy-IfExists (Join-Path $packageRootFull "manifest.json") $packageOut | Out-Null
Copy-IfExists (Join-Path $packageRootFull "SHA256SUMS.txt") $packageOut | Out-Null
Copy-IfExists (Join-Path $packageRootFull "verification.log") $packageOut | Out-Null
Copy-IfExists (Join-Path $packageRootFull "docs\WINDOWS_ACCEPTANCE.md") $packageOut | Out-Null

$packageVerifySucceeded = $false
$packageVerifyLog = Join-Path $packageOut "package-verify.txt"
$packageVerifier = Join-Path $packageRootFull "scripts\Verify-B15AcceptancePackage.ps1"
if (Test-Path -LiteralPath $packageVerifier -PathType Leaf) {
    try {
        $verifyOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $packageVerifier -PackageRoot $packageRootFull *>&1 |
            Out-String -Width 240
        $verifyExitCode = $LASTEXITCODE
        $verifyOutput | Set-Content -LiteralPath $packageVerifyLog -Encoding UTF8
        if ($verifyExitCode -ne 0) {
            "Verify-B15AcceptancePackage.ps1 exited with code $verifyExitCode." |
                Add-Content -LiteralPath $packageVerifyLog -Encoding UTF8
        }
        else {
            $packageVerifySucceeded = $true
        }
    }
    catch {
        "Failed to run package verifier: $($_.Exception.Message)" |
            Set-Content -LiteralPath $packageVerifyLog -Encoding UTF8
    }
}
else {
    "Package verifier not found: $packageVerifier" |
        Set-Content -LiteralPath $packageVerifyLog -Encoding UTF8
}

$programData = Join-Path $env:ProgramData "EveryStage"
Copy-IfExists (Join-Path $programData "logs") (Join-Path $evidenceRoot "programdata") | Out-Null
Copy-IfExists (Join-Path $programData "paired-devices.json") (Join-Path $evidenceRoot "programdata") | Out-Null
Copy-IfExists (Join-Path $programData "caster-paired-terminals.json") (Join-Path $evidenceRoot "programdata") | Out-Null

$manualEvidence = Join-Path $packageRootFull "manual-evidence"
$extraOut = Join-Path $evidenceRoot "extra"
if (Test-Path -LiteralPath $manualEvidence) {
    Copy-IfExists $manualEvidence $extraOut | Out-Null
}
else {
    New-Item -ItemType Directory -Force -Path $extraOut | Out-Null
    "Place pcaps, screenshots, and videos in <package>\manual-evidence before running this script to include them here." |
        Set-Content -LiteralPath (Join-Path $extraOut "README.txt") -Encoding UTF8
}

$readme = @"
# B-15 Acceptance Evidence

Machine role: $MachineRole
Computer: $computerName
Created: $((Get-Date).ToString("o"))

Contents:

- summary.json: machine and package paths
- system-info.txt: OS, computer, and GPU information
- package\: package manifest, SHA256SUMS, package verification output, B-15 local verification log, acceptance document
- programdata\: EveryStage logs and pairing state files when present
- extra\: copied from <package>\manual-evidence when present; otherwise contains a README placeholder
"@
$readme | Set-Content -LiteralPath (Join-Path $evidenceRoot "README.md") -Encoding UTF8

$zipPath = $evidenceRoot + ".zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $evidenceRoot "*") -DestinationPath $zipPath -Force
if (!(Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "Evidence zip was not created: $zipPath"
}

Write-Host "Evidence: $evidenceRoot"
Write-Host "Zip:      $zipPath"
if (!$packageVerifySucceeded) {
    Write-Error "Package verification failed. Evidence was still collected at: $zipPath"
    exit 1
}
exit 0
