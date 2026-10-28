param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    # Optional: also write every frame as PNG plus a light/dark contact sheet, for review.
    [string]$PreviewDirectory
)

# Draws the EveryStage app icon (dark navy rounded tile; cyan/blue stacked panels with a play screen)
# as vectors at every icon size and writes a multi-resolution .ico for Terminal and Caster.
# Geometry is traced from the reference artwork's 1254 x 1254 canvas and mapped onto the tile.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$Cyan = [System.Drawing.Color]::FromArgb(0, 250, 254)
$Blue = [System.Drawing.Color]::FromArgb(0, 121, 254)
$Play = [System.Drawing.Color]::White
$Tile = [System.Drawing.Color]::FromArgb(0, 23, 76)
$TileBorder = $null   # the navy reference has no outline

# Reference-canvas geometry. Each shape is a list of @(x, y, cornerRadius).
$RefTile = @{ Left = 104.0; Top = 123.0; Right = 1150.5; Bottom = 1137.5; Radius = 230.0 }
$GlyphCenter = @(630.5, 568.0)   # center of the artwork bounding box
$Shapes = @(
    @{ Color = $Cyan; Points = @(@(280.5, 395.4, 22), @(373.5, 418.6, 0), @(373.5, 820.6, 0), @(280.5, 795.4, 22)) },          # thin back panel
    @{ Color = $Blue; Points = @(@(397.5, 320.8, 22), @(512.5, 355.0, 0), @(512.5, 855.3, 0), @(397.5, 826.0, 22)) },          # blue panel
    @{ Color = $Cyan; Points = @(@(512.5, 857.6, 0), @(512.5, 278.0, 32), @(880.5, 391.3, 16), @(880.5, 436.5, 0),              # L frame
                                  @(558.5, 357.3, 24), @(558.5, 826.0, 6)) },
    @{ Color = $Cyan; Points = @(@(592.5, 412.2, 24), @(980.5, 503.4, 22), @(980.5, 792.0, 22), @(592.5, 852.2, 22)) },        # front screen
    @{ Color = $Play; Points = @(@(709.5, 527.7, 20), @(890.0, 641.6, 20), @(709.5, 749.8, 20)) }                             # play
)

function New-RoundedPolygon($points, [double]$scale, [double]$dx, [double]$dy) {
    $pts = foreach ($p in $points) { [pscustomobject]@{ X = $p[0] * $scale + $dx; Y = $p[1] * $scale + $dy; R = $p[2] * $scale } }
    $n = $pts.Count
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corners = for ($i = 0; $i -lt $n; $i++) {
        $v = $pts[$i]; $prev = $pts[($i + $n - 1) % $n]; $next = $pts[($i + 1) % $n]
        $ax = $prev.X - $v.X; $ay = $prev.Y - $v.Y; $la = [Math]::Sqrt($ax * $ax + $ay * $ay); $ax /= $la; $ay /= $la
        $bx = $next.X - $v.X; $by = $next.Y - $v.Y; $lb = [Math]::Sqrt($bx * $bx + $by * $by); $bx /= $lb; $by /= $lb
        if ($v.R -le 0) { [pscustomobject]@{ A = [System.Drawing.PointF]::new($v.X, $v.Y); B = $null; C1 = $null; C2 = $null }; continue }
        $theta = [Math]::Acos([Math]::Max(-1.0, [Math]::Min(1.0, $ax * $bx + $ay * $by)))
        $t = [Math]::Min($v.R / [Math]::Tan($theta / 2), [Math]::Min($la, $lb) / 2)
        $r = $t * [Math]::Tan($theta / 2)
        $h = 4.0 / 3.0 * [Math]::Tan(([Math]::PI - $theta) / 4) * $r   # cubic handle for a circular arc
        [pscustomobject]@{
            A = [System.Drawing.PointF]::new($v.X + $ax * $t, $v.Y + $ay * $t)
            B = [System.Drawing.PointF]::new($v.X + $bx * $t, $v.Y + $by * $t)
            C1 = [System.Drawing.PointF]::new($v.X + $ax * ($t - $h), $v.Y + $ay * ($t - $h))
            C2 = [System.Drawing.PointF]::new($v.X + $bx * ($t - $h), $v.Y + $by * ($t - $h))
        }
    }
    for ($i = 0; $i -lt $n; $i++) {
        $c = $corners[$i]; $nextStart = $corners[($i + 1) % $n].A
        $end = if ($c.B) { $path.AddBezier($c.A, $c.C1, $c.C2, $c.B); $c.B } else { $c.A }
        $path.AddLine($end, $nextStart)
    }
    $path.CloseFigure()
    $path
}

function New-IconFrame([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Square tile with a small margin; the artwork keeps its position relative to the tile center.
    $margin = $size / 32.0
    $side = $size - 2 * $margin
    $refSide = (($RefTile.Right - $RefTile.Left) + ($RefTile.Bottom - $RefTile.Top)) / 2
    $scale = $side / $refSide
    $dx = $size / 2.0 - ($RefTile.Left + $RefTile.Right) / 2 * $scale
    $dy = $size / 2.0 - ($RefTile.Top + $RefTile.Bottom) / 2 * $scale
    # Small frames enlarge the artwork around its own center so the panels stay legible.
    $boost = if ($size -le 20) { 1.25 } elseif ($size -le 24) { 1.22 } elseif ($size -le 32) { 1.16 } elseif ($size -le 48) { 1.08 } else { 1.0 }
    $glyphScale = $scale * $boost
    $glyphDx = ($GlyphCenter[0] * $scale + $dx) - $GlyphCenter[0] * $glyphScale
    $glyphDy = ($GlyphCenter[1] * $scale + $dy) - $GlyphCenter[1] * $glyphScale

    $radius = $RefTile.Radius / $refSide * $side
    $tilePoints = @(@($margin, $margin, $radius), @(($size - $margin), $margin, $radius),
                    @(($size - $margin), ($size - $margin), $radius), @($margin, ($size - $margin), $radius))
    $tilePath = New-RoundedPolygon $tilePoints 1.0 0 0
    $tileBrush = New-Object System.Drawing.SolidBrush $Tile
    $g.FillPath($tileBrush, $tilePath)
    if ($TileBorder) {
        $borderPen = New-Object System.Drawing.Pen $TileBorder, ([Math]::Max(1.0, $size / 256.0))
        $borderPen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
        $g.DrawPath($borderPen, $tilePath)
        $borderPen.Dispose()
    }

    foreach ($shape in $Shapes) {
        $path = New-RoundedPolygon $shape.Points $glyphScale $glyphDx $glyphDy
        $brush = New-Object System.Drawing.SolidBrush $shape.Color
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }
    $tileBrush.Dispose(); $tilePath.Dispose(); $g.Dispose()
    $bitmap
}

# 32-bit BMP (DIB + AND mask) frames for <= 128 px, PNG for 256 px — the layout Windows expects.
function Get-FrameBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    $writer = New-Object System.IO.BinaryWriter $stream
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $data = $bitmap.LockBits((New-Object System.Drawing.Rectangle 0, 0, $size, $size),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($size * 4)
        for ($y = $size - 1; $y -ge 0; $y--) {
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
            $writer.Write($row)
        }
    }
    finally { $bitmap.UnlockBits($data) }
    $writer.Write((New-Object byte[] ($maskStride * $size)))   # alpha channel is authoritative
    $writer.Flush()
    , $stream.ToArray()
}

function Write-Ico([string]$path, $frames) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $payloads = foreach ($frame in $frames) { , (Get-FrameBytes $frame) }
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        for ($i = 0; $i -lt $frames.Count; $i++) {
            $size = $frames[$i].Width
            $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([int16]1); $writer.Write([int16]32)
            $writer.Write([int]$payloads[$i].Length); $writer.Write([int]$offset)
            $offset += $payloads[$i].Length
        }
        foreach ($payload in $payloads) { $writer.Write($payload) }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

$frames = foreach ($size in $Sizes) { New-IconFrame $size }
try {
    foreach ($target in "src/Terminal/EveryStage.Terminal/Assets/EveryStage.ico", "src/Caster/EveryStage.Caster/Assets/EveryStage.ico") {
        Write-Ico (Join-Path $RepositoryRoot $target) $frames
    }
    if ($PreviewDirectory) {
        New-Item -ItemType Directory -Force -Path $PreviewDirectory | Out-Null
        foreach ($frame in $frames) { $frame.Save((Join-Path $PreviewDirectory "icon_$($frame.Width).png"), [System.Drawing.Imaging.ImageFormat]::Png) }
        # Contact sheet: every size up to 64 px at 1:1 and 4x (nearest neighbour), plus 256 px,
        # on a dark (taskbar) band and a light (Explorer) band.
        $sheet = New-Object System.Drawing.Bitmap 1500, 1000
        $g = [System.Drawing.Graphics]::FromImage($sheet)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        foreach ($band in @(@{ Top = 0; Color = [System.Drawing.Color]::FromArgb(32, 32, 32) }, @{ Top = 500; Color = [System.Drawing.Color]::FromArgb(243, 243, 243) })) {
            $brush = New-Object System.Drawing.SolidBrush $band.Color
            $g.FillRectangle($brush, 0, $band.Top, 1500, 500); $brush.Dispose()
            $x = 20
            foreach ($frame in $frames | Where-Object { $_.Width -le 64 }) {
                $g.DrawImage($frame, $x, $band.Top + 20, $frame.Width, $frame.Height)
                $g.DrawImage($frame, $x, $band.Top + 110, $frame.Width * 4, $frame.Height * 4)
                $x += [Math]::Max($frame.Width * 4, 40) + 20
            }
            $g.DrawImage(($frames | Where-Object { $_.Width -eq 256 }), 1230, $band.Top + 20, 256, 256)
        }
        $g.Dispose()
        $sheet.Save((Join-Path $PreviewDirectory "contact_sheet.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $sheet.Dispose()
    }
}
finally { foreach ($frame in $frames) { $frame.Dispose() } }
