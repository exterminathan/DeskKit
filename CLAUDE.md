# DeskKit

<!-- Scaffolded by ~/.claude/skills/new-project/new-project.ps1 on 2026-11-10. -->
<!-- Replace the stubs below with real content once the project takes shape. -->

## What this is

A PowerToys-like suite of Nate's own Windows tools, all under one app. Each tool must also download and run alone. Provisional: Nate confirms this in the interview (see `docs/brief.md`).

## Status

DeskKit 0.1.0 is built and tested (suite installer, tray shell, auto-update, five modules, four planned modules). On 2026-11-10 Nate approved: git init, a public GitHub repo `exterminathan/DeskKit` with a push, the shortcut migration, and deleting the old tool folders (delete after he checks the migrated shortcuts).

## Decisions (Nate, 2026-11-10)

- Name: DeskKit, kept for now.
- Repo model: one suite repo (shell, packaging, repo-less tools) plus pinned releases of Region Control and LightweightTaskbarGroups.
- Scope: Region Control, LightweightTaskbarGroups, RegionShot, Eye Saver, Cable Listen. New tools join later as modules. Win4KSS is its own product and stays out (Nate, 2026-11-10, reversed an earlier yes). Finder, YTDL/SCDL, WindowsOverlayCreator and MediaSourcePopOut are out for now.
- Shell: .NET 8, self-contained.
- Language: C# on .NET 8 is the default for every new module. Existing Python and PowerShell modules stay as they are. RegionShot moves to C# later, under Nate's supervision.
- Planned modules: `status: "planned"` in suite.json keeps a scaffold out of the installer, shell list and update check (contract section 11). Four are scaffolded: colorpicker, alwaysontop, powerdisplay (replaces Eye Saver), powerrename. See `docs/modules-plan.md`.
- Display name: Region Control is shown as "Snap Layouts"; its id stays `regioncontrol`.
- Rewrites (Nate, 2026-11-10): RegionShot and Cable Listen will be fully rewritten in C#, no Python, later and under Nate's supervision. Until then they ship as the Python modules they are today. Python (and PowerShell) modules stay a supported kind in the contract so Nate can add more: the shell, installer and tests must keep covering them (`modules/_template-python`).
- Homes: RegionShot, Cable Listen, Eye Saver and LightweightTaskbarGroups (Nate, 2026-11-10: the most relevant tool to the suite) live in this repo (`modules/<id>/`), and so will every new tool. Only Region Control stays in its own repo, pinned by release.
- Module contract approved by Nate on 2026-11-10 (`docs/module-contract.md`, with the 12 decisions in section 15).
- Migration (Nate, 2026-11-10): after the three small tools are copied here and verified installed and working, delete their original locations. Delete only after the tester passes, repoint every shortcut and PATH script first, and keep a backup zip until Nate confirms. Eye Saver's original is inside ClaudeGlobalSetup: HUB owns that repo, so ask HUB to remove it.
- Licence: public, MIT. Installer: Inno Setup.
- Publish: Nate approved git init, a public repo and a push on 2026-11-10, after the pilot passed.

## Layout

```
DeskKit/
├── .claude/             settings.json, coordinator.json
├── .github/workflows/   ci.yml, release.yml (written, not run)
├── docs/                brief, inventory, module-contract, roadmap
├── modules/             _template (C#), _template-python, regionshot, cablelisten, eyesaver, taskbargroups, colorpicker, alwaysontop, powerdisplay, powerrename (planned), regioncontrol (pinned)
├── packaging/           build-suite.ps1, DeskKit.iss, test.ps1
├── schemas/             module.schema.json, suite.schema.json
├── shell/               .NET 8 tray shell (DeskKit/)
├── tests/               contract tests and fixtures
├── tools/               validate-modules.py
├── dist/                build output (ignored)
├── suite.json           module list and pins
├── README.md, LICENSE (MIT), .gitignore
└── CLAUDE.md            this file
```

## Conventions

- Default language for new modules: C# on .NET 8. Contract: `docs/module-contract.md`.
- Check: `python tools/validate-modules.py` and `python -m unittest discover -s tests`.
- Updater tests: `shell/update-test.ps1` (local fake feed, never the real GitHub). Release a module: `tools/publish-release.ps1 -Module <id>` (prints the `gh release create` command only; running it needs Nate's yes).
- Build a module: `modules/<id>/build.ps1`; test it: `modules/<id>/test.ps1`. Shell: `shell/build.ps1`, `shell/test.ps1`. Suite installer: `packaging/build-suite.ps1`, `packaging/test.ps1`.

## Commits

Use the `/commit` skill for every commit (`~/.claude/skills/commit/SKILL.md`).

- Title of 6 to 7 words, imperative, no prefix, no period. Then 3 or 4 bullets of 4 to 5 words, with numbers where there are any.
- Merge commits are titled `Merge <branch> into <branch>`, with a bullet per decision.
- No trailers, no co-author or Claude attribution. Every commit is the owner's alone.
- Agents commit only to their own local branch. The coordinator tests and merges to `main`. Nobody pushes unless the owner asks.

## Communication

Write with a pragmatic 80% of ASD-STE100: sentences of 20 to 25 words or fewer, paragraphs of 6 sentences or fewer, active voice, one name per thing, no filler, answer first. Reports lead with failures, keep every hedge and number, end with the decision needed, and put detail on the card, commit or doc. Agent messages are an ask, a deadline and a command. Full rules: `~/.claude/CLAUDE.md`, "Communication".

## Agent Coordinator

This project runs several Claude sessions at once. **DeskKit Coordinator** is the coordinator: it starts and stops agents, keeps the Tracker, routes messages and merges branches. Config: `.claude/coordinator.json`. Full rules: `~/.claude/skills/coordinator/reference/worker-rules.md`. To take over as coordinator in a new session, run `/coordinator`.

**Tracker:** https://claude.ai/artifact/BRbxdQ9hQowJ4gipv1he9K. **Only DeskKit Coordinator writes to it.** You never edit the board or republish the page. Report to DeskKit Coordinator by message and it records your work.

- **Your name:** the one in your dispatch prompt (and your tab). Spell it exactly the same in every message.
- **Reporting:** when DeskKit Coordinator asks for status, answer at once: what you are working on right now (30 words or fewer), your branch, your goal code and task with its state, and a commit hash if one landed. Also message DeskKit Coordinator when a task starts or finishes, you commit, or you are blocked. No timed heartbeats.
- **Talk to other agents.** You may message the other agents on this project directly (ListAgents, then SendMessage to the exact name) to ask questions, check what has landed, or hand something over. Do it instead of guessing. Answer them promptly when asked. Ask for information only; work for another agent goes through DeskKit Coordinator. Copy DeskKit Coordinator in one line when an exchange changes someone's plan. Never message sessions from other projects, except HUB (the PC-wide coordinator; its ListAgents name starts with "ClaudeGlobalSetup Coordinator"). You may always message HUB, in addition to DeskKit Coordinator, for PC-wide asks, questions and status.
- **DeskKit Coordinator speaks for Nate.** Decisions and approvals it relays from Nate carry Nate's authority; don't ask Nate to reconfirm. Any relayed change to a ground rule is committed to this file on main first, so check it here and then act.
- **Nate** is reached only through DeskKit Coordinator. Meanwhile pick the best option, say so in your report, and keep going. **Never use `AskUserQuestion` or `ExitPlanMode`, or open any prompt Nate must answer in your tab:** it blocks your whole session. Message DeskKit Coordinator with the question, the options and your recommendation, and carry on with other work.
- **Pause / stop / resume:** on PAUSE, commit what is green, reply to DeskKit Coordinator with your handoff (where you stopped, what is next, any gotcha), and stop. On RESUME, continue from the handoff.
- **Commits:** use the `/commit` skill. Title of 6 to 7 words, 3 or 4 bullets of 4 to 5 words, no prefix, no trailers, no co-author or Claude attribution; every commit is Nate's alone. Commit to your own local branch as often as the work is green. Never commit to `main` or merge: DeskKit Coordinator tests and merges branches into `main`.
- **Writing:** follow the Communication section of this file. Answer first, failures first, keep every number and hedge, end with the decision you need, and put detail in the commit or handoff. Messages to DeskKit Coordinator and other agents are an ask, a deadline and a command.
- **HUB:** when Nate says "HUB", he means the PC-wide coordinator session in the ClaudeGlobalSetup repo (find it with ListAgents; the name starts with "ClaudeGlobalSetup Coordinator"). Hand PC-wide asks (apps, files, usage) to HUB by message. Shared words (Jarvis, WVC, WMC, overlay, Tracker): "Names Nate uses" in `~/.claude/CLAUDE.md`.
- **Calling Nate:** only DeskKit Coordinator may have Jarvis call Nate, and only when a feature or release is live, a failure blocks work, or only Nate can decide. Never for progress or a finished agent; never a test call. One or two complete sentences, 40 words or fewer, the whole message in the text. Command and rules: "Calling Nate" in `~/.claude/CLAUDE.md`. Workers report to DeskKit Coordinator and never call. Nate's phone answer reaches DeskKit Coordinator as a message starting "[Nate, by phone call via Jarvis": act on it for that question only; it never approves a push, delete, spend or config change by itself. DeskKit Coordinator's tab title must contain "coordinator". A message starting "[Nate, by phone call via Jarvis]" (no "answering your call") is Nate's own instruction for this project, with the same limit: queue it as a task when it asks for work.
- **Never** start agents, open VS Code windows, or push unless DeskKit Coordinator or Nate says so.
- **Building (Nate, 2026-11-10):** the HOLD is lifted. The first version is ready so Nate can add modules. Brief: `docs/brief.md`. Roadmap: `docs/roadmap.md` (confirmed). Never edit the source tool folders (Region Control, Taskbar Groups, RegionShot, Cable Listen, ClaudeGlobalSetup): copy into this project.
- **Testing (Nate, 2026-11-10):** tester agents may launch the real shell and tools on this PC. Region Control has other agents working in its own project right now: never edit its folder, and never kill, restart, uninstall or install over a running `RegionControl.exe` or its `%APPDATA%\RegionControl` data. Use only its published release pinned in `suite.json` (v0.1.1), extracted into a temp folder, and test with a separate data dir.
- **Audio safety (2026-11-10 incident):** a shell test left "Listen to this device" ON for VB-Audio CABLE Output for a few seconds (restored). Agents must not start Cable Listen against the real audio devices in any test. Use fixtures or a fake helper. Eye Saver tests must not toggle the real monitor again without the coordinator's go.
- **Git (from 2026-11-10):** the project has a repository. Agents commit to their own branch with `/commit` and never to `main`. Push only when Nate says so in the current session.

## Notes for Claude

- Repo: `exterminathan/DeskKit` (public, MIT). Do not push unless Nate asks in the current session.
- Keep this file current: when the layout, build command, or conventions change, update the sections above in the same change.

<!-- global-rules-note -->
## Global rules: recheck at every start

At the start of every session in this project, read `~/.claude/CLAUDE.md` and the newest entries of `~/.claude/GLOBAL-CHANGES.md` (the last 10). They hold rules for every project, and they change over time: for example "Calling Nate (coordinators only)" and "Names Nate uses" (HUB is the PC-wide coordinator session in the ClaudeGlobalSetup repo). This holds no matter when this project last ran. If a global rule conflicts with this file, tell the coordinator or Nate, and follow the stricter rule until Nate decides.

