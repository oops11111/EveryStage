param(
    [string]$DataRoot,
    [string]$BackupRoot,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "EveryStage"
}
else {
    $DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $BackupRoot = Join-Path $DataRoot "B15PairingBackup-$stamp"
}
else {
    $BackupRoot = [System.IO.Path]::GetFullPath($BackupRoot)
}

$targets = @(
    (Join-Path $DataRoot "paired-devices.json"),
    (Join-Path $DataRoot "caster-paired-terminals.json")
)

Write-Host "EveryStage data root: $DataRoot"
Write-Host "Backup root: $BackupRoot"
Write-Host "Mode: $(if ($Apply) { 'apply' } else { 'dry-run' })"
Write-Host ""

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        Write-Host "missing: $target"
        continue
    }

    Write-Host "found: $target"
    if (-not $Apply) {
        Write-Host "  would copy to backup and delete original"
        continue
    }

    New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
    $backup = Join-Path $BackupRoot ([IO.Path]::GetFileName($target))
    Copy-Item -LiteralPath $target -Destination $backup -Force
    Remove-Item -LiteralPath $target -Force
    Write-Host "  backed up: $backup"
    Write-Host "  deleted"
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "Dry run only. Re-run with -Apply to back up and clear these pairing files."
}
