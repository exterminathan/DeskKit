"""Runs tests/test_migrate.ps1 (tools/migrate-launchers.ps1 against a fake tree in %TEMP%).

Windows only: the script uses WScript.Shell to read and write .lnk files.
"""
import os
import shutil
import subprocess
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))


@unittest.skipUnless(sys.platform == "win32" and shutil.which("powershell.exe"), "needs Windows PowerShell")
class MigrateLaunchersTest(unittest.TestCase):
    def test_migrate_launchers(self):
        proc = subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             os.path.join(HERE, "test_migrate.ps1")],
            capture_output=True, text=True, timeout=600,
        )
        fails = [line for line in proc.stdout.splitlines() if line.startswith("FAIL")]
        self.assertEqual(proc.returncode, 0, "\n".join(fails) or proc.stdout[-2000:] + proc.stderr[-2000:])


if __name__ == "__main__":
    unittest.main()
