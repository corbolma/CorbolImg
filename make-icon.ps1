# Genera appimg.ico (multi-risoluzione, PNG interni) e un'anteprima icon-preview.png
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)          # margine
    $w = $s - 2 * $m
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $bg = New-RoundRect $m $m $w $w ($s * 0.22)

    # Sfondo: gradiente diagonale viola -> rosa
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 109, 40, 217)),
        ([System.Drawing.Color]::FromArgb(255, 236, 72, 153)), 45.0
    $g.FillPath($grad, $bg)

    # Riflesso morbido in alto
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillPath($hl, $bg)

    # "Foto": riquadro bianco con montagne e sole
    $px = $s * 0.20; $py = $s * 0.24; $pw = $s * 0.60; $ph = $s * 0.50
    if ($s -lt 24) { $px = $s * 0.14; $py = $s * 0.2; $pw = $s * 0.72; $ph = $s * 0.6 }
    $photo = New-RoundRect $px $py $pw $ph ([Math]::Max(1.0, $s * 0.06))
    $g.FillPath([System.Drawing.Brushes]::White, $photo)
    $g.SetClip($photo)
    $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 124, 58, 237))
    $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 251, 191, 36))
    $hill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 236, 72, 153))
    $sr = $ph * 0.14
    $g.FillEllipse($sun, $px + $pw * 0.70 - $sr, $py + $ph * 0.28 - $sr, 2 * $sr, 2 * $sr)
    $back = @(
        (New-Object System.Drawing.PointF ($px - 1), ($py + $ph + 1)),
        (New-Object System.Drawing.PointF ($px + $pw * 0.30), ($py + $ph * 0.38)),
        (New-Object System.Drawing.PointF ($px + $pw * 0.62), ($py + $ph + 1)))
    $g.FillPolygon($sky, $back)
    $front = @(
        (New-Object System.Drawing.PointF ($px + $pw * 0.35), ($py + $ph + 1)),
        (New-Object System.Drawing.PointF ($px + $pw * 0.68), ($py + $ph * 0.55)),
        (New-Object System.Drawing.PointF ($px + $pw + 1), ($py + $ph + 1)))
    $g.FillPolygon($hill, $front)
    $g.ResetClip()

    # Due frecce circolari (conversione) in basso a destra, dalle dimensioni medie in su
    if ($s -ge 32) {
        $cx = $s * 0.76; $cy = $s * 0.76; $r = $s * 0.17
        $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.FillEllipse($ring, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 109, 40, 217)), ([Math]::Max(1.5, $s * 0.035))
        $ar = $r * 0.55
        $g.DrawArc($pen, $cx - $ar, $cy - $ar, 2 * $ar, 2 * $ar, 200, 130)
        $g.DrawArc($pen, $cx - $ar, $cy - $ar, 2 * $ar, 2 * $ar, 20, 130)
        $hb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 109, 40, 217))
        $hs = $r * 0.32
        foreach ($a in 330, 150) {
            $rad = $a * [Math]::PI / 180
            $ex = $cx + $ar * [Math]::Cos($rad); $ey = $cy + $ar * [Math]::Sin($rad)
            $t = $rad + [Math]::PI / 2   # direzione tangente (senso orario)
            $tip = New-Object System.Drawing.PointF ($ex + $hs * [Math]::Cos($t)), ($ey + $hs * [Math]::Sin($t))
            $l = New-Object System.Drawing.PointF ($ex + $hs * 0.8 * [Math]::Cos($rad)), ($ey + $hs * 0.8 * [Math]::Sin($rad))
            $rr = New-Object System.Drawing.PointF ($ex - $hs * 0.8 * [Math]::Cos($rad)), ($ey - $hs * 0.8 * [Math]::Sin($rad))
            $g.FillPolygon($hb, @($tip, $l, $rr))
        }
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

# Scrive il file .ico
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'appimg.ico'), $out.ToArray())

# Anteprima: tutte le dimensioni affiancate su sfondo chiaro e scuro
$pw = 20; foreach ($s in $sizes) { $pw += $s + 20 }
$prev = New-Object System.Drawing.Bitmap $pw, 620
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(245, 245, 245))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 310, $pw, 310)
$x = 20
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $pg.DrawImage($b, $x, 290 - $s); $pg.DrawImage($b, $x, 600 - $s)
    $x += $s + 20; $b.Dispose()
}
$prev.Save((Join-Path $dir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "Creati appimg.ico e icon-preview.png"
