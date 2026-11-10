"""Fixed-region screenshot tool — pin one screen area, then shoot it repeatedly.

Snipping Tool makes you redraw the selection every time, so consecutive shots
never line up. This pins a region once and reshoots exactly it, which is what
you want for before/after images of the same panel.

Self-contained: this folder ships its own `.venv`, so the tool does not depend
on any other project or on which Python happens to be first on PATH.

    modules\\regionshot\\
        src\\region_shot.py   this file
        launch.cmd           run it alone, no console window
        icon.ico             tray/shortcut icon

The packages live in a venv at %LOCALAPPDATA%\\DeskKit\\regionshot\\venv, built
on first run (docs/module-contract.md 5.3). Settings and output go in
%APPDATA%\\DeskKit\\regionshot\\.

Start it with `launch.cmd`, or from the DeskKit shell (`DeskKit.exe --run regionshot`).

Everything is driven from the window: Select Region, Show, Capture and Record,
plus the pinned region's X/Y/W/H and the capture Delay. Everything that only
tunes the output — save folder, prefix, record format, FPS, GIF width — lives
behind the gear button in the status bar, next to `?`.

Nudging X/Y/W/H flashes the outline up for a moment so the new framing is
visible; with Show on it simply tracks the values live. Those four boxes sit
behind a padlock, since Select Region is the usual way to set a region and the
numbers are only for bumping it afterwards.

Recording uses ffmpeg's gdigrab to pull the region straight off the desktop —
no per-frame work in Python — writing MP4, GIF, or both. GIFs go through a
two-pass palette so UI text stays legible instead of dithering into mush.
Stills and clips share the same pinned region, so they frame identically.
Needs ffmpeg: the `ffmpeg_path` setting, the DeskKit helpers folder, or PATH.

With `Trim clips before saving` on, a recording stops into a trim editor —
filmstrip, two handles and a preview — and nothing is exported until the range
is settled.

Clicking Capture gives *this* window focus, which dims the target app's title
bar and drops its focus ring, so the shot no longer matches what you were
looking at. Set a Delay when that matters: the grab happens after the wait, so
you can click back into the target app first.

Region, output folder, prefix, delay and recording options persist between runs.
"""

from __future__ import annotations

import sys

MIN_PY = (3, 11)
MODULE_ID = "regionshot"
NAME = "RegionShot"
CONSOLE = False  # equals "console" in module.json
PY_MISSING = ("Python 3.11 or newer not found. "
              "Install it from https://www.python.org/downloads/windows/ and try again.")

# Exit codes, docs/module-contract.md section 9.1.
EXIT_OK, EXIT_ERROR, EXIT_USAGE, EXIT_HELPER, EXIT_RUNTIME, EXIT_DEVICE, EXIT_PACKAGES = range(7)


def fail(code: int, message: str) -> None:
    """One ERROR line on stderr; a message box when nobody can read it; exit."""
    try:
        if sys.stderr is not None:
            print(f"ERROR: {message}", file=sys.stderr)
    except OSError:
        pass
    import os
    if not CONSOLE and not os.environ.get("DESKKIT_SHELL"):
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, message, NAME, 0x10)  # MB_ICONERROR
    sys.exit(code)


if sys.version_info < MIN_PY:
    fail(EXIT_RUNTIME, PY_MISSING)

import ctypes
import hashlib
import json
import os
import queue
import re
import shutil
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Keep child consoles off the screen.
CREATE_NO_WINDOW = 0x08000000


# -- Paths --------------------------------------------------------------

def data_dir() -> Path:
    """%APPDATA%\\DeskKit\\regionshot, or what the shell passed in DESKKIT_DATA_DIR."""
    passed = os.environ.get("DESKKIT_DATA_DIR")
    if passed:
        return Path(passed)
    appdata = os.environ.get("APPDATA")
    if not appdata:
        fail(EXIT_ERROR, "APPDATA is not set, so there is nowhere to keep settings.")
    return Path(appdata) / "DeskKit" / MODULE_ID


def local_dir() -> Path:
    """%LOCALAPPDATA%\\DeskKit\\regionshot: large, rebuildable files (the venv)."""
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        fail(EXIT_ERROR, "LOCALAPPDATA is not set, so there is nowhere to build the venv.")
    return Path(local) / "DeskKit" / MODULE_ID


def helpers_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", "")) / "DeskKit" / "helpers"


# -- Venv bootstrap (contract 5.3) ---------------------------------------

def _venv_python(venv: Path, gui: bool) -> Path:
    exe = venv / "Scripts" / ("pythonw.exe" if gui else "python.exe")
    return exe if exe.is_file() else venv / "Scripts" / "python.exe"


def _run_hidden(cmd: list[str], progress=None) -> tuple[int, str]:
    """Run a command with no window; return (exit code, output). `progress` is
    called about 10 times a second while it runs."""
    proc = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW)
    chunks: list[bytes] = []
    reader = threading.Thread(
        target=lambda: chunks.append(proc.stdout.read()), daemon=True)
    reader.start()
    while proc.poll() is None:
        if progress:
            progress()
        time.sleep(0.1)
    reader.join(5)
    return proc.returncode, b"".join(chunks).decode("utf-8", "replace")


def _setup_window():
    """A small 'Setting up' window for the first run, or None without tkinter."""
    if os.environ.get("DESKKIT_NO_SETUP_WINDOW"):
        return None
    try:
        import tkinter
        root = tkinter.Tk()
        root.title(NAME)
        root.resizable(False, False)
        tkinter.Label(root, text=f"Setting up {NAME} (first run)...\nThis downloads "
                      "Python packages and takes a minute.", padx=24, pady=18).pack()
        root.update()
        return root
    except Exception:
        return None


def ensure_venv(setup_only: bool) -> None:
    """Make sure we run inside the venv; re-run the entry file there if not."""
    venv = local_dir() / "venv"
    try:
        inside = Path(sys.prefix).resolve() == venv.resolve()
    except OSError:
        inside = False
    if inside:
        if setup_only:
            sys.exit(EXIT_OK)
        return

    req = Path(__file__).with_name("requirements.txt")
    try:
        digest = hashlib.sha256(req.read_bytes()).hexdigest()
    except OSError as e:
        fail(EXIT_PACKAGES, f"Could not install Python packages for {NAME}: {e}")
    marker = venv / "deskkit-requirements.sha256"
    good = False
    py = venv / "Scripts" / "python.exe"
    if py.is_file() and marker.is_file():
        try:
            good = (marker.read_text(encoding="ascii").strip() == digest and subprocess.run(
                [str(py), "-c", "pass"], stdin=subprocess.DEVNULL, capture_output=True,
                creationflags=CREATE_NO_WINDOW, timeout=60).returncode == 0)
        except (OSError, subprocess.SubprocessError):
            good = False
    if not good:
        window = _setup_window()
        pump = (lambda: window.update()) if window else None
        try:
            if venv.exists():
                shutil.rmtree(venv, ignore_errors=True)
            venv.parent.mkdir(parents=True, exist_ok=True)
            code, out = _run_hidden([sys.executable, "-m", "venv", str(venv)], pump)
            if code == 0:
                code, out = _run_hidden(
                    [str(py), "-m", "pip", "install", "--disable-pip-version-check",
                     "-r", str(req)], pump)
            if code != 0:
                last = out.strip().splitlines()[-1] if out.strip() else f"exit code {code}"
                fail(EXIT_PACKAGES, f"Could not install Python packages for {NAME}: {last}")
            marker.write_text(digest, encoding="ascii")
        except OSError as e:
            fail(EXIT_PACKAGES, f"Could not install Python packages for {NAME}: {e}")
        finally:
            if window:
                try:
                    window.destroy()
                except Exception:
                    pass
    if setup_only:
        sys.exit(EXIT_OK)

    args = [a for a in sys.argv[1:] if a != "--deskkit-setup"]
    std = {name: subprocess.DEVNULL for name in ("stdin", "stdout", "stderr")
           if getattr(sys, name, None) is None}
    child = subprocess.Popen(
        [str(_venv_python(venv, not CONSOLE)), os.path.abspath(__file__), *args], **std)
    sys.exit(child.wait())


if __name__ == "__main__":
    ensure_venv("--deskkit-setup" in sys.argv[1:])

try:
    from PySide6.QtCore import (
        QEventLoop, QObject, QPoint, QPointF, QRect, QRectF, QSettings, QSize,
        Qt, QTimer, Signal,
    )
    from PySide6.QtGui import (
        QColor, QGuiApplication, QIcon, QImage, QPainter, QPainterPath, QPen,
        QPixmap, QRegion,
    )
    from PySide6.QtWidgets import (
        QAbstractSpinBox, QApplication, QCheckBox, QComboBox, QDialog,
        QDoubleSpinBox, QFileDialog, QFrame, QGridLayout, QHBoxLayout, QLabel,
        QLineEdit, QMessageBox, QPushButton, QSizePolicy, QSpinBox, QStyle,
        QStyleOptionComboBox, QStyleOptionSpinBox, QVBoxLayout, QWidget,
    )
except ImportError as exc:  # pragma: no cover
    fail(EXIT_PACKAGES, f"Could not load PySide6 ({exc}) with {sys.executable}. "
                        "Run launch.cmd, or delete the venv under "
                        "%LOCALAPPDATA%\\DeskKit\\regionshot and start again.")

# -- Diagnostics --------------------------------------------------------
# Under pythonw there is no console: a crash leaves no trace at all. Everything
# important is appended to {data_dir}\logs\regionshot.log.

LOG_PATH = data_dir() / "logs" / "regionshot.log"


def log(msg: str) -> None:
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        if LOG_PATH.exists() and LOG_PATH.stat().st_size > 1_000_000:
            os.replace(LOG_PATH, LOG_PATH.with_name(LOG_PATH.name + ".1"))
        with LOG_PATH.open("a", encoding="utf-8") as fh:
            fh.write(f"{time.strftime('%H:%M:%S')}  {msg}" + chr(10))
    except OSError:
        pass


def _log_crash(exc_type, exc, tb) -> None:
    import traceback
    log("CRASH " + "".join(traceback.format_exception(exc_type, exc, tb)).strip())
    sys.__excepthook__(exc_type, exc, tb)


sys.excepthook = _log_crash


# -- Settings: {data_dir}\settings.json (contract 6) ---------------------

DEFAULT_API_PORT = 47800
SETTINGS_DEFAULTS = {
    "x": 100, "y": 100, "w": 800, "h": 600,
    "dir": str(data_dir() / "output"),
    "prefix": "shot", "delay": 0.0, "clip": "false", "rec_format": "MP4",
    "fps": 12, "gif_width": 1000, "cursor": "true", "trim": "false",
    "unlocked": "false", "api_port": DEFAULT_API_PORT, "ffmpeg_path": "",
}
_INT_KEYS = {"x", "y", "w", "h", "fps", "gif_width", "api_port"}
_FLOAT_KEYS = {"delay"}


class JsonSettings:
    """Drop-in for the QSettings calls this file makes: value() and setValue().
    Keeps a JSON object in settings.json and writes it when a value changes."""

    def __init__(self, path: Path):
        self._path = path
        self._data: dict = {}
        try:
            loaded = json.loads(path.read_text(encoding="utf-8-sig"))
            if isinstance(loaded, dict):
                self._data = loaded
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as e:
            log(f"settings: cannot read {path}: {e} - using defaults")
        added = False
        for key, val in SETTINGS_DEFAULTS.items():
            if key not in self._data:
                self._data[key] = val
                added = True
        if added or not path.exists():
            self._write()

    def value(self, key, default=None):
        return self._data.get(key, default)

    def setValue(self, key, val):
        if self._data.get(key) != val:
            self._data[key] = val
            self._write()

    def _write(self):
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self._path.with_name(self._path.name + ".tmp")
            tmp.write_text(json.dumps(self._data, indent=2) + "\n", encoding="utf-8")
            os.replace(tmp, self._path)
        except OSError as e:
            log(f"settings: cannot write {self._path}: {e}")


def _migrate_registry_settings(path: Path) -> None:
    """Once, when settings.json does not exist yet: copy the old QSettings values
    (HKCU\\Software\\RegionShot\\RegionShot) into it. Copy only: the registry key
    and the old output folder stay as they are. The 'dir' value keeps pointing
    at the user's old output folder."""
    if path.exists() or os.environ.get("DESKKIT_NO_MIGRATE"):
        return
    old = QSettings("RegionShot", "RegionShot")
    keys = [k for k in old.allKeys() if k in SETTINGS_DEFAULTS]
    if not keys:
        return
    data = dict(SETTINGS_DEFAULTS)
    for k in keys:
        raw = old.value(k)
        try:
            if k in _INT_KEYS:
                raw = int(raw)
            elif k in _FLOAT_KEYS:
                raw = float(raw)
            else:
                raw = str(raw)
        except (TypeError, ValueError):
            continue
        data[k] = raw
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        log(f"migrated {len(keys)} settings from the registry (old key kept)")
    except OSError as e:
        log(f"settings migration failed: {e}")


_SETTINGS: JsonSettings | None = None   # set in main(); find_ffmpeg reads it


RECORD_FORMATS = ["MP4", "GIF", "MP4 + GIF"]

# Outline colours: orange while a still is being taken, red while recording.
OUTLINE_SHOT = (255, 176, 32)
OUTLINE_REC = (232, 58, 48)

# The three action buttons are colour-coded to match what they do: green to
# pick, orange for a still (same orange as its outline), red for a recording.
ACTION_COLORS = {
    "select": ("#2e9b4f", "#37b95e", "#24803f"),
    "capture": ("#d9822b", "#ec9539", "#b46a1e"),
    "record": ("#d13b2e", "#e64a3b", "#ad2c21"),
}


def action_style(kind: str) -> str:
    base, hover, pressed = ACTION_COLORS[kind]
    return (f"QPushButton {{ background-color: {base}; color: white;"
            f" font-weight: bold; border: none; border-radius: 4px;"
            f" padding: 6px 14px; }}"
            f"QPushButton:hover {{ background-color: {hover}; }}"
            f"QPushButton:pressed {{ background-color: {pressed}; }}")


def find_ffmpeg() -> str | None:
    """Helper detect rule (contract 8.2): the ffmpeg_path setting, then the
    DeskKit helpers folder, then PATH."""
    custom = str(_SETTINGS.value("ffmpeg_path", "") if _SETTINGS else "").strip()
    if custom and Path(custom).is_file():
        return custom
    hits = sorted((helpers_dir() / "ffmpeg").glob("*/bin/ffmpeg.exe"))
    if hits:
        return str(hits[-1])
    return shutil.which("ffmpeg")


def find_ffprobe() -> str | None:
    """ffprobe sits next to ffmpeg.exe. Only the trim editor needs it, so a
    missing one degrades rather than breaks."""
    ffmpeg = find_ffmpeg()
    if ffmpeg:
        beside = Path(ffmpeg).with_name("ffprobe.exe")
        if beside.is_file():
            return str(beside)
    return shutil.which("ffprobe")


FFMPEG_MISSING = ("FFmpeg not found. Recording needs it. Download it from "
                  "https://www.gyan.dev/ffmpeg/builds/ and put ffmpeg.exe on PATH, "
                  "or set ffmpeg_path in settings.json.")


def probe_duration(path: Path) -> float | None:
    """Clip length in seconds, or None if ffprobe can't say."""
    ffprobe = find_ffprobe()
    if ffprobe is None:
        return None
    try:
        out = subprocess.run(
            [ffprobe, "-v", "error", "-show_entries", "format=duration",
             "-of", "default=nw=1:nk=1", str(path)],
            capture_output=True, timeout=15, creationflags=CREATE_NO_WINDOW)
    except (OSError, subprocess.SubprocessError):
        return None
    try:
        secs = float(out.stdout.decode("utf-8", "replace").strip())
    except ValueError:
        return None
    return secs if secs > 0 else None

# Windows fades a window out over ~200ms when it hides. Grab too soon and a
# ghost of this window ends up in the shot, so wait the animation out.
HIDE_SETTLE_MS = 380

# How long the outline flashes up after X/Y/W/H is nudged while Show is off —
# long enough to see where the region landed, short enough not to sit on top of
# whatever is being framed. Each nudge restarts it, so it stays up for as long
# as the user keeps adjusting and clears shortly after they stop.
PEEK_MS = 1200

# The action buttons are this tall, fixed. They used to grow from 40 up to this
# with the window, but the window now opens at exactly its layout minimum so
# that no empty band sits between the region row and the status bar — and at
# the minimum there is no slack to grow into, so "growing" only ever meant
# "start small". Measured: the stretch above the status bar takes the first
# ~32px of any slack before the buttons see any of it, so the opening height
# has to be the minimum for the gap to be zero.
ACTION_H = 72

# Offered in the Delay dropdown. Any other number can still be typed in.
DELAY_PRESETS = (0.0, 1.0, 3.0, 5.0)
DELAY_MAX = 600.0

# The local HTTP API listens here (127.0.0.1 only, never 0.0.0.0). Overridable
# via `api_port` in settings.json — there is no UI for it. A
# taken port disables the API for the session; it never blocks the GUI.
DEFAULT_API_PORT = 47800

# ── Trim editor ───────────────────────────────────────────────────────
# The preview is built out of JPEG stills that ffmpeg extracts from the clip in
# one pass, rather than a video player: PySide6-Essentials here has no
# QtMultimedia, and ffmpeg is already a hard dependency of recording. One
# process writes every frame the editor will ever need, so scrubbing is a
# dictionary lookup rather than a seek — see TrimDialog.
TRIM_PLAY_FPS = 10.0        # preview frame rate; also the scrub resolution
TRIM_MAX_FRAMES = 900       # cap for long clips — the rate drops to stay under
TRIM_FRAME_H = 360          # extracted frame height, never upscaled past source
TRIM_PREVIEW_H = 230        # the preview pane
TRIM_STRIP_H = 58           # the filmstrip / handle bar
TRIM_MIN_MS = 100           # the shortest selection the handles allow
TRIM_ACCENT = OUTLINE_SHOT  # same orange as the region outline


# ── Small UI helpers ──────────────────────────────────────────────────

# The two status-bar buttons — gear and ? — are the same 24px circle, so the
# style lives in one place and they cannot drift apart.
ROUND_BTN_STYLE = (
    "QPushButton { border: 1px solid palette(mid); border-radius: 12px;"
    " font-weight: bold; }"
    "QPushButton:hover { background-color: palette(midlight); }")

# The region lock. Checked means "manual override on", so it is the checked
# state that gets the highlight — the icon swaps to an open padlock alongside.
LOCK_BTN_STYLE = (
    "QPushButton { border: 1px solid palette(mid); border-radius: 14px; }"
    "QPushButton:hover { background-color: palette(midlight); }"
    "QPushButton:checked { background-color: palette(highlight);"
    " border-color: palette(highlight); }")


def heading(text: str) -> QLabel:
    lbl = QLabel(text)
    font = lbl.font()
    font.setBold(True)
    lbl.setFont(font)
    return lbl


# QLineEdit keeps 2px of its own margin either side of the text and another
# pixel or two for the cursor, none of which SC_SpinBoxEditField knows about.
# At 9pt that gap was the difference between "3439" and "343" — the style rect
# said 32px of room, the text got 23. Found by rendering the same box at every
# width from 74 to 98px and reading which one first painted the last digit: 86,
# against the 80 the style maths predicted.
EDIT_SLACK = 14


def fit_spin(spin: QAbstractSpinBox, *texts: str) -> int:
    """Guarantee `spin` can show every one of `texts`, whatever the system font.

    Qt's own spin-box sizeHint under-measures under the Windows 11 style: the
    two arrow buttons sit side by side and take more width than
    sizeFromContents budgets for, so the first thing to be clipped is the value
    itself. At a 16pt system font — verified — Delay dropped its " s" suffix,
    GIF width rendered "1000" as "10", and FPS showed no number at all, while
    every widget still measured >= its sizeHint. Trusting sizeHint here is
    exactly the mistake.

    So ask the style how much of a spin box is *not* the text (probing with a
    known width, since the real geometry does not exist yet) and add the text
    that has to fit.
    """
    opt = QStyleOptionSpinBox()
    opt.initFrom(spin)
    # initFrom() does NOT copy this, and the default is UpDownArrows — so
    # without it a NoButtons spin box is still measured as though it had the
    # (very wide, under this style) arrows, and comes out needlessly padded.
    opt.buttonSymbols = spin.buttonSymbols()
    opt.subControls = (QStyle.SubControl.SC_SpinBoxFrame
                       | QStyle.SubControl.SC_SpinBoxEditField)
    if spin.buttonSymbols() != QAbstractSpinBox.ButtonSymbols.NoButtons:
        opt.subControls |= (QStyle.SubControl.SC_SpinBoxUp
                            | QStyle.SubControl.SC_SpinBoxDown)
    probe = 1000
    opt.rect = QRect(0, 0, probe, spin.sizeHint().height())
    edit = spin.style().subControlRect(
        QStyle.ComplexControl.CC_SpinBox, opt,
        QStyle.SubControl.SC_SpinBoxEditField, spin)
    chrome = probe - edit.width()          # frame + whatever buttons are shown
    fm = spin.fontMetrics()
    need = max(fm.horizontalAdvance(t) for t in texts) + chrome + EDIT_SLACK
    spin.setMinimumWidth(need)
    return need


def fit_combo(combo: QComboBox, *texts: str) -> None:
    """`fit_spin` for combo boxes — same reasoning, different sub-control.

    Also covers the editable case, where the arrow *and* a line-edit frame come
    out of the width before the text gets any.
    """
    opt = QStyleOptionComboBox()
    opt.initFrom(combo)
    opt.editable = combo.isEditable()
    opt.subControls = (QStyle.SubControl.SC_ComboBoxFrame
                       | QStyle.SubControl.SC_ComboBoxArrow
                       | QStyle.SubControl.SC_ComboBoxEditField)
    probe = 1000
    opt.rect = QRect(0, 0, probe, combo.sizeHint().height())
    edit = combo.style().subControlRect(
        QStyle.ComplexControl.CC_ComboBox, opt,
        QStyle.SubControl.SC_ComboBoxEditField, combo)
    chrome = probe - edit.width()
    fm = combo.fontMetrics()
    combo.setMinimumWidth(max(
        [combo.minimumWidth()]
        + [fm.horizontalAdvance(t) + chrome + EDIT_SLACK for t in texts]))


def fmt_secs(secs: float) -> str:
    """`1 s`, `2.5 s`, `0 s` — short, and what the Delay box parses back."""
    return f"{secs:g} s"


def fmt_clock(ms: float) -> str:
    """`0:03.4` — clip positions, at the tenth the trim handles resolve to."""
    secs = max(0.0, ms / 1000.0)
    return f"{int(secs // 60)}:{secs % 60:04.1f}"


class DelayBox(QComboBox):
    """Delay: pick a preset from the dropdown, or type any number of seconds.

    Presented to the rest of the app as the QDoubleSpinBox it replaced —
    `value()`, `setValue()`, `maximum()` and a `valueChanged` signal — so
    `capture()`, `_save()` and `_load()` did not have to learn about it.

    Typed text is parsed leniently (the first number wins, so "3", "3 s" and
    "3s" all mean the same) and then rewritten in canonical form. A validator
    would have to reject the " s" suffix mid-keystroke, which fights the user
    while they type.
    """

    valueChanged = Signal(float)

    def __init__(self):
        super().__init__()
        self.setEditable(True)
        self.setInsertPolicy(QComboBox.InsertPolicy.NoInsert)
        for secs in DELAY_PRESETS:
            self.addItem(fmt_secs(secs), secs)
        self._value = float(DELAY_PRESETS[0])
        self.setCurrentIndex(0)
        self.lineEdit().editingFinished.connect(self._commit_text)
        self.activated.connect(self._commit_index)

    # -- the QDoubleSpinBox-shaped half -------------------------------
    def value(self) -> float:
        return self._value

    def maximum(self) -> float:
        return DELAY_MAX

    def setValue(self, secs: float):
        secs = max(0.0, min(DELAY_MAX, float(secs)))
        changed = secs != self._value
        self._value = secs
        if self.currentText() != fmt_secs(secs):
            self.setCurrentText(fmt_secs(secs))
        if changed:
            self.valueChanged.emit(secs)

    # -- input ---------------------------------------------------------
    def _commit_text(self):
        self.setValue(self._parse(self.lineEdit().text()))
        # Rewrite even when the value did not change, so a half-typed "3 ss"
        # does not stay on screen looking like it was accepted.
        self.lineEdit().setText(fmt_secs(self._value))

    def _commit_index(self, index: int):
        data = self.itemData(index)
        self.setValue(self._value if data is None else float(data))

    def _parse(self, text: str) -> float:
        match = re.search(r"\d+(?:[.,]\d+)?", text)
        return float(match.group().replace(",", ".")) if match else self._value


def lock_icon(px: int, color: QColor, locked: bool) -> QIcon:
    """A padlock, painted for the same reason the gear is (see `gear_icon`).

    Unlocked is the shackle lifted clear and swung right, which reads at 16px
    where a subtler "open" hint does not.
    """
    scale = 4
    n = px * scale
    pm = QPixmap(n, n)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.translate(n / 2, n / 2)

    body = QRectF(-n * 0.30, -n * 0.06, n * 0.60, n * 0.42)
    radius = n * 0.18                      # shackle
    cx = 0.0 if locked else n * 0.12
    cy = body.top() - n * (0.06 if locked else 0.16)

    path = QPainterPath()
    arc = QRectF(cx - radius, cy - radius, radius * 2, radius * 2)
    if locked:
        path.moveTo(cx - radius, body.top())
        path.lineTo(cx - radius, cy)
    else:
        # No left leg: the gap is what says "open".
        path.moveTo(cx - radius, cy)
    path.arcTo(arc, 180, -180)
    path.lineTo(cx + radius, body.top())
    pen = QPen(color, n * 0.10)
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.setBrush(Qt.BrushStyle.NoBrush)
    p.drawPath(path)

    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(color)
    p.drawRoundedRect(body, n * 0.07, n * 0.07)
    p.setCompositionMode(QPainter.CompositionMode.CompositionMode_Clear)
    p.drawEllipse(QPointF(0.0, body.center().y()), n * 0.07, n * 0.07)
    p.end()
    pm.setDevicePixelRatio(scale)
    return QIcon(pm)


def gear_icon(px: int, color: QColor) -> QIcon:
    """A gear, drawn rather than typed.

    U+2699 is the obvious way to do this, but on Windows it can fall through to
    Segoe UI Emoji and arrive as a colour sticker sitting next to the flat `?`
    on the button beside it. Painting it keeps the pair matching, and follows
    the palette instead of the emoji font.
    """
    scale = 4                       # drawn oversized, then scaled for HiDPI
    n = px * scale
    pm = QPixmap(n, n)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.translate(n / 2, n / 2)
    p.setPen(Qt.PenStyle.NoPen)
    p.setBrush(color)
    p.drawEllipse(QPointF(0, 0), n * 0.30, n * 0.30)
    tooth = QRectF(-n * 0.08, -n * 0.45, n * 0.16, n * 0.90)
    for i in range(4):              # four bars crossing at 45° = eight teeth
        p.save()
        p.rotate(i * 45)
        p.drawRoundedRect(tooth, n * 0.03, n * 0.03)
        p.restore()
    p.setCompositionMode(QPainter.CompositionMode.CompositionMode_Clear)
    p.drawEllipse(QPointF(0, 0), n * 0.13, n * 0.13)   # the hub
    p.end()
    pm.setDevicePixelRatio(scale)
    return QIcon(pm)


# ── Screen capture ────────────────────────────────────────────────────

def grab_region(rect: QRect) -> QImage | None:
    """Grab `rect` (global logical coords) off the screen containing it."""
    if rect.width() <= 0 or rect.height() <= 0:
        return None
    screen = QGuiApplication.screenAt(rect.center()) or QGuiApplication.primaryScreen()
    if screen is None:
        return None

    pixmap = screen.grabWindow(0)
    dpr = pixmap.devicePixelRatio() or 1.0
    geo = screen.geometry()
    # Qt hands back either just this screen or the whole virtual desktop
    # depending on platform/version; pick the origin that matches what we got.
    if abs(pixmap.width() - round(geo.width() * dpr)) > 2:
        origin = screen.virtualGeometry().topLeft()
    else:
        origin = geo.topLeft()

    src = QRect(round((rect.x() - origin.x()) * dpr),
                round((rect.y() - origin.y()) * dpr),
                round(rect.width() * dpr),
                round(rect.height() * dpr))
    img = pixmap.toImage().copy(src)
    img.setDevicePixelRatio(1.0)
    return img


def _pump(ms: int) -> None:
    loop = QEventLoop()
    QTimer.singleShot(max(0, int(ms)), loop.quit)
    loop.exec()


# ── Region picker overlay ─────────────────────────────────────────────

class RegionOverlay(QWidget):
    """Full-desktop dimmed overlay; drag to carve out the capture region."""

    def __init__(self, initial: QRect, on_done):
        super().__init__(None, Qt.WindowType.FramelessWindowHint
                         | Qt.WindowType.WindowStaysOnTopHint
                         | Qt.WindowType.Tool)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setCursor(Qt.CursorShape.CrossCursor)
        self._virt = QGuiApplication.primaryScreen().virtualGeometry()
        self.setGeometry(self._virt)
        self._rect = QRect(initial) if initial.isValid() else QRect()
        self._anchor: QPoint | None = None
        self._on_done = on_done

    def paintEvent(self, _event):
        p = QPainter(self)
        p.fillRect(self.rect(), QColor(0, 0, 0, 120))
        if not self._rect.isValid():
            self._draw_hint(p)
            return

        local = self._rect.translated(-self._virt.topLeft())
        # Alpha 1, not 0: Windows makes a translucent window click-through
        # wherever its alpha is exactly 0, so a fully cleared hole would send
        # clicks to the app underneath — making it impossible to start a new
        # drag inside the current region.
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_Source)
        p.fillRect(local, QColor(0, 0, 0, 1))
        p.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceOver)

        p.setPen(QPen(QColor(255, 190, 60), 2))
        p.drawRect(local.adjusted(0, 0, -1, -1))

        label = f"{self._rect.width()} x {self._rect.height()}"
        p.setPen(QColor(255, 255, 255))
        font = p.font()
        font.setPointSize(11)
        font.setBold(True)
        p.setFont(font)
        box = QRect(local.left(), max(0, local.top() - 26), 160, 22)
        p.fillRect(box, QColor(20, 20, 20, 210))
        p.drawText(box.adjusted(6, 0, 0, 0),
                   Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, label)
        self._draw_hint(p)

    def _draw_hint(self, p: QPainter):
        p.setPen(QColor(235, 235, 235))
        font = p.font()
        font.setPointSize(13)
        font.setBold(False)
        p.setFont(font)
        p.drawText(QRect(0, 40, self.width(), 30),
                   Qt.AlignmentFlag.AlignHCenter,
                   "Drag to set the capture region     ·     Enter to keep the "
                   "current one     ·     Esc to cancel")

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.LeftButton:
            self._anchor = event.globalPosition().toPoint()
            self._rect = QRect(self._anchor, self._anchor)
            self.update()

    def mouseMoveEvent(self, event):
        if self._anchor is not None:
            self._rect = QRect(self._anchor,
                               event.globalPosition().toPoint()).normalized()
            self.update()

    def mouseReleaseEvent(self, event):
        if event.button() != Qt.MouseButton.LeftButton or self._anchor is None:
            return
        self._anchor = None
        if self._rect.width() >= 8 and self._rect.height() >= 8:
            self._finish(self._rect)

    def keyPressEvent(self, event):
        if event.key() == Qt.Key.Key_Escape:
            self._finish(None)
        elif event.key() in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
            self._finish(self._rect if self._rect.isValid() else None)

    def _finish(self, rect: QRect | None):
        self.close()
        self._on_done(rect)


class RegionOutline(QWidget):
    """Dotted click-through outline sitting just OUTSIDE the pinned region.

    It has to be visible while a capture or recording is running, yet must
    never appear in the output. The window covers the region plus a PAD ring,
    and painting is clipped to that ring — so nothing can be drawn on a pixel
    that the capture will read, regardless of pen width or rounding.

    WA_ShowWithoutActivating matters as much: the outline goes up before a
    capture and stays up through a recording, and taking focus would dim the
    target app's title bar — changing the very thing being captured.
    """

    PAD = 5
    PEN = 3

    def __init__(self, rect: QRect, color: QColor):
        super().__init__(None, Qt.WindowType.FramelessWindowHint
                         | Qt.WindowType.WindowStaysOnTopHint
                         | Qt.WindowType.Tool
                         | Qt.WindowType.WindowTransparentForInput)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)
        self._color = QColor(color)
        # The protected area, in widget-local coordinates.
        self.inner = QRect()
        self.set_region(rect)

    def color(self) -> QColor:
        return QColor(self._color)

    def set_region(self, rect: QRect):
        """Move and resize onto `rect` without recreating the window.

        Nudging X/Y/W/H fires this on every step — and once on every keystroke
        while typing a value — so tearing the top-level window down and
        building a new one each time would flicker.
        """
        self.inner = QRect(self.PAD, self.PAD, rect.width(), rect.height())
        self.setGeometry(rect.adjusted(-self.PAD, -self.PAD, self.PAD, self.PAD))
        self.update()

    def paintEvent(self, _event):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        # Hard guarantee that the outline cannot enter the captured pixels.
        p.setClipRegion(QRegion(self.rect()) - QRegion(self.inner))
        pen = QPen(self._color, self.PEN)
        pen.setStyle(Qt.PenStyle.CustomDashLine)
        pen.setDashPattern([2, 2])   # dotted, in multiples of the pen width
        pen.setCapStyle(Qt.PenCapStyle.FlatCap)
        p.setPen(pen)
        p.setBrush(Qt.BrushStyle.NoBrush)
        # Centred so the whole stroke lands in the ring, never on `inner`.
        p.drawRect(self.inner.adjusted(-3, -3, 2, 2))


# ── Trim editor ───────────────────────────────────────────────────────

class PreviewPane(QWidget):
    """The trim editor's frame view.

    A widget rather than a QLabel holding a pixmap, because a QLabel takes its
    sizeHint from that pixmap: scale a frame to fit the label and the label
    grows to the frame, the dialog grows to the label, and the next render
    scales to the bigger box again. Painting the frame instead means the pane
    is whatever size the layout gave it and the picture follows.
    """

    def __init__(self):
        super().__init__()
        self._pm: QPixmap | None = None
        self._text = "Loading preview…"
        self._scaled: QPixmap | None = None
        self._scaled_for = None
        self.setMinimumHeight(TRIM_PREVIEW_H)
        self.setSizePolicy(QSizePolicy.Policy.Expanding,
                           QSizePolicy.Policy.Expanding)

    def set_frame(self, pm: QPixmap):
        self._pm = pm
        self.update()

    def set_text(self, text: str):
        self._text = text
        self.update()

    def paintEvent(self, _event):
        p = QPainter(self)
        p.fillRect(self.rect(), QColor(18, 18, 18))
        p.setPen(QColor(88, 88, 88))
        p.drawRect(self.rect().adjusted(0, 0, -1, -1))
        if self._pm is None or self._pm.isNull():
            p.setPen(QColor(172, 172, 172))
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, self._text)
            return
        # Strictly inside the pane, so the frame can never argue the layout
        # into growing, and cached so a repaint is not a rescale.
        box = self.size() - QSize(8, 8)
        key = (self._pm.cacheKey(), box.width(), box.height())
        if self._scaled is None or self._scaled_for != key:
            self._scaled = self._pm.scaled(
                box, Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation)
            self._scaled_for = key
        pm = self._scaled
        p.drawPixmap((self.width() - pm.width()) // 2,
                     (self.height() - pm.height()) // 2, pm)


class TrimBar(QWidget):
    """Filmstrip with a start handle, an end handle and a playhead.

    Modelled on the Windows Photos trimmer: thumbnails across the whole clip,
    the excluded ends dimmed, a grip at each edge of the selection. Dragging a
    handle drags the playhead with it — that is what makes the preview show the
    frame you are trimming *to*, rather than wherever the playhead was left.

    It owns the selection; the dialog's spin boxes are a second view of it.
    """

    HANDLE_W = 11
    GRAB = 15            # how near a handle a click counts as grabbing it

    rangeChanged = Signal(int, int)      # start_ms, end_ms
    scrubbed = Signal(int)               # playhead ms
    playToggled = Signal()

    def __init__(self, duration_ms: int, thumb_at):
        super().__init__()
        self._dur = max(1, int(duration_ms))
        self._thumb_at = thumb_at        # (ms, height) -> QPixmap | None
        # A clip shorter than the nominal minimum selection still has to be
        # trimmable to something, so the floor is never longer than the clip.
        self._min = min(TRIM_MIN_MS, self._dur)
        self._start = 0
        self._end = self._dur
        self._pos = 0
        self._drag: str | None = None
        self.setFixedHeight(TRIM_STRIP_H)
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)

    # -- state ---------------------------------------------------------
    def start(self) -> int:
        return self._start

    def end(self) -> int:
        return self._end

    def position(self) -> int:
        return self._pos

    def set_range(self, start_ms: int, end_ms: int, emit: bool = True):
        self._start = max(0, min(int(start_ms), self._dur - self._min))
        self._end = max(self._start + self._min, min(int(end_ms), self._dur))
        self._pos = max(self._start, min(self._pos, self._end))
        self.update()
        if emit:
            self.rangeChanged.emit(self._start, self._end)

    def set_position(self, ms: int, emit: bool = True):
        self._pos = max(self._start, min(int(ms), self._end))
        self.update()
        if emit:
            self.scrubbed.emit(self._pos)

    # -- geometry ------------------------------------------------------
    def track(self) -> QRect:
        # Inset by a handle's width either side, so a handle parked at 0 or at
        # the very end is still drawn whole and still grabbable.
        return self.rect().adjusted(self.HANDLE_W, 0, -self.HANDLE_W, 0)

    def x_of(self, ms: float) -> float:
        tr = self.track()
        return tr.left() + tr.width() * (ms / self._dur)

    def ms_of(self, x: float) -> int:
        tr = self.track()
        if tr.width() <= 0:
            return 0
        return int(round(max(0.0, min(1.0, (x - tr.left()) / tr.width()))
                         * self._dur))

    # -- painting ------------------------------------------------------
    def paintEvent(self, _event):
        p = QPainter(self)
        tr = self.track()
        p.fillRect(tr, QColor(38, 38, 38))

        p.save()
        p.setClipRect(tr)
        probe = self._thumb_at(0, tr.height())
        if probe is not None and probe.width() > 0:
            tw = probe.width()
            x = float(tr.left())
            while x < tr.right():
                pm = self._thumb_at(self.ms_of(x + tw / 2), tr.height())
                if pm is None:
                    break
                p.drawPixmap(int(round(x)), tr.top(), pm)
                x += tw
        p.restore()

        sx, ex = self.x_of(self._start), self.x_of(self._end)
        dim = QColor(0, 0, 0, 150)
        p.fillRect(QRectF(tr.left(), tr.top(), sx - tr.left(), tr.height()), dim)
        p.fillRect(QRectF(ex, tr.top(), tr.right() + 1 - ex, tr.height()), dim)

        accent = QColor(*TRIM_ACCENT)
        p.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        p.setPen(QPen(accent, 2))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawRect(QRectF(sx, tr.top() + 1, ex - sx, tr.height() - 2))
        for x in (sx, ex):
            self._draw_handle(p, x, tr, accent)

        px = self.x_of(self._pos)
        p.setPen(QPen(QColor(255, 255, 255), 2))
        p.drawLine(QPointF(px, tr.top()), QPointF(px, tr.bottom() + 1))

    def _draw_handle(self, p: QPainter, x: float, tr: QRect, accent: QColor):
        box = QRectF(x - self.HANDLE_W / 2, tr.top() - 1,
                     self.HANDLE_W, tr.height() + 2)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(accent)
        p.drawRoundedRect(box, 3, 3)
        p.setPen(QPen(QColor(48, 30, 6), 1))     # grip lines
        for dx in (-2.0, 2.0):
            p.drawLine(QPointF(box.center().x() + dx, box.top() + 7),
                       QPointF(box.center().x() + dx, box.bottom() - 7))

    # -- input ---------------------------------------------------------
    def _nearest(self, x: float) -> str | None:
        ds, de = abs(x - self.x_of(self._start)), abs(x - self.x_of(self._end))
        if min(ds, de) > self.GRAB:
            return None
        return "start" if ds <= de else "end"

    def mousePressEvent(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            return
        x = event.position().x()
        self._drag = self._nearest(x) or "pos"
        self._apply_drag(x)

    def mouseMoveEvent(self, event):
        if self._drag is None:
            self.setCursor(Qt.CursorShape.SizeHorCursor
                           if self._nearest(event.position().x())
                           else Qt.CursorShape.PointingHandCursor)
            return
        self._apply_drag(event.position().x())

    def mouseReleaseEvent(self, _event):
        self._drag = None

    def _apply_drag(self, x: float):
        ms = self.ms_of(x)
        if self._drag == "start":
            self._start = max(0, min(ms, self._end - self._min))
            self._pos = self._start
            self.rangeChanged.emit(self._start, self._end)
        elif self._drag == "end":
            self._end = min(self._dur, max(ms, self._start + self._min))
            self._pos = self._end
            self.rangeChanged.emit(self._start, self._end)
        else:
            self._pos = max(self._start, min(ms, self._end))
        self.scrubbed.emit(self._pos)
        self.update()

    def keyPressEvent(self, event):
        step = int(round(1000 / TRIM_PLAY_FPS))
        if event.modifiers() & Qt.KeyboardModifier.ShiftModifier:
            step *= 10
        key = event.key()
        if key == Qt.Key.Key_Left:
            self.set_position(self._pos - step)
        elif key == Qt.Key.Key_Right:
            self.set_position(self._pos + step)
        elif key == Qt.Key.Key_Home:
            self.set_position(self._start)
        elif key == Qt.Key.Key_End:
            self.set_position(self._end)
        elif key == Qt.Key.Key_Space:
            self.playToggled.emit()
        else:
            super().keyPressEvent(event)


class TrimDialog(QDialog):
    """Set a clip's start and end before it is saved.

    Windows Photos' trim, without leaving the app that made the recording.

    The preview is stills, not video: PySide6-Essentials here ships no
    QtMultimedia, and ffmpeg — already required for recording — can produce
    every frame the editor will ever show in a single pass. So one process
    extracts the whole clip to small JPEGs while the dialog is open, and
    scrubbing, the filmstrip and playback are all dictionary lookups against
    those files rather than seeks. Frames are picked up as they are written, so
    the editor is usable a moment after it opens instead of after the whole
    clip has been decoded.

    Nothing here touches the clip. The dialog reports what the user chose
    through `outcome`, and RegionShot does the cutting.
    """

    TRIMMED, FULL, DISCARD = "trimmed", "full", "discarded"

    def __init__(self, path: Path, duration_ms: int, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Region Shot — Trim clip")
        self.setMinimumWidth(560)
        self._path = Path(path)
        self._dur = max(1, int(duration_ms))
        # Esc and the X button leave this alone: the safe default is to keep
        # the recording whole, never to lose a take to a stray key.
        self.outcome = self.FULL

        self._tmp = Path(tempfile.mkdtemp(prefix="regionshot-trim-"))
        self._proc: subprocess.Popen | None = None
        self._fps = TRIM_PLAY_FPS
        self._avail = 0              # frames ffmpeg has finished writing
        self._frames: dict[int, QPixmap] = {}
        self._frame_order: list[int] = []
        self._thumbs: dict[tuple[int, int], QPixmap] = {}
        self._quiet = False          # bar <-> spin boxes, one writer at a time

        self._play_timer = QTimer(self)
        self._play_timer.timeout.connect(self._tick_play)
        self._scan_timer = QTimer(self)
        self._scan_timer.timeout.connect(self._scan_frames)

        self._build()
        self._start_extract()

    # -- UI ------------------------------------------------------------
    def _build(self):
        lay = QVBoxLayout(self)
        lay.setSpacing(8)

        self._preview = PreviewPane()
        lay.addWidget(self._preview, stretch=1)

        clock_row = QHBoxLayout()
        self._clock = QLabel()
        clock_row.addWidget(self._clock)
        clock_row.addStretch(1)
        self._length = QLabel()
        clock_row.addWidget(self._length)
        lay.addLayout(clock_row)

        self._bar = TrimBar(self._dur, self._thumb_at)
        self._bar.setToolTip(
            "Drag the orange handles to set the start and end · click the "
            "strip to scrub · Space plays the selection")
        self._bar.rangeChanged.connect(self._on_range)
        self._bar.scrubbed.connect(self._on_scrub)
        self._bar.playToggled.connect(self.toggle_play)
        lay.addWidget(self._bar)

        ctl = QHBoxLayout()
        ctl.setSpacing(8)
        self._play_btn = QPushButton("Play")
        self._play_btn.setMinimumWidth(84)
        self._play_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self._play_btn.setToolTip("Play the selected range")
        self._play_btn.clicked.connect(self.toggle_play)
        ctl.addWidget(self._play_btn)
        ctl.addStretch(1)
        secs = self._dur / 1000.0
        self._start_spin = self._make_spin(secs, 0.0)
        self._end_spin = self._make_spin(secs, secs)
        self._start_spin.valueChanged.connect(self._on_spin)
        self._end_spin.valueChanged.connect(self._on_spin)
        ctl.addWidget(QLabel("Start:"))
        ctl.addWidget(self._start_spin)
        ctl.addSpacing(8)
        ctl.addWidget(QLabel("End:"))
        ctl.addWidget(self._end_spin)
        lay.addLayout(ctl)

        lay.addSpacing(4)
        btns = QHBoxLayout()
        discard = QPushButton("Discard")
        discard.setToolTip("Delete this recording without saving anything")
        discard.clicked.connect(self._discard)
        btns.addWidget(discard)
        btns.addStretch(1)
        full = QPushButton("Save Full Clip")
        full.setToolTip("Keep the whole recording, untrimmed")
        full.clicked.connect(self._keep_full)
        btns.addWidget(full)
        self._save_btn = QPushButton("Save Trimmed")
        self._save_btn.setDefault(True)
        self._save_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self._save_btn.setStyleSheet(action_style("record"))
        self._save_btn.setToolTip("Save only the selected range")
        self._save_btn.clicked.connect(self._keep_trimmed)
        btns.addWidget(self._save_btn)
        lay.addLayout(btns)

        self._update_labels()
        self._bar.setFocus()

    def _make_spin(self, maximum: float, value: float) -> QDoubleSpinBox:
        spin = QDoubleSpinBox()
        spin.setDecimals(2)
        spin.setRange(0.0, maximum)
        spin.setSingleStep(0.1)
        spin.setSuffix(" s")
        spin.setValue(value)
        # fit_spin, never sizeHint — the arrows under this style take more
        # width than sizeFromContents budgets and the value is what gets cut.
        fit_spin(spin, f"{maximum:.2f} s")
        return spin

    # -- selection -----------------------------------------------------
    def start_ms(self) -> int:
        return self._bar.start()

    def end_ms(self) -> int:
        return self._bar.end()

    def is_full(self) -> bool:
        """True when the selection is the whole clip — nothing to cut."""
        return self._bar.start() <= 0 and self._bar.end() >= self._dur

    def _on_range(self, start_ms: int, end_ms: int):
        if self._quiet:
            return
        self._quiet = True
        self._start_spin.setValue(start_ms / 1000.0)
        self._end_spin.setValue(end_ms / 1000.0)
        self._quiet = False
        self._update_labels()

    def _on_spin(self, _value: float):
        if self._quiet:
            return
        self._quiet = True
        try:
            start = int(round(self._start_spin.value() * 1000))
            end = int(round(self._end_spin.value() * 1000))
            moved_end = end != self._bar.end()
            self._bar.set_range(start, end, emit=False)
            # Write the clamped values back: typing a start past the end has to
            # settle somewhere, and the boxes must agree with the handles.
            self._start_spin.setValue(self._bar.start() / 1000.0)
            self._end_spin.setValue(self._bar.end() / 1000.0)
            self._bar.set_position(
                self._bar.end() if moved_end else self._bar.start(), emit=False)
        finally:
            self._quiet = False
        self._update_labels()
        self._render(self._bar.position())

    def _on_scrub(self, ms: int):
        self._update_labels()
        self._render(ms)

    def _update_labels(self):
        self._clock.setText(f"{fmt_clock(self._bar.position())}   /   "
                            f"{fmt_clock(self._dur)}")
        span = (self._bar.end() - self._bar.start()) / 1000.0
        self._length.setText(f"Selected {span:.2f} s of "
                             f"{self._dur / 1000.0:.2f} s")

    # -- frames --------------------------------------------------------
    def _start_extract(self):
        ffmpeg = find_ffmpeg()
        if ffmpeg is None:
            self._preview.set_text("Preview needs ffmpeg — trim by the numbers")
            return
        secs = self._dur / 1000.0
        # A long clip drops below the nominal rate rather than filling the temp
        # folder. Playback follows whatever rate we settle on, so the preview
        # still runs at the clip's real speed, just with coarser frames.
        if secs * self._fps > TRIM_MAX_FRAMES:
            self._fps = max(1.0, TRIM_MAX_FRAMES / secs)
        cmd = [ffmpeg, "-y", "-loglevel", "error", "-i", str(self._path),
               "-vf", f"fps={self._fps:g},scale=-2:'min({TRIM_FRAME_H},ih)'",
               "-q:v", "5", "-start_number", "0",
               str(self._tmp / "f_%05d.jpg")]
        try:
            self._proc = subprocess.Popen(
                cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                creationflags=CREATE_NO_WINDOW)
        except OSError as exc:
            log(f"trim: could not start ffmpeg: {exc}")
            self._preview.set_text("Preview unavailable")
            return
        self._scan_timer.start(120)

    def _scan_frames(self):
        """Pick frames up as ffmpeg writes them.

        While ffmpeg is still running the newest file is skipped: it is the one
        being written, and half a JPEG loads as nothing at all.
        """
        done = self._proc is None or self._proc.poll() is not None
        try:
            count = sum(1 for _ in self._tmp.glob("f_*.jpg"))
        except OSError:
            count = self._avail
        avail = count if done else max(0, count - 1)
        if avail != self._avail:
            self._avail = avail
            self._bar.update()
            self._render(self._bar.position())
        if done:
            self._scan_timer.stop()
            if self._avail == 0:
                self._preview.set_text("Preview unavailable")
            else:
                self._render(self._bar.position())

    def _frame_index(self, ms: float) -> int:
        return max(0, int(round(ms / 1000.0 * self._fps)))

    def _frame_file(self, idx: int) -> Path:
        return self._tmp / f"f_{idx:05d}.jpg"

    def _load_frame(self, ms: float) -> QPixmap | None:
        """The extracted frame nearest `ms`, or None while there is none yet.

        Bounded: a long clip is hundreds of frames and holding them all as
        pixmaps would cost far more than the JPEGs on disk. Re-reading one is
        a millisecond, which playback at these rates does not notice.
        """
        if self._avail <= 0:
            return None
        idx = min(self._frame_index(ms), self._avail - 1)
        pm = self._frames.get(idx)
        if pm is None:
            pm = QPixmap(str(self._frame_file(idx)))
            if pm.isNull():
                return None
            self._frames[idx] = pm
            self._frame_order.append(idx)
            while len(self._frame_order) > 24:
                self._frames.pop(self._frame_order.pop(0), None)
        return pm

    def _thumb_at(self, ms: float, height: int) -> QPixmap | None:
        """Filmstrip thumbnail. Cached per (frame, height) and kept — at strip
        height these are a couple of KB each and the bar repaints constantly."""
        if self._avail <= 0 or height <= 0:
            return None
        idx = min(self._frame_index(ms), self._avail - 1)
        key = (idx, height)
        pm = self._thumbs.get(key)
        if pm is None:
            src = QPixmap(str(self._frame_file(idx)))
            if src.isNull():
                return None
            pm = src.scaledToHeight(
                height, Qt.TransformationMode.SmoothTransformation)
            self._thumbs[key] = pm
        return pm

    def _render(self, ms: float):
        pm = self._load_frame(ms)
        if pm is not None:
            self._preview.set_frame(pm)

    # -- playback ------------------------------------------------------
    def is_playing(self) -> bool:
        return self._play_timer.isActive()

    def toggle_play(self):
        if self.is_playing():
            self._stop_play()
            return
        if self._bar.position() >= self._bar.end():
            self._bar.set_position(self._bar.start())
        self._play_btn.setText("Pause")
        self._play_timer.start(max(20, int(round(1000 / self._fps))))

    def _stop_play(self):
        self._play_timer.stop()
        self._play_btn.setText("Play")

    def _tick_play(self):
        step = int(round(1000 / self._fps))
        nxt = self._bar.position() + step
        if nxt >= self._bar.end():
            self._bar.set_position(self._bar.end())
            self._stop_play()
            return
        self._bar.set_position(nxt)

    # -- outcomes ------------------------------------------------------
    def _keep_trimmed(self):
        self.outcome = self.TRIMMED
        self.accept()

    def _keep_full(self):
        self.outcome = self.FULL
        self.accept()

    def _discard(self):
        # A take cannot be re-made, so deleting one is worth a question.
        if QMessageBox.question(
                self, "Discard recording",
                f"Delete {self._path.name} without saving?",
                QMessageBox.StandardButton.Discard
                | QMessageBox.StandardButton.Cancel,
                QMessageBox.StandardButton.Cancel
        ) != QMessageBox.StandardButton.Discard:
            return
        self.outcome = self.DISCARD
        self.accept()

    def done(self, result: int):
        """Every exit runs through here.

        accept() does not raise a closeEvent, so cleanup cannot live there —
        and leaving it undone would strand an ffmpeg process and a few hundred
        JPEGs in the temp folder on every recording.
        """
        self._stop_play()
        self._scan_timer.stop()
        if self._proc is not None and self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(timeout=3)
            except subprocess.TimeoutExpired:      # pragma: no cover
                self._proc.kill()
        self._proc = None
        self._frames.clear()
        self._frame_order.clear()
        self._thumbs.clear()
        shutil.rmtree(self._tmp, ignore_errors=True)
        super().done(result)


# ── Main window ───────────────────────────────────────────────────────

class RegionShot(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Region Shot")
        self.setWindowFlag(Qt.WindowType.WindowStaysOnTopHint, True)

        # {data_dir}\settings.json; main() has set _SETTINGS.
        self._settings = _SETTINGS
        self._overlay: RegionOverlay | None = None
        self._outline: RegionOutline | None = None
        self._shots = 0
        # set_region() saves, and _load() calls it before the other fields have
        # been read back — without this guard the load would persist the
        # constructor defaults over the user's folder/prefix/delay.
        self._loading = False

        # Hands the outline back to the Show toggle after a peek. Single-shot
        # and restarted on every nudge, so continuous adjusting keeps it up.
        self._peek_timer = QTimer(self)
        self._peek_timer.setSingleShot(True)
        self._peek_timer.timeout.connect(self._sync_outline)

        self._rec_proc: subprocess.Popen | None = None
        self._rec_stem = Path()
        self._rec_started = 0.0
        self._rec_timer = QTimer(self)
        self._rec_timer.timeout.connect(self._tick_record)

        self._build_ui()
        self._load()

    # -- UI ------------------------------------------------------------
    def _build_ui(self):
        outer = QVBoxLayout(self)
        outer.setSpacing(8)

        # ── Row 1: the actions ────────────────────────────────────────
        act_row = QHBoxLayout()
        act_row.setSpacing(8)
        select_btn = QPushButton("Select Region")
        select_btn.setStyleSheet(action_style("select"))
        select_btn.clicked.connect(self.select_region)
        self._capture_btn = QPushButton("Capture")
        self._capture_btn.setStyleSheet(action_style("capture"))
        # Not connected directly: clicked emits a `checked` bool, which would
        # arrive in capture()'s delay_override and zero the GUI delay.
        self._capture_btn.clicked.connect(lambda: self.capture())
        self._record_btn = QPushButton("Record")
        self._record_btn.setStyleSheet(action_style("record"))
        self._record_btn.clicked.connect(self.toggle_record)
        for btn in (select_btn, self._capture_btn, self._record_btn):
            btn.setMinimumSize(110, ACTION_H)
            btn.setMaximumHeight(ACTION_H)
            btn.setSizePolicy(QSizePolicy.Policy.Expanding,
                              QSizePolicy.Policy.Fixed)
            btn.setCursor(Qt.CursorShape.PointingHandCursor)
            act_row.addWidget(btn, stretch=1)
        self._show_btn = QPushButton("Show")
        self._show_btn.setCheckable(True)
        self._show_btn.setMinimumSize(64, ACTION_H)
        self._show_btn.setMaximumHeight(ACTION_H)
        self._show_btn.setSizePolicy(QSizePolicy.Policy.Fixed,
                                     QSizePolicy.Policy.Fixed)
        self._show_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self._show_btn.setToolTip(
            "Keep a dotted outline around the pinned region — stays up until "
            "you switch it off, so you can see the region while nudging it")
        self._show_btn.toggled.connect(lambda _on: self._sync_outline())
        act_row.addWidget(self._show_btn)
        outer.addLayout(act_row)

        # ── Row 2: the pinned region, and Delay beside it ─────────────
        # Centred as one block, so the matrix and Delay read as a single group.
        mid_row = QHBoxLayout()
        mid_row.setSpacing(18)
        mid_row.addStretch(1)

        # X/W over Y/H, so the pair that moves the region and the pair that
        # sizes it read as columns instead of one undifferentiated run.
        grid = QGridLayout()
        grid.setHorizontalSpacing(8)
        grid.setVerticalSpacing(4)
        self._spins: dict[str, QSpinBox] = {}
        # The ranges are the virtual desktop itself, not the desktop plus a
        # 10000px slop as they used to be. A region outside the desktop, or
        # bigger than it, grabs nothing useful — and the slop was doing real
        # damage to the layout, because fit_spin() sizes each box for the
        # widest value its range allows: "-10000" and "20000" bought two
        # digits' worth of width that could never hold a number.
        virt = QGuiApplication.primaryScreen().virtualGeometry()
        for row, (pos_key, pos_label, lo, hi), (size_key, size_label, size_hi) in (
            (0, ("x", "Region X:", virt.left(), virt.right()),
             ("w", "W", virt.width())),
            (1, ("y", "Region Y:", virt.top(), virt.bottom()),
             ("h", "H", virt.height())),
        ):
            for col, (key, label, rng) in enumerate((
                (pos_key, pos_label, (lo, hi)),
                (size_key, size_label, (1, size_hi)),
            )):
                spin = QSpinBox()
                spin.setRange(*rng)
                # _apply_lock() sizes these — the width depends on whether the
                # arrows are currently drawn, so it cannot be settled here.
                spin.setFixedWidth(fit_spin(spin, str(rng[0]), str(rng[1])))
                spin.valueChanged.connect(self._on_region_edited)
                self._spins[key] = spin
                grid.addWidget(QLabel(label), row, col * 2,
                               alignment=Qt.AlignmentFlag.AlignRight
                               | Qt.AlignmentFlag.AlignVCenter)
                grid.addWidget(spin, row, col * 2 + 1)
        mid_row.addLayout(grid)

        # The lock guards the matrix. Select Region is the normal way to set a
        # region; X/Y/W/H are for bumping it afterwards, and locking them by
        # default keeps a stray scroll or keystroke from moving a region that
        # was just framed by hand.
        self._lock_btn = QPushButton()
        self._lock_btn.setCheckable(True)
        self._lock_btn.setFixedSize(28, 28)
        self._lock_btn.setIconSize(QSize(16, 16))
        self._lock_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self._lock_btn.setStyleSheet(LOCK_BTN_STYLE)
        self._lock_btn.toggled.connect(self._apply_lock)
        mid_row.addWidget(self._lock_btn, alignment=Qt.AlignmentFlag.AlignVCenter)

        # Delay lives with the region rather than in Settings: it is part of
        # framing a shot — the wait that lets you click back into the target
        # app — not part of where files land or how clips are encoded.
        delay_box = QWidget()
        delay_lay = QHBoxLayout(delay_box)
        delay_lay.setContentsMargins(0, 0, 0, 0)
        delay_lay.setSpacing(6)
        delay_lay.addWidget(QLabel("Delay:"))
        self._delay = DelayBox()
        fit_combo(self._delay, *[fmt_secs(s) for s in DELAY_PRESETS],
                  fmt_secs(DELAY_MAX))
        self._delay.setToolTip("Wait before grabbing — use it to click back "
                               "into the target app, open a menu or hover a "
                               "tooltip first. Pick a preset or type any "
                               "number of seconds.")
        self._delay.valueChanged.connect(self._save)
        delay_lay.addWidget(self._delay)
        mid_row.addWidget(delay_box, alignment=Qt.AlignmentFlag.AlignVCenter)

        mid_row.addStretch(1)
        outer.addLayout(mid_row)
        outer.addStretch(1)

        line = QFrame()
        line.setFrameShape(QFrame.Shape.HLine)
        outer.addWidget(line)

        # Everything that only tunes the output now lives behind the gear.
        self._settings_dlg = self._build_settings()

        # ── Row 3: status, with the gear and ? beside it ──────────────
        status_row = QHBoxLayout()
        status_row.setSpacing(6)
        self._status = QLabel("Set a region to begin.")
        self._status.setWordWrap(True)
        status_row.addWidget(self._status, stretch=1)
        gear_btn = QPushButton()
        gear_btn.setFixedSize(24, 24)
        gear_btn.setIcon(gear_icon(14, self.palette().windowText().color()))
        gear_btn.setIconSize(QSize(14, 14))
        gear_btn.setToolTip("Settings — output folder, prefix, recording")
        gear_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        gear_btn.setStyleSheet(ROUND_BTN_STYLE)
        gear_btn.clicked.connect(self.show_settings)
        status_row.addWidget(gear_btn, alignment=Qt.AlignmentFlag.AlignBottom)
        help_btn = QPushButton("?")
        help_btn.setFixedSize(24, 24)
        help_btn.setToolTip("What each control does")
        help_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        help_btn.setStyleSheet(ROUND_BTN_STYLE)
        help_btn.clicked.connect(self.show_help)
        status_row.addWidget(help_btn, alignment=Qt.AlignmentFlag.AlignBottom)
        outer.addLayout(status_row)

        # Never smaller than the layout genuinely needs. Nothing in the window
        # wraps any more, so minimumSizeHint is honest about the height and
        # needs no second opinion.
        outer.activate()
        floor = self.minimumSizeHint()
        self.setMinimumSize(floor)
        # Open at 480 wide and at exactly the layout minimum height. Every
        # height above the minimum is slack, and slack is precisely the empty
        # band between the region row and the status bar — so the only opening
        # height with no band is the minimum itself. Dragging it taller still
        # works; the stretch above the status bar takes the extra.
        self.resize(max(floor.width(), 480), floor.height())

    # -- Settings ------------------------------------------------------
    def _build_settings(self) -> QDialog:
        """Everything that only tunes the output, behind the gear button.

        The controls are children of this dialog but are still owned and read
        by RegionShot — `_load`, `_save`, `capture` and the recorder reach them
        through the same attributes as when they sat in the window, so moving
        them here changed no behaviour. Each one still saves on change; the
        dialog itself holds no state and needs no OK/Cancel.
        """
        dlg = QDialog(self)
        dlg.setWindowTitle("Region Shot — Settings")
        dlg.setMinimumWidth(430)
        lay = QVBoxLayout(dlg)
        lay.setSpacing(8)

        right = Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        left = Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter

        # ── Output ────────────────────────────────────────────────────
        lay.addWidget(heading("Output"))
        out = QGridLayout()
        out.setHorizontalSpacing(8)
        out.setVerticalSpacing(6)
        out.setColumnStretch(1, 1)

        self._dir = QLineEdit()
        self._dir.editingFinished.connect(self._save)
        out.addWidget(QLabel("Save to:"), 0, 0, alignment=right)
        out.addWidget(self._dir, 0, 1)

        dir_btns = QHBoxLayout()
        browse = QPushButton("Browse…")
        browse.clicked.connect(self._browse)
        dir_btns.addWidget(browse)
        open_btn = QPushButton("Open")
        open_btn.clicked.connect(self._open_folder)
        dir_btns.addWidget(open_btn)
        dir_btns.addStretch(1)
        out.addLayout(dir_btns, 1, 1)

        # Widths here are floors, never ceilings. setFixedWidth() was fine in
        # the old cramped options row but clips the moment the text is wider
        # than the guess. Spin boxes go through fit_spin(), which measures the
        # widest value they can ever show instead of trusting sizeHint.
        self._prefix = QLineEdit("shot")
        self._prefix.setMinimumWidth(120)
        self._prefix.setToolTip("Files are numbered prefix-001, prefix-002… — "
                                "stills and clips share one sequence")
        self._prefix.editingFinished.connect(self._save)
        out.addWidget(QLabel("Prefix:"), 2, 0, alignment=right)
        out.addWidget(self._prefix, 2, 1, alignment=left)

        # Delay is deliberately NOT here — it lives beside the region matrix in
        # the main window, since it is part of framing a shot.
        self._clip = QCheckBox("Copy stills to the clipboard")
        self._clip.setToolTip("Copy each still to the clipboard as well as "
                              "saving it")
        self._clip.stateChanged.connect(self._save)
        out.addWidget(self._clip, 3, 1, alignment=left)
        lay.addLayout(out)

        # ── Recording ─────────────────────────────────────────────────
        lay.addSpacing(6)
        lay.addWidget(heading("Recording"))
        rec = QGridLayout()
        rec.setHorizontalSpacing(8)
        rec.setVerticalSpacing(6)
        rec.setColumnStretch(1, 1)

        self._rec_format = QComboBox()
        self._rec_format.addItems(RECORD_FORMATS)
        self._rec_format.setMinimumWidth(120)
        self._rec_format.currentIndexChanged.connect(lambda _i: self._save())
        rec.addWidget(QLabel("Record:"), 0, 0, alignment=right)
        rec.addWidget(self._rec_format, 0, 1, alignment=left)

        self._fps = QSpinBox()
        self._fps.setRange(1, 60)
        self._fps.setValue(12)
        fit_spin(self._fps, str(self._fps.maximum()))
        self._fps.valueChanged.connect(lambda _v: self._save())
        rec.addWidget(QLabel("FPS:"), 1, 0, alignment=right)
        rec.addWidget(self._fps, 1, 1, alignment=left)

        self._gif_width = QSpinBox()
        self._gif_width.setRange(0, 4000)
        self._gif_width.setValue(1000)
        self._gif_width.setSingleStep(50)
        self._gif_width.setSpecialValueText("full")
        fit_spin(self._gif_width, str(self._gif_width.maximum()),
                 self._gif_width.specialValueText())
        self._gif_width.setToolTip("Scale the GIF down to this width (0 = keep "
                                   "full size). GIFs get large fast.")
        self._gif_width.valueChanged.connect(lambda _v: self._save())
        rec.addWidget(QLabel("GIF width:"), 2, 0, alignment=right)
        rec.addWidget(self._gif_width, 2, 1, alignment=left)

        self._cursor = QCheckBox("Draw the cursor into recordings")
        self._cursor.setChecked(True)
        self._cursor.stateChanged.connect(lambda _s: self._save())
        rec.addWidget(self._cursor, 3, 1, alignment=left)

        # Off by default: with it on, every recording stops into a dialog, and
        # that is only worth it when clips actually need topping and tailing.
        self._trim = QCheckBox("Trim clips before saving")
        self._trim.setToolTip(
            "When a recording stops, open the trim editor to set its start "
            "and end. The clip is cut — and any GIF made from it — only after "
            "you save.")
        self._trim.stateChanged.connect(lambda _s: self._save())
        rec.addWidget(self._trim, 4, 1, alignment=left)
        lay.addLayout(rec)

        lay.addStretch(1)
        close_row = QHBoxLayout()
        close_row.addStretch(1)
        close_btn = QPushButton("Close")
        close_btn.setDefault(True)
        close_btn.clicked.connect(dlg.accept)
        close_row.addWidget(close_btn)
        lay.addLayout(close_row)
        return dlg

    def show_settings(self):
        # Modal on purpose: a settings window left floating over the desktop
        # would sit in front of the region and land in the next shot, and the
        # capture path only knows to duck the main window out of the way.
        self._settings_dlg.exec()

    # -- Region --------------------------------------------------------
    def _apply_lock(self, unlocked: bool):
        """Checked = manual override on = X/Y/W/H editable.

        Read-only rather than disabled: the numbers stay legible, and
        `set_region()` still writes them, so Select Region works either way —
        the lock is only ever about *typed* edits. The arrows are hidden too,
        since a read-only spin box that still shows steppers reads as broken.

        Each box is then re-measured for the state it is now in. Under the
        Windows 11 style the arrows are enormous — 48 of an 86px box at 9pt —
        so sizing both states for the unlocked one would leave the locked
        matrix, which is what you look at almost all the time, mostly air.
        The row is centred, so it simply re-centres; the reflow only ever
        happens on a deliberate click of the padlock, where visible feedback
        is the point.
        """
        for spin in self._spins.values():
            spin.setReadOnly(not unlocked)
            spin.setButtonSymbols(
                QAbstractSpinBox.ButtonSymbols.UpDownArrows if unlocked
                else QAbstractSpinBox.ButtonSymbols.NoButtons)
            spin.setFixedWidth(
                fit_spin(spin, str(spin.minimum()), str(spin.maximum())))
        self._lock_btn.setIcon(lock_icon(
            16, self.palette().windowText().color(), locked=not unlocked))
        self._lock_btn.setToolTip(
            "Manual override on — X/Y/W/H can be edited. Click to lock them."
            if unlocked else
            "X/Y/W/H are locked. Click for manual override — Select Region "
            "still works either way.")
        self._save()

    def region(self) -> QRect:
        return QRect(self._spins["x"].value(), self._spins["y"].value(),
                     self._spins["w"].value(), self._spins["h"].value())

    def set_region(self, rect: QRect):
        for key, val in (("x", rect.x()), ("y", rect.y()),
                         ("w", rect.width()), ("h", rect.height())):
            spin = self._spins[key]
            spin.blockSignals(True)
            spin.setValue(val)
            spin.blockSignals(False)
        self._save()
        self._sync_outline()

    def _on_region_edited(self, _value: int):
        self._save()
        # A live recording owns the outline and paints it red; show_outline()
        # here would repaint it orange mid-take. _sync_outline() defers to a
        # recording, but this path does not go through it.
        if self.is_recording():
            return
        # With Show on the outline is already up and just tracks the values.
        # With Show off it is borrowed for a moment so the new framing is
        # visible, then handed back — through _sync_outline(), which stays the
        # only thing that decides whether the outline belongs on screen.
        self.show_outline(OUTLINE_SHOT)
        if not self._show_btn.isChecked():
            self._peek_timer.start(PEEK_MS)

    def select_region(self):
        if self._overlay is not None:
            return

        def done(rect: QRect | None):
            self._overlay = None
            self.show()
            self.raise_()
            if rect is not None:
                self.set_region(rect)
                self._status.setText(
                    f"Region set to {rect.width()}x{rect.height()} at "
                    f"({rect.x()}, {rect.y()}).")
            else:
                self._status.setText("Region unchanged.")

        self.hide()          # keep this window out of the picker
        _pump(HIDE_SETTLE_MS)
        self._overlay = RegionOverlay(self.region(), done)
        self._overlay.show()
        self._overlay.activateWindow()
        self._overlay.setFocus()

    # -- Help ----------------------------------------------------------
    def show_help(self):
        """Short reference: the controls and the few gotchas.

        Kept deliberately terse — anything that reads like prose here is
        something the UI should have said on its own.
        """
        html = f"""
<h3 style="margin-top:0">Region Shot</h3>
<p>Pin one screen area — then shoot or record exactly it, over and over.</p>

<h4>Controls</h4>
<table cellpadding="4">
<tr><td><b>Select Region</b></td><td>Drag out the area — Enter keeps, Esc cancels</td></tr>
<tr><td><b>Region X / Y</b></td><td>Move it to the pixel — <b>W / H</b> beside them size it.
The outline flashes up as you nudge, so you can see where it landed</td></tr>
<tr><td><b>Padlock</b></td><td>Manual override. X/Y/W/H are locked until you open it —
<b>Select Region</b> works either way</td></tr>
<tr><td><b>Show</b></td><td>Outline on / off — stays up while you nudge</td></tr>
<tr><td><b>Delay</b></td><td>Wait before grabbing — time to click back into the
target app. Pick a preset or type any number of seconds</td></tr>
<tr><td><b>&#9881; Settings</b></td><td>Folder, prefix, and the recording options</td></tr>
</table>

<h4>Settings</h4>
<table cellpadding="4">
<tr><td><b>Save to / Prefix</b></td><td>Where files land and what they are called</td></tr>
<tr><td><b>Record</b></td><td>MP4, GIF, or both — GIFs get large fast</td></tr>
<tr><td><b>FPS / GIF width</b></td><td>Recording rate, and how far the GIF is scaled down</td></tr>
<tr><td><b>Cursor</b></td><td>Draw the pointer into recordings</td></tr>
<tr><td><b>Trim clips</b></td><td>Stop a recording into a trim editor — drag the
handles to set the start and end, then <b>Save Trimmed</b>. Space plays the
selection; <b>Discard</b> throws the take away</td></tr>
</table>

<h4>Good to know</h4>
<ul>
<li>Clicking <b>Capture</b> focuses this window, which dims the target app's
title bar and drops its focus ring. Give yourself a <b>Delay</b> and click back
into the target first.</li>
<li>The dotted outline sits <i>outside</i> the region, never in the output —
<span style="color:#ffb020">orange</span> for the region,
<span style="color:#e83a30">red</span> while recording.</li>
<li>Files are numbered <tt>prefix-001</tt>, <tt>prefix-002</tt>… — stills and
clips share one sequence.</li>
<li>Log: <tt>{LOG_PATH.name}</tt> in <tt>{LOG_PATH.parent}</tt></li>
</ul>
"""
        box = QMessageBox(self)
        box.setWindowTitle("Region Shot — Help")
        box.setTextFormat(Qt.TextFormat.RichText)
        # QMessageBox sizes to its text and defaults to a tall narrow column;
        # widening the label lets the tables breathe and roughly halves the
        # height. Styling the label is the only hook it exposes for this.
        box.setStyleSheet("QLabel { min-width: 520px; }")
        box.setText(html)
        box.setIcon(QMessageBox.Icon.NoIcon)
        box.setStandardButtons(QMessageBox.StandardButton.Close)
        box.exec()

    # -- Outline -------------------------------------------------------
    def show_outline(self, rgb: tuple[int, int, int]):
        """Put the dotted outline up around the pinned region.

        Reuses the window it already has when only the geometry changed, so
        live-tracking the spin boxes does not strobe a top-level window in and
        out on every step. A colour change (orange ↔ red) still rebuilds.
        """
        rect = self.region()
        if rect.width() < 1 or rect.height() < 1:
            self.hide_outline()
            return
        color = QColor(*rgb)
        if self._outline is not None and self._outline.color() != color:
            self.hide_outline()
        if self._outline is None:
            self._outline = RegionOutline(rect, color)
        else:
            self._outline.set_region(rect)
        self._outline.show()
        self._outline.raise_()

    def hide_outline(self):
        if self._outline is not None:
            self._outline.close()
            self._outline = None

    def _sync_outline(self):
        """Put the outline back where the Show toggle says it belongs.

        Called whenever something transient finishes — a capture, a recording,
        a region edit. A live recording owns the outline (red) and outranks the
        toggle, so leave it alone until the recording ends.
        """
        if self.is_recording():
            return
        if self._show_btn.isChecked():
            self.show_outline(OUTLINE_SHOT)
        else:
            self.hide_outline()

    # -- Capture -------------------------------------------------------
    def capture(self, delay_override: float | None = None) -> dict:
        """Grab the pinned region. Returns a result dict for the API; the
        Capture button ignores it (and connects via a lambda, since clicked's
        `checked` bool would otherwise land in `delay_override`).

        `delay_override` is the API's zero-by-default delay — an HTTP call
        steals no focus, so the wait the GUI Delay box exists for isn't needed.
        None means "use the Delay box", as a button click always has.
        """
        rect = self.region()
        if rect.width() < 1 or rect.height() < 1:
            self._status.setText("No region set — click Select…")
            return {"ok": False, "error": "no region set"}

        # Orange outline up for the whole action — it sits outside the region,
        # so it is visible on screen but absent from the shot.
        self.show_outline(OUTLINE_SHOT)
        _pump(90)

        delay = (self._delay.value() if delay_override is None
                 else max(0.0, min(DELAY_MAX, float(delay_override))))
        delay_ms = int(delay * 1000)
        if delay_ms:
            self._status.setText(f"Capturing in {delay:g}s…")
            _pump(delay_ms)

        # Only duck out of the way if we'd actually be in the shot; hiding
        # needlessly costs a repaint and steals focus back from the target.
        # A minimized window can't be in the shot, so it never gets cycled.
        overlaps = (self.isVisible() and not self.isMinimized()
                    and self.frameGeometry().intersects(rect))
        if overlaps:
            self.hide()
            _pump(HIDE_SETTLE_MS)

        img = grab_region(rect)

        if overlaps:
            self.show()
        # Linger briefly so the outline reads as "that just fired", then hand
        # it back to the Show toggle.
        QTimer.singleShot(450, self._sync_outline)

        if img is None or img.isNull():
            self._status.setText("Capture failed — is the region on screen?")
            return {"ok": False, "error": "capture failed — is the region on screen?"}

        path = self._next_path()
        if not img.save(str(path), "PNG"):
            self._status.setText(f"Could not write {path}")
            return {"ok": False, "error": f"could not write {path}"}

        if self._clip.isChecked():
            QGuiApplication.clipboard().setImage(img)

        self._shots += 1
        log(f"captured {path.name}")
        self._status.setText(
            f"Saved {path.name}  ({img.width()}x{img.height()})   ·   "
            f"{self._shots} this session   ·   {path.parent}")
        return {"ok": True, "path": str(path),
                "width": img.width(), "height": img.height()}

    def _next_path(self, ext: str = "png") -> Path:
        """Next free `prefix-NNN.ext`. Numbering is shared across stills and
        recordings so a shot and a clip never collide on the same index."""
        folder = Path(self._dir.text().strip() or SETTINGS_DEFAULTS["dir"])
        folder.mkdir(parents=True, exist_ok=True)
        prefix = self._prefix.text().strip() or "shot"
        used = []
        for p in folder.glob(f"{prefix}-*"):
            tail = p.stem[len(prefix) + 1:]
            if tail.isdigit():
                used.append(int(tail))
        return folder / f"{prefix}-{(max(used) + 1) if used else 1:03d}.{ext}"

    # -- Recording -----------------------------------------------------
    def is_recording(self) -> bool:
        return self._rec_proc is not None and self._rec_proc.poll() is None

    def toggle_record(self):
        if self.is_recording():
            self.stop_record()
        else:
            self.start_record()

    def start_record(self) -> dict:
        if self.is_recording():
            return {"ok": False, "error": "already recording"}
        ffmpeg = find_ffmpeg()
        if ffmpeg is None:
            self._status.setText(FFMPEG_MISSING)
            return {"ok": False, "error": FFMPEG_MISSING}
        rect = self.region()
        if rect.width() < 2 or rect.height() < 2:
            self._status.setText("No region set — click Select…")
            return {"ok": False, "error": "no region set"}

        # H.264 needs even dimensions; trim rather than pad so the framing
        # still matches the stills to within a pixel.
        w = rect.width() - (rect.width() % 2)
        h = rect.height() - (rect.height() % 2)

        self._rec_stem = self._next_path("mp4").with_suffix("")
        target = self._rec_stem.with_suffix(".mp4")
        fps = self._fps.value()
        cmd = [
            ffmpeg, "-y", "-loglevel", "error",
            "-f", "gdigrab",
            "-framerate", str(fps),
            "-draw_mouse", "1" if self._cursor.isChecked() else "0",
            "-offset_x", str(rect.x()), "-offset_y", str(rect.y()),
            "-video_size", f"{w}x{h}",
            "-i", "desktop",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-pix_fmt", "yuv420p",
            str(target),
        ]
        try:
            self._rec_proc = subprocess.Popen(
                cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE, creationflags=CREATE_NO_WINDOW)
        except OSError as exc:
            self._status.setText(f"Could not start ffmpeg: {exc}")
            return {"ok": False, "error": f"could not start ffmpeg: {exc}"}

        self._rec_started = time.monotonic()
        self._rec_timer.start(250)
        self.show_outline(OUTLINE_REC)   # stays up for the whole recording
        self._record_btn.setText("Stop")
        self._status.setText(f"Recording {w}x{h} @ {fps} fps → {target.name}")
        return {"ok": True, "file": target.name}

    def _tick_record(self):
        if not self.is_recording():
            # ffmpeg fell over on its own (bad region, disk full, ...).
            self._rec_timer.stop()
            self._finish_record(crashed=True)
            return
        secs = time.monotonic() - self._rec_started
        self._record_btn.setText(f"Stop   ({secs:0.1f}s)")

    def stop_record(self, allow_trim: bool = True) -> dict:
        """`allow_trim=False` is the API's default: the caller may not be at
        the keyboard, and a surprise modal would hang their request on a
        dialog nobody is looking at."""
        if not self.is_recording():
            return {"ok": False, "error": "not recording"}
        self._rec_timer.stop()
        self._status.setText("Finishing recording…")
        # 'q' on stdin makes ffmpeg finalise the container. terminate() would
        # leave an MP4 with no moov atom, i.e. an unplayable file.
        try:
            self._rec_proc.stdin.write(b"q")
            self._rec_proc.stdin.flush()
        except OSError:
            pass
        for _ in range(80):          # up to ~8s for the muxer to close out
            if self._rec_proc.poll() is not None:
                break
            _pump(100)
        if self._rec_proc.poll() is None:
            self._rec_proc.terminate()
            _pump(500)
        return self._finish_record(allow_trim=allow_trim)

    def _finish_record(self, crashed: bool = False,
                       allow_trim: bool = True) -> dict:
        proc, self._rec_proc = self._rec_proc, None
        # Clear _rec_proc first: _sync_outline defers to a live recording, and
        # this one is over.
        self._sync_outline()
        self._record_btn.setText("Record")
        mp4 = self._rec_stem.with_suffix(".mp4")
        if crashed:
            err = ""
            if proc is not None and proc.stderr is not None:
                err = proc.stderr.read().decode("utf-8", "replace").strip()[:200]
            self._status.setText(f"Recording stopped unexpectedly. {err}")
            return {"ok": False, "error": f"recording stopped unexpectedly: {err}"}
        if not mp4.is_file() or mp4.stat().st_size == 0:
            self._status.setText("Recording produced no file.")
            return {"ok": False, "error": "recording produced no file"}

        # The trim editor comes first, before any of the export below: a GIF
        # has to be made from what the user kept, not made and then contradicted
        # by a trim applied afterwards.
        note = ""
        if allow_trim and self._trim.isChecked():
            outcome = self._trim_clip(mp4)
            if outcome == TrimDialog.DISCARD:
                mp4.unlink(missing_ok=True)
                log(f"discarded {mp4.name}")
                self._status.setText(f"Discarded {mp4.name}.")
                return {"ok": True, "files": []}
            if outcome == TrimDialog.TRIMMED:
                note = "   ·   trimmed"

        want = self._rec_format.currentText()
        made = []
        if "MP4" in want:
            made.append(mp4.name)
        if "GIF" in want:
            gif = self._encode_gif(mp4)
            if gif is not None:
                made.append(gif.name)
        if want == "GIF":
            mp4.unlink(missing_ok=True)   # MP4 was only an intermediate

        self._shots += 1
        self._status.setText(
            f"Saved {', '.join(made)}{note}   ·   {self._rec_stem.parent}")
        return {"ok": True, "files": made}

    # -- Trim ----------------------------------------------------------
    def _trim_clip(self, mp4: Path) -> str:
        """Show the trim editor for `mp4`, and cut it in place if asked to.

        Returns what actually happened, which is not always what was clicked:
        Save Trimmed on an untouched selection, or a cut that fails, both come
        back as FULL, since in both cases the file on disk is the whole take.
        """
        secs = probe_duration(mp4)
        if secs is None:
            # No ffprobe, or it could not read the file. The wall clock since
            # the take started is close enough to hang handles on — and the cut
            # itself is exact either way, since ffmpeg is given timestamps.
            secs = max(0.1, time.monotonic() - self._rec_started)
            log(f"trim: no probed duration for {mp4.name}, using {secs:.2f}s")
        self._status.setText(f"Trim {mp4.name} — set the start and end.")
        dlg = TrimDialog(mp4, int(round(secs * 1000)), self)
        dlg.exec()
        if dlg.outcome == TrimDialog.DISCARD:
            return TrimDialog.DISCARD
        # Save Trimmed on an untouched selection is a save, not a cut: there is
        # nothing to remove, and re-encoding would cost quality for nothing.
        if dlg.outcome == TrimDialog.FULL or dlg.is_full():
            return TrimDialog.FULL
        if not self._apply_trim(mp4, dlg.start_ms(), dlg.end_ms()):
            self._status.setText("Trim failed — kept the full clip.")
            return TrimDialog.FULL
        return TrimDialog.TRIMMED

    def _apply_trim(self, mp4: Path, start_ms: int, end_ms: int) -> bool:
        """Cut [start, end] out of `mp4`, in place.

        Re-encodes rather than stream-copying. `-c copy` can only cut on a
        keyframe, so it would slide the start back by up to a second — the one
        thing a trim editor must not do. These are short screen clips, so the
        encode costs a moment and buys an exact cut.

        `-ss` before `-i` seeks the input; `-t` after it is an output duration,
        which sidesteps the question of whether `-to` is absolute or relative.

        Writes beside the target and replaces it only on success, so a failed
        cut leaves the recording intact instead of half-written.
        """
        ffmpeg = find_ffmpeg()
        if ffmpeg is None:
            return False
        span = max(0.05, (end_ms - start_ms) / 1000.0)
        tmp = mp4.with_name(mp4.stem + ".trimming.mp4")
        cmd = [ffmpeg, "-y", "-loglevel", "error",
               "-ss", f"{start_ms / 1000.0:.3f}", "-i", str(mp4),
               "-t", f"{span:.3f}",
               "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
               "-pix_fmt", "yuv420p", str(tmp)]
        self._status.setText("Trimming…")
        try:
            proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL,
                                    stderr=subprocess.PIPE,
                                    creationflags=CREATE_NO_WINDOW)
        except OSError as exc:
            log(f"trim: could not start ffmpeg: {exc}")
            return False
        while proc.poll() is None:      # keep the UI alive during the encode
            _pump(120)
        if proc.returncode != 0 or not tmp.is_file() or not tmp.stat().st_size:
            err = proc.stderr.read().decode("utf-8", "replace").strip()[:200] \
                if proc.stderr is not None else ""
            log(f"trim failed rc={proc.returncode} {err}")
            tmp.unlink(missing_ok=True)
            return False
        os.replace(tmp, mp4)
        log(f"trimmed {mp4.name}: {span:.2f}s from {start_ms / 1000.0:.2f}s")
        return True

    def _encode_gif(self, mp4: Path) -> Path | None:
        """Two-pass palette transcode — a single-pass GIF looks dithered and
        banded, which defeats the point for UI captures."""
        ffmpeg = find_ffmpeg()
        if ffmpeg is None:
            return None
        gif = mp4.with_suffix(".gif")
        width = self._gif_width.value()
        scale = f"scale={width}:-2:flags=lanczos," if width > 0 else ""
        vf = (f"{scale}split[a][b];[a]palettegen=max_colors=160:"
              f"stats_mode=diff[p];[b][p]paletteuse=dither=bayer:"
              f"bayer_scale=4:diff_mode=rectangle")
        self._status.setText("Encoding GIF…")
        try:
            proc = subprocess.Popen(
                [ffmpeg, "-y", "-loglevel", "error", "-i", str(mp4),
                 "-vf", vf, "-loop", "0", str(gif)],
                stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                creationflags=CREATE_NO_WINDOW)
        except OSError:
            return None
        while proc.poll() is None:      # keep the UI alive during the encode
            _pump(120)
        return gif if gif.is_file() and gif.stat().st_size else None

    # -- Folder --------------------------------------------------------
    def _browse(self):
        chosen = QFileDialog.getExistingDirectory(
            self, "Save screenshots to", self._dir.text())
        if chosen:
            self._dir.setText(chosen)
            self._save()

    def _open_folder(self):
        folder = Path(self._dir.text().strip())
        folder.mkdir(parents=True, exist_ok=True)
        subprocess.Popen(["explorer", str(folder)])

    # -- Shutdown ------------------------------------------------------
    def closeEvent(self, event):
        log("closeEvent - shutting down")
        # Finalise an in-flight recording, otherwise the MP4 has no moov atom
        # and won't play.
        if self.is_recording():
            self.stop_record()
        self._peek_timer.stop()
        self.hide_outline()
        self._save()
        super().closeEvent(event)

    # -- Persistence ---------------------------------------------------
    def _load(self):
        s = self._settings
        self._loading = True
        try:
            rect = QRect(int(s.value("x", 100)), int(s.value("y", 100)),
                         int(s.value("w", 800)), int(s.value("h", 600)))
            self.set_region(rect)
            self._dir.setText(
                str(s.value("dir", SETTINGS_DEFAULTS["dir"])))
            self._prefix.setText(str(s.value("prefix", "shot")))
            self._delay.setValue(float(s.value("delay", 0.0)))
            self._clip.setChecked(str(s.value("clip", "false")).lower() == "true")
            fmt = str(s.value("rec_format", RECORD_FORMATS[0]))
            self._rec_format.setCurrentIndex(
                RECORD_FORMATS.index(fmt) if fmt in RECORD_FORMATS else 0)
            self._fps.setValue(int(s.value("fps", 12)))
            self._gif_width.setValue(int(s.value("gif_width", 1000)))
            self._cursor.setChecked(str(s.value("cursor", "true")).lower() == "true")
            self._trim.setChecked(str(s.value("trim", "false")).lower() == "true")
            # Locked unless the user last left it unlocked. _apply_lock() runs
            # either way, since it is what puts the icon on the button.
            unlocked = str(s.value("unlocked", "false")).lower() == "true"
            self._lock_btn.setChecked(unlocked)
            self._apply_lock(unlocked)
        finally:
            self._loading = False
        self._status.setText(
            f"Region {rect.width()}x{rect.height()} at ({rect.x()}, {rect.y()}).")

    def _save(self):
        if self._loading:
            return
        s = self._settings
        rect = self.region()
        s.setValue("x", rect.x())
        s.setValue("y", rect.y())
        s.setValue("w", rect.width())
        s.setValue("h", rect.height())
        s.setValue("dir", self._dir.text())
        s.setValue("prefix", self._prefix.text())
        s.setValue("delay", self._delay.value())
        s.setValue("clip", "true" if self._clip.isChecked() else "false")
        s.setValue("rec_format", self._rec_format.currentText())
        s.setValue("fps", self._fps.value())
        s.setValue("gif_width", self._gif_width.value())
        s.setValue("cursor", "true" if self._cursor.isChecked() else "false")
        s.setValue("trim", "true" if self._trim.isChecked() else "false")
        s.setValue("unlocked", "true" if self._lock_btn.isChecked() else "false")


# ── Local HTTP API ────────────────────────────────────────────────────
# External tools trigger the same actions the buttons do, over localhost HTTP.
# The server lives in a daemon thread and never touches Qt: every request is
# marshalled onto the main thread through ApiBridge's signal (a cross-thread
# emit is automatically a queued connection) and the handler blocks on a queue
# for the result. Only plain dicts cross the thread boundary.


class ApiBridge(QObject):
    """Runs API actions on the Qt main thread and hands the result back."""

    _submitted = Signal(object)          # (action, params, reply_queue)

    def __init__(self, win: RegionShot):
        super().__init__(win)            # main-thread affinity
        self._win = win
        self._submitted.connect(self._run)

    def call(self, action: str, params: dict, timeout: float) -> dict:
        """Called from the HTTP thread. Blocks until the main thread answers."""
        reply: queue.Queue = queue.Queue(1)
        self._submitted.emit((action, params, reply))
        try:
            return reply.get(timeout=timeout)
        except queue.Empty:
            return {"ok": False, "error": "timed out waiting for the app"}

    def _run(self, job):                 # main thread
        action, params, reply = job
        try:
            result = self._dispatch(action, params)
        except Exception as exc:         # never leave the HTTP thread hanging
            log(f"api: {action} raised {exc!r}")
            result = {"ok": False, "error": str(exc)}
        reply.put(result)

    def _region_dict(self) -> dict:
        rect = self._win.region()
        return {"x": rect.x(), "y": rect.y(),
                "w": rect.width(), "h": rect.height()}

    def _dispatch(self, action: str, params: dict) -> dict:
        win = self._win
        if action == "status":
            return {"ok": True, "region": self._region_dict(),
                    "recording": win.is_recording(),
                    "shots_this_session": win._shots}
        if action == "capture":
            # Zero by default, whatever the GUI Delay box says: an HTTP call
            # steals no focus, so the delay's whole reason isn't there.
            return win.capture(delay_override=float(params.get("delay", 0.0)))
        if action == "record_start":
            return win.start_record()
        if action == "record_stop":
            return win.stop_record(allow_trim=bool(params.get("trim", False)))
        if action == "region_get":
            return {"ok": True, **self._region_dict()}
        if action == "region_set":
            cur = win.region()
            rect = QRect(int(params.get("x", cur.x())),
                         int(params.get("y", cur.y())),
                         int(params.get("w", cur.width())),
                         int(params.get("h", cur.height())))
            win.set_region(rect)         # spin-box ranges clamp; outline tracks
            return {"ok": True, **self._region_dict()}
        if action == "select":
            if win._overlay is not None:
                return {"ok": True, "note": "picker already open"}
            win.select_region()          # returns once the overlay is up
            return {"ok": True, "note": "picker opened"}
        return {"ok": False, "error": f"unknown action {action}"}


def start_api_server(win: RegionShot) -> None:
    """Bind the API on localhost and serve it from a daemon thread.

    A taken port logs and returns — the API is a convenience, and it must
    never keep the GUI from starting.
    """
    s = win._settings
    try:
        port = int(s.value("api_port", DEFAULT_API_PORT))
    except (TypeError, ValueError):
        port = DEFAULT_API_PORT
    bridge = ApiBridge(win)

    # (method, path) -> (action, timeout-in-seconds builder). record_stop is
    # generous: ffmpeg finalise + optional GIF encode + optional trim dialog.
    def _capture_timeout(params: dict) -> float:
        try:
            return max(0.0, min(DELAY_MAX, float(params.get("delay", 0.0)))) + 60
        except (TypeError, ValueError):
            return 60
    routes = {
        ("GET", "/status"): ("status", lambda p: 30),
        ("POST", "/capture"): ("capture", _capture_timeout),
        ("POST", "/record/start"): ("record_start", lambda p: 30),
        ("POST", "/record/stop"): ("record_stop", lambda p: 600),
        ("GET", "/region"): ("region_get", lambda p: 30),
        ("POST", "/region"): ("region_set", lambda p: 30),
        ("POST", "/select"): ("select", lambda p: 30),
    }
    known_paths = {path for _m, path in routes}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):   # keep http.server off stderr
            pass

        def _reply(self, code: int, payload: dict):
            body = json.dumps(payload).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _body(self) -> dict | None:
            """The JSON body as a dict; {} when empty, None when invalid."""
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                return None
            if length <= 0:
                return {}
            try:
                data = json.loads(self.rfile.read(length) or b"{}")
            except (OSError, ValueError):
                return None
            return data if isinstance(data, dict) else None

        def _handle(self, method: str):
            path = self.path.split("?", 1)[0].rstrip("/") or "/"
            route = routes.get((method, path))
            if route is None:
                if path in known_paths:
                    self._reply(405, {"ok": False,
                                      "error": f"{path} does not take {method}"})
                    log(f"api {method} {path} -> 405")
                else:
                    self._reply(404, {"ok": False,
                                      "error": f"unknown endpoint {path}"})
                    log(f"api {method} {path} -> 404")
                return
            params = self._body()
            if params is None:
                self._reply(400, {"ok": False, "error": "invalid JSON body"})
                log(f"api {method} {path} -> 400 bad body")
                return
            action, timeout = route
            result = bridge.call(action, params, timeout(params))
            # Application failures are HTTP 200 with ok:false — the transport
            # worked, the action didn't.
            self._reply(200, result)
            outcome = "ok" if result.get("ok") else f"error: {result.get('error')}"
            log(f"api {method} {path} -> {outcome}")

        def do_GET(self):
            self._handle("GET")

        def do_POST(self):
            self._handle("POST")

    try:
        server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    except OSError as exc:
        log(f"api: port {port} unavailable ({exc}) — API disabled")
        return
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever,
                     name="regionshot-api", daemon=True).start()
    log(f"api: listening on 127.0.0.1:{port}")


MUTEX_NAME = "Local\\DeskKit.regionshot"
SHOW_EVENT = "Local\\DeskKit.regionshot.show"
STOP_EVENT = "Local\\DeskKit.regionshot.stop"


def _k32():
    from ctypes import wintypes
    k = ctypes.windll.kernel32
    k.CreateMutexW.restype = wintypes.HANDLE
    k.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
    k.CreateEventW.restype = wintypes.HANDLE
    k.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    k.OpenEventW.restype = wintypes.HANDLE
    k.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
    k.SetEvent.argtypes = [wintypes.HANDLE]
    k.WaitForMultipleObjects.argtypes = [
        wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE), wintypes.BOOL, wintypes.DWORD]
    return k


def _claim_single_instance() -> bool:
    """Contract 7.2. False when another copy runs; that copy is told to show
    its window. Two copies would share one settings file and output folder."""
    k = _k32()
    handle = k.CreateMutexW(None, False, MUTEX_NAME)
    if not handle:
        return True     # cannot tell - do not block the user
    if ctypes.windll.kernel32.GetLastError() == 183:   # ERROR_ALREADY_EXISTS
        ev = k.OpenEventW(0x0002, False, SHOW_EVENT)   # EVENT_MODIFY_STATE
        if ev:
            k.SetEvent(ev)
        return False
    globals()["_INSTANCE_MUTEX"] = handle    # hold it for the process lifetime
    return True


class _InstanceSignals(QObject):
    show_requested = Signal()
    stop_requested = Signal()


def _watch_instance_events(signals: _InstanceSignals) -> None:
    """Background thread: turn the show and stop events into Qt signals."""
    from ctypes import wintypes
    k = _k32()
    show = k.CreateEventW(None, False, False, SHOW_EVENT)
    stop = k.CreateEventW(None, False, False, STOP_EVENT)
    if not show or not stop:
        log("could not create the show/stop events")
        return
    handles = (wintypes.HANDLE * 2)(show, stop)

    def loop():
        while True:
            r = k.WaitForMultipleObjects(2, handles, False, 0xFFFFFFFF)
            if r == 0:
                signals.show_requested.emit()
            elif r == 1:
                signals.stop_requested.emit()
                return
            else:
                return
    threading.Thread(target=loop, name="regionshot-events", daemon=True).start()


def main() -> int:
    global _SETTINGS
    log("=" * 60)
    log(f"start pid={os.getpid()} exe={sys.executable}")
    if any(a.startswith("-") and a != "--minimized" for a in sys.argv[1:]):
        fail(EXIT_USAGE, "Usage: region_shot.py [--minimized]")
    app = QApplication(sys.argv)
    if not _claim_single_instance():
        log("another instance already running - asked it to show, exiting")
        return 0
    settings_path = data_dir() / "settings.json"
    _migrate_registry_settings(settings_path)
    _SETTINGS = JsonSettings(settings_path)
    win = RegionShot()
    signals = _InstanceSignals()

    def _show():
        win.showNormal()
        win.raise_()
        win.activateWindow()

    def _stop():
        log("stop event - closing")
        t = threading.Timer(5.0, lambda: os._exit(0))   # failsafe, contract 7.2
        t.daemon = True   # a non-daemon timer would hold the process open for the full 5 s
        t.start()
        win.close()
        app.quit()

    signals.show_requested.connect(_show)
    signals.stop_requested.connect(_stop)
    win._instance_signals = signals
    _watch_instance_events(signals)
    if "--minimized" in sys.argv[1:]:
        win.showMinimized()
    else:
        win.show()
    start_api_server(win)
    log("window shown - entering event loop")
    rc = app.exec()
    log(f"event loop exited rc={rc}")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
