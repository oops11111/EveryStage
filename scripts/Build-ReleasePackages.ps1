param(
    [string]$OutputDirectory = "artifacts/release"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$terminalDir = Join-Path $outputRoot "EveryStage-Terminal-win-x64"
$casterDir = Join-Path $outputRoot "EveryStage-Caster-win-x64"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($path in @($terminalDir, $casterDir)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

dotnet publish (Join-Path $repoRoot "src/Terminal/EveryStage.Terminal/EveryStage.Terminal.csproj") `
    --configuration Release --runtime win-x64 --no-restore --no-self-contained --nologo --output $terminalDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish (Join-Path $repoRoot "src/Caster/EveryStage.Caster/EveryStage.Caster.csproj") `
    --configuration Release --runtime win-x64 --no-restore --no-self-contained --nologo --output $casterDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pdfium = Join-Path $terminalDir "x64/pdfium.dll"
if (-not (Test-Path -LiteralPath $pdfium -PathType Leaf)) {
    throw "Terminal package is missing x64/pdfium.dll. PDF rendering would fail after deployment."
}

$deploymentDir = Join-Path $outputRoot "deployment"
if (Test-Path -LiteralPath $deploymentDir) { Remove-Item -LiteralPath $deploymentDir -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $repoRoot "deployment") -Destination $deploymentDir -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot "docs/DEPLOYMENT.md") -Destination (Join-Path $outputRoot "DEPLOYMENT.md")

$manifest = [ordered]@{
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    runtime = "win-x64"
    frameworkDependent = $true
    requiredDotNetRuntime = "Microsoft.WindowsDesktop.App 8.x x64"
    terminal = [ordered]@{
        executable = "EveryStage-Terminal-win-x64/EveryStage.Terminal.exe"
        pdfium = "EveryStage-Terminal-win-x64/x64/pdfium.dll"
        pdfiumSha256 = (Get-FileHash -LiteralPath $pdfium -Algorithm SHA256).Hash
        wpsRequired = $true
    }
    caster = [ordered]@{ executable = "EveryStage-Caster-win-x64/EveryStage.Caster.exe" }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot "manifest.json") -Encoding utf8

foreach ($name in @("EveryStage-Terminal-win-x64", "EveryStage-Caster-win-x64")) {
    $zipPath = Join-Path $outputRoot "$name.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath (Join-Path $outputRoot $name) -DestinationPath $zipPath -CompressionLevel Optimal
}

$bundlePath = Join-Path $outputRoot "EveryStage-DeploymentBundle-win-x64.zip"
if (Test-Path -LiteralPath $bundlePath) { Remove-Item -LiteralPath $bundlePath -Force }
Compress-Archive -LiteralPath @(
    $terminalDir,
    $casterDir,
    $deploymentDir,
    (Join-Path $outputRoot "DEPLOYMENT.md"),
    (Join-Path $outputRoot "manifest.json")
) -DestinationPath $bundlePath -CompressionLevel Optimal

Write-Host "Release packages created in $outputRoot"
