# Manifest schema (schemaVersion 1)

`manifests/<Form>.json`, one per form class. Lists are sorted; there are no timestamps; the file is
byte-stable for the same input, so `git diff` is meaningful and the spec binds to its SHA-256.

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
| `constants[]` | ConstantInfo | constants and resource texts the slices and bound model read by name; facts, not ids |
| `raw[]` | RawStatement | designer statements not interpreted |
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
