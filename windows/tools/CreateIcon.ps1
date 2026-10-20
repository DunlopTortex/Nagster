# Builds the desktop icon (Assets\Alarm.ico) from the shared artwork in IconArt.ps1.
. (Join-Path $PSScriptRoot 'IconArt.ps1')
$assetDir = Join-Path $PSScriptRoot '..\Nagster\Assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

# 24 and 64 cover Windows display scaling at 150% and 200%; each size is drawn natively rather than shrunk.
$layers = @()
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = New-IconBitmap $size -Rounded
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $layers += ,@($size, $stream.ToArray())
    $stream.Dispose(); $bitmap.Dispose()
}

# ICO container: header, one directory entry per size, then the PNG images.
$path = Join-Path $assetDir 'Alarm.ico'
$file = [System.IO.File]::Create($path)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$layers.Count)
$offset = 6 + 16 * $layers.Count
foreach ($layer in $layers) {
    $size = [int]$layer[0]; $bytes = [byte[]]$layer[1]
    # A size byte of 0 means 256.
    $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$bytes.Length); $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($layer in $layers) { $writer.Write([byte[]]$layer[1]) }
$writer.Dispose(); $file.Dispose()
Write-Output $path
