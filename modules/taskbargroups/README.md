# Taskbar Groups

Group shortcuts under one pinned taskbar icon. Clicking the icon opens a small popup launcher with the group's shortcuts. DeskKit module `taskbargroups`, in-repo, C# on .NET 8 WinForms. It started as LightweightTaskbarGroups, a from-scratch take on the [TaskbarGroups](https://github.com/tjackenpacken/taskbar-groups) idea; the original source notes are in `src/README.md`.

## Run it alone

Unzip `taskbargroups-<version>.zip` anywhere and run `taskbargroups\bin\TaskbarGroups.exe`. It is self-contained: no .NET install needed.

- `TaskbarGroups.exe`: opens the config window. Create a group, add shortcuts, save. Saving writes `<group>.lnk` into the Shortcuts folder. Right-click that `.lnk` and choose **Pin to taskbar**.
- `TaskbarGroups.exe "<group>"`: opens the popup for that group above the taskbar. Click an item or press `1`–`9`; `Esc` or a click outside closes it.

Run mode `popup`: no background process, no tray icon, and two copies at once are safe. Keep the exe in a stable folder: each group `.lnk` stores the exe's absolute path.

Exit codes: 0 OK, 1 group not found or the data folder is unusable, 2 more than one argument. Errors print one `ERROR:` line to stderr, plus a message box when DeskKit's shell did not start it.

## Data

All data lives in `%APPDATA%\DeskKit\taskbargroups\` (or `DESKKIT_DATA_DIR` when the shell passes it). Nothing is written next to the exe.

| Path | What |
|---|---|
| `config\<group>\group.json` | One group: name, icon, colours, shortcuts |
| `config\<group>\GroupIcon.ico`, `icons\` | Cached icons of that group |
| `icons\` | Group icons; `link.ico` and `jarv.ico` are written on first run |
| `Shortcuts\` | Default folder for the generated `<group>.lnk` files |
| `settings.json` | Settings (below) |
| `migrated-from.txt` | Written once if the first run copied an old config |

### settings.json keys

| Key | Default | Meaning |
|---|---|---|
| `shortcutsPath` | `null` | Folder for the generated `.lnk` files. `null`: `Shortcuts\` in the data dir |
| `stayOpenUntilClickOff` | `true` | Keep the popup open until you click outside it. `false`: close when it loses focus or after a launch |

Edit them in the config window (**Settings...**) or in the file.

### Paths in group.json

- `targetPath` of a shortcut is stored as picked (absolute, for example a `.lnk` in your shortcuts folder). It is never rewritten.
- `iconPath` of a group may be absolute or relative. A relative path resolves against the data dir; new groups start with `icons/link.ico`. If a stored absolute icon is gone, the same file name in `icons\` is used. The cached `GroupIcon.ico` is the fallback for the preview.

### First run: copy from an old install

If the data dir has no `config\` and no `settings.json`, the first run copies (never moves) `config\`, `icons\` and `settings.json` from the first of these that has them:

1. the exe folder (old builds kept their config next to the exe);
2. `<system drive>\TaskbarGroups` (the old default install folder), or the folder in `TASKBARGROUPS_LEGACY_DIR` when that variable is set.

The old folder stays unchanged, and its pinned `.lnk` files keep starting the old exe until they are repointed.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File modules/taskbargroups/build.ps1
```

It publishes a self-contained win-x64 single-file `TaskbarGroups.exe` into `bin\` and writes `dist\taskbargroups-<version>.zip` at the repo root. Needs the .NET 8 SDK. `-NoZip` skips the zip; `-OutDir <folder>` puts the zip elsewhere. The build installs nothing and kills no running copy.

`test.ps1` builds into a temp folder, unpacks the zip and runs it with a throwaway data dir: config window, popup, two copies at once, a missing group, and the first-run copy from a fake old folder.

## Licence

MIT. Copyright (c) 2026 inNATE Tools. The repo-level `LICENSE` ships in the zip.
