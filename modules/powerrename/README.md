# Power Rename

**Status: scaffold, planned.** Rename many files at once with search, replace, regular expressions and a live preview. The module builds and starts (a small window, saying "Power Rename scaffold, no features yet"). It has no features. It is not in the installer yet.

C# on .NET 8, self-contained. In-repo module: this folder is the source of truth.

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` (builds `bin\PowerRename.exe`). Test: `test.ps1`. Run alone: `bin\PowerRename.exe`.

- Data dir: `%APPDATA%\DeskKit\powerrename\`. The scaffold writes `logs\powerrename.log`.
- Single instance: mutex `Local\DeskKit.powerrename`, events `.show` and `.stop` (contract 7.2).

## Planned features

- **Run mode: `window`.** Like PowerToys PowerRename, it opens a window with a file list and a live preview of the new names. It replaces ReNamer. A window fits because the user works in it for minutes. A second start brings the window to the front.
- Add files by drag and drop and by command-line paths; the Explorer context menu later.
- Search and replace, with options: regular expressions, case sensitive, all matches, include folders, files only, extension only.
- Counters (`${start=1,increment=1,padding=2}`) and date tokens from file dates.
- Live preview, with name conflicts in red. Rename only on "Apply". Undo of the last rename.
- Presets saved in settings.
- Settings keys (planned): `last_search`, `last_replace`, `options`, `presets`.
- Needs: file access only. The Explorer context menu (later) is a shell extension with an HKCU registry entry. No admin rights. Risk: a bad rename of many files, so undo comes before the context menu.
