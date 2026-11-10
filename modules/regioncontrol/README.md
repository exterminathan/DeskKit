# Snap Layouts (pinned)

Custom snap layouts for Windows 11. DeskKit shows it as "Snap Layouts"; in its own repo and releases it is still Region Control (`region-control`, `RegionControl-Portable-*.exe`). The module id `regioncontrol`, its mutex, URLs and file names keep the old name. Source and releases: https://github.com/exterminathan/region-control. DeskKit never edits its code.

DeskKit pins one release asset in `../../suite.json`: v0.1.0, `RegionControl-Portable-0.1.0.exe`, with its SHA256. The suite build downloads it, checks the hash, and installs it as `bin/RegionControl.exe`.

Never touch Region Control's local folder or a running copy from DeskKit work (contract 7.5).

Update the pin:
1. Read the new release: `gh release view <tag> -R exterminathan/region-control --json assets`.
2. In `suite.json`, set `version`, `tag`, `url` (the portable exe) and `sha256` (the asset digest).
3. Set `version` in this folder's `module.json` to the same value.
4. Run `python tools/validate-modules.py` and bump the suite version.
