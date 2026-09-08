# Store logo images: 1:1 box art (1080) and 9:16 poster art (720x1080), the ring on Windows blue.
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$icon = [System.Drawing.Image]::FromFile((Join-Path $here "..\Assets\Square150x150Logo.scale-400.png"))   # 600 px tile with rounded corners

function Canvas($w, $h) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 103, 192))
  return @($bmp, $g)
}

# 1:1 box art: the tile fills most of the square
$c = Canvas 1080 1080; $bmp = $c[0]; $g = $c[1]
$g.DrawImage($icon, 190, 190, 700, 700)
$bmp.Save((Join-Path $here "boxart-1080.png")); "boxart"

# 9:16 poster: tile up top, name beneath
$c = Canvas 720 1080; $bmp = $c[0]; $g = $c[1]
$g.DrawImage($icon, 160, 190, 400, 400)
$f = New-Object System.Drawing.Font('Segoe UI Variable Display', 54, [System.Drawing.FontStyle]::Bold)
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString("Watt's Left", $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 650, 720, 100), $fmt)
$f2 = New-Object System.Drawing.Font('Segoe UI Variable Text', 22)
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 255, 255, 255))
$g.DrawString("Your battery, in watts.", $f2, $soft, (New-Object System.Drawing.RectangleF 0, 740, 720, 60), $fmt)
$bmp.Save((Join-Path $here "poster-720x1080.png")); "poster"

# 300x300 app tile icon for the Store display
$c = Canvas 300 300; $bmp = $c[0]; $g = $c[1]
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($icon, 0, 0, 300, 300)
$bmp.Save((Join-Path $here "tile-300.png")); "tile"
