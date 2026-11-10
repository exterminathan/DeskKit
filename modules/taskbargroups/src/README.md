# Lightweight Taskbar Groups

> **DeskKit note (2026-11-10):** this is the original project README, kept for its credit and feature notes. In DeskKit, the module README (`../README.md`) replaces the Build, data and licence sections. `dotnet build` no longer installs to `C:\TaskbarGroups`, kills running copies or makes a Start Menu shortcut. Data lives in `%APPDATA%\DeskKit\taskbargroups\`, not next to the exe. Licence: MIT, Copyright (c) 2026 inNATE Tools.

A minimal Windows utility for grouping pinned taskbar shortcuts into a small pop-up launcher. Click a single pinned icon, get a tidy grid of apps, files, or folders.

Built as a single-binary .NET 8 WinForms app — no installer, no background process, no telemetry.

## Credit

This project is a from-scratch reimplementation of the **[TaskbarGroups](https://github.com/tjackenpacken/taskbar-groups)** concept. All credit for the original idea, UX, and "pin a `.lnk` that opens a popup" trick goes to the original author. This rewrite is smaller, simpler to build, and easier to hack on. It is **not** a drop-in replacement and does not share the original's config format.

## Features

- Group any number of shortcuts (apps, files, folders, `.lnk` files) under one taskbar icon
- Pop-up launcher anchored above the taskbar with Windows 11 native rounded corners (region fallback on Win10)
- Per-group: background color, opacity, icon, and shortcut icon size
- Number-key shortcuts (`1`–`9`) inside the popup; `Esc` to dismiss
- Configurable Shortcuts folder — keep your `.lnk` files anywhere you want
- Pure WinForms + DWM — no WPF, no WebView, no extra NuGet dependencies

## How it works

1. The config UI writes each group to `config/<GroupName>/group.json` plus cached icons.
2. Saving a group generates a `.lnk` in the Shortcuts folder whose target is `TaskbarGroups.exe "<GroupName>"`.
3. You **right-click that `.lnk` → Pin to taskbar**. Clicking the pinned icon launches the popup for that group.

Argument dispatch in [Program.cs](TaskbarGroups/Program.cs):
- No args → config window
- One arg → popup launcher for that group

## Requirements

- Windows 10 / 11
- .NET 8 SDK (to build)
- .NET 8 Desktop Runtime (to run a framework-dependent build — not needed for self-contained)

## Build

In DeskKit, run `modules/taskbargroups/build.ps1`. It publishes one self-contained win-x64 exe into `bin\` and zips the module. `dotnet build` only compiles: it installs nothing, kills no running copy and makes no Start Menu shortcut.

Keep the exe in a **stable folder**: pinned `.lnk` files reference its absolute path.

## Usage

1. Run `TaskbarGroups.exe` (no arguments) to open the config window.
2. Click **New Group** → name it, pick an icon, add shortcuts, adjust icon size and appearance, then save.
3. The Shortcuts folder opens automatically on first creation. Right-click the `.lnk` → **Pin to taskbar**.
4. Click the pinned icon → popup appears above the taskbar → click an item or press `1`–`9` to launch, `Esc` to dismiss.

### Configuring the Shortcuts folder

Click **Settings...** in the config window to change where `.lnk` files are written. Leave blank to use the default `Shortcuts\` folder in the data dir (`%APPDATA%\DeskKit\taskbargroups\`). This setting is stored in `settings.json` in the data dir.

## Project layout

```
TaskbarGroups/
  Program.cs              entry point + arg dispatch
  app.manifest            PerMonitorV2 DPI awareness
  Config/
    ConfigForm.cs         group list + management UI
    GroupEditForm.cs      group editor (name, icon, shortcuts, appearance)
    SettingsForm.cs       shortcuts folder override
  Launcher/
    LauncherForm.cs       popup launcher
  Models/
    GroupConfig.cs        group POCO (JSON-serialized)
    ShortcutItem.cs       shortcut POCO
    AppSettings.cs        per-install settings
  Services/
    ConfigService.cs      disk I/O, paths, JSON
    IconService.cs        icon extraction + caching (PrivateExtractIcons)
    ShortcutService.cs    .lnk generation via IShellLink P/Invoke
    ShellLink.cs          IShellLinkW / IPersistFile COM interop
    SettingsService.cs    settings.json read/write
  icons/                  bundled default icon
scripts/
  publish-standalone.ps1  single-file publish helper
```

Runtime data lives in `%APPDATA%\DeskKit\taskbargroups\` under `config\`, `icons\`, `Shortcuts\` and `settings.json`. Nothing is written next to the exe.

## License

MIT, see `LICENSE` in the module folder. The original TaskbarGroups is MIT-licensed; if you reuse anything conceptual from it, keep that attribution intact.
