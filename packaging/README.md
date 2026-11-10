# packaging/

The suite installer (roadmap G5).

- `build-suite.ps1`: validates, builds the shell and in-repo modules, stages `dist\stage\`, downloads and checks pinned assets (cache in `dist\cache\`), compiles `DeskKit.iss`, writes `dist\DeskKit-Setup-X.Y.Z.exe` and `dist\SHA256SUMS.txt`. Switches: `-Release` (fail on a skipped module or missing shell), `-SkipBuild`, `-SkipValidate`.
- `DeskKit.iss`: the Inno Setup script. Per-user (`{autopf}\DeskKit`, `PrivilegesRequired=lowest`), fixed `AppId` `{60B9D8BF-5C25-489A-B8BA-99D41F974319}`, MIT licence page, one component per module, optional start at login, optional "Download missing helpers" task. The build writes `dist\generated.iss` (components, files, helper table) and the script includes it.
- Region Control: its box starts unchecked when its own install exists. The snap-off task and uninstall restore are copied from its `RegionControl.iss` v0.1.0. Uninstall restores snapping only if this install held Region Control.
- Uninstall keeps `%APPDATA%\DeskKit\` and `%LOCALAPPDATA%\DeskKit\` and names them.
- `test.ps1`: builds, then silently installs into a temp dir (all components except Region Control), checks files and uninstaller, uninstalls. It skips the install if a real DeskKit install exists.
- Shell lookup: the build takes `dist\shell\DeskKit.exe` first, then `shell\bin\`, then the newest `DeskKit.exe` under `shell\` (outside `obj\`). Without it the installer builds without the shell and warns.
- Needs Inno Setup 6 (`ISCC.exe`), Python 3 and, for the shell, the .NET 8 SDK.
