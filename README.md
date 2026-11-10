# DeskKit

DeskKit is a suite of small Windows tools by inNATE Tools, in the spirit of PowerToys. One installer and one tray app hold them all. Each tool also downloads and runs alone, with no DeskKit app.

Status: version 0.1.0 in progress. Nothing is released yet.

## Modules

| Module | What it does | Runtime | Source |
|---|---|---|---|
| Snap Layouts (Region Control) | Custom snap layouts for Windows 11 | .NET 8 | pinned release ([region-control](https://github.com/exterminathan/region-control) v0.1.1) |
| Taskbar Groups | Group shortcuts under one pinned taskbar icon | .NET 8 | in-repo (moved from the LightweightTaskbarGroups repo) |
| RegionShot | Pin a screen region, then screenshot or record it | Python 3.11+ | in this repo |
| Cable Listen | Turn "Listen to this device" on or off and pick the output | Python 3.12+ | in this repo |
| Eye Saver | Switch the monitor's picture profile over DDC/CI | PowerShell 5.1 | in this repo |

Python modules use your own Python install. DeskKit never bundles helper programs (FFmpeg, SoundVolumeView); it finds them or offers a download.

## Layout

- `shell/`: the DeskKit tray app (.NET 8, self-contained)
- `modules/`: one folder per module, each with a `module.json`; `_template/` (C#) and `_template-python/` start new ones
- `packaging/`: the Inno Setup suite installer
- `tools/`: build and check scripts
- `tests/`: tests for the contract and the tools
- `docs/`: the module contract, roadmap, brief and inventory
- `suite.json`: the suite version and every module with its version or pinned release

## Build and check

Check every module against the contract (Python 3.12):

```
python tools/validate-modules.py
python -m unittest discover -s tests -v
```

Build the C# template module: `powershell -NoProfile -ExecutionPolicy Bypass -File modules/_template/build.ps1`. Shell and installer build steps come with the shell and packaging work; see `docs/module-contract.md` section 10.4.

## Add a module

Copy `modules/_template/` and follow the checklist in [docs/module-contract.md](docs/module-contract.md), section 14. New modules default to C# on .NET 8.

## Licence

MIT. Copyright (c) 2026 inNATE Tools. See [LICENSE](LICENSE). Helper programs that a module downloads keep their own licences.
