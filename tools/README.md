# tools/

Scripts that build and check DeskKit. Each takes its paths as arguments or from environment variables: no user-name paths.

- `validate-modules.py`: checks every `module.json` and `suite.json` against the schemas and the contract. `python tools/validate-modules.py [--release]`. Exit 0 = pass. See `docs/module-contract.md` section 12.
- `publish-release.ps1 -Module <id>`: prepares one in-repo module release in `dist\release\<id>-<version>\` (zip, `SHA256SUMS.txt`, `NOTES.md` from the CHANGELOG) and prints the `gh release create <id>/vX.Y.Z ...` command. It runs nothing against GitHub. See `docs/module-contract.md` section 16.11.
- `migrate-launchers.ps1`: repoints the old tools' shortcuts, PATH scripts, pinned taskbar and Start Menu entries to the installed `DeskKit.exe --run <id>`. See `docs/module-contract.md` section 13.
  - Dry run (default, changes nothing): `powershell -NoProfile -ExecutionPolicy Bypass -File tools/migrate-launchers.ps1`. Saved output from Nate's PC: `docs/migration-dry-run.txt`.
  - Apply: add `-Apply`. Pinned taskbar and Start Menu entries also need `-IncludeTaskbarPins`. `-Only regionshot,eyesaver` limits the run.
  - Every `-Apply` backs up the files it touches to `%USERPROFILE%\DeskKit-migration-backup\<yyyyMMdd-HHmmss>\`. Undo: run `restore.ps1` in that folder (add `-RemoveCreated` to delete the icon, helper and settings copies too).
  - It refuses `-Apply` (exit 2) when `DeskKit.exe` or a needed module is not installed. It never changes the old tool folders.
  - Tests: `tests/test_migrate.ps1` (fake tree in `%TEMP%`; see `tests/test_migrate.md`).

Planned (written by later agents, to the contract):

- `build-module.ps1 <id>`: builds one module's release zip (section 10.2).
