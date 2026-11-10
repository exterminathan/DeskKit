# DeskKit brief (written 2026-11-07/10 by the ClaudeGlobalSetup sister session, for the coordinator)

Nate's words: he has several small Windows tools and wants "a full tool, sort of imitating Windows PowerToys but customized for me, so I can do stuff on this PC, all under one tool." Each tool must stay downloadable separately, packaged as one suite too. He asked whether one general repo should hold the big release and sub releases, and what to do with the existing repos and their history.

## HOLD

Build nothing yet. Create no GitHub repo, no remote, no module code, no installer. Do the bootstrap (Tracker, CLAUDE.md block), then interview Nate. Ask one question at a time, each with a recommended default. Open questions are at the end. Nate answers; you then draft the roadmap with him.

## What exists (verified 2026-11-10 unless marked)

| Tool | Where | Facts |
|---|---|---|
| Region Control | `D:\My Software\Region Control` | Custom snap layouts for Windows 11 (drag to top edge, pick a layout, editor, per monitor, tray app). Git repo, 5 commits, remote `exterminathan/region-control`, a LICENSE file (type not checked), `src/RegionControl`, Inno Setup installer (`installer/RegionControl.iss`), portable exe, SHA256SUMS, release v0.1.0 (pre-release, unsigned, SmartScreen warns). Newest tool. Docs: `docs/roadmap.md`. |
| LightweightTaskbarGroups | `C:\Users\Nathan\Documents\GitHub\LightweightTaskbarGroups` | .NET 8 WinForms popup launcher that groups shortcuts under one pinned taskbar icon. Git repo, 7 commits, remote `exterminathan/LightweightTaskbarGroups`. Three installed copies exist on disk; the live one is `C:\TaskbarGroups`. |
| RegionShot | `D:\My Software\RegionShot` | Python (`region_shot.py`, 2356 lines). Pin a screen region, screenshot or record it repeatedly. No git repo. Launched by `RegionShot.bat`. |
| Eye Saver | ClaudeGlobalSetup repo, `tools/monitor/monitor.ps1` | PowerShell script, run as `monitor.ps1 eyesaver toggle` from a shortcut. Lives inside the ClaudeGlobalSetup repo, no repo of its own. |
| Finder | ClaudeGlobalSetup repo, `tools/finder` | Python (`finder.pyw`): describe a file in words, headless Claude searches an index. Tied to Claude, so probably out of scope. |
| Cable Listen | `C:\Scripts\cable_listen.pyw` | Python tkinter. Toggles "Listen to this device", drives NirSoft SoundVolumeView. Not a repo. |
| YTDL / SCDL | `C:\Scripts\ytdl.bat`, `scdl.bat` | Clipboard URL to yt-dlp. Thin scripts. |
| Win4KSS | `D:\My Software\Win4KSS` | Screensaver product. 66 commits, remote `exterminathan/Win4KSS`, installer and publish folders. Probably its own product, not a utility. |
| Others in `D:\My Software` | | MediaSourcePopOut (browser extension), WindowsOverlayCreator (Python, no repo), image-to-pdf (Android), DiscordExporter (third-party binaries), openmhz-live, FPVGameUnity (game). Unreviewed. Ask Nate which belong. |

Launch pattern today: `.bat` in `C:\Scripts` (on PATH) and one `.lnk` per tool in `C:\Shortcuts`, grouped by LightweightTaskbarGroups. Hard-coded paths (`C:\Scripts`, `D:\My Software`, the user name) must go before packaging.

## Recommendation from the sister session (Nate asked for it; he has not approved the details)

Decision so far: yes to one new suite repo. Not decided: how much moves into it.

**Model: a suite repo that holds the shell, shared packaging and the small repo-less tools, and pulls in published tools as pinned releases.**
- In the repo as modules: Eye Saver, RegionShot, Cable Listen, and whatever else has no repo.
- Stay in their own repos, consumed by pinned release (a `suite.json` lists module, version, asset URL, SHA256): Region Control, LightweightTaskbarGroups. Reason: public repos with URLs, releases and (for Region Control) a finished installer. Moving them breaks links and gains nothing yet.
- Fold them in later only if shared code (settings UI, hotkeys, updater) makes one repo worth it. Keep this reversible.
- The alternative Nate asked about is a full monorepo with history imported (`git filter-repo --to-subdirectory-filter`, merge with `--allow-unrelated-histories`, `--path tools/monitor` for tools inside ClaudeGlobalSetup, run gitleaks first). Explain the trade to him; do not assume.

**Module contract:** every module runs alone with no shell. A folder with a small `module.json` (name, version, entry point, icon, settings location `%APPDATA%\DeskKit\<module>\`, autostart yes or no). The shell is optional: tray icon, list of modules, toggle, launch, open settings, autostart. Do not rewrite tools into one language.

**Versioning and releases:** each module has its own SemVer and its own download (zip and installer). Suite tag `vX.Y.Z` builds one installer with a checkbox per module from the pinned versions. Tags for in-repo modules: `<module>/vX.Y.Z`. CI per module and for the suite (GitHub Actions). winget and code signing come after 0.1.

**Scope rule:** lightweight utilities only. Leave out heavy or product-sized apps (EfficientAssetRipper, Sammie-Roto, WVC, Win4KSS unless Nate says otherwise). Check redistribution rights before bundling any helper (NirSoft SoundVolumeView, yt-dlp, voidtools Everything SDK).

**Old repos:** archive, never delete. One line at the top of each README pointing to DeskKit. Keep old tags, releases and local folders until the new build works. If a tool moves in, its old repo gets a final release that points to the new home.

## Phases (a proposal to refine with Nate)

0. Inventory table (language, entry, config location, run mode, hard-coded paths) and Nate's scope decisions.
1. Module contract and repo skeleton (`shell/`, `modules/`, `packaging/`, `.github/workflows/`, `docs/`).
2. Pilot: one repo-less tool as a module with its own release (RegionShot is the likeliest), and one pinned external release (Region Control).
3. Shell MVP (tray, module list, launch, toggle, autostart).
4. Suite 0.1 installer.
5. Retire or archive old locations, update READMEs.

## Open questions for Nate (ask one at a time, recommended default first)

1. Final name. Working name is DeskKit (Nate chose it 2026-11-10). Check it is free as a GitHub repo name and has no big clash.
2. Is the model above right: suite repo plus pinned releases for the two published tools? Default: yes.
3. Which tools are in scope? Default: Region Control, LightweightTaskbarGroups, RegionShot, Eye Saver, Cable Listen. Ask about Finder, YTDL/SCDL, WindowsOverlayCreator, MediaSourcePopOut, Win4KSS.
4. Shell language: .NET 8 (matches Taskbar Groups and Region Control, if Region Control is .NET; verify) or something else? Default: .NET 8 self-contained.
5. Public or private, and which licence? Default: public, same licence as Region Control.
6. Installer technology: Inno Setup (Region Control already uses it) or MSIX or WiX? Default: Inno Setup.
7. When to publish: after the pilot works, or only at suite 0.1? Default: after the pilot.

Rules from Nate that apply: no git remote and no push without his word in the session. Agents report changed files until he decides on git. Commits use the `/commit` skill. Write short (ASD-STE100 style, see `~/.claude/CLAUDE.md`).
