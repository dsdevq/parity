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
- cite: handler:..., method:...     at least one
- given: ...
- when: ...
- then: ...                         must name a control (ctrl:/dyn: id or control name), a svc: or a form:
- fixtures: name, name              optional; each must exist as fixtures/<name>.json
- quirk: preserve|diverge - ..., decided-by <name> <YYYY-MM-DD>   optional
```

Ids containing commas (method parameter lists) are split on top-level commas only.
Hedging words anywhere in given/when/then are errors: probably, likely, assume(d), should, seem(s),
typically, usually, maybe, might, presumably, appears to, "I think".

### No behavior

`| event | reason |`: events whose handler needs no port (selects text, cosmetic paint). An event is
either here or in some rule's `covers`, never both.

### Service calls

`| service | called from | fixtures |`: one row per `svc:` id in the manifest, with at least one fixture
whose `service` field equals the id.

### Open questions

`- none`, or lines of questions. Anything other than `- none` keeps the gate closed, by design: the
spec may be drafted with questions; it may not be implemented with them.

## Fixtures

`fixtures/<name>.json`:

```json
{ "service": "svc:IService.Method", "recorded": "YYYY-MM-DD", "request": {}, "response": {} }
```

How they are recorded is the adopter's business (a headless runner against the old API); the lint only
checks presence and the service id.
