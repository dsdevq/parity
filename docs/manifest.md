# Manifest schema (schemaVersion 1)

`manifests/<Form>.json`, one per form class. Lists are sorted; there are no timestamps; the file is
byte-stable for the same input, so `git diff` is meaningful and the spec binds to its SHA-256.

`manifests/<Form>.prev.json` is the manifest before the last re-extract that changed it: `extract`
moves the old file aside only when the new content differs, so running it twice on the same input
keeps the previous generation. One generation is kept; older ones are in git. `module.json` ignores it.
`parity diff <old.json> <new.json>` compares two manifests entry by entry, keyed by id per section
(`form` for the header fields, then `controls`, `events`, `bindings`, `validationRules`, `handlers`,
`methods`, `serviceCalls`, `dynamicUi`; `raw` by `file:line`, `diagnostics` by the debt key
`CODE: Form.member`). It prints one line per entry, `+` added, `-` removed, `~` changed with the fields
that differ (`props.Text`, `source`, `loc`), sorted by id, and exits 0 when identical, 1 when not.
`parity rebind` (see `docs/spec.md`) runs the same comparison between `.prev.json` and the new manifest.

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | string | bump on breaking change |
| `id` | `form:<Class>` | |
| `className`, `namespace` | string | |
| `bases` | string[] | the base-type chain |
| `files` | string[] | partial files, relative to the project directory |
| `caption` | string? | `this.Text` |
| `controls[]` | ControlInfo | includes `$this`; designer-declared components too (categories `nonvisual`, `rule`) |
| `events[]` | EventWiring | `source`: designer, code, dynamic, override, ctor |
| `bindings[]` | BindingInfo | `DataBindings.Add`, `DataSource`, `Properties.DataSource`, grid column `FieldName` |
| `validationRules[]` | ValidationRuleInfo | `SetValidationRule(control, rule)`; the rule's properties are on the rule's control entry |
| `handlers[]` | HandlerInfo | see below |
| `methods[]` | MethodInfo | transitive, depth 1..maxCallDepth; `source` null when only metadata was available |
| `serviceCalls[]` | ServiceCallInfo | aggregated per `svc:` id with callers and argument texts seen |
| `dynamicUi[]` | DynamicUiInfo | controls created in code |
| `raw[]` | RawStatement | designer statements not interpreted |
| `diagnostics[]` | Diagnostic | `REF-MISSING`, `REF-LOAD`, `BASE-UNRESOLVED`, `NO-DESIGNER`, `TYPE-UNRESOLVED`, `RESX-MISSING`, `MEMBER-UNRESOLVED`, `HANDLER-MISSING`, `WIRING-UNKNOWN`, `UNREACHED-METHOD` (says what it checked and where else the name appears), `GRID-NO-COLUMNS` (a grid view, tree list, list view or DataGridView with no designer column, naming the loaded code that touches its `Columns`) |

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

## MethodInfo

`id, display, depth, reads, writes, calls, serviceCalls, unresolved, loc, source, assembly`

## module.json

`schemaVersion, module, project, forms[], serviceCalls[], duplicates[] { hash, kind: exact|near, tokens, members[] }`

## Grid columns: what is extracted

Columns are controls of category `column` whose `parent` is the view (DevExpress `GridView`/`BandedGridView`
under its `GridControl`, `TreeList`, `ListView`, `DataGridView`). They are found when the designer declares
them: `this.colX = new GridColumn()` plus `view.Columns.AddRange(new[] { this.colX, ... })`, with `FieldName`,
`Caption`, `Visible`, `VisibleIndex`, `ColumnEdit` and `OptionsColumn.*` as props. Columns created in code
(`Columns.AddVisible(...)`, `Columns.Add(...)`) are not controls; the view then carries `GRID-NO-COLUMNS`
naming the method that builds them, whose slice is in the manifest to cite. Columns restored from a layout
XML or a base class are invisible to the tool and the diagnostic says so.
