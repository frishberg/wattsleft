# Composes Store screenshots (1920x1080) from a clean capture of the app window.
# Usage: powershell -ExecutionPolicy Bypass -File make-screenshots.ps1
Add-Type -AssemblyName System.Drawing

$here = $PSScriptRoot
$src = [System.Drawing.Image]::FromFile((Join-Path $here "..\..\site\img\app.png"))

function Shot([string]$name, [string]$headline, [string]$sub) {
    $w = 1920; $h = 1080
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 103, 192))

    $scale = [Math]::Min(960.0 / $src.Height, 1.0)
    $dw = [int]($src.Width * $scale); $dh = [int]($src.Height * $scale)
    $x = $w - $dw - 200; $y = [int](($h - $dh) / 2)
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 0, 30, 80))
    $g.FillRectangle($shadow, $x + 10, $y + 18, $dw, $dh)
    $g.DrawImage($src, $x, $y, $dw, $dh)

    $f1 = New-Object System.Drawing.Font('Segoe UI Variable Display', 64, [System.Drawing.FontStyle]::Bold)
    $f2 = New-Object System.Drawing.Font('Segoe UI Variable Text', 26)
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 255, 255, 255))
    $g.DrawString($headline, $f1, [System.Drawing.Brushes]::White, 180, 300)
    $g.DrawString($sub, $f2, $soft, 184, 560)
    $bmp.Save((Join-Path $here $name)); $bmp.Dispose()
    "saved $name"
}

Shot "screenshot-1.png" "Your battery,`nin watts." "Watts in from the charger, watts out to the laptop,`nreal time left, stored energy, voltage and health.`nLive, four times a second. Free. No tracking."
Shot "screenshot-2.png" "Thirty minutes`nof history." "Charge, watts in, watts out and voltage,`nsampled every five seconds and kept across restarts.`nHover any point for the value at that moment."
Shot "screenshot-3.png" "Every number`nexplained." "Hover anything for one plain sentence on what it means,`nwhat moves it, and what's normal."
