# Cable Listen

A small borderless window that turns "Listen to this device" on or off for a recording device and picks the output it plays through. It also sets the volume of the picked source. Closing the window (the x in the corner) turns listening off. Drag it by the top strip. Works with any audio device; VB-Audio Virtual Cable is only the default pick.

Python 3.12 or newer, standard library only. In-repo module: this folder is the source of truth.

Run alone: double-click `launch.cmd`. A second start brings the open window to the front.

## Needs

- **SoundVolumeView** (NirSoft, freeware). Found in this order: `svv_path` in settings, `%LOCALAPPDATA%\DeskKit\helpers\soundvolumeview\SoundVolumeView.exe`, then PATH. If missing, Cable Listen shows the download page and exits with code 3. Download from https://www.nirsoft.net/utils/sound_volume_view.html and unzip it unchanged into the helpers folder (the DeskKit installer or shell can do this and check the SHA256).
- **Python 3.12+**. If missing, the message says where to get it (exit 4).
- **A recording device.** If none is active, exit 5. If the default VB-Cable device is not installed and nothing is saved yet, the title bar shows "VB-Cable not found" and the first device in the list is used.

## Settings

`%APPDATA%\DeskKit\cablelisten\settings.json`, written on first run:

- `source`: the device picked last (Command-Line Friendly ID). Default `VB-Audio Virtual Cable\Device\CABLE Input\Render`.
- `svv_path`: full path to SoundVolumeView.exe, or `""` to search.

Old `cable_listen.json` (same key `source`) is copied once to `settings.json` if none exists yet. It is searched for in the data folder, beside the script, and in every PATH folder. The old file stays where it was.

Exit codes: 0 ok, 1 error, 3 SoundVolumeView missing, 4 Python too old, 5 no audio device.
