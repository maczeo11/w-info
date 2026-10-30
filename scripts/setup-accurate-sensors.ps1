<#
.SYNOPSIS
  One-time setup for ACCURATE CPU temperature and fan speeds in the W-INFO widget.

.DESCRIPTION
  Windows exposes no real CPU die temperature or fan RPM to normal programs; reading them
  needs ring-0 access (an elevated process plus a kernel driver). The widget cannot be
  elevated, so it reads them from LibreHardwareMonitor (LHM) instead. This script makes that
  a single step, with one UAC prompt:

    1. installs LibreHardwareMonitor     (winget, portable, no admin needed for this part)
    2. installs the PawnIO driver        (winget, official; skipped when already present)
    3. turns LHM's web server on and starts it minimised to the tray (edits its .config)
    4. registers a logon task that starts LHM elevated with no UAC prompt at every login
    5. starts it now and shows what the widget will read

  Nothing is bundled: both components come from their official winget packages (PawnIO is
  proprietary freeware and may not be redistributed, so it is always fetched, never shipped).

  Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Check
  Read-only status report (no admin, no changes). Exit code 0 = the widget has accurate data.

.PARAMETER Uninstall
  Removes the logon task and stops LHM. Leaves PawnIO alone (other tools use it).

.PARAMETER RemoveLhm
  With -Uninstall: also uninstalls LibreHardwareMonitor.

.PARAMETER NoStartup
  Install and configure, but do not register the logon task.

.EXAMPLE
  .\setup-accurate-sensors.ps1 -WhatIf     # show the plan, change nothing
  .\setup-accurate-sensors.ps1 -Check      # where do things stand?
  .\setup-accurate-sensors.ps1             # do it (one UAC prompt)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [switch]$Check,
  [switch]$Uninstall,
  [switch]$RemoveLhm,
  [switch]$NoStartup,

  # Don't wait for Enter at the end of an elevated window (used when Install.ps1 calls this script).
  [switch]$NoPause,

  # Internal: set when this script relaunches itself elevated.
  [switch]$Elevated,
  [string]$LhmExe,
  [string]$TaskUser
)

$ErrorActionPreference = 'Stop'

$LhmId    = 'LibreHardwareMonitor.LibreHardwareMonitor'
$PawnId   = 'namazso.PawnIO'
$TaskName = 'W-INFO LibreHardwareMonitor'
$Endpoint = 'http://127.0.0.1:8085/data.json'   # must match LibreHardwareMonitorSource.cs

# ----------------------------------------------------------------------------- helpers

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    [ok]   $text" -ForegroundColor Green }
function Write-Warn2([string]$text){ Write-Host "    [warn] $text" -ForegroundColor Yellow }
function Write-Bad([string]$text)  { Write-Host "    [!!]   $text" -ForegroundColor Red }

function Get-LhmExePath {
  # winget unpacks the portable zip under %LOCALAPPDATA%\Microsoft\WinGet\Packages\<id>_*\
  $pkgRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
  if (Test-Path $pkgRoot) {
    $hit = Get-ChildItem $pkgRoot -Directory -Filter "$LhmId*" -ErrorAction SilentlyContinue |
           ForEach-Object { Get-ChildItem $_.FullName -Filter 'LibreHardwareMonitor.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue } |
           Select-Object -First 1
    if ($hit) { return $hit.FullName }
  }
  $running = Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($running -and $running.Path) { return $running.Path }
  return $null
}

function Get-PawnIoInstalled { [bool](Get-Service -Name PawnIO -ErrorAction SilentlyContinue) }

function Get-Feed {
  # What the widget will see: parse LHM's data.json the same way LibreHardwareMonitorSource does.
  try {
    $json = Invoke-RestMethod -Uri $Endpoint -TimeoutSec 4 -ErrorAction Stop
  } catch { return $null }

  $sensors = New-Object System.Collections.ArrayList
  $walk = $null
  $walk = {
    param($node)
    if ($node.PSObject.Properties['SensorId'] -and $node.PSObject.Properties['Type'] -and $node.PSObject.Properties['RawValue']) {
      [void]$sensors.Add($node)
    }
    if ($node.PSObject.Properties['Children']) { foreach ($c in @($node.Children)) { & $walk $c } }
  }
  & $walk $json

  # RawValue is a JSON number, which PowerShell may parse as Int32/Double/Decimal; a "NaN" literal arrives as a string.
  $cpu = @($sensors | Where-Object { $_.Type -eq 'Temperature' -and $_.SensorId -match '^/(amd|intel)cpu/' -and $_.Text -notmatch 'distance' -and $null -ne $_.RawValue -and $_.RawValue -isnot [string] })
  $preferred = @($cpu | Where-Object { $_.Text -match 'package|tctl|tdie' })
  $pick = if ($preferred.Count -gt 0) { $preferred } else { $cpu }
  $cpuTemp = if ($pick.Count -gt 0) { ($pick | Measure-Object -Property RawValue -Maximum).Maximum } else { $null }
  $fans = @($sensors | Where-Object { $_.Type -eq 'Fan' -and $_.RawValue -isnot [string] -and $null -ne $_.RawValue -and $_.RawValue -gt 0 })
  [pscustomobject]@{ CpuTemp = $cpuTemp; Fans = $fans; Version = $json.Version }
}

function Wait-Feed([int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    $f = Get-Feed
    if ($f -and $null -ne $f.CpuTemp) { return $f }
    Start-Sleep -Seconds 2
  }
  return (Get-Feed)
}

function Show-Status {
  Write-Step 'W-INFO accurate-sensor status'
  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if ($winget) { Write-Ok "winget available" } else { Write-Bad "winget not found (install 'App Installer' from the Microsoft Store)" }

  if (Get-PawnIoInstalled) { Write-Ok "PawnIO driver installed" } else { Write-Warn2 "PawnIO driver not installed (needed for CPU temperature on AMD/Intel)" }

  $exe = Get-LhmExePath
  if ($exe) { Write-Ok "LibreHardwareMonitor: $exe" } else { Write-Warn2 "LibreHardwareMonitor not installed" }

  $proc = Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue
  if ($proc) { Write-Ok "LibreHardwareMonitor is running" } else { Write-Warn2 "LibreHardwareMonitor is not running" }

  $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($task) { Write-Ok "logon task '$TaskName' registered ($($task.State))" } else { Write-Warn2 "no logon task, LHM will not start by itself at login" }

  $feed = Get-Feed
  if (-not $feed) {
    Write-Bad "web server not reachable at $Endpoint"
    Write-Host "         (LHM: Options > Remote Web Server > Run)"
    return $false
  }
  Write-Ok "web server reachable (LHM $($feed.Version))"
  if ($null -ne $feed.CpuTemp) {
    Write-Ok ("widget will show CPU {0:N1} C (real die temperature, no 'EST.' label)" -f $feed.CpuTemp)
  } else {
    Write-Warn2 "LHM is up but reports no CPU temperature: PawnIO missing, or LHM not elevated"
  }
  if ($feed.Fans.Count -gt 0) {
    Write-Ok ("fans: " + (($feed.Fans | ForEach-Object { "{0} {1:N0} rpm" -f $_.Text, $_.RawValue }) -join ', '))
  } else {
    Write-Warn2 "no fan RPM reported (common on laptops: the embedded controller owns the fan)"
  }
  return ($null -ne $feed.CpuTemp)
}

function Invoke-Elevated([string[]]$extra) {
  $hostExe = (Get-Process -Id $PID).Path
  $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated') + $extra
  try {
    $p = Start-Process -FilePath $hostExe -ArgumentList $argList -Verb RunAs -Wait -PassThru
    return $p.ExitCode
  } catch {
    # Declining the UAC prompt surfaces as "The operation was canceled by the user".
    Write-Bad "Elevation was cancelled or failed: $($_.Exception.Message)"
    return 1
  }
}

# winget exit codes that mean "nothing to do", not "failed".
$WingetAlreadyInstalled = -1978335135   # APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED
$WingetNoUpgrade        = -1978335189   # APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE

# ----------------------------------------------------------------------------- modes

if ($Check) {
  $ok = Show-Status
  if (-not $ok) {
    Write-Host ""
    Write-Host "Next step: run  .\scripts\setup-accurate-sensors.ps1  (one UAC prompt)." -ForegroundColor Yellow
  }
  exit ([int](-not $ok))
}

if ($Uninstall) {
  if (-not (Test-Admin)) {
    if ($PSCmdlet.ShouldProcess('this script', 'relaunch elevated to uninstall')) {
      $extra = @('-Uninstall')
      if ($RemoveLhm) { $extra += '-RemoveLhm' }
      exit (Invoke-Elevated $extra)
    }
    Write-Host "(WhatIf) would remove task '$TaskName', stop LibreHardwareMonitor$(if ($RemoveLhm) { ', uninstall it' }); PawnIO is kept."
    exit 0
  }
  Write-Step 'Removing W-INFO sensor setup'
  if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($TaskName, 'unregister scheduled task')) {
      Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
      Write-Ok "task removed"
    }
  }
  if ($PSCmdlet.ShouldProcess('LibreHardwareMonitor', 'stop process')) {
    Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
  }
  if ($RemoveLhm -and $PSCmdlet.ShouldProcess($LhmId, 'winget uninstall')) {
    winget uninstall --id $LhmId -e --silent
  }
  Write-Ok "done (PawnIO left in place)"
  if ($Elevated -and -not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
  exit 0
}

# ----------------------------------------------------------------------------- setup

# Stage 2: elevated work. Reached when launched as admin, or relaunched by stage 1.
if (Test-Admin) {
  if (-not $LhmExe) { $LhmExe = Get-LhmExePath }
  if (-not $TaskUser) { $TaskUser = "$env:USERDOMAIN\$env:USERNAME" }

  try {
    if (-not $LhmExe -or -not (Test-Path $LhmExe)) { $LhmExe = Get-LhmExePath }
    if (-not $LhmExe) {
      # Started from an already-elevated shell (or by Install.ps1): stage 1 never ran, so install LHM here.
      Write-Step 'LibreHardwareMonitor'
      if ($PSCmdlet.ShouldProcess($LhmId, 'winget install (portable)')) {
        winget install --id $LhmId -e --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -notin 0, $WingetAlreadyInstalled, $WingetNoUpgrade) { throw "LibreHardwareMonitor install failed (winget exit $LASTEXITCODE)" }
        $LhmExe = Get-LhmExePath
        if (-not $LhmExe) { throw 'Installed, but LibreHardwareMonitor.exe could not be located.' }
        Write-Ok "installed: $LhmExe"
      } else {
        $LhmExe = '<LibreHardwareMonitor.exe>'   # WhatIf placeholder, only used in messages
      }
    }

    Write-Step 'Stopping LibreHardwareMonitor (it rewrites its config on exit)'
    if ($PSCmdlet.ShouldProcess('LibreHardwareMonitor', 'stop if running')) {
      Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
      Start-Sleep -Seconds 1
    }

    Write-Step 'PawnIO driver'
    if (Get-PawnIoInstalled) {
      Write-Ok 'already installed, leaving it alone'
    } elseif ($PSCmdlet.ShouldProcess($PawnId, 'winget install (kernel driver, official package)')) {
      winget install --id $PawnId -e --silent --accept-package-agreements --accept-source-agreements
      if ($LASTEXITCODE -notin 0, 3010, $WingetAlreadyInstalled) { throw "PawnIO install failed (winget exit $LASTEXITCODE)" }
      if ($LASTEXITCODE -eq 3010) { Write-Warn2 'PawnIO asked for a restart before it works' }
      Write-Ok 'installed'
    }

    Write-Step 'Configuring LibreHardwareMonitor (web server on, start minimised to tray)'
    $cfg = [System.IO.Path]::ChangeExtension($LhmExe, '.config')
    if ($PSCmdlet.ShouldProcess($cfg, 'set runWebServerMenuItem / startMinMenuItem / minTrayMenuItem = true')) {
      $doc = New-Object System.Xml.XmlDocument
      if (Test-Path $cfg) { $doc.Load($cfg) }
      else { $doc.LoadXml('<?xml version="1.0" encoding="utf-8"?><configuration><appSettings /></configuration>') }
      $settings = $doc.SelectSingleNode('/configuration/appSettings')
      if (-not $settings) { $settings = $doc.DocumentElement.AppendChild($doc.CreateElement('appSettings')) }
      # LHM reads booleans with a case-sensitive `== "true"`, so the value must be lower-case.
      foreach ($key in 'runWebServerMenuItem', 'startMinMenuItem', 'minTrayMenuItem') {
        $node = $settings.SelectSingleNode("add[@key='$key']")
        if (-not $node) { $node = $settings.AppendChild($doc.CreateElement('add')); $node.SetAttribute('key', $key) }
        $node.SetAttribute('value', 'true')
      }
      $doc.Save($cfg)
      Write-Ok "settings written to $cfg (other settings preserved)"
    }

    if (-not $NoStartup) {
      Write-Step "Registering the logon task '$TaskName' (runs elevated, no UAC prompt at login)"
      if ($PSCmdlet.ShouldProcess($TaskName, "register scheduled task for $TaskUser, highest privileges")) {
        $action    = New-ScheduledTaskAction -Execute $LhmExe -WorkingDirectory (Split-Path $LhmExe)
        $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $TaskUser
        $principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType Interactive -RunLevel Highest
        # Laptop-safe: don't refuse to start on battery, don't kill it when unplugged, no time limit.
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                 -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
          -Settings $set -Description 'Starts LibreHardwareMonitor elevated so the W-INFO widget can read real CPU temperature and fans.' -Force | Out-Null
        Write-Ok 'registered'
      }
    }

    Write-Step 'Starting LibreHardwareMonitor'
    if ($PSCmdlet.ShouldProcess($LhmExe, 'start (via the logon task when registered)')) {
      if (-not $NoStartup -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Start-ScheduledTask -TaskName $TaskName
      } else {
        Start-Process -FilePath $LhmExe -WorkingDirectory (Split-Path $LhmExe)
      }
    }

    if ($WhatIfPreference) { Write-Host "`n(WhatIf) no changes were made."; exit 0 }
    $feed = Wait-Feed 30
    if ($feed -and $null -ne $feed.CpuTemp) { Write-Ok ("live: CPU {0:N1} C" -f $feed.CpuTemp) }
    else { Write-Warn2 'LHM did not publish a CPU temperature within 30 s. Run with -Check for details.' }
    $code = 0
  }
  catch {
    Write-Bad $_.Exception.Message
    $code = 1
  }
  if ($Elevated -and -not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
  exit $code
}

# Stage 1: normal user. Do the parts that need no admin, then elevate once for the rest.
Write-Step 'LibreHardwareMonitor'
$exe = Get-LhmExePath
if ($exe) {
  Write-Ok "already installed: $exe"
} elseif ($PSCmdlet.ShouldProcess($LhmId, 'winget install (portable, per-user)')) {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw "winget not found. Install 'App Installer' from the Microsoft Store, then run this again." }
  winget install --id $LhmId -e --silent --accept-package-agreements --accept-source-agreements
  if ($LASTEXITCODE -notin 0, $WingetAlreadyInstalled, $WingetNoUpgrade) { throw "LibreHardwareMonitor install failed (winget exit $LASTEXITCODE)" }
  $exe = Get-LhmExePath
  if (-not $exe) { throw 'Installed, but LibreHardwareMonitor.exe could not be located.' }
  Write-Ok "installed: $exe"
}

if ($WhatIfPreference) {
  Write-Host ''
  Write-Host "(WhatIf) next: relaunch elevated (one UAC prompt) to stop any running LHM, install PawnIO if missing,"
  Write-Host "         seed LHM's web-server setting, register the logon task '$TaskName', start it and verify."
  exit 0
}

Write-Step 'Elevating once (a UAC prompt will appear) for the driver, config and logon task'
$extra = @('-LhmExe', "`"$exe`"", '-TaskUser', "`"$env:USERDOMAIN\$env:USERNAME`"")
if ($NoStartup) { $extra += '-NoStartup' }
[void](Invoke-Elevated $extra)

Write-Host ''
$ok = Show-Status
if ($ok) {
  Write-Host ''
  Write-Host 'Done. Open the widget board: the CPU label no longer says EST. and shows the real die temperature.' -ForegroundColor Green
} else {
  Write-Host ''
  Write-Host 'Not fully working yet. See the [warn]/[!!] lines above; a restart may be needed after the driver install.' -ForegroundColor Yellow
}
exit ([int](-not $ok))
