# Draws the ChromaBridge icon (a ring through the color wheel) and writes it as a multi-size
# .ico file. Run with Windows PowerShell: powershell.exe -File tools\make-icon.ps1
# This is placeholder artwork until the project has a designed icon.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\ChromaBridge.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RingBitmap([int]$size) {
    # Draw at four times the size and scale down for smooth edges
    $scale = 4
    $big = New-Object System.Drawing.Bitmap ($size * $scale), ($size * $scale), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $full = $size * $scale
    $thickness = $full / 4.6
    $inset = $thickness / 2 + $full * 0.04
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($full - 2 * $inset), ($full - 2 * $inset)

    $segments = 72
    for ($i = 0; $i -lt $segments; $i++) {
        $hue = $i * 360.0 / $segments
        $sector = $hue / 60.0
        $x = [int][Math]::Round(255 * (1 - [Math]::Abs(($sector % 2) - 1)))
        switch ([int][Math]::Floor($sector)) {
            0 { $c = [System.Drawing.Color]::FromArgb(255, $x, 0) }
            1 { $c = [System.Drawing.Color]::FromArgb($x, 255, 0) }
            2 { $c = [System.Drawing.Color]::FromArgb(0, 255, $x) }
            3 { $c = [System.Drawing.Color]::FromArgb(0, $x, 255) }
            4 { $c = [System.Drawing.Color]::FromArgb($x, 0, 255) }
            default { $c = [System.Drawing.Color]::FromArgb(255, 0, $x) }
        }
        $pen = New-Object System.Drawing.Pen $c, $thickness
        $g.DrawArc($pen, $rect, ($i * 360.0 / $segments - 90), (360.0 / $segments + 1.2))
        $pen.Dispose()
    }
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose()
    $big.Dispose()

    return $small
}

# One icon image: sizes up to 64 as classic 32-bit bitmaps (every icon reader handles those),
# 256 as PNG.
function Get-IconImageBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $ms = New-Object System.IO.MemoryStream

    if ($size -ge 256) {
        $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }

    $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)

    # BITMAPINFOHEADER; the height counts the color rows plus the mask rows
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($size * $size * 4 + $maskStride * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $p = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
        }
    }

    # The mask is all zero: transparency comes from the alpha channel
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()

    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-RingBitmap $size
    [pscustomobject]@{ Size = $size; Bytes = (Get-IconImageBytes $bitmap) }
    $bitmap.Dispose()
}

$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file

$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]$image.Bytes.Length); $writer.Write([int]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write([byte[]]$image.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($Out, $file.ToArray())

"Wrote $([System.IO.Path]::GetFullPath($Out)) ($($file.Length) bytes, sizes: $($sizes -join ', '))"
