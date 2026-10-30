# Generates the MSIX tile icons by scaling ONE master image, so the packaged
# app always shows the brand and re-branding is a one-file change:
#
#   replace Assets\logo.png (square PNG, 512 px or larger, transparent
#   corners) and run package.ps1 (or this script) — every tile follows.
#
# package.ps1 runs this into the publish layout on every build. It used to
# draw its own thermometer-and-fan art here, which silently overwrote the
# committed logo in the packaged app.

param(
  # Folder the PNGs land in.
  $OutDir = (Join-Path $PSScriptRoot "..\Assets"),

  # The brand master every tile is scaled from.
  $Source = (Join-Path $PSScriptRoot "..\Assets\logo.png"),

  # Manifest-referenced assets: name → rendered size in pixels.
  # Match this to Package.appxmanifest; add entries here if the manifest grows.
  $Sizes = @{
    "StoreLogo.png"        = 50
    "Square44x44Logo.png"  = 44
    "Square150x150Logo.png" = 150
  }
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Source)) {
  throw "Brand master not found: $Source (expected a square PNG)."
}

# Load via a stream copy so the master isn't left locked while we write.
$master = [System.Drawing.Image]::FromStream([System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Source).Path)))
if ($master.Width -ne $master.Height) {
  Write-Warning "Master is $($master.Width)x$($master.Height), not square; tiles will be stretched."
}

<#
.SYNOPSIS
  Scales the master to one square size with the highest-quality resampler.
#>
function New-TileBitmap([System.Drawing.Image]$image, [int]$size) {
  $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($image, 0, 0, $size, $size)
  $g.Dispose()
  return $bmp
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($entry in $Sizes.GetEnumerator()) {
  $bmp = New-TileBitmap $master ([int]$entry.Value)
  $file = Join-Path $OutDir $entry.Key
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "Generated $file"
}
$master.Dispose()
