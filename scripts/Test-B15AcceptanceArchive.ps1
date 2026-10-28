param(
    [string]$ZipPath = "C:\Users\admin\everystage\b15-acceptance-package.zip",
    [string]$ExtractRoot = ""
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

$zipFull = Resolve-FullPath $ZipPath
$shaPath = "$zipFull.sha256"

if (!(Test-Path -LiteralPath $zipFull -PathType Leaf)) {
    throw "Zip package not found: $zipFull"
}
if (!(Test-Path -LiteralPath $shaPath -PathType Leaf)) {
    throw "Zip SHA sidecar not found: $shaPath"
}

$sidecar = (Get-Content -LiteralPath $shaPath -Raw).Trim()
if ($sidecar -notmatch '^([0-9a-fA-F]{64})\s\s(.+)$') {
    throw "Malformed SHA sidecar: $shaPath"
}

$expected = $matches[1].ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    throw "Zip SHA mismatch. Expected $expected, actual $actual."
}
Write-Host "OK   zip SHA256: $([System.IO.Path]::GetFileName($zipFull))"

if ([string]::IsNullOrWhiteSpace($ExtractRoot)) {
    $ExtractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("b15-acceptance-package-" + [Guid]::NewGuid().ToString("N"))
}
$extractRootFull = Resolve-FullPath $ExtractRoot
if (Test-Path -LiteralPath $extractRootFull) {
    Remove-Item -LiteralPath $extractRootFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $extractRootFull | Out-Null

Expand-Archive -LiteralPath $zipFull -DestinationPath $extractRootFull -Force
$verifier = Join-Path $extractRootFull "scripts\Verify-B15AcceptancePackage.ps1"
if (!(Test-Path -LiteralPath $verifier -PathType Leaf)) {
    throw "Package verifier not found after extraction: $verifier"
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $verifier -PackageRoot $extractRootFull
if ($LASTEXITCODE -ne 0) {
    throw "Inner package verification failed with exit code $LASTEXITCODE."
}

Write-Host "B-15 acceptance archive verification passed."
Write-Host "Extracted package: $extractRootFull"
