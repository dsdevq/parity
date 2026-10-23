# Lint codes

`parity lint <spec.md>` runs the structure checks. `--gate` adds coverage and readiness.
`--implement <dir>` adds `COV-TEST`. Output: `path:line CODE message`, sorted; warnings are suffixed
`[warning]`; exit 1 when any error exists. The manifest is found at `<spec dir>/../manifests/<Form>.json`,
fixtures at `<spec dir>/../fixtures/`, debt at `<spec dir>/../extractor-debt.md`.

| Code | Level | Meaning |
|---|---|---|
| GRAMMAR | lint | front matter, section, table or rule-block shape is wrong; unknown kind or disposition; `then` names no observable outcome; event both covered and no-behavior |
| STALE | lint | front-matter hash differs from the manifest on disk, or the manifest is missing. Nothing else is checked. |
| CITE-UNKNOWN | lint | an id in `covers`, `cite`, Fields, No behavior or Service calls is not in the manifest |
| CITE-KIND | lint | `covers` holds something other than `evt:`/`dyn:`; `cite` something other than `handler:`/`method:` |
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
| COV-RAW | gate | the manifest has raw statements or diagnostics not acknowledged in `extractor-debt.md` (line with the `file:line` or code, an owner and a date) |
| OPEN | gate | Open questions is not exactly `- none` |
| STATUS | gate | status is not `reviewed`, or reviewed-by lacks a name and date |
| COV-TEST | gate + implement | a rule id appears in no `.ts`, `.js` or `.cs` file under the directory |

Coverage is computed from the manifest. The spec can add rows and rules; it cannot remove what the
extractor found.
