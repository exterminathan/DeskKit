# Color Picker

**Status: scaffold, planned.** Pick any colour on screen with a hotkey and copy it as HEX, RGB or HSL. The module builds and starts (a tray icon, no window, saying "Color Picker scaffold, no features yet"). It has no features. It is not in the installer yet.

C# on .NET 8, self-contained. In-repo module: this folder is the source of truth.

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` (builds `bin\ColorPicker.exe`). Test: `test.ps1`. Run alone: `bin\ColorPicker.exe`.

- Data dir: `%APPDATA%\DeskKit\colorpicker\`. The scaffold writes `logs\colorpicker.log`.
- Single instance: mutex `Local\DeskKit.colorpicker`, events `.show` and `.stop` (contract 7.2).

## Planned features

- **Run mode: `tray`.** Like PowerToys Color Picker, it waits in the tray and a global hotkey starts a pick. A window would be in the way for a tool you use for two seconds.
- Global hotkey, default `Win+Shift+C` (the PowerToys default; changeable in settings).
- Magnifier loupe under the cursor, click to pick, `Esc` to cancel.
- Copy as HEX, RGB or HSL; the user switches formats on and off. Default copy format: HEX.
- Colour history (last 20) in the tray menu.
- A small editor window to fine-tune a colour (later).
- Settings keys (planned): `hotkey`, `copy_format`, `formats`, `history_size`.
- Needs: `RegisterHotKey` and a screen capture of the pixel under the cursor. No admin rights, no helpers.
