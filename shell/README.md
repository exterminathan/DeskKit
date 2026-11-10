# shell/

The DeskKit tray app, `DeskKit.exe`: C# on .NET 8, WinForms, self-contained, single file, win-x64, no NuGet packages. Its behaviour follows `docs/module-contract.md` sections 5 to 9 and 13. The shell is optional: every module runs without it.

## Build and test

```
powershell -NoProfile -ExecutionPolicy Bypass -File shell/build.ps1        # publish to dist\shell\DeskKit.exe
powershell -NoProfile -ExecutionPolicy Bypass -File shell/test.ps1         # build, then DeskKit.exe --selftest
powershell -NoProfile -ExecutionPolicy Bypass -File shell/launch-test.ps1  # real tray, UI and command-line test
    [-SvvPath <SoundVolumeView.exe>] [-RegionShot]                          # also start Cable Listen / RegionShot
powershell -NoProfile -ExecutionPolicy Bypass -File shell/update-test.ps1  # updates against a local fake feed
    [-NoBuild] [-Keep]
```

- `build.ps1` takes the version from `suite.json` `shell.version`. The output is one file, `dist\shell\DeskKit.exe` (about 65 MB).
- `--selftest` checks manifest scanning, `--list` on this repo's modules, the run-mode rules, the command per runtime, helper detection, autostart state, logs, and real start and stop of the test modules in `tests\fixtures\`. It redirects `APPDATA`, `LOCALAPPDATA` and the Run key to temp.
- `launch-test.ps1` starts the published tray with temp data folders and a temp modules dir. It drives the main window through UI Automation, runs `--list`, `--run`, `--stop` and `--exit`, and checks autostart after a restart. It also tests `--run <id> -- <args>` with an echo module, starts `_template-python` through the shell, and simulates a missing Python with a PATH that holds only System32. With Python and SoundVolumeView it starts and stops Cable Listen from the main window, but skips it when any recording device has "Listen to this device" on, because Cable Listen's stop turns listening off. `-RegionShot` also starts and stops RegionShot (its first start builds a venv in the temp LOCALAPPDATA, which downloads packages). It skips RegionShot when a copy already runs. It closes only the shell it started. It refuses to run if another DeskKit shell is running.

- `update-test.ps1` tests contract 16 against `testsake_feed.py`, a local GitHub-style feed on 127.0.0.1 (never the internet). It uses a temp install dir, temp `APPDATA` and `LOCALAPPDATA`, and test keys under `HKCU\Software\DeskKit-UpdateTest`. It covers: newer and same version, ETag 304, pre-release rules, SHA256 mismatch, SHA256SUMS, download failure, rollback, a locked file, a running module (decline and agree), the data dir, `--update all --yes` (with a copy of the real Eye Saver payload, never started), a separately installed Region Control (notify only), source folders, the shell's installer (downloaded, never run) and offline. It ends with the real tray: the first check runs, and a click on Update installs the new Eye Saver into the temp install dir. It waits up to 5 min if another DeskKit shell runs, and never touches it.

## Command line

| Command | What it does |
|---|---|
| `DeskKit.exe` | Start the tray. A second copy asks the first to show its window, then exits 0 (mutex `Local\DeskKit.Shell`). |
| `--run <id> [--action <id>]` | Start one module with its `args` (or the action's `args`) and exit 0 at once, without a tray. Exit 1 for an unknown id or action, 4 for a missing runtime, each with a message box and an `ERROR:` line on stderr. |
| `--run <id> -- <args>` | The same, with `<args>` appended after the module's `args`, each one unchanged (no token or variable expansion). Example: `--run taskbargroups -- Tools`. Does not combine with `--action` (exit 2). Put `--modules-dir` before `--`. |
| `--stop <id>` | Set the module's own stop event and wait 5 s. It never kills a process (this command did not start it). |
| `--list` | Print each module's id, version, run mode, source, state and status. |
| `--version` | Print `DeskKit <version>`. |
| `--selftest` | Run the self-test. Exit 0 when every check passes. |
| `--show` | Open the main window when the tray starts. |
| `--exit` | Close the running tray. Modules keep running. |
| `--check-updates` | Check every component and print `ID`, `INSTALLED`, `AVAILABLE`, `STATE`, `NOTE`. Installs nothing; exit 0, also offline (contract 16.9). |
| `--update <id>`, `--update all` | Update one module, or every module with an update. Exit 0 updated or current, 1 failure, 2 unknown id, 3 skipped (declined, busy, notify only). |
| `--update shell` | Download and check `DeskKit-Setup-X.Y.Z.exe` and start it with `/SILENT /SUPPRESSMSGBOXES /NORESTART`. |
| `--yes` | With `--update`: stop a running module without asking. Without it the shell asks; with `DESKKIT_NO_UI=1` the answer is No. |
| `--modules-dir <path>` | Use another modules folder. The default is `<exe dir>\modules`. If that is missing, the shell uses `modules\` in a parent folder that holds `suite.json` (for running from `dist\shell` in the repo). |

The shell finds Python as contract 5.2 says, and looks up `py.exe` on PATH like `python.exe`, so a PATH without Python means "not found". It caches the result until it restarts.

Set `DESKKIT_NO_UI=1` to replace message boxes with stderr only (tests). Set `DESKKIT_RUN_KEY` to a subkey of HKCU to use it in place of the real Run key (tests). Update test hooks (`DESKKIT_FEED_BASE`, `DESKKIT_RC_UNINSTALL_KEY`, `DESKKIT_UPDATE_NO_RUN`, `DESKKIT_UPDATE_FAIL_AT`, `DESKKIT_UPDATE_FIRST_DELAY`): contract 16.10.

## What the tray does

- Lists every `modules\*\module.json` and skips folders that start with `_`. A file that fails to parse shows as broken, with the error.
- One submenu per module: the main control (Turn on/off, Open/Close, Open, Run), Stop, each action, Settings, Open settings folder, View log, Download helper (when a helper is missing), and Start at login.
- The main window shows the same list: state, version, run mode, autostart and a status line. The status line names a missing runtime, entry file or required helper, with where to get it. Right-click a row for the same menu.
- It checks every module's state every 2 s: the mutex for tray and window modules, else the process the shell started.
- Stop follows contract 7.3. It sets the stop event (for any copy), then sends WM_CLOSE, then kills the process tree. It does the last two only for a process the shell started.
- A oneshot still running after 60 s is killed and reported. A crash (exit code not 0) shows the last `ERROR:` line. Exit code 3 points to the Download item.
- Updates (contract 16): the first check runs 30 s after the tray starts, then every 24 h while "Check for updates automatically" is on. "Check for updates" in the tray menu and the main window checks at once. One notice per new version. The main window has an Update button and an update status line for the selected module. A running module is updated only after you agree. Region Control installed by its own installer is notify only ("open release page"). The shell's own update runs the suite installer and closes the tray.
- Autostart: per-module on/off lives in `%APPDATA%\DeskKit\shell\state.json`. The shell's own login entry is the HKCU Run value `DeskKit`. When you turn a module's autostart on, the shell login entry turns on too. For `managed_by: "module"`, the shell only shows whether a Run value starts the module's exe.

## Files

| File | Holds |
|---|---|
| `DeskKit/Program.cs` | Command line, `--list`, `--run`, `--stop`, single instance |
| `DeskKit/Manifest.cs` | module.json model and discovery |
| `DeskKit/Runtimes.cs` | Path tokens, finding Python, PowerShell and .NET, helper detection, command per runtime, status line |
| `DeskKit/Launcher.cs` | Start a module with `DESKKIT_*` variables and log capture; the shell log |
| `DeskKit/RunModes.cs` | Run-mode rules (pure functions) |
| `DeskKit/ModuleController.cs` | Start, toggle, stop protocol, oneshot limit, crash notices, settings, autostart |
| `DeskKit/ShellState.cs` | state.json and the HKCU Run value |
| `DeskKit/HelperDownloader.cs` | Helper download with SHA256 check (contract 8.3) |
| `DeskKit/UpdaterVersion.cs` | SemVer parse and compare (contract 16.4) |
| `DeskKit/UpdaterFeed.cs` | Update sources, GitHub Releases feed with ETag cache, choice of release (16.1, 16.2, 16.7) |
| `DeskKit/UpdaterInstall.cs` | Download, SHA256 check, staging, swap with rollback, the shell's installer (16.3, 16.5, 16.6) |
| `DeskKit/UpdaterState.cs` | The `updates` object in state.json (16.8) |
| `DeskKit/UpdateCli.cs`, `DeskKit/UpdaterUi.cs` | `--check-updates` and `--update`; tray items, timer, notices, Update button (16.9) |
| `tests/fake_feed.py` | Local fake GitHub Releases feed for `update-test.ps1`; never shipped |
| `DeskKit/TrayApp.cs`, `DeskKit/MainForm.cs` | Tray icon, menus, main window |
| `DeskKit/Native.cs` | Win32 calls: mutexes, events, CreateProcess with a log handle, process tree, WM_CLOSE |
| `DeskKit/SelfTest.cs` | `--selftest` |
| `tests/fixtures/fixture.ps1` | Test module for the self-test and the launch test; never shipped |

## Logs and data

- Shell log: `%APPDATA%\DeskKit\shell\shell.log`. Module output: `%APPDATA%\DeskKit\shell\logs\<id>.log`, which rotates to `.log.1` at 1 MB.
- `console: true` modules get their own visible console, and the shell does not redirect their output (contract 9.3 exception).
