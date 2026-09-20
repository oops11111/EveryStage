param(
    [ValidateSet("Terminal", "Caster")][string]$Component,
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = "$env:ProgramFiles\EveryStage",
    [switch]$NoAutoStart,
    [switch]$WpsLicenseConfirmed
)

$ErrorActionPreference = "Stop"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this installer from an elevated PowerShell session." }

$desktopRuntime = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 8\.' }
if (-not $desktopRuntime) { throw "Microsoft .NET 8 Windows Desktop Runtime x64 is required." }

$sourceName = "EveryStage-$Component-win-x64"
$source = Join-Path $PackageRoot $sourceName
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Package directory not found: $source" }

if ($Component -eq "Terminal") {
    $wpsProgIds = @("KWPS.Application", "KET.Application", "KWPP.Application")
    $missing = @($wpsProgIds | Where-Object { -not (Test-Path -LiteralPath "Registry::HKEY_CLASSES_ROOT\$_\CLSID") })
    if ($missing.Count -gt 0) { throw "WPS COM components are missing: $($missing -join ', ')" }
    if (-not $WpsLicenseConfirmed) {
        throw "Confirm a valid per-device WPS license, then rerun with -WpsLicenseConfirmed."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $source "x64/pdfium.dll") -PathType Leaf)) {
        throw "The Terminal package is missing x64/pdfium.dll."
    }
}

$destination = Join-Path $InstallRoot $Component
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force

$exe = Join-Path $destination "EveryStage.$Component.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Installed executable not found: $exe" }

$runKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "EveryStage$Component"
if ($NoAutoStart) {
    Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
} else {
    New-ItemProperty -Path $runKey -Name $runName -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
}

[ordered]@{
    component = $Component
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    installPath = $destination
    autoStart = -not $NoAutoStart
    wpsLicenseConfirmed = ($Component -ne "Terminal") -or [bool]$WpsLicenseConfirmed
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination "deployment-status.json") -Encoding utf8

Write-Host "$Component installed to $destination"
