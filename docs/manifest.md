# Manifest schema (schemaVersion 2)

`manifests/<Form>.json`, one per form class. Lists are sorted; there are no timestamps; the file is
byte-stable for the same input, so `git diff` is meaningful and the spec binds to its SHA-256.

`manifests/<Form>.prev.json` is the manifest before the last re-extract that changed it: `extract`
moves the old file aside only when the new content differs, so running it twice on the same input
keeps the previous generation. One generation is kept; older ones are in git. `module.json` ignores it.
`parity diff <old.json> <new.json>` compares two manifests entry by entry, keyed by id per section
(`form` for the header fields, then `controls`, `events`, `bindings`, `validationRules`, `handlers`,
`methods`, `serviceCalls`, `dynamicUi`; `raw` by `file:line`, `diagnostics` by the debt key
`CODE: Form.member`, both including the variants'; `variants` for each variant's header, `delta` by `var:` id,
`variantMethods`, `factories` by `method:` id, `contract` by entry handler and by input or output kind and name,
`sharedState` by `state:` id, `embeds` by `embed:` id). It prints one line per entry, `+` added, `-` removed, `~` changed with the fields
that differ (`props.Text`, `source`, `loc`), sorted by id, and exits 0 when identical, 1 when not.
`parity rebind` (see `docs/spec.md`) runs the same comparison between `.prev.json` and the new manifest.

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | string | bump on breaking change; see Schema history |
| `id` | `form:<Class>` | |
| `className`, `namespace` | string | |
| `bases` | string[] | the base-type chain |
| `files` | string[] | partial files, relative to the project directory |
| `caption` | string? | `this.Text` |
| `controls[]` | ControlInfo | includes `$this`; designer-declared components too (categories `nonvisual`, `rule`) |
| `events[]` | EventWiring | `source`: designer, code, dynamic, override, ctor, entry, reflection |
| `bindings[]` | BindingInfo | `DataBindings.Add`, `DataSource`, `Properties.DataSource`, grid column `FieldName` |
| `validationRules[]` | ValidationRuleInfo | `SetValidationRule(control, rule)`; the rule's properties are on the rule's control entry |
| `handlers[]` | HandlerInfo | see below |
| `methods[]` | MethodInfo | transitive, depth 1..maxCallDepth; `source` null when only metadata was available |
| `serviceCalls[]` | ServiceCallInfo | aggregated per `svc:` id with callers and argument texts seen; `signature`, `request` and `reply` (type templates, never values) and `sites` (each caller with its arguments) feed the dependency catalog |
| `dynamicUi[]` | DynamicUiInfo | controls created in code |
| `constants[]` | ConstantInfo | constants and resource texts the slices and bound model read by name; facts, not ids |
| `raw[]` | RawStatement | designer statements not interpreted |
| `variants[]` | VariantInfo? | only when `variants` is configured and a subclass of this form was found; see Variants |
| `factories[]` | FactoryInfo? | only alongside variants or a configured factory; see Factories |
| `contract` | UnitContract? | the unit's entries, inputs and outputs; see Context around a unit |
| `sharedState[]` | SharedStateUse? | only when `sharedState` is configured and the unit touches a member; see Context around a unit |
| `embeds[]` | EmbedInfo? | other units of the module this unit creates; see Context around a unit |
| `diagnostics[]` | Diagnostic | `REF-MISSING`, `REF-LOAD`, `BASE-UNRESOLVED`, `NO-DESIGNER`, `TYPE-UNRESOLVED`, `RESX-MISSING`, `MEMBER-UNRESOLVED`, `HANDLER-MISSING`, `WIRING-UNKNOWN`, `UNREACHED-METHOD` (says what it checked and where else the name appears), `GRID-NO-COLUMNS` (a grid view, tree list, list view or DataGridView with no designer column, naming the loaded code that touches its `Columns`), `RESOURCE-KEY-MISSING` (a resource key read by a slice or a `[Display]` that the resource's default-culture resx in the project does not define; names the resource, the resx and the readers) |

## Ids

| Prefix | Shape | Cited from |
|---|---|---|
| `form:` | `form:Class` | spec front matter; a `then` may name a form shown |
| `ctrl:` | `ctrl:Class/field`, `ctrl:Class/$this` | Fields rows |
| `dyn:` | `dyn:Class/<creating handler>/<local or field>` | Fields rows |
| `evt:` | `evt:Class/field.Event`, `evt:Class/$this.Load`, `evt:Class/$this.OnLoad`, `evt:Class/$this.ctor(2)` | `covers:` and No behavior |
| `handler:` | `handler:Class/method`, `handler:Class/lambda@L<line>`, `handler:Class/ctor(n)` | `cite:` |
| `method:` | `method:Namespace.Type.Method(paramTypes)`, `...Prop.get` | `cite:` |
| `svc:` | `svc:IService.Method` (containing type's simple name) | Service calls table; `then` text |
| `ev:` | `ev:Class/<n>`, not in the manifest: a record in `evidence/<Class>.json` written by `parity evidence add` | `cite:`; a question closed by evidence |
| `var:` | `var:Variant` (the variant), `var:Variant/<kind>/<name>` (one delta entry) | `covers:` or `cite:` of a rule scoped to that variant, or Variant deltas without a rule |
| `state:` | `state:Type.Member` (simple type name) of a configured shared-state type | a scenario's `exercises`; `then` text |
| `embed:` | `embed:Host/<creating handler or method>/<local or field>` | a scenario's `exercises` |
| `scn:` | `scn:<words>`, not in the manifest: a scenario in `registry/scenarios.json`, versioned | a rule's `scenarios:` as `scn:<id>@<version>`; a fixture's `scenario` |
| `dec:` | `dec:<words>`, not in the manifest: a decision in `registry/decisions.json` | `decided-by dec:<id>`; `closed by decision dec:<id>` |

## ControlInfo

`id, field, type, category, parent, layoutItem, caption, props, bindings, events, loc`

- `parent` follows `Controls.Add`, collection `AddRange` (grid columns under their view, views under
  their grid, layout items under their group), `Parent =`, `GridControl =`.
- `layoutItem` is the DevExpress `LayoutControlItem` whose `Control` is this control; its `Text` is
  usually the label the user sees.
- `props` is a flat map of member path to value text; resx-resolved values also keep `<path>@resx = key`.
  Value collections (`Properties.Items.AddRange`, lookup columns) are kept as `<path>.AddRange = <args>`.
- `category` comes from the control-categories table through the type chain; `unknown` when no entry matches;
  `local` for objects declared as locals inside `InitializeComponent` (image options, reduce operations).
- Names the designer uses without creating them are resolved, not diagnosed: `@local` marks a designer local,
  `@inherited = <declaring type>` a field or property found on a base form (metadata or source), `@declaredOnly`
  a field of the form created elsewhere (a constructor, for instance). A form property path such as
  `this.Appearance.BackColor` folds into the `$this` entry's props. Only a name found nowhere is `MEMBER-UNRESOLVED`.
- Bound model facts (see Grid columns) are props too: `@modelType` on a BindingSource, grid, view or editor
  whose `DataSource` carries rows of a project type (several types joined by `; `), and on a column
  `@display`, `@display@resx = <resource>.<key>`, `@browsable`, `@required`, `@stringLength` (`n` or `min..max`).
- Calls on a control inside the designer that are not tree, binding or validation shapes are kept as
  `<path>.<Method>()` props with the argument texts, one entry per call joined by `; `
  (`Images.SetKeyName() = 0, "a"; 1, "b"`).

## HandlerInfo

`id, name, kind, events, reads, writes, calls, serviceCalls, unresolved, loc, source`

- `kind`: validation, action, change, lifecycle, timer, ui, ctor, entry. Validation also when the body sets or
  clears provider errors. `entry` is a public, internal or protected method of the form that nothing wires:
  the way hosting forms hand the form its inputs. It gets a pseudo-event `evt:Form/$this.<Method>` (source
  `entry`) so a rule can cover it, and its callees are sliced like a handler's.
- `calls` lists `method:` ids including those beyond `maxCallDepth` (which then have no `methods[]` entry).
- `diagnostics[]` also carries `VARIANT-CONFIG` (a `variants` entry that cannot be applied: no id, a missing
  subclass, a listed subclass whose base chain reaches no form of the main project, a variant with no subclass
  of any form of the main project, a class the compiler does not resolve as deriving from its base, a class
  claimed twice) and `VARIANT-INTERMEDIATE` (see Variants). One that names a unit (`<Form>: ...`) is on that
  unit's manifest only; one tied to no unit (`variant <id>: ...`) is on every manifest of the run and in the
  extract log, so a tenant that is not described is never silent. A subclass of a form the run's glob does not
  select is not diagnosed: that form's own run describes it. Before compiling, only a written base chain that stays
  in the main project's files and reaches no `formBaseTypes` entry (full or simple name, `Ns.*` wildcards included)
  is diagnosed; a chain through a base the main project does not declare (one from a referenced assembly) is
  loaded as a candidate, and the compiler's type chain decides.

## MethodInfo

`id, display, depth, reads, writes, calls, serviceCalls, unresolved, loc, source, assembly`

## ConstantInfo

`kind, type, member, valueType, value, declared, cultures, firstUse, usedBy`

- `kind`: `const` (a `const` field), `readonly` (a `static readonly` field with a constant initializer) or
  `resource` (a resource text). Only fields of project types are listed, never framework ones or enum members;
  a `static readonly` set from anything but a literal is state, not a fact, and is skipped.
- `type`, `member`: the declaring type's full name and the field name; for a resource, the resource name
  (a generated resource class's full name, or the name given to `new ResourceManager(...)`) and the key.
- `value`: the literal, invariant culture (`true`/`false`, `40`, the string unquoted, an enum value as
  `Enum.Member`). For a resource, the default-culture resx text; null when the key is missing (then
  `RESOURCE-KEY-MISSING`) or when the resource's resx is not one of the project's files (no diagnostic: the
  tool cannot tell).
- `declared`: the declarator, or the resx `<data>` element, as `file:line`.
- `cultures`: for a resource, the other cultures whose resx (`X.fr.resx`, `X.de-CH.resx` beside `X.resx`)
  defines the key. Only the targeted project's resx files are read.
- `firstUse`: the first read inside a handler or method slice, else the first read from a model annotation.
- `usedBy`: the `handler:`/`method:` ids whose slices read it, and the `ctrl:` column a model annotation feeds.

Resource reads are a generated class's static property (`Captions.lsX`; the key is the literal its getter passes to
`GetString`, so `Policy_log` reads the key `Policy log`, and the property name only when the getter has no source),
`R.ResourceManager.GetString("key")`,
`GetString("key")` on a `ResourceManager` created from `typeof(R)` or a name, and `[Display(ResourceType = typeof(R), Name = "key")]`.

## Variants

A variant is a subclass of the form that some tenants get instead of it (configured under `variants`, see
adopt.md). It is not a unit of its own: it has no manifest file and no spec. The base manifest describes the
form as written, and `variants[]` says, per variant, what differs.

`id, name, project, class, bases, files, delta, layout, methods, raw, diagnostics`

- `project` is the variant's csproj as configured, null when the subclass lives in the main project.
  `files` and every `loc` are relative to the main project directory, so a sibling project reads
  `../Other/Forms/X.cs`.
- `bases` is the subclass's own chain, starting at the form it customises.
- `delta[]` is one entry per difference, `id, kind, target, detail, type, category, changes, events, reads,
  writes, calls, serviceCalls, source, loc`. Every entry carries the variant's file:line.

| kind | id | target | what it is |
|---|---|---|---|
| `added` | `var:V/added/<field>`, `var:V/added/<method>/<local>` | the container it is added to | a control the variant creates (designer, or code as in `dyn:`) |
| `removed` | `var:V/removed/<field>` | `ctrl:Base/<field>` | `Controls.Remove` of an inherited control |
| `retagged` | `var:V/retagged/<field>` | `ctrl:Base/<field>` (`$this` for the form) | an inherited control with a different caption, text, visibility, enabled state, layout parent or other behavioural property; `changes[]` holds `property, base, value, loc` (base null when the base never sets it) |
| `event` | `var:V/event/<field>.<Event>` | the control | a wiring the variant adds; `detail` names the handler entry |
| `override` | `var:V/override/<method>` | the base `handler:` or `method:` id | an override of a handler or method; `detail` is `calls base` or `replaces base` |
| `handler` | `var:V/handler/<method>` | - | a new method wired by the variant, sliced like a handler |
| `ctor` | `var:V/ctor`, `var:V/ctor(n)` | - | a constructor doing more than re-tags, removals, added controls and wiring (those become their own entries) |
| `service-added` | `var:V/service-added/<IService.Method>` | the `svc:` id | a call the variant makes that the base does not |
| `service-removed` | `var:V/service-removed/<IService.Method>` | the `svc:` id | a base call whose only callers the variant replaces |
| `constant` | `var:V/constant/<Type>.<Member>` | the base's `<Type>.<Member>` text, null when the base reads no such member | a constant or resource text the variant's code reads that the base does not read as such (`type` is the declaring type or resource, `detail` is `const`, `readonly` or `resource`); `changes[]` has one `value` entry, `base` the base's value (null for a read the base does not make), `value` the variant's, `loc` its first use |

- `layout[]` holds changes to layout-only properties (location, size, tab index) of inherited controls. They
  are reported, not gated.
- `methods[]` are the variant's sliced callees; `raw[]` and `diagnostics[]` are the variant's own and feed
  `COV-RAW` like the base's.
- Classes between the base and the configured subclass are not a delta: they are diagnosed as
  `VARIANT-INTERMEDIATE`, and only the subclass's own declarations are read.

## Factories

`factories[]`: methods that hand out the form or one of its variants by a key, so "which class does tenant X
get" is a cited fact. `id` is the method's `method:` id (citeable from a rule), `on` the key expression or
dictionary field, `loc` the method. `cases[]` is `key, class, variant, loc`: `variant` is `base` for the form
itself, the variant name, or null for another class. Understood shapes: a `switch` statement or expression on
the key, an if-chain comparing the key to string literals (`==`, `Equals`, `string.Equals`), and a dictionary
field from key to `typeof(T)` or `new T()` that the method reads. A `when` clause, or a condition between a
keyed label and the `new T()`, joins the key with `&&` (`LRK && pilot`), and every form a keyed case guards
is a case of its own. A form the method still creates outside
these shapes is the `default` case only when nothing conditional guards it (an unconditional return, an
`else` branch, or the miss of a dictionary lookup); behind any other condition it is a case keyed by that
condition's source text. Methods are read from files that instantiate two or more classes of the same form
family, plus any `factories` configured on a variant: a type, or `Type.Method`, simple or namespace-qualified.
An entry that matches no type, or from which no factory is read, is a `VARIANT-CONFIG` diagnostic; the second
is checked only in a run that describes the entry's variant.

## Context around a unit

A unit is a form class the glob selects. What crosses its edge is recorded in three sections, all derived
from the handlers, methods and declarations the extract already reads, so they cost no extra compilation.

**`contract`** `{ entries[], inputs[], outputs[] }`.
`entries[]` is `handler, kind, signature, loc, invokedFrom?`: every way in from outside. `kind` is one of:

- `ctor`;
- `entry`: a non-private method nothing in the unit wires;
- `reflection`: a private method whose name is the first argument, as a string literal, of a
  `GetMethod`, `GetProperty` or `InvokeMember` call on `typeof(Unit)` or `x.GetType()` in a loaded file.
  It becomes a handler with an `evt:Unit/$this.<name>` pseudo-event of source `reflection`, so it is
  coverable like any entry. `invokedFrom` lists the calling `file:line`s.

`inputs[]` and `outputs[]` are `kind, name, type, via[], values?`. `via` lists the handler and method ids that
read or write the value. Input kinds:

- `param`: of an entry;
- `property`: a property of the unit with a non-private setter, which the unit reads;
- `state`: a shared-state member read;
- `host`: a member read on an object handed in through a ctor or entry parameter.

Output kinds:

- `property`: a property of the unit with a public getter, which the unit writes;
- `dialog-result`;
- `state`: a shared-state member written;
- `host`: a member written on a handed-in object;
- `host-call`: a method called on one.

**`sharedState[]`** `{ id, type, member, valueType, reads[], writes[] }`. Each entry is a member of a type in
the config's `sharedState` list that the unit reads or writes. The pass covers the unit's handlers and the
in-source methods they reach, down to `sharedStateDepth` (default `maxCallDepth`, 0 = handlers only). It stops
at another unit's code, which is described in that unit's manifest. The analysis is syntactic:

- a write is the left side of an assignment or an `out` argument;
- a compound assignment, `++`, `--` or `ref` argument is both;
- anything else is a read.

The extract log prints `shared-state: <n> bodies to depth <d>, <n> access(es), <t>s`, or
`shared-state: skipped` when `sharedState` is empty. `docs/design.md` explains why the pass is syntactic.

**`embeds[]`** `{ id, unit, class, via, mode, factory?, factoryArgs?, calls[], sets[], reads[], loc, statement }`.
Each entry is another unit of the module that this unit creates. `mode` is one of:

- `dialog` (`ShowDialog`);
- `show`;
- `embedded` (added to a container; it is then also a `dyn:` entry);
- `created`.

`factory` is the `method:` id when the instance comes from a factory rather than `new`. That factory's cases
(Factories, #20) say which variant each host key gets. `calls`, `sets` and `reads` are `member, args, loc,
reflection?`: what the host does with the instance. A `GetMethod(...).Invoke` on it is a call with
`reflection: true`. They come from the body that creates the instance; when the host keeps it in a field or
property, `reads` also holds what the host's other handlers and methods read from it (a pay button reading
the embedded panel's `SelectedAccount`), each at the line of the read. Calls and sets made from those other
bodies are not recorded yet (`docs/pilot-metrics.md`, Known gaps).

## Schema history

**2.** A unit that a handler opens with `new X()` and does not add to a container no longer gets a `dyn:` entry.
It gets an `embed:` entry, and the callee's body is no longer sliced into the host: the call is recorded as a
call, and the callee's code is in its own manifest. A spec that cited a `dyn:` id of such a unit, or a
`method:` id that was reached only through another unit, loses its target.

Migration: re-extract the module, which moves each changed manifest to `.prev.json`. `parity diff
manifests/<Form>.prev.json manifests/<Form>.json` lists the dropped `dyn:` and `method:` ids and the new
`embed:` ids, and `parity rebind <spec>` lists each spec that cites a dropped id as stale. Cite the unit's own
manifest, or the `embed:` id, instead.

## module.json

`schemaVersion, module, project, forms[], serviceCalls[], duplicates[] { hash, kind: exact|near, tokens, members[] }`,
and the three module-wide context graphs. Each is built from the manifests on disk, so `module.json`
accumulates across runs like `forms`.

- `sharedState { types[], fields[] }`. Each field is `id, version, type, valueType, producers[], consumers[],
  retired?`, where each producer or consumer is `{ unit, via }`.
  - `version` starts at 1 and goes up when the member's value type changes.
  - A field no manifest touches any more stays, with `retired: true`. Its id is never reused for something else.
  - Absent when `sharedState` is not configured.
- `composition[] { unit, hosts[] }`: for each embedded unit, every host. A host is `unit, embed, mode,
  factory?, variants?, setup[]`. `variants` lists the factory's cases as `key -> variant`. `setup` lists the
  calls and sets the host makes before showing the unit, a reflective call marked `(reflection)`.
- `dependencies[]`: one per `svc:` id. Each is `id, type, method, signature, request, reply, callers[]`,
  where a caller is `{ unit, via, args[] }`. Error cases and fixture provenance are not facts of the code.
  They live in `registry/dependencies.json` and `fixtures/`, and `parity registry catalog` joins the three.

## Grid columns: what is extracted

Columns are controls of category `column` whose `parent` is the view (DevExpress `GridView`/`BandedGridView`
under its `GridControl`, `TreeList`, `ListView`, `DataGridView`). They are found when the designer declares
them: `this.colX = new GridColumn()` plus `view.Columns.AddRange(new[] { this.colX, ... })`, with `FieldName`,
`Caption`, `Visible`, `VisibleIndex`, `ColumnEdit` and `OptionsColumn.*` as props. Columns created in code
(`Columns.AddVisible(...)`, `Columns.Add(...)`) are not controls; the view then carries `GRID-NO-COLUMNS`
naming the method that builds them, whose slice is in the manifest to cite. Columns restored from a layout
XML or a base class are invisible to the tool and the diagnostic says so.

The bound model's data annotations are column facts too. A model type comes from `DataSource = typeof(T)`
(designer or code), or a code `DataSource` whose value is `T`, `T[]` or an `IEnumerable<T>` of a project type,
and follows BindingSource-to-grid references and the grid to its views; the owner gets `@modelType`. Each
public property of `T` and of its project-local base types (the walk stops at the first framework base, so
`Component.Site` and the like never appear) lands on the column whose `FieldName` or `DataPropertyName` names it:
`[Display(Name)]` or `[DisplayName]` as `@display` (resolved through the resource when `ResourceType` is set,
then `@display@resx`), `[Browsable(false)]` as `@browsable = false`, `[Required]` as `@required = true`,
`[StringLength]` as `@stringLength`. A column without a designer caption takes `@display` as its `caption`, as
the grid would. A property no designer column names is summarized on the view as
`@model.<Property> = Browsable(false), Required, ...`, which is how an auto-generated or code-built grid's
hidden and required fields show up.
