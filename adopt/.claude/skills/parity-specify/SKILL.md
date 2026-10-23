---
name: parity-specify
description: Write or extend the parity spec for one form from its manifest, until parity lint is green. Use when asked to "specify <Form>", "write the spec for <Form>", or "port <Form>" before any implementation exists.
---

# parity-specify

Your only input is `legacy/<Module>/manifests/<Form>.md` plus `legacy/<Module>/fixtures/` and, when it
exists, `legacy/<Module>/evidence/<Form>.json`. The legacy source is off limits in this phase (a hook
enforces it). Everything you may cite is printed in the manifest or recorded as an `ev:` id in the
evidence file; every id you write must appear there verbatim.

1. Ensure `legacy/.phase` contains `specify`. If the spec already exists and `parity lint` reports
   `STALE`, run `parity rebind legacy/<Module>/spec/<Form>.md` before anything else: exit 0 means it is
   rebound and you continue at step 7; exit 1 prints the rules whose citations changed or vanished, so
   re-read exactly those in the manifest, fix them, set the hash it printed, and continue at step 7;
   a refusal says which previous manifest is missing, so report that to the user and stop.
   For a new spec, open the manifest .md and copy the `manifest:` hash line.
2. Start `legacy/<Module>/spec/<Form>.md` from `templates/spec.md` (status `draft`).
3. Fields: one row per control the manifest lists with category input, action, grid, column, display or
   unknown, per container that has events, and per `dyn:` entry. Labels come from the control caption or
   its layout item's caption. `dropped` needs a decision from the user, not from you; if you think a control
   is out of scope, write it as `field` and add an Open question.
4. Rules: read each handler and the methods it reaches, in the manifest. One rule per observable
   behavior; a rule may cover several events. `given`/`when`/`then` describe what the legacy code does,
   in user terms, naming controls. Cite the handler and every method the behavior depends on. If a
   handler calls a `svc:` id, name it in `then` and list a fixture. A rule that branches on the reply
   declares `outcomes:` and lists a fixture per outcome by its `variant`, or an `outcome-gap:` line.
5. Constant values and resource texts are in the manifest's Constants and resource texts table and in
   the comment beside their first use in a slice: write the value into `then`, do not ask for it.
   Anything you cannot establish from the manifest goes under Open questions, phrased as a question.
   Never resolve it by assumption and never use hedging words in a rule.
6. Events whose handler needs no port go under No behavior with the reason.
   If the manifest has a Variants section, the rules above describe the base. For each variant, account for
   every delta id: a rule with `variants: <Id>` that covers or cites it, or a row under Variant deltas without
   a rule with the reason. A base rule that does not hold for a variant (it cites a handler the variant
   replaces) gets `variants:` without that variant. The Factories table says which tenant key gets which
   variant; cite the factory method where a rule depends on it.
7. Run `parity lint legacy/<Module>/spec/<Form>.md`. Fix every error. Repeat until `0 error(s)`.
8. Run `parity lint ... --gate` once and report the coverage findings as the list of what is still
   missing (do not force-close Open questions to make it pass).
9. Commit the spec. Tell the user what needs a human: open questions, candidate drops, quirks.

Stop. `status: reviewed` is set by the reviewer, not by you.
