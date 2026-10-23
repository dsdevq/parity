# Spec grammar

A spec is markdown with a line grammar strict enough for `parity lint` to parse. Humans review it in a
pull request; the agent writes it from the rendered manifest and nothing else. `templates/spec.md` is
the blank; `examples/PolicyWizard/spec/PolicyWizardStep3.md` is a complete one.

## Front matter

```
---
form: form:<Class>
manifest: sha256:<16 hex>     # printed at the top of manifests/<Class>.md; STALE when it no longer matches
status: draft | reviewed       # the human sets reviewed
reviewed-by: <name> <YYYY-MM-DD>
---
```

## Sections, in this order

`# <title>` and a `Purpose:` line. Then exactly these `##` sections: **Fields**, **Rules**,
**No behavior**, **Service calls**, **Open questions**.

### Fields

A table `| control | label | kind | binding | disposition | note |`. One row per manifest control of
category input, action, grid, column, display or unknown, per container that has wired events, and per
`dyn:` entry. Disposition is one of `field`, `display`, `layout`, `nonvisual`, `shared:<Component>`,
`dropped`. A dropped row's note carries `decided-by <name> <YYYY-MM-DD>`.

### Rules

```
### R-<Class>-NNN
- kind: validation | enablement | navigation | data | calculation | ui | service | lifecycle
- covers: evt:..., evt:...          at least one; dyn: ids also allowed
- cite: handler:..., method:..., ev:...   at least one handler: or method:; ev: optional
- given: ...
- when: ...
- then: ...                         must name a control (ctrl:/dyn: id or control name), a svc: or a form:
- fixtures: name, name              optional; each must exist as fixtures/<name>.json
- quirk: preserve|diverge - ..., decided-by <name> <YYYY-MM-DD>   optional
```

Ids containing commas (method parameter lists) are split on top-level commas only.
Hedging words anywhere in given/when/then are errors: probably, likely, assume(d), should, seem(s),
typically, usually, maybe, might, presumably, appears to, "I think".

Literal values come from the rendered manifest's **Constants and resource texts** table, `| kind | name |
value | type | declared | other cultures | first use | used by |`, and from the comment the renderer appends
to the first line in each slice that reads one (`// PolicyLimits.ReasonMaxLength = 40`,
`// Captions.lsReasons = "Reasons are kept for audit"`). Write the value in `then` text (the message shown,
the limit checked); never ask it as an Open question. A row is a fact, not an id: the rule still cites
the handler or method that reads it. A value marked "not found in the project's default-culture resx" is
an Open question (and a `RESOURCE-KEY-MISSING` diagnostic).

### No behavior

`| event | reason |`: events whose handler needs no port (selects text, cosmetic paint). An event is
either here or in some rule's `covers`, never both.

### Service calls

`| service | called from | fixtures |`: one row per `svc:` id in the manifest, with at least one fixture
whose `service` field equals the id.

### Open questions

`- none`, or lines of questions. Anything other than `- none` keeps the gate closed, by design: the
spec may be drafted with questions; it may not be implemented with them.

A question is closed one way: by evidence. It stays listed, rewritten as

```
- closed by evidence ev:<Class>/<n>[, ev:...]: <question> - <answer>
```

and stops counting against the gate. Any other wording, `closed` or `answered` included, is still an
open question. A closed line must link at least one `ev:` id (`EV-UNLINKED`
otherwise) and each id must resolve like a cited one. A question a human answers is not closed; the
answer goes into the rule or Fields row it changes and the question line is deleted.

## Evidence

The manifest is what the extractor understood. When a rule needs a fact it lacks (a model class, a
config value, a stored procedure), `parity evidence add <Class> <file>:<start>-<end> --note <text>`
quotes those lines into `evidence/<Class>.json` next to `manifests/` and prints the new `ev:<Class>/<n>`
(`templates/evidence.json` shows the shape):

```json
{ "schemaVersion": "1", "unit": "<Class>", "root": "<legacy root, relative to this file>",
  "records": [ { "id": "ev:<Class>/1", "file": "Services/Dto.cs", "start": 21, "end": 21,
                 "quoteHash": "sha256:<16 hex>", "revision": "git:<sha> | manifest:sha256:<16 hex>",
                 "created": "YYYY-MM-DD", "note": "...", "quote": ["..."] } ] }
```

- `file` is relative to the legacy root (the directory of `project` in the extract config).
- `quoteHash` is sha256 of the quoted lines joined by `\n`, first 16 hex, like the manifest hash; line
  endings never count.
- `revision` is the legacy root's git HEAD, suffixed `+dirty` when the quoted file differs from it;
  when the root is not a git work tree it is the unit's manifest hash, named `manifest:`.
- Ids are numbered one past the highest in the file and never reused. Records are written by the
  command, not by hand: a `quote` that does not hash to `quoteHash` is `EV-INVALID`.

A rule cites a record in `cite:` beside its handler, never in `covers:`. Lint reads the quoted lines from
the legacy root (`PARITY_LEGACY_ROOT` when set, else the file's `root`) and reports `EV-STALE` when the
file is gone, the range is past its end or the lines no longer hash to `quoteHash`, naming where the
quoted lines sit now when they only moved. Re-record with `parity evidence add` and update the citing
rules; the old id stays in the file until no spec cites it.

## Fixtures

`fixtures/<name>.json`:

```json
{ "service": "svc:IService.Method", "recorded": "YYYY-MM-DD", "request": {}, "response": {} }
```

How they are recorded is the adopter's business (a headless runner against the old API); the lint only
checks presence and the service id.
