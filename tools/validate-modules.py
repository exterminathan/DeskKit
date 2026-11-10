"""Validate every DeskKit module.json and suite.json.

Usage:
    python tools/validate-modules.py [--root <repo>] [--release]

Checks (docs/module-contract.md, section 12):
  * every modules/*/module.json (templates included) against schemas/module.schema.json
  * suite.json against schemas/suite.schema.json
  * contract rules a schema cannot express (ids, names, paths, files, suite match)
  * --release also fails on a pinned module with url null and on licence NOASSERTION

Uses the `jsonschema` package if it is installed; otherwise a built-in checker
for the subset of JSON Schema 2020-12 that the DeskKit schemas use.

Exit 0 when everything passes, 1 when there is any error. Warnings never fail.
Python 3.12, standard library only (jsonschema optional).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

try:  # optional
    import jsonschema  # type: ignore
except ImportError:  # pragma: no cover - depends on the machine
    jsonschema = None

ALLOWED_ENV = {"APPDATA", "LOCALAPPDATA", "TEMP"}
ALLOWED_TOKENS = {"{module_dir}", "{data_dir}", "{helpers_dir}"}
DRIVE_RE = re.compile(r"(?<![A-Za-z])[A-Za-z]:[\\/]")
USERS_RE = re.compile(r"[\\/]Users[\\/]", re.IGNORECASE)
ENV_RE = re.compile(r"%([^%]+)%")
TOKEN_RE = re.compile(r"\{[^{}]*\}")
COMPILED = {"dotnet", "native"}
SCRIPTED = {"python", "powershell"}


# --------------------------------------------------------------------------
# Built-in JSON Schema subset checker
# --------------------------------------------------------------------------

_TYPES = {
    "object": lambda v: isinstance(v, dict),
    "array": lambda v: isinstance(v, list),
    "string": lambda v: isinstance(v, str),
    "boolean": lambda v: isinstance(v, bool),
    "null": lambda v: v is None,
    "integer": lambda v: isinstance(v, int) and not isinstance(v, bool),
    "number": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool),
}

_SUPPORTED = {
    "$schema", "$id", "title", "description", "$defs", "$ref", "type", "const",
    "enum", "pattern", "minLength", "maxLength", "minItems", "maxItems", "items",
    "properties", "required", "additionalProperties", "allOf", "anyOf", "oneOf",
    "not", "if", "then", "else", "minimum", "maximum",
}


class MiniValidator:
    """Checks an instance against a schema. Returns a list of error strings."""

    def __init__(self, root_schema: dict):
        self.root = root_schema

    def _resolve(self, ref: str) -> dict:
        if not ref.startswith("#/"):
            raise ValueError(f"only local $ref is supported, got {ref}")
        node = self.root
        for part in ref[2:].split("/"):
            node = node[part]
        return node

    def errors(self, inst, schema=None, path="$") -> list[str]:
        if schema is None:
            schema = self.root
        if schema is True:
            return []
        if schema is False:
            return [f"{path}: not allowed"]
        unknown = set(schema) - _SUPPORTED
        if unknown:
            raise ValueError(f"schema keyword(s) not supported by built-in checker: {sorted(unknown)}")
        out: list[str] = []
        if "$ref" in schema:
            out += self.errors(inst, self._resolve(schema["$ref"]), path)
        if "type" in schema:
            types = schema["type"] if isinstance(schema["type"], list) else [schema["type"]]
            if not any(_TYPES[t](inst) for t in types):
                out.append(f"{path}: expected {' or '.join(types)}, got {type(inst).__name__}")
                return out
        if "const" in schema and inst != schema["const"]:
            out.append(f"{path}: must be {json.dumps(schema['const'])}")
        if "enum" in schema and inst not in schema["enum"]:
            out.append(f"{path}: must be one of {schema['enum']}")
        if isinstance(inst, str):
            if "pattern" in schema and not re.search(schema["pattern"], inst):
                out.append(f"{path}: {inst!r} does not match {schema['pattern']}")
            if "minLength" in schema and len(inst) < schema["minLength"]:
                out.append(f"{path}: shorter than {schema['minLength']}")
            if "maxLength" in schema and len(inst) > schema["maxLength"]:
                out.append(f"{path}: longer than {schema['maxLength']}")
        if isinstance(inst, (int, float)) and not isinstance(inst, bool):
            if "minimum" in schema and inst < schema["minimum"]:
                out.append(f"{path}: below {schema['minimum']}")
            if "maximum" in schema and inst > schema["maximum"]:
                out.append(f"{path}: above {schema['maximum']}")
        if isinstance(inst, list):
            if "minItems" in schema and len(inst) < schema["minItems"]:
                out.append(f"{path}: fewer than {schema['minItems']} items")
            if "maxItems" in schema and len(inst) > schema["maxItems"]:
                out.append(f"{path}: more than {schema['maxItems']} items")
            if "items" in schema:
                for i, item in enumerate(inst):
                    out += self.errors(item, schema["items"], f"{path}[{i}]")
        if isinstance(inst, dict):
            props = schema.get("properties", {})
            for key in schema.get("required", []):
                if key not in inst:
                    out.append(f"{path}: missing required key {key!r}")
            for key, sub in props.items():
                if key in inst:
                    out += self.errors(inst[key], sub, f"{path}.{key}")
            if "additionalProperties" in schema:
                extra = [k for k in inst if k not in props]
                ap = schema["additionalProperties"]
                for key in extra:
                    if ap is False:
                        out.append(f"{path}: unknown key {key!r}")
                    elif isinstance(ap, dict):
                        out += self.errors(inst[key], ap, f"{path}.{key}")
        for sub in schema.get("allOf", []):
            out += self.errors(inst, sub, path)
        if "anyOf" in schema:
            results = [self.errors(inst, sub, path) for sub in schema["anyOf"]]
            if all(results):
                out.append(f"{path}: matches none of anyOf ({'; '.join(r[0] for r in results)})")
        if "oneOf" in schema:
            ok = sum(1 for sub in schema["oneOf"] if not self.errors(inst, sub, path))
            if ok != 1:
                out.append(f"{path}: must match exactly one of oneOf (matched {ok})")
        if "not" in schema and not self.errors(inst, schema["not"], path):
            out.append(f"{path}: must not match {json.dumps(schema['not'])}")
        if "if" in schema:
            if not self.errors(inst, schema["if"], path):
                if "then" in schema:
                    out += self.errors(inst, schema["then"], path)
            elif "else" in schema:
                out += self.errors(inst, schema["else"], path)
        return out


def schema_errors(instance, schema: dict) -> list[str]:
    if jsonschema is not None:
        v = jsonschema.Draft202012Validator(schema)
        return [
            f"$.{'.'.join(str(p) for p in e.absolute_path)}: {e.message}" if e.absolute_path else f"$: {e.message}"
            for e in sorted(v.iter_errors(instance), key=lambda e: list(map(str, e.absolute_path)))
        ]
    return MiniValidator(schema).errors(instance)


# --------------------------------------------------------------------------
# Contract checks
# --------------------------------------------------------------------------

def iter_strings(value, path="$"):
    if isinstance(value, str):
        yield path, value
    elif isinstance(value, dict):
        for k, v in value.items():
            yield from iter_strings(v, f"{path}.{k}")
    elif isinstance(value, list):
        for i, v in enumerate(value):
            yield from iter_strings(v, f"{path}[{i}]")


def path_rule_errors(doc) -> list[str]:
    """No drive letters, no \\Users\\ paths, only allowed %VAR% and {tokens}."""
    out = []
    for p, s in iter_strings(doc):
        # $schema is an editor hint; notes and note are free text for builders, never used as paths.
        if p == "$.$schema" or p == "$.notes" or re.fullmatch(r"\$\.modules\[\d+\]\.note", p):
            continue
        if s.startswith("https://"):
            continue
        if DRIVE_RE.search(s):
            out.append(f"{p}: hard-coded drive path {s!r}")
        if USERS_RE.search(s):
            out.append(f"{p}: hard-coded user path {s!r}")
        for var in ENV_RE.findall(s):
            if var.upper() not in ALLOWED_ENV:
                out.append(f"{p}: environment variable %{var}% is not allowed (use {sorted(ALLOWED_ENV)})")
    return out


def token_errors(module: dict) -> list[str]:
    out = []
    fields = [("$.data_dir", module.get("data_dir")), ("$.settings_path", module.get("settings_path"))]
    fields += [(f"$.args[{i}]", a) for i, a in enumerate(module.get("args") or [])]
    for ai, act in enumerate(module.get("actions") or []):
        fields += [(f"$.actions[{ai}].args[{i}]", a) for i, a in enumerate(act.get("args") or [])]
    for hi, h in enumerate(module.get("helpers") or []):
        fields += [(f"$.helpers[{hi}].detect.paths[{i}]", s) for i, s in enumerate(h.get("detect", {}).get("paths") or [])]
    for p, s in fields:
        if not isinstance(s, str):
            continue
        for tok in TOKEN_RE.findall(s):
            if tok not in ALLOWED_TOKENS:
                out.append(f"{p}: unknown token {tok} (use {sorted(ALLOWED_TOKENS)})")
    dd = module.get("data_dir")
    if isinstance(dd, str) and "{data_dir}" in dd:
        out.append("$.data_dir: may not contain {data_dir}")
    return out


def module_rule_errors(module: dict, folder: Path, is_template: bool) -> list[str]:
    out: list[str] = []
    mid = module.get("id")
    expected_id = folder.name[1:] if is_template else folder.name
    if mid != expected_id:
        out.append(f"$.id: {mid!r} must equal {expected_id!r} (folder {folder.name})")
    out += path_rule_errors(module)
    out += token_errors(module)

    source = module.get("source")
    run_mode = module.get("run_mode")
    rt = module.get("runtime") or {}
    rtype = rt.get("type")
    inst = module.get("instance") or {}

    if source == "in-repo":
        want_dd = f"%APPDATA%\\DeskKit\\{mid}"
        if module.get("data_dir") != want_dd:
            out.append(f"$.data_dir: in-repo module must use {want_dd!r}")
        if run_mode in ("tray", "window"):
            for key, want in (("mutex", f"Local\\DeskKit.{mid}"),
                              ("show_event", f"Local\\DeskKit.{mid}.show"),
                              ("stop_event", f"Local\\DeskKit.{mid}.stop")):
                if inst.get(key) != want:
                    out.append(f"$.instance.{key}: must be {want!r}")
        # files
        def need(rel, what):
            if isinstance(rel, str) and not (folder / rel).is_file():
                out.append(f"{what}: file {rel!r} not found in {folder.name}/")
        if rtype in COMPILED:
            if not (folder / "build.ps1").is_file():
                out.append(f"build.ps1: required for a {rtype} in-repo module, not found in {folder.name}/")
            if module.get("launcher") is not None:
                out.append("$.launcher: must be null for a compiled module")
        else:
            need(module.get("entry"), "$.entry")
            if module.get("launcher") is None:
                out.append(f"$.launcher: required for a {rtype} in-repo module (launch.cmd)")
            need(module.get("launcher"), "$.launcher")
        need(module.get("icon"), "$.icon")
        if rtype == "python":
            need(rt.get("requirements"), "$.runtime.requirements")
        for f in ("README.md", "CHANGELOG.md"):
            if not (folder / f).is_file():
                out.append(f"{f}: required, not found in {folder.name}/")
    elif source == "pinned":
        if module.get("launcher") is not None:
            out.append("$.launcher: must be null for a pinned module")
        if module.get("icon") is not None and not (folder / module["icon"]).is_file():
            out.append(f"$.icon: file {module['icon']!r} not found in {folder.name}/")
        if not (folder / "README.md").is_file():
            out.append(f"README.md: required, not found in {folder.name}/")
        for key in ("show_event", "stop_event"):
            if inst.get(key) is not None:
                out.append(f"$.instance.{key}: must be null for a pinned module")

    action_ids = [a.get("id") for a in module.get("actions") or []]
    if len(action_ids) != len(set(action_ids)):
        out.append("$.actions: action ids must be unique")
    sa = module.get("settings_action")
    if sa is not None and sa not in action_ids:
        out.append(f"$.settings_action: {sa!r} is not an action id")

    helper_ids = [h.get("id") for h in module.get("helpers") or []]
    if len(helper_ids) != len(set(helper_ids)):
        out.append("$.helpers: helper ids must be unique")
    return out


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

def load_json(path: Path):
    with path.open(encoding="utf-8") as f:
        return json.load(f)


def validate(root: Path, release: bool = False) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []
    try:
        mschema = load_json(root / "schemas" / "module.schema.json")
        sschema = load_json(root / "schemas" / "suite.schema.json")
    except (OSError, json.JSONDecodeError) as e:
        return [f"schemas: cannot load: {e}"], warnings

    modules: dict[str, dict] = {}
    helper_downloads: dict[str, tuple[str, dict]] = {}
    mdir = root / "modules"
    folders = sorted(p for p in mdir.iterdir() if p.is_dir()) if mdir.is_dir() else []
    if not folders:
        errors.append("modules/: no module folders found")
    for folder in folders:
        is_template = folder.name.startswith("_")
        mf = folder / "module.json"
        label = f"modules/{folder.name}/module.json"
        if not mf.is_file():
            errors.append(f"{label}: missing")
            continue
        try:
            module = load_json(mf)
        except json.JSONDecodeError as e:
            errors.append(f"{label}: invalid JSON: {e}")
            continue
        errs = schema_errors(module, mschema)
        if isinstance(module, dict):
            errs += module_rule_errors(module, folder, is_template)
        errors += [f"{label}: {e}" for e in errs]
        if not isinstance(module, dict):
            continue
        for h in module.get("helpers") or []:
            hid, dl = h.get("id"), h.get("download")
            if hid in helper_downloads and helper_downloads[hid][1] != dl:
                errors.append(f"{label}: helper {hid!r} download differs from modules/{helper_downloads[hid][0]}")
            helper_downloads.setdefault(hid, (folder.name, dl))
        if release and module.get("licence") == "NOASSERTION":
            errors.append(f"{label}: licence NOASSERTION is not allowed in a release")
        if not is_template:
            modules[folder.name] = module

    sf = root / "suite.json"
    if not sf.is_file():
        errors.append("suite.json: missing")
        return errors, warnings
    try:
        suite = load_json(sf)
    except json.JSONDecodeError as e:
        errors.append(f"suite.json: invalid JSON: {e}")
        return errors, warnings
    errors += [f"suite.json: {e}" for e in schema_errors(suite, sschema)]
    errors += [f"suite.json: {e}" for e in path_rule_errors(suite)]
    entries = suite.get("modules", []) if isinstance(suite, dict) else []
    seen = set()
    for i, entry in enumerate(entries):
        if not isinstance(entry, dict):
            continue
        sid = entry.get("id")
        where = f"suite.json: modules[{i}] ({sid})"
        if sid in seen:
            errors.append(f"{where}: listed twice")
        seen.add(sid)
        if entry.get("path") != f"modules/{sid}":
            errors.append(f"{where}: path must be 'modules/{sid}'")
        mod = modules.get(sid)
        if mod is None:
            errors.append(f"{where}: no folder modules/{sid}/ with a module.json")
            continue
        for key in ("version", "source"):
            if entry.get(key) != mod.get(key):
                errors.append(f"{where}: {key} {entry.get(key)!r} differs from module.json {mod.get(key)!r}")
        if entry.get("source") == "pinned" and entry.get("repo") != mod.get("repo"):
            errors.append(f"{where}: repo differs from module.json")
        if entry.get("source") == "pinned" and entry.get("url") is None:
            msg = f"{where}: pinned module has no release (url null); a suite build skips it"
            (errors if release else warnings).append(msg)
    for name in modules:
        if name not in seen:
            errors.append(f"suite.json: modules/{name}/ is not listed")
    return errors, warnings


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Validate DeskKit module.json and suite.json files.")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                    help="repo root (default: the folder above tools/)")
    ap.add_argument("--release", action="store_true",
                    help="also fail on pinned modules without a release and on licence NOASSERTION")
    args = ap.parse_args(argv)
    errors, warnings = validate(args.root.resolve(), args.release)
    checker = "jsonschema" if jsonschema is not None else "built-in checker"
    for w in warnings:
        print(f"WARNING: {w}")
    for e in errors:
        print(f"ERROR: {e}")
    if errors:
        print(f"FAILED: {len(errors)} error(s), {len(warnings)} warning(s) ({checker}).")
        return 1
    print(f"OK: all modules and suite.json valid, {len(warnings)} warning(s) ({checker}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
