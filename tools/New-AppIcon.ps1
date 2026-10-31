<#
.SYNOPSIS
    Draws the DiskScope icon (a stylized platter with a scan arc) and writes a PNG-compressed .ico file.
#>
param([Parameter(Mandatory)] [string] $Path, [string] $PngPath)

Add-Type -AssemblyName System.Drawing
$sizes = 256, 64, 48, 32, 16
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 256.0

    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size),
        [System.Drawing.Color]::FromArgb(255, 0x1F, 0x6F, 0xEB), [System.Drawing.Color]::FromArgb(255, 0x0B, 0x3A, 0x7A))
    $radius = 56 * $s
    $rect = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect.AddArc(0, 0, 2 * $radius, 2 * $radius, 180, 90)
    $rect.AddArc($size - 2 * $radius, 0, 2 * $radius, 2 * $radius, 270, 90)
    $rect.AddArc($size - 2 * $radius, $size - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
    $rect.AddArc(0, $size - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
    $g.FillPath($background, $rect)

    $platter = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xE6, 0xED, 0xF3))
    $g.FillEllipse($platter, 40 * $s, 40 * $s, 176 * $s, 176 * $s)
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x9F, 0xB3, 0xC8)), ([Math]::Max(1, 6 * $s))
    $g.DrawEllipse($ring, 72 * $s, 72 * $s, 112 * $s, 112 * $s)
    $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x3F, 0xB9, 0x50)), ([Math]::Max(2, 18 * $s))
    $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
    $g.DrawArc($arc, 56 * $s, 56 * $s, 144 * $s, 144 * $s, -90, 120)
    $hub = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x3A, 0x7A))
    $g.FillEllipse($hub, 112 * $s, 112 * $s, 32 * $s, 32 * $s)
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Flush()
New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
[System.IO.File]::WriteAllBytes($Path, $out.ToArray())
if ($PngPath) { [System.IO.File]::WriteAllBytes($PngPath, $images[0].Bytes) }
