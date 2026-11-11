# Modules plan

Written 2026-11-10 by G9 Scaffold, from Nate's notes list. Renaming and combining are fine (Nate). Contract: `module-contract.md`.

Status words, one meaning each:

- **existing**: in the suite and in the installer today.
- **scaffolded**: folder, build and test exist; no features; `status: "planned"` in `suite.json`, so the installer and shell skip it.
- **maybe**: Nate is not sure. No folder.
- **later**: not a PowerToys tool; wanted as a suite. No folder.

## Table

| Tool | Status | Id | Replaces | Run mode | PowerToys equivalent | Language | Helpers and permissions | Main risk |
|---|---|---|---|---|---|---|---|---|
| Snap Layouts (Region Control, renamed) | existing | `regioncontrol` | none | tray | FancyZones | C# (pinned release) | Turns off Windows' own snap-on-drag (installer task). Low-level keyboard hook. No admin. | Own repo with other agents: DeskKit only pins a release. |
| LightweightTaskbarGroups | existing | `taskbargroups` | none | popup | none | C# | Reads `.lnk` files. No admin. | Pinned taskbar `.lnk` repoint is not tested (contract 13). |
| RegionShot | existing | `regionshot` | none | window | none (closest: Snipping Tool, Screen Ruler) | Python now; C# rewrite later, with Nate | PySide6 venv (235 MB), local HTTP API on 127.0.0.1. No admin. | The rewrite: the largest piece of work in the suite. |
| Cable Listen | existing | `cablelisten` | none | window | none | Python now; C# rewrite later, with Nate | Helper: NirSoft SoundVolumeView. Reads MMDevices registry. No admin. | Leaves "Listen to this device" on if killed (audio safety rule). |
| Color Picker | scaffolded | `colorpicker` | none | tray + hotkey | Color Picker | C# | `RegisterHotKey`, screen pixel read. No admin, no helpers. | Colour accuracy on HDR and mixed-DPI monitors. |
| Always On Top | scaffolded | `alwaysontop` | desk pins (Nate's current pin tool) | tray + hotkey | Always On Top | C# | `RegisterHotKey`, `SetWinEventHook`. No admin; cannot pin elevated windows without admin. | A pinned window with no way to unpin: unpin all on exit. |
| Power Display | scaffolded | `powerdisplay` | Eye Saver (`eyesaver`) | tray | PowerDisplay | C# | `dxva2.dll` DDC/CI. No admin, no helpers. | Monitors answer DDC/CI differently; tests must not touch the real monitor without the coordinator's go. |
| Power Rename | scaffolded | `powerrename` | ReNamer (third-party tool Nate uses now) | window | PowerRename | C# | File access. Explorer context menu later: shell extension, HKCU registry entry. No admin. | A bad rename of many files: undo and preview come first. |
| Mouse Without Borders | maybe | `mousewithoutborders` | none | tray | Mouse Without Borders | C# | Network (TCP, LAN), low-level mouse and keyboard hooks, input injection, clipboard share. Likely a Windows service and admin for the secure desktop and UAC prompts. Firewall rule. | Security (input over the network, pairing key) and the admin service. Biggest and riskiest tool. |
| New+ | maybe | `newplus` | none | tray (no UI of its own) | New+ | C# | Explorer "New" menu entry: a shell extension (COM) or a registry `ShellNew` key. Template folder in the data dir. Registry write, possibly admin for the Windows 11 menu. | Windows 11 menu integration needs a packaged COM extension; the simple `ShellNew` key may be enough. |
| File Locksmith | maybe | `filelocksmith` | none | window (from context menu) | File Locksmith | C# | Restart Manager API (`RmGetList`) to find locking processes. Explorer context menu: shell extension. Admin to see and end elevated processes. | Ending a process loses user data; show, never auto-kill. |
| Image converter | later | `imageconvert` | none | window | none | C# | Helper: ImageMagick (downloaded, pinned SHA256; contract 8). | Licence and size of ImageMagick; HEIC and RAW need extra codecs. |
| Video converter | later | `videoconvert` | none | window | none | C# | Helper: ffmpeg (downloaded, pinned SHA256). | Long jobs: progress, cancel, no half files. GPL builds of ffmpeg. |
| X-to-Y file converters (documents, audio, archives, data) | later | one id each, or one `convert` module | none | window | none | C# | Helpers per format: ffmpeg (audio), pandoc (documents), 7-Zip (archives). | Scope creep: a long list of small tools. |

## Notes by tool

- **Renamed or combined.** Nate allows both. Two candidates to combine: the image, video and audio converters into one `convert` module with one window and one engine each; Color Picker and Always On Top as one "Desk tools" tray module. Keep them separate until they have features: the scaffolds are small and a merge is cheap.
- **Power Display and Eye Saver.** Eye Saver stays until Power Display matches it. Plan: `modules/powerdisplay/README.md`, section "Replaces Eye Saver".
- **Always On Top replaces DeskPins** (`C:\Program Files (x86)\DeskPins\deskpins.exe`, confirmed by Nate 2026-11-10). DeskPins pins one or more windows on top. Nate wants custom controls: pin on all virtual desktops or only the current one, plus whatever else is useful. Details: `modules/alwaysontop/README.md`.
- **Power Rename replaces ReNamer.** ReNamer is a third-party tool. Nate keeps it until Power Rename covers his rules (counters, regular expressions, presets).

## Recommended build order

Smallest first, so each finish teaches the next one.

1. **Always On Top.** Smallest: one hotkey and one Win32 call (`SetWindowPos`). Tests the tray plus hotkey pattern that Color Picker and Power Display reuse. Replaces something Nate uses.
2. **Color Picker.** Small, and reuses the hotkey code. Adds screen capture and a loupe.
3. **Power Display.** Medium. The DDC/CI logic exists in `modules/eyesaver/src/monitor.ps1`; port it to C#. Removes Eye Saver. Needs care with the real monitor (coordinator's go).
4. **Power Rename.** Medium to large: a real window, regular expressions, preview, undo. High value for Nate (replaces ReNamer). Add the Explorer context menu last.
5. **File Locksmith** (if Nate says yes). Small core (Restart Manager), but it needs the context menu and admin handling.
6. **New+** (if yes). Small if the `ShellNew` registry route is enough; large if it needs the Windows 11 COM menu.
7. **Image converter**, then **video converter**, then the other converters, on one shared engine (below).
8. **Mouse Without Borders** (if yes). Last: network, security, a service and admin. Needs a design review before any code.
9. **RegionShot and Cable Listen rewrites in C#**, under Nate's supervision, when Nate says so. Not scheduled here.

## Which converters can share one engine

- **One window, many engines.** The shell of the converter (file list, drag and drop, output folder, queue, progress, cancel) is the same for every format pair. Build it once as a `convert` module with an engine per helper.
- **ffmpeg** covers video and audio: video to video, video to audio, audio to audio, GIF. One helper download (`ffmpeg`, same `download` block everywhere, contract 8.1).
- **ImageMagick** covers images: PNG, JPG, WebP, TIFF, BMP, ICO, PDF to image. It needs Ghostscript for PDF input. ffmpeg can also do simple image conversions, so ImageMagick is optional for a first version.
- **pandoc** covers documents (Markdown, HTML, DOCX). **7-Zip** covers archives. Each is one more helper and one more engine class.
- All helpers are downloaded on request, pinned by SHA256 and never bundled (contract 8). The module detects them with the detect rule and disables a format when its helper is missing.
