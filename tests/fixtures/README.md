# tests/fixtures/

- `broken/`: a deliberately broken mini repo (`modules/badmod/module.json` and `suite.json`). `test_contract.py` copies the real `schemas/` next to it and expects `tools/validate-modules.py` to fail with known errors. Do not fix these files.
