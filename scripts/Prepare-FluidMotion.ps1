# Build one exact pinned framework candidate for this application's pipeline.
param([string]$FrameworkSourcePath, [string]$ResumeBuildManifest)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pin = '44401d3f002b427d828500331a3e9b4ada5881cd'
$root = Join-Path $repo 'artifacts/fluidmotion'
$statePath = Join-Path $root 'component-state.json'
$feed = Join-Path $root 'feed'
$cache = Join-Path $root 'nuget-cache'
function Sha([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $package = Join-Path $repo $state.package_relative
    if ($state.source_commit -ne $pin -or (Sha $package) -ne $state.package_sha256) {
        throw 'Existing pipeline package provenance changed; do not silently rebuild or replace it'
    }
    $manifestPath = Join-Path $repo $state.framework_manifest_relative
    if ((Sha $manifestPath) -ne $state.framework_manifest_sha256) { throw 'Framework manifest bytes changed' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.source_commit -ne $pin -or $manifest.source_dirty -or -not $manifest.status.StartsWith('PASS')) {
        throw 'Stored framework manifest is not the accepted clean source build'
    }
    Write-Host 'Reusing the same verified pipeline package (not building a second copy).'
} else {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    if (-not $ResumeBuildManifest) {
    if (-not $FrameworkSourcePath) {
        $FrameworkSourcePath = Join-Path ([IO.Path]::GetTempPath()) ('EveryStage-fluidmotion-source-' + [guid]::NewGuid().ToString('N'))
    }
    $sourceFull = [IO.Path]::GetFullPath($FrameworkSourcePath)
    if ($sourceFull.StartsWith($repo.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Framework source must be outside the application tree to avoid inherited Directory.Build/NuGet configuration'
    }
    if (-not (Test-Path -LiteralPath $FrameworkSourcePath)) {
        git clone --no-hardlinks --no-checkout https://github.com/oops11111/motion_framework.git $FrameworkSourcePath
        if ($LASTEXITCODE) { throw 'Framework clone failed' }
        git -C $FrameworkSourcePath checkout --detach $pin
        if ($LASTEXITCODE) { throw 'Framework pin checkout failed' }
    }
    $actual = (git -C $FrameworkSourcePath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $actual -ne $pin) { throw 'Framework source is not the accepted exact commit' }
    if (git -C $FrameworkSourcePath status --porcelain) { throw 'Framework source is dirty' }
    git -C $FrameworkSourcePath cat-file -e '11b3378fb20498675165f0b52275aa8d23ec2ccd^{commit}'
    if ($LASTEXITCODE) { throw 'Full framework Git history is required (fetch-depth: 0)' }
    $attemptId = [guid]::NewGuid().ToString('N')
    $build = Join-Path $root ('build-' + $attemptId)
    $stdout = Join-Path $root ('component-build-' + $attemptId + '.stdout.log')
    $stderr = Join-Path $root ('component-build-' + $attemptId + '.stderr.log')
    $process = Start-Process -FilePath python -ArgumentList @(
        ('"' + (Join-Path $FrameworkSourcePath 'tools/delivery/build_component.py') + '"'),
        '--output', ('"' + $build + '"')
    ) -WorkingDirectory $FrameworkSourcePath -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.WaitForExit()
    $process.Refresh()
    if ($process.ExitCode) { throw ('Pinned component build failed; original logs preserved in ' + $root) }
    $manifestPath = Join-Path $build 'result.json'
    } else {
        $manifestPath = [IO.Path]::GetFullPath($ResumeBuildManifest)
        if (-not $manifestPath.StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Resume is restricted to this pipeline artifacts/fluidmotion build result'
        }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.source_commit -ne $pin -or $manifest.source_dirty -or -not $manifest.status.StartsWith('PASS') -or -not $manifest.input_hashes_POST_equal -or -not $manifest.source_commit_POST_equal) {
        throw 'Framework build is not a clean committed successful candidate'
    }
    if (Test-Path -LiteralPath $feed) { throw 'Feed exists without a provenance state; inspect rather than overwrite' }
    New-Item -ItemType Directory -Path $feed | Out-Null
    $package = Join-Path $feed 'FluidMotion.L1.0.1.0.nupkg'
    Copy-Item -LiteralPath $manifest.package.path -Destination $package
    if ((Sha $package) -ne $manifest.package.sha256) { throw 'Copied component package hash mismatch' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw 'Invalid component nuspec set' }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($spec.package.metadata.id -ne 'FluidMotion.L1' -or $spec.package.metadata.version -ne '0.1.0' -or $spec.package.metadata.repository.commit -ne $pin) {
            throw 'Component ID/version/source commit was polluted or changed'
        }
    } finally { $zip.Dispose() }
    $relative = [IO.Path]::GetRelativePath($repo, $package).Replace('\', '/')
    $state = [ordered]@{
        source_repository = 'oops11111/motion_framework'
        source_commit = $pin
        package_id = 'FluidMotion.L1'
        version = '0.1.0'
        exact_version_range = '[0.1.0]'
        candidate = $true
        package_relative = $relative
        package_sha256 = Sha $package
        package_members = $manifest.package_members
        framework_manifest_relative = [IO.Path]::GetRelativePath($repo, $manifestPath).Replace('\', '/')
        framework_manifest_sha256 = Sha $manifestPath
    }
    $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $statePath -Encoding UTF8
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$env:NUGET_PACKAGES = $cache
if ($env:GITHUB_ENV) { 'NUGET_PACKAGES=' + $cache | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8 }
Write-Host ('NUGET_PACKAGES=' + $cache)
Write-Host ('COMPONENT_STATE=' + $statePath)
