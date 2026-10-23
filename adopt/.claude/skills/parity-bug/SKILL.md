---
name: parity-bug
description: Triage a parity bug report ("this button does not behave like the legacy") against the manifest, spec and implementation, and land the fix as a test, a spec line or an extractor issue. Use for any behavior mismatch on a ported form.
---

# parity-bug

A bug is never fixed with only a code patch. Classify first.

1. Ensure `legacy/.phase` contains `bug` (reading the legacy source is allowed in this phase, but start
   from the manifest).
2. Locate: find the control in `legacy/<Module>/manifests/<Form>.md` (search the caption or field
   name). Note its `evt:` ids, the handlers, and the methods they reach. Read the slices.
3. Classify, exactly one:
   - **Spec has the rule, implementation violates it.** Write or fix the test for that rule id, then the
     code. Output: a test.
   - **Spec lacks the rule, manifest has the behavior.** Switch to a specify session: add the rule with
     citations, lint, reviewer re-reviews, then implement. Output: a spec line, then a test.
   - **Manifest lacks the behavior.** Check `raw`, `diagnostics`, `unresolved` and `methods` without
     source. If the logic is beyond `maxCallDepth`, raise it and re-extract. Otherwise this is an
     extractor hole: file an issue with the anonymized evidence, acknowledge in `extractor-debt.md`.
     Output: an issue, then the above once fixed.
   - **The legacy itself is wrong or strange.** Add `quirk: preserve` or `quirk: diverge` to the rule
     with `decided-by` from the user. Output: a spec line.
4. Report the classification and the artifact that changed. If only code changed, you skipped a step.
