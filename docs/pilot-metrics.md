# Pilot metrics

How to tell whether parity pays for itself on a consuming team that already has a working method: a
gap-driven research workflow plus a live acceptance ladder, where every fault the host raises names the
field to build. The first pilot (issue #18) asked that question on one small embedded form and one large
form. This page fixes six metrics for the next evaluation, says how each one is read from the tool's own
outputs, and records the pilot as the baseline row.

A metric that needs a human count says so. Every other figure comes from a line the tool prints or a file
it writes, so two evaluations measure the same thing.

## The six metrics

### 1. Gaps found per hour, against the existing workflow

A gap is a behavior the new app gets wrong or lacks, named by the rule id that states it. The tool gives
the numerator. The comparison of the spec against the implementation lists failing or missing rules by id:
the `COV-TEST` findings of `parity lint <spec> --gate --implement <dir>` and the tests that fail against
the rule. Count only the ids the existing workflow's tracker does not already hold for the same unit.

The hours are the wall-clock from the first `parity extract` of the unit to the end of that comparison. The
extract's own share is the `time:` on its `files loaded:` line, and the rest comes from the session
timestamps. Report both workflows' figures for the same unit side by side, because the two find different
kinds of gap: the ladder proves what the host accepts, and the spec sees what the host never enforces.

### 2. Open questions per unit at the first gate

The questions under `## Open questions` in the spec when `parity lint --gate` is first run on it, not
counting lines that are already `closed by decision` or `closed by evidence`. While the count is above zero
the gate reports `OPEN`. Split the count into context questions and the rest. A context question is about
something outside the unit: who sets an input, what a host does with an output, which tenant gets which
variant, how a service fails. Since #17 the answers to those live in `module.json` (contract, composition,
shared state) and `registry/`, so after a map phase this count should fall to the questions only a person
can answer.

### 3. Human research answers needed per unit

The answers a person had to supply before the gate went green: the `dec:` ids in `registry/decisions.json`
that cite the unit, plus the `ev:` records under `evidence/<Unit>.json` that `parity evidence add` wrote
for it. Both are on record and checked by lint (`DECISION-UNKNOWN`, `EV-STALE`). An answer kept anywhere
else, such as a side file of notes, also counts and is a regression: the tool cannot check it, and the next
session will not see it.

### 4. Share of rules with a real recorded fixture

Of the rules that touch a `svc:` id, the share that have at least one fixture with a `recorded` date (not
`synthetic`). Read it from the gate:

- `parity lint --gate` ends with `fixtures: N (M synthetic)`;
- each rule whose fixtures are all synthetic gets a `FIXTURE-SYNTHETIC` warning;
- `parity registry catalog` lists every service's fixtures with their provenance (`recorded <date>, <environment>` or `synthetic`).

Share = 1 - (rules with a `FIXTURE-SYNTHETIC` warning) / (rules touching a service). A synthetic fixture
the gate accepts has `shapeVerified: true` and a note, so a low share is not a lint failure. It tells you
how much of the spec still rests on replies nobody saw the host send.

### 5. Time from "tool fix released" to "spec green again"

Start at the release that changes what the extractor emits. Stop at the first green `parity lint --gate`
on each spec of the module. The steps in between all print a line:

- the re-extract prints `changed:` and `stale spec:` lines;
- `parity diff` lists what moved;
- `parity rebind` either moves the spec or lists the rules to re-read;
- `parity registry check` reports `REGISTRY-STALE` until `parity registry stamp`.

Report the median per spec, and how many specs `rebind` moved without anyone reading them.

### 6. False-positive rate of diagnostics

Of the diagnostics the extract reports for a unit (the `diag` column of the extract summary, listed by
`parity report`), the share that turn out on review not to be a real gap in the manifest. Each one the team
accepts is acknowledged by a dated, owned line in `extractor-debt.md`, which `COV-RAW` requires. Mark an
acknowledgement that is really a false positive as such in that line. The rate is false positives divided by
diagnostics. Each false-positive class goes into an issue, gets a fixture pattern (`docs/testing.md`) and
leaves the count. Raw statements are not part of this rate: they are always real work for the extractor.

## Baseline

The pilot used parity at 8977f4e, before #14-#17 and #19-#25 landed. Its figures are the baseline.

| Metric | Pilot (8977f4e) |
|---|---|
| Extract | small form 4.7 s (17 controls, 1 service call); large form 22 s (679 controls, 219 events, 210 handlers, 32 service calls) |
| 1. Gaps found per hour | 5 gaps the host never rejects, on top of the 2 the existing workflow found (both from host faults); hours not recorded |
| 2. Open questions at the first gate | 8 (7 context) |
| 3. Human research answers | 8, closed by hand research in a side file the tool cannot read |
| 4. Rules with a real recorded fixture | not measurable: fixtures carried no provenance before #24 |
| 5. Fix released to spec green | not measurable: a re-extract left the spec STALE with no diff and no rebind (#25) |
| 6. Diagnostic false-positive rate | not counted |
| Specify / implement | 22 rules, lint green; 68 tests passing, 7 `it.todo`, gate green despite the todos (#16) |

The acceptance unit below is not a pilot measurement. It shows what the tool reads off a unit of the same
shape today, so the next evaluation knows what a good figure looks like.

| Metric | Acceptance unit (`examples/Payouts`) |
|---|---|
| Extract | 3 forms in about 3.3 s; panel: 8 controls, 4 events, 2 service calls; raw 0, diagnostics 0 |
| 2. Open questions at the first gate | 1 (who decides whether a refund may go to a third-party account), closed by `dec:refund-offers-own-accounts-only` |
| 3. Human research answers | 1 decision, 0 side files |
| 4. Rules with a real recorded fixture | 3 of the 3 rules that touch a service (5 rules in all); `fixtures: 3 (1 synthetic)`, no `FIXTURE-SYNTHETIC` warning |
| 6. Diagnostic false-positive rate | 0 of 0 |

## The acceptance test

The pilot proposed an acceptance test for the tool: one unit with two tenant variants, embedded in two
hosts, calling two or more services; a good run gives a green gate with no side files and no hand research.
`IssueTests.issue_18_a_unit_with_two_variants_two_hosts_and_recorded_and_synthetic_replies_passes_extract_map_and_the_gate_with_no_side_files`
is that test, run on the fixture through the same calls the CLI makes:

- the unit is `PayeeLookupPanel`;
- its variants are Larkspur (a subclass) and Thistle (a variant project), handed out by `PayeeLookupPanelFactory`;
- its hosts are `ClaimPayoutForm` (on Load) and `RefundPayoutForm` (on a click);
- it calls `GetPayeeAccounts` and `VerifyPayoutAccount`, with two recorded replies and one synthetic rejection.

The run extracts, copies in the reviewed registry and fixtures, stamps and gates. It then answers each
context question from the manifests and the registries alone: the hosts and how they embed the unit, the
variant each tenant key gets, what each host sets (or the decision that covers what a host leaves unset),
which host reads the output, and each service's callers, error cases and fixture provenance. The
`examples/Payouts` README gives the same run on the command line.

## Known gaps

What the acceptance run still cannot answer from the manifests, and why it passes anyway:

- **Sets and calls from a host's other handlers.** An embed records the calls and sets made in the body that
  creates the instance, plus the reads in every other body of the host. A host that creates the unit in one
  handler and sets its inputs in another shows those inputs as unset. The fixture hosts set everything where
  they create the unit. Recording them would change what `setup` means in the composition, which is beyond a
  fix inside the existing pass.
- **`this._field.Member`.** A host that reaches the kept instance through `this.` is not tracked. Only the
  bare field or property name is.
- **Where a cross-handler read happens.** It carries the line of the read but not the id of the handler it
  sits in, so a spec cites the host's handler by hand.
- **Hours.** Metric 1 needs the session wall-clock, which the tool does not record beyond the extract time.
