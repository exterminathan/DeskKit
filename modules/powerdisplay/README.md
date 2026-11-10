# Power Display

**Status: scaffold, planned.** Control monitor brightness, contrast and picture profiles over DDC/CI from the tray. The module builds and starts (a tray icon, no window, saying "Power Display scaffold, no features yet"). It has no features. It is not in the installer yet.

C# on .NET 8, self-contained. In-repo module: this folder is the source of truth.

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` (builds `bin\PowerDisplay.exe`). Test: `test.ps1`. Run alone: `bin\PowerDisplay.exe`.

- Data dir: `%APPDATA%\DeskKit\powerdisplay\`. The scaffold writes `logs\powerdisplay.log`.
- Single instance: mutex `Local\DeskKit.powerdisplay`, events `.show` and `.stop` (contract 7.2).

## Planned features

- **Run mode: `tray`.** Like PowerToys PowerDisplay, it keeps a tray flyout with sliders for each monitor. Eye Saver was a `oneshot`; the tray adds sliders and a profile menu, and the old one-shot calls stay as command-line arguments (see "Replaces Eye Saver").
- Per-monitor brightness and contrast sliders (VCP 0x10 and 0x12).
- Profiles (named sets of VCP values). The Eye Saver `on` and `off` profiles become the first two.
- Power on and standby (VCP 0xD6). Input select (VCP 0x60) later.
- Optional global hotkey to toggle the eye-saver profile (default none; the user sets it).
- Settings keys (planned): `monitors`, `profiles`, `hotkey_toggle`, `detect_code`, `detect_on_max`.
- Needs: Windows `dxva2.dll`, as Eye Saver uses. No admin rights, no helpers. Risk: monitors answer DDC/CI differently.

## Replaces Eye Saver

Power Display replaces Eye Saver (`modules/eyesaver`, not touched). Eye Saver goes when Power Display matches it.

### What carries over

Eye Saver commands (today `monitor.ps1`; see its README):

| Eye Saver | Power Display (planned) |
|---|---|
| `eyesaver on`, `eyesaver off` | Same arguments: `PowerDisplay.exe eyesaver on` and `off`. They apply the `on` or `off` profile. |
| `eyesaver toggle` or `toggle` | Same. It reads `detect_code` and `detect_on_max`, then applies the other profile. |
| `status` | Same. It prints the monitor name and the profile values. |
| `on` (monitor power on, VCP 0xD6 = 1) | Same name. |
| `standby` (VCP 0xD6 = 4) | Same name. |
| `brightness <0-100>`, `contrast <0-100>` | Same names (VCP 0x10, 0x12), plus sliders in the tray flyout. |

The exit codes carry over: 0 ok, 2 bad arguments, 5 no DDC/CI monitor, 1 other error.

Eye Saver `settings.json` fields (`%APPDATA%\DeskKit\eyesaver\settings.json`):

| Field | Carries over as |
|---|---|
| `monitor_match` | `monitors[].match`: text to find in the monitor description. Empty or no match means the primary monitor. |
| `profiles.on`, `profiles.off` | `profiles.on`, `profiles.off`, unchanged: VCP code (hex string) to value. New profiles join them. |
| `detect_code`, `detect_on_max` | Unchanged. The `toggle` rule stays the same. |

First run of Power Display: if its `settings.json` is missing and the Eye Saver file exists, copy those fields (read only; never change the Eye Saver file).

### When Eye Saver can go

Eye Saver can go when Power Display matches all of: `on`, `off`, `toggle`, `status`, `standby`, `brightness`, `contrast`, with the same exit codes. Then:

1. Give Power Display the actions `toggle`, `on`, `off` and `status` in `module.json`, so `DeskKit.exe --run powerdisplay --action toggle` replaces `--run eyesaver --action toggle`.
2. A tester checks each command on the real monitor, with the coordinator's go (monitor rule in `CLAUDE.md`).
3. Repoint the shortcuts and PATH scripts (`tools/migrate-launchers.ps1`).
4. After Nate confirms, remove `modules/eyesaver`, its `suite.json` entry and its installer component. Keep a backup zip until Nate confirms.
