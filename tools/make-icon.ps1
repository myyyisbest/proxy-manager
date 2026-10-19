# Generate app icon: rounded square, Claude-coral gradient, glossy skeuomorphic finish,
# white share-arrow (curved swoosh + solid arrowhead). Outputs app.ico (multi-size) + preview png.
# NOTE: keep this file ASCII-only (PowerShell 5.1 misreads non-BOM UTF-8 comments).
Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $dir '..\src\ProxyManager\Assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-RoundedPath([float]$s, [float]$radius) {
    $d = $radius * 2; $w = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-ShareArrow([System.Drawing.Graphics]$g, [float]$s, [System.Drawing.Color]$color, [float]$offsetY) {
    # curved swoosh tail: smooth diagonal sweep from bottom-left into the arrowhead
    $pts = @(
        (New-Object System.Drawing.PointF (0.185 * $s), (0.74 * $s + $offsetY)),
        (New-Object System.Drawing.PointF (0.235 * $s), (0.55 * $s + $offsetY)),
        (New-Object System.Drawing.PointF (0.345 * $s), (0.435 * $s + $offsetY)),
        (New-Object System.Drawing.PointF (0.505 * $s), (0.41 * $s + $offsetY))
    )
    $pen = New-Object System.Drawing.Pen ($color), (0.15 * $s)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawCurve($pen, $pts, 0.75)
    $pen.Dispose()

    # solid arrowhead
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF (0.465 * $s), (0.215 * $s + $offsetY)),
        (New-Object System.Drawing.PointF (0.815 * $s), (0.465 * $s + $offsetY)),
        (New-Object System.Drawing.PointF (0.465 * $s), (0.715 * $s + $offsetY))
    ))
    $brush = New-Object System.Drawing.SolidBrush ($color)
    $g.FillPath($brush, $head)
    $brush.Dispose()
    $head.Dispose()
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int][Math]::Round($s * 0.235))
    $body = New-RoundedPath $s $radius
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)

    # base: Claude-coral vertical gradient (lighter top -> deeper bottom)
    $cTop = [System.Drawing.Color]::FromArgb(255, 0xEF, 0x9C, 0x79)
    $cMid = [System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57)
    $cBot = [System.Drawing.Color]::FromArgb(255, 0xBE, 0x5D, 0x3B)
    $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($rect), ($cTop), ($cBot), 90.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = @($cTop, $cMid, $cBot)
    $blend.Positions = @(0.0, 0.55, 1.0)
    $baseBrush.InterpolationColors = $blend
    $g.FillPath($baseBrush, $body)
    $baseBrush.Dispose()

    $oldClip = $g.Clip
    $g.SetClip($body)

    # inner shadow: fades IN toward the bottom edge (transparent -> dark) so no hard line appears
    $shadeRect = New-Object System.Drawing.Rectangle(0, [int]($s * 0.5), $s, [int]($s * 0.5))
    $shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($shadeRect), ([System.Drawing.Color]::FromArgb(0, 90, 35, 18)), ([System.Drawing.Color]::FromArgb(80, 90, 35, 18)), 90.0
    $shade.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillRectangle($shade, $shadeRect)
    $shade.Dispose()

    # gloss: soft white sheen across the top half
    $glossRect = New-Object System.Drawing.Rectangle(0, 0, $s, [int]($s * 0.52))
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($glossRect), ([System.Drawing.Color]::FromArgb(85, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillRectangle($gloss, $glossRect)
    $gloss.Dispose()
    $g.Clip = $oldClip

    # 1px lighter rim on the edge for a beveled feel
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 255, 235, 225)), ([Math]::Max(1.0, $s * 0.012))
    $g.DrawPath($rim, $body)
    $rim.Dispose()

    # arrow: soft drop shadow first, then the white arrow on top
    $shadowColor = [System.Drawing.Color]::FromArgb(80, 122, 48, 24)
    Draw-ShareArrow $g $s $shadowColor ($s * 0.035)

    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    Draw-ShareArrow $g $s $white 0

    $g.Dispose()
    return ,$bmp
}

function Get-IconBmpBytes([System.Drawing.Bitmap]$b) {
    $w = $b.Width; $h = $b.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $b.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $b.UnlockBits($data)

    $rowBytes = $w * 4
    $body = New-Object byte[] ($rowBytes * $h)
    for ($y = 0; $y -lt $h; $y++) {
        [Array]::Copy($px, $y * $rowBytes, $body, ($h - 1 - $y) * $rowBytes, $rowBytes)
    }
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $mask = New-Object byte[] ($maskStride * $h)

    $header = New-Object byte[] 40
    [BitConverter]::GetBytes([Int32]40).CopyTo($header, 0)
    [BitConverter]::GetBytes([Int32]$w).CopyTo($header, 4)
    [BitConverter]::GetBytes([Int32]($h * 2)).CopyTo($header, 8)
    [BitConverter]::GetBytes([Int16]1).CopyTo($header, 12)
    [BitConverter]::GetBytes([Int16]32).CopyTo($header, 14)
    return ,($header + $body + $mask)
}

$bmpSizes = @(16, 24, 32, 48)
$blobs = @()
foreach ($s in $bmpSizes) {
    $img = New-IconBitmap $s
    $bytes = Get-IconBmpBytes $img
    $img.Dispose()
    $blobs += ,@($s, [byte[]]$bytes)
}
$big = New-IconBitmap 256
$ms = New-Object System.IO.MemoryStream
$big.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$blobs += ,@(256, $ms.ToArray())

# write ICO container
$icoPath = Join-Path $out 'app.ico'
$stream = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($stream)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($b in $blobs) {
    $s = $b[0]; $bytes = $b[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($b in $blobs) { $bw.Write([byte[]]$b[1]) }
$bw.Flush(); $bw.Close()

# preview png for visual check
$big.Save((Join-Path $out 'icon_preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

Write-Host "OK: $icoPath"
