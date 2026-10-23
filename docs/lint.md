# Lint codes

`parity lint <spec.md>` runs the structure checks. `--gate` adds coverage and readiness.
`--implement <dir>` adds `COV-TEST` and `COV-TODO`. Output: `path:line CODE message`, sorted; warnings are suffixed
`[warning]`; exit 1 when any error exists. The manifest is found at `<spec dir>/../manifests/<Form>.json`,
fixtures at `<spec dir>/../fixtures/`, debt at `<spec dir>/../extractor-debt.md`, evidence at
`<spec dir>/../evidence/<Class>.json`. Resolving an `ev:` id reads the legacy source: the phase guard
keeps the agent out of it, not the tool.

| Code | Level | Meaning |
|---|---|---|
| GRAMMAR | lint | front matter, section, table or rule-block shape is wrong; unknown kind or disposition; `then` names no observable outcome; event both covered and no-behavior |
| STALE | lint | front-matter hash differs from the manifest on disk, or the manifest is missing. Nothing else is checked. Remedy: `parity rebind <spec.md>` rewrites the hash when nothing the spec cites changed, else prints the rules to re-read (`docs/spec.md`). |
| CITE-UNKNOWN | lint | an id in `covers`, `cite`, Fields, No behavior or Service calls is not in the manifest; an `ev:` id has no record in `evidence/<Class>.json` |
| CITE-KIND | lint | `covers` holds something other than `evt:`/`dyn:`; `cite` something other than `handler:`/`method:`/`ev:`, or only `ev:` ids |
| EV-STALE | lint | a cited `ev:` record no longer matches the legacy source: the file is gone, the range is past its end, or the lines changed. Names the record, the file, the revision it was recorded at and, when the lines only moved, where they are now |
| EV-INVALID | lint | an evidence file is not valid JSON or lacks `root`/`records`, or a record's `quote` does not hash to its `quoteHash` |
| EV-UNLINKED | lint | an Open questions line is `closed by evidence` but links no `ev:` id |
| ORPHAN-RULE | lint | a rule covers nothing |
| HEDGE | lint | a hedging word in given/when/then |
| DROP-REASON | lint | a dropped Fields row or a quirk without `decided-by <name> <date>` |
| COV-CONTROL | gate | a manifest control that needs a Fields row has none |
| COV-DYNAMIC | gate | a `dyn:` entry has no Fields row |
| COV-EVENT | gate | an event is neither covered by a rule nor under No behavior |
| COV-HANDLER | gate | a handler is cited by no rule and not fully explained by No behavior (trivial constructors exempt) |
| COV-METHOD | gate, warning | a sliced transitive method is cited by no rule |
| COV-SERVICE | gate | a `svc:` id has no Service calls row or no fixtures listed |
| FIXTURE-MISSING | gate | a named fixture file does not exist or records a different service |
| FIXTURE-INVALID | gate | a fixture file is not valid JSON |
| COV-RAW | gate | the manifest has raw statements or diagnostics not acknowledged in `extractor-debt.md`. A raw statement is acknowledged by a line carrying its `file:line`; a diagnostic by a line carrying `CODE: Form.member` (the message's subject) or, for diagnostics without a subject, the full message. The line also needs an owner and a `YYYY-MM-DD`. A bare code acknowledges nothing; one line covers one entry. |
| OPEN | gate | Open questions, less the lines closed by evidence, is not exactly `- none` |
| STATUS | gate | status is not `reviewed`, or reviewed-by lacks a name and date |
| COV-TEST | gate + implement | a rule id is in the title of no active test declaration in a `.ts`, `.js` or `.cs` file under the directory. Active: `it(`, `test(`, `describe(` (also `.only`, `.each`) or an xunit `[Fact]`/`[Theory]` whose `DisplayName` or method name (`R_Form_001` for `R-Form-001`) holds the id. A comment, a bare string constant, `it.todo`, `.skip`, `xit`, `xtest`, `xdescribe` or `Skip =` is not coverage. Known limit: the match is a regex over declaration titles, not a parser, so a declaration built dynamically or disabled by other means is not seen, and a passing run is not checked. |
| COV-TODO | implement, warning | an id that `COV-TEST` rejects appears only in a todo or skipped declaration; the message names the file and the form so the placeholder can be found |

Coverage is computed from the manifest. The spec can add rows and rules; it cannot remove what the
extractor found.
