param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeIconCleanup {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@

function New-EveryStageIcon([string]$Path) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap 256, 256
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Rectangle 18, 18, 220, 220),
        [System.Drawing.Color]::FromArgb(31, 101, 235),
        [System.Drawing.Color]::FromArgb(41, 181, 255), 45)
    $graphicsPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $graphicsPath.AddArc(18, 18, 64, 64, 180, 90)
    $graphicsPath.AddArc(174, 18, 64, 64, 270, 90)
    $graphicsPath.AddArc(174, 174, 64, 64, 0, 90)
    $graphicsPath.AddArc(18, 174, 64, 64, 90, 90)
    $graphicsPath.CloseFigure()
    $graphics.FillPath($background, $graphicsPath)

    $glass = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 225, 242, 255))
    $upper = [System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point 57, 73),
        (New-Object System.Drawing.Point 176, 45),
        (New-Object System.Drawing.Point 176, 104),
        (New-Object System.Drawing.Point 57, 132))
    $lower = [System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point 80, 126),
        (New-Object System.Drawing.Point 199, 98),
        (New-Object System.Drawing.Point 199, 157),
        (New-Object System.Drawing.Point 80, 185))
    $graphics.FillPolygon($glass, $upper)
    $graphics.FillPolygon($glass, $lower)

    $play = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 9, 34, 66))
    $triangle = [System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point 112, 91),
        (New-Object System.Drawing.Point 112, 165),
        (New-Object System.Drawing.Point 169, 128))
    $graphics.FillPolygon($play, $triangle)

    $handle = $bitmap.GetHicon()
    try {
        $icon = [System.Drawing.Icon]::FromHandle($handle)
        $stream = [System.IO.File]::Create($Path)
        try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose() }
    }
    finally {
        [NativeIconCleanup]::DestroyIcon($handle) | Out-Null
        $play.Dispose(); $glass.Dispose(); $graphicsPath.Dispose(); $background.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}

New-EveryStageIcon (Join-Path $RepositoryRoot "src/Terminal/EveryStage.Terminal/Assets/EveryStage.ico")
New-EveryStageIcon (Join-Path $RepositoryRoot "src/Caster/EveryStage.Caster/Assets/EveryStage.ico")
