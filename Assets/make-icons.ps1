# Draws the app icon (Windows-blue battery showing what's left) at every size Windows and the Store want.
# Run:  powershell -ExecutionPolicy Bypass -File make-icons.ps1     (from the Assets folder)
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$canvas, [double]$fill = 0.72) {
    # The mark: a ring, bright for the charge that's left (shown at 75 %), faint for what's gone.
    $bmp = New-Object System.Drawing.Bitmap $canvas, $canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $size = $canvas * $fill
    $stroke = $size * 0.22
    $r = ($size - $stroke) / 2
    $c = $canvas / 2
    $rect = [System.Drawing.RectangleF]::new($c - $r, $c - $r, $r * 2, $r * 2)
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), $stroke
    $lit = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $lit.StartCap = 'Round'; $lit.EndCap = 'Round'
    $g.DrawEllipse($track, $rect)
    $g.DrawArc($lit, $rect, -90, 270)
    $g.Dispose()
    return $bmp
}

function Draw-Tile([int]$canvas) {
    # Store tile: the ring on a rounded Windows-blue square
    $bmp = New-Object System.Drawing.Bitmap $canvas, $canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $rad = $canvas * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $rad * 2, $rad * 2, 180, 90)
    $path.AddArc($canvas - $rad * 2, 0, $rad * 2, $rad * 2, 270, 90)
    $path.AddArc($canvas - $rad * 2, $canvas - $rad * 2, $rad * 2, $rad * 2, 0, 90)
    $path.AddArc(0, $canvas - $rad * 2, $rad * 2, $rad * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($canvas, $canvas)), ([System.Drawing.Color]::FromArgb(255, 43, 139, 224)), ([System.Drawing.Color]::FromArgb(255, 0, 88, 168))
    $g.FillPath($brush, $path)
    $ring = Draw-Icon $canvas 0.6
    $g.DrawImage($ring, 0, 0)
    $g.Dispose(); $ring.Dispose()
    return $bmp
}

function Save([System.Drawing.Bitmap]$bmp, [string]$name) {
    $bmp.Save((Join-Path $PSScriptRoot $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Draw-Wide([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $icon = Draw-Tile ([int]($h * 0.7))
    $g.DrawImage($icon, [int](($w - $icon.Width) / 2), [int](($h - $icon.Height) / 2))
    $g.Dispose(); $icon.Dispose()
    return $bmp
}

# App list icon (taskbar / Start): scale variants + exact target sizes, plated and unplated
# Taskbar and Start: the ring on its blue tile, so it reads blue on any taskbar
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Tile ([int](44 * $scale / 100))) "Square44x44Logo.scale-$scale.png" }
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 64, 256) {
    Save (Draw-Tile $size) "Square44x44Logo.targetsize-$size.png"
    Save (Draw-Tile $size) "Square44x44Logo.targetsize-${size}_altform-unplated.png"
    Save (Draw-Tile $size) "Square44x44Logo.targetsize-${size}_altform-lightunplated.png"
}
# Tiles, Store logo, splash
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Tile ([int](150 * $scale / 100))) "Square150x150Logo.scale-$scale.png" }
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Tile ([int](71 * $scale / 100))) "SmallTile.scale-$scale.png" }
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Wide ([int](310 * $scale / 100)) ([int](150 * $scale / 100))) "Wide310x150Logo.scale-$scale.png" }
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Wide ([int](620 * $scale / 100)) ([int](300 * $scale / 100))) "SplashScreen.scale-$scale.png" }
foreach ($scale in 100, 125, 150, 200, 400) { Save (Draw-Tile ([int](50 * $scale / 100))) "StoreLogo.scale-$scale.png" }
Save (Draw-Tile 50) "StoreLogo.png"
Save (Draw-Icon 24 0.9) "LockScreenLogo.scale-200.png"

# AppIcon.ico for the window: PNG-compressed frames in an ICO container
$sizes = 16, 24, 32, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $bmp = Draw-Tile $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "AppIcon.ico"), $out.ToArray())
"icons written"
