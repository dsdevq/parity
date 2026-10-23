# Design

## The problem it is shaped around

Porting a legacy WinForms module to a new UI with the same behavior fails in a specific way: the agent
guesses. Not because it is careless, but because the ground truth is spread over a designer file, two or
three partial classes, DevExpress validation providers, helper classes elsewhere in the project, and the
API behind a proxy, and reading all of it costs more than extrapolating from half of it. Instructions in
a skill ("read everything", "do not assume") do not change the economics. Mechanisms do.

## Three artifacts, one direction of flow

```
legacy repo ──> manifest (generated, deterministic)
                 │
                 ├──> fixtures (recorded service calls)
                 │
                 └──> spec (curated, every rule cites manifest ids)
                          │
                          └──> implementation (reads spec + fixtures only)
```

Nothing flows backwards. A bug found in the implementation is classified against the three artifacts:

1. the spec has the rule and the code violates it: implementation bug, fix with a test named after the rule;
2. the spec lacks the rule but the manifest has the handler: spec gap, add the rule with its citation;
3. the manifest lacks it: extractor hole, fix the tool and re-extract, which upgrades every module;
4. the legacy itself is strange: record it as a `quirk` with a decision and a decider.

A fix that ends as only a code patch has escaped the system.

## Phases and gates

| Phase | Who | Input | Output | Gate |
|---|---|---|---|---|
| probe | script | csproj | counts, statement shapes | none, it is reconnaissance |
| extract | script | config, form glob | manifests, module index | `raw == 0 && diag == 0` or each entry acknowledged in `extractor-debt.md` |
| capture | script (yours) | module service-call ids | `fixtures/*.json` | every `svc:` id has at least one fixture; a synthetic one is labelled, shape-verified and explained |
| specify | agent, then human | rendered manifest, fixtures, evidence | `spec/<Form>.md` | `parity lint` green; human sets `status: reviewed` |
| scaffold | script (yours, optional) | manifest, categories | component skeleton | every control mapped or explicitly dropped |
| implement | agent | spec, fixtures, skeleton | components, tests | `parity lint --gate --implement <dir>` green |

Each phase is its own session for an agent. The only thing that crosses a phase boundary is a committed file.

## Rule to mechanism

| Rule in a skill today | Why it gets ignored | What replaces it |
|---|---|---|
| read the source before porting | partial reads look like full reads | the manifest contains every relevant slice; nothing to skip |
| do not assume | nothing to check against | every rule cites ids; `HEDGE` rejects hedging words |
| cover the whole form | "whole" undefined | coverage is computed from the manifest |
| implement from the spec, not the code | the code is available | a hook denies the legacy path during specify and implement |
| verify parity | a judgement call | tests carry rule ids; `COV-TEST` finds the missing ones |
| follow the steps | one session blends them | one skill per phase, each stops at its gate |

## Extraction

**Loading.** The csproj is parsed for the explicit file list and defines. A regex index maps type names to
files. Only files declaring a form that matches the glob enter the first compilation. References come from
the configured bin folder globs plus the project's own built assembly (`selfAssembly`), so every type not
loaded as source still resolves as metadata. The compilation then grows: names the compiler reports as
unresolved (CS0246, CS0103, CS0234, CS0118, CS1061) that the index knows, and in-source call targets up to
`maxCallDepth`, are added round by round until nothing is wanted or eight rounds pass.

**Designer pass.** `InitializeComponent` is interpreted statement by statement: object creation declares a
control with its semantic type chain; property assignment records a property (resx `GetString` and
`ApplyResources` resolved); `Controls.Add`, `AddRange` on any collection, `Parent`, `GridControl` and
DevExpress `LayoutControlItem.Control` build the tree; `DataBindings.Add` records a binding;
`SetValidationRule` records a provider rule; `+=` records wiring. Statements on the form itself are the
same shapes with `this` as the root. `BeginInit`, `SuspendLayout` and friends are skipped. Anything else
is `raw`.

**Handler pass.** Handlers are methods wired from the designer or anywhere in the class, lambdas in a
`+=`, overrides, and constructors (overrides and constructors get a pseudo-event under `$this` so they
are coverable). For each: the dedented source, controls read and written (write = root of an assignment
left side, or first argument of `SetError`, or receiver of a mutating call), invocations resolved to
`svc:` when the containing type matches the service boundary, `method:` when declared in source or in a
project-local assembly, ignored when matching `ignoreCalls`, otherwise `unresolved`. Property getters with
a body in source count as calls. Object creations of control-derived types inside a handler become
`dyn:` entries with the container they are added to. A method that is also a handler collapses into the
handler. `InitializeComponent` is never followed. Private methods neither wired nor reached are diagnosed.

**Constants and resource texts.** Every `const`, literal `static readonly` and resource read a slice or a
bound model annotation makes is resolved to its literal or default-culture resx text, with the other
cultures, in `constants[]`. They are facts, not citable ids: a spec writes the value into `then` and still
cites the handler or method that reads it, so a changed value surfaces as a manifest diff, not a broken cite.

**Variants.** A tenant customisation is a subclass of a module form, in the module's project (named in
`subclasses`) or in a tenant project (`project`, read with the same csproj reader; its files join the
compilation through the same round-growing, and its `selfAssembly` is one more reference). The subclass is
not a unit: a spec per tenant would repeat the base's rules and drift from them. It is read with the same
designer and handler passes, the base's fields treated as its own, and compared with the base unit:

- Granularity is one delta entry per control, wiring, method or service id, never per property: a control
  re-tagged in three properties is one `retagged` entry with three `changes`, because a reviewer accounts for
  "Larkspur relabels this control", not for each assignment. Layout-only properties go to `layout` and are
  not gated, for the same reason the base's layout is not.
- Constructor statements that re-tag, remove, add or wire controls are folded into those entries; only a
  constructor doing something else is a `ctor` entry. An override says whether it calls base: a replacement
  takes its base handler's service calls with it (`service-removed`, when no other caller remains).
- Factories are read from files that construct two members of a form family, so the tool does not need to
  be told where they are; each case is cited. A form a factory creates behind a condition the tool does not
  understand is a case keyed by that condition's text, never the `default`.
- Constants and resource texts the variant's code reads are compared with the base's `constants[]` by
  `Type.Member` and value: the same `Type.Member` is nothing, the same member name on another type or
  resource is the base's value replaced, anything else is a read the base does not make (`constant` entries).

The spec has one grammar for this, not a section per variant: a rule carries `variants:` (absent means every
tenant), and a delta entry is accounted for by a rule scoped to its variant or by a reasoned row. That keeps
"what the base does" in one place and makes the lint able to say which tenant a rule is wrong for (`VAR-SCOPE`:
an unscoped rule citing a handler a variant replaces). Known limits: a replacement form that is not a subclass
is a separate unit; a variant project is parsed with the main project's defines; classes between the
base and the configured subclass are diagnosed, not described.

**Duplicates.** Token streams with identifiers and literals replaced; exact hash clusters, and for
same-named handlers across forms a 3-gram Jaccard similarity above 0.6 as a near cluster.

## Adoption: `init`, `doctor` and the embedded kit

Adoption used to be a checklist (copy hooks, skills and templates, merge a hooks block, put the binary on PATH,
create folders), and every step was a place to get it subtly wrong: a skill pointing at `templates/spec.md` that
the consuming repo does not have, hooks ordered after a repo's own. `parity init` does the layout and
`parity doctor` checks it; `docs/adopt.md` ("Setup") is the user-facing description. Decisions:

- **The kit is embedded in the binary.** `adopt/` and `templates/` are compiled in as resources. The release is one
  self-contained file used on a machine without a parity checkout, and the installed skills and hooks always
  match the version of the tool that reads their output (`"${PARITY_BIN:-parity}"` calls, hook behaviour, the spec
  template the lint expects). `adopt/` and `templates/` stay the single source; a test compares the embedded copy
  with the files in the repo and fails on a kit file the binary does not carry.
- **`init` is data-driven.** `adopt/kit.json` says which folders and files go where, which are the repo's own after
  the first write (config, control categories, `.phase`), which `settings.json` block is merged, and what the phases
  are (name, prompt). A new skill folder, hook script or template, or a new phase, is a kit change; `init`
  and `doctor` iterate the manifest and need no code change.
- **Ownership decides overwriting.** Tool-owned files are created, then reported as `would-update` when they differ
  and rewritten only by `--merge`; repo-owned files are written once. A second `init` is a no-op that exits 0; a
  pending change exits 1, so a script can tell.
- **`settings.json` is merged, never replaced.** Parity's entries are appended after the repo's own for each event
  (hooks run in order, and the repo's hooks may depend on running first), nothing foreign is reordered or
  rewritten, and an entry already wired (same command or same script) is not added twice. A file with comments is
  refused, because rewriting it would lose them; the same strictness is needed by `jq` in the guard.
- **The config skeleton comes from the csproj**, using the same reader as `extract`, so what `init` writes is what
  `extract` resolves. What is a decision of the adopter (`serviceBoundary`, `variants`) is emptied, not guessed.
- **`doctor` looks tools up instead of running them** (`sh`, `jq`, `parity` on PATH or `PARITY_BIN`) and reuses
  `CompilationBuilder.BuildReferences` for the reference count and `REF-MISSING`, so it reports exactly what
  `extract` will see, including the >200 DLL warning (the per-run cost is the antivirus scanning each DLL).
- **The phase driver is a process per phase.** "A fresh session per phase" is only true if the agent is a new
  process with the phase file already set, so the script writes `.phase` and starts `PARITY_AGENT` (default
  `claude -p`) once per phase with that phase's prompt. It is generated from the kit's phases so it cannot drift.
  The caller names the phases to run, so the steps only a person can do (recording fixtures, reviewing a spec) sit
  between runs. The script is POSIX `sh`, like the hooks, and runs on Windows through the Git for Windows `sh` the
  hooks already require.
- Not in `init`: what the phases do, the guard's matching rules (issue 22), and a map phase (issue 17).

## What it deliberately does not do

- Load the solution or run MSBuild.
- Guess: an uninterpreted statement is reported, never approximated.
- Generate the target code. Scaffolding is a separate script that reads the manifest and the category
  table; this repo ships the table, not the scaffolder, because the target stack is the adopter's.
- Hold any knowledge of a specific legacy application. All of it is config.
