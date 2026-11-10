# modules/

One folder per module, named by its id, each with a `module.json`. The rules are in `docs/module-contract.md`.

- In-repo modules (`regionshot`, `cablelisten`, `eyesaver`): the folder is the source of truth for the code.
- Pinned modules (`regioncontrol`): only `module.json` and `README.md`. The code lives in their own repos; `suite.json` pins one release asset.
- Templates (folders starting with `_`): `_template/` (C#, .NET 8, the default) and `_template-python/`. The shell and installer skip them.

To add a module, follow `docs/module-contract.md` section 14.
