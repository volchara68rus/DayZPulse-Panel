# ============================================================
#  Генерация иконок DayZ Pulse.
#
#  Берёт исходник icon.ico, перекрашивает ЛИНИЮ ПУЛЬСА из красной
#  в градиент «красный → зелёный» (слева направо), не трогая серый
#  «Z», силуэт карты и белую надпись. Затем пишет:
#    installer\app.ico        — иконка exe, ярлыков, установщика
#    installer\dayz-pulse.ico — иконка установщика (копия)
#    wwwroot\assets\favicon.ico
#    wwwroot\assets\logo.png  — логотип для панели
#    installer\tray.png       — значок трея (эмблема без подписи)
#
#  Запуск:
#    powershell -ExecutionPolicy Bypass -File installer\make-icon.ps1
#    powershell ... -Source "$env:USERPROFILE\Downloads\icon.ico"
# ============================================================
param(
    [string]$Source = "$env:USERPROFILE\Downloads\icon.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Не найден исходный файл: $Source" }

$root      = Split-Path -Parent $PSScriptRoot
$installer = $PSScriptRoot
$assets    = Join-Path $root 'wwwroot\assets'

# ------------------------------------------------------------
# 1. Исходник -> квадратный холст 256x256 (по центру).
# ------------------------------------------------------------
$srcIcon = New-Object System.Drawing.Icon($Source, 256, 256)
$srcBmp  = $srcIcon.ToBitmap()

$size = 256
$canvas = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.Clear([System.Drawing.Color]::Transparent)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.DrawImage($srcBmp,
    [int](($size - $srcBmp.Width) / 2), [int](($size - $srcBmp.Height) / 2),
    $srcBmp.Width, $srcBmp.Height)
$g.Dispose()

# ------------------------------------------------------------
# 2. Перекраска линии пульса: красный -> градиент красный..зелёный.
#    Маска = «насколько пиксель краснее нейтрального серого».
#    Серый «Z», карта и белая надпись (R≈G≈B) не затрагиваются.
# ------------------------------------------------------------
function Recolor-Pulse([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height

    # Цвета градиента (слева/справа).
    $leftR = 255; $leftG = 45;  $leftB = 45
    $rightR = 60; $rightG = 220; $rightB = 95

    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.A -eq 0) { continue }

            $neutral = [Math]::Max($c.G, $c.B)
            $redness = $c.R - $neutral

            if ($redness -le 6) { continue }   # не красный — пропускаем

            # Насколько пиксель «линия», а не фон: по превышению над серым.
            $mask = [Math]::Min(1.0, $redness / 70.0)
            # Яркость исходной линии (красный канал как мера свечения).
            $intensity = [Math]::Min(1.0, $c.R / 200.0)

            # Цвет градиента в этой колонке.
            $t = $x / [double]($w - 1)
            $tr = [int]($leftR + ($rightR - $leftR) * $t)
            $tg = [int]($leftG + ($rightG - $leftG) * $t)
            $tb = [int]($leftB + ($rightB - $leftB) * $t)

            # Целевой цвет с учётом яркости и маски.
            $nr = [int]($tr * $intensity)
            $ng = [int]($tg * $intensity)
            $nb = [int]($tb * $intensity)

            # Смешиваем с исходным по маске.
            $rr = [int]($c.R * (1 - $mask) + $nr * $mask)
            $rg = [int]($c.G * (1 - $mask) + $ng * $mask)
            $rb = [int]($c.B * (1 - $mask) + $nb * $mask)

            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $rr, $rg, $rb))
        }
    }
}

Recolor-Pulse $canvas

# ------------------------------------------------------------
# 3. Масштабирование.
# ------------------------------------------------------------
function Resize-Image([System.Drawing.Bitmap]$source, [int]$target) {
    $bmp = New-Object System.Drawing.Bitmap($target, $target, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.Clear([System.Drawing.Color]::Transparent)
    $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gr.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gr.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gr.DrawImage($source,
        (New-Object System.Drawing.Rectangle(0, 0, $target, $target)),
        (New-Object System.Drawing.Rectangle(0, 0, $source.Width, $source.Height)),
        [System.Drawing.GraphicsUnit]::Pixel)
    $gr.Dispose()
    return $bmp
}

# ------------------------------------------------------------
# 4. Кадр DIB (BMP) для .ico — совместимо с Windows и Inno Setup.
# ------------------------------------------------------------
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$s)
    $bw.Write([Int32]($s * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]($s * $s * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    for ($row = $s - 1; $row -ge 0; $row--) {
        for ($col = 0; $col -lt $s; $col++) {
            $c = $bmp.GetPixel($col, $row)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }

    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $mask = New-Object byte[] ($maskRow * $s)
    for ($row = 0; $row -lt $s; $row++) {
        $srcRow = $s - 1 - $row
        $off = $row * $maskRow
        for ($col = 0; $col -lt $s; $col++) {
            if ($bmp.GetPixel($col, $srcRow).A -lt 128) {
                $mask[$off + [int][Math]::Floor($col / 8)] =
                    $mask[$off + [int][Math]::Floor($col / 8)] -bor (0x80 -shr ($col % 8))
            }
        }
    }
    $bw.Write([byte[]]$mask)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $frames = @()
    foreach ($s in $sizes) {
        $b = Resize-Image $canvas $s
        $frames += [pscustomobject]@{ Size = $s; Data = (Get-DibBytes $b) }
        $b.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$f.Data.Length); $bw.Write([UInt32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f.Data) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
    "  {0} — {1} px, {2:N0} байт" -f (Split-Path -Leaf $path), ($sizes -join '/'), (Get-Item $path).Length
}

$fullSizes    = @(16, 24, 32, 48, 64, 128, 256)
$faviconSizes = @(16, 32, 48)

Write-Host "==> Запись иконок" -ForegroundColor Cyan
Write-Ico (Join-Path $installer 'app.ico') $fullSizes
Write-Ico (Join-Path $installer 'dayz-pulse.ico') $fullSizes
New-Item -ItemType Directory -Force $assets | Out-Null
Write-Ico (Join-Path $assets 'favicon.ico') $faviconSizes

# Логотип для панели.
$logo = Resize-Image $canvas 256
$logo.Save((Join-Path $assets 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
"  logo.png — 256 px"

# Значок трея: эмблема без строки с названием (в 16 px текст не читается).
$tile = New-Object System.Drawing.Bitmap(256, 256)
$tg = [System.Drawing.Graphics]::FromImage($tile)
$tg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$tg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$tg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

$radius = 40
$clip = New-Object System.Drawing.Drawing2D.GraphicsPath
$clip.AddArc(0, 0, $radius*2, $radius*2, 180, 90)
$clip.AddArc(256-$radius*2, 0, $radius*2, $radius*2, 270, 90)
$clip.AddArc(256-$radius*2, 256-$radius*2, $radius*2, $radius*2, 0, 90)
$clip.AddArc(0, 256-$radius*2, $radius*2, $radius*2, 90, 90)
$clip.CloseFigure()
$tg.SetClip($clip)

# Верхняя часть (эмблема без надписи DAYZ PULSE).
$tg.DrawImage($canvas,
    (New-Object System.Drawing.Rectangle(0, 0, 256, 256)),
    (New-Object System.Drawing.Rectangle(4, 0, 249, 204)),
    [System.Drawing.GraphicsUnit]::Pixel)

$tg.ResetClip(); $tg.Dispose(); $clip.Dispose()
$tile.Save((Join-Path $installer 'tray.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$tile.Dispose()
"  tray.png — 256 px"

$canvas.Dispose(); $srcBmp.Dispose(); $srcIcon.Dispose()
Write-Host "Готово." -ForegroundColor Green
