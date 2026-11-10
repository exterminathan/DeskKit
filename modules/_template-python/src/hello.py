"""DeskKit template module (Python): print a greeting and exit.

Copy modules/_template-python/ to modules/<id>/ to start a Python module.
It shows the contract basics (docs/module-contract.md):
  * version check before any other import (5.2)
  * data dir and settings.json with defaults written on first run (6)
  * exit codes and one ERROR line on stderr (9)

Usage: hello.py [name]
"""
import sys

MIN = (3, 11)
MODULE_ID = "template-python"
NAME = "Hello (Python)"
CONSOLE = True  # equals "console" in module.json

# Exit codes, contract section 9.1.
EXIT_OK, EXIT_ERROR, EXIT_USAGE, EXIT_HELPER, EXIT_RUNTIME, EXIT_DEVICE, EXIT_PACKAGES = range(7)


def fail(code: int, message: str) -> None:
    """Write one ERROR line, show a message box when nobody can read stderr, and exit."""
    import os
    print(f"ERROR: {message}", file=sys.stderr)
    if not CONSOLE and not os.environ.get("DESKKIT_SHELL"):
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, message, NAME, 0x10)  # MB_ICONERROR
    sys.exit(code)


if sys.version_info < MIN:
    fail(EXIT_RUNTIME, f"Python {MIN[0]}.{MIN[1]} or newer not found. "
                       "Install it from https://www.python.org/downloads/windows/ and try again.")

import json  # noqa: E402  (after the version check on purpose)
import os  # noqa: E402
from pathlib import Path  # noqa: E402

DEFAULTS = {"greeting": "Hello"}


def data_dir() -> Path:
    """%APPDATA%\\DeskKit\\<id>, or what the shell passed in DESKKIT_DATA_DIR."""
    passed = os.environ.get("DESKKIT_DATA_DIR")
    if passed:
        return Path(passed)
    appdata = os.environ.get("APPDATA")
    if not appdata:
        fail(EXIT_ERROR, "APPDATA is not set, so there is nowhere to keep settings.")
    return Path(appdata) / "DeskKit" / MODULE_ID


def load_settings() -> dict:
    """Read settings.json; write it with all defaults when it is missing (contract 6)."""
    path = data_dir() / "settings.json"
    settings = dict(DEFAULTS)
    if path.is_file():
        try:
            settings.update(json.loads(path.read_text(encoding="utf-8")))
        except (OSError, ValueError) as e:
            fail(EXIT_ERROR, f"Cannot read {path}: {e}. Fix or delete the file.")
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")
    return settings


def main(argv: list[str]) -> int:
    if len(argv) > 1 or any(a.startswith("-") for a in argv):
        fail(EXIT_USAGE, "Usage: hello.py [name]")
    who = argv[0] if argv else "DeskKit"
    settings = load_settings()
    print(f"{settings['greeting']}, {who}! ({NAME}, Python {sys.version.split()[0]})")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
