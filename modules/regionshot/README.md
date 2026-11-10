# RegionShot

Pin a screen region, then screenshot or record it again and again. Python 3.11+, PySide6. In-repo module: this folder is the source of truth.

## Run

- Alone: double-click `launch.cmd`.
- From DeskKit: `DeskKit.exe --run regionshot`. The shell starts `src/region_shot.py` with your Python.
- Only one copy runs. A second start brings the first window to the front.

## Install first

| Needed | For | Where |
|---|---|---|
| Python 3.11 or newer | everything | https://www.python.org/downloads/windows/ |
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`) | recording and the trim preview only | https://www.gyan.dev/ffmpeg/builds/ |

Screenshots work without FFmpeg. FFmpeg is found in this order: the `ffmpeg_path` setting, `%LOCALAPPDATA%\DeskKit\helpers\ffmpeg\<version>\bin\`, then PATH.

Errors shown when something is missing:

- `Python 3.11 or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.` (exit code 4)
- `FFmpeg not found. Recording needs it. Download it from https://www.gyan.dev/ffmpeg/builds/ and put ffmpeg.exe on PATH, or set ffmpeg_path in settings.json.` (shown in the status bar when you start a recording; the app keeps running)

## First run

The module builds its own venv in `%LOCALAPPDATA%\DeskKit\regionshot\venv` from `src/requirements.txt` (about 235 MB, needs internet). A small "Setting up RegionShot (first run)" window shows meanwhile. If pip fails, the module exits with code 6 and `ERROR: Could not install Python packages for RegionShot: <reason>`. `launch.cmd --deskkit-setup` builds the venv and exits.

## Data

All under `%APPDATA%\DeskKit\regionshot\`:

- `settings.json`: written with every key on first run. Keys: `x`, `y`, `w`, `h` (region), `dir` (output folder, default `output\` in the data folder), `prefix`, `delay`, `clip`, `rec_format`, `fps`, `gif_width`, `cursor`, `trim`, `unlocked`, `api_port` (default 47800), `ffmpeg_path` (empty).
- `logs\regionshot.log`: renamed to `.log.1` past 1 MB.

The first start with no `settings.json` copies the old registry settings (`HKCU\Software\RegionShot\RegionShot`) once. It deletes nothing, and `dir` keeps pointing at your old output folder. Old files stay where they are.

## HTTP API

Hidden, `127.0.0.1` only, port from `api_port`. `GET /status`, `GET|POST /region`, `POST /capture`, `POST /record/start`, `POST /record/stop`, `POST /select`. A taken port disables the API for that session.

## Build and test

`build.ps1` writes `dist\regionshot-<version>.zip` at the repo root. `test.ps1` runs every check, including a real launch from the unpacked zip.
