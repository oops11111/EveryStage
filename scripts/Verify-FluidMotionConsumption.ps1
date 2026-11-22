param([Parameter(Mandatory = $true)][string[]]$OutputDirectories)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo 'artifacts/fluidmotion'
$state = Get-Content -LiteralPath (Join-Path $root 'component-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$pin = '44401d3f002b427d828500331a3e9b4ada5881cd'
function Sha([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ($state.source_commit -ne $pin -or $state.version -ne '0.1.0') { throw 'Component state does not match fixed source/version' }
$package = Join-Path $repo $state.package_relative
if ((Sha $package) -ne $state.package_sha256) { throw 'Pipeline package bytes changed' }
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $root 'nuget-cache' }
$restored = Join-Path $cache 'fluidmotion.l1/0.1.0/fluidmotion.l1.0.1.0.nupkg'
if ((Sha $restored) -ne $state.package_sha256) { throw 'Application restored a different package than the pipeline feed' }
$assets = Get-Content -LiteralPath (Join-Path $repo 'src/Terminal/EveryStage.Terminal/obj/project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
if ('FluidMotion.L1/0.1.0' -notin $assets.libraries.PSObject.Properties.Name) { throw 'Exact component not present in application restore graph' }
$files = [ordered]@{
    'FluidMotion.L1.dll' = 'lib/net8.0/FluidMotion.L1.dll'
    'fluidmotion.dll' = 'runtimes/win-x64/native/fluidmotion.dll'
    'demo_motion.json' = 'contentFiles/any/net8.0/demo_motion.json'
}
$verified = @()
foreach ($directory in $OutputDirectories) {
    $full = [IO.Path]::GetFullPath((Join-Path $repo $directory))
    if (-not $full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must belong to this isolated application repository' }
    foreach ($name in $files.Keys) {
        $member = @($state.package_members | Where-Object path -EQ $files[$name])
        if ($member.Count -ne 1) { throw ('Missing package-member identity for ' + $name) }
        $path = Join-Path $full $name
        $actual = Sha $path
        if ($actual -ne $member[0].sha256) { throw ('Consumed package asset changed: ' + $path) }
        $verified += [ordered]@{directory=$directory;name=$name;sha256=$actual}
    }
}
$receipt = [ordered]@{source_commit=$pin;package_sha256=$state.package_sha256;restored_package_sha256=Sha $restored;outputs=$verified}
$receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root ('consumption-' + [guid]::NewGuid().ToString('N') + '.json')) -Encoding UTF8
Write-Host 'Exact component package and all managed/native/token output assets verified.'
