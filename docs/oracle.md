# The oracle layer: record, replay, diff, verify

Every other check parity makes is reading-based: the manifest makes the reading of the legacy code complete
and cited, and a person who knows the business marks a spec `reviewed`. Without that person nothing in the loop
can say that a correct-looking port is wrong. The oracle layer adds a check that needs no reader: the legacy
client's backend calls, recorded once per scenario from the host's own per-call log, are the expected output,
and the new client's calls for the same scenario are diffed against them. A scenario is verified when the diff
is empty and the scenario's tests are green, computed by the tool, never asserted.

```
person runs the scenario in the legacy client, host log on
        │
        └──parity oracle export──> legacy/<Module>/recordings/<scenario>/   (record phase; redacted, sealed, read-only after)
                                              │
new client's tests for the scenario ──────────┤
        │                                     │
        └──parity oracle verify──> capture ──diff──> work list by call and field, or "verified" + lastVerified
                                                      (implement phase; repeat until empty)
```

Behaviour that never reaches the host (visibility, enabling, step gating, client-side validation) stays with
the spec and the manifest. A legacy refusal shows up in a recording as a call that never happens.

## Layout

```
legacy/<Module>/
  recordings/<slug>/recording.json      the ordered index (category, type, sequence, write), provenance, redaction count, seal
  recordings/<slug>/calls/NNN.request.<xml|json|txt>, NNN.reply.<...>, NNN.dataset.<xml|json>
  recordings/normalization.json         the normalization rules (one file per module)
  recordings/oracle.json                the oracle settings: writes, redact, replay (one file per module)
  replays/<slug>/                       the new client's last capture, same format; rewritten by every registry `diff --capture` and `verify` (the standalone diff writes nothing)
  verified/<slug>.json                  written only by parity oracle verify
```

`<slug>` is the scenario id without `scn:` (`scn:rider-added` is `rider-added`). The scenario must exist in
`registry/scenarios.json` (written in the map phase, or by hand: see What each command needs); export refuses one that does not.

Commit `recordings/` (after reading it, see Redaction) and `verified/`. `replays/` is regenerated; commit it or
ignore it as you like. The raw dumps (the log rows before redaction) never belong in the repo.

## What each command needs

None of the oracle commands needs the extractor's output (`module.json`, the manifests). Most need a registry, which the map
phase writes from that output but which can be written by hand, so a team can record and diff before (or without) the map phase.

| Command | Registry it reads | Extractor output |
|---|---|---|
| `oracle export` | `scenarios.json`: the scenario's `id` and `version` | not read |
| `oracle diff <module-dir> <scn:id>` | `scenarios.json` (the recording must be of the scenario's current version); `decisions.json` once `normalization.json` or `oracle.json` sets anything; `dependencies.json` only for the report's "legacy client" line (`wire`) | `module.json` only for the callers on that line; without it the line names the svc: id alone |
| `oracle diff --recording <dir> --capture <dir-or-rows>` | none | not read |
| `oracle verify <module-dir> <scn:id>` | as the registry diff, and it writes the scenario's `lastVerified` into `scenarios.json` | not read |
| `registry check` (its `RECORDING-*`, `NORM-*`, `ORACLE-*` and `SCENARIO-VERIFY-UNBACKED` findings) | all three files | required: without `module.json` it stops at `REGISTRY-MISSING` |

Without a registry, only the standalone diff runs. Without the extractor's output, `registry check` does not report a broken
seal, but `diff` and `verify` check the seal themselves and refuse.

**The minimal registry.** The oracle commands read only the fields named above; the schema of each file (`docs/adopt.md`, The
map phase and the registries) still applies, and `registry check` holds them to it once the extract exists. For one scenario, with no normalization
rule and no oracle settings:

`registry/scenarios.json`:

```json
{ "module": "unreviewed", "scenarios": [
  { "id": "scn:rider-added", "version": 1, "title": "an agent adds a rider to a policy", "role": "agent", "rights": [],
    "negative": false, "exercises": [], "cite": [] }
] }
```

`registry/decisions.json`: `{ "module": "unreviewed", "decisions": [] }`. `registry/dependencies.json` (optional for the oracle):
`{ "module": "unreviewed", "dependencies": [] }`.

`module` is the extract revision the registry was reviewed against; the oracle commands do not read it, and `parity registry init`
and `stamp` write the real one once the extract exists. `exercises` lists the `handler:`, `svc:` and `state:` ids the scenario runs
through: the oracle does not need them, `registry check` requires at least one. Each normalization rule and the oracle settings then
need a decision, as below:

```json
{ "id": "dec:session-id-differs-every-login", "question": "The session id is different on every login. May the diff ignore its value?",
  "answer": "Yes. The host issues it per login.", "owner": "alex", "date": "2026-11-03", "cite": ["norm:session-id"] }
```

## The host log

The tool reads a dump of the host's per-call log table, one row per call, as JSON lines (one object per line)
or a JSON array. Keys are case-insensitive; columns the format does not name are never read, so they never reach
a recording.

| Column | Required | Meaning |
|---|---|---|
| `category`, `type` | yes | the wire call; together `Category/Type`, the name the diff and the `wire` mapping use. No `/` in either |
| `request` | yes | the logged call body: XML, JSON or text |
| `reply` | no | the reply column (status, a generated key) |
| `dataset` | no | the serialized dataset of a data read: `DataSet.WriteXml` output (with or without an inline schema, or a diffgram) or a JSON object of tables, each an array of row objects |
| `error` | no | the logged error |
| `time` | no | ISO 8601; rows of several dumps are merged by it when every row has one, else kept in the order given |
| `session` | no | selects one run (`--session`); never written to a recording |
| `durationMs` | no | kept in the index for information, never compared |

Your own query produces the dump (the same way your own runner records fixtures): select the log rows of the run
and alias the columns to these names. A login split across a shared pre-login log and a per-tenant log is two
dumps passed together (`--log shared.jsonl,tenant.jsonl`).

## Record (phase `record`, a person)

```sh
parity oracle export legacy/<Module> scn:<id> --log <dump.jsonl>[,<dump2.jsonl>] --session <id>[,<id>] \
    --recorded-by <name> --environment <env> --legacy-build <build> [--from <time>] [--to <time>] [--replace]
```

- **Selective.** A session (`--session`) or a time window (`--from`, `--to`) is required; filters intersect. One
  run of one scenario, never a whole log.
- **Writes.** `writes` in `recordings/oracle.json` (Oracle settings, below) lists the calls that change host state as
  `Category/Type` globs; each call in the index carries `write` from it.
- **Redaction.** `redact` lists rules: `target` (`request`, `reply`, `dataset`, `error` or `any`), `field`
  (a field path glob, below) and an optional `match` (a regular expression; only its matches are replaced). A
  redacted value becomes `redacted:<8 hex>`, an HMAC of the value under `PARITY_REDACT_KEY`. The key keeps the
  pseudonyms of a recording and a replay equal, so a redacted field is still compared, and keeps them from being
  reversed by anyone without it (a plain hash of a date or an amount is reversed by trying every value). Export
  refuses rules without the key. Keep the key out of the repo and use the same value wherever recordings and
  replays are exported. A value no rule names stays as the host logged it: export prints a reminder to read the
  files before committing.
- **Datasets are checked.** Every row's dataset must be a serialized DataSet (XML, or a JSON object of tables), with or
  without redaction rules; export, and the replay import, name the row and the call when one is not.
- **Provenance.** `recording.json` carries the date, environment, legacy build, who recorded, the dump file names,
  the row count and how the rows were selected. Never the session id or the log's user column.
- **Sealed.** The index holds a hash of every file (line endings read as LF, so a checkout that converts them is not an
  edit) and a seal over the index. `diff` and `verify` refuse a recording
  whose seal no longer matches and name the edited, missing or added file; `parity registry check` reports it as
  `RECORDING-TAMPERED`. Restore it from git or record again.
- **Only in `record`.** Export refuses in any other phase (it runs where `legacy/.phase` is `record` or absent), and
  an existing recording is replaced only with `--replace`.

The `parity-record` skill walks an agent through helping the person; the person runs the legacy client.

## Field paths

A request or reply is flattened to leaves:

- XML: `Root/Child/Leaf`; the second and later siblings of one name get `[n]` (`Items/Item`, `Items/Item[2]`), so a
  single element on one side pairs with the first of several on the other; an attribute is `@name`; namespaces are dropped.
- JSON: `a/b/c`; every array element is indexed (`items[1]/id`).
- Anything else: one leaf, `(body)`.
- A dataset: `Table/Column` per row. In an XML dataset, a same-name sibling group that repeats below a row (`Items`
  holding thousands of `Item`) is a nested table of its own, named by its element path with the indexes removed
  (`Policy/Items/Item`); its rows are taken out of the row they sit in, wherever they sit, and a row element with no
  child element has its text as the column `(text)`. A flat `DataSet.WriteXml` has no such group, so its tables are
  the root's children, as before. A JSON dataset is tables of row objects by format and has no nested tables.
  Redaction and normalization globs see the same paths either way (`Policy/Items/Item/Code`).

A glob (in `redact`, `normalization.json`) matches the path with its indexes removed: `*` is any run of characters
inside one segment, `**` any number of segments (`**/SessionId` matches `GetPolicy/SessionId` and
`SaveRider/Header/SessionId`).

## Replay and diff

```sh
parity oracle diff legacy/<Module> scn:<id> [--capture <rows.jsonl>] [--max-call-mb <n>] [--json]
```

`--capture` takes the host log rows of one new-client run (all of them: the replay command's output is one run),
imports them into `replays/<slug>/` through the same redaction as a recording, and diffs. Without it, the last
replay is diffed. `--max-call-mb` (default 64, `0` for no limit) bounds one side of one call: its request,
reply and dataset files together. A pair over it on either side is not compared and is one `call not compared`
difference that names the call, both sizes, the limit and the flag, so the diff never hangs on a payload nobody sized
and never passes over one in silence. Exit 0 when there is no difference, 1 when there is, 3 when the diff is refused
(a broken seal, a rule without a decision, a scenario the registry lacks, a recording made at another version of the
scenario). It never marks anything verified.

**Standalone.** `parity oracle diff --recording <recordings/<slug>/> --capture <replays/<slug>/ | rows.jsonl>` runs the same
diff with no registry read and nothing written:

- `--recording` is a sealed recording folder (the output of export, wherever it now sits); a broken seal is refused as above.
- `--capture` is a sealed replay folder or the host log rows of one new-client run. Rows are imported through the same redaction
  into a temporary folder, which is deleted after the comparison: `replays/`, `verified/` and `registry/` are not touched.
- The oracle settings and the normalization rules are the recording's own: `oracle.json` and `normalization.json` in the folder
  that holds it (`recordings/` in a module). The redaction, the `writes` and the masking are therefore the registry diff's. A rule
  or a settings file that is not valid is refused; the decision behind each is not checked, since that needs `decisions.json`.
- The scenario's version is not checked (it needs `scenarios.json`), and the report has no "legacy client" line (it needs
  `dependencies.json`). Its third line says so: `standalone diff: no registry read, ...`.
- `--max-call-mb` bounds a call as above.
- Exit codes are the registry diff's: 0 no difference, 1 a difference, 3 refused. It never marks anything verified: only `verify`,
  which needs the registry and every decision, does.

**Matching.** Calls are paired by `Category/Type` and their order among the calls of that key. When one side has
fewer calls of a key, the pairing is an order-preserving alignment that skips the calls whose requests fit worst,
so one skipped call does not shift every pair after it. Reads may come in any order between two writes; a paired
call that changes sides of a paired write is a `call order` difference.

**Comparison**, per pair, after normalization:

| Level | Difference |
|---|---|
| call | a call only the legacy client makes (`missing call`) or only the new one makes (`extra call`); a pair over the per-call limit (`call not compared`) |
| order | a call made before a write by one client and after it by the other |
| request, reply | a changed value (`request differs`), or a missing or extra field. Leaves missing under a node the other side lacks are one `block missing` with the field count, not one line per field |
| error | the logged error text differs |
| dataset | only the tables the new client's reply holds, because the table filter travels in a header the log does not keep: a table only the new reply has, a row count, a missing or extra column, and per column the number of rows that differ with the first one shown (by where it sits, `Policy[1]/Items[2]/Item[17]/Code`). A nested table is compared with its top-level table, and a group that repeats on either side is a table on both; one the new reply lacks under a top-level table it holds is a row count to 0. Legacy top-level tables the new client does not ask for are listed under "not compared" and kept in the verification. A capture whose reply holds no dataset (or no table) where the recording has one is a `dataset missing` difference: nothing was compared |

**The report** reads without domain knowledge: a summary line (`scn:x: 6 recorded call(s), 5 replayed, 3
difference(s)`), the recording's provenance and the rules applied (with how many values each masked), then one
numbered entry per difference with the call, the field, the legacy and the new value, and where the legacy client
makes the call. That last line comes from `wire` in `registry/dependencies.json`: a dependency entry lists the wire
calls its `svc:` id makes (`"wire": ["Product/GetLimits"]`), and the report prints the svc id with its callers from
`module.json` (handler and argument text). Without a mapping it says which entry to add it to. The mapping is
knowledge, so the map phase writes it.

**Per call, as JSON.** `--json` prints the result for a program instead of the report: one JSON document on stdout,
nothing else. A repo that keeps its own per-call state (`none`, `open`, `not-compared`) reads it instead of
parsing text. The exit codes stay 0, 1 and 3; a refused diff (exit 3) prints nothing on stdout, its reason goes to
stderr as without `--json`. Every value is the report's: read from the redacted recording and replay, after
normalization. It works on both forms of `oracle diff`, the registry one and the standalone one.

```json
{
  "schema": 1,
  "scenario": "scn:rider-added",
  "calls": [
    { "recordedSeq": 1, "captureSeq": 1, "call": "Auth/Login", "notCompared": [], "differences": [] },
    { "recordedSeq": 2, "captureSeq": 3, "call": "Policy/GetPolicy", "notCompared": ["Audit"], "differences": [] },
    { "recordedSeq": 4, "captureSeq": null, "call": "Product/GetLimits", "notCompared": [],
      "differences": [ { "level": "call", "kind": "missing", "path": null, "legacy": null, "new": null,
                         "detail": "the legacy client makes this call; the new client never does" } ] },
    { "recordedSeq": 5, "captureSeq": 4, "call": "Policy/SaveRider", "notCompared": [],
      "differences": [
        { "level": "request", "kind": "block missing", "path": "SaveRider/Audit",
          "legacy": "SaveRider/Audit/Reason = \"client request\"; SaveRider/Audit/Channel = \"desk\"", "new": null,
          "detail": null },
        { "level": "request", "kind": "changed", "path": "SaveRider/Rider/EffectiveDate", "legacy": "2024-01-01", "new": "2026-11-03",
          "detail": null } ] },
    { "recordedSeq": null, "captureSeq": 6, "call": "Audit/Ping", "notCompared": [],
      "differences": [ { "level": "call", "kind": "extra", "path": null, "legacy": null, "new": null,
                         "detail": "the new client makes this call; the legacy client never does" } ] }
  ]
}
```

- `calls`: every recorded call in recorded order, a call that matched with no difference included (`differences`
  empty), then every capture call no recorded call pairs with (`recordedSeq` null). `captureSeq` is the capture
  call it was paired with, null when the new client never makes it.
- `level`: `call`, `order`, `request`, `reply`, `error` or `dataset`. `kind`: `missing` (the legacy side has it, the
  new side does not), `extra`, `changed`, `block missing` and `block extra` (a missing or extra subtree), or `reordered`
  (level `order`).
- `path`: the field path (Field paths, above); for a dataset, `Table/Column`, `Table[row]/Column` or `Table rows`;
  `error` for the error; null for a whole call, an order difference or a whole missing dataset.
- `legacy`, `new`: the value on each side as text, null where that side has none (a missing field's `new`, an error
  only one client got). A missing or extra value is the text the report prints (a single
  field's value in quotes, a block's leaves as `path = "value"` joined by `; `).
  A dataset difference carries the report's words where there is no single value (`tables Policy, Audit`, `3 row(s)`).
- `detail`: the report's explanation line, or null. `notCompared`: the legacy reply tables the pair did not compare.
- `schema` changes only when a consumer would break (a field removed, renamed or retyped, a value with a new
  meaning); an added field keeps it.

## Normalization rules

`legacy/<Module>/recordings/normalization.json` (a template is in `legacy/templates/normalization.json`):

```json
{
  "version": 1,
  "rules": [
    { "id": "norm:session-id", "decision": "dec:session-id-differs-every-login", "scope": "global",
      "target": "any", "field": "**/SessionId", "match": null }
  ]
}
```

- `scope`: `global`, `call:<Category>/<Type>` (a glob) or `scn:<id>`.
- `target`: `request`, `reply`, `dataset` or `any`. `field` and `match` as in redaction; a masked value becomes
  `<norm:id>` on both sides. Presence is still compared: a rule hides a value, never a missing field.
- Every rule names a `dec:` entry of `registry/decisions.json`, and that decision must cite the rule's id. A rule
  without one is `NORM-UNDECIDED` and the diff refuses to run. The decision's question is the one a person can
  judge without domain knowledge ("the value is different every session: may the diff ignore it?").
- `version` is an optional label. A verification records the file's hash, so any change to the rules leaves the
  scenarios verified under the old ones unbacked (`SCENARIO-VERIFY-UNBACKED`, below).

Normalization is the main way to game a diff ("ignore the field that differs"), which is why the file sits under
`recordings/`: the guard keeps it read-only outside the record phase, and a decision with an owner backs every rule.

## Oracle settings

`legacy/<Module>/recordings/oracle.json` (`replay`, `writes`, `redact`; a template is in `legacy/templates/oracle.json`)
decides what a verification trusts: the command whose capture is compared, which calls order the comparison, which
values never reach a recording. It is held exactly like `normalization.json`: the file sits under `recordings/`, so
the guard keeps it read-only to the agent outside the record phase (the agent cannot loosen the settings and then
cite a decision of its own), and a canonical hash of the parsed settings (formatting does not count) must be cited
by a `dec:` entry of `registry/decisions.json` as `oracle:sha256:<16 hex>`; without one `ORACLE-UNDECIDED` refuses
`diff` and `verify` and fails `registry check`, and the message names the hash to cite. The hash is recorded in each
verification, so changing the settings leaves verified scenarios unbacked until they are verified again. A key
outside `replay`, `writes` and `redact`, or a file that is not valid settings, is `ORACLE-INVALID`. An absent or
empty file needs no decision. `extract.config.json` no longer holds an `oracle` section.

## Verify

```sh
parity oracle verify legacy/<Module> scn:<id> [--max-call-mb <n>]
```

1. Runs `replay` from `recordings/oracle.json` with `sh -c` (Git Bash's `sh` on Windows, as the hooks),
   from the current directory, with `PARITY_SCENARIO`, `PARITY_CAPTURE` (a temporary file) and `PARITY_MODULE_DIR`
   set; `{scenario}`, `{capture}` and `{module}` in the command are replaced by the same values, quoted. The command
   is the scenario's tests: the new client runs the scenario against the local host with logging on, and the
   command writes the host log rows of that run to `$PARITY_CAPTURE`. Its exit code is the test result.
2. A non-zero exit is "not verified: the tests are not green". A green run that left no capture is refused.
3. The capture is imported into `replays/<slug>/` (redacted; the raw file is deleted) and diffed.
4. Only an empty diff writes `verified/<slug>.json` (scenario version, date, recording seal, rules hash, oracle settings hash,
   replay seal, call count, the legacy reply tables not compared) and sets the scenario's `lastVerified` in `registry/scenarios.json`, editing that one
   value in place so the file keeps its layout.

Exit 0 verified, 1 not verified (red tests or a difference), 3 refused. Verify reads its command from the settings
file only: there is no flag to run something else. `--max-call-mb` is the diff's limit (above); a call over it is a
difference, so it can only keep a scenario from being verified, never verify one.

## Guard rails

| Rail | Mechanism |
|---|---|
| Recordings are read-only to the agent outside record | the guard refuses, in every phase but `record`, a write tool aimed under `legacy/<module>/recordings/`, and a shell word or MCP value that resolves there (also a bare `recordings` after a `cd`, and such a path behind a variable, a command substitution or a brace expansion); export refuses outside `record`; the seal catches what a hook cannot see |
| A normalization rule needs a human decision | `NORM-UNDECIDED` refuses the diff and fails `registry check`; the rules file is under `recordings/`, so it changes only in `record` |
| The oracle settings need a human decision | `recordings/oracle.json` is under `recordings/`, so it changes only in `record`; `ORACLE-UNDECIDED` refuses the diff and verify and fails `registry check` until a `dec:` entry cites the settings' hash; a change unbacks every verification |
| Only the diff marks a scenario verified | `verified/` is refused to every tool call in every phase, `record` included; for a recorded scenario `registry check` refuses a `lastVerified` that a current verification (same date, recording, rules, oracle settings and scenario version) does not back: `SCENARIO-VERIFY-UNBACKED` |
| Reports are readable without domain knowledge | the report format above; every difference is a call, a field and two values |

`Read`, `Grep` and `Glob` may read recordings in every phase. A shell command that names either folder is refused
whatever it does, `git add` included: stage the module folder (`git add legacy/<Module>`) instead. The guard is for a cooperative agent: what a program
does with a path it builds itself is outside it, which is why the seal and the registry check exist too. A shell word under a module folder, or after a `cd`, that holds `$`, a backtick, `%x%` or a brace is refused in every phase, since the guard cannot see where it resolves.

## State and the contract the tool expects

Write scenarios change the database. The tool does not reset or snapshot host state: the replay command owns the
starting state. Its contract: each run of the replay command starts the scenario from a state equivalent to the
one the recording started from (a fresh entity created by the test's setup, a reset script, or a restored
snapshot), so that the same requests get the same replies. A reply difference on a read after a write is either
the new client's doing or a state the command did not reset; the request side tells them apart, since requests do
not depend on the database. Generated keys that differ per run are normalization candidates, decided like any rule.

For a read-only screen an empty request diff proves the new client makes the same calls with the same fields;
whether it shows the reply the way the legacy client does is still the spec's to say.

## Design decisions (issue 36's open questions)

1. **State.** Out of scope for the tool, owned by the replay command, contract above. A reset or snapshot mechanism
   is specific to each host and database; building one into parity would bind it to one stack, and the command
   already is the adopter's code. Revisit when two adopters need the same mechanism.
2. **Matching.** Category + type + order within the key, with an alignment when the counts differ, reads free
   between writes and nothing across a write. It is the simplest pairing that survives an independent read moving
   (which UI frameworks do) without hiding a read that moved across a save, the case where order matters.
3. **Capturing the new client's side.** The same host log, imported through the same export and redaction. Capture
   in the new client's adapter would compare what the adapter believes it sent with what the host logged for the
   legacy client: two formats and two serializers, and every difference between them a false positive.
4. **Normalization rules.** One JSON file per module with `global`, per-call and per-scenario scopes,
   every rule tied to a `dec:` entry that cites it. One file keeps every exception in one reviewable place; the
   hash binds each verification to the rule set it used.
5. **Reads vs writes.** Writes are named in the oracle settings (`writes`) and recorded in the index; they order the
   comparison. Reads are compared on their request and on the dataset tables the new client asks for; mapping a
   reply onto the screen stays with the spec. No snapshot/restore is built (decision 1).
6. **Home.** Inside parity. The layer reuses the registry (scenarios, decisions, dependencies and `module.json`
   callers for the report), the phase file, the guard hook and the `ev:`-style provenance discipline; a sibling
   tool would duplicate all of them or reach into parity's files anyway. It is one more command group (`parity
   oracle`), not a new process.

## Design decisions (issue 39)

1. **The standalone diff checks no decision.** A normalization rule hides a value, which is why the registry diff refuses a rule
   without a `dec:` entry. The standalone diff has no `decisions.json` to check, and refusing every rule would make it useless on a
   recording whose session ids differ per login. It applies the recording's rules, says on its third line that their decisions
   were not checked, and marks nothing verified. A verification still needs `verify`, which refuses an undecided rule.
2. **Rules and settings come from beside the recording, not from flags.** `oracle.json` and `normalization.json` sit in
   `recordings/` next to the recording they belong to, where the guard keeps them read-only outside the record phase. A flag naming
   another rules file would let a diff use rules nobody recorded with the scenario.
3. **`--recording` takes a sealed folder, not log rows.** The legacy side is the expected output: export selects one run, redacts
   it and seals it, and the standalone diff keeps that. A repo with no registry makes its recording with export and the minimal
   registry above. `--capture` takes rows as well as a replay folder, because the new client's capture is regenerated every run.
4. **Nothing is written.** A standalone capture is imported into a temporary folder: a `replays/<slug>/` it wrote would be read by
   the registry diff as its last replay, from a run the registry never saw.
5. **No `parity oracle init`.** Issue 39's second ask (a command that writes the minimal registry) is not built: the minimal
   registry is documented above, and the standalone diff covers the case that needs no registry at all. Revisit when an adopter
   writes the same minimal registry by hand twice.

## Design decisions (issue 38: a large nested dataset)

The diff ran for minutes on a 5.5 MB dataset of 128,849 elements (a wrapper root, `Items` nodes holding thousands of
same-name siblings): every root child was one row whose columns were all the leaf paths below it, about 100,000,
each looked up in a list of the other side's columns, and each element's index was found by rescanning its
siblings. Both are linear now: a sibling group is indexed in one pass, a table's columns are sets and each row is
read once. A generated dataset of that shape (`Issue38Tests`) diffs in about 2 s, and 7 MB in about 5 s.

- **`dec:oracle-nested-dataset-tables`.** Q: should a nested dataset be compared per `Table/Column`, as designed, or
  as a path tree? A: per `Table/Column`, extended: each same-name sibling group that repeats is a table keyed by its
  element path with the indexes removed. The tables model is what the report and "not compared" are built on, and a
  path tree of a reply with thousands of rows reports every shifted row as its own changed field; tables keep it to
  a row count and one line per column. The key has no indexes, so groups under different parents (`Items[1]/Item`,
  `Items[2]/Item`) are one table in document order, as a `DataSet` nested relation is; the report still names the
  first differing row by its exact element. Repetition is decided on both sides together: a group with one row on
  one side and several on the other would otherwise be columns on one side and a table on the other. A flat
  `WriteXml` has no repeating group below a row, so its comparison is unchanged.
- **`dec:oracle-per-call-size-limit`.** Q: a per-call time limit or a size limit? A: size, `--max-call-mb`, default
  64 (more than ten times the reported reply, which now diffs in seconds). A time limit makes the result depend on
  the machine: the same recording and capture could verify on one runner and not on a slower one. A size limit is
  the same everywhere, is read from the file sizes before the call's content is parsed, and with the diff linear it
  bounds the time. A call over it is a difference (exit 1) with the flag in its message, never a skip.

## Design decisions (issue 40, the JSON result)

1. **Unpaired capture calls are listed too**, after the recorded calls, with `recordedSeq` null, so every
   difference the report holds is in the JSON and a consumer can count extra calls without reading the report.
2. **Kinds.** The issue named `block missing`; its mirror `block extra` and the order level's `reordered` follow the
   same words.
3. **Values are text or null.** Absent is null, never the report's `(absent)` or `(no error)`. A block keeps the
   report's one-text value in `legacy`/`new`.
4. **Per call, the tables not compared** (`notCompared`): the input a consumer needs to say `not-compared`. Nothing
   about the recording, replay or rules travels with the result; a field added later keeps `schema`.
5. **A refusal prints nothing on stdout.** Exit 3 with the reason on stderr, as without `--json`: a consumer never
   reads a partial document.
6. **Only `diff`.** `verify` keeps writing `verified/<slug>.json` on an empty diff; a consumer that needs the per-call
   result of a verify run diffs the replay it left (`parity oracle diff ... --json` without `--capture`).

## Known limits

- One request or reply format per call: XML, JSON or text. Text is compared whole.
- XML namespaces are dropped from paths; two elements that differ only by namespace collide.
- A nested table's rows are paired by position, as a top-level table's are: one row removed early in a group of
  thousands shifts the rest, and each column reports how many rows differ with the first one shown.
- The phase model: the fix step of the loop runs in `implement`, where the guard refuses code-graph MCP tools. The
  report's "legacy client" line (svc id, handler and arguments from the manifest) is what the agent has there; a
  field the manifest does not explain is a question for the map phase, not a code-graph lookup.

## Follow-ups (from the v0.2.2 re-evaluation, not part of this layer)

- Host-side logic: server classes behind the service boundary get no rules; recordings cover their observable effect.
- Page or container scope for wizard pages inline in one large form class.
- A test-data catalog: which entities and users work for which scenario (the registry refuses host values).
- A subsystem-wide scan for orientation.
- A place for the code graph in the phase model (see Known limits).
- Recorded replies as fixtures with real provenance (#24).
