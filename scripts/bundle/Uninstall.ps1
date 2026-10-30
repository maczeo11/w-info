<#
.SYNOPSIS
  Removes W-INFO that Install.ps1 set up. One UAC prompt.

.DESCRIPTION
  Removes the app and the certificate trust. With -RemoveSensors it also removes the logon
  task and stops LibreHardwareMonitor. PawnIO (a shared driver other tools may use) and the
  LibreHardwareMonitor files are left alone unless you also pass -RemoveLhm.

.EXAMPLE
  .\Uninstall.ps1 -WhatIf
  .\Uninstall.ps1 -RemoveSensors -RemoveLhm
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [switch]$RemoveSensors,
  [switch]$RemoveLhm,
  [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$PackageName = 'TempFanWidget'
$CertSubject = 'CN=TempFanWidget'

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
  if (-not $PSCmdlet.ShouldProcess('this script', 'relaunch elevated to uninstall')) {
    Write-Host "(WhatIf) would remove the app '$PackageName' and the '$CertSubject' certificate trust$(if ($RemoveSensors) { ', the logon task and LibreHardwareMonitor autostart' })."
    exit 0
  }
  $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
  if ($RemoveSensors) { $argList += '-RemoveSensors' }
  if ($RemoveLhm)     { $argList += '-RemoveLhm' }
  try {
    $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait -PassThru
    exit $p.ExitCode
  } catch {
    Write-Host "Elevation was cancelled or failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
  }
}

try {
  Write-Host '==> Removing the app' -ForegroundColor Cyan
  $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
  if ($pkg -and $PSCmdlet.ShouldProcess($pkg.PackageFullName, 'Remove-AppxPackage')) { $pkg | Remove-AppxPackage; Write-Host '    [ok]   removed' -ForegroundColor Green }
  elseif (-not $pkg) { Write-Host '    [ok]   not installed' -ForegroundColor Green }

  Write-Host '==> Removing the certificate trust' -ForegroundColor Cyan
  $certs = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq $CertSubject }
  foreach ($c in $certs) {
    if ($PSCmdlet.ShouldProcess($c.Thumbprint, 'remove from LocalMachine\TrustedPeople')) { Remove-Item $c.PSPath; Write-Host "    [ok]   removed $($c.Thumbprint)" -ForegroundColor Green }
  }

  if ($RemoveSensors) {
    Write-Host '==> Removing the accurate-sensor setup' -ForegroundColor Cyan
    $extra = @{ NoPause = $true }
    if ($RemoveLhm) { $extra.RemoveLhm = $true }
    & (Join-Path $here 'setup-accurate-sensors.ps1') -Uninstall @extra
  }
  $code = 0
} catch {
  Write-Host "    [!!]   $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}
if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
exit $code
