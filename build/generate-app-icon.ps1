param(
    [string]$OutputDirectory = "..\CrabDesk.App\Assets"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$output = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
[System.IO.Directory]::CreateDirectory($output) | Out-Null

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-CrabDeskPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $inset = [Math]::Max(1.0, $Size * 0.035)
        $outer = [System.Drawing.RectangleF]::new($inset, $inset, $Size - 2 * $inset, $Size - 2 * $inset)
        $outerPath = New-RoundedPath $outer ([float]($Size * 0.21))
        $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 31, 35, 41))
        try {
            $graphics.FillPath($background, $outerPath)
        }
        finally {
            $background.Dispose()
            $outerPath.Dispose()
        }

        $coral = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 105, 91))
        $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 80, 177, 216))
        try {
            $tiles = @(
                @{ X = 0.20; Y = 0.22; W = 0.27; H = 0.57; Brush = $coral },
                @{ X = 0.53; Y = 0.22; W = 0.27; H = 0.25; Brush = $blue },
                @{ X = 0.53; Y = 0.53; W = 0.27; H = 0.26; Brush = $coral }
            )
            foreach ($tile in $tiles) {
                $bounds = [System.Drawing.RectangleF]::new(
                    [float]($Size * $tile.X),
                    [float]($Size * $tile.Y),
                    [float]($Size * $tile.W),
                    [float]($Size * $tile.H))
                $path = New-RoundedPath $bounds ([float]([Math]::Max(1, $Size * 0.055)))
                try {
                    $graphics.FillPath($tile.Brush, $path)
                }
                finally {
                    $path.Dispose()
                }
            }
        }
        finally {
            $coral.Dispose()
            $blue.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    [PSCustomObject]@{ Size = $size; Bytes = (New-CrabDeskPng $size) }
}

$iconPath = Join-Path $output "CrabDesk.ico"
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]$(if ($frame.Size -eq 256) { 0 } else { $frame.Size }))
        $writer.Write([byte]$(if ($frame.Size -eq 256) { 0 } else { $frame.Size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

[System.IO.File]::WriteAllBytes((Join-Path $output "CrabDesk-256.png"), ($frames | Where-Object Size -eq 256).Bytes)
Write-Host "Generated $iconPath"
