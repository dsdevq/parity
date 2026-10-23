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
| capture | script (yours) | module service-call ids | `fixtures/*.json` | every `svc:` id has at least one recording |
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

**Duplicates.** Token streams with identifiers and literals replaced; exact hash clusters, and for
same-named handlers across forms a 3-gram Jaccard similarity above 0.6 as a near cluster.

## What it deliberately does not do

- Load the solution or run MSBuild.
- Guess: an uninterpreted statement is reported, never approximated.
- Generate the target code. Scaffolding is a separate script that reads the manifest and the category
  table; this repo ships the table, not the scaffolder, because the target stack is the adopter's.
- Hold any knowledge of a specific legacy application. All of it is config.
