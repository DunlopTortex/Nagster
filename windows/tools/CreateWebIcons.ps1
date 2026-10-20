# Builds the web interface icons from the shared artwork in IconArt.ps1. The home-screen icon is a full-bleed,
# opaque square because iOS rounds the corners itself and fills transparency with black.
. (Join-Path $PSScriptRoot 'IconArt.ps1')
$webDir = Join-Path $PSScriptRoot '..\DGNaggingAlarms\Web'

$touch = New-IconBitmap 180
$touch.Save((Join-Path $webDir 'apple-touch-icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$touch.Dispose()

# Browser tabs show icons on their own background, so the favicon uses the rounded tile like the desktop icon.
$favicon = New-IconBitmap 32 -Rounded
$favicon.Save((Join-Path $webDir 'favicon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$favicon.Dispose()
