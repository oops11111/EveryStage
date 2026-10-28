param(
    [string]$OutputRoot = "artifacts/verify-b15-local",
    [string]$TranscriptPath,
    [switch]$NoRestore,
    [switch]$SkipCoreSelfTests,
    [switch]$SkipWindowsSelfTests,
    [switch]$SkipStrictBuild,
    [switch]$SkipUiSnapshot
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRootFull = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $TranscriptPath = Join-Path $outputRootFull "verification.log"
}
else {
    $TranscriptPath = if ([System.IO.Path]::IsPathRooted($TranscriptPath)) {
        [System.IO.Path]::GetFullPath($TranscriptPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $TranscriptPath))
    }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TranscriptPath) | Out-Null
Set-Content -LiteralPath $TranscriptPath -Value @(
    "B-15 local verification log"
    "Start time: $((Get-Date).ToString("o"))"
    "Working directory: $repoRoot"
    "Output root: $outputRootFull"
    ""
) -Encoding UTF8

function Write-Log {
    param([string]$Message = "")
    Write-Host $Message
    Add-Content -LiteralPath $TranscriptPath -Value $Message -Encoding UTF8
}

Write-Log "B-15 local verification log: $TranscriptPath"

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Body
    )

    Write-Log ""
    Write-Log "== $Name =="
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function DotnetArgsWithRestoreFlag {
    param([string[]]$InputArgs)
    if (-not $NoRestore) { return $InputArgs }

    $separator = [Array]::IndexOf($InputArgs, "--")
    if ($separator -ge 0) {
        return @($InputArgs[0..($separator - 1)] + "--no-restore" + $InputArgs[$separator..($InputArgs.Count - 1)])
    }

    return @($InputArgs + "--no-restore")
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & dotnet @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        Add-Content -LiteralPath $TranscriptPath -Value $line -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipCoreSelfTests) {
    $coreOut = Join-Path $outputRootFull "CoreSelfTests"
    Invoke-Step "CoreSelfTests" {
        $args = DotnetArgsWithRestoreFlag -InputArgs @(
            "run",
            "--project", (Join-Path $repoRoot "src/Tests/EveryStage.CoreSelfTests/EveryStage.CoreSelfTests.csproj"),
            "-c", "Release",
            "-p:OutDir=$coreOut"
        )
        Invoke-Dotnet -Arguments $args
    }
}

if (-not $SkipWindowsSelfTests) {
    $windowsOut = Join-Path $outputRootFull "WindowsSelfTests"
    Invoke-Step "WindowsSelfTests" {
        $args = DotnetArgsWithRestoreFlag -InputArgs @(
            "run",
            "--project", (Join-Path $repoRoot "src/Tests/EveryStage.WindowsSelfTests/EveryStage.WindowsSelfTests.csproj"),
            "-c", "Release",
            "-p:OutDir=$windowsOut"
        )
        Invoke-Dotnet -Arguments $args
    }
}

if (-not $SkipStrictBuild) {
    $projects = @(
        "src/Shared/EveryStage.Discovery/EveryStage.Discovery.csproj",
        "src/Shared/EveryStage.Transport/EveryStage.Transport.csproj",
        "src/Shared/EveryStage.Rendering/EveryStage.Rendering.csproj",
        "src/Terminal/EveryStage.Terminal/EveryStage.Terminal.csproj",
        "src/Caster/EveryStage.Caster/EveryStage.Caster.csproj",
        "src/Tests/EveryStage.CoreSelfTests/EveryStage.CoreSelfTests.csproj",
        "src/Tests/EveryStage.WindowsSelfTests/EveryStage.WindowsSelfTests.csproj",
        "src/Poc/ZeroCopyRenderDemo/ZeroCopyRenderDemo.csproj",
        "src/Poc/WpsComInteropSpike/WpsComInteropSpike.csproj"
    )

    foreach ($project in $projects) {
        $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
        $projectOut = Join-Path $outputRootFull $projectName
        Invoke-Step "Strict build: $projectName" {
            $args = DotnetArgsWithRestoreFlag -InputArgs @(
                "build",
                (Join-Path $repoRoot $project),
                "-c", "Release",
                "-warnaserror",
                "-p:OutDir=$projectOut"
            )
            Invoke-Dotnet -Arguments $args
        }
    }
}

if (-not $SkipUiSnapshot) {
    $snapshotOut = Join-Path $outputRootFull "ui_snapshot"
    $snapshotBin = Join-Path $outputRootFull "ui_snapshot_bin\"
    New-Item -ItemType Directory -Force -Path $snapshotOut | Out-Null

    foreach ($mode in @("waiting", "preview", "program")) {
        Invoke-Step "ui_snapshot $mode" {
            $oldSmoke = $env:SMOKE
            $oldDeviceCast = $env:DEVICE_CAST
            try {
                $env:SMOKE = "1"
                $env:DEVICE_CAST = $mode
                $args = DotnetArgsWithRestoreFlag -InputArgs @(
                    "run",
                    "--project", (Join-Path $repoRoot "artifacts/ui_snapshot/ui_snapshot.csproj"),
                    "-c", "Release",
                    "-p:OutDir=$snapshotBin",
                    "--",
                    $snapshotOut,
                    $mode,
                    "960",
                    "720"
                )
                Invoke-Dotnet -Arguments $args
            }
            finally {
                if ($null -eq $oldSmoke) { Remove-Item Env:SMOKE -ErrorAction SilentlyContinue } else { $env:SMOKE = $oldSmoke }
                if ($null -eq $oldDeviceCast) { Remove-Item Env:DEVICE_CAST -ErrorAction SilentlyContinue } else { $env:DEVICE_CAST = $oldDeviceCast }
            }
        }
    }
}

Write-Log ""
Write-Log "B-15 local verification finished. Output: $outputRootFull"
Write-Log "End time: $((Get-Date).ToString("o"))"
