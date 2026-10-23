---
name: parity-map
description: Map the context around a module's units into its registries (scenarios, decisions, dependency error cases) after an extract and before any spec. Use when asked to "map <Module>", "update the registries", or when parity registry check reports STALE or GAP.
---

# parity-map

Research phase. You may read the legacy source and everything under `legacy/<Module>/`; you write only
`legacy/<Module>/registry/` and `legacy/<Module>/evidence/` (a hook enforces it). What you learn goes into
the registries with citations, never into a spec, a manifest or code.

1. Ensure `legacy/.phase` contains `map`. If `legacy/<Module>/registry/` does not exist yet, run
   `"${PARITY_BIN:-parity}" registry init legacy/<Module>` (it seeds `dependencies.json` with every `svc:` id of `module.json`
   and never overwrites).
2. Run `"${PARITY_BIN:-parity}" registry check legacy/<Module>`. Read every finding first; they are the work list.
   - `REGISTRY-STALE`: a re-extract moved the module. Run `"${PARITY_BIN:-parity}" diff` on each changed manifest (the
     extract log prints the command) and re-read every scenario, decision and dependency entry that cites
     an id in it.
   - `REGISTRY-GAP`: a `svc:` id has no entry in `dependencies.json`; add it after step 4.
   - `CITE-UNKNOWN`, `EV-STALE`: a citation no longer resolves; find what replaced it.
3. Contract, shared state and composition are in each `manifests/<Unit>.md` (Contract, Shared state, Units
   this unit opens) and in `module.json` (sharedState, composition, dependencies). Start there; open the
   source only for what they do not show.
4. Dependencies: for each `svc:` id, list its error cases as `{ "case": "<lowercase-token>", "reply":
   "<what the service answers>", "fixture": "<name>" }` or with `"cite": [...]` instead of a fixture. A case
   nothing backs is `REGISTRY-UNCITED`. `"${PARITY_BIN:-parity}" registry catalog legacy/<Module>` shows each service's shape,
   callers, error cases and fixture provenance in one page.
5. Scenarios: one per situation worth a test, with an invented title, a `role` and `rights` as lowercase
   tokens, `negative`, the ids it `exercises` (`handler:`, `svc:`, `state:`, ...), and a `cite`. Never copy a
   value from the host (names, numbers, policy or client data): the schema refuses any other field. Leave
   `lastVerified` out until someone has run the scenario against the host. Raise `version` when its meaning
   changes; rules name it as `scn:<id>@<version>`.
6. Evidence: when the manifest lacks a fact you need to cite, record the legacy lines with
   `"${PARITY_BIN:-parity}" evidence add <Unit> <file>:<start>-<end> --note "<what it shows>"` and cite the printed `ev:` id.
7. Decisions: a human answer becomes an entry in `decisions.json` (`id` dec:<words>, `question`, `answer`,
   `owner`, `date` YYYY-MM-DD, `cite`). You record an answer the user gave; you never answer for them. Specs
   then write `decided-by dec:<id>` on a quirk or dropped field and `closed by decision dec:<id>` on a question.
8. Run `"${PARITY_BIN:-parity}" registry check legacy/<Module> --gate` until it is green (the stop hook runs it too, and
   refuses a turn that changed files outside the registry and evidence). Unreferenced or unverified scenarios
   are listed for the user, not deleted.
9. Run `"${PARITY_BIN:-parity}" registry stamp legacy/<Module>`: it records that the registry was reviewed against this
   extract, and refuses while check has errors. Commit `registry/` and `evidence/`.

Stop. Report what changed, the scenarios still unverified or unreferenced, and the questions only a human
can answer. Specs are written in the specify phase, where `legacy/.phase` is `specify` and the source is off
limits again.
