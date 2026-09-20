param(
    [ValidateSet("Terminal", "Caster")][string]$Component,
    [string]$InstallRoot = "$env:ProgramFiles\EveryStage"
)

$ErrorActionPreference = "Stop"
$destination = [System.IO.Path]::GetFullPath((Join-Path $InstallRoot $Component))
$expectedRoot = [System.IO.Path]::GetFullPath($InstallRoot) + [System.IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a path outside the install root: $destination"
}

Remove-ItemProperty -Path "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "EveryStage$Component" -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
Write-Host "$Component removed from $destination. ProgramData content and logs were retained."
