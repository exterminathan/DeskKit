# .github/workflows/

GitHub Actions for DeskKit. Planned only: DeskKit has no git repo and no remote yet, so nothing runs. Nate decides when to publish (after the G3 pilot).

Planned workflows (`windows-latest` runners):

- `validate.yml`: on every push and pull request, run `python tools/validate-modules.py` and `python -m unittest discover -s tests`.
- `module-release.yml`: on a tag `<id>/vX.Y.Z`, build that module's zip and `SHA256SUMS.txt` and create the GitHub release (`docs/module-contract.md` section 10.2).
- `suite-release.yml`: on a tag `vX.Y.Z`, run `tools/validate-modules.py --release`, build the shell and the installer, and create the release (section 10.4).
