r"""Cable Listen: a small borderless square that toggles 'Listen to this device'
on a recording device and picks the output it plays through. Closing it (the x
in the corner) turns listening off. Drag it by the top strip.

A playback device (e.g. CABLE Input) can be picked as the source too: Windows
only listens to recording devices, so it listens to the recording side of the
same driver (CABLE Output).

Drives NirSoft SoundVolumeView (SVV below). Reads the real listen state from
the MMDevices registry, since SoundVolumeView has no way to report it.

DeskKit module (docs/module-contract.md). Settings: %APPDATA%\DeskKit\cablelisten\settings.json
  source   : the device picked last (a Command-Line Friendly ID)
  svv_path : full path to SoundVolumeView.exe, or "" to search for it
Exit codes: 0 ok, 3 SoundVolumeView missing, 4 Python too old, 5 no audio device, 1 other error.
"""
import sys

MIN = (3, 12)
NAME = "Cable Listen"
MODULE_ID = "cablelisten"
EXIT_ERROR, EXIT_HELPER, EXIT_RUNTIME, EXIT_DEVICE = 1, 3, 4, 5


def fail(code, message):
    """One ERROR line on stderr; a message box when no shell is there to show it (contract 9.2)."""
    import os
    print(f"ERROR: {message}", file=sys.stderr)
    if not os.environ.get("DESKKIT_SHELL"):
        try:
            import ctypes
            ctypes.windll.user32.MessageBoxW(None, message, NAME, 0x10)
        except Exception:
            pass
    sys.exit(code)


if sys.version_info < MIN:
    fail(EXIT_RUNTIME, f"Python {MIN[0]}.{MIN[1]} or newer not found. "
                       "Install it from https://www.python.org/downloads/windows/ and try again.")

import csv  # noqa: E402  (after the version check on purpose)
import ctypes  # noqa: E402
import json  # noqa: E402
import math  # noqa: E402
import os  # noqa: E402
import re  # noqa: E402
import shutil  # noqa: E402
import subprocess  # noqa: E402
import tempfile  # noqa: E402
import threading  # noqa: E402
import tkinter as tk  # noqa: E402
import winreg  # noqa: E402
from tkinter import ttk  # noqa: E402

DATA_DIR = os.environ.get("DESKKIT_DATA_DIR") or os.path.join(
    os.environ.get("APPDATA", ""), "DeskKit", MODULE_ID)
CONFIG = os.path.join(DATA_DIR, "settings.json")
LEGACY_CONFIG = "cable_listen.json"
SVV_HELP = ("SoundVolumeView (NirSoft freeware) turns 'Listen to this device' on and off. "
            "Download it from https://www.nirsoft.net/utils/sound_volume_view.html and unzip it into "
            "%LOCALAPPDATA%\\DeskKit\\helpers\\soundvolumeview, or put it on PATH, "
            "or set svv_path in " + CONFIG + ".")
CAPTURE_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture"
LISTEN_PROP = "{24dbb0fc-9311-4b3d-9cf0-18ff155639d4}"  # ,1 = listen on/off, ,0 = play-through endpoint
FID = "Command-Line Friendly ID"
DEFAULT_SOURCE = "VB-Audio Virtual Cable\\Device\\CABLE Input\\Render"
DEFAULT_OUTPUT = "Default playback device"  # SVV sets this when given an empty device name
LOOPBACK_DRIVERS = re.compile(r"cable|virtual|voicemeeter", re.I)
# App volumes are linear amplitude (45% sounds nearly as loud as 100%); the slider spreads this many
# decibels evenly across its length instead. Device volumes are already scaled this way by Windows.
APP_RANGE_DB = 40
SIZE_INCHES = 2.4  # side of the square
NO_WINDOW = 0x08000000


def find_svv(configured=""):
    """Contract 8.2: the svv_path setting, then the helpers dir, then PATH."""
    if configured and os.path.isfile(configured):
        return configured
    local = os.environ.get("LOCALAPPDATA", "")
    if local:
        p = os.path.join(local, "DeskKit", "helpers", "soundvolumeview", "SoundVolumeView.exe")
        if os.path.isfile(p):
            return p
    return shutil.which("SoundVolumeView.exe")


SVV = None  # set in main()


def svv(*args):
    subprocess.run([SVV, *args], creationflags=NO_WINDOW, timeout=15)


def list_items():
    fd, path = tempfile.mkstemp(prefix="cable_listen_items_", suffix=".csv")
    os.close(fd)
    svv("/scomma", path, "/Columns", f"Name,Type,Direction,Device Name,Device State,Item ID,{FID},Volume Percent")
    try:
        with open(path, encoding="utf-8-sig", errors="replace", newline="") as f:
            return list(csv.DictReader(f))
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def read_listen(item_id):
    """Return (listening, play-through endpoint id) for a capture endpoint."""
    guid = item_id.rsplit(".", 1)[-1]
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, rf"{CAPTURE_KEY}\{guid}\Properties") as k:
            on = winreg.QueryValueEx(k, LISTEN_PROP + ",1")[0]
            try:
                through = winreg.QueryValueEx(k, LISTEN_PROP + ",0")[0]
            except OSError:
                through = ""
    except OSError:
        return False, ""
    return bytes(on[8:10]) == b"\xff\xff", str(through)


def set_listen(capture_fid, on, output_fid=""):
    if on:  # setting the play-through device resets listening, so it goes first
        svv("/SetPlaybackThroughDevice", capture_fid, output_fid)
    svv("/SetListenToThisDevice", capture_fid, "1" if on else "0")


def migrate_legacy_config():
    """Copy (never move) an old cable_listen.json to settings.json, once.

    Looks in the data dir, beside this script, and in every PATH folder (the old tool lived in
    a scripts folder on PATH). The old key "source" is the same key in settings.json.
    """
    if os.path.isfile(CONFIG):
        return
    folders = [DATA_DIR, os.path.dirname(os.path.abspath(__file__))] + os.environ.get("PATH", "").split(os.pathsep)
    for folder in folders:
        old = os.path.join(folder, LEGACY_CONFIG) if folder else ""
        if old and os.path.isfile(old):
            try:
                os.makedirs(DATA_DIR, exist_ok=True)
                shutil.copyfile(old, CONFIG)
            except OSError:
                pass
            return


def load_config():
    migrate_legacy_config()
    try:
        with open(CONFIG, encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            data = {}
    except (OSError, ValueError):
        data = {}
    if not os.path.isfile(CONFIG):  # first run: write every key with its default (contract 6)
        data = {"source": DEFAULT_SOURCE, "svv_path": ""}
        write_config(data)
    return data


def write_config(data):
    try:
        os.makedirs(DATA_DIR, exist_ok=True)
        with open(CONFIG, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
    except OSError:
        pass


def pos_to_level(pos):
    """Slider position (0-100) to a linear app volume, so equal slider steps are equal decibel steps."""
    return 0.0 if pos <= 0 else 100 * 10 ** (APP_RANGE_DB * (pos / 100 - 1) / 20)


def level_to_pos(level):
    if level <= 0:
        return 0.0
    return max(0.0, min(100.0, 100 * (1 + 20 * math.log10(level / 100) / APP_RANGE_DB)))


def percent(row):
    try:
        return float(row["Volume Percent"].rstrip("%"))
    except (KeyError, ValueError):
        return 0.0


def is_app(row):
    return row["Type"] == "Application"


def device_label(d):
    return f"{d['Name']} ({d['Device Name']})"


class App(tk.Tk):
    def __init__(self, config_data):
        super().__init__()
        self.title(NAME)
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.config_data = config_data
        self.listening = False
        self.last_capture = None
        self.volume_lock = threading.Lock()
        self.volume_pending = None
        self.volume_busy = False
        self.setting_volume = False
        self.dragging = False
        self.ticks = 0

        side = round(SIZE_INCHES * self.winfo_fpixels("1i"))
        self.geometry(f"{side}x{side}")
        # dropdown lists open wider than the narrow boxes so full device names show
        ttk.Style(self).configure("Wide.TCombobox", postoffset=(0, 0, side, 0))

        outer = tk.Frame(self, highlightthickness=1, highlightbackground="#8a8a8a")
        outer.pack(fill="both", expand=True)
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(6, weight=1)

        bar = tk.Frame(outer)
        bar.grid(row=0, column=0, sticky="ew", padx=(8, 2), pady=(2, 0))
        self.title_label = title = tk.Label(bar, text=NAME, fg="gray", font=("Segoe UI", 8))
        title.pack(side="left")
        close = tk.Label(bar, text="\u2715", fg="gray", font=("Segoe UI", 9), padx=6, cursor="hand2")
        close.pack(side="right")
        close.bind("<Button-1>", lambda e: self.on_close())
        for w in (bar, title):
            w.bind("<ButtonPress-1>", self.drag_start)
            w.bind("<B1-Motion>", self.drag_move)

        pad = {"padx": 8, "sticky": "ew"}
        ttk.Label(outer, text="Listen to").grid(row=1, column=0, pady=(2, 0), **pad)
        self.source_cb = ttk.Combobox(outer, state="readonly", style="Wide.TCombobox")
        self.source_cb.grid(row=2, column=0, **pad)
        self.source_cb.bind("<<ComboboxSelected>>", self.on_source_change)

        ttk.Label(outer, text="Play through").grid(row=3, column=0, pady=(6, 0), **pad)
        self.output_cb = ttk.Combobox(outer, state="readonly", style="Wide.TCombobox")
        self.output_cb.grid(row=4, column=0, **pad)
        self.output_cb.bind("<<ComboboxSelected>>", self.on_output_change)

        vol = tk.Frame(outer)
        vol.grid(row=5, column=0, sticky="ew", padx=8, pady=(8, 0))
        ttk.Label(vol, text="Volume").pack(side="left")
        self.vol_text = ttk.Label(vol, width=4, anchor="e")
        self.vol_text.pack(side="right")
        self.vol = ttk.Scale(vol, from_=0, to=100, command=self.on_volume)
        self.vol.pack(side="left", fill="x", expand=True, padx=(6, 2))
        self.vol.bind("<ButtonPress-1>", lambda e: setattr(self, "dragging", True))
        self.vol.bind("<ButtonRelease-1>", lambda e: setattr(self, "dragging", False))

        self.toggle_btn = tk.Button(outer, text="...", font=("Segoe UI", 14, "bold"), relief="flat",
                                    fg="white", activeforeground="white", command=self.toggle)
        self.toggle_btn.grid(row=6, column=0, sticky="nsew", padx=8, pady=8)

        self.load_lists()
        self.place_near_cursor(side)
        self.after(1000, self.poll)

    # ---- lists ----------------------------------------------------------

    def load_lists(self):
        items = self.items = list_items()
        active =[i for i in items if i["Type"] == "Device" and i["Device State"] == "Active"]
        captures = [i for i in active if i["Direction"] == "Capture"]
        renders = [i for i in active if i["Direction"] == "Render"]

        # each source: what was picked (key) and the recording endpoint that actually listens
        # "vol" is the device the slider is for: the one picked, since a virtual cable's recording
        # level doesn't change what is heard (CABLE Output can sit at 0% and still be audible)
        self.sources = [{"label": f"Rec: {device_label(c)}", "key": c[FID], "capture": c, "vol": c}
                        for c in captures]
        # only virtual cables loop playback back to their recording side; on real hardware
        # the "twin" would be an unrelated mic, so other playback devices aren't offered
        for r in (r for r in renders if LOOPBACK_DRIVERS.search(r["Device Name"])):
            twins = [c for c in captures if c["Device Name"] == r["Device Name"]]
            twin = next((c for c in twins if c["Name"] == r["Name"]), twins[0] if twins else None)
            if twin:
                self.sources.append({"label": f"Play: {device_label(r)}", "key": r[FID], "capture": twin, "vol": r})
        self.outputs = [None] + renders  # None = default playback device

        self.source_cb["values"] = [s["label"] for s in self.sources]
        self.output_cb["values"] = [DEFAULT_OUTPUT] + [device_label(d) for d in renders]

        want = self.config_data.get("source", DEFAULT_SOURCE).lower()
        n = next((n for n, s in enumerate(self.sources) if s["key"].lower() == want), -1)
        if n < 0:
            n = 0
            if want == DEFAULT_SOURCE.lower():  # nothing saved yet and the default cable is not installed
                self.title_label.config(text="VB-Cable not found", fg="#b04040")
                print("WARNING: The default device (VB-Audio Virtual Cable) is not installed. "
                      "Pick another device in the list.", file=sys.stderr)
        if self.sources:
            self.source_cb.current(n)
        self.sync_from_registry()
        self.last_capture = self.capture_id()

    def capture(self):
        n = self.source_cb.current()
        return self.sources[n]["capture"] if n >= 0 else None

    def capture_id(self):
        c = self.capture()
        return c[FID] if c else None

    def output_id(self):
        n = self.output_cb.current()
        return self.outputs[n][FID] if n > 0 else ""

    def sync_from_registry(self):
        c = self.capture()
        self.show_volume()
        if not c:
            self.show_state(False)
            return
        on, through = read_listen(c["Item ID"])
        n = next((n for n, d in enumerate(self.outputs) if d and d["Item ID"].lower() == through.lower()), 0)
        self.output_cb.current(n)
        self.show_state(on)

    # ---- volume (of the picked source device) ---------------------------

    def vol_targets(self):
        """Rows the slider sets for the picked source; the first one's level is shown."""
        n = self.source_cb.current()
        if n < 0:
            return []
        dev = self.sources[n]["vol"]
        if dev["Direction"] != "Render":
            return [dev]
        # A virtual cable ignores its own volume, so turn down the apps playing into it instead;
        # Windows applies per-app volume before the audio reaches the driver.
        apps = {i[FID]: i for i in self.items
                if i["Type"] == "Application" and i["Direction"] == "Render"
                and i["Device Name"] == dev["Device Name"]
                and "Operating System" not in i["Name"] and not i[FID].endswith("\\System Sounds")}
        playing_first = lambda a: (a["Device State"] == "Active", percent(a))
        return sorted(apps.values(), key=playing_first, reverse=True) or [dev]

    def show_volume(self):
        targets = self.vol_targets()
        level = percent(targets[0]) if targets else 0.0
        pos = level_to_pos(level) if targets and is_app(targets[0]) else level
        self.setting_volume = True
        self.vol.set(pos)
        self.setting_volume = False
        self.vol_text.config(text=f"{round(pos)}%")

    def on_volume(self, value):
        pos = round(float(value))
        self.vol_text.config(text=f"{pos}%")
        targets = self.vol_targets()
        if self.setting_volume or not targets:
            return
        level = f"{pos_to_level(pos) if is_app(targets[0]) else pos:.2f}"
        for t in targets:  # keep the cached levels in step for source switches and refreshes
            t["Volume Percent"] = f"{level}%"
        with self.volume_lock:
            self.volume_pending = ([t[FID] for t in targets], level)
            if self.volume_busy:
                return
            self.volume_busy = True
        threading.Thread(target=self.volume_worker, daemon=True).start()

    def volume_worker(self):
        # coalesce a drag into as few SoundVolumeView calls as it can keep up with
        while True:
            with self.volume_lock:
                if not self.volume_pending:
                    self.volume_busy = False
                    return
                fids, level = self.volume_pending
                self.volume_pending = None
            for fid in fids:
                svv("/SetVolume", fid, level)

    def refresh_items(self):
        # apps start and stop playing into the cable; re-read them in the background
        def work():
            items = list_items()
            self.after(0, lambda: self.apply_items(items))

        threading.Thread(target=work, daemon=True).start()

    def apply_items(self, items):
        self.items = items
        if not self.dragging and not self.volume_busy:
            self.show_volume()

    def poll(self):
        # pick up changes made elsewhere (e.g. the Sound control panel)
        c = self.capture()
        if c:
            on, _ = read_listen(c["Item ID"])
            if on != self.listening:
                self.show_state(on)
        self.ticks += 1
        if self.ticks % 10 == 0:
            self.refresh_items()
        self.after(1000, self.poll)

    # ---- listen ---------------------------------------------------------

    def show_state(self, on):
        self.listening = on
        self.toggle_btn.config(text="Listening\nON" if on else "Listening\nOFF",
                               bg="#2e9e4f" if on else "#6b6b6b",
                               activebackground="#257f3f" if on else "#555555")

    def apply(self, capture_id, on, then=None):
        out = self.output_id()

        def work():
            set_listen(capture_id, on, out)
            self.after(0, then or self.sync_from_registry)

        threading.Thread(target=work, daemon=True).start()

    def toggle(self):
        c = self.capture_id()
        if c:
            self.apply(c, not self.listening)

    def on_source_change(self, _event=None):
        old, new = self.last_capture, self.capture_id()
        if old and new and old != new and self.listening:
            # carry listening over to the newly picked device
            self.apply(old, False, then=lambda: self.apply(new, True))
        else:
            self.sync_from_registry()
        self.last_capture = new
        self.save()

    def on_output_change(self, _event=None):
        c = self.capture_id()
        if c and self.listening:
            self.apply(c, True)

    # ---- window ---------------------------------------------------------

    def drag_start(self, event):
        self.drag_from = (event.x_root - self.winfo_x(), event.y_root - self.winfo_y())

    def drag_move(self, event):
        dx, dy = self.drag_from
        self.geometry(f"+{event.x_root - dx}+{event.y_root - dy}")

    def save(self):
        n = self.source_cb.current()
        data = dict(self.config_data)  # keep keys this code does not own (svv_path, user additions)
        data["source"] = self.sources[n]["key"] if n >= 0 else DEFAULT_SOURCE
        write_config(data)

    def on_close(self):
        self.save()
        c = self.capture_id()
        if c and self.listening:
            svv("/SetListenToThisDevice", c, "0")
        self.destroy()

    def bring_to_front(self):
        self.deiconify()
        self.lift()
        self.attributes("-topmost", True)
        self.focus_force()

    def place_near_cursor(self, side):
        x, y = self.winfo_pointerxy()
        x = max(0, min(x - side // 2, self.winfo_screenwidth() - side))
        y = max(0, min(y - side // 2, self.winfo_screenheight() - side - 48))
        self.geometry(f"+{x}+{y}")


# ---- single instance (contract 7.2) --------------------------------------

MUTEX_NAME = "Local\\DeskKit.cablelisten"
SHOW_EVENT = "Local\\DeskKit.cablelisten.show"
STOP_EVENT = "Local\\DeskKit.cablelisten.stop"
_handles = []  # keep the mutex and events alive for the whole run


def claim_single_instance():
    """Return (show, stop) event handles, or exit 0 after waking the running copy."""
    k32 = ctypes.windll.kernel32
    k32.CreateMutexW.restype = ctypes.c_void_p
    k32.CreateEventW.restype = ctypes.c_void_p
    k32.OpenEventW.restype = ctypes.c_void_p
    k32.SetEvent.argtypes = [ctypes.c_void_p]
    mutex = k32.CreateMutexW(None, False, MUTEX_NAME)
    if k32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        h = k32.OpenEventW(0x0002, False, SHOW_EVENT)  # EVENT_MODIFY_STATE
        if h:
            k32.SetEvent(h)
        sys.exit(0)
    show = k32.CreateEventW(None, False, False, SHOW_EVENT)
    stop = k32.CreateEventW(None, False, False, STOP_EVENT)
    _handles.extend([mutex, show, stop])
    return show, stop


def watch_events(app, show, stop):
    k32 = ctypes.windll.kernel32
    k32.WaitForMultipleObjects.argtypes = [ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p),
                                           ctypes.c_int, ctypes.c_uint32]
    arr = (ctypes.c_void_p * 2)(show, stop)

    def loop():
        while True:
            r = k32.WaitForMultipleObjects(2, arr, False, 0xFFFFFFFF)
            if r == 0:
                app.after(0, app.bring_to_front)
            elif r == 1:
                app.after(0, app.on_close)
                return
            else:
                return

    threading.Thread(target=loop, daemon=True).start()


def main():
    global SVV
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except (AttributeError, OSError):
        pass
    show, stop = claim_single_instance()
    config_data = load_config()
    SVV = find_svv(str(config_data.get("svv_path", "")))
    if not SVV:
        fail(EXIT_HELPER, "SoundVolumeView.exe was not found. " + SVV_HELP)
    app = App(config_data)
    if not app.sources:
        app.destroy()
        fail(EXIT_DEVICE, "No active audio device found. Plug in or enable a recording device and try again.")
    watch_events(app, show, stop)
    app.mainloop()


if __name__ == "__main__":
    main()
