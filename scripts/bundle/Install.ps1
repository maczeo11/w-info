<#
.SYNOPSIS
  Installs W-INFO from this release bundle. One UAC prompt, about a minute.

.DESCRIPTION
  1. Verifies the bundle: file hashes, and that the app package really is signed by the
     certificate shipped next to it.
  2. Shows you the certificate and asks before trusting it (a self-signed publisher cannot
     be installed any other way; this is why Windows needs the extra step).
  3. Trusts the certificate for this PC (LocalMachine\TrustedPeople) and installs the app.
  4. Sets up ACCURATE CPU temperature and fan speeds (LibreHardwareMonitor + PawnIO, from
     their official winget packages). Skip with -SkipAccurateSensors; you can run
     setup-accurate-sensors.ps1 later.

  Afterwards: Win+W, click +, pick "Temp & Fan".

.PARAMETER SkipAccurateSensors
  Install only the widget. CPU temperature then comes from the firmware's ACPI zone and is
  labelled "EST." on the card.

.PARAMETER Yes
  Don't ask before trusting the certificate.

.EXAMPLE
  .\Install.ps1 -WhatIf      # verify the bundle and show the plan, change nothing
  .\Install.ps1              # install
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [switch]$SkipAccurateSensors,
  [switch]$Yes,

  # Internal: set when this script relaunches itself elevated.
  [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Write-Step([string]$t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Write-Ok([string]$t)   { Write-Host "    [ok]   $t" -ForegroundColor Green }
function Write-Warn2([string]$t){ Write-Host "    [warn] $t" -ForegroundColor Yellow }

try {
  $msix = Get-ChildItem $here -Filter *.msix | Select-Object -First 1
  $cer  = Get-ChildItem $here -Filter *.cer  | Select-Object -First 1
  if (-not $msix -or -not $cer) { throw 'The .msix and .cer files must sit next to this script. Extract the whole zip first.' }

  # --- 1. verify the bundle (read-only, runs in every mode) -------------------------------
  Write-Step 'Verifying the bundle'
  # Windows PowerShell 5.1's Get-FileHash honours an ambient -WhatIf and then returns nothing,
  # which would read as a hash mismatch. Verification is read-only, so run it with WhatIf off.
  $userWhatIf = $WhatIfPreference
  $WhatIfPreference = $false
  $sums = Join-Path $here 'SHA256SUMS.txt'
  if (Test-Path $sums) {
    foreach ($line in Get-Content $sums) {
      if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+)$') {
        $file = Join-Path $here $Matches[2].Trim()
        if (-not (Test-Path $file)) { throw "Missing file listed in SHA256SUMS.txt: $($Matches[2])" }
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $Matches[1].ToUpper()) { throw "Hash mismatch: $($Matches[2]). The download is corrupt or has been tampered with." }
      }
    }
    Write-Ok 'file hashes match SHA256SUMS.txt'
  } else {
    Write-Warn2 'no SHA256SUMS.txt in the bundle; skipping the hash check'
  }

  $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer.FullName)
  $sig  = Get-AuthenticodeSignature -FilePath $msix.FullName
  if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
    throw 'The app package is NOT signed by the bundled certificate. Do not install it.'
  }
  Write-Ok 'the package is signed by the bundled certificate'
  $WhatIfPreference = $userWhatIf

  Write-Host ''
  Write-Host '  Publisher   : ' $cert.Subject
  Write-Host '  Thumbprint  : ' $cert.Thumbprint
  Write-Host '  Valid until : ' $cert.NotAfter.ToString('yyyy-MM-dd')
  Write-Host '  (compare the thumbprint with the one on the release page)'
  Write-Host ''

  if ($WhatIfPreference) {
    Write-Host '(WhatIf) next: confirm, relaunch elevated (one UAC prompt), trust the certificate,'
    Write-Host "         Add-AppxPackage $($msix.Name)$(if (-not $SkipAccurateSensors) { ', then run setup-accurate-sensors.ps1' })."
    exit 0
  }

  # --- 2. consent, in the user's own window, before any elevation -------------------------
  if (-not $Yes -and -not $Elevated) {
    $answer = Read-Host 'Trust this publisher on this PC and install W-INFO? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled. Nothing was changed.'; exit 1 }
  }

  # --- 3. one elevation ------------------------------------------------------------------
  if (-not (Test-Admin)) {
    Write-Step 'Elevating once (a UAC prompt will appear)'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Yes')
    if ($SkipAccurateSensors) { $argList += '-SkipAccurateSensors' }
    try {
      $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait -PassThru
      exit $p.ExitCode
    } catch {
      throw "Elevation was cancelled or failed: $($_.Exception.Message)"
    }
  }

  Write-Step 'Trusting the signing certificate for this PC'
  Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  Write-Ok 'added to LocalMachine\TrustedPeople'

  Write-Step 'Installing W-INFO'
  $existing = Get-AppxPackage -Name ($msix.BaseName -replace '_.*$', '') -ErrorAction SilentlyContinue
  if ($existing -and $existing.IsDevelopmentMode) {
    throw "A development copy is registered ($($existing.InstallLocation)). Remove it first:  Get-AppxPackage $($existing.Name) | Remove-AppxPackage"
  }
  Add-AppxPackage -Path $msix.FullName
  Write-Ok 'installed'

  if (-not $SkipAccurateSensors) {
    Write-Step 'Setting up accurate CPU temperature and fans'
    try {
      & (Join-Path $here 'setup-accurate-sensors.ps1') -NoPause
    } catch {
      Write-Warn2 "accurate-sensor setup did not finish: $($_.Exception.Message)"
      Write-Warn2 'The widget works without it (CPU temp is labelled EST.). Retry later: .\setup-accurate-sensors.ps1'
    }
  }

  Write-Host ''
  Write-Host 'Done. Press Win+W, click +, and pin "Temp & Fan".' -ForegroundColor Green
  $code = 0
} catch {
  Write-Host "    [!!]   $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}
if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
exit $code
