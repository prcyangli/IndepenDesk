# IndepenDesk logo üretici: app.ico (çok boyutlu) + MSIX Assets PNG'leri.
# Bir kez çalıştırılır, çıktılar depoya commit edilir.
Add-Type -AssemblyName System.Drawing

$out = $PSScriptRoot
$assets = Join-Path $out "Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $m = [Math]::Max(1, [int]($size / 10))          # dış kenar boşluğu
    $gap = [Math]::Max(1, [int]($size / 16))        # iki ekran arası boşluk
    $w = [int](($size - 2 * $m - $gap) / 2)
    $h = [int]($size * 0.62)
    $y = [int](($size - $h) / 2)
    $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 120, 215))
    $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 200, 250))
    $g.FillRectangle($b1, $m, $y, $w, $h)
    $g.FillRectangle($b2, $m + $w + $gap, $y, $w, $h)
    $g.Dispose()
    return $bmp
}

function Save-Png([int]$size, [string]$path) {
    $bmp = Draw-Logo $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# MSIX görselleri
Save-Png 44  (Join-Path $assets "Square44x44Logo.png")
Save-Png 150 (Join-Path $assets "Square150x150Logo.png")
Save-Png 50  (Join-Path $assets "StoreLogo.png")

# Çok boyutlu .ico (PNG gömülü)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Logo $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$icoPath = Join-Path $out "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # genişlik
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # yükseklik
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

"OK: app.ico + Assets/ üretildi"
