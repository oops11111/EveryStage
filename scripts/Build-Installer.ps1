# Builds the EveryStage Windows installer (NSIS) on Windows.
# Requires: .NET 8 SDK, NSIS 3 (https://nsis.sourceforge.io, or: choco install nsis).
# Output: artifacts/installer/EveryStage-Setup-<Version>-win-x64.exe
param([string]$Version = "0.9.0")

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = Join-Path $repoRoot "artifacts/installer"
$publish = Join-Path $outputRoot "publish"
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$build = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
if (-not $build) { $build = "dev" }

# Self-contained: the installer carries the .NET runtime, so target machines need no separate .NET 8.
foreach ($component in @("Terminal", "Caster")) {
    dotnet publish (Join-Path $repoRoot "src/$component/EveryStage.$component/EveryStage.$component.csproj") `
        --configuration Release --runtime win-x64 --self-contained true --nologo --output (Join-Path $publish $component)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish "Terminal/x64/pdfium.dll") -PathType Leaf)) {
    throw "Terminal publish is missing x64/pdfium.dll."
}

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $makensis) { throw "makensis not found. Install NSIS 3 (choco install nsis)." }

$setup = Join-Path $outputRoot "EveryStage-Setup-$Version-win-x64.exe"
& $makensis -V2 "-DPUBLISH_DIR=$publish" "-DVERSION=$Version" "-DBUILD=$build" "-DOUTFILE=$setup" `
    (Join-Path $repoRoot "deployment/installer/EveryStage.nsi")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Installer: $setup"
