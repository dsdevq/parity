# adopt/

The kit `parity init` installs into the consuming repo (the one that will hold the new implementation). It is
embedded in the binary; nothing here is copied by hand.

- `.claude/skills/parity-*` -> `.claude/skills/` (one skill per phase; each stops at its gate)
- `.claude/settings.json` -> its `hooks` block is merged into the repo's `.claude/settings.json`
- `hooks/*.sh` -> `legacy/hooks/`
- `kit.json` -> says where each of these goes, which `templates/` files are installed, what the phases are and
  what the phase driver tells the agent. Add a skill folder, a hook script or a template and list it there;
  `parity init` and `parity doctor` need no change.

Run `parity init` and then `parity doctor` in the consuming repo. See `docs/adopt.md`.
