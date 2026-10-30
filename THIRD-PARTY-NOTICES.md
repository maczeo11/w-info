# Third-party notices

W-INFO is released under the MIT licence (see `LICENSE`). This file lists what the
release contains from other parties, and what the installer fetches for you.

## Inside the app package

| Component | Licence |
|---|---|
| .NET 8 runtime (self-contained, so no separate install is needed) | MIT: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows App SDK and the Widgets provider API (`Microsoft.WindowsAppSDK`, `Microsoft.WindowsAppSDK.Widgets`) | Microsoft Software License Terms of those NuGet packages; redistributed unmodified |
| `System.Diagnostics.PerformanceCounter` | MIT |
| `System.Management` | MIT |

## Fetched by the installer, NOT included in this download

`setup-accurate-sensors.ps1` (run by `Install.ps1` unless you pass `-SkipAccurateSensors`)
installs the following from their official winget packages, at your request. Nothing below
is copied into, linked with, or redistributed by W-INFO.

* **LibreHardwareMonitor**: Mozilla Public License 2.0.
  https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
  W-INFO only reads its local web server (`http://127.0.0.1:8085/data.json`); no
  LibreHardwareMonitor code or binary is part of W-INFO.
* **PawnIO** (driver by namazso): listed on winget as *Proprietary (Freeware)*, so it may not
  be redistributed and is never bundled. It is installed from its official package.
  LibreHardwareMonitor loads it; W-INFO does not call it.

## Used from your system

* **NVIDIA NVML** (`nvml.dll`): ships with the NVIDIA display driver. W-INFO calls the copy
  already on your PC when an NVIDIA GPU is present and includes none.
