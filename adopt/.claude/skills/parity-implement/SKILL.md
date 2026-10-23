---
name: parity-implement
description: Implement one form from its reviewed parity spec and recorded fixtures, with tests named after rule ids. Use when asked to "implement <Form>" and the spec passes parity lint --gate.
---

# parity-implement

Precondition: `parity lint legacy/<Module>/spec/<Form>.md --gate` prints `0 error(s)`. If not, stop
and say which gate is red; do not implement around it.

Inputs, and nothing else: the spec, `legacy/<Module>/fixtures/`, the existing scaffold or shared
components in this repo. The legacy source is off limits (hook-enforced). The manifest is reference
material for ids only; behavior comes from the spec.

1. Ensure `legacy/.phase` contains `implement`.
2. For each Fields row, map the control to a component per the repo's control mapping. `shared:<X>` rows
   use the shared component; `dropped` rows produce nothing.
3. For each rule, in order: write the test first, named so the rule id appears verbatim
   (`describe('R-<Form>-003', ...)`), driven by the listed fixtures; then the implementation.
   The `then` clause is the assertion. A `quirk: preserve` is implemented as written, with the quirk
   noted in the test name.
4. Service calls go through the repo's existing gateway client; fixtures are the mocked responses.
5. Run the repo's tests. Then `parity lint legacy/<Module>/spec/<Form>.md --gate --implement <src dir>`.
   Fix `COV-TEST` by adding the missing test, never by editing the spec.
6. If the spec is wrong or incomplete, stop: report the rule id and the problem. The spec is changed in
   a specify session with the reviewer, not here.

Report: rules implemented, tests added, anything deliberately left out (there should be nothing).
