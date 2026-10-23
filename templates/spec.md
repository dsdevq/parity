---
form: form:<FormName>
manifest: sha256:<copy from the top of manifests/<FormName>.md>
status: draft
reviewed-by:
---

# <FormName> - <what the screen is for>

Purpose: one or two sentences. This is the only free text in the file.

## Fields

| control | label | kind | binding | disposition | note |
|---|---|---|---|---|---|
| ctrl:<FormName>/<field> | <label the user sees> | input | <datasource.member or -> | field | |

<!-- disposition: field | display | layout | nonvisual | shared:<Component> | dropped (note must carry "decided-by <name> <YYYY-MM-DD>")
     Every manifest control of category input, action, grid, column, display, unknown needs a row; dyn: ids too. -->

## Rules

### R-<FormName>-001
- kind: validation
- covers: evt:<FormName>/<field>.<Event>
- cite: handler:<FormName>/<method>, method:<Namespace.Type.Method(params)>
- given: <state before>
- when: <the event, in user terms>
- then: <observable outcome naming a control, a svc: call or a form: shown>
- fixtures: <name>
- quirk: preserve - <what is strange and why it stays>, decided-by <name> <YYYY-MM-DD>

<!-- kind: validation | enablement | navigation | data | calculation | ui | service | lifecycle
     covers: evt: ids this rule accounts for (required, at least one)
     cite: handler: / method: ids the rule is read from (required)
     outcomes: valid, invalid, ... (optional) - each needs a fixture with that "variant", or "- outcome-gap: <outcome> - <reason>"
     variants: base, <Id>  only when the manifest has Variants and the rule holds for some tenants; absent means all
     No hedging words: probably, likely, assume, should, seems, typically, usually, maybe, might.
     Unsure? Put it under Open questions; the gate stays closed until it is answered. -->

## No behavior

| event | reason |
|---|---|
| evt:<FormName>/<field>.<Event> | <why nothing needs porting, e.g. handler only selects text> |

## Service calls

| service | called from | fixtures |
|---|---|---|
| svc:<IService>.<Method> | R-<FormName>-001 | <fixture name> |

## Open questions

- none
