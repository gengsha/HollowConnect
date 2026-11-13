# Install HollowConnect 0.3.1

Download the complete **Windows ZIP** from [Releases](https://github.com/gengsha/HollowConnect/releases/tag/v0.3.1). Choose **Legacy** for Hollow Knight 1.5.78.11833, or **Unity6** for 1.5.12620. Extract every file and close the game.

## Installer

Run `HollowConnectInstaller.exe` (also provided as `一键安装.exe`), choose **English** in the language selector, select the game folder, then choose **Install / Update**. Keep the files beside the installer. It backs up replaced files; Uninstall / Restore keeps current saves.

## Script installation — no installer required

Open PowerShell in the extracted package and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GamePath "E:\SteamLibrary\steamapps\common\Hollow Knight"
```

The script verifies the API, creates backups and installs the mod. To restore the original files, use `Uninstall.ps1` with the same `-GamePath`.

## Manual installation — no installer required

1. Back up saves, existing `Mods/HKMP`, and the Managed files you will replace.
2. Extract the **Modding API ZIP included in `dependencies/`** into `hollow_knight_Data/Managed`, retaining its directory structure and replacing matching API files. Use bundled v77 for Legacy and the bundled pinned API for Unity6; do not mix APIs.
3. Copy the complete **`payload/HKMP` folder** into `hollow_knight_Data/Managed/Mods/HKMP`. Move the old HKMP folder aside before updating. Copy all dependencies, including HKMP.dll and BouncyCastle.Cryptography.dll.
4. Start the game. Manual removal requires restoring your own backups; it does not create the script's backup chain.

## Playing

All players need the same mod version and a package matching their game. The main menu provides **LAN multiplayer** (IP / UDP port) and **Steam multiplayer**. Set room rules under Options → Mods → HollowConnect before hosting. Back up existing saves before enabling shared progress.

In-game text follows the game's language; other unsupported languages use English. F6 gives up waiting for rescue, F7 selects a teammate, F8 teleports if allowed. Routine status text stays in logs. Hold F5 for the death board; F9 opens diagnostics. Cross-room teammate locations are available while the map is open.

[Release notes and validation limits](UPDATE-0.3.1.md) · [Known issues](KNOWN-ISSUES.md)
