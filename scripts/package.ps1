# Produces a signed MSIX without Visual Studio.
#
#   1. dotnet publish            — compiles app + self-contained Windows App SDK
#   2. make-icons                — regenerates Assets PNGs (idempotent, skipped here — run via make-icons.ps1)
#   3. makepri createconfig/new  — resources.pri for the tile assets
#   4. makeappx pack             — manifest + files -> TempFanWidget.msix
#   5. signtool sign             — signed by the CN=TempFanWidget dev cert
#
# The dev certificate is created on first run if missing. Its *public* part
# is exported next to the MSIX so trust-signing-cert.ps1 (elevated, one time)
# can put it in the machine's Trusted People store, enabling Add-AppxPackage.

param(
  [string]$Configuration = "Release",

  # Stop after building the publish\ layout: no MSIX, no signing, and the
  # certificate store is never touched. Enough for the local test loop:
  #   .\scripts\package.ps1 -LayoutOnly
  #   Add-AppxPackage -Register publish\AppxManifest.xml
  [switch]$LayoutOnly,

  # Where the layout is built (default: publish\, the folder the dev package is
  # registered from) and where the .msix/.cer land (default: the repo root).
  # make-release.ps1 points both at dist\ so a release build never touches the
  # layout your Widgets Board is running.
  [string]$LayoutDir,
  [string]$OutDir,

  # Identity of the self-signed cert used for signing. Must match the
  # Publisher CN in Package.appxmanifest exactly.
  [string]$CertSubject = "CN=TempFanWidget",

  # RFC3161 timestamp server — without /tr, signatures die when the cert expires.
  [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$layout    = if ($LayoutDir) { $LayoutDir } else { Join-Path $root "publish" }
$outDir    = if ($OutDir) { $OutDir } else { $root }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$manifest  = Join-Path $root "Package.appxmanifest"
$buildTools = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools") -Directory |
              Sort-Object -Property Name -Descending | Select-Object -First 1
$bin       = Join-Path $buildTools.FullName "bin"
$sdkBin    = Get-ChildItem $bin -Directory | Sort-Object -Property Name -Descending | Select-Object -First 1
$tools     = @{
  makepri = Join-Path $sdkBin.FullName "x64\makepri.exe"
  makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
  signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"
}

# Read identity/version straight from the manifest, so naming stays in sync.
$xml = [xml](Get-Content $manifest -Raw)
$name    = $xml.Package.Identity.Name
$version = $xml.Package.Identity.Version
$msix    = Join-Path $outDir "$name`_$version`_x64.msix"

Write-Host "==> dotnet publish"
# Name the project explicitly: the repo root also holds W-INFO.sln, and
# `dotnet publish` refuses to guess between two project files (MSB1011).
dotnet publish (Join-Path $root "TempFanWidget.csproj") -c $Configuration -p:Platform=x64 -o $layout
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

Write-Host "==> copy manifest + assets into layout"
Copy-Item $manifest (Join-Path $layout "AppxManifest.xml") -Force

# Run icon generation directly into the layout so no stale assets are packed.
& (Join-Path $PSScriptRoot "make-icons.ps1") -OutDir (Join-Path $layout "Assets")
if ($LASTEXITCODE -ne 0) { throw "icon generation failed" }

# — makepri: index our file-based assets into a single resources.pri —
#   Because Windows App SDK self-contained files include their own .pri
#   artifacts, temporarily set aside every .pri except the fresh output.
$priConfig = Join-Path $root "package-logs\priconfig.xml"
$priArchive = Join-Path $root "publish.pri-archive"
Write-Host "==> makepri"
New-Item -ItemType Directory -Force -Path (Split-Path $priConfig) | Out-Null
if (Test-Path $priArchive) { Remove-Item $priArchive -Recurse -Force }
New-Item -ItemType Directory -Force -Path $priArchive | Out-Null
Get-ChildItem $layout -Filter *.pri | ForEach-Object {
  Move-Item $_.FullName (Join-Path $priArchive $_.Name)
} -ErrorAction SilentlyContinue

& $tools.makepri createconfig /cf $priConfig /dq en-US_scale-100 /pv 10.0.0 /o
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
& $tools.makepri new /pr $layout /cf $priConfig /of (Join-Path $layout "resources.pri") /in $name /o
if ($LASTEXITCODE -ne 0) { throw "makepri new failed" }

# Restore Windows App SDK resource maps alongside our fresh resources.pri.
Get-ChildItem $priArchive -Filter *.pri | ForEach-Object {
  Move-Item $_.FullName (Join-Path $layout $_.Name) -Force
}
Remove-Item $priArchive -Recurse -Force

if ($LayoutOnly) {
  Write-Host ""
  Write-Host "Layout ready (unsigned, not packed): $layout"
  Write-Host "Register it with:  Add-AppxPackage -Register `"$layout\AppxManifest.xml`""
  return
}

# — makeappx: pack the layout —
Write-Host "==> makeappx"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $tools.makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

# — signing cert: create once, reuse forever —
Write-Host "==> signing"
# Only reuse a cert that carries the Code Signing EKU; one minted without it
# (an older version of this script) is invisible to signtool and would make
# every later run fail with "No certificates were found".
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey -and
                       $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3" } |
        Sort-Object -Property NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
  Write-Host "    creating self-signed dev certificate $CertSubject"
  # TextExtension adds the Code-Signing EKU + a non-CA basicConstraints —
  # without them signtool filters the cert out ("no certificates met criteria").
  $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
          -KeyUsage DigitalSignature -FriendlyName "TempFanWidget packaging" `
          -KeyExportPolicy Exportable `
          -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
          -NotAfter (Get-Date).AddYears(5) -CertStoreLocation Cert:\CurrentUser\My
}
# Export the *public* certificate for the trust one-time installer.
$certPath = [System.IO.Path]::ChangeExtension($msix, ".cer")
Export-Certificate -Cert $cert -FilePath $certPath | Out-Null

# Select the cert by thumbprint. `/n "CN=TempFanWidget"` never matches (signtool's /n
# compares the bare common name, not the "CN=" form), and several certs may share a subject.
& $tools.signtool sign /sha1 $cert.Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

Write-Host ""
Write-Host "Done:"
Write-Host "  $msix"
Write-Host "  $certPath  (run trust-signing-cert.ps1 as admin once)"
