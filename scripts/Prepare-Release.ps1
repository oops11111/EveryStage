# F-29: the fixed release routine, scripted so no step depends on memory.
# Does the machine-checkable parts (version read, strict build, core self-tests, release packages)
# and generates the release record from docs/releases/RELEASE-RECORD-TEMPLATE.md with the facts it
# established filled in. It does NOT publish anything: 真机签字、干净机器全流程、建 GitHub Release
# stay manual and are listed at the end, unchecked, in the generated record.
param(
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw)).Project.PropertyGroup.EveryStageVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw "EveryStageVersion not found in Directory.Build.props." }
$sha = (git -C $repoRoot rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "git rev-parse failed; run inside the repository." }

if (-not $AllowDirty) {
    $dirty = git -C $repoRoot status --porcelain
    if ($dirty) { throw "Working tree is not clean. Commit or stash first, or pass -AllowDirty if this is a rehearsal." }
}

Write-Host "Preparing release $version ($sha)"

# Strict build of every project, same as CI (which enumerates csproj files rather than a solution).
$projects = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -Filter *.csproj | Sort-Object FullName
foreach ($project in $projects) {
    dotnet restore $project.FullName --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $project.FullName --no-restore --configuration Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $SkipTests) {
    dotnet run --project (Join-Path $repoRoot "src/Tests/EveryStage.CoreSelfTests/EveryStage.CoreSelfTests.csproj") --no-build --configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& (Join-Path $repoRoot "scripts/Build-ReleasePackages.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$recordPath = Join-Path $repoRoot "docs/releases/RELEASE-RECORD-$version-$sha.md"
if (Test-Path -LiteralPath $recordPath) { throw "Release record already exists: $recordPath" }
$record = Get-Content -LiteralPath (Join-Path $repoRoot "docs/releases/RELEASE-RECORD-TEMPLATE.md") -Raw
$record = $record.Replace("__VERSION__", $version).Replace("__SHA__", $sha).Replace("__DATE__", (Get-Date).ToString("yyyy-MM-dd"))
Set-Content -LiteralPath $recordPath -Value $record -Encoding utf8

Write-Host ""
Write-Host "Release record created: $recordPath"
Write-Host "Still manual before publishing (fill them into the record, do not skip):"
Write-Host "  1. WindowsSelfTests on a machine with a real GPU and WPS installed; list every skipped case in the record."
Write-Host "  2. scripts/Run-B15LocalVerification.ps1 and the V1 gate items this release claims, signed on real hardware."
Write-Host "  3. Build the installer (scripts/Build-Installer.ps1) and walk a clean machine through install, pair, cast, uninstall."
Write-Host "  4. Create the GitHub Release with a tag matching $version and attach the installer plus its SHA256."
