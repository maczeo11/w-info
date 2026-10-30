<div align="center">

<img src="Assets/logo.svg" alt="W-INFO logo" width="136" />

# W-INFO

**CPU & GPU temps, fan speeds, CPU load, and network speed — right on your Windows 11 Widgets Board and Lock Screen.**

[![Platform](https://img.shields.io/badge/platform-Windows%2011%20(22H2%2B)-0078D4?logo=windows11&logoColor=white)](https://github.com/maczeo11/W-INFO)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

</div>

---

## What it does

Pin **Temp & Fan** on your Widgets Board (<kbd>Win</kbd> + <kbd>W</kbd>) and get a live dashboard of your PC's health — no sign-in, no cloud, no ads. It works out of the box with zero configuration:

```
┌─────────────────────────────────────────────────────────┐
│  W-INFO                              [ ↻ Refresh ]    │
├────────────────────────────┬────────────────────────────┤
│  CPU Temp         48 °C    │  CPU Load           12 %   │
│  GPU Temp         42 °C    │  Fan Speed      1,850 RPM  │
├────────────────────────────┴────────────────────────────┤
│  ↓ 2.4 MB/s                ↑ 180 KB/s                   │
│  ● NVIDIA NVML + Windows                                │
└─────────────────────────────────────────────────────────┘
```

**What you see at a glance:**

| Metric | Where it comes from | Needs anything extra? |
|---|---|---|
| **CPU load** | Windows performance counters (same as Task Manager) | No |
| **CPU temperature** | ACPI thermal zone (labelled **EST.**) or true die temp via LibreHardwareMonitor | No (see [Accurate sensors](#accurate-cpu-temperature-and-fans) for exact temps) |
| **GPU temperature** | NVIDIA NVML (`nvml.dll` ships with the driver) | No (NVIDIA only — AMD/Intel GPUs need LibreHardwareMonitor) |
| **Fan speeds** | LibreHardwareMonitor | [One-time setup](#accurate-cpu-temperature-and-fans) |
| **Network speed** | Adapter byte counters (download ↓ and upload ↑) | No |

> [!TIP]
> Right-click the pinned card to switch between **Small** (2×1), **Medium** (2×2), and **Large** (4×2) sizes.

---

## Install

### Quick install (recommended)

1. Download **`W-INFO-vX.Y.Z-setup.zip`** from the [latest release](https://github.com/maczeo11/W-INFO/releases/latest) and extract it.
2. Right-click **`Install.ps1`** → *Run with PowerShell*. It will:
   - verify the download hasn't been tampered with,
   - show you the signing certificate and ask before trusting it (**one admin prompt**),
   - install the widget,
   - optionally set up [accurate CPU temperature and fans](#accurate-cpu-temperature-and-fans).
3. Press <kbd>Win</kbd> + <kbd>W</kbd>, click **+**, and pin **Temp & Fan**.

**That's it.** No .NET runtime or SDK to install — everything is bundled inside the package.

> Add `-SkipAccurateSensors` if you only want the widget without the optional sensor setup, or `-WhatIf` to preview the plan without changing anything. To remove everything later, run `Uninstall.ps1`.

### Build from source

<details>
<summary>For developers who want to modify or contribute</summary>

**Prerequisites:** Windows 11 22000+, .NET 8.0 SDK, Windows 10/11 SDK Build Tools.

```powershell
git clone https://github.com/maczeo11/W-INFO.git
cd W-INFO

# Install local dev signing certificate (once, admin PowerShell)
.\scripts\trust-signing-cert.ps1

# Build the signed MSIX
.\scripts\package.ps1

# Register with Windows
.\scripts\install.ps1
```

**Quick dev loop** (no certificate, no admin):

```powershell
.\scripts\package.ps1 -LayoutOnly
Add-AppxPackage -Register .\publish\AppxManifest.xml
```

**Build a release bundle:**

```powershell
.\scripts\make-release.ps1             # → dist\W-INFO-vX.Y.Z-setup.zip
.\scripts\make-release.ps1 -Publish    # also creates a DRAFT GitHub release
```

</details>

---

## Accurate CPU temperature and fans

Out of the box, the CPU temperature comes from the firmware's ACPI thermal zone — a **system estimate that's often 20–50 °C below the actual die temperature**. The card labels it **`CPU · EST.`** so you know it's approximate.

For the real die temperature and fan RPMs, run the included setup script (**one admin prompt**):

```powershell
.\scripts\setup-accurate-sensors.ps1
```

This installs [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and the [PawnIO](https://github.com/namazso/PawnIO.Setup) driver from their official packages (nothing third-party is bundled), starts LibreHardwareMonitor minimised to the tray, and registers it to start at login. Once running, the **`EST.`** tag disappears and fan RPMs appear.

```powershell
# Preview the plan without changing anything
.\scripts\setup-accurate-sensors.ps1 -WhatIf

# Check if accurate sensors are already working
.\scripts\setup-accurate-sensors.ps1 -Check

# Remove the sensor setup (keeps LibreHardwareMonitor installed)
.\scripts\setup-accurate-sensors.ps1 -Uninstall

# Remove LibreHardwareMonitor too
.\scripts\setup-accurate-sensors.ps1 -Uninstall -RemoveLhm
```

**Good to know:**

- **Laptop fans** are often controlled by the embedded controller and report no RPM — if fan speed shows "—", this is a hardware limitation, not a bug.
- **LibreHardwareMonitor's web server** listens on port 8085. Windows Firewall blocks external access by default, but don't open that port, and consider enabling LibreHardwareMonitor's built-in authentication on shared networks.

---

## How it works

The widget reads from multiple sensor sources every 5 seconds and merges them — each value comes from the best source available:

| Priority | Source | What it provides |
|---|---|---|
| 1st | **LibreHardwareMonitor** (optional) | True CPU die temp, fan RPMs, GPU temp |
| 2nd | **NVIDIA NVML** (auto-detected) | GPU core temp |
| 3rd | **Windows** (always available) | CPU load, estimated CPU temp, network speed |

- **No idle waste:** the background process only runs while a widget is pinned. Unpin all widgets and it exits.
- **Smooth readings:** a rolling median filter prevents random sensor spikes.
- **Efficient updates:** the card only re-renders when values actually change (±1 °C, ±2% load, ±50 RPM, ±10 KB/s).
- **Color-coded temps:** green (< 60 °C) → amber (60–79 °C) → red (≥ 80 °C).

---

## Troubleshooting

| Symptom | What's happening | Fix |
|---|---|---|
| **CPU temp shows "—" or looks low** | Without LibreHardwareMonitor, the CPU temp is the firmware estimate | Run the [accurate sensor setup](#accurate-cpu-temperature-and-fans) |
| **CPU temp says `EST.`** | Same as above — the number is from the ACPI thermal zone, not the CPU die | Run the [accurate sensor setup](#accurate-cpu-temperature-and-fans) |
| **Fan shows "—"** | Windows has no fan API; laptop fans are often not readable | Fan RPMs come only from LibreHardwareMonitor, and only on hardware that exposes them |
| **GPU shows "—"** | No NVIDIA GPU, or NVIDIA driver not installed | NVIDIA GPUs work automatically. AMD/Intel Arc GPUs need LibreHardwareMonitor |
| **Network speed shows "—"** | No physical adapter detected (common in VMs) | Virtual adapters (Hyper-V, VPN, Tailscale, etc.) are excluded to avoid double-counting |
| **Widget doesn't appear in picker** | Package registration is stale | Re-run `Install.ps1` or reinstall via PowerShell |
| **Lock Screen widget not visible** | Requires Windows 11 24H2+ | Check *Settings → Personalization → Lock screen* |

### Verifying what your system can see

These commands need no admin and show what the widget reads:

```powershell
# CPU load (same counter the widget uses)
Get-Counter '\Processor Information(_Total)\% Processor Utility'

# ACPI thermal zones (value is in Kelvin, e.g. 313 = 39.9 °C)
Get-Counter '\Thermal Zone Information(*)\Temperature'

# Network adapters
Get-NetAdapterStatistics | Select-Object Name, ReceivedBytes, SentBytes

# NVIDIA GPU (if installed)
nvidia-smi --query-gpu=name,temperature.gpu --format=csv

# LibreHardwareMonitor feed (if running)
Invoke-RestMethod http://127.0.0.1:8085/data.json
```

---

## Uninstalling

Run the included `Uninstall.ps1`:

```powershell
# Remove the widget
.\Uninstall.ps1

# Also remove the sensor setup
.\Uninstall.ps1 -RemoveSensors

# Also remove LibreHardwareMonitor
.\Uninstall.ps1 -RemoveSensors -RemoveLhm

# Preview without changing anything
.\Uninstall.ps1 -WhatIf
```

---

## License

[MIT](LICENSE). Built by [maczeo11](https://github.com/maczeo11).
</div>
