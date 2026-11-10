# Changelog

## 0.1.1 (2026-11-10)

- Stop event now ends the process at once (the 5 s failsafe timer is a daemon thread).

## 0.1.0 (unreleased)

- Copied into DeskKit as an in-repo module.
- Settings move from the registry to `%APPDATA%\DeskKit\regionshot\settings.json`, with a one-time copy of old values.
- Log moves to `%APPDATA%\DeskKit\regionshot\logs\`.
- Builds its own venv in `%LOCALAPPDATA%\DeskKit\regionshot\venv` on first run.
- FFmpeg found by setting, helpers folder, then PATH. No fixed `C:\` fallback.
- Single instance by contract mutex, with show and stop events.
- Exit codes and `ERROR:` lines follow the module contract.
