# Eye Saver

**To be replaced** by the Power Display module (`modules/powerdisplay`) when it matches these commands.

Switch the monitor between an eye-saver picture profile and a normal one over DDC/CI. Windows PowerShell 5.1, no helpers (uses Windows `dxva2.dll`). In-repo module: this folder is the source of truth.

Run alone: double-click `launch.cmd` (toggles), or `launch.cmd <command>`.

## Commands

| Command | Does |
|---|---|
| `eyesaver on` / `eyesaver off` | Apply the `on` or `off` profile. |
| `eyesaver toggle` or `toggle` | Switch to the other profile (reads `detect_code`). |
| `status` | Print the monitor name and the profile values. |
| `on` | Power the monitor on (VCP 0xD6 = 1). |
| `standby` | Put the monitor in standby (VCP 0xD6 = 4). |
| `brightness <0-100>`, `contrast <0-100>` | Set VCP 0x10 or 0x12. |

Exit codes: 0 ok, 2 bad arguments, 5 no DDC/CI monitor or the monitor did not answer, 1 other error. Errors print one `ERROR:` line; a message box also shows when `DESKKIT_SHELL` is not set.

## Settings

`%APPDATA%\DeskKit\eyesaver\settings.json`, written with these defaults on first run:

- `monitor_match`: text to find in the monitor description (`"S34J55"`). No match, or `""`: the primary monitor. Windows often reports only "Generic PnP Monitor", so on most PCs this falls back to the primary monitor.
- `profiles.on` and `profiles.off`: VCP code (hex string) to value. Add or remove codes freely.
- `detect_code` (`"0x1A"`) and `detect_on_max` (`19`): `toggle` treats the monitor as "on" when this code reads at or below the value.

Some monitors answer DDC/CI only after their on-screen menu has been opened once.
