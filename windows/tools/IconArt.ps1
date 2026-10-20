# Shared icon artwork: a white clock face on the app's accent purple (#7C6CF2). Dot-sourced by
# CreateIcon.ps1 (desktop .ico) and CreateWebIcons.ps1 (web and iPhone home screen) so they always match.
Add-Type -AssemblyName System.Drawing

# Returns a $size x $size bitmap. -Rounded gives a rounded tile on a transparent background (Windows);
# without it the tile is a full-bleed opaque square, because iOS rounds corners itself and fills transparency black.
function New-IconBitmap([int]$size, [switch]$Rounded) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # Everything is drawn on a 64-unit grid and scaled.
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $tile = [System.Drawing.RectangleF]::new(0, 0, 64, 64)
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new($tile,
        [System.Drawing.Color]::FromArgb(142, 128, 244), [System.Drawing.Color]::FromArgb(88, 70, 214), 45.0)
    if ($Rounded) {
        # Inset by a unit so anti-aliased edges aren't clipped; radius matches Windows 11's rounded tiles.
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $radius = 14; $left = 1; $top = 1; $right = 63; $bottom = 63
        $path.AddArc($left, $top, $radius, $radius, 180, 90)
        $path.AddArc($right - $radius, $top, $radius, $radius, 270, 90)
        $path.AddArc($right - $radius, $bottom - $radius, $radius, $radius, 0, 90)
        $path.AddArc($left, $bottom - $radius, $radius, $radius, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($background, $path)
        $path.Dispose()
    }
    else {
        $graphics.FillRectangle($background, $tile)
    }

    $ink = [System.Drawing.Color]::FromArgb(48, 40, 120)
    $face = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(250, 250, 255))
    $hub = [System.Drawing.SolidBrush]::new($ink)
    # Small icons get a larger face and bolder hands so the clock still reads at 16 pixels.
    $small = $size -lt 32
    $faceSize = if ($Rounded) { if ($small) { 46 } else { 42 } } else { 36 }
    $hand = [System.Drawing.Pen]::new($ink, $(if ($small) { 5.5 } else { 3.6 }))
    $hand.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hand.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $faceOffset = 32 - $faceSize / 2
    $graphics.FillEllipse($face, $faceOffset, $faceOffset, $faceSize, $faceSize)
    $reach = $faceSize / 2
    if (-not $small) {
        $tick = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 140, 210), 2.2)
        $tick.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $tick.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($angle in 0, 90, 180, 270) {
            $radians = $angle * [Math]::PI / 180
            $graphics.DrawLine($tick, 32 + ($reach - 4) * [Math]::Sin($radians), 32 - ($reach - 4) * [Math]::Cos($radians),
                                      32 + ($reach - 2) * [Math]::Sin($radians), 32 - ($reach - 2) * [Math]::Cos($radians))
        }
        $tick.Dispose()
    }
    # Hands at ten past two, as on the original icon.
    $graphics.DrawLine($hand, 32, 32, 32, 32 - $reach * 0.62)
    $graphics.DrawLine($hand, 32, 32, 32 + $reach * 0.5, 32 + $reach * 0.22)
    $hubSize = if ($small) { 7 } else { 5 }
    $graphics.FillEllipse($hub, 32 - $hubSize / 2, 32 - $hubSize / 2, $hubSize, $hubSize)

    $graphics.Dispose(); $background.Dispose(); $face.Dispose(); $hub.Dispose(); $hand.Dispose()
    return $bitmap
}
