# Installs the freshly built MSIX for the current user (no elevation
# needed once trust-signing-cert.ps1 imported the signing certificate).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msix = Get-ChildItem $root -Filter "TempFanWidget_*.msix" | Select-Object -First 1
if (-not $msix) { throw "No TempFanWidget_*.msix found — run scripts\package.ps1 first." }

Write-Host "Installing $($msix.FullName) ..."
Add-AppxPackage -Path $msix.FullName -ErrorAction Stop
Get-AppxPackage TempFanWidget | Select-Object Name, Version, InstallLocation
Write-Host ""
Write-Host "Installed. Open the Widgets Board (Win + W) and pin 'Temp & Fan'."
