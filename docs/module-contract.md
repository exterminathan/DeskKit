# DeskKit module contract

Version 1 of the contract, written 2026-11-10 by G2 Contract. It tells module, shell and installer builders exactly what to build. Nate approves it at gate G2.

Words used in this file, one meaning each:

- **Module**: one tool, in one folder under `modules/<id>/`, described by one `module.json`.
- **Shell**: the DeskKit tray app (`DeskKit.exe`, .NET 8, self-contained). It is optional. Every module runs without it.
- **Suite**: the DeskKit repo, its `suite.json` and the one installer built from it.
- **Helper**: an outside program a module needs, such as ffmpeg. DeskKit never bundles a helper.
- **Install dir**: where the suite installer puts DeskKit (see "Installed layout").
- **Module dir**: the folder of one module, in the repo or in the install dir.
- **Data dir**: where a module keeps its settings, logs and state.

"MUST" means required. "SHOULD" means do it unless you write down why not. A line marked **DECISION** is a choice G2 Contract made where no fact or ruling existed. Nate can change it.

## 1. What a module is

A module is one small Windows tool that:

1. Runs alone, with no shell, from its own folder or its own release zip.
2. Keeps the language it was written in. DeskKit never rewrites a tool into another language.
3. Has one `module.json` that the shell, the installer and the validator read.
4. Has no hard-coded `C:\`, `D:\` or user-name paths, in `module.json` or in code. Use environment variables and the tokens in section 4.
5. Keeps its data in its data dir (section 6).
6. Never bundles a helper. It finds the helper, or tells the user where to get it (section 8).

**Default language for new modules: C# on .NET 8, self-contained** (Nate, 2026-11-10). Start from `modules/_template/`. Use another runtime only when the tool already exists in it or needs it; `modules/_template-python/` is the Python start point. Existing tools keep their language.

A module has one of two sources:

- **in-repo**: the code lives in `modules/<id>/` in the DeskKit repo. Today: `regionshot`, `cablelisten`, `eyesaver`. **The module folder is the source of truth** (Nate, 2026-11-10). These three are in-repo for good. Their old folders (`D:\My Software\RegionShot`, `C:\Scripts\cable_listen.pyw`, ClaudeGlobalSetup `tools/monitor/`) are deleted after Nate verifies the modules. Never edit the old copies, and never copy changes back to them.
- **pinned**: the code lives in its own GitHub repo. DeskKit uses one exact release asset, fixed by URL and SHA256 in `suite.json`. Today: `regioncontrol` only. DeskKit never edits a pinned module's code. A change goes to its own repo and comes back as a new release.

## 2. Repo layout

```
DeskKit/
├── suite.json                 the suite: version, shell, every module and its pin
├── schemas/
│   ├── module.schema.json     JSON Schema for module.json
│   └── suite.schema.json      JSON Schema for suite.json
├── shell/                     the tray app (.NET 8)
├── modules/
│   ├── _template/             copy this to start a new module (C# .NET 8, the default)
│   ├── _template-python/      copy this for a Python module
│   ├── regionshot/            in-repo
│   ├── cablelisten/           in-repo
│   ├── eyesaver/              in-repo
│   ├── regioncontrol/         pinned: module.json and README.md only
│   └── taskbargroups/         in-repo: C# source in src/, module.json, build.ps1, test.ps1
├── packaging/                 Inno Setup script and build steps for the suite installer
├── tools/                     build and check scripts (validate-modules.py)
├── tests/                     tests for the contract and the tools
├── docs/                      brief, roadmap, inventory, this contract
└── .github/workflows/         CI (planned; no remote yet)
```

Folders under `modules/` whose name starts with `_` are templates. The shell, the installer and `suite.json` skip them. The validator checks them, with one rule changed: a template's `id` equals its folder name without the leading `_` (`_template` has id `template`).

### 2.1 In-repo module folder

```
modules/<id>/
├── module.json      required
├── README.md        required: what it does, how to run it alone, its settings keys
├── CHANGELOG.md     required: one "## X.Y.Z (YYYY-MM-DD)" heading per release, newest first
├── launch.cmd       required for python and powershell modules (section 5.4)
├── icon.ico         optional; when present, `icon` in module.json names it
├── src/             required: the code
│   └── requirements.txt   python modules with pip packages only
├── build.ps1        required for dotnet and native modules: builds into bin/
├── bin/             build output of dotnet and native modules (gitignored)
└── tests/           optional; never shipped
```

What ships (the "payload"): `module.json`, `README.md`, `CHANGELOG.md`, `launch.cmd`, `icon.ico`, and `src/` (script runtimes) or `bin/` (compiled runtimes). `tests/`, `build.ps1` and `__pycache__/` never ship. The repo's root `LICENSE` is copied into every payload as `LICENSE`.

Paths in `module.json` are relative to the module dir and use backslashes or forward slashes. The payload keeps the same relative paths, so `entry` is the same path in the repo, in a release zip and in the install dir.

### 2.2 Pinned module folder

```
modules/<id>/
├── module.json      required: describes the pinned release
├── README.md        required: what it is, its own repo, how to update the pin
├── icon.ico         optional
└── bin/             filled by the build from the pinned asset (gitignored)
```

The suite build downloads the asset from `suite.json`, checks its SHA256, and puts it in `bin/` (section 10.4).

### 2.3 Installed layout

The suite installer installs per user, with no admin rights: Inno Setup `DefaultDirName={autopf}\DeskKit` with `PrivilegesRequired=lowest`. That resolves to `%LOCALAPPDATA%\Programs\DeskKit`.

```
%LOCALAPPDATA%\Programs\DeskKit\     install dir
├── DeskKit.exe                      the shell
├── suite.json
├── LICENSE
└── modules\<id>\                    one payload per chosen module
```

## 3. module.json

The schema is `schemas/module.schema.json` (JSON Schema draft 2020-12). The schema is the final word on types. This table is the final word on meaning. Every key below is required unless the "Required" column says otherwise. Unknown keys are an error.

| Key | Type | Required | Meaning |
|---|---|---|---|
| `$schema` | string | no | Editor hint, e.g. `"../../schemas/module.schema.json"`. Ignored by tools. |
| `schema_version` | integer, `1` | yes | Contract version. This file is version 1. |
| `id` | string, `^[a-z][a-z0-9-]{1,31}$` | yes | Stable id. Equals the folder name (templates: the folder name without the leading `_`). Used in paths, tags, mutex names. Never changes after the first release. |
| `name` | string, 1–40 chars | yes | Display name, e.g. `"RegionShot"`. |
| `version` | string, SemVer 2.0 | yes | `MAJOR.MINOR.PATCH`, optional `-prerelease`. No `v` prefix. Section 10. |
| `description` | string, 1–200 chars | yes | One sentence for the shell list and the installer checkbox. |
| `source` | `"in-repo"` or `"pinned"` | yes | Section 1. |
| `repo` | URL string or null | yes | Pinned: the GitHub repo URL (required, not null). In-repo: null. |
| `runtime` | object | yes | Section 3.1. |
| `entry` | relative path | yes | The file the shell starts. Section 5. No `..`, no drive letter. |
| `args` | array of strings | yes | Default arguments for a plain start. May hold tokens (section 4). `[]` for none. |
| `console` | boolean | yes | `true`: the module needs a visible console window. `false`: no console window (pythonw, hidden PowerShell). |
| `run_mode` | `"tray"`, `"window"`, `"popup"` or `"oneshot"` | yes | Section 7. |
| `instance` | object | yes | Section 7.2. |
| `actions` | array of objects | yes | Extra named starts the shell shows as menu items. Each: `id` (`^[a-z][a-z0-9-]{0,31}$`, unique in the module), `label` (1–40 chars), `args` (array of strings). `[]` for none. |
| `autostart` | object | yes | `default` (boolean): autostart state on a fresh install. `managed_by`: `"shell"` (the shell starts it at login) or `"module"` (the module owns its own login entry; the shell only shows the state). `default: true` is allowed only for `tray` and `window` modules. |
| `icon` | relative path or null | yes | An `.ico` in the module dir. Null: the shell uses the exe's icon (dotnet, native) or a generic icon. |
| `data_dir` | path string | yes | Section 6. In-repo modules: exactly `%APPDATA%\DeskKit\<id>`. |
| `settings_path` | path string or null | yes | The module's settings file, e.g. `{data_dir}\settings.json`. Null if the module has no settings file. |
| `settings_action` | action id or null | yes | If set, the shell's "Settings" item runs this action. If null and `settings_path` is set, the shell opens `settings_path` with the default app (section 7.4). |
| `launcher` | relative path or null | yes | `launch.cmd` for python and powershell in-repo modules; null otherwise. Section 5.4. |
| `helpers` | array of objects | yes | Section 8. `[]` for none. |
| `licence` | string | yes | SPDX id, e.g. `"MIT"`. `"NOASSERTION"` only while a pinned module has no licence; a release check rejects it. |
| `homepage` | URL string or null | yes | Project page. Null while DeskKit has no remote. |
| `notes` | string or null | no | Free text for builders: known gaps and work still needed. Not shown to users. |

### 3.1 runtime

| Key | Type | Required | Meaning |
|---|---|---|---|
| `type` | `"dotnet"`, `"python"`, `"powershell"`, `"native"` | yes | How the shell starts `entry` (section 5). |
| `min` | string, `^\d+(\.\d+){0,2}$` | yes | Lowest runtime version: .NET `"8.0"`, Python `"3.11"`, PowerShell `"5.1"`. For `native`, the lowest Windows build, e.g. `"10.0.19041"`. |
| `self_contained` | boolean | dotnet only | `true`: the exe carries its own .NET; the shell checks nothing. `false`: the shell checks for the .NET Desktop Runtime `min` (section 5.1). |
| `requirements` | relative path or null | python only | pip requirements file, e.g. `src/requirements.txt`. Null: the module uses the standard library only. |

**DECISION:** dotnet modules SHOULD ship self-contained, like Region Control and the shell. A framework-dependent module makes the user install the .NET Desktop Runtime.

## 4. Path tokens and environment variables

`args`, `actions[].args`, `data_dir`, `settings_path` and `helpers[].detect.paths` may use these. Tools expand tokens first, then environment variables.

| Token | Expands to |
|---|---|
| `{module_dir}` | Absolute path of the module dir, no trailing backslash. |
| `{data_dir}` | The module's expanded `data_dir`. Not allowed inside `data_dir` itself. |
| `{helpers_dir}` | `%LOCALAPPDATA%\DeskKit\helpers` |

Allowed environment variables: `%APPDATA%`, `%LOCALAPPDATA%`, `%TEMP%`. No other `%VAR%`. No drive letters (`C:\`, `D:/`) and no `\Users\` paths anywhere in `module.json` or `suite.json`. The validator rejects them. Only the free-text `notes` (module.json) and `note` (suite.json) may name old paths, to describe work still needed.

## 5. How a module starts

The shell builds one command from `runtime`, `entry` and the arguments. It uses `ProcessStartInfo.ArgumentList` (one list element per argument; .NET quotes them). The working directory is always the module dir.

The shell sets these environment variables for every module it starts. A module MUST run without them.

| Variable | Value |
|---|---|
| `DESKKIT_SHELL` | `1` |
| `DESKKIT_MODULE_ID` | the module id |
| `DESKKIT_MODULE_DIR` | the module dir |
| `DESKKIT_DATA_DIR` | the expanded data dir |

### 5.1 Command per runtime

| `runtime.type` | FileName | Arguments |
|---|---|---|
| `dotnet` | `{module_dir}\<entry>` | `args` |
| `native` | `{module_dir}\<entry>` | `args` |
| `python` | the found `pythonw.exe` (`console: false`) or `python.exe` (`console: true`) | `{module_dir}\<entry>`, then `args` |
| `powershell` | `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` when `min` is below 7; `pwsh.exe` from PATH when `min` is 7 or higher | `-NoProfile -ExecutionPolicy Bypass -File {module_dir}\<entry>`, then `args`. With `console: false`, add `-NonInteractive -WindowStyle Hidden` before `-File`, and set `CreateNoWindow = true`. |

dotnet with `self_contained: false`: before the start, the shell runs `dotnet --list-runtimes` and looks for `Microsoft.WindowsDesktop.App` at version `min` or higher. If missing, it does not start the module and shows: "<name> needs the .NET <min> Desktop Runtime. Install it from https://dotnet.microsoft.com/download/dotnet/<min> and try again."

### 5.2 Finding Python

The shell and every python module's `launch.cmd` use the user's installed Python. DeskKit never ships Python.

The shell finds Python in this order and keeps the first one at version `min` or higher:

1. The py launcher: run `py.exe -3 -c "import sys; print(sys.executable)"`. `pythonw.exe` is the file next to the printed `python.exe`.
2. `python.exe` on PATH. Skip any path under `%LOCALAPPDATA%\Microsoft\WindowsApps\` (the Store alias stub).
3. Check the version with `<python.exe> -c "import sys; print('%d.%d.%d' % sys.version_info[:3])"`.

If none is found, the shell does not start the module and shows exactly:

> Python <min> or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.

The shell caches the found path until it restarts.

Every python entry file MUST check its own version first, before any import outside the standard library. If `sys.version_info` is below `min`, it writes the same message to stderr (section 9.2) and exits with code 4.

### 5.3 Python packages (venv bootstrap)

A python module with `runtime.requirements` set MUST bootstrap its own venv. The module does this itself, so it works with no shell. The shell only starts the entry file with the found Python.

The venv lives at `%LOCALAPPDATA%\DeskKit\<id>\venv`. **DECISION:** a venv can be 235 MB (RegionShot), so it goes in the local profile, not in the roaming data dir.

At start, before importing any package from `requirements.txt`, the entry file runs these steps:

1. If `sys.prefix` already equals the venv folder, skip to step 6.
2. Read `requirements.txt` and compute its SHA256 (`H`). The marker file is `<venv>\deskkit-requirements.sha256`.
3. The venv is "good" when `<venv>\Scripts\python.exe -c "pass"` exits 0 and the marker holds `H`.
4. If the venv is not good: delete the folder, run `<current python> -m venv <venv>`, run `<venv>\Scripts\python.exe -m pip install --disable-pip-version-check -r <requirements.txt>`, then write `H` to the marker. On any failure, write `ERROR: Could not install Python packages for <name>: <reason>` to stderr and exit with code 6.
5. Run `<venv>\Scripts\pythonw.exe` (or `python.exe` when `console` is true) with the entry file and the same arguments. Pass the same standard handles. Wait for it and exit with its exit code. The parent stays alive, so the shell's process handle stays valid.
6. Continue as the real app.

The argument `--deskkit-setup` runs steps 1 to 4 only and exits 0. The installer MAY call it to build the venv at install time.

A first run downloads packages and takes time. A windowed module SHOULD show a small "Setting up <name> (first run)" window while step 4 runs.

### 5.4 launch.cmd (running alone)

Every python and powershell in-repo module ships `launch.cmd` at its module root. The user double-clicks it to run the module with no shell. The shell never uses it. Rules:

- CRLF line endings (cmd.exe misreads LF-only files).
- Find Python with `py -3`, then `python` on PATH. If neither exists, print the section 5.2 message, `pause`, and `exit /b 4`.
- Pass `%*` through. `oneshot` and `popup` modules run in the foreground and end with `exit /b %errorlevel%`.
- `tray` and `window` modules start with `pyw -3` or `pythonw` through `start ""`, so no console stays open, and end with `exit /b 0`. `modules/regionshot/launch.cmd` is the reference copy.
- PowerShell modules call `powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\<file>.ps1" %*`.

`modules/_template-python/launch.cmd` is the reference copy for a foreground module.

## 6. Data and settings

- Data dir of an in-repo module: exactly `%APPDATA%\DeskKit\<id>\`. Settings, state and logs all go there. Logs go in `{data_dir}\logs\`.
- A module creates its data dir on first write. It never writes next to its own code (the install dir can be read-only).
- Settings file: `{data_dir}\settings.json`, UTF-8, a JSON object. A missing file or a missing key means "use the default". The module writes the file with all keys and defaults on first run, so the user can edit it.
- A module MUST NOT read or write the registry for its own settings (QSettings to the registry is not allowed). It MAY read the registry for Windows facts (Cable Listen reads MMDevices).
- Pinned modules keep the data dir their own code uses, and `data_dir` records it (Region Control: `%APPDATA%\RegionControl`). A pinned module that writes next to its exe must change before its next release. Taskbar Groups is in-repo and writes to `%APPDATA%\DeskKit\taskbargroups`.
- Caches that are large and rebuildable (venvs, downloads) go in `%LOCALAPPDATA%\DeskKit\<id>\`.
- Uninstall keeps `%APPDATA%\DeskKit\` and `%LOCALAPPDATA%\DeskKit\`. **DECISION:** user data survives uninstall; the uninstaller says where it is.

Shell state lives in `%APPDATA%\DeskKit\shell\`: `state.json` (per module: `autostart` true or false) and `logs\<id>.log` (section 9.3).

## 7. Run modes and how the shell drives them

### 7.1 The four run modes

| `run_mode` | What it is | Example | Shell's main control | "Running" means | Stop | Autostart |
|---|---|---|---|---|---|---|
| `tray` | Long-running, own tray icon, no main window | Region Control | On/Off switch | its mutex exists | stop protocol (7.3) | allowed |
| `window` | Long-running, has a main window | RegionShot, Cable Listen | Open / Close button | its mutex exists | stop protocol (7.3) | allowed |
| `popup` | Opens, does one thing, exits by itself | LightweightTaskbarGroups | Open button (runs `args`) | the process the shell started is alive | none; it exits by itself | not allowed |
| `oneshot` | Runs a command and exits | Eye Saver | Run button (runs the default action) | the process the shell started is alive | killed after 60 s | not allowed |

"Toggle" in the shell means:

- `tray` and `window`: if running, stop it; else start it with `args`.
- `oneshot`: run the action with id `toggle` if one exists, else start with `args`.
- `popup`: start with `args` (there is nothing to stop).

All four modes show their `actions` as menu items. Running an action starts the entry with that action's `args` instead of `args`. For `tray` and `window`, an action on a running module is still started; the module's single-instance code decides what to do with it.

**DECISION:** a `oneshot` process still alive after 60 s is killed with its process tree, and the shell shows "<name> did not finish in 60 seconds".

### 7.2 instance and the single-instance rule

| Key | Type | Meaning |
|---|---|---|
| `single` | boolean | `true` for every `tray` and `window` module. |
| `mutex` | string or null | Named mutex the running module holds. In-repo: exactly `Local\DeskKit.<id>`. Pinned: the name its code uses, or null. |
| `show_event` | string or null | In-repo `tray` and `window`: exactly `Local\DeskKit.<id>.show`. Null for pinned modules and for `popup` and `oneshot`. |
| `stop_event` | string or null | In-repo `tray` and `window`: exactly `Local\DeskKit.<id>.stop`. Null for pinned modules and for `popup` and `oneshot`. |

Single-instance rule for in-repo `tray` and `window` modules (Win32 calls; Python uses `ctypes`):

1. At start, call `CreateMutexW(NULL, FALSE, mutex)`. If `GetLastError()` is `ERROR_ALREADY_EXISTS` (183), another copy runs. Call `OpenEventW(EVENT_MODIFY_STATE, FALSE, show_event)`, then `SetEvent`, and exit with code 0. Do not open a second copy.
2. The first copy creates both events with `CreateEventW(NULL, FALSE, FALSE, name)` (auto-reset, not signalled).
3. A background thread waits on both events with `WaitForMultipleObjects`.
4. On `show_event`: a `window` module restores and brings its main window to the front. A `tray` module opens its main panel or menu, or does nothing if it has none.
5. On `stop_event`: the module saves its state, undoes any system change it owns (Cable Listen turns listening off), and exits with code 0 within 5 seconds. GUI work runs on the GUI thread (Qt `QMetaObject.invokeMethod`, tkinter `after`).
6. Windows releases the mutex when the process ends.

`popup` and `oneshot` modules set `single: false` and all three names to null. Two copies at once must be safe.

### 7.3 Stop protocol (shell)

The shell stops a `tray` or `window` module in this order and stops at the first step that works:

1. If `stop_event` is set: `OpenEventW` + `SetEvent`. Wait up to 5 s for the mutex to disappear (poll `OpenMutexW` every 250 ms).
2. If the shell started the process itself and still holds it: post `WM_CLOSE` to every top-level window of that process. Wait up to 5 s.
3. If the shell holds the process: `Process.Kill(entireProcessTree: true)`.

The shell never kills a module process it did not start. If steps 1 and 2 cannot work (a pinned module started at login by itself), the shell shows "<name> was not started by DeskKit. Close it from its own tray icon." and does nothing else.

### 7.4 Other shell behaviour

- **Command line:** `DeskKit.exe --run <id> [--action <action id>]` starts one module exactly as section 5 says (with `args`, or with the action's `args`) and exits 0 at once, without a tray icon. Everything after a bare `--` is passed to the module as extra arguments, appended after `args` (`--run taskbargroups -- Tools` starts the popup for group "Tools"). `--` and `--action` do not combine. It exits 1 with a message box if the id or action is unknown, and with the section 5 messages if the runtime is missing. Shortcuts and PATH scripts use it (section 13). `DeskKit.exe` with no arguments starts the tray; a second tray copy exits at once (mutex `Local\DeskKit.Shell`).
- **Discovery:** the shell lists modules by reading `<install dir>\modules\*\module.json`, skipping folders that start with `_`. A file that fails to parse shows as "broken" with the parse error. `--modules-dir <path>` points the shell at another folder (for development: the repo's `modules\`).
- **Running state:** the shell checks every module every 2 s. `tray` and `window`: `OpenMutexW(SYNCHRONIZE, FALSE, mutex)` succeeds means running. A pinned module with `mutex: null`: running only if the shell's own process for it is alive.
- **Settings item:** run `settings_action` if set. Else, if `settings_path` is set: open the file with the default app if it exists, else open its folder (create the folder first). Else, hide the item.
- **Autostart:** the shell itself starts at login through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DeskKit`. At its own start it starts every `tray` and `window` module with `managed_by: "shell"` whose autostart is on in `state.json` (or `autostart.default` when `state.json` has no entry). For `managed_by: "module"`, the shell shows the module's own state and never writes the module's login entry.
- **Crash:** if a `tray` or `window` module started by the shell exits with a code other than 0, the shell shows a notification with the last `ERROR:` line (section 9.2). It does not restart the module. **DECISION:** no auto-restart in 0.1.
- **Helpers:** if a module exits with code 3, the shell offers "Download <helper name>" for each helper of that module whose detect rule fails (section 8.3).

### 7.5 Testing rule: never touch the user's running modules

Nate uses these tools every day. Testers, agents and CI MUST NOT:

- kill, stop or restart a running copy of any module that they did not start themselves in that test;
- overwrite, delete or move the files of an installed or running module, pinned or in-repo (for example `C:\Program Files\Region Control\` or the live LightweightTaskbarGroups copy);
- change a module's data dir under the real `%APPDATA%`;
- touch Region Control's source folder or its running process. Region Control is only a pinned release (v0.1.0) to DeskKit.

Run tests against a copy in a temp folder, with `APPDATA` and `LOCALAPPDATA` pointed at temp folders where the module allows it. If a test would need to stop the user's copy, skip it and report that it was skipped.

## 8. Helpers

DeskKit never bundles a helper in an installer or a release zip. A module finds the helper; the shell or the installer may download it on request.

### 8.1 helpers[] fields

| Key | Type | Meaning |
|---|---|---|
| `id` | string, `^[a-z][a-z0-9-]{1,31}$` | Helper id, e.g. `ffmpeg`. Unique in the suite. Two modules that use the same id MUST have the same `download` block. |
| `name` | string | Display name, e.g. `"FFmpeg"`. |
| `required` | boolean | `true`: the module cannot start without it (exit 3 at start). `false`: only some features need it; the module disables them and says why. |
| `purpose` | string | What it is for, shown to the user, e.g. `"Screen recording"`. |
| `detect` | object | Section 8.2. |
| `download` | object | `url` (HTTPS, a direct file), `sha256` (64 lowercase hex), `archive` (`"zip"` or `"none"`), `page` (HTTPS page for a manual download). |
| `licence` | string | SPDX id or `"Freeware"`. Shown to the user before a download. |

### 8.2 Detect rule

The module (and the shell, for its "Download" offer) checks these in order and uses the first file that exists:

1. `detect.setting` (string or null): a key in the module's `settings.json` that holds a full path set by the user.
2. `detect.paths` (array): paths with tokens. One `*` may stand for exactly one folder name, to match a versioned folder from a zip (e.g. `{helpers_dir}\ffmpeg\*\bin\ffmpeg.exe`). If several match, use the last in sorted order.
3. `detect.on_path` (string or null): a file name searched on `PATH`.

The module expands `{helpers_dir}` itself as `%LOCALAPPDATA%\DeskKit\helpers`. Module code MUST NOT contain fixed fallbacks such as `C:\ffmpeg` or `C:\Tools`.

### 8.3 Download

The suite installer offers an unchecked task "Download missing helpers" that runs this for every helper of a chosen module whose detect rule fails. The shell runs the same steps from its "Download" offer. A module running alone never downloads; it shows the missing helper's name, `purpose` and `page` and exits 3 (required) or disables the feature (not required).

1. Show the helper `name`, `licence` and `page`, and ask the user to confirm.
2. Download `url` to a temp file over HTTPS.
3. Check SHA256 against `sha256`. On a mismatch, delete the file, show "The download of <name> did not match its checksum. Download it yourself from <page>", and stop. Never run or keep a file that fails the check.
4. `archive: "zip"`: extract to `{helpers_dir}\<helper id>\`. `archive: "none"`: save as `{helpers_dir}\<helper id>\<file name from url>`.
5. Run the detect rule again and report the result.

**DECISION:** every helper download is pinned by SHA256, even NirSoft's, whose URL always serves the newest version. When NirSoft updates SoundVolumeView, the check fails and the user gets the manual page until someone updates `module.json`. Safety beats convenience.

## 9. Exit codes and errors

### 9.1 Exit codes

| Code | Meaning | Shell shows |
|---|---|---|
| 0 | Success, or a second copy handed over to the first (section 7.2) | nothing |
| 1 | General error | the last `ERROR:` line |
| 2 | Wrong arguments | the last `ERROR:` line |
| 3 | A required helper is missing | the line, plus the "Download" offer (7.4) |
| 4 | Runtime missing or too old (Python, PowerShell 7, .NET) | the line |
| 5 | A needed device or resource is not there (e.g. no DDC/CI monitor) | the line |
| 6 | Package install failed (venv bootstrap, 5.3) | the line |

Pinned modules may not follow this table. The shell treats any code other than 0 as an error and shows the module's last stderr line.

### 9.2 Error messages

- On a fatal error, a module writes one line `ERROR: <message>` to stderr, then exits with the code above. The message names the problem and what the user can do.
- If `DESKKIT_SHELL` is not set and `console` is false, the module also shows the same message in a message box (there is no console to read).
- If `DESKKIT_SHELL` is set, the module does not show a message box. The shell shows the error.

### 9.3 Shell log capture

The shell redirects stdout and stderr of every module it starts, except a module with `console: true` (that module gets a visible console and no redirect), to `%APPDATA%\DeskKit\shell\logs\<id>.log`. It appends a line `=== <ISO time> start <args> ===` per start. When the file passes 1 MB, the shell renames it to `<id>.log.1` (replacing an old one) and starts a new file.

## 10. Versions, tags and releases

### 10.1 Module versions

- Every module has its own SemVer in `module.json` `version`.
- MAJOR: the settings file format breaks, `args` or `actions` change meaning, or `run_mode` changes. MINOR: new features. PATCH: fixes.
- Every in-repo release adds a `CHANGELOG.md` heading for that version.

### 10.2 In-repo module releases

- Tag: `<id>/vX.Y.Z`, e.g. `regionshot/v0.1.0`. The tag version equals `module.json` `version`.
- Assets: `<id>-X.Y.Z.zip` and `SHA256SUMS.txt` (lines of `<sha256>  <file name>`, two spaces).
- The zip holds one top folder `<id>\` with the payload (section 2.1). Unzip it anywhere and run `launch.cmd` (script modules) or `bin\<exe>` (compiled modules).
- Release title: `<name> X.Y.Z`. A version with a `-prerelease` part is a GitHub pre-release.
- **DECISION:** in-repo modules ship a zip only in 0.1, no per-module installer.

### 10.3 Pinned module releases

- A pinned module is released from its own repo with its own tags (Region Control uses `vX.Y.Z`).
- To use a new release in DeskKit: update the module's entry in `suite.json` (`version`, `tag`, `url`, `sha256`) and `version` in `modules/<id>/module.json`. Run the validator. Bump the suite version.
- The asset DeskKit pins MUST be a portable build: one self-contained exe (`asset_type: "exe"`) or a zip with the exe inside (`asset_type: "zip"`). DeskKit does not run another product's installer.

### 10.4 Suite version and installer

- Suite tag: `vX.Y.Z`, equal to `suite.json` `version`. First suite: `v0.1.0`.
- Bump the suite version when the shell changes or any module version in `suite.json` changes. The shell has no tag of its own; its version is `suite.json` `shell.version` and the `<Version>` in its project file.
- Installer file: `DeskKit-Setup-X.Y.Z.exe`, with `SHA256SUMS.txt`. Inno Setup, per user (section 2.3), one checkbox per module, all checked by default, with the module's `description` as the checkbox text.
- Inno Setup `AppId={{60B9D8BF-5C25-489A-B8BA-99D41F974319}`. **DECISION:** fixed now; never change it, or upgrades install side by side.
- The build stages the payload in `out\stage\`:
  1. Run `tools/validate-modules.py --release`. Stop on any error.
  2. Publish the shell (`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`) to `out\stage\DeskKit.exe`.
  3. In-repo modules: run `build.ps1` for compiled ones, then copy the payload to `out\stage\modules\<id>\`.
  4. Pinned modules: download `url` to `out\cache\<id>-<version>\`, check `sha256` (stop on a mismatch), then for `exe` copy it to `out\stage\modules\<id>\<entry>`; for `zip` extract it into `out\stage\modules\<id>\bin\` and check that `entry` exists. Copy `module.json`, `README.md` and `icon.ico`.
  5. Copy `suite.json` and `LICENSE`. Run ISCC on `packaging\DeskKit.iss`.
- A pinned module with `url: null` is skipped with a warning by a normal build. `--release` makes it an error, so no suite tag ships without it.
- Region Control may already be installed by its own installer (Inno `AppId` `{6F1D2B7E-3C54-4E0A-9B6A-5D2C8A41E7F3}`; uninstall key `...\Uninstall\{6F1D2B7E-3C54-4E0A-9B6A-5D2C8A41E7F3}_is1` under HKCU or HKLM). If that key exists, the suite installer starts the `regioncontrol` checkbox unchecked, with the text "(already installed separately)". **DECISION:** two copies would fight over autostart; the mutex stops them running together, but the user should keep one.
- Region Control's own installer turns off Windows' snap-on-drag and restores it at uninstall (its `RegionControl.iss`, `[Code]` "native snap off / restore"). The suite installer MUST copy that task and its uninstall restore for the `regioncontrol` module, or say on its last page that the user must turn Windows snapping off. Read `RegionControl.iss` from the v0.1.0 release tag on GitHub; never from Region Control's local folder.

## 11. suite.json

The schema is `schemas/suite.schema.json`. Unknown keys are an error.

| Key | Type | Meaning |
|---|---|---|
| `$schema` | string, optional | Editor hint. |
| `schema_version` | `1` | Contract version. |
| `name` | `"DeskKit"` | Suite name. |
| `version` | SemVer | Suite version (10.4). |
| `publisher` | string | `"inNATE Tools"`. |
| `licence` | string | `"MIT"`. |
| `repo` | URL, optional | The suite repo, `https://github.com/exterminathan/DeskKit`: update source of the shell and every in-repo module (16.1). |
| `shell` | object | `version` (SemVer), `path` (`"shell"`). |
| `modules` | array | One entry per module, in installer order. |

Each `modules[]` entry:

| Key | Type | In-repo | Pinned |
|---|---|---|---|
| `id` | string | required | required |
| `version` | SemVer | required; equals `module.json` | required; equals `module.json` |
| `source` | `"in-repo"` / `"pinned"` | required; equals `module.json` | required; equals `module.json` |
| `path` | string | `modules/<id>` | `modules/<id>` |
| `repo` | URL | not allowed | required |
| `tag` | string or null | not allowed | the release tag, or null if no release exists |
| `url` | URL or null | not allowed | the release asset URL, or null if no release exists |
| `sha256` | hex or null | not allowed | 64 lowercase hex; null only when `url` is null |
| `asset_type` | `"exe"` / `"zip"` | not allowed | required |
| `asset_pattern` | string | not allowed | optional; the release asset file name with `{version}`, e.g. `RegionControl-Portable-{version}.exe` (16.1) |
| `note` | string | optional | optional |
| `status` | `"planned"` | optional | not allowed |

The validator checks that every non-`_` folder in `modules/` has a `suite.json` entry, and every entry has a folder.

**Planned modules.** `status: "planned"` (in-repo only) marks a scaffold with no features yet. The validator checks it like any other entry (folder, `module.json`, version). The suite build does not stage it, so the installer has no component for it. The shell skips it when it scans a modules dir whose parent `suite.json` marks it planned, so `--list`, the main window and `--check-updates` do not show it. Remove `status` in the same change that ships the first real feature.

## 12. The validator

`python tools/validate-modules.py [--root <repo>] [--release]` checks:

- every `modules/*/module.json` (including `_template`) and `suite.json` against the schemas (with `jsonschema` if it is installed, else a built-in checker for the subset the schemas use);
- the rules in this file that a schema cannot express: id equals folder name; in-repo `data_dir`, `mutex`, `show_event` and `stop_event` names; no drive letters or `\Users\` paths; only allowed `%VAR%`s; entry, launcher, icon and requirements files exist for in-repo modules (compiled in-repo modules: `build.ps1` exists instead of `entry`); `settings_action` names a real action; autostart only for `tray` and `window`; helpers with the same id have the same `download`; `suite.json` matches the module folders.
- `--release` also fails on `url: null` and `licence: "NOASSERTION"`.

Exit 0 when all pass, 1 when any error. Warnings never fail a normal run.

## 13. Migration: repoint old shortcuts and PATH scripts

Today Nate starts these tools from `.lnk` files in a shortcuts folder (grouped by LightweightTaskbarGroups), from `.bat` forwarders in a scripts folder on PATH, and from a pinned taskbar entry and a Start Menu entry. After the suite installs, these launch points must start the installed modules. `tools/migrate-launchers.ps1` (written by G6 Migrate, 2026-11-10) does this. Real dry run on Nate's PC: `docs/migration-dry-run.txt`. Tests: `tests/test_migrate.ps1`.

Parameters (every default comes from an environment variable; no user name in the script):

| Parameter | Default | Meaning |
|---|---|---|
| `-InstallDir` | `%LOCALAPPDATA%\Programs\DeskKit` | Where `DeskKit.exe` and `modules\` are. |
| `-ShortcutsDir` | `%SystemDrive%\Shortcuts` | Folder of the old `.lnk` files. |
| `-ScriptsDir` | `%SystemDrive%\Scripts` | Folder of the PATH forwarders (`.bat`, `.cmd`). |
| `-PinnedDir` | `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar` and `%APPDATA%\Microsoft\Windows\Start Menu\Programs` | Pinned taskbar and Start Menu folders (an array, or one comma-separated string). |
| `-IncludeTaskbarPins` | off | Without it, entries in `-PinnedDir` are listed as SKIP and never changed, even with `-Apply`. |
| `-TaskbarGroupsDir` | empty: the folder of any shortcut that targets `TaskbarGroups.exe` | The old TaskbarGroups folder. Read only. |
| `-SoundVolumeViewDir` | `%SystemDrive%\Tools\SoundVolumeView` | The old SoundVolumeView folder. Read only. |
| `-BackupRoot` | `%USERPROFILE%\DeskKit-migration-backup` | Where backups go. |
| `-Only <id,id>` | all | Limit the run to these modules. |
| `-Apply` | off | Write the changes. |

Rules:

1. Dry run by default. It prints a table of every file (action, module, kind, path), then for each file the current and new target, arguments, working folder and icon, any icon or helper copy, and a note. It also lists every file it did not match. It changes nothing. Paths show as `%LOCALAPPDATA%`, `%APPDATA%` or `%USERPROFILE%`, so the output names no user. `-Apply` writes the changes.
2. Actions: CHANGE (would change, or changed), DONE (already migrated: a `.lnk` that targets `DeskKit.exe --run <id>`, a `.bat` that forwards to it, a helper or settings copy already in place), SKIP (needs `-IncludeTaskbarPins`, or the target folder needs a hand copy), LEFT ALONE (inside the old TaskbarGroups folder). A second `-Apply` finds nothing to change and makes no backup.
3. Backup and restore. Before `-Apply` changes a file, it copies it to `<BackupRoot>\<yyyyMMdd-HHmmss>\` and keeps the folder structure (`C:\Shortcuts\Region Shot.lnk` goes to `C\Shortcuts\Region Shot.lnk`). The folder holds `manifest.json` and `restore.ps1`. `restore.ps1` puts every changed file back byte for byte. `restore.ps1 -RemoveCreated` also deletes what the run created (icon copies, the helper copy, the settings copy). After several runs, restore the newest folder first.
4. It keeps each `.lnk` file at its path and name, and changes only its target, arguments, working folder and icon. Window style and hotkey stay. LightweightTaskbarGroups' `group.json` stores `.lnk` paths, so the groups keep working.
5. New `.lnk` target: `<install dir>\DeskKit.exe`, arguments `--run <id>` (plus `--action <action id>` or `-- <arguments>` where the table says so), working folder `<install dir>`. Icon: the first rule that applies.
   1. The module's `icon.ico` in the install dir, if the module has one.
   2. An old icon inside the old TaskbarGroups folder: the module's own exe if it has the same file name (`TaskbarGroups.exe`). Else a copy of the icon file in `%APPDATA%\DeskKit\taskbargroups\icons\` (same name; `<name>-2` if a different file has that name; an equal file is reused).
   3. An old icon file that no longer exists: `DeskKit.exe,0`.
   4. Else the old icon, unchanged. Cable Listen keeps `SndVol.exe,0` and Eye Saver keeps `shell32.dll,255`.
6. A PATH forwarder `.bat` is rewritten to two lines, with CRLF endings:
   `@echo off`
   `"%LOCALAPPDATA%\Programs\DeskKit\DeskKit.exe" --run <id> -- %*`
   With a non-default `-InstallDir`, the line holds that absolute path instead.
7. It matches a launch point to a module by its old target and arguments (table below). It changes nothing it cannot match, and lists it as "not matched". One exception: a shortcut that matches no module but has its icon inside the old TaskbarGroups folder gets only its icon repointed (rule 5.2). Today that is `Doggosync Companion.lnk`.
8. It never changes or deletes anything in an old tool folder (`C:\TaskbarGroups`, `D:\My Software\RegionShot`, `C:\Scripts\cable_listen.pyw`, `C:\Tools\SoundVolumeView`, ClaudeGlobalSetup `tools\monitor`). It only reads and copies from them. Nate deletes them after he checks the modules.
9. `-Apply` refuses (exit 2, nothing changed) if `<install dir>\DeskKit.exe` is missing, or if `<install dir>\modules\<id>\module.json` is missing for a module it would point a `.lnk` or `.bat` at. `-Only` leaves such a module out.
10. Cable Listen helper. If `-SoundVolumeViewDir` holds `SoundVolumeView.exe` and `%LOCALAPPDATA%\DeskKit\helpers\soundvolumeview\` is missing or empty, `-Apply` copies the whole folder there, every file unchanged (NirSoft's terms allow free copies of the unmodified package). The module then finds it by its detect rule (section 8.2). If the target folder holds other files but no `SoundVolumeView.exe`, the row is SKIP.
11. Cable Listen settings. If an old `cable_listen.json` sits next to the old `cable_listen.pyw` (or in `-ScriptsDir`), and the data dir has neither `settings.json` nor `cable_listen.json`, `-Apply` copies it to `%APPDATA%\DeskKit\cablelisten\`. The module turns it into `settings.json` on its first run (its README holds the key mapping).
12. Exit codes: 0 done or dry run, 1 a change failed (the backup holds every file touched so far), 2 refused.

Known launch points (from `docs/inventory.md` and `docs/test-report.md` step 6):

| Module | Launch point | Matched by | New target and arguments | Icon |
|---|---|---|---|---|
| `regionshot` | `C:\Shortcuts\Region Shot.lnk` | target or arguments contain `RegionShot.bat` | `DeskKit.exe --run regionshot` | module `icon.ico` (the old icon path, `D:\Software\...`, is broken today) |
| `regionshot` | `C:\Scripts\region_shot.bat` (PATH forwarder; today it points at `D:\Software\RegionShot`, which does not exist) | file contains `RegionShot.bat` | `--run regionshot -- %*` | n/a |
| `cablelisten` | `C:\Shortcuts\Cable Listen.lnk` | arguments contain `cable_listen.pyw` | `DeskKit.exe --run cablelisten` | old icon kept |
| `cablelisten` | helper folder `C:\Tools\SoundVolumeView\` | folder holds `SoundVolumeView.exe` | copied to `%LOCALAPPDATA%\DeskKit\helpers\soundvolumeview\` (rule 10) | n/a |
| `eyesaver` | `C:\Shortcuts\Eye Saver.lnk` | arguments contain `monitor.ps1` and `eyesaver` | `DeskKit.exe --run eyesaver --action toggle` | old icon kept |
| `taskbargroups` | pinned `...\User Pinned\TaskBar\Tools.lnk` (target `TaskbarGroups.exe`, arguments `"Tools"`) | target file name is `TaskbarGroups.exe`; the arguments name the group | `DeskKit.exe --run taskbargroups -- Tools` (needs `-IncludeTaskbarPins`) | copy of `GroupIcon.ico` in `%APPDATA%\DeskKit\taskbargroups\icons\` |
| `taskbargroups` | Start Menu `TaskbarGroups.lnk` (no arguments) | same | `DeskKit.exe --run taskbargroups` (config window; needs `-IncludeTaskbarPins`) | the installed `modules\taskbargroups\bin\TaskbarGroups.exe,0` |
| `taskbargroups` | `C:\TaskbarGroups\Shortcuts\Tools.lnk` | inside the old folder | listed, LEFT ALONE | n/a |
| `taskbargroups` | `C:\Shortcuts\Doggosync Companion.lnk` (not a module) | icon inside the old TaskbarGroups folder | target unchanged; icon only | copy of `Doggosync Companion.ico` in `%APPDATA%\DeskKit\taskbargroups\icons\` |
| `regioncontrol` | none | n/a | Leave its own install and its HKCU Run entry alone. | n/a |

Not tested yet: whether Windows keeps using an edited pinned taskbar `.lnk` (it may cache pin entries). If the pinned entry misbehaves after `-Apply -IncludeTaskbarPins`, unpin it and pin the repointed `.lnk` again, or run `restore.ps1`. The group's own `iconPath` in `group.json` is the Taskbar Groups module's job: it copies and rewrites it on its first run.

## 14. How to add a module (checklist for Nate)

1. Pick an id: lowercase letters and digits, e.g. `clipnotes`. It never changes later.
2. Copy `modules/_template/` (C# .NET 8, the default) to `modules/<id>/`. Use `modules/_template-python/` only for a Python tool.
3. Write the code in `modules/<id>/src/`. C#: rename the project and keep `build.ps1` building into `bin/`.
4. Edit `module.json`: `id`, `name`, `description`, `entry`, `args`, `run_mode`, `instance` names (`Local\DeskKit.<id>`, `.show`, `.stop`), `data_dir`, `settings_path`, `helpers`.
5. Keep paths out of the code: settings go in `%APPDATA%\DeskKit\<id>\settings.json`; no `C:\`, `D:\` or user names.
6. `tray` or `window` module: call the single-instance code (section 7.2; the C# template has `SingleInstance.cs`). Every module: use the exit codes and `ERROR:` lines (section 9).
7. Add an entry to `suite.json` (`id`, `version`, `source: "in-repo"`, `path`) and bump the suite version. A scaffold with no features yet gets `"status": "planned"` (section 11), so the installer and the shell skip it; delete that key when it ships.
8. Write `README.md` and `CHANGELOG.md`.
9. Run `python tools/validate-modules.py` and `python -m unittest discover -s tests`. Both must pass.
10. Run `build.ps1` (C#) or `launch.cmd` (Python), then start it once from the shell.

## 15. Decisions in this contract

Each is marked **DECISION** above. Nate can change any of them.

1. dotnet modules ship self-contained (3.1).
2. Python venvs live in `%LOCALAPPDATA%\DeskKit\<id>\venv`, built by the module itself (5.3).
3. User data survives uninstall (6).
4. A `oneshot` is killed after 60 s (7.1).
5. No auto-restart of a crashed module in 0.1 (7.4).
6. Every helper download is pinned by SHA256, NirSoft too (8.3).
7. In-repo modules ship a zip only in 0.1 (10.2).
8. The suite installer `AppId` is fixed now (10.4).
9. The shell lists modules by scanning `modules\*\module.json`, not from `suite.json` (7.4).
10. The shell never kills a module process it did not start (7.3).
11. The suite installer leaves the Region Control checkbox unchecked when Region Control's own install exists (10.4).
12. Migration keeps each `.lnk` in place and points it at `DeskKit.exe --run <id>` (13). Pinned taskbar and Start Menu entries change only with `-IncludeTaskbarPins`.

## 16. Updates

Added 2026-11-10 by G8 Updater, from Nate's rulings: the shell tells the user about new releases and updates a module with one click. The feed is GitHub Releases. Nothing here changes sections 1 to 15.

Words used in this section:

- **Update source**: the GitHub repo and tag rule where the shell looks for a component's releases.
- **Component**: one module, or the shell itself.
- **Feed base**: `https://api.github.com`, or the value of the environment variable `DESKKIT_FEED_BASE` (tests only, 16.10).

### 16.1 Update source of each component

| Component | Repo | Tag | Asset |
|---|---|---|---|
| In-repo module | `suite.json` `repo` (the suite repo) | `<id>/vX.Y.Z` (10.2) | `<id>-X.Y.Z.zip` |
| Pinned module | its own `module.json` `repo` | `vX.Y.Z` (10.3) | `suite.json` entry `asset_pattern` with `{version}` replaced; without `asset_pattern`, the file name of the entry's `url` with its pinned `version` replaced by the new one |
| Shell | `suite.json` `repo` | `vX.Y.Z` (10.4) | `DeskKit-Setup-X.Y.Z.exe` |

- The shell reads `suite.json` from the parent folder of its modules dir: `<install dir>\suite.json` when installed, the repo root in development.
- If `suite.json` or its `repo` is missing, in-repo modules and the shell show "no update source". Pinned modules still use their `module.json` `repo`.
- The shell reads one URL per repo: `<feed base>/repos/<owner>/<repo>/releases?per_page=100`. One request covers the shell and every in-repo module, because they share the suite repo. Region Control adds a second request.
- The ids `all` and `shell` are reserved for the command line (16.9). No module may use them.

### 16.2 Choosing a release

1. Skip drafts.
2. Keep releases whose tag matches the component's tag rule (16.1). Read the version from the tag: the part after `<id>/v` or after `v`. Skip a tag whose version is not SemVer 2.0.
3. Pre-releases: a version with a `-prerelease` part (for example `0.2.0-beta.1`) counts only when the user turns on "Include pre-releases" (16.8). **DECISION:** the GitHub "pre-release" flag on a release is ignored; only the SemVer `-` part counts. Today's Region Control releases (`v0.1.0`, `v0.1.1`) carry the GitHub flag but plain versions, so the default finds them.
4. Keep the release with the highest version (16.4). It is an update only if it is higher than the installed version. The same or a lower version is ignored.
5. The release must hold the asset named in 16.1. If it does not, the shell logs one line and shows "no asset" for that component; it never picks another file.

### 16.3 Checksum

The shell never installs or runs a file that fails this check.

1. If the release has an asset `SHA256SUMS.txt` (or `SHA256SUMS`), download it and read the line for the asset's file name (`<sha256>  <file name>`, 10.2).
2. Else, use the asset's `digest` field from the release JSON (`sha256:<hex>`), which GitHub fills for new uploads.
3. If both exist and disagree, refuse.
4. If neither exists, refuse: "<name> <version> has no checksum. Download it yourself from <release page>."
5. After the download, compute SHA256. On a mismatch, delete the downloaded file, leave the module untouched, show "The download of <name> <version> did not match its checksum. Nothing was changed.", and record `failed` (16.8).

### 16.4 Version compare

SemVer 2.0 precedence: compare MAJOR, MINOR and PATCH as numbers. A version with a pre-release part is lower than the same version without one (`0.2.0-beta.1` < `0.2.0`). Pre-release identifiers compare dot by dot: numbers as numbers, text by ASCII, numbers below text, and a shorter list is lower when all shared parts are equal. Build metadata (`+...`) is ignored.

### 16.5 What the shell can update

| Case | What the shell does |
|---|---|
| A module the suite installer put in the user's install dir (`%LOCALAPPDATA%\Programs\DeskKit\modules\<id>`) | Update with one click (16.6). |
| The modules dir is not writable (the shell cannot create and delete `<modules dir>\_update\.write-test`) | Notify only: "<name> <version> is available. DeskKit cannot write to <modules dir>." |
| A module dir that holds `build.ps1`, or a modules dir whose parent holds `schemas\module.schema.json` (the repo: development with `--modules-dir`) | Notify only. The shell never writes into source folders, not even the write test. `build.ps1` never ships in a payload (2.1). |
| Region Control installed separately by its own installer (its uninstall key exists, 10.4) and no `modules\regioncontrol` in the modules dir | Notify only. Compare the key's `DisplayVersion` with the feed. Offer "Open release page" (the release's `html_url`). Never write to its install folder (for example `C:\Program Files\Region Control`), its data dir or its process. |
| A module that is running | Ask first (16.6 step 4). |
| The shell's own exe | A running exe cannot replace itself. On the user's click: download `DeskKit-Setup-X.Y.Z.exe`, check it (16.3), start it with `/SILENT /SUPPRESSMSGBOXES /NORESTART`, then exit the tray (modules keep running). The installer replaces the shell and starts it again. |

### 16.6 Update steps (one module)

1. Find the update (16.2). Download the asset to `%LOCALAPPDATA%\DeskKit\updates\<id>-<version>\<asset>` over HTTPS.
2. Check SHA256 (16.3).
3. Stage the new module dir in `<modules dir>\_update\<id>.new` (same volume as the module dir, so the swap is a rename). In-repo zip: extract the zip and take its top folder `<id>\`. Pinned exe: copy the current module dir and replace `entry` with the new exe. Pinned zip: copy the current module dir and extract into `bin\`. For a pinned module the shell sets `version` in the staged `module.json` to the new version; that is the only key the shell ever writes. Check the staged folder: `module.json` parses, its `id` equals `<id>`, its `version` equals the new version, and `entry` exists. Any failure: delete the staging folder, change nothing.
4. If the module is running (7.4 "Running state"), ask: "<name> is running. Stop it, update it to <version> and start it again?" No: delete the staging folder, change nothing, record `declined`. Yes: stop it with the stop protocol (7.3; step 1 is its `stop_event`). If it does not stop, change nothing and show the 7.3 message. A running `popup` or `oneshot` is not stopped: the shell shows "<name> is busy. Try again when it finishes." and records `declined`.
5. Swap: rename `<modules dir>\<id>` to `<modules dir>\_update\<id>.old`, then rename `_update\<id>.new` to `<modules dir>\<id>`. If the first rename fails (a file is in use), nothing changed: delete the staging folder, show "<name> could not be updated: a file is in use. Nothing was changed." If the second rename fails, roll back: rename `_update\<id>.old` back to `<modules dir>\<id>`, and record `rolled-back`.
6. On success, delete `_update\<id>.old` and the download. A leftover `_update\` folder is deleted at the next shell start. The scanner skips `_update\` because its name starts with `_` (2).
7. The data dir (6) is never read, moved or written by an update. The venv of a python module stays; the module rebuilds it when its requirements change (5.3).
8. If step 4 stopped the module, start it again with `args` (5).
9. Record the result in `%APPDATA%\DeskKit\shell\state.json` (16.8) and write one line to `shell.log`. Rescan the modules.

### 16.7 When the shell checks, rate limits and offline

- The tray checks 30 seconds after it starts, then every 24 hours while it runs, when "Check for updates automatically" is on. "Check for updates" in the tray menu checks at once, even when automatic checks are off.
- The GitHub API is used without a token: 60 requests an hour per IP address. One check makes 2 requests (16.1).
- Cache: `%LOCALAPPDATA%\DeskKit\updates\feed-cache.json` keeps each URL's `ETag` and body. The shell sends `If-None-Match`; a `304` uses the cached body and does not count against the limit.
- Offline, a timeout (15 seconds), a `403` or `429` rate limit, or a bad response: the check fails silently with one line in `shell.log`. An automatic check shows nothing. A manual check shows "Could not check for updates: <reason>".
- The tray shows one notice per new version: "Updates available: <name> <version>, ...". It does not repeat the notice for the same version (`notified` in 16.8). Clicking the notice opens the main window.
- No telemetry. The shell sends only the GET requests above, with `User-Agent: DeskKit/<version>`, and the asset downloads. It sends no ids, no module list and no usage data.

### 16.8 Settings and state

`%APPDATA%\DeskKit\shell\state.json` gets one more top-level object, `updates`. Every key is optional; the shell keeps the other top-level keys when it writes the file.

```json
"updates": {
  "check_enabled": true,
  "include_prereleases": false,
  "last_check": "2026-11-10T12:00:00+01:00",
  "notified": { "eyesaver": "0.2.0" },
  "results": {
    "eyesaver": { "from": "0.1.0", "to": "0.2.0", "result": "updated", "time": "2026-11-10T12:01:00+01:00", "message": "" }
  }
}
```

- `check_enabled` (default `true`): automatic checks. The tray item "Check for updates automatically" switches it.
- `include_prereleases` (default `false`): 16.2 step 3.
- `results.<id>.result`: `updated`, `failed`, `rolled-back` or `declined`.

### 16.9 Command line and user interface

- `DeskKit.exe --check-updates`: checks every component and prints a table (`ID`, `INSTALLED`, `AVAILABLE`, `STATE`, `NOTE`; `STATE` is `current`, `update`, `notify-only`, `no-source` or `error`). It installs nothing and exits 0, also when offline.
- `DeskKit.exe --update <id>` and `--update all`: update one module, or every module with an update. `--update shell` downloads and starts the suite installer (16.5). `--yes` answers Yes to the "is running" question (16.6 step 4). Without `--yes` the shell asks in a message box; with `DESKKIT_NO_UI=1` and no `--yes`, the answer is No.
- Exit codes of `--update`: 0 updated or already current; 1 a failure (download, checksum, swap); 2 wrong arguments; 3 skipped (declined, busy, or notify only). With `all`, notify-only modules do not count as skipped.
- Tray menu: "Check for updates" and "Check for updates automatically" (a check mark).
- Main window: an "Update" button for the selected module (enabled when an update is found) and a status line with the result of the last check or update.

### 16.10 Test hooks

Tests never use the real internet or the real GitHub repos. These environment variables exist for tests only:

| Variable | Effect |
|---|---|
| `DESKKIT_FEED_BASE` | Replaces `https://api.github.com`. While it is set, asset URLs may also use `http://` on `127.0.0.1` or `localhost`. Without it, every URL must be HTTPS. |
| `DESKKIT_RC_UNINSTALL_KEY` | A subkey of HKCU read instead of Region Control's real uninstall keys (16.5). |
| `DESKKIT_UPDATE_NO_RUN` | `1`: `--update shell` downloads and checks the installer but does not start it. |
| `DESKKIT_UPDATE_FAIL_AT` | `swap-in`: the second rename of 16.6 step 5 fails, to test the rollback. |
| `DESKKIT_UPDATE_FIRST_DELAY` | Seconds before the tray's first automatic check, instead of 30. |

### 16.11 How to release a module

For the person releasing. A push or a GitHub release needs Nate's word in the session.

1. Bump `version` in `modules/<id>/module.json` and the module's entry in `suite.json`. Add a `## X.Y.Z (YYYY-MM-DD)` heading to its `CHANGELOG.md`.
2. Run `python tools/validate-modules.py` and the module's `test.ps1`.
3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-release.ps1 -Module <id>`. It builds `dist\release\<id>-X.Y.Z\<id>-X.Y.Z.zip`, writes `SHA256SUMS.txt` beside it, and prints the `gh release create <id>/vX.Y.Z ...` command. It runs nothing against GitHub.
4. With Nate's word, run the printed command. Add `--prerelease` for a version with a `-` part.
5. A pinned module (Region Control) releases from its own repo. To change DeskKit's pin, follow 10.3.

### 16.12 Decisions in this section

1. Only the SemVer `-` part makes a pre-release; the GitHub flag is ignored (16.2).
2. The shell's own update runs the suite installer and exits the tray (16.5).
3. A separately installed Region Control is notify only (16.5).
4. The staging and rollback folders live in `<modules dir>\_update\` so the swap is a rename on one volume (16.6).
5. The first automatic check runs 30 seconds after the tray starts (16.7).
