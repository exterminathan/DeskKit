# Region Shot

Pin one screen area, then screenshot or record exactly it, over and over.

Snipping Tool makes you redraw the selection every time, so consecutive shots
never line up. This pins a region once and reshoots it, which is what you want
for before/after images of the same UI panel.

## Running it

| How | What |
|-----|------|
| Taskbar → **Tools** group → Region Shot | normal use |
| `region_shot` in any terminal | `C:\Scripts` is on PATH and forwards here |
| `RegionShot.bat` | direct |

## Using it

Everything runs from the window: **Select Region**, **Show**, **Capture**,
**Record**, plus the region's X/Y/W/H and the capture **Delay**.

**Select Region** is the usual way to set a region. X/Y/W/H are for bumping it
afterwards, so they sit behind a **padlock** — open it for manual override, and
the spin arrows appear. Select Region works whether it is locked or not.

**Show** toggles a dotted outline around the pinned region. It stays up until
you switch it off — so you can watch the region while nudging X/Y/W/H — and it
never lands in the output. With **Show** off, nudging X/Y/W/H still flashes the
outline up for about a second so you can see where the region landed; keep
adjusting and it stays up, stop and it clears itself.

**Delay** offers 0/1/3/5 s in its dropdown, and takes any number of seconds up
to 600 if you type one.

Everything that only tunes the output — save folder, prefix, clipboard copy,
record format, FPS, GIF width, cursor, clip trimming — sits behind the **⚙**
button in the status bar, beside **?**. The window itself stays down to the region, the
delay, and the four things you actually click.

Clicking **Capture** gives *this* window focus, which dims the target app's
title bar and drops its focus ring, so the shot stops matching what you were
looking at. Set a **Delay** when that matters: the grab happens after the wait,
so you can click back into the target app first.

## Layout

```
D:\Software\RegionShot\
    region_shot.py      the whole app
    region_shot.ico     icon used by the shortcut and taskbar group
    RegionShot.bat      launcher — pins .venv, no console window
    region_shot.log     start-up, captures, crashes
    .venv\              private PySide6-Essentials environment (~235 MB)
```

Self-contained: it does not depend on any other project, or on which Python is
first on PATH. Settings live in `HKCU\Software\RegionShot\RegionShot`.

## Recording

ffmpeg `gdigrab` pulls the region straight off the desktop — no per-frame work
in Python. Writes MP4, GIF, or both; GIFs go through a two-pass palette so UI
text stays legible.

Measured on this machine (8 cores, 5 s clips):

| Config | % of one core | % of total CPU | fps achieved |
|--------|---------------|----------------|--------------|
| 800×600 @12 | 11% | 1.3% | 12.0 |
| 1920×1080 @12 | 29% | 3.7% | 12.0 |
| 1920×1080 @30 | 52% | 6.6% | **25.4** — drops frames |

gdigrab cannot keep up at 1080p30. `ddagrab` (GPU, Desktop Duplication) hits a
full 30 fps for slightly more CPU, and would be the fix if smooth 30 fps clips
are ever wanted.

Needs ffmpeg on PATH or at `C:\ffmpeg\ffmpeg.exe`.

### Trimming

Turn on **Trim clips before saving** in Settings and a recording stops into a
trim editor instead of going straight to disk — the same idea as the trimmer in
Windows Photos:

```
┌─────────────────────────────────────────┐
│           frame at the playhead         │
├─────────────────────────────────────────┤
│ 0:03.4 / 0:08.0        Selected 4.40 s  │
│ ░░░│▓▓▓▓▓ filmstrip ▓▓▓▓▓│░░░░░░░░░░░░  │  ← drag the orange handles
│ [Play]        Start 1.80 s  End 6.20 s  │
│ [Discard]      [Save Full Clip] [Save]  │
└─────────────────────────────────────────┘
```

Drag the handles to set the start and end, click the strip to scrub, and press
**Play** (or Space) to watch just the selection. Arrow keys nudge the playhead a
frame at a time, Shift+arrow ten.

Nothing is written until you choose: **Save Trimmed** cuts to the selection,
**Save Full Clip** keeps the whole take, **Discard** throws it away. Esc means
Save Full Clip. Any GIF is made *after* the trim, from what you kept.

The cut re-encodes rather than stream-copying, so the clip starts exactly where
the handle is instead of at the nearest keyframe. The preview is built from
stills that ffmpeg extracts in one pass while the editor is open, which is why
scrubbing is instant and why long clips get coarser preview frames rather than
a longer wait.

## Dependencies

- Python 3.11 (the `.venv` is built on it)
- PySide6-Essentials — only QtCore/QtGui/QtWidgets are used
- ffmpeg + ffprobe, for recording only

Rebuild the environment with:

```
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install PySide6-Essentials
```
