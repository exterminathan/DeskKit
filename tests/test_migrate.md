# Migration script tests

`test_migrate.ps1` tests `tools/migrate-launchers.ps1`. `test_migrate.py` runs it from `python -m unittest discover -s tests` (Windows only).

Run alone: `powershell -NoProfile -ExecutionPolicy Bypass -File tests/test_migrate.ps1 [-Keep]`. Takes about 15 seconds. Exit 0 = all pass.

What it does:

- Builds a fake tree in `%TEMP%\dk-g6-migrate-<random>`: Shortcuts, Scripts, pinned taskbar and Start Menu folders, an old TaskbarGroups folder, a SoundVolumeView folder and a fake install. The `.lnk` files are real, made through WScript.Shell. `region_shot.bat` and `ytdl.bat` are copied from the real scripts folder when it exists (read only), else stand-ins.
- Points `APPDATA`, `LOCALAPPDATA` and `USERPROFILE` into the tree for every script run, so icon, helper, settings and backup copies stay in the tree.
- Checks: the dry run changes no file (SHA256); `-Apply` refuses with no `DeskKit.exe` or with a module missing; `-Only`; `-Apply` sets targets, arguments, working folders and icons and keeps window style and hotkey; pinned entries change only with `-IncludeTaskbarPins`; the old TaskbarGroups folder never changes; the helper and icon copies are byte-identical; a third `-Apply` changes nothing and makes no backup; `restore.ps1 -RemoveCreated` puts every file back byte for byte.
- Deletes the tree at the end (`-Keep` keeps it).

It never runs `-Apply` against the real folders. It never starts DeskKit, a module, or anything that touches audio or the monitor.

Not covered: whether Windows still uses an edited pinned taskbar `.lnk` (pin cache). Only a real pinned edit on Nate's PC can show that.
