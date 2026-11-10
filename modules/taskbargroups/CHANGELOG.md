# Changelog

## 0.1.1 (2026-11-10)

- First-run copy now copies any icon a group `iconPath` references inside the old folder into `icons\` and rewrites the path. The old folder is not changed.
- Removed a local path from `module.json` notes. Refreshed the source README.

## 0.1.0 (2026-11-10)

- First DeskKit release, moved in-repo from LightweightTaskbarGroups.
- Config moves to `%APPDATA%\DeskKit\taskbargroups\`. First run copies the old `config\`, `icons\` and `settings.json` from the exe folder or `<system drive>\TaskbarGroups`; the old folder is not changed.
- Relative group icon paths resolve against the data dir; new groups default to `icons/link.ico`. Stored absolute paths keep working.
- The build no longer installs into `C:\TaskbarGroups`, kills running copies or creates a Start Menu shortcut. It publishes one self-contained win-x64 exe into `bin\`.
- Errors print one `ERROR:` line and exit 1 (missing group) or 2 (wrong arguments).
- MIT licence.
