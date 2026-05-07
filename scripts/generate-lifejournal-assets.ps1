param(
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\MasterApp\wwwroot\life\assets')
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$rng = [System.Random]::new(4817)

function Save-Png($bitmap, $name) {
  $path = Join-Path $OutputDirectory $name
  $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bitmap.Dispose()
}

function New-Brush($color) {
  return [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($color))
}

function New-Pen($color, $width = 1) {
  return [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($color), $width)
}

function Add-Noise($bitmap, $baseR, $baseG, $baseB, $spread, $alpha = 255) {
  for ($y = 0; $y -lt $bitmap.Height; $y++) {
    for ($x = 0; $x -lt $bitmap.Width; $x++) {
      $n = $rng.Next(-$spread, $spread + 1)
      $r = [Math]::Max(0, [Math]::Min(255, $baseR + $n + $rng.Next(-5, 6)))
      $g = [Math]::Max(0, [Math]::Min(255, $baseG + $n))
      $b = [Math]::Max(0, [Math]::Min(255, $baseB + [int]($n / 2)))
      $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $r, $g, $b))
    }
  }
}

function New-CorkBoard {
  $bitmap = [System.Drawing.Bitmap]::new(960, 1600)
  Add-Noise $bitmap 156 104 55 38
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

  for ($i = 0; $i -lt 9000; $i++) {
    $x = $rng.Next(0, $bitmap.Width)
    $y = $rng.Next(0, $bitmap.Height)
    $w = $rng.Next(1, 8)
    $h = $rng.Next(1, 5)
    $alpha = $rng.Next(28, 96)
    $r = $rng.Next(98, 205)
    $g = $rng.Next(61, 139)
    $b = $rng.Next(30, 82)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($alpha, $r, $g, $b))
    $graphics.FillEllipse($brush, $x, $y, $w, $h)
    $brush.Dispose()
  }

  $vignette = [System.Drawing.Drawing2D.PathGradientBrush]::new([System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(0, 0),
    [System.Drawing.Point]::new($bitmap.Width, 0),
    [System.Drawing.Point]::new($bitmap.Width, $bitmap.Height),
    [System.Drawing.Point]::new(0, $bitmap.Height)
  ))
  $vignette.CenterColor = [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
  $vignette.SurroundColors = @([System.Drawing.Color]::FromArgb(95, 64, 32, 14))
  $graphics.FillRectangle($vignette, 0, 0, $bitmap.Width, $bitmap.Height)
  $vignette.Dispose()
  $graphics.Dispose()
  Save-Png $bitmap 'cork_board_bg.png'
}

function New-PhotoFrame {
  $bitmap = [System.Drawing.Bitmap]::new(360, 440)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::Transparent)
  $shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(58, 45, 30, 18))
  $paper = New-Brush '#f8f2e7'
  $edge = New-Pen '#ded2c0' 2
  $graphics.FillRectangle($shadow, 22, 26, 312, 374)
  $graphics.FillRectangle($paper, 12, 12, 312, 374)
  $graphics.DrawRectangle($edge, 12, 12, 312, 374)
  $inner = New-Pen '#eee5d6' 2
  $graphics.DrawRectangle($inner, 31, 31, 274, 268)
  $shadow.Dispose(); $paper.Dispose(); $edge.Dispose(); $inner.Dispose(); $graphics.Dispose()
  Save-Png $bitmap 'photo_card_frame.png'
}

function New-Pin($name, $fill, $dark) {
  $bitmap = [System.Drawing.Bitmap]::new(96, 96)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::Transparent)
  $shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(75, 35, 22, 15))
  $body = New-Brush $fill
  $rim = New-Pen $dark 3
  $shine = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(115, 255, 255, 255))
  $needle = New-Pen '#4d4038' 4
  $graphics.DrawLine($needle, 49, 52, 71, 89)
  $graphics.FillEllipse($shadow, 24, 18, 52, 52)
  $graphics.FillEllipse($body, 18, 13, 52, 52)
  $graphics.DrawEllipse($rim, 18, 13, 52, 52)
  $graphics.FillEllipse($shine, 29, 22, 17, 13)
  $shadow.Dispose(); $body.Dispose(); $rim.Dispose(); $shine.Dispose(); $needle.Dispose(); $graphics.Dispose()
  Save-Png $bitmap $name
}

function New-Staple {
  $bitmap = [System.Drawing.Bitmap]::new(128, 64)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::Transparent)
  $shadow = New-Pen '#3c3028' 8
  $metal = New-Pen '#d5d8d2' 7
  $highlight = New-Pen '#f6f7f0' 2
  $graphics.DrawArc($shadow, 23, 17, 82, 38, 192, 156)
  $graphics.DrawArc($metal, 19, 12, 82, 38, 192, 156)
  $graphics.DrawArc($highlight, 21, 14, 78, 30, 198, 144)
  $shadow.Dispose(); $metal.Dispose(); $highlight.Dispose(); $graphics.Dispose()
  Save-Png $bitmap 'staple.png'
}

function New-PaperNote {
  $bitmap = [System.Drawing.Bitmap]::new(640, 420)
  Add-Noise $bitmap 241 230 202 10
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $linePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(72, 156, 134, 102), 2)
  for ($y = 64; $y -lt 390; $y += 42) {
    $graphics.DrawLine($linePen, 42, $y, 598, $y)
  }
  $marginPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(84, 176, 92, 72), 2)
  $graphics.DrawLine($marginPen, 82, 32, 82, 390)
  $edge = New-Pen '#d3c29d' 3
  $graphics.DrawRectangle($edge, 10, 10, 620, 400)
  $linePen.Dispose(); $marginPen.Dispose(); $edge.Dispose(); $graphics.Dispose()
  Save-Png $bitmap 'paper_note_bg.png'
}

New-CorkBoard
New-PhotoFrame
New-Pin 'pin_red.png' '#b53b35' '#7f211f'
New-Pin 'pin_blue.png' '#2e79a8' '#1c4b69'
New-Staple
New-PaperNote

Get-ChildItem -Path $OutputDirectory -Filter *.png | Select-Object FullName, Length
