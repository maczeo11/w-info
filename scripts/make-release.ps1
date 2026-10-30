<#
.SYNOPSIS
  Builds the GitHub release bundle: the signed app package plus installer scripts, in one zip.

.DESCRIPTION
  1. builds + signs the MSIX into dist\ (package.ps1 -LayoutDir/-OutDir: your dev layout in
     publish\ is never touched),
  2. stages it with Install.ps1 / Uninstall.ps1 / setup-accurate-sensors.ps1, the licence and
     third-party notices, and a SHA256SUMS.txt that Install.ps1 verifies before trusting anything,
  3. zips it to dist\W-INFO-vX.Y.Z-setup.zip (+ .sha256) and writes dist\release-notes.md.

  Nothing third-party is bundled: LibreHardwareMonitor (MPL-2.0) and PawnIO (proprietary
  freeware, not redistributable) are fetched from their official winget packages by the
  installer.

  Local build by default. -Publish additionally creates a DRAFT GitHub release with the zip
  attached; a draft is invisible to everyone else until you click Publish on GitHub.

.PARAMETER Publish
  Create (or update) a draft release on the repo's GitHub remote with gh.

.EXAMPLE
  .\scripts\make-release.ps1              # build only, look at dist\
  .\scripts\make-release.ps1 -Publish     # build, then create the draft release
#>
[CmdletBinding()]
param(
  [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$dist     = Join-Path $root 'dist'
$build    = Join-Path $dist 'build'
$manifest = [xml](Get-Content (Join-Path $root 'Package.appxmanifest') -Raw)
$name     = $manifest.Package.Identity.Name
$version  = $manifest.Package.Identity.Version          # 1.0.0.0
$tag      = 'v' + ($version -replace '\.0$', '')        # v1.0.0
$stageName = "W-INFO-$tag-setup"
$stage    = Join-Path $dist $stageName
$zip      = Join-Path $dist "$stageName.zip"

Write-Host "==> Release $tag ($name $version)" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $build, $stage | Out-Null

Write-Host '==> Building and signing the package' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'package.ps1') -LayoutDir (Join-Path $build 'layout') -OutDir $build
$msix = Join-Path $build "$name`_$version`_x64.msix"
$cer  = [System.IO.Path]::ChangeExtension($msix, '.cer')
if (-not (Test-Path $msix) -or -not (Test-Path $cer)) { throw 'package.ps1 did not produce the .msix/.cer' }

Write-Host '==> Staging the bundle' -ForegroundColor Cyan
Copy-Item $msix, $cer $stage
Copy-Item (Join-Path $PSScriptRoot 'bundle\Install.ps1'), (Join-Path $PSScriptRoot 'bundle\Uninstall.ps1'), (Join-Path $PSScriptRoot 'bundle\README.txt') $stage
Copy-Item (Join-Path $PSScriptRoot 'setup-accurate-sensors.ps1') $stage
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

# Hashes Install.ps1 verifies before it trusts the certificate (format: HASH  file).
$sums = Get-ChildItem $stage -File | Sort-Object Name | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
}
[System.IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $sums, [System.Text.UTF8Encoding]::new($false))

Write-Host '==> Zipping' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText("$zip.sha256", "$zipHash  $stageName.zip`n", [System.Text.UTF8Encoding]::new($false))

$certObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
$notes = Join-Path $dist 'release-notes.md'
@"
## W-INFO $tag

Windows 11 widget for your **Widgets Board** (Win+W): CPU and GPU temperature, CPU load, network speed, fans.

### Install (about a minute, one UAC prompt)
1. Download **$stageName.zip** and extract it.
2. Right-click **Install.ps1** and choose *Run with PowerShell* (or run ``.\Install.ps1`` in a terminal).
3. Press **Win+W**, click **+**, pin **Temp & Fan**.

``Install.ps1`` verifies the bundle, shows you the signing certificate and asks before trusting it, installs the app,
and sets up accurate CPU temperature and fan speeds (LibreHardwareMonitor and the PawnIO driver, from their official
winget packages; nothing third-party is bundled here). Skip that part with ``-SkipAccurateSensors``; the widget still
works and labels its CPU temperature **EST.** until you set it up.

### Verify before you run
| | |
|---|---|
| ZIP SHA-256 | ``$zipHash`` |
| Signing certificate | ``$($certObj.Subject)`` |
| Certificate thumbprint | ``$($certObj.Thumbprint)`` |

The thumbprint ``Install.ps1`` prints must match the one above.

### Why the extra trust step?
The package is signed with a self-signed certificate, so Windows will not install it until that certificate is trusted.
A publicly trusted code-signing certificate (or the Microsoft Store) would remove the step; this release does not have one yet.

### Requirements
Windows 11 22H2+ (build 22000+), x64. Accurate sensors need administrator approval once (a kernel driver) and
[winget](https://learn.microsoft.com/windows/package-manager/winget/).

Uninstall: ``Uninstall.ps1`` (add ``-RemoveSensors`` to also remove the logon task).
"@ | Set-Content -Path $notes -Encoding UTF8

Write-Host ''
Write-Host 'Bundle contents:' -ForegroundColor Green
Get-ChildItem $stage -File | ForEach-Object { '  {0,-44} {1,10:N0} B' -f $_.Name, $_.Length }
Write-Host ''
Write-Host ("Zip      : {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256  : $zipHash"
Write-Host "Cert     : $($certObj.Thumbprint)  ($($certObj.Subject))"
Write-Host "Notes    : $notes"

if ($Publish) {
  Write-Host ''
  Write-Host "==> Creating DRAFT release $tag on GitHub" -ForegroundColor Cyan
  if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) not found.' }
  gh release create $tag $zip "$zip.sha256" --draft --title "W-INFO $tag" --notes-file $notes
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
  Write-Host 'Draft created. It is private until you press Publish on the release page.' -ForegroundColor Green
}
