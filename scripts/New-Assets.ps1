param([string]$Directory = (Join-Path $PSScriptRoot '../packaging/Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $Directory | Out-Null
foreach($size in 44,50,150,256) {
    $bmp = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $u = $size / 100.0
    $g.ScaleTransform($u,$u)
    $ellipse = [Drawing.RectangleF]::new(6,6,88,88)
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new($ellipse,[Drawing.Color]::FromArgb(18,40,83),[Drawing.Color]::FromArgb(115,54,178),65)
    $g.FillEllipse($gradient,$ellipse)
    $rim = [Drawing.Pen]::new([Drawing.Color]::FromArgb(113,197,255),2.5)
    $g.DrawEllipse($rim,$ellipse)
    $wave = [Drawing.Pen]::new([Drawing.Color]::FromArgb(232,244,255),4.4)
    $wave.StartCap = $wave.EndCap = 'Round'
    $shape = [Drawing.PointF[]]@([Drawing.PointF]::new(19,51),[Drawing.PointF]::new(33,51),[Drawing.PointF]::new(41,33),[Drawing.PointF]::new(53,68),[Drawing.PointF]::new(65,47),[Drawing.PointF]::new(81,47))
    $g.DrawLines($wave,$shape)
    $name = switch($size){44{'Square44x44Logo.png'} 50{'StoreLogo.png'} 150{'Square150x150Logo.png'} 256{'AppIcon.png'}}
    $bmp.Save((Join-Path $Directory $name),[Drawing.Imaging.ImageFormat]::Png)
    $wave.Dispose(); $rim.Dispose(); $gradient.Dispose(); $g.Dispose(); $bmp.Dispose()
}
