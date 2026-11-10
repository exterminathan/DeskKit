# DeskKit roadmap

Starter roadmap from `docs/brief.md`. Placeholder until Nate confirms it in the interview.

## Track G: Goals

### G1 Inventory and scope
Summary: List every candidate tool and decide what is in scope.
Gate: Inventory table done and Nate has approved the scope list.
- [ ] Inventory table (language, entry, config, run mode, hard-coded paths)
- [ ] Scope decisions from Nate (decided 2026-11-10: Region Control, LightweightTaskbarGroups, RegionShot, Eye Saver, Cable Listen; Win4KSS is its own product and stays out)
- [ ] Redistribution check for helper binaries

### G2 Module contract and repo skeleton
Summary: Define how a module runs alone, and lay out the repo.
Gate: `module.json` spec written and Nate has approved it.
- [ ] module.json spec
- [ ] Repo skeleton (shell, modules, packaging, workflows, docs)
- [ ] Decide git and remote with Nate

### G3 Pilot modules
Summary: Prove the model with one in-repo tool and one pinned external release.
Gate: A module installs and runs alone from its own release.
- [ ] RegionShot as a module with its own release
- [ ] Region Control pinned in suite.json

### G4 Shell MVP
Summary: A tray app that lists, launches and toggles modules.
Gate: Shell starts, lists modules, launches one, and autostarts.
- [ ] Tray icon and module list
- [ ] Launch, toggle and settings
- [ ] Autostart

### G5 Suite 0.1 installer
Summary: One installer with a checkbox per module.
Gate: Installer installs chosen modules on a clean machine.
- [ ] Build installer from pinned versions
- [ ] CI for suite and modules

### G7 C# rewrites
Summary: Rewrite RegionShot and Cable Listen in C#, no Python, under Nate's supervision.
Gate: Each rewrite matches the old module and replaces it; Nate approves.
- [ ] RegionShot in C#
- [ ] Cable Listen in C#

### G6 Retire old locations
Summary: Point old repos and folders at DeskKit.
Gate: Old repos archived with a README pointer; local paths updated.
- [ ] README pointer in each old repo
- [ ] Archive old repos
- [ ] Update shortcuts and PATH scripts
