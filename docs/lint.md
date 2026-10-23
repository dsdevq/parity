# Lint codes

`parity lint <spec.md>` runs the structure checks. `--gate` adds coverage and readiness.
`--implement <dir>` adds `COV-TEST` and `COV-TODO`. Output: `path:line CODE message`, sorted; warnings are suffixed
`[warning]`; then a summary line on stderr, `E error(s), W warning(s), fixtures: N (M synthetic)`, counting the
fixtures the spec lists that exist; exit 1 when any error exists. The manifest is found at `<spec dir>/../manifests/<Form>.json`,
fixtures at `<spec dir>/../fixtures/`, debt at `<spec dir>/../extractor-debt.md`, evidence at
`<spec dir>/../evidence/<Class>.json`. Resolving an `ev:` id reads the legacy source: the phase guard
keeps the agent out of it, not the tool.

| Code | Level | Meaning |
|---|---|---|
| GRAMMAR | lint | front matter, section, table or rule-block shape is wrong; unknown kind or disposition; `then` names no observable outcome; event both covered and no-behavior; an empty or repeated `outcomes` entry, an `outcome-gap` that is not `<outcome> - <reason>`, names an undeclared outcome or repeats one; an empty `variants:`; a row under Variant deltas without a rule that has no reason or whose id a rule also describes |
| STALE | lint | front-matter hash differs from the manifest on disk, or the manifest is missing. Nothing else is checked. Remedy: `parity rebind <spec.md>` rewrites the hash when nothing the spec cites changed, else prints the rules to re-read (`docs/spec.md`). |
| CITE-UNKNOWN | lint | an id in `covers`, `cite`, Fields, No behavior or Service calls is not in the manifest; an `ev:` id has no record in `evidence/<Class>.json` |
| CITE-KIND | lint | `covers` holds something other than `evt:`/`dyn:`/a non-code `var:` entry; `cite` something other than `handler:`/`method:`/`ev:`/a `var:` override, handler or ctor entry, or only `ev:` ids |
| EV-STALE | lint | a cited `ev:` record no longer matches the legacy source: the file is gone, the range is past its end, or the lines changed. Names the record, the file, the revision it was recorded at and, when the lines only moved, where they are now |
| EV-INVALID | lint | an evidence file is not valid JSON or lacks `root`/`records`, or a record's `quote` does not hash to its `quoteHash` |
| EV-UNLINKED | lint | an Open questions line is `closed by evidence` but links no `ev:` id |
| ORPHAN-RULE | lint | a rule covers nothing |
| HEDGE | lint | a hedging word in given/when/then |
| DROP-REASON | lint | a dropped Fields row or a quirk without `decided-by dec:<id>` or `decided-by <name> <date>` |
| DECISION-UNKNOWN | lint | a `decided-by dec:` or `closed by decision` id is not in `registry/decisions.json`, or a `closed by decision` line links no `dec:` id (the question then stays open) |
| SCENARIO-UNKNOWN | lint | a rule's `scenarios:` names an id `registry/scenarios.json` lacks, or the module has no readable scenarios file |
| SCENARIO-VERSION | lint | a rule's `scenarios:` entry has no `@<version>`, or one that is not the scenario's current version |
| COV-CONTROL | gate | a manifest control that needs a Fields row has none |
| COV-DYNAMIC | gate | a `dyn:` entry has no Fields row |
| COV-EVENT | gate | an event is neither covered by a rule nor under No behavior |
| COV-HANDLER | gate | a handler is cited by no rule and not fully explained by No behavior (trivial constructors exempt) |
| COV-METHOD | gate, warning | a sliced transitive method is cited by no rule |
| COV-SERVICE | gate | a `svc:` id has no Service calls row or no fixtures listed |
| FIXTURE-MISSING | gate | a named fixture file does not exist or records a different service |
| FIXTURE-INVALID | gate | a fixture file is not valid JSON; a listed fixture has no `service`, a `recorded` that is neither a `YYYY-MM-DD` date nor `synthetic`, or a field of the wrong type |
| FIXTURE-SYNTHETIC | gate | a listed synthetic fixture lacks `shapeVerified: true` or a non-empty `note` |
| FIXTURE-SYNTHETIC | gate, warning | every fixture a rule uses is synthetic; a test built on them must say so. A rule that lists `fixtures:` is judged by them alone; otherwise by its services' rows (the Service calls rows naming it, a `svc:` in `then`) |
| FIXTURE-OUTCOME | gate | a rule's declared outcome has no fixture with that `variant` and no `outcome-gap` line. A rule that lists `fixtures:` is judged by them alone; otherwise by its services' rows |
| COV-RAW | gate | the manifest (base or any variant) has raw statements or diagnostics not acknowledged in `extractor-debt.md`. A raw statement is acknowledged by a line carrying its `file:line`; a diagnostic by a line carrying `CODE: Form.member` (the message's subject) or, for diagnostics without a subject, the full message. The line also needs an owner and a `YYYY-MM-DD`. A bare code acknowledges nothing; one line covers one entry. |
| VAR-UNKNOWN | lint | a rule's `variants:` names no variant of the manifest (or the form has none), or a `var:` id in a rule or in Variant deltas without a rule is not a delta entry |
| VAR-SCOPE | lint | a `var:` id is used in a rule whose scope includes `base` or excludes its variant; a rule whose scope includes a variant cites a base handler or method that variant replaces, or covers an event on a control it removes |
| VAR-UNCOVERED | gate | a variant delta entry is neither covered or cited by a rule scoped to its variant nor listed under Variant deltas without a rule |
| OPEN | gate | Open questions, less the lines closed by evidence or by decision, is not exactly `- none` |
| REGISTRY-STALE | gate | the module has a `registry/` that was not stamped against the current extract (see Registry below) |
| STATUS | gate | status is not `reviewed`, or reviewed-by lacks a name and date |
| COV-TEST | gate + implement | a rule id is in the title of no active test declaration in a `.ts`, `.js` or `.cs` file under the directory. Active: `it(`, `test(`, `describe(` (also `.only`, `.each`) or an xunit `[Fact]`/`[Theory]` whose `DisplayName` or method name (`R_Form_001` for `R-Form-001`) holds the id. A comment, a bare string constant, `it.todo`, `.skip`, `xit`, `xtest`, `xdescribe` or `Skip =` is not coverage. Known limit: the match is a regex over declaration titles, not a parser, so a declaration built dynamically or disabled by other means is not seen, and a passing run is not checked. |
| COV-TODO | implement, warning | an id that `COV-TEST` rejects appears only in a todo or skipped declaration; the message names the file and the form so the placeholder can be found |

Coverage is computed from the manifest. The spec can add rows and rules; it cannot remove what the
extractor found.

## Registry

`parity registry check <module-dir> [--gate]` checks `registry/` against the module's extract, in the same
output format (`docs/adopt.md`, The map phase and the registries). Exit 1 on errors.

| Code | Level | Meaning |
|---|---|---|
| REGISTRY-MISSING | error | no `registry/`, no `module.json`, or one of the three files is missing |
| REGISTRY-STALE | error | a file's `module` stamp is not the current extract revision (a hash over every manifest's hash): a re-extract changed a manifest and the registry has not been reviewed since. `parity registry stamp` clears it once the rest is green |
| REGISTRY-INVALID | error | not valid JSON; a field outside the schema (the schema holds only invented or structural fields, never host data); a missing or mistyped field; an id that is not `scn:`/`dec:` lowercase words; a repeated id; `role`, `rights` or an error `case` that is not a lowercase token; a date that is not `YYYY-MM-DD` |
| DECISION-INVALID | error | a decision entry with a field outside the schema, a missing `question`, `answer`, `owner` or `date`, a date that is not `YYYY-MM-DD`, no `cite`, a malformed id or a repeated one |
| REGISTRY-GAP | error | a `svc:` id of `module.json` has no entry in `dependencies.json` |
| REGISTRY-ORPHAN | error | a `dependencies.json` entry names a `svc:` id the module no longer calls |
| REGISTRY-UNCITED | error | a scenario cites nothing, or an error case names neither a fixture nor a cite |
| FIXTURE-MISSING | error | an error case's fixture does not exist or records another service |
| CITE-UNKNOWN | error | a scenario's `exercises` or any `cite` holds an id that is not in the module's manifests, or an `ev:` id with no record |
| EV-STALE, EV-INVALID | error | a cited `ev:` record, as in spec lint |
| SCENARIO-UNKNOWN | error | a fixture's `scenario` is not in `scenarios.json` |
| SCENARIO-UNREFERENCED | warning, error with `--gate` | no fixture names the scenario and no spec rule lists it under `scenarios:` |
| SCENARIO-UNVERIFIED | warning, error with `--gate` | the scenario has no `lastVerified` date |
