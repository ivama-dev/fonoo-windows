# Convert the unchanged Fonoo artwork to Windows PNG and multi-resolution ICO assets.
# Source: macOS reference f3df743, macos/FonooMac/Assets.xcassets/AppIcon.appiconset/Fonoo.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetFolder = Join-Path $PSScriptRoot 'Fonoo.Windows\Assets'
$source = [System.Drawing.Image]::FromFile((Join-Path $assetFolder 'Fonoo.png'))
function Get-IconPng([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $size = [Math]::Min($width, $height)
        $graphics.DrawImage($source, [int](($width-$size)/2), [int](($height-$size)/2), $size, $size)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
function Save-IconPng([string]$name, [int]$width, [int]$height) {
    [System.IO.File]::WriteAllBytes((Join-Path $assetFolder $name), (Get-IconPng $width $height))
}
try {
    foreach ($scale in @(100,125,150,200,400)) {
        foreach ($base in @(44,150)) {
            $size = [int][Math]::Ceiling($base*$scale/100)
            Save-IconPng "Square${base}x${base}Logo.scale-$scale.png" $size $size
        }
    }
    foreach ($size in @(16,24,32,48,256)) {
        Save-IconPng "Square44x44Logo.targetsize-$size.png" $size $size
        Save-IconPng "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size
    }
    Save-IconPng 'StoreLogo.png' 50 50
    Save-IconPng 'LockScreenLogo.scale-200.png' 48 48
    Save-IconPng 'SplashScreen.scale-200.png' 1240 600
    Save-IconPng 'Wide310x150Logo.scale-200.png' 620 300
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $images = @($sizes | ForEach-Object { ,(Get-IconPng $_ $_) })
    $file = [System.IO.File]::Create((Join-Path $assetFolder 'Fonoo.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16*$sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $sizeByte = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$sizeByte); $writer.Write([byte]$sizeByte)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $source.Dispose() }
