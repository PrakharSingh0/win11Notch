# Win11Notch

A sleek, native Windows 11 Dynamic Island overlay and productivity hub built with **.NET 10 WPF**, **WPF-UI 4.3.0**, and **Win32 Per-Monitor V2 DPI** APIs — engineered specifically for crisp contrast on IPS displays and near-zero idle resource usage.

## Key Features
- **3-Tier Non-Intrusive State Machine**
  - **Idle Handle:** Minimal unobtrusive pill (`#1A1C20` frosted slate) with zero focus steal (`WS_EX_NOACTIVATE`) and zero background polling timers while idle.
  - **Collapsed Hover & Live Island:** Time-aware startup greeting, Now Playing mini-capsule, Volume/Brightness HUD, Bluetooth battery badge, Pomodoro & Stopwatch pill, and calendar glance.
  - **Peek Expanded Hub:** Interactive frosted dashboard with:
    - **Media & Volume / Brightness Controls + Audio Output Switcher**
    - **Drop Shelf & Quick Share:** Stage files or share directly via **Google Quick Share / Windows Nearby Share**
    - **Productivity Tools:** Pomodoro focus timer, Stopwatch, Quick Notes, Hydration tracker, and Color Picker
    - **Calendar & Reminders**
    - **Clipboard History** (Text, URLs, Images with pinning & search)
    - **System Telemetry:** CPU, GPU, RAM, Storage, Network, and Bluetooth battery levels
  - **Stealth Mode:** Automatically hides (`WS_EX_TRANSPARENT`, `0%` opacity) when fullscreen games or apps are active.

## Requirements
- **OS:** Windows 11 (10.0.22621.0 or later)
- **Runtime:** [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Installation
- **Installer (`Win11Notch-Setup.exe`):** Per-user install to `%LocalAppData%\Programs\Win11Notch` with Start Menu shortcut, optional Startup launch, and native Windows *Installed Apps* uninstaller (no UAC elevation needed).
- **Portable (`Win11Notch.exe`):** Single-file executable ready to run from anywhere. Configuration and logs are cleanly stored in `%AppData%\Win11Notch\`.

## Build from Source
```powershell
dotnet build src\Win11Notch\Win11Notch.csproj -c Release
.\scripts\publish-release.ps1
```

## Author & Repository
- **Author:** Prakhar Singh
- **GitHub:** [PrakharSingh0/win11Notch](https://github.com/PrakharSingh0/win11Notch)

## License
Licensed under the **MIT License** — see [LICENSE](LICENSE) for full text.
