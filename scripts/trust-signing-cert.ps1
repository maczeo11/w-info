# One-time, administrator-level setup: puts the TempFanTile signing
# certificate in the machine store's Trusted People, where Windows expects
# it for New-Sideloading. After this succeeds, install.ps1 (normal user)
# can Add-AppxPackage without further elevation.

# Requires elevation — fail fast if not.
$isAdmin = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          | ForEach-Object { $_.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not $isAdmin) {
  Write-Error "Run elevated (right-click Start > Terminal (Admin), then:  powershell -File $PSCommandPath)"
  exit 1
}

$root = Split-Path -Parent $PSScriptRoot
$certFile = Get-ChildItem $root -Filter "TempFanWidget_*.cer" | Select-Object -First 1
if (-not $certFile) { throw "No TempFanWidget_*.cer found next to the MSIX — run scripts\package.ps1 first." }

Write-Host "Importing $($certFile.FullName) into LocalMachine\TrustedPeople..."
Import-Certificate -FilePath $certFile.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

Write-Host "Trusted People updated. Now run scripts\install.ps1 (no elevation)."
