# Regenerates src/RepoWatch.Desktop/Assets/RepoWatch.ico (multi-size, PNG-compressed entries).
# The design: a deep-space rounded square, a cyan orbit ring and a diamond "station" (the HUD brand mark).
# Windows only (System.Drawing). Run from the repository root:  pwsh scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 64.0

    # Rounded square background with a vertical navy gradient.
    $radius = 14 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [System.Drawing.RectangleF]::new(2 * $s, 2 * $s, 60 * $s, 60 * $s)
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90); $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0x14, 0x24, 0x40)), ([System.Drawing.Color]::FromArgb(255, 0x06, 0x0A, 0x14)), 90.0
    $g.FillPath($fill, $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 0x3F, 0xE0, 0xFF)), ([Math]::Max(1, 1.5 * $s))), $path)

    # Orbit ring.
    $cyan = [System.Drawing.Color]::FromArgb(255, 0x3F, 0xE0, 0xFF)
    $ring = New-Object System.Drawing.Pen $cyan, ([Math]::Max(1.2, 3.2 * $s))
    $g.DrawEllipse($ring, 15 * $s, 15 * $s, 34 * $s, 34 * $s)

    # Diamond station in the center, and a small satellite on the ring.
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(32 * $s, 22 * $s), [System.Drawing.PointF]::new(42 * $s, 32 * $s),
        [System.Drawing.PointF]::new(32 * $s, 42 * $s), [System.Drawing.PointF]::new(22 * $s, 32 * $s))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $cyan), $points)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3D, 0xFF, 0xA2))), 43 * $s, 14 * $s, 8 * $s, 8 * $s)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))); $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
$target = Join-Path (Get-Location) 'src/RepoWatch.Desktop/Assets/RepoWatch.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', '))"
