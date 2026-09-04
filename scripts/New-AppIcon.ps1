Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "..\src\Beamcast\Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))

    $pad = [int]($size * 0.06)
    $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = [int]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 18, 24))
    $g.FillPath($bg, $path)

    $accent = [System.Drawing.Color]::FromArgb(255, 255, 77, 109)
    $cx = $size * 0.42
    $cy = $size * 0.5

    # Soft glow behind the beam origin.
    $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 255, 77, 109))
    $g.FillEllipse($glow, $cx - $size * 0.26, $cy - $size * 0.26, $size * 0.52, $size * 0.52)

    # Broadcast arcs opening to the right.
    $penWidth = [Math]::Max(1.0, $size / 18.0)
    foreach ($r in @(0.16, 0.26, 0.36)) {
        $alpha = [int](230 - ($r * 400))
        if ($alpha -lt 90) { $alpha = 90 }
        $color = [System.Drawing.Color]::FromArgb($alpha, $accent)
        $pen = New-Object System.Drawing.Pen -ArgumentList @($color, [single]$penWidth)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $rr = $size * $r
        $g.DrawArc($pen, [single]($cx - $rr), [single]($cy - $rr), [single]($rr * 2), [single]($rr * 2), -38, 76)
        $pen.Dispose()
    }

    # Solid core dot.
    $core = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($core, $cx - $size * 0.07, $cy - $size * 0.07, $size * 0.14, $size * 0.14)

    $g.Dispose()
    $path.Dispose()
    $bg.Dispose()
    $glow.Dispose()
    $core.Dispose()
    return $bmp
}

function Write-Ico($path, [System.Drawing.Bitmap[]]$images) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$images.Count)

    $payloads = @()
    foreach ($img in $images) {
        $png = New-Object System.IO.MemoryStream
        $img.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $payloads += , $png.ToArray()
        $png.Dispose()
    }

    $offset = 6 + (16 * $images.Count)
    for ($i = 0; $i -lt $images.Count; $i++) {
        $img = $images[$i]
        $w = if ($img.Width -ge 256) { 0 } else { [byte]$img.Width }
        $h = if ($img.Height -ge 256) { 0 } else { [byte]$img.Height }
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$payloads[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $payloads[$i].Length
    }

    foreach ($bytes in $payloads) { $bw.Write($bytes) }
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) { $images += New-Mark $s }

Write-Ico (Join-Path $assets "Beamcast.ico") $images
$images[-1].Save((Join-Path $assets "Beamcast.png"), [System.Drawing.Imaging.ImageFormat]::Png)
foreach ($img in $images) { $img.Dispose() }

Write-Host "Icon written to $assets"
