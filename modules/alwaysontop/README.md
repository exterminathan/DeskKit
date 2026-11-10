# Always On Top

**Status: scaffold, planned.** Pin any window above all others with a hotkey, with a coloured border on pinned windows. The module builds and starts (a tray icon, no window, saying "Always On Top scaffold, no features yet"). It has no features. It is not in the installer yet.

C# on .NET 8, self-contained. In-repo module: this folder is the source of truth.

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` (builds `bin\AlwaysOnTop.exe`). Test: `test.ps1`. Run alone: `bin\AlwaysOnTop.exe`.

- Data dir: `%APPDATA%\DeskKit\alwaysontop\`. The scaffold writes `logs\alwaysontop.log`.
- Single instance: mutex `Local\DeskKit.alwaysontop`, events `.show` and `.stop` (contract 7.2).

## Planned features

- **Run mode: `tray`.** Like PowerToys Always On Top, it waits in the tray and a global hotkey pins or unpins the focused window. It replaces the old desk pins.
- Global hotkey, default `Win+Ctrl+T` (the PowerToys default).
- Pin with `SetWindowPos(HWND_TOPMOST)`; unpin with `HWND_NOTOPMOST`.
- Coloured border around a pinned window (default on) and a sound on pin (default off).
- Tray menu lists pinned windows and has "Unpin all".
- Unpin everything on exit, so no window stays pinned with no way to unpin it.
- Excluded apps list.
- Settings keys (planned): `hotkey`, `border`, `border_color`, `sound`, `excluded_apps`.
- Needs: `RegisterHotKey` and `SetWinEventHook` for window moves. No admin rights, but it cannot pin windows of elevated programs (UIPI) unless DeskKit runs as admin.
