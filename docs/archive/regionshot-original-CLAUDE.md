# CLAUDE.md — Region Shot

Context for future sessions. Read `README.md` first for what the tool is and
how to run it; this file records the things that are not obvious from the code,
and the mistakes already paid for.

## Shape

One file, `region_shot.py`, ~900 lines, PySide6 + a little ctypes. No package,
no tests, no build step. Windows-only by design — the recorder is ffmpeg
`gdigrab` and the DPI/screen handling assumes Win32.

Key classes:

| Thing | Role |
|-------|------|
| `RegionShot(QWidget)` | the whole UI and all state |
| `RegionOverlay` | full-desktop dimmed drag-to-select overlay |
| `RegionOutline` | dotted outline shown *outside* the region |
| `grab_region(rect)` | screen grab + crop, DPI aware |
| `DelayBox(QComboBox)` | presets + free entry, shaped like the spin box it replaced |
| `TrimDialog` | post-recording trim editor — see details 17–20 |
| `TrimBar` / `PreviewPane` | its filmstrip-with-handles, and its frame view |
| `gear_icon` / `lock_icon` | painted icons — see History |
| `ApiBridge` / `start_api_server` | local HTTP API — see its section below |
| `fit_spin` / `fit_combo` | measured widths — see detail 10 |

The window is three bands: the three coloured action buttons plus `Show`; the
X/W over Y/H region matrix with the padlock and `Delay` beside it, centred as
one block; the status bar with the `⚙` and `?` buttons. It opens at 480 wide by
**exactly its layout minimum height** — see detail 14 — and resizes freely
above that.

`Delay` sits with the region rather than in Settings on purpose: it is part of
framing a shot — the wait that lets you click back into the target app — not
part of where files land or how clips are encoded.

Everything that only tunes the output — folder, prefix, clipboard copy, record
format, FPS, GIF width, cursor, trim — lives in the settings dialog built by
`_build_settings()`. Those controls are children of the dialog but are still
owned and read by `RegionShot` through the same `self._dir` / `self._fps` /…
attributes they always had, so `_load`, `_save`, `capture` and the recorder
never had to learn where they moved to. The dialog holds no state of its own
and has no OK/Cancel: every control saves on change, exactly as before.

Everything is driven from the window. There are no global hotkeys — see History.

## Local HTTP API (2026-08-31)

A localhost-only JSON API fires the same actions as the buttons: `GET /status`,
`GET|POST /region`, `POST /capture` (`{"delay": s}` optional, default 0
regardless of the GUI Delay box — an HTTP call steals no focus), `POST
/record/start`, `POST /record/stop` (`{"trim": true}` to allow the trim dialog;
default skips it even when the setting is on — the caller may not be at the
keyboard), `POST /select` (returns as soon as the overlay is up). Port comes
from the `api_port` QSettings value (default 47800, written back on first run
so it is discoverable in regedit; no UI). A taken port logs `API disabled` and
the GUI starts anyway. `--minimized` starts the window minimized with the API
up.

Mechanics worth not breaking:

- `ApiBridge(QObject)` is the only path from the HTTP daemon thread to Qt: the
  handler emits its signal (cross-thread → queued), blocks on a `queue.Queue`,
  and only plain dicts cross the boundary. Even read-only `/status` goes
  through it — widgets are not thread-safe to read.
- `capture()`, `start_record()`, `stop_record()`/`_finish_record()` now return
  result dicts mirroring the status line; the GUI ignores them. **The Capture
  button connects via `lambda: self.capture()`** — `clicked` emits a `checked`
  bool that would otherwise land in `delay_override` as `False` and silently
  zero every GUI delay. Verified live: API capture with `{"delay": 2}` took
  2.5 s, plain capture was immediate.
- `stop_record(allow_trim=...)` gates the trim dialog; GUI callers pass
  nothing (True). Verified with the Trim setting on: API stop returned a
  playable MP4 with no dialog.
- The `overlaps` check in `capture()` includes `not self.isMinimized()` — a
  minimized window can't be in the shot, and hide/show would churn it.
  Verified: capture while `--minimized` leaves the window minimized.
- The venv's `python.exe` is a launcher shim that spawns the base Python 3.11
  interpreter as a child — one app instance is **two** processes, and the pid
  in the log is the child's. Kill both when clearing a stale instance.

## Hard-won details — do not undo these

1. **The outline is clipped to the ring outside the region**
   (`QRegion(widget) - QRegion(inner)`). That is what guarantees it can never
   appear in the output, regardless of pen width or rounding. Verified: 0
   painted pixels inside the region across 4 region sizes, and 0 outline-coloured
   pixels in recorded frames.

2. **`WA_ShowWithoutActivating` on the outline.** The Show toggle keeps the
   outline up while the user works; without this, every time it appears it
   steals focus and dims the target app's title bar — changing the very thing
   being captured. The peek (detail 12) leans on this even harder: the outline
   now goes up on *every* nudge of X/Y/W/H, so without it the spin box being
   edited would lose focus mid-edit.

3. **`HIDE_SETTLE_MS = 380`.** Windows fades a window out over ~200 ms; grabbing
   sooner catches a semi-transparent ghost of this window in the shot.

4. **Stopping a recording writes `q` to ffmpeg's stdin.** `terminate()` leaves an
   MP4 with no moov atom — an unplayable file.

5. **`_save()` is guarded by `self._loading`.** `_load()` calls `set_region()`,
   which saves; without the guard the load persists constructor defaults over
   the user's folder/prefix/delay on every launch.

6. **H.264 needs even dimensions.** A 641×403 region records as 640×402 —
   trimmed, not padded, so framing still matches the stills.

7. **`.bat` files must keep CRLF.** cmd.exe mis-tokenises LF-only batch files
   and ran several branches at once.

8. **The settings dialog is modal, deliberately.** A floating settings window
   would sit in front of the region and land in the next shot — and `capture()`
   only knows to duck *the main window* out of the way
   (`frameGeometry().intersects(rect)`). `exec()` makes that unreachable. If it
   is ever made non-modal, it has to join that overlap check.

9. **The gear is painted, not typed** (`gear_icon()`). U+2699 falls through to
   Segoe UI Emoji on some Windows installs and arrives as a colour sticker
   beside the flat `?` on the button next to it. Both circles share
   `ROUND_BTN_STYLE` so they cannot drift apart.

10. **Spin-box widths come from `fit_spin()`, never from `setFixedWidth()` and
    never from Qt's `sizeHint()`.** Under the Windows 11 style the two arrow
    buttons sit side by side and take more width than `sizeFromContents`
    budgets for, so the value is the first thing clipped. At a 16pt system font
    — verified — `Delay` lost its `" s"` suffix, `GIF width` rendered `1000` as
    `10`, and `FPS` showed *no number at all*, while every widget still measured
    at or above its `sizeHint`. `fit_spin()` asks the style how much of a spin
    box is not the text (`SC_SpinBoxEditField` against a probe rect) and adds
    the widest value the box can ever show. Verified clean 9–24pt.

    Two things `fit_spin()` must get right, both found the hard way:

    - **`EDIT_SLACK = 14`, not 8.** `SC_SpinBoxEditField` is not the text area:
      QLineEdit keeps 2px of its own margin either side plus a pixel or two for
      the cursor. At 9pt the style rect claimed 32px of room while the text got
      23, so a box sized by the old maths rendered `3439` as `343` — and every
      numeric check still passed. Settled by rendering the same box at every
      width from 74 to 98px and reading which one first painted the last digit
      (86, against the 80 the maths predicted).
    - **`opt.buttonSymbols` must be set explicitly.** `initFrom()` does not
      copy it and the default is `UpDownArrows`, so a `NoButtons` spin box gets
      measured as though it had arrows. Under this style those arrows are 48 of
      an 86px box, so that mistake more than doubled the locked matrix.

    Every other width in the dialog is a minimum, not a fixed width, for the
    same reason: a fixed width is a guess about a font you have not seen.

11. **`_sync_outline()` is the single owner of "should the outline be up".**
    Three things borrow the outline — a capture, a recording, and the peek that
    flashes it up when X/Y/W/H is nudged with `Show` off — and every one of them
    must hand it back through that method, never `hide_outline()` directly.
    Otherwise a capture leaves the `Show` toggle switched on with nothing on
    screen. It defers to a live recording, so clear `_rec_proc` *before* calling
    it in `_finish_record()`.

12. **The peek must not fight a recording.** `_on_region_edited()` returns early
    while `is_recording()`, because it calls `show_outline(OUTLINE_SHOT)`
    directly and would repaint the red recording outline orange mid-take.
    `_sync_outline()` defers to a recording on its own, but this path does not
    go through it. `PEEK_MS = 1200`, restarted on every nudge, so continuous
    adjusting keeps the outline up and it clears shortly after the user stops.

13. **`show_outline()` reuses the outline window when only the geometry
    changed.** With `Show` on, the outline tracks the spin boxes live, and
    `valueChanged` fires on every step *and* every keystroke while typing a
    value — tearing down a top-level window and building a new one that often
    strobes. It still rebuilds on a colour change, since orange ↔ red is a
    different outline.

14. **The window opens at its layout minimum height, and that is load-bearing.**
    Any height above the minimum is slack, and the slack lands in the stretch
    above the status bar — which *is* the empty band. Measured: that stretch
    takes the first ~32 px of slack before the action buttons see any of it, so
    "open taller and let the buttons grow into it" does not work. The buttons
    are therefore a fixed `ACTION_H` (72) rather than the old 40→72 range,
    which makes the minimum height the right height. Verified: stretch = 0 px
    at the opening size, 480×201.

15. **The region matrix is read-only behind `_lock_btn`, not disabled.**
    `setReadOnly(True)` blocks typing, the arrow keys and the wheel — all
    verified by sending the events — while leaving the numbers legible and,
    crucially, leaving `set_region()` free to write them, so Select Region
    works locked. The steppers are hidden with `NoButtons` when locked, since a
    read-only spin box that still shows arrows reads as broken. The lock state
    persists (`unlocked`), defaulting to locked.

    **The boxes are re-measured on every toggle and the two states are
    different widths** — 38px locked against 86px unlocked at 9pt. That is
    deliberate: the Windows 11 arrows take 48px, so one shared width would
    leave the locked matrix, which is what you look at almost all the time,
    mostly air. The row is centred, so it re-centres; the reflow only happens
    on a deliberate click of the padlock, where visible feedback is the point.

16. **`DelayBox` parses leniently and reformats.** It is an editable
    `QComboBox`, so a validator would have to reject the `" s"` suffix
    mid-keystroke and fight the user as they type. Instead the first number in
    the text wins (`3`, `3 s`, `3s`, `1,5` all work), the value is clamped to
    `0..DELAY_MAX`, and the text is rewritten canonically on commit;
    unparseable text falls back to the last good value. It keeps
    `value()`/`setValue()`/`maximum()`/`valueChanged` so `capture()`, `_save()`
    and `_load()` never learned it stopped being a `QDoubleSpinBox`.

17. **The trim editor's preview is stills, not video.** There is no
    `PySide6.QtMultimedia` in this `.venv` — verified, it is not merely
    unimported — so `QMediaPlayer` was never an option. ffmpeg is already a
    hard dependency of recording, and one pass of it
    (`-vf fps=N,scale=-2:'min(360,ih)'`) writes every frame the editor will
    ever show, as small JPEGs in a temp folder. Scrubbing, the filmstrip and
    playback are then dictionary lookups against those files rather than seeks,
    which is why dragging a handle is instant. Per-scrub `ffmpeg -ss` calls
    were the obvious alternative and are ~150 ms each — visibly laggy.

    The extraction runs while the dialog is open and frames are picked up as
    they land (`_scan_frames`, 120 ms). **The newest file is skipped while
    ffmpeg is still running** — it is the one being written, and half a JPEG
    loads as a null pixmap. Long clips drop below `TRIM_PLAY_FPS` rather than
    filling the disk; playback uses whatever rate was settled on, so it still
    runs at the clip's real speed.

18. **The trim cut re-encodes, and that is not laziness.** `-c copy` can only
    cut at a keyframe, which slides the start back by up to a second — the one
    thing a trim editor must not do. Verified by comparing pictures rather than
    bytes (the cut clip has been through libx264 again, so bytes never match):
    frame 0 of a clip cut at 2.0 s differs from the source frame at 2.0 s by
    0.11/255, and from the frame at 0.0 s by 21.84.

    `-ss` goes **before** `-i` and the length is `-t` **after** it. `-to` as an
    input option is an absolute source timestamp, not a relative one, and that
    ambiguity is worth stepping around entirely.

    The output is written beside the target and `os.replace`d in only on
    success, so a failed cut leaves the take intact instead of half-written.

19. **Trim happens before the export, not after it.** `_finish_record()` runs
    the editor ahead of the MP4/GIF format handling, so the GIF is encoded from
    what the user kept. Doing it the other way round would mean a GIF that
    contradicts the trim beside it. Verified: with `MP4 + GIF` and a 2.0–5.5 s
    selection, the GIF comes out 42 frames, not the full clip's 96.

    `Save Trimmed` on an untouched selection returns `FULL`, not `TRIMMED` —
    there is nothing to cut, and re-encoding would cost quality for nothing.
    So does a cut that fails: in both cases the file on disk is the whole take,
    and the status must not claim otherwise.

20. **`PreviewPane` is a widget, not a QLabel holding a pixmap.** A QLabel
    takes its sizeHint from its pixmap, so a frame scaled to fit the label
    grows the label, which grows the dialog, which makes the next render scale
    to a bigger box — a dialog that creeps outward every time the preview
    updates. Painting the frame in `paintEvent` has no such loop. Verified:
    a 1920×1080 frame pushed into a 700×500 dialog six times leaves it 700×500.

    `TrimDialog.done()` is where cleanup lives, **not `closeEvent`** —
    `accept()` never raises a close event, so putting it there would strand an
    ffmpeg process and a few hundred JPEGs on every recording.

    Like the settings dialog (detail 8) the trim editor is modal, which is what
    keeps it out of `capture()`'s overlap check.

21. **The select overlay's region hole is alpha 1, not alpha 0.** Windows makes
    a translucent top-level window click-through wherever its alpha is exactly
    0, so punching the current region out with `CompositionMode_Clear` sent
    every click inside it to the app underneath — starting a new drag from
    inside the existing region was impossible. The hole is filled with
    `QColor(0, 0, 0, 1)` in `CompositionMode_Source` instead: visually
    identical, but the overlay keeps receiving mouse input there.

## Failure modes that wasted hours

- **A stale instance.** Two copies share one registry key and one output folder,
  so they overwrite each other's region and race for the next `prefix-NNN`.
  `_claim_single_instance()` (named mutex) refuses the second copy. If the app
  behaves as though settings keep reverting, look for a leftover process first —
  `Get-CimInstance Win32_Process | ? CommandLine -like '*region_shot*'`.

- **Under `pythonw` there is no console**, so a crash is completely invisible —
  the app just never appears. This is why `region_shot.log` exists and why
  `sys.excepthook` writes to it. A startup `AttributeError` from an incomplete
  edit went undiagnosed for a long time until the log was added. **Check the log
  first, always.**

- **Bare `pythonw` on PATH resolves to Python 3.12 on this machine, which has no
  PySide6.** The launcher pins `.venv` for exactly this reason.

## Testing

There is no test suite; verification is ad-hoc scripts run headlessly with
`QT_QPA_PLATFORM=offscreen`.

**A headless `RegionShot` writes to the real registry.** It is the live app, not
a fixture: `closeEvent()` calls `_save()`, and every spin-box change calls it
too — so a test that sets a scratch folder and prefix and then closes silently
overwrites the user's actual settings. This happened twice in one session. Patch the
symbol the module itself uses — `QSettings.setDefaultFormat()` does **not**
redirect a `QSettings("RegionShot", "RegionShot")`, it still lands in the
registry — and assert the redirect took before touching any widget:

```python
os.environ["QT_QPA_PLATFORM"] = "offscreen"     # nothing is ever mapped
RS.QSettings = lambda *a, **k: QSettings(INI, QSettings.Format.IniFormat)
w = RS.RegionShot()                              # bypasses the single-instance guard
assert os.path.normcase(os.path.normpath(w._settings.fileName())) == os.path.normcase(INI)
w._show_btn.setChecked(True); w._outline is not None
```

Qt normalises `fileName()` to forward slashes, hence the `normpath`.

Recovery, if it happens anyway: a still-running older instance holds the real
values in its widgets, so closing *it* writes them back.

**Synthetic input is not proof.** Wiring can be tested headlessly; what a real
click or a real screen does cannot — that needs a human.

**A crashing test writes `CRASH` into `region_shot.log`.** Importing the module
installs its `sys.excepthook`, and `LOG_PATH` is next to the source — so a
traceback from a test script lands in the same log the app uses, looking
exactly like an app failure. Check the timestamps against the `start pid=` line
before chasing one.

**Never measure layout under `offscreen`.** That platform has no fonts, so
every width it reports is fiction — it will happily tell you a dialog fits.
Layout and clipping have to be checked on the normal Windows plugin. Nothing
needs to appear on screen for that:

```python
dlg.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
dlg.show()                    # a real layout pass, no window
dlg.adjustSize(); app.processEvents()
dlg.grab().save("dialog.png")
```

`adjustSize()` + `layout().activate()` on an unshown widget is *not* enough:
the layout keeps running at the previous geometry and reports phantom
"cut off" widgets — that produced a 20-minute chase after a checkbox that was
16px short only in the harness.

**Measure the rendered text, not the widget box, and then look at the image.**
This has now caught two separate clipping bugs that every numeric check
cleared:

- `width >= sizeHint()` passed on every control in the settings dialog at 16pt
  while three of them were visibly clipped (hard-won detail 10).
- Later, `advance <= SC_SpinBoxEditField.width()` passed on the region matrix
  while `3439` was rendering as `343` — the style rect is not the text area.

Both were found by grabbing to a PNG and reading it, and neither was found any
other way. Set each control to its *longest possible* value first — `20.00 s`,
`4000`, `MP4 + GIF`, the range endpoints — since the defaults are short enough
to hide the problem. When a width is genuinely in question, render the widget
at a range of widths into one contact sheet and read off where the text
survives; that is what settled `EDIT_SLACK`.

A checker that measures widget rects is worth having, but treat a pass as
"nothing obviously wrong", not as proof.

Recording can be verified with `ffprobe` metadata (dimensions, frame count)
without decoding pixels — which matters when the desktop being captured is
someone's screen.

**The trim editor is testable without a screen recording.** A synthetic clip
(`ffmpeg -f lavfi -i testsrc=size=640x402:rate=12:duration=8`) stands in for a
take, and everything downstream is measurable: `ffprobe` for the cut's length,
frame count and dimensions; a pixel-difference against source frames for
whether the cut is where it was asked for rather than snapped to a keyframe.
Note the even dimensions — `testsrc` at 641×403 will not encode (detail 6).

`_finish_record()`'s routing is testable by staging a copy of that clip as
`_rec_stem` and replacing `_trim_clip` with a stub returning each outcome, so
Discard/Save Full/Save Trimmed and a failed cut can all be checked without a
dialog appearing.

`TrimBar` paints against a filmstrip that the test supplies, so feeding it a
flat white one makes every mark it draws measurable off `grab()` — the dim
outside the selection, the accent handles, the playhead — the same trick that
proved the region outline stays out of the capture.

## Wiring outside this folder

| Path | Points at |
|------|-----------|
| `C:\Shortcuts\Region Shot.lnk` | `RegionShot.bat`, WindowStyle 7 (minimised) |
| `C:\Scripts\region_shot.bat` | thin forwarder, so `region_shot` works from any terminal |
| `C:\TaskbarGroups\config\Tools\group.json` | entry pointing at the `.lnk` |
| `C:\TaskbarGroups\config\Tools\icons\Region Shot.ico` | copy of `region_shot.ico` |

Moving this folder means updating `APP=` in the forwarder and the `.lnk` target.

## History

Started as `tools/region_shot.py` inside the EfficientAssetRipper repo, built to
replace hand-driven screenshotting. Moved here to be standalone: its own venv,
its own registry scope, no reference back to that repo.

**Global hotkeys removed 2026-08-09**, at the user's request — `Ctrl+Shift+2`
capture, `Ctrl+Shift+1` re-select, `Ctrl+Shift+3` record, along with
`_HotkeyFilter`, the `WM_HOTKEY` plumbing and the `_capture_ok`/`_region_ok`/
`_record_ok` status reporting. Everything is buttons now, and `Delay` is what
covers the focus problem the keys used to. Two things worth keeping if they are
ever reinstated:

- Register against the thread (`hwnd = NULL`), not a window. A window-scoped
  hotkey gets `WM_HOTKEY` delivered to the window proc *and* the dispatcher, and
  Qt runs native event filters on both passes — so every press fired twice and
  produced two screenshots.
- Only one process can hold a given hotkey, so a second copy comes up looking
  healthy with dead keys. That presented as "the shortcut is broken" three
  separate times before the single-instance mutex existed.

`Show` became a toggle at the same time; it was a 1.1 s flash before.

**Settings moved behind a gear 2026-08-10**, at the user's request — record
format, FPS, GIF width, prefix, delay, cursor, clipboard copy and the whole
Save-to block left the window for a modal dialog on a `⚙` circle beside `?`.
The window is now just the actions, the region matrix and the status bar.

That removed `FlowLayout` (~90 lines) and its `field()` helper, which existed
only for the options row. Two things are worth knowing if wrapping content ever
comes back:

- The row was the only reason the window had a wide minimum: one long line of
  options fixed the floor wider than a square window wanted to be, so the row
  had to reflow instead.
- **`minimumSizeHint()` under-reports height while a `FlowLayout` is in the
  window.** It asks the wrapping row how tall it is without saying how narrow
  it is about to be, so the extra lines it grows at the minimum width spill out
  the bottom — verified at 534×194, six widgets past the frame. The constructor
  had to re-ask with `outer.minimumHeightForWidth(...)` and take the larger.
  That second opinion is gone now that nothing wraps; verified honest at the
  new 440×163 minimum (lowest content bottom 151).

The default window is 480 square rather than 620 — with the settings gone,
620 was mostly empty.

**The trim editor landed 2026-08-14**, at the user's request, modelled on
Windows Photos' trimmer: `Trim clips before saving` in the Recording section of
Settings (off by default) makes a recording stop into `TrimDialog` — preview
pane, filmstrip with two handles and a playhead, `Start`/`End` boxes, and
`Play` for the selection — before anything is exported. It exits as `Save
Trimmed`, `Save Full Clip` or `Discard`; Esc and the X button mean Save Full
Clip, because the safe default is never to lose a take.

The interesting constraint was that this `.venv` has no `QtMultimedia`, so the
preview had to be built out of ffmpeg stills (details 17–20). That turned out
better than a player would have been: one extraction pass makes scrubbing free,
and the filmstrip comes from the same frames.

**Delay came back to the main window 2026-08-10**, into the region row, on the
reasoning in Shape above. **The outline peek landed the same day**: nudging
X/Y/W/H with `Show` off flashes the outline up so the new framing is visible
(details 11–13).

**The region lock, the Delay dropdown and the tight opening height, also
2026-08-10**, all at the user's request. `Delay` became a `DelayBox` (presets
0/1/3/5 s plus free entry, detail 16); a painted padlock between the matrix and
`Delay` gates manual X/Y/W/H edits (detail 15) because Select Region is the
normal path and the numbers are only for bumping; and the window stopped
opening square — 480×201, its exact content height, with no empty band above
the status bar (detail 14).

**The region boxes were tightened the same day.** Their ranges had been the
virtual desktop ±10000 with sizes up to 20000, and since `fit_spin()` sizes a
box for the widest value its range allows, that slop bought two digits of width
that could never hold a number. The ranges are now the desktop itself — a
region outside it, or larger than it, grabs nothing useful — so the widest
value is 4 digits. Combined with per-state sizing (detail 15) the locked matrix
went from 89px boxes to 38px.

<!-- global-rules-note -->
## Global rules: recheck at every start

At the start of every session in this project, read `~/.claude/CLAUDE.md` and the newest entries of `~/.claude/GLOBAL-CHANGES.md` (the last 10). They hold rules for every project, and they change over time: for example "Calling Nate (coordinators only)" and "Names Nate uses" (HUB is the PC-wide coordinator session in the ClaudeGlobalSetup repo). This holds no matter when this project last ran. If a global rule conflicts with this file, tell the coordinator or Nate, and follow the stricter rule until Nate decides. Any session in this project may message HUB (the PC-wide coordinator) in addition to this project's coordinator.
