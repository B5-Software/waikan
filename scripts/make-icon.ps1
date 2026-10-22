$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\Waikan\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir 'app.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = New-Object System.Collections.Generic.List[byte[]]

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1.0, $size * 0.04)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [Math]::Max(4.0, $size * 0.24)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 236, 101, 62),
        [System.Drawing.Color]::FromArgb(255, 190, 54, 35),
        45.0)
    $g.FillPath($brush, $path)

    $fontSize = [Math]::Max(7.0, $size * 0.53)
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, (-$size * 0.015), $size, $size)
    $g.DrawString('外', $font, [System.Drawing.Brushes]::White, $textRect, $fmt)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $font.Dispose()
    $fmt.Dispose()
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)          # reserved
$writer.Write([UInt16]1)          # icon
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $data = $pngs[$i]
    $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([Byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([Byte]0)        # color count
    $writer.Write([Byte]0)        # reserved
    $writer.Write([UInt16]1)      # planes
    $writer.Write([UInt16]32)     # bpp
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $writer.Write($data) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($outFile, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "Wrote $outFile"
