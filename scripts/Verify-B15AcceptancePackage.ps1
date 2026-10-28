param(
    [string]$PackageRoot = "C:\Users\admin\everystage\b15-acceptance-package"
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

$packageRootFull = Resolve-FullPath $PackageRoot
$sumsPath = Join-Path $packageRootFull "SHA256SUMS.txt"
$manifestPath = Join-Path $packageRootFull "manifest.json"

if (!(Test-Path -LiteralPath $packageRootFull -PathType Container)) {
    throw "Package root not found: $packageRootFull"
}
if (!(Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
    throw "SHA256SUMS.txt not found: $sumsPath"
}
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "manifest.json not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.packageFileCount) {
    throw "manifest.json is missing packageFileCount."
}

$checked = 0
$failed = 0

Get-Content -LiteralPath $sumsPath | ForEach-Object {
    $line = $_
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    if ($line -notmatch '^([0-9a-fA-F]{64})\s\s(.+)$') {
        Write-Host "FAIL malformed line: $line" -ForegroundColor Red
        $script:failed++
        return
    }

    $expected = $matches[1].ToLowerInvariant()
    $relative = $matches[2]
    $path = Join-Path $packageRootFull $relative
    $checked++

    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "FAIL missing: $relative" -ForegroundColor Red
        $script:failed++
        return
    }

    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Host "FAIL hash mismatch: $relative" -ForegroundColor Red
        Write-Host "  expected $expected"
        Write-Host "  actual   $actual"
        $script:failed++
        return
    }

    Write-Host "OK   $relative"
}

if ($failed -ne 0) {
    Write-Host "B-15 acceptance package verification failed: $failed of $checked files failed." -ForegroundColor Red
    exit 1
}

$actualPackageFileCount = (Get-ChildItem -LiteralPath $packageRootFull -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Measure-Object).Count

if ([int]$manifest.packageFileCount -ne $checked) {
    Write-Host "FAIL manifest packageFileCount mismatch: manifest=$($manifest.packageFileCount), SHA256SUMS=$checked." -ForegroundColor Red
    exit 1
}

if ($actualPackageFileCount -ne $checked) {
    Write-Host "FAIL package file count mismatch: actual=$actualPackageFileCount, SHA256SUMS=$checked. Extra or missing files are present." -ForegroundColor Red
    exit 1
}

Write-Host "B-15 acceptance package verification passed: $checked files."
