# Builds the iPhone app icon from the shared artwork in IconArt.ps1: one 1024x1024 image, from which Xcode makes
# every size. Full-bleed and opaque, because iOS rounds the corners itself, and saved without an alpha channel,
# which the App Store rejects in app icons.
. (Join-Path $PSScriptRoot 'IconArt.ps1')
$iconDir = Join-Path $PSScriptRoot '..\..\ios\Nagster\Assets.xcassets\AppIcon.appiconset'

$art = New-IconBitmap 1024
$opaque = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$graphics = [System.Drawing.Graphics]::FromImage($opaque)
$graphics.DrawImage($art, 0, 0, 1024, 1024)
$graphics.Dispose()
$opaque.Save((Join-Path $iconDir 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$opaque.Dispose()
$art.Dispose()
