<#
.SYNOPSIS
    Draws the Stratum icon (three stacked layers on a rounded tile) and writes the .ico used by
    the app plus the PNG logos used by the package manifest. Re-run after changing the design.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$appAssets = Join-Path $root 'src\Stratum.App\Assets'
$pkgAssets = Join-Path $root 'packaging\sparse\Assets'
New-Item -ItemType Directory -Force $appAssets, $pkgAssets | Out-Null

function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, $size * 0.04)
    $side = $size - 2 * $pad
    $radius = $side * 0.24
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $tile.AddArc($pad, $pad, $d, $d, 180, 90)
    $tile.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
    $tile.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
    $tile.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
    $tile.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $side, $side
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 56, 132, 255)),
        ([System.Drawing.Color]::FromArgb(255, 104, 82, 232)), 55
    $g.FillPath($fill, $tile)

    # Three layers, each a little narrower and fainter than the one below it.
    $barHeight = $side * 0.13
    $gap = $side * 0.075
    $total = 3 * $barHeight + 2 * $gap
    $top = $pad + ($side - $total) / 2
    $widths = 0.36, 0.50, 0.64
    $alphas = 150, 205, 255
    for ($i = 0; $i -lt 3; $i++) {
        $w = $side * $widths[$i]
        $x = $pad + ($side - $w) / 2
        $y = $top + $i * ($barHeight + $gap)
        $r = $barHeight / 2
        $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bar.AddArc($x, $y, 2 * $r, 2 * $r, 90, 180)
        $bar.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 180)
        $bar.CloseFigure()
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alphas[$i], 255, 255, 255))
        $g.FillPath($brush, $bar)
    }
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# .ico with PNG-compressed entries.
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (Get-PngBytes (New-Tile $s)) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $appAssets 'Stratum.ico'), $ico.ToArray())

(New-Tile 44).Save((Join-Path $pkgAssets 'Square44x44Logo.png'))
(New-Tile 150).Save((Join-Path $pkgAssets 'Square150x150Logo.png'))
(New-Tile 50).Save((Join-Path $pkgAssets 'StoreLogo.png'))
(New-Tile 256).Save((Join-Path $root 'docs\icon.png'))

Write-Host 'Icon assets written.'
