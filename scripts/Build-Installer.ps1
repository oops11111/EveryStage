# Builds the EveryStage Windows installer (NSIS) on Windows.
# Requires: .NET 8 SDK, NSIS 3 (https://nsis.sourceforge.io, or: choco install nsis).
# Output: artifacts/installer/EveryStage-Setup-<Version>-win-x64.exe
# The version is read from Directory.Build.props (the single version source); it is not a parameter.
# This installer is the only package meant for users; the ZIP/PS1 release packages are DEV-ONLY.

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = Join-Path $repoRoot "artifacts/installer"
$publish = Join-Path $outputRoot "publish"
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$Version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw)).Project.PropertyGroup.EveryStageVersion
if (-not $Version) { throw "Directory.Build.props has no EveryStageVersion." }
$commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
if (-not $commit) { $commit = "dev" }
$build = if ($commit -eq "dev") { "dev" } else { $commit.Substring(0, 7) }

# Self-contained: the installer carries the .NET runtime, so target machines need no separate .NET 8.
foreach ($component in @("Terminal", "Caster")) {
    dotnet publish (Join-Path $repoRoot "src/$component/EveryStage.$component/EveryStage.$component.csproj") `
        --configuration Release --runtime win-x64 --self-contained true --nologo "-p:SourceRevisionId=$commit" --output (Join-Path $publish $component)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish "Terminal/x64/pdfium.dll") -PathType Leaf)) {
    throw "Terminal publish is missing x64/pdfium.dll."
}

# The published executables must carry exactly this version (+ commit): the about page and
# 程序和功能 then agree.
foreach ($exe in @("Terminal/EveryStage.Terminal.exe", "Caster/EveryStage.Caster.exe")) {
    $info = (Get-Item -LiteralPath (Join-Path $publish $exe)).VersionInfo
    if ($info.FileVersion -ne "$Version.0" -or -not $info.ProductVersion.StartsWith("$Version+$commit")) {
        throw "$exe has version $($info.FileVersion) / $($info.ProductVersion), expected $Version+$commit."
    }
}

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $makensis) { throw "makensis not found. Install NSIS 3 (choco install nsis)." }

$setup = Join-Path $outputRoot "EveryStage-Setup-$Version-win-x64.exe"
& $makensis -V2 "-DPUBLISH_DIR=$publish" "-DVERSION=$Version" "-DBUILD=$build" "-DOUTFILE=$setup" "-DUNSIGNED=1" `
    (Join-Path $repoRoot "deployment/installer/EveryStage.nsi")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Installer: $setup"
