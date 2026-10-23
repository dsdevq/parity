using System.Text;
using Parity.Model;

namespace Parity.Render;

/// <summary>The view the agent reads. Every id it may cite is printed verbatim; every slice is inline.</summary>
public static class MarkdownRenderer
{
    public static string Render(FormManifest m, string hash)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {m.ClassName}");
        sb.AppendLine();
        sb.AppendLine($"- id: `{m.Id}`");
        sb.AppendLine($"- manifest: `{hash}`  (copy into the spec front matter)");
        sb.AppendLine($"- namespace: `{m.Namespace}`");
        sb.AppendLine($"- bases: {string.Join(" -> ", m.Bases.Select(b => $"`{b}`"))}");
        sb.AppendLine($"- files: {string.Join(", ", m.Files.Select(f => $"`{f}`"))}");
        if (m.Caption != null) sb.AppendLine($"- caption: {m.Caption}");
        sb.AppendLine();
        sb.AppendLine("Ids you may cite in a spec: `ctrl:` and `dyn:` (Fields rows), `evt:` (covers), `handler:` and `method:` (cite), `svc:` (service calls).");
        if (m.Variants is { Count: > 0 } vs)
            sb.AppendLine($"This unit has {vs.Count} variant(s) ({string.Join(", ", vs.Select(v => v.Name))}): everything up to \"Variants\" is the base; `var:` delta ids go in rules scoped `variants:` (see Variants).");
        sb.AppendLine();

        sb.AppendLine("## Controls");
        sb.AppendLine();
        sb.AppendLine("| id | type | category | parent | caption | binding | events | loc |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in m.Controls)
        {
            var parent = c.Parent == null ? "" : Short(c.Parent);
            var events = string.Join(", ", c.Events.Select(e => $"{e.Key}->{e.Value}"));
            sb.AppendLine($"| `{c.Id}` | `{Simple(c.Type)}` | {c.Category} | {parent} | {Esc(c.Caption)} | {Esc(string.Join("; ", c.Bindings))} | {Esc(events)} | {LocStr(c.Loc)} |");
        }
        sb.AppendLine();

        var interesting = m.Controls.Where(c => c.Props.Any(p => IsInterestingProp(p.Key))).ToList();
        if (interesting.Count > 0)
        {
            sb.AppendLine("### Behavioral properties");
            sb.AppendLine();
            sb.AppendLine("Layout-only properties (Location, Size, TabIndex, Name, Anchor, Dock) are in the JSON, not here.");
            sb.AppendLine();
            foreach (var c in interesting)
            {
                sb.AppendLine($"- `{c.Id}`");
                foreach (var p in c.Props.Where(p => IsInterestingProp(p.Key)))
                    sb.AppendLine($"  - {p.Key} = {Esc(p.Value)}");
            }
            sb.AppendLine();
        }

        if (m.DynamicUi.Count > 0)
        {
            sb.AppendLine("## Dynamic UI (created in code, not in the designer)");
            sb.AppendLine();
            sb.AppendLine("| id | type | added to | created in | loc | statement |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var d in m.DynamicUi)
                sb.AppendLine($"| `{d.Id}` | `{Simple(d.Type)}` | {(d.AddedTo == null ? "" : $"`{d.AddedTo}`")} | `{d.Handler}` | {LocStr(d.Loc)} | `{Esc(d.Statement)}` |");
            sb.AppendLine();
        }

        sb.AppendLine("## Events");
        sb.AppendLine();
        sb.AppendLine("| id | handler | source | loc |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var e in m.Events) sb.AppendLine($"| `{e.Id}` | `{e.Handler}` | {e.Source} | {LocStr(e.Loc)} |");
        sb.AppendLine();

        if (m.Bindings.Count > 0)
        {
            sb.AppendLine("## Bindings");
            sb.AppendLine();
            sb.AppendLine("| control | property | source | member | where | loc |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var b in m.Bindings) sb.AppendLine($"| `{b.Control}` | {b.Property} | {Esc(b.DataSource)} | {Esc(b.Member)} | {b.Source} | {LocStr(b.Loc)} |");
            sb.AppendLine();
        }

        if (m.ValidationRules.Count > 0)
        {
            sb.AppendLine("## Validation rules (provider-based)");
            sb.AppendLine();
            foreach (var r in m.ValidationRules)
            {
                var ruleCtl = m.Controls.FirstOrDefault(c => c.Field == r.Rule);
                var props = ruleCtl == null ? "" : " " + string.Join(", ", ruleCtl.Props.Select(p => $"{p.Key}={p.Value}"));
                sb.AppendLine($"- `{r.Id}` on `{r.Control}`: rule `{r.Rule}`{props} ({r.Source}, {LocStr(r.Loc)})");
            }
            sb.AppendLine();
        }

        var constants = m.Constants ?? new List<ConstantInfo>(); // absent in manifests written before #19
        if (constants.Count > 0)
        {
            sb.AppendLine("## Constants and resource texts");
            sb.AppendLine();
            sb.AppendLine("Static facts the slices and the bound model read by name, with their values. Facts, not ids: cite the handler or method that uses them.");
            sb.AppendLine();
            sb.AppendLine("| kind | name | value | type | declared | other cultures | first use | used by |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var c in constants)
                sb.AppendLine($"| {c.Kind} | `{c.Type}.{c.Member}` | {(c.Value == null ? NotFound(c) : $"`{Esc(Literal(c))}`")} | {c.ValueType} | {LocStr(c.Declared)} | {string.Join(", ", c.Cultures)} | {LocStr(c.FirstUse)} | {Ids(c.UsedBy)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Service calls");
        sb.AppendLine();
        if (m.ServiceCalls.Count == 0) sb.AppendLine("none found behind the configured service boundary");
        else
        {
            sb.AppendLine("| id | type | called from | args seen |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in m.ServiceCalls)
                sb.AppendLine($"| `{s.Id}` | `{s.Type}` | {string.Join(", ", s.CalledFrom.Select(x => $"`{x}`"))} | {Esc(string.Join(" / ", s.Args))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Handlers");
        sb.AppendLine();
        foreach (var h in m.Handlers)
        {
            sb.AppendLine($"### `{h.Id}`");
            sb.AppendLine();
            sb.AppendLine($"- kind: {h.Kind}");
            sb.AppendLine($"- events: {Ids(h.Events)}");
            sb.AppendLine($"- reads: {Ids(h.Reads)}");
            sb.AppendLine($"- writes: {Ids(h.Writes)}");
            sb.AppendLine($"- calls: {Ids(h.Calls)}");
            sb.AppendLine($"- service calls: {Ids(h.ServiceCalls)}");
            if (h.Unresolved.Count > 0) sb.AppendLine($"- unresolved: {string.Join(", ", h.Unresolved.Select(u => $"`{Esc(u)}`"))}");
            sb.AppendLine($"- loc: {LocStr(h.Loc)}");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(Annotate(h.Source, h.Loc, constants));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (m.Methods.Count > 0)
        {
            sb.AppendLine("## Methods reached from handlers");
            sb.AppendLine();
            foreach (var x in m.Methods)
            {
                sb.AppendLine($"### `{x.Id}`");
                sb.AppendLine();
                sb.AppendLine($"- depth: {x.Depth}");
                if (x.Reads.Count > 0) sb.AppendLine($"- reads: {Ids(x.Reads)}");
                if (x.Writes.Count > 0) sb.AppendLine($"- writes: {Ids(x.Writes)}");
                if (x.Calls.Count > 0) sb.AppendLine($"- calls: {Ids(x.Calls)}");
                if (x.ServiceCalls.Count > 0) sb.AppendLine($"- service calls: {Ids(x.ServiceCalls)}");
                if (x.Unresolved.Count > 0) sb.AppendLine($"- unresolved: {string.Join(", ", x.Unresolved.Select(u => $"`{Esc(u)}`"))}");
                if (x.Source != null)
                {
                    sb.AppendLine($"- loc: {LocStr(x.Loc)}");
                    sb.AppendLine();
                    sb.AppendLine("```csharp");
                    sb.AppendLine(Annotate(x.Source, x.Loc, constants));
                    sb.AppendLine("```");
                }
                else sb.AppendLine($"- source: not loaded (assembly `{x.Assembly}`); raise maxCallDepth or add the file if a rule needs it");
                sb.AppendLine();
            }
        }

        if (m.Variants is { Count: > 0 }) RenderVariants(sb, m);
        if (m.Factories is { Count: > 0 }) RenderFactories(sb, m.Factories);

        sb.AppendLine("## Raw (designer statements the extractor did not interpret)");
        sb.AppendLine();
        if (m.Raw.Count == 0) sb.AppendLine("none");
        else foreach (var r in m.Raw) sb.AppendLine($"- {LocStr(r.Loc)}: `{Esc(r.Statement)}`");
        sb.AppendLine();

        sb.AppendLine("## Diagnostics");
        sb.AppendLine();
        if (m.Diagnostics.Count == 0) sb.AppendLine("none");
        else foreach (var d in m.Diagnostics) sb.AppendLine($"- {d.Code}: {Esc(d.Message)}{(d.Loc == null ? "" : " (" + LocStr(d.Loc) + ")")}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string Literal(ConstantInfo c) => c.ValueType is "string" or "char" ? System.Text.Json.JsonSerializer.Serialize(c.Value) : c.Value!;

    private static string NotFound(ConstantInfo c) => c.Kind == "resource" ? "not found in the project's default-culture resx" : "unknown";

    /// <summary>The slice with each constant's value beside its first use (md only; the JSON source stays verbatim).</summary>
    private static string Annotate(string source, Loc? loc, List<ConstantInfo> constants)
    {
        if (loc == null) return source;
        var notes = constants.Where(c => c.FirstUse != null && c.FirstUse.File == loc.File && c.FirstUse.Start >= loc.Start && c.FirstUse.Start <= loc.End)
            .GroupBy(c => c.FirstUse!.Start - loc.Start).ToList();
        if (notes.Count == 0) return source;
        var lines = source.Split('\n');
        foreach (var g in notes.Where(g => g.Key < lines.Length))
            lines[g.Key] += "  // " + string.Join("; ", g.Select(c => $"{Simple(c.Type)}.{c.Member}" + (c.Value == null ? ": " + NotFound(c) : " = " + Literal(c))));
        return string.Join('\n', lines);
    }

    private static void RenderVariants(StringBuilder sb, FormManifest m)
    {
        sb.AppendLine("## Variants");
        sb.AppendLine();
        sb.AppendLine("Each variant is a subclass that some tenants get instead of the base: it inherits everything above except its delta. "
            + "Every delta id must be covered (`covers`/`cite`) by a rule whose `variants:` names the variant, or be listed under \"## Variant deltas without a rule\" in the spec.");
        sb.AppendLine();
        foreach (var v in m.Variants!)
        {
            sb.AppendLine($"### Variant {v.Name}");
            sb.AppendLine();
            sb.AppendLine($"- id: `{v.Id}`");
            sb.AppendLine($"- class: `{v.Class}`");
            if (v.Project != null) sb.AppendLine($"- project: `{v.Project}`");
            sb.AppendLine($"- files: {string.Join(", ", v.Files.Select(f => $"`{f}`"))}");
            sb.AppendLine();
            sb.AppendLine("| delta | kind | target | detail | loc |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in v.Delta)
                sb.AppendLine($"| `{d.Id}` | {d.Kind} | {(d.Target == null ? "" : $"`{d.Target}`")} | {Esc(d.Detail)} | {LocStr(d.Loc)} |");
            sb.AppendLine();
            var changed = v.Delta.Where(d => d.Changes is { Count: > 0 }).ToList();
            if (changed.Count > 0)
            {
                sb.AppendLine("#### Properties");
                sb.AppendLine();
                foreach (var d in changed)
                {
                    sb.AppendLine($"- `{d.Id}`");
                    foreach (var c in d.Changes!)
                        sb.AppendLine($"  - {c.Property}: {(d.Kind == "retagged" ? (c.Base == null ? "(not set in the base) -> " : $"{Esc(c.Base)} -> ") : d.Kind == "constant" && c.Base != null ? $"{Esc(c.Base)} -> " : "")}{Esc(c.Value)}{(c.Loc == null ? "" : " " + LocStr(c.Loc))}");
                }
                sb.AppendLine();
            }
            foreach (var d in v.Delta.Where(d => d.Source != null && d.Kind is "override" or "handler" or "ctor"))
            {
                sb.AppendLine($"#### `{d.Id}`");
                sb.AppendLine();
                sb.AppendLine($"- kind: {d.Kind}{(d.Detail == null ? "" : $" ({Esc(d.Detail)})")}");
                if (d.Target != null) sb.AppendLine($"- overrides: `{d.Target}`");
                if (d.Events is { Count: > 0 }) sb.AppendLine($"- events: {Ids(d.Events)}");
                sb.AppendLine($"- reads: {Ids(d.Reads ?? new())}");
                sb.AppendLine($"- writes: {Ids(d.Writes ?? new())}");
                sb.AppendLine($"- calls: {Ids(d.Calls ?? new())}");
                sb.AppendLine($"- service calls: {Ids(d.ServiceCalls ?? new())}");
                sb.AppendLine($"- loc: {LocStr(d.Loc)}");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(d.Source);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (v.Methods.Count > 0)
            {
                sb.AppendLine("#### Methods reached from the variant's code");
                sb.AppendLine();
                foreach (var x in v.Methods)
                    sb.AppendLine($"- `{x.Id}` (depth {x.Depth}{(x.Loc == null ? $", not loaded: `{x.Assembly}`" : ", " + LocStr(x.Loc))})");
                sb.AppendLine();
            }
            if (v.Layout.Count > 0)
            {
                sb.AppendLine("#### Layout-only changes (not delta entries)");
                sb.AppendLine();
                foreach (var c in v.Layout) sb.AppendLine($"- `{c.Control}` {c.Property}: {Esc(c.Base)} -> {Esc(c.Value)}");
                sb.AppendLine();
            }
            sb.AppendLine($"#### Raw and diagnostics ({v.Name})");
            sb.AppendLine();
            if (v.Raw.Count == 0 && v.Diagnostics.Count == 0) sb.AppendLine("none");
            foreach (var r in v.Raw) sb.AppendLine($"- raw {LocStr(r.Loc)}: `{Esc(r.Statement)}`");
            foreach (var d in v.Diagnostics) sb.AppendLine($"- {d.Code}: {Esc(d.Message)}{(d.Loc == null ? "" : " (" + LocStr(d.Loc) + ")")}");
            sb.AppendLine();
        }
    }

    private static void RenderFactories(StringBuilder sb, List<FactoryInfo> factories)
    {
        sb.AppendLine("## Factories");
        sb.AppendLine();
        sb.AppendLine("Which class a factory hands out for which key; `base` is this unit. Cite the factory id when a rule depends on who gets which variant.");
        sb.AppendLine();
        sb.AppendLine("| factory | on | key | class | variant | loc |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var f in factories)
            foreach (var c in f.Cases)
                sb.AppendLine($"| `{f.Id}` | {Esc(f.On)} | `{Esc(c.Key)}` | `{Simple(c.Class)}` | {c.Variant ?? "(other form)"} | {LocStr(c.Loc)} |");
        sb.AppendLine();
    }

    /// <summary>False for layout-only properties; a variant's change to one of those is layout, not a retag.</summary>
    public static bool IsInterestingProp(string key) =>
        key is not ("Location" or "Size" or "TabIndex" or "Name" or "Anchor" or "Dock" or "TextSize" or "CustomizationFormText" or "Margin" or "Padding" or "AutoSize" or "Font" or "ForeColor" or "BackColor" or "TabStop" or "ImeMode" or "Appearance" or "StyleController" or "AllowHide" or "TextLocation" or "TextVisible" or "GroupBordersVisible")
        && !key.StartsWith("Appearance", StringComparison.Ordinal) && !key.StartsWith("Location", StringComparison.Ordinal) && !key.StartsWith("Size", StringComparison.Ordinal)
        && !key.EndsWith("@resx", StringComparison.Ordinal);

    private static string Ids(List<string> ids) => ids.Count == 0 ? "-" : string.Join(", ", ids.Select(i => $"`{i}`"));
    private static string Short(string id) => $"`{id}`";
    private static string Simple(string type) => type[(type.LastIndexOf('.') + 1)..];
    private static string LocStr(Loc? l) => l == null ? "" : $"`{l.File}:{l.Start}-{l.End}`";
    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
}
