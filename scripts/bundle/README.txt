W-INFO - Windows 11 hardware widget
=====================================

INSTALL
  1. Extract this whole folder (don't run the scripts from inside the zip).
  2. Right-click Install.ps1 -> Run with PowerShell   (or:  .\Install.ps1)
  3. Press Win+W, click +, pin "Temp & Fan".

  Install.ps1 asks for administrator approval once. It will:
    - check the files against SHA256SUMS.txt and check the app is signed by W-INFO.cer,
    - show you the certificate and ask before trusting it,
    - install the app,
    - set up accurate CPU temperature / fans (LibreHardwareMonitor + the PawnIO driver, fetched
      from their official winget packages; they are NOT included in this download).

  Options:   .\Install.ps1 -WhatIf                  verify and show the plan, change nothing
             .\Install.ps1 -SkipAccurateSensors     widget only (CPU temp shows as "EST.")

CHECK STATUS ANY TIME
  .\setup-accurate-sensors.ps1 -Check

UNINSTALL
  .\Uninstall.ps1                      removes the app and the certificate trust
  .\Uninstall.ps1 -RemoveSensors       also removes the logon task for LibreHardwareMonitor

Why a certificate prompt? The app is signed with a self-signed certificate, so Windows will not
install it until you trust that publisher. The thumbprint shown by Install.ps1 must match the one on
the release page.

Licence: LICENSE (MIT). Third-party: THIRD-PARTY-NOTICES.md
