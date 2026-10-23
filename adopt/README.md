# adopt/

Copy into the consuming repo (the one that will hold the new implementation):

- `.claude/skills/parity-*` -> `.claude/skills/` (one skill per phase; each stops at its gate)
- `.claude/settings.json` -> merge the `hooks` block into `.claude/settings.json`
- `hooks/*.sh` -> `legacy/hooks/`

Then create `legacy/.phase` containing one of `extract`, `specify`, `implement`, `bug`. See `docs/adopt.md`.
