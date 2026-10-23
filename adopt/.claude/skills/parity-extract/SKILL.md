---
name: parity-extract
description: Extract one legacy WinForms module into parity manifests. Use when asked to "extract <Module>", "re-extract", or before any spec work on a module that has no manifests yet.
---

# parity-extract

Deterministic phase. You run scripts and read their output; you do not interpret the legacy code.

1. Ensure `legacy/.phase` contains `extract`.
2. If this module was never probed: `"${PARITY_BIN:-parity}" probe <csproj from legacy/extract.config.json> --forms '<glob>'`.
   Read the shapes list once. Do not analyse it further; it exists for issue reports.
3. Run `"${PARITY_BIN:-parity}" extract --config legacy/extract.config.json --module <Module> --forms '<glob>'`.
4. Read the summary table. For every form:
   - `raw > 0` or `diag > 0`: open `legacy/<Module>/manifests/<Form>.md`, sections Raw and Diagnostics.
     - `REF-MISSING` / `TYPE-UNRESOLVED`: the config lacks a reference; fix `references` or `selfAssembly` and rerun.
     - anything else: add a line per entry to `legacy/<Module>/extractor-debt.md` (location or code, what it is, `owner <name>`, `until <YYYY-MM-DD>`), and tell the user to file an extractor-hole issue with the Raw lines and the probe shapes. Do not attempt to work around the hole in a spec.
5. Commit `legacy/<Module>/manifests/`, `module.json`, and `extractor-debt.md` if created. On a re-extract,
   every spec of a changed form is `STALE`; say so in the report (`parity rebind` is the specify session's first step).
6. Report: forms extracted, counts, files loaded, peak RSS, and which service-call ids now need fixtures (`module.json` -> `serviceCalls`).

Stop. Do not start a spec in this session.
