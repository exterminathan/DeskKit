"""Tests for the DeskKit module contract (docs/module-contract.md).

Run from the repo root:  python -m unittest discover -s tests -v
Standard library only. Tests never touch the real %APPDATA%: they point APPDATA at a temp folder.
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VALIDATOR = ROOT / "tools" / "validate-modules.py"
FIXTURE_BROKEN = ROOT / "tests" / "fixtures" / "broken"
TEMPLATE_PY = ROOT / "modules" / "_template-python"


def load_validator():
    spec = importlib.util.spec_from_file_location("validate_modules", VALIDATOR)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


vm = load_validator()


def run_validator(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(VALIDATOR), *args],
                          capture_output=True, text=True, timeout=60)


def temp_env(appdata: Path) -> dict:
    env = dict(os.environ)
    env["APPDATA"] = str(appdata)
    env.pop("DESKKIT_DATA_DIR", None)
    env.pop("DESKKIT_SHELL", None)
    return env


class RealRepoTests(unittest.TestCase):
    def test_validator_passes_on_repo(self):
        r = run_validator("--root", str(ROOT))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("OK:", r.stdout)

    def test_every_module_and_template_checked(self):
        folders = {p.name for p in (ROOT / "modules").iterdir() if p.is_dir()}
        want = {"regionshot", "cablelisten", "eyesaver", "regioncontrol", "taskbargroups",
                "_template", "_template-python"}
        self.assertTrue(want <= folders, f"missing: {want - folders}")
        errors, _ = vm.validate(ROOT)
        self.assertEqual(errors, [])

    def test_suite_lists_the_five_modules(self):
        suite = json.loads((ROOT / "suite.json").read_text(encoding="utf-8"))
        ids = {m["id"] for m in suite["modules"] if m.get("status") != "planned"}
        self.assertEqual(ids, {"regionshot", "cablelisten", "eyesaver", "regioncontrol", "taskbargroups"})

    def test_planned_scaffolds_are_listed_as_planned(self):
        suite = json.loads((ROOT / "suite.json").read_text(encoding="utf-8"))
        planned = {m["id"] for m in suite["modules"] if m.get("status") == "planned"}
        self.assertEqual(planned, {"colorpicker", "alwaysontop", "powerdisplay", "powerrename"})

    def test_launchers_have_crlf(self):
        for cmd in (ROOT / "modules").glob("*/launch.cmd"):
            data = cmd.read_bytes()
            lf = data.count(b"\n")
            self.assertGreater(lf, 0, cmd)
            self.assertEqual(data.count(b"\r\n"), lf, f"{cmd} must use CRLF line endings")


class BrokenFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = Path(tempfile.mkdtemp(prefix="deskkit-broken-"))
        shutil.copytree(FIXTURE_BROKEN, cls.tmp, dirs_exist_ok=True)
        shutil.copytree(ROOT / "schemas", cls.tmp / "schemas")
        cls.result = run_validator("--root", str(cls.tmp))
        cls.errors, _ = vm.validate(cls.tmp)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_validator_fails(self):
        self.assertEqual(self.result.returncode, 1, self.result.stdout)
        self.assertIn("FAILED:", self.result.stdout)

    def assertError(self, *parts: str):
        hits = [e for e in self.errors if all(p in e for p in parts)]
        self.assertTrue(hits, f"no error containing {parts}; errors were:\n" + "\n".join(self.errors))

    def test_schema_errors_found(self):
        self.assertError("$.version", "does not match")            # "1.0" is not SemVer
        self.assertError("'colour'")                               # additionalProperties false
        self.assertError("$.repo")                                  # in-repo needs repo null
        self.assertError("$.instance.single")                      # oneshot must be single false
        self.assertError("$.autostart.default")                    # oneshot cannot autostart
        self.assertError("$.runtime")                               # self_contained on python

    def test_contract_errors_found(self):
        self.assertError("$.id", "'wrongid'", "'badmod'")
        self.assertError("$.entry", "hard-coded drive path")
        self.assertError("$.data_dir", "hard-coded drive path")
        self.assertError("%USERPROFILE%", "not allowed")
        self.assertError("unknown token {unknown_token}")
        self.assertError("$.settings_action", "'nosuchaction'")
        self.assertError("action ids must be unique")
        self.assertError("$.launcher", "required")
        self.assertError("$.icon", "missing.ico")
        self.assertError("README.md", "required")

    def test_suite_errors_found(self):
        self.assertError("suite.json", "badmod", "version '2.0.0'")
        self.assertError("suite.json", "ghost", "no folder modules/ghost/")
        self.assertError("suite.json", "sha256")                   # url set but sha256 null


class PlannedStatusTests(unittest.TestCase):
    """suite.json `status: "planned"` (contract 11): validated like any entry, never installed."""

    def make_root(self, entries: list[dict], folders=("colorpicker",)) -> Path:
        tmp = Path(tempfile.mkdtemp(prefix="deskkit-planned-"))
        self.addCleanup(shutil.rmtree, tmp, True)
        shutil.copytree(ROOT / "schemas", tmp / "schemas")
        for f in folders:
            shutil.copytree(ROOT / "modules" / f, tmp / "modules" / f,
                            ignore=shutil.ignore_patterns("bin", "obj"))
        suite = {"schema_version": 1, "name": "DeskKit", "version": "0.1.0", "publisher": "x", "licence": "MIT",
                 "shell": {"version": "0.1.0", "path": "shell"}, "modules": entries}
        (tmp / "suite.json").write_text(json.dumps(suite), encoding="utf-8")
        return tmp

    @staticmethod
    def entry(mid="colorpicker", **extra) -> dict:
        return {"id": mid, "version": "0.0.1", "source": "in-repo", "path": f"modules/{mid}", **extra}

    def test_planned_entry_validates(self):
        root = self.make_root([self.entry(status="planned")])
        errors, _ = vm.validate(root)
        self.assertEqual(errors, [])
        self.assertEqual(run_validator("--root", str(root)).returncode, 0)

    def test_planned_entry_passes_release_check(self):
        root = self.make_root([self.entry(status="planned")])
        errors, _ = vm.validate(root, release=True)
        self.assertEqual(errors, [])

    def test_planned_entry_with_missing_folder_fails(self):
        root = self.make_root([self.entry(status="planned"), self.entry("ghostplan", status="planned")])
        errors, _ = vm.validate(root)
        self.assertTrue(any("ghostplan" in e and "no folder modules/ghostplan/" in e for e in errors), errors)

    def test_entry_without_planned_and_missing_folder_fails(self):
        root = self.make_root([self.entry(), self.entry("ghost")])
        errors, _ = vm.validate(root)
        self.assertTrue(any("ghost" in e and "no folder modules/ghost/" in e for e in errors), errors)

    def test_folder_not_listed_fails_even_if_scaffold(self):
        root = self.make_root([self.entry(mid="alwaysontop", status="planned")], folders=("colorpicker", "alwaysontop"))
        errors, _ = vm.validate(root)
        self.assertTrue(any("modules/colorpicker/ is not listed" in e for e in errors), errors)

    def test_status_value_must_be_planned(self):
        root = self.make_root([self.entry(status="shipped")])
        errors, _ = vm.validate(root)
        self.assertTrue(any("status" in e for e in errors), errors)

    def test_pinned_entry_cannot_be_planned(self):
        e = {"id": "colorpicker", "version": "0.0.1", "source": "pinned", "path": "modules/colorpicker",
             "repo": "https://github.com/a/b", "tag": None, "url": None, "sha256": None, "asset_type": "exe",
             "status": "planned"}
        root = self.make_root([e])
        errors, _ = vm.validate(root)
        self.assertTrue(any("status" in e for e in errors), errors)

    def test_real_suite_entries_marked_planned_are_not_in_build_or_installer(self):
        # build-suite.ps1 skips them by status; the installer components come from what build-suite stages.
        script = (ROOT / "packaging" / "build-suite.ps1").read_text(encoding="utf-8")
        self.assertIn("-eq 'planned'", script)


class MiniValidatorTests(unittest.TestCase):
    def test_if_then_else(self):
        schema = {"type": "object",
                  "if": {"properties": {"a": {"const": 1}}},
                  "then": {"required": ["b"]},
                  "else": {"not": {"required": ["b"]}}}
        v = vm.MiniValidator(schema)
        self.assertEqual(v.errors({"a": 1, "b": 2}), [])
        self.assertTrue(v.errors({"a": 1}))
        self.assertTrue(v.errors({"a": 2, "b": 2}))
        self.assertEqual(v.errors({"a": 2}), [])

    def test_ref_anyof_types(self):
        schema = {"$defs": {"s": {"type": "string", "pattern": "^x"}},
                  "anyOf": [{"type": "null"}, {"$ref": "#/$defs/s"}]}
        v = vm.MiniValidator(schema)
        self.assertEqual(v.errors(None), [])
        self.assertEqual(v.errors("xy"), [])
        self.assertTrue(v.errors("yx"))
        self.assertTrue(v.errors(3))

    def test_bool_is_not_integer(self):
        self.assertTrue(vm.MiniValidator({"type": "integer"}).errors(True))

    def test_unsupported_keyword_raises(self):
        with self.assertRaises(ValueError):
            vm.MiniValidator({"format": "uri"}).errors("x")

    def test_path_rules(self):
        errs = vm.path_rule_errors({"a": "C:\\x", "b": "d:/y", "c": "%APPDATA%\\ok",
                                    "d": "%PROGRAMFILES%\\no", "e": "\\Users\\nate",
                                    "f": "https://example.com/C:/fine", "notes": "C:\\allowed in notes"})
        joined = "\n".join(errs)
        self.assertIn("$.a", joined)
        self.assertIn("$.b", joined)
        self.assertNotIn("$.c", joined)
        self.assertIn("PROGRAMFILES", joined)
        self.assertIn("$.e", joined)
        self.assertNotIn("$.f", joined)
        self.assertNotIn("$.notes", joined)

    @unittest.skipIf(vm.jsonschema is None, "jsonschema not installed")
    def test_builtin_agrees_with_jsonschema(self):
        schema = json.loads((ROOT / "schemas" / "module.schema.json").read_text(encoding="utf-8"))
        for mf in list((ROOT / "modules").glob("*/module.json")) + [FIXTURE_BROKEN / "modules/badmod/module.json"]:
            doc = json.loads(mf.read_text(encoding="utf-8"))
            ours = bool(vm.MiniValidator(schema).errors(doc))
            theirs = bool(list(vm.jsonschema.Draft202012Validator(schema).iter_errors(doc)))
            self.assertEqual(ours, theirs, mf)


class TemplateRunsTests(unittest.TestCase):
    def setUp(self):
        self.appdata = Path(tempfile.mkdtemp(prefix="deskkit-appdata-"))

    def tearDown(self):
        shutil.rmtree(self.appdata, ignore_errors=True)

    def test_python_template_runs_and_writes_settings(self):
        r = subprocess.run([sys.executable, str(TEMPLATE_PY / "src" / "hello.py"), "Nate"],
                           capture_output=True, text=True, timeout=60, env=temp_env(self.appdata))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Hello, Nate!", r.stdout)
        settings = self.appdata / "DeskKit" / "template-python" / "settings.json"
        self.assertEqual(json.loads(settings.read_text(encoding="utf-8")), {"greeting": "Hello"})

    def test_python_template_reads_settings(self):
        d = self.appdata / "DeskKit" / "template-python"
        d.mkdir(parents=True)
        (d / "settings.json").write_text('{"greeting": "Hi"}', encoding="utf-8")
        r = subprocess.run([sys.executable, str(TEMPLATE_PY / "src" / "hello.py")],
                           capture_output=True, text=True, timeout=60, env=temp_env(self.appdata))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Hi, DeskKit!", r.stdout)

    def test_python_template_bad_args_exit_2(self):
        r = subprocess.run([sys.executable, str(TEMPLATE_PY / "src" / "hello.py"), "--bogus"],
                           capture_output=True, text=True, timeout=60, env=temp_env(self.appdata))
        self.assertEqual(r.returncode, 2)
        self.assertTrue(r.stderr.startswith("ERROR: "), r.stderr)

    @unittest.skipUnless(sys.platform == "win32", "launch.cmd needs cmd.exe")
    def test_python_template_launcher_runs(self):
        r = subprocess.run(["cmd.exe", "/c", str(TEMPLATE_PY / "launch.cmd"), "Nate"],
                           capture_output=True, text=True, timeout=60, env=temp_env(self.appdata),
                           stdin=subprocess.DEVNULL)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("Hello, Nate!", r.stdout)


if __name__ == "__main__":
    unittest.main()
