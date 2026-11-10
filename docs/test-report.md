# DeskKit 0.1.0 test report

Tester: T1 Tester. Date: 2026-11-10, 13:20 to 13:55 (PST). Task: T1-full-test. Repo state: no git, `dist\DeskKit-Setup-0.1.0.exe` rebuilt by `packaging/test.ps1` during this run.

## Verdict

**Safe to migrate originals? NO, not yet.** The first version works. Install, run, update, rollback and uninstall pass. Four things block deleting the originals:

1. `tools/migrate-launchers.ps1` does not exist (defect 2). Nothing can repoint the shortcuts yet.
2. Cable Listen cannot find SoundVolumeView on this PC after migration (defect 3). Its only copy is `C:\Tools\SoundVolumeView\`.
3. Taskbar Groups: `C:\TaskbarGroups` must stay. Icons in `group.json`, the pinned taskbar shortcut and `Doggosync Companion.lnk` point into it (defect 5). The pinned taskbar shortcut is outside the contract's folders (defect 4).
4. RegionShot takes 5 s or more to stop (defect 1), so the shell's stop protocol can escalate to a kill.

Per original: `C:\TaskbarGroups` no. `D:\My Software\RegionShot` no, until the script exists and `CLAUDE.md` (27 KB) is saved (defect 14). `C:\Scripts\cable_listen.pyw` no, until `svv_path` is set. Eye Saver original (ClaudeGlobalSetup `tools\monitor`) yes after `Eye Saver.lnk` is repointed: nothing else uses it.

## Results by step

| Step | Result | Detail |
|---|---|---|
| 1 Baseline | pass | 83 lines saved: file sizes, times and SHA256 (first 16 hex chars) for the listed folders, HKCU Run values, processes. |
| 2 Suites | pass | See the counts below. 0 failures. |
| 3 Hygiene | pass, 3 low findings | Defects 12, 13 and 15 below. No secrets, LICENSE present, versions match. |
| 4 Real install | pass, 1 medium finding | Defect 1 (RegionShot stop time). RegionShot did not close by itself. |
| 5 Update flow | pass | Update, corrupt asset, zip-slip, bad module.json and forced swap failure all behave. Defects 9 and 10 are minor. |
| 6 Migration dry run | blocked | Script missing. Contract gaps in defects 2 to 7 and 14. |
| 7 Code review | pass, 2 low findings | Defects 9 and 10. No traversal, no unquoted path, no data deletion found. |
| End state | pass, 1 change I caused | Section "End-state snapshot". |

### Step 2: counts

| Suite | Result |
|---|---|
| `python tools/validate-modules.py` | OK, 0 warnings |
| `python -m unittest discover -s tests` | 18 run, 17 pass, 1 skipped |
| `shell/test.ps1` (rebuilds the shell) | 140 pass, 0 fail; `--version` prints `DeskKit 0.1.0` |
| `shell/launch-test.ps1` | 53 pass, 0 fail (RegionShot launch not requested) |
| `shell/update-test.ps1` | 75 pass, 0 fail |
| `modules/taskbargroups/test.ps1` | 22 pass |
| `modules/eyesaver/test.ps1` | 11 pass (read-only `status` on the real monitor) |
| `modules/cablelisten/test.ps1 -SkipWindow` | 7 pass; window test skipped on the coordinator's order (audio safety) |
| `modules/regionshot/test.ps1 -SkipLaunch` | 6 pass; the launch is covered in step 4 |
| `packaging/test.ps1` | 29 pass (build, silent install, uninstall) |

### Step 3: hygiene

- Hard-coded paths. Real hits in shipped or source files:
  - `modules/taskbargroups/module.json` `notes` holds `C:\Users\Nathan\Documents\GitHub\LightweightTaskbarGroups`. The contract allows paths in `notes`, but `notes` ships in the payload (defect 15).
  - `shell/DeskKit/Runtimes.cs:141` falls back to `C:\Windows` when `SystemRoot` is unset. Harmless.
  - `modules/taskbargroups/src/README.md:3,46,49` tells the reader the build installs to `C:\TaskbarGroups` and kills running copies. Stale since the module changed.
  - `.claude/coordinator.json:3` holds an absolute root. It does not ship.
  - All other hits are in tests, fixtures, the contract text and test-only registry keys.
- `Nathan` appears only in `.claude/coordinator.json`, the taskbargroups `notes` and `modules/regionshot/test.ps1` (a check that looks for it).
- Every `module.json` entry exists. Versions equal `suite.json`: cablelisten 0.1.0, eyesaver 0.1.0, regionshot 0.1.0, taskbargroups 0.1.0, regioncontrol 0.1.1. `bin/Hello.exe` (template) and `bin/RegionControl.exe` (pinned) are absent by design.
- Build outputs: `build.ps1` ran inside every module test and the packaging test. `dist\taskbargroups-0.1.0.zip` and `dist\regionshot-0.1.0.zip` exist.
- LICENSE: MIT, present. Payloads carry it.
- Secrets: grep for key, token, secret, password, `ghp_`, `AKIA`, `sk-` and PEM headers found only code identifiers and `GH_TOKEN: ${{ github.token }}` in `release.yml`. No secret.
- Source tool folders: nothing in the repo points into Region Control. The only outside-folder path is defect 15.

### Step 4: real install (temp dir, 4 modules, no Region Control, no startup task)

| Check | Result |
|---|---|
| Silent install exit | 0. Dir holds DeskKit.exe, LICENSE, suite.json, unins000.*, 4 modules. |
| `--version` | `DeskKit 0.1.0` |
| `--list` | 4 modules, all 0.1.0. cablelisten: "Needs NirSoft SoundVolumeView" (defect 3). Others `ok`. |
| `--check-updates` against the fake feed | Table lists 6 rows (4 modules, shell, regioncontrol), all `current`, 2 requests to the fake feed. |
| `--run taskbargroups -- Tools` | Popup "Tools" opened with 11 buttons: SCDL, YTDL, Sammie-Roto-2, Region Shot, Sound Options, Doggosync Companion, Find, Cable Listen, Notion Autofix, Eye Saver, WVC. First run copied the data into the throwaway `%APPDATA%\DeskKit\taskbargroups`. I closed it with the window close pattern. `CloseMainWindow()` does nothing to it (no main window handle). |
| `--run eyesaver --action status` | Exit 0. Log shows monitor "Generic PnP Monitor" and 6 VCP values. Read-only. |
| `--run regionshot` | Venv built in 17 s (pip cache, no big download seen). Window "Region Shot" visible at 1472,588 (496x240). **Stayed open 217 s untouched. Did not close by itself.** |
| RegionShot self-close (about 7 s, earlier run) | Not reproduced. Likely cause: a stop event from another test. The window closes at once on the stop event, as the module logs `stop event - closing` then `closeEvent`. Not confirmed. |
| RegionShot settings | Registry values (`HKCU\Software\RegionShot\RegionShot`, 14 keys) copied into `settings.json`. Registry key kept. Types changed: `clip`, `cursor`, `trim`, `unlocked` are now the strings `"true"` and `"false"`. |
| RegionShot stop | Raw stop event: mutex gone after 5033 ms. `DeskKit.exe --stop regionshot`: 6040 ms (includes shell start). No process left. Defect 1. |
| Stop for the other modules | taskbargroups (popup) and eyesaver (oneshot) have no stop event. Cable Listen: not started (audio safety). |
| Uninstall (silent) | Uninstall key, Start Menu entry and Run value gone. Install dir keeps `modules\eyesaver\NEW.txt` (file my update added, defect 8). Real `%APPDATA%\DeskKit` and `%LOCALAPPDATA%\DeskKit` untouched by uninstall (as designed). |

### Step 5: update flow (temp install, fake feed on 127.0.0.1)

| Case | Result |
|---|---|
| `--update eyesaver` to 0.1.1 | Exit 0. Version 0.1.1, new file present, `_update` empty, download deleted, state.json records `updated`. |
| Corrupt asset (checksum from the good zip) | Exit 1, "did not match its checksum. Nothing was changed.", 0.1.0 kept, download deleted. |
| Zip-slip (valid checksum, entries `../`, `..\`, `eyesaver/../../../`) | Exit 1, "file outside the specified destination directory". Nothing written outside. 0.1.0 kept. Leftover `_update\eyesaver.extract` (defect 10). |
| Wrong `version` inside module.json | Exit 1, refused at staging. 0.1.0 kept. |
| Forced swap failure (`DESKKIT_UPDATE_FAIL_AT=swap-in`) | Exit 1, `rolled-back`, 0.1.0 restored. |
| User data | `%APPDATA%\DeskKit\eyesaver\settings.json` SHA256 equal before and after every case. |

## Defects

Severity: high blocks use, medium blocks migration or breaks the contract, low is cosmetic or unlikely.

1. **Medium. RegionShot needs 5 s or more to exit after the stop event.** File `modules/regionshot/src/region_shot.py:2648`: `threading.Timer(5.0, lambda: os._exit(0)).start()` is a non-daemon thread. The interpreter waits for it, so every stop ends at about 5.0 s, not at once. The module log shows `closeEvent` and `event loop exited` in the same second as the stop. Contract 7.2 step 5 says "exits within 5 seconds", and the shell waits only 5 s (`ModuleController.StopWait`), so step 1 can time out and the shell goes on to WM_CLOSE (0 windows) and then kills the tree. Measured: 5033 ms with the raw event, 6040 ms with `DeskKit.exe --stop regionshot`. Fix: set `daemon = True` on the timer. Owner: the RegionShot agent.
2. **Medium. `tools/migrate-launchers.ps1` does not exist.** `ls tools` shows `README.md`, `publish-release.ps1`, `validate-modules.py`. The contract section 13 says a G6 agent writes it. Step 6 is a paper dry run only.
3. **Medium. Cable Listen will not find SoundVolumeView on this PC.** The only copy is `C:\Tools\SoundVolumeView\SoundVolumeView.exe`. The module looks at `svv_path`, `{helpers_dir}\soundvolumeview\` and PATH, so `--list` says "Needs NirSoft SoundVolumeView". The old script hard-coded `C:\Tools\...`. After migration `Cable Listen.lnk` starts a module that exits with code 3. The migration must set `svv_path` in `%APPDATA%\DeskKit\cablelisten\settings.json`, or Nate must run the helper download. Decision for Nate or the coordinator.
4. **Medium. Contract section 13 misses the Taskbar Groups launch points.** None of them are in `C:\Shortcuts` or `C:\Scripts`:
   - `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Tools.lnk`: target `C:\TaskbarGroups\TaskbarGroups.exe`, arguments `"Tools"`.
   - `%APPDATA%\Microsoft\Windows\Start Menu\Programs\TaskbarGroups.lnk`: target `C:\TaskbarGroups\TaskbarGroups.exe`, no arguments.
   - `C:\TaskbarGroups\Shortcuts\Tools.lnk`: same target and arguments `"Tools"`, icon `C:\TaskbarGroups\config\Tools\GroupIcon.ico`.
   The script takes only `-ShortcutsDir` and `-ScriptsDir`, so it needs a third parameter for the pinned folder. Windows may keep a cached pin entry: whether an edited pinned `.lnk` still works is not tested. The Start Menu entry with no arguments needs `--run taskbargroups` (config window).
5. **Medium. Icons and settings point into folders that Nate will delete.** Observed:
   - Group `Tools` `group.json`: `"iconPath": "C:\\TaskbarGroups\\icons\\link.ico"`. The first-run copy in the module does not rewrite it (checked in the throwaway data dir).
   - `Doggosync Companion.lnk` icon: `C:\TaskbarGroups\config\Tools\icons\Doggosync Companion.ico`.
   - `Tools.lnk` icon: `C:\TaskbarGroups\config\Tools\GroupIcon.ico`.
   Deleting `C:\TaskbarGroups` breaks those icons. The module should rewrite `iconPath` on migration, and the script should repoint the Doggosync icon.
6. **Low. Contract 13 rule 5 loses icons.** The new icon is "the module's icon.ico if it has one". `cablelisten`, `eyesaver` and `taskbargroups` have `icon: null`. `Cable Listen.lnk` (`SndVol.exe,0`) and `Eye Saver.lnk` (`shell32.dll,255`) would take the DeskKit.exe icon. Recommend: keep the old icon when the module has none, or add `icon.ico` files.
7. **Low. Two launch points are broken today, so the old tools already fail.** `Region Shot.lnk` icon is `D:\Software\RegionShot\region_shot.ico` and `C:\Scripts\region_shot.bat` sets `APP=D:\Software\RegionShot\RegionShot.bat`. `D:\Software` does not exist. The real folder is `D:\My Software\RegionShot`. The `.lnk` target is correct (`D:\My Software\RegionShot\RegionShot.bat`). Rules 5 and 6 fix both: the `.bat` file contains `RegionShot.bat`, so it matches.
8. **Low. Uninstall leaves files that an update added.** After my update, `modules\eyesaver\NEW.txt` stayed in the install dir. `packaging/DeskKit.iss:98` deletes only `{app}\modules\_update`. A real update that adds or renames a file leaves the same orphan. Fix: add `Type: filesandordirs; Name: "{app}\modules"` to `[UninstallDelete]` (user data is elsewhere).
9. **Low, not reproduced. Two shell processes can corrupt one update.** `UpdaterInstall.cs:37-59` (`CleanLeftovers`) runs at every `--check-updates`, `--update` and tray start, with no lock. If it runs between the two renames of another process's swap, it moves `.old` back and deletes the staging folder. The first process then fails its second rename, and its rollback (`UpdaterInstall.cs:309-310`) deletes the restored module folder. The window is a few milliseconds. Fix: a named mutex around update and cleanup.
10. **Low. A failed staging leaves `_update\<id>.extract`.** `UpdaterInstall.cs:168-172` deletes only the `.new` folder, while `Stage` extracts to `.extract` (line 245) and the exception skips line 251. Seen after the zip-slip case. The next shell start deletes it. An empty `_update` folder also stays after any failure.
11. **Low. `packaging/test.ps1` writes the real `%APPDATA%\DeskKit\shell\shell.log`.** The test starts the installed tray with the real APPDATA. The log grew from 3961 to 4952 bytes at 13:34. The earlier lines (13:28) come from an earlier run of the same test. Fix: set a temp `APPDATA` in the test before the silent install.
12. **Low. `--list` shows `STATE running` with `STATUS Not installed` for regioncontrol.** The user's separately installed Region Control holds the mutex, so the row says `running`. Cosmetic.
13. **Low, docs. CLAUDE.md says Region Control is pinned at v0.1.0. `suite.json` pins v0.1.1.** The contract (section 16.2) says v0.1.1 exists. Update CLAUDE.md.
14. **Low. Files the originals hold that the modules do not.**
    - `D:\My Software\RegionShot`: `CLAUDE.md` (27,043 bytes, changed 2026-11-07), `README.md` (5,118), `.gitignore` (36), `region_shot.log` (36,228, old history), `__pycache__\`, `.venv\` (rebuilt by the module). `RegionShot.bat` and `region_shot.ico` are replaced (the icon is byte-identical). No captures: the save folder is `C:\Users\Nathan\Desktop`. Settings sit in the registry key `HKCU\Software\RegionShot\RegionShot`, which the module copies and keeps.
    - Cable Listen: `C:\Scripts\cable_listen.json` (71 bytes, `source`). The module copies it once (searches PATH, and `C:\Scripts` is on PATH). Not tested here because the window test was skipped.
    - Eye Saver: `monitor.ps1` only (3,166 bytes). The module's copy is a rewrite (6,790 bytes). No settings or logs.
    - Taskbar Groups: `Shortcuts\Tools.lnk`, `icons\`, `config\`, `settings.json` (29 bytes). See defects 4 and 5.
    Save the RegionShot `CLAUDE.md` somewhere before the folder goes.
15. **Low. A path and a user name ship in the taskbargroups payload.** `modules/taskbargroups/module.json` `notes` has `C:\Users\Nathan\Documents\GitHub\LightweightTaskbarGroups`. Allowed by the contract, but the file ships. Also `src/README.md` is stale (see Step 3).

## End-state snapshot comparison

Snapshots are in the scratchpad: `baseline.txt` (13:21) and `final.txt` (13:55). I compared the file lists with size, time and hash, HKCU Run values and the process list.

- Unchanged: `C:\TaskbarGroups\config` (all files), `C:\Shortcuts\*.lnk` (17), `C:\Scripts\*` (12), `D:\My Software\RegionShot` top level, `C:\Program Files\Region Control` (same files, same hashes), `%APPDATA%\RegionControl` (same files, same hashes), all HKCU Run values (including `RegionControl`), the running `RegionControl.exe` (PID 93472, still running). No DeskKit, TaskbarGroups or RegionShot process remains.
- Changed, and I caused it: `%APPDATA%\DeskKit\shell\shell.log` grew from 3961 to 4952 bytes (defect 11). `%APPDATA%\DeskKit\eyesaver\settings.json` is unchanged (722 bytes, same hash).
- No change I did not cause.
- Temp folders: `%TEMP%\dk-t1` held my install, venv and fake feed. I deleted it at the end.

## Not tested

- Cable Listen window, stop event and the `cable_listen.json` copy (audio safety, coordinator's order).
- Eye Saver `toggle`, `on` and `off` (monitor must not change).
- The tray UI and the main window by hand (the self-test and launch test cover them). Real autostart through the real Run key (covered with a test key by the launch test).
- `--update shell` against a real installer (the update test covers download and check only).
- Updating a running window module in the installed copy (the update test covers it with fixtures).
- A repointed pinned taskbar `.lnk` (defect 4).
- Region Control's own install, data and process (not touched, by rule).
- Internet downloads: SoundVolumeView and the pinned Region Control download (the packaging build verified the pinned SHA256 from its cache).
