# parity

One tool, one purpose: reverse-engineer a legacy WinForms module into a **citable manifest**, and
refuse any spec written from it that does not **cover** that manifest. It exists so that an agent
porting a legacy screen never has to guess, because everything it may claim has an id and a line.

It is not a converter, not a code generator, not a code-search index. It produces facts about one
module, deterministically, and checks a human-readable spec against them.

```
legacy csproj ──extract──> manifests/<Form>.json + .md   (deterministic, cited, complete or loudly incomplete)
                                    │
             human + agent ──specify──> spec/<Form>.md    (rules: given/when/then, every rule cites manifest ids)
                                    │
                           ──lint --gate──> green or a list of what is not covered
                                    │
                    agent ──implement──> Angular (or anything) from the spec only; tests carry rule ids
```

## Why

A 50-million-line solution with 800 projects cannot be loaded, read, or remembered by a model. An
agent told to "port this form" opens a 5,000-line file, reads part of it, and extrapolates the rest.
The result looks right and is wrong somewhere, and nothing it learned survives the session.

WinForms is regular enough to be parsed instead of read. `InitializeComponent` is serialized code:
every control, property, binding and event wiring is one statement with a known shape. Handlers
are methods with a resolvable call graph. So the facts can be extracted once by a tool, given stable
ids, and the agent's job shrinks to writing rules that cite those ids. A lint then computes coverage
from the manifest, which the agent cannot shrink.

## What you get

| Artifact | Produced by | Contents |
|---|---|---|
| `manifests/<Form>.json` | `parity extract` | control tree (incl. DevExpress layout items and grid columns), resx captions, event wiring from designer, code-behind, lambdas, overrides and constructors, per-handler source slice with controls read and written, calls, service-boundary calls with argument text, transitive methods to a bounded depth with their slices, bindings, provider validation rules, controls created at runtime, constants and resource texts with their values, and everything it could not interpret under `raw` and `diagnostics` |
| `manifests/<Form>.md` | same run | the same, rendered for an agent to read instead of the legacy file |
| `module.json` | same run | forms, every service call id, exact and near duplicate handlers across forms |
| `evidence/<Form>.json` | `parity evidence add` | quoted legacy lines the manifest lacks (a model class, a config value), each an `ev:` id with its file, range, quote hash and source revision |
| `spec/<Form>.md` | you + agent | field inventory, rules with citations, no-behavior list, service calls with fixtures, open questions |
| lint findings | `parity lint` | `file:line CODE message`, exit 1 on errors |

Every id is stable and citeable: `ctrl:Form/field`, `dyn:Form/handler/local`, `evt:Form/field.Event`,
`handler:Form/method`, `method:Namespace.Type.Method(params)`, `svc:IService.Method`. Every item
carries `file:start-end`. The spec cites ids, never line numbers, so a citation either resolves or fails.
A fact outside the manifest is cited the same way, through an `ev:Form/n` record whose quoted lines lint
re-reads and re-hashes, so evidence either still matches the source or is `EV-STALE`.

## Install

Needs the .NET 10 SDK to build, or nothing at all if you take a release binary. All commands in this
file are POSIX shell; on Windows run them from Git Bash (installed with Git for Windows). The only
Windows-native entry point is `bin\parity.cmd`, which wraps the debug build.

```sh
git clone https://github.com/dsdevq/parity && cd parity
dotnet build            # builds the tool, the stub assembly and the synthetic legacy fixture
dotnet test             # tests over the fixture: extraction, duplicates, every lint code
bin/parity --help       # Windows: bin\parity.cmd --help
```

Self-contained single binary for a machine without the SDK (the usual case for a locked-down work laptop):

```sh
bin/publish.sh win-x64        # -> dist/win-x64/parity.exe (also linux-x64, osx-arm64)
```

Tagged releases publish the same binaries as GitHub release assets.

## Quickstart on the fixture

```sh
bin/parity probe   fixtures/SampleLegacy/SampleLegacy.csproj
bin/parity extract --config fixtures/extract.config.json --module PolicyWizard --forms 'PolicyWizardStep3'
less .out/legacy/PolicyWizard/manifests/PolicyWizardStep3.md
```

`examples/PolicyWizard/spec/PolicyWizardStep3.md` is a complete spec for that manifest. To lint it:

```sh
mkdir -p .out/legacy/PolicyWizard/spec && cp -r examples/PolicyWizard/fixtures .out/legacy/PolicyWizard/
sed "s/sha256:0000000000000000/$(bin/parity hash .out/legacy/PolicyWizard/manifests/PolicyWizardStep3.json)/" \
    examples/PolicyWizard/spec/PolicyWizardStep3.md > .out/legacy/PolicyWizard/spec/PolicyWizardStep3.md
bin/parity lint .out/legacy/PolicyWizard/spec/PolicyWizardStep3.md --gate
```

Then delete a rule and run it again to see what the gate says.

## Quickstart on your project

1. **Probe first.** `parity probe path/to/Legacy.csproj --forms 'YourForm*'` prints counts and the
   distinct statement shapes inside `InitializeComponent` with identifiers and literals replaced. It
   needs no references and no build. If a shape in that list is not one the extractor knows, you
   will see it later under `raw`; this is also what you paste into an issue.
2. **Write `extract.config.json`** from `templates/extract.config.json`: the csproj, the references,
   the project's own built DLL as `selfAssembly`, your form base types, and the **service boundary**:
   the namespaces or proxy types that constitute the old API client. The boundary is what turns a
   call into a `svc:` id. For references prefer `referencesFromProject: true` with `referenceDirs`
   pointing at the project's output folder: the tool then loads exactly the DLLs the csproj names.
   **Never glob a solution-wide output folder**; a thousand DLLs behind a corporate antivirus took
   over thirty minutes on a real machine, while the csproj's own hundred took forty seconds.
3. **Copy `templates/control-categories.json`** next to it and add your custom controls. Categories
   decide which controls must appear in a spec and are shared with any scaffolding step.
4. **Extract one small form**, not the biggest one. Check the summary line: `raw` and `diag` must be
   zero or explained. If they are not, the extractor has a hole; file it before writing any spec.
   `parity report manifests/<Form>.json` prints the raw statements, diagnostics and unresolved calls
   with identifiers removed, which is what goes into the issue.
5. **Specify** from `manifests/<Form>.md` only, using `templates/spec.md`. Run `parity lint` until
   green, get the spec reviewed by someone who knows the business, set `status: reviewed`. When a rule
   needs a fact the manifest lacks, record it before specifying:
   `parity evidence add <Form> <file>:<start>-<end> --note <text>` prints an `ev:<Form>/<n>` the rule
   cites beside its handler (`docs/spec.md`, Evidence).
6. **Gate**: `parity lint spec/<Form>.md --gate`. Every control, event, handler and service call is
   accounted for or the gate is red. `--implement <dir>` additionally wants every rule id in the
   title of an active test (`it`, `test`, `describe`, `[Fact]`); `it.todo`, `.skip`, comments and
   constants do not count (`COV-TODO` says where). See `docs/lint.md`.

The work-side pieces that make the phases stick for an agent (skills, a hook that locks the legacy
path during specify and implement, a hook that lints on stop) are in `adopt/`. See `docs/adopt.md`.

## Handoff: setting up in the consuming repo

This section is written to be handed to an agent as is. Each step ends with a check; do not continue
past a failed check. Nothing here touches the legacy repository. "Consuming repo" means the repository
that will hold the new implementation.

**0. Prerequisites**

- Windows: Git for Windows. Run every command in this section from **Git Bash**, not PowerShell;
  the hooks run under its `sh`. `jq` is optional: the guard hook falls back to `sed`, and if it
  still cannot find the legacy root it refuses every call in a guarded phase rather than failing open.
  Setting `PARITY_LEGACY_ROOT` to the legacy project directory bypasses both.
- The legacy project must have been **built at least once** so its `bin` folder holds its own DLL and
  every referenced DLL (DevExpress included). parity never builds it; it reads the outputs.
- For a .NET Framework legacy project, the Reference Assemblies folder must exist, typically
  `C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8/`. It ships with
  Visual Studio and with the targeting pack.
- Check: `dotnet --list-sdks` shows a 10.x SDK **or** you have a release binary from
  https://github.com/dsdevq/parity/releases. No admin rights are needed for either.

**1. Get the binary**

- With the SDK: `git clone https://github.com/dsdevq/parity && cd parity && dotnet build && dotnet test`,
  then `bin/publish.sh win-x64` and copy `dist/win-x64/parity.exe` somewhere on PATH. Until you
  publish, `bin/parity` (Git Bash) or `bin\parity.cmd` (cmd/PowerShell) run the debug build.
- Without the SDK: unzip the release for your OS, put `parity` (or `parity.exe`) on PATH.
- Check: `parity --help` prints the usage; `parity probe <any csproj>` prints counts, the csproj's
  resolved `outputPath` (often several levels up: `..\..\..\bin\Debug\`) and its reference counts.
  That `outputPath` is what `referenceDirs` and `selfAssembly` point at.

**2. Lay out the consuming repo**

```
legacy/
  extract.config.json       copy of templates/extract.config.json, edited below
  control-categories.json   copy of templates/control-categories.json
  .phase                    text file containing: extract
  hooks/                    copy of adopt/hooks/*.sh
.claude/settings.json       merge the hooks block from adopt/.claude/settings.json
.claude/skills/parity-*/    copy of adopt/.claude/skills/*
```

Check: `sh legacy/hooks/guard-legacy.sh </dev/null` exits 0 (phase is extract, so nothing is blocked).
PowerShell equivalent: `$null | sh legacy/hooks/guard-legacy.sh; $LASTEXITCODE`.

If `.claude/settings.json` already has hooks, add parity's entries to the existing arrays; several
entries per event are fine and run in order:

```json
"PreToolUse": [
  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "your-existing-hook" } ] },
  { "matcher": "Read|Grep|Glob|Bash|Edit|Write|MultiEdit", "hooks": [ { "type": "command", "command": "sh legacy/hooks/guard-legacy.sh" } ] }
]
```

If `legacy/` is ignored by `.gitignore` or `.git/info/exclude` in this repo, un-ignore it: the
manifests and specs are meant to be committed and reviewed.

**3. Edit `legacy/extract.config.json`** (paths with forward slashes, relative to the file or absolute)

- `project`: the legacy csproj of the module.
- `references`: for a .NET Framework project, the Reference Assemblies folder glob **and** its
  `Facades/*.dll` (both must exist), then optionally the DevExpress install folder if its DLLs are
  not copied to the output folder. Set `includeRuntimeReferences` to `false`.
  For a .NET Core-era project leave `includeRuntimeReferences` true and skip the framework globs.
- `referencesFromProject: true` and `referenceDirs: ["<outputPath from probe>"]`: the DLLs named by
  the csproj's `Reference` and `ProjectReference` items are looked up there. Do not glob the
  solution-wide output folder in `references`; see Memory and speed.
- `selfAssembly`: the legacy project's own DLL in that bin folder.
- `formBaseTypes`: add every custom base form or user control the module derives from, as full
  metadata names. An entry that does not resolve produces a `BASE-UNRESOLVED` diagnostic in every
  manifest, and when no form matches at all the error lists the matched classes with their bases.
- `serviceBoundary`: the namespaces or proxy types that are the old API client. Patterns accept a
  trailing `*`. Everything behind this becomes a `svc:` id. Ask the team if unsure; do not guess.
- `output`: `.` so modules land under `legacy/<Module>/`.
- Check: `parity extract --config legacy/extract.config.json --module Probe --forms '<one small form>'`
  prints a summary row. `diag` must not contain `REF-MISSING` or `TYPE-UNRESOLVED` on framework or
  DevExpress types; if it does, the references list is wrong, fix it before anything else.
  Delete `legacy/Probe/` afterwards.

**4. Probe, then extract one small form for real**

- `parity probe <csproj> --forms '<Form>'` and keep the output; it is what an issue needs.
- `parity extract --config legacy/extract.config.json --module <Module> --forms '<Form>'`.
- Check: `raw` and `diag` are 0. If not, open `legacy/<Module>/manifests/<Form>.md`, sections Raw
  and Diagnostics. Each entry is either a config problem (fix and rerun) or an extractor hole: create
  `legacy/<Module>/extractor-debt.md` from `templates/extractor-debt.md` with one line per entry
  (location or code, owner, date) and file an issue at https://github.com/dsdevq/parity/issues using
  the "Extractor hole" template with the output of `parity report <manifest.json>` and the probe
  shapes. Those two commands are the only sources an issue may quote; the manifest itself contains
  real identifiers.
- Commit `legacy/<Module>/`.

**5. Record fixtures**

- `legacy/<Module>/module.json` lists every `svc:` id. Record at least one real call per id with your
  own runner into `legacy/<Module>/fixtures/<name>.json`, shape per `templates/fixture.json`, the
  `service` field set to the id.
- Check: `ls legacy/<Module>/fixtures` has a file for every id.
- While `.phase` is still `extract`, quote any legacy fact a rule will need that the manifest lacks:
  `parity evidence add <Form> <file>:<start>-<end> --note <text>` (file relative to the legacy root)
  writes `legacy/<Module>/evidence/<Form>.json` and prints the `ev:` id. It refuses in `specify` and
  `implement`. Commit the evidence file with the manifests.

**6. Specify, then implement, each in a fresh session**

- Write `specify` into `legacy/.phase`, start a new session, run the `parity-specify` skill for the form.
  The guard hook now refuses any read under the legacy path; that is intended.
- Check: `parity lint legacy/<Module>/spec/<Form>.md` prints `0 error(s)`. A human reviews the spec
  against the running legacy app and sets `status: reviewed` with `reviewed-by: <name> <date>`.
- Check: `parity lint legacy/<Module>/spec/<Form>.md --gate` prints `0 error(s)`.
- Write `implement` into `.phase`, new session, `parity-implement` skill. Set `PARITY_IMPLEMENT_DIR` to
  the source directory so the stop hook runs `--implement`.
- Check: `parity lint ... --gate --implement <src dir>` prints `0 error(s)`.

**Known limits, so nobody discovers them the hard way**

- The tool reads Designer files and C# only. Logic that lives behind the proxy (in the old API) shows up
  as a `svc:` call with no client-side rule, which is a correct finding, not a hole.
- Dynamic property names, reflection, and controls instantiated through factories produce
  `unresolved` entries or `dyn:` entries without a container; those are spec open questions.
- Transitive methods stop at `maxCallDepth` (default 2). A `method:` without a source slice means raise
  the depth and re-extract.
- The release binary is about 140 MB because it is self-contained; memory at run time is what matters
  and is printed on every run.

## Memory and speed

The tool never opens the solution and never runs MSBuild. It reads the csproj for the file list,
builds a cheap textual index of which file declares which type, and loads into the compilation
only the files of the targeted forms. It then grows that set round by round from two signals: names
the compiler could not resolve that the index knows, and call-graph targets up to `maxCallDepth`.
Everything else is referenced through `selfAssembly` and the bin folder as metadata. Memory therefore
scales with the module, not the project. Every run prints `files loaded: n of m` and `peak-rss-mb`.

On the fixture: one form, 6 of 20 files loaded, under a second, about 150 MB peak including the
runtime. Every run also prints the reference-load time and a line per round, so a slow run shows
where the time goes. The one way to make it slow is to glob a solution-wide output folder as
references: reference metadata is read lazily but per DLL, and a corporate antivirus pays per file.
Use `referencesFromProject` or list the project's own output folder.

## The contract, in one table

| Rule | Mechanism |
|---|---|
| Nothing is dropped silently | an uninterpreted designer statement lands in `raw`; an unresolved type, missing resource or unreached method is a diagnostic; the gate refuses both unless acknowledged with an owner and a date in `extractor-debt.md` |
| An agent cannot cite what does not exist | `CITE-UNKNOWN`, `CITE-KIND` |
| Evidence outside the manifest is checkable | `EV-STALE` when the quoted lines moved or changed, `EV-INVALID`, `EV-UNLINKED` for a question closed without an `ev:` id |
| Done is computed, not asserted | `COV-CONTROL`, `COV-EVENT`, `COV-HANDLER`, `COV-SERVICE`, `COV-DYNAMIC`, `COV-RAW`, all computed from the manifest |
| A spec is bound to one manifest | `STALE` on hash mismatch |
| Facts, not guesses | `HEDGE` rejects probably / likely / assume / should / seems / typically / usually / maybe / might; uncertainty goes under Open questions, which keeps the gate closed |
| Decisions are owned | `DROP-REASON` for dropped controls and quirks; `STATUS` requires a named reviewer and a date |
| Implementation traces to rules | `COV-TEST` wants each `R-` id in the title of an active, non-skipped test |

Full list: `docs/lint.md`.

## Feedback loop

The tool is developed against a synthetic legacy project (`fixtures/SampleLegacy`) that reproduces
messy patterns: partial classes across three files, DevExpress `LayoutControl` trees, resx captions,
controls built in handlers, duplicated client search, a hand-rolled wizard controller. It cannot see
your code, so your code's shapes reach it through issues:

- **Extractor hole**: paste the output of `parity report <manifest.json>` and the relevant `probe`
  shapes. Both replace identifiers with `$id`, `$lit`, `$form`, `$type` and `<path>`; the manifest's
  own Raw and Diagnostics sections are **not** anonymized, do not paste those. Review the report
  output before posting anyway.
- **Lint mismatch**: the finding and the spec fragment.
- **Fixture request**: a form under `fixtures/SampleLegacy` with invented names that reproduces the
  pattern, ideally as a PR. A work-side agent can write that skeleton; nothing from the real codebase
  may appear in it.

## Prior art and why this exists anyway

Roslyn does the parsing; this tool is the few hundred lines around it that know what a form is. Two
open-source projects cover parts of the designer side: a WinForms designer MCP server (control tree,
properties, event wiring; no resx, no handler bodies, no call graph) and a solution-level legacy
analyzer (needs the whole .sln loaded, which is the one thing a huge solution rules out). Neither has
stable ids, locations, a service boundary, or a coverage lint, and those are the contract. Details in
`docs/prior-art.md`.

## Layout

```
src/Parity/          the CLI: Project/ (csproj, index, compilation), Passes/ (designer, handlers, duplicates, extractor), Render/, Lint/
fixtures/            WinFormsStubs (stand-ins for WinForms + DevExpress), SampleLegacy (synthetic mess), SampleLegacy.Build (its DLL)
examples/            a complete module folder: spec + recorded fixtures
templates/           spec.md, extractor-debt.md, fixture.json, evidence.json, extract.config.json, control-categories.json
adopt/               what goes into the consuming repo: skills, hooks, settings
docs/                design, manifest schema, spec grammar, lint codes, adoption, prior art
tests/               xunit over the fixture
```

MIT.
