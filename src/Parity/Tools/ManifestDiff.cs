using System.Text;
using System.Text.Json;
using Parity.Lint;
using Parity.Model;
using Parity.Passes;

namespace Parity.Tools;

/// <summary>One entry that differs between two manifests. Kind is '+' added, '-' removed, '~' changed.</summary>
public sealed record EntryChange(string Section, string Id, char Kind, List<string> Fields)
{
    /// <summary>A change that can alter what a spec says: removed, or changed in more than its location.</summary>
    public bool Significant => Kind == '-' || (Kind == '~' && Fields.Any(f => f != "loc"));
}

/// <summary>
/// Compares two manifests of one form entry by entry, keyed by id per section, so a re-extract says what it
/// changed (issue #25). Entries without an id are keyed the way the debt file acknowledges them: raw statements
/// by file:line, diagnostics by <see cref="SpecLinter.DebtKey"/>. The form's own header fields are the
/// <c>form</c> section. A changed entry names the top-level fields that differ; object fields (props, events
/// of a control) are named down to their keys, except <c>loc</c>, which is one value. Variants (issue #20) are a
/// header per variant, one <c>delta</c> entry per var: id and the variants' sliced methods; their raw statements and
/// diagnostics join the base's sections, keyed the same way.
/// </summary>
public static class ManifestDiff
{
    public static readonly string[] Sections = { "form", "controls", "events", "bindings", "validationRules", "handlers", "methods", "serviceCalls", "dynamicUi", "raw", "diagnostics", "variants", "delta", "variantMethods", "factories", "contract", "sharedState", "embeds" };

    public static FormManifest Load(string path) => JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(path), Json.Options)!;

    public static List<EntryChange> Compare(FormManifest a, FormManifest b)
    {
        var changes = new List<EntryChange>();
        foreach (var section in Sections)
        {
            var left = Entries(a, section); var right = Entries(b, section);
            foreach (var id in left.Keys.Union(right.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!right.TryGetValue(id, out var r)) changes.Add(new EntryChange(section, id, '-', new()));
                else if (!left.TryGetValue(id, out var l)) changes.Add(new EntryChange(section, id, '+', new()));
                else if (Fields(l, r) is { Count: > 0 } fields) changes.Add(new EntryChange(section, id, '~', fields));
            }
        }
        return changes;
    }

    /// <summary>Stable text: a header with both hashes, then per section one line per entry, sorted by id; then a count line.</summary>
    public static string Render(string oldPath, string newPath, List<EntryChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- {oldPath} {ManifestHash.Of(oldPath)}");
        sb.AppendLine($"+++ {newPath} {ManifestHash.Of(newPath)}");
        foreach (var g in changes.GroupBy(c => c.Section))
        {
            sb.AppendLine(g.Key);
            foreach (var c in g) sb.AppendLine(Line(c));
        }
        sb.AppendLine(changes.Count == 0 ? "identical"
            : $"{changes.Count} difference(s): {changes.Count(c => c.Kind == '+')} added, {changes.Count(c => c.Kind == '-')} removed, {changes.Count(c => c.Kind == '~')} changed");
        return sb.ToString();
    }

    public static string Line(EntryChange c) => $"  {c.Kind} {c.Id}" + (c.Fields.Count > 0 ? "  " + string.Join(", ", c.Fields) : "");

    private static Dictionary<string, List<JsonElement>> Entries(FormManifest m, string section)
    {
        IEnumerable<(string Id, object Entry)> items = section switch
        {
            "form" => new[] { (m.Id, (object)new { m.SchemaVersion, m.Id, m.ClassName, m.Namespace, m.Bases, m.Files, m.Caption }) },
            "controls" => m.Controls.Select(x => (x.Id, (object)x)),
            "events" => m.Events.Select(x => (x.Id, (object)x)),
            "bindings" => m.Bindings.Select(x => (x.Id, (object)x)),
            "validationRules" => m.ValidationRules.Select(x => (x.Id, (object)x)),
            "handlers" => m.Handlers.Select(x => (x.Id, (object)x)),
            "methods" => m.Methods.Select(x => (x.Id, (object)x)),
            "serviceCalls" => m.ServiceCalls.Select(x => (x.Id, (object)x)),
            "dynamicUi" => m.DynamicUi.Select(x => (x.Id, (object)x)),
            "raw" => m.Raw.Concat(Variants(m).SelectMany(v => v.Raw)).Select(x => ($"{x.Loc.File}:{x.Loc.Start}", (object)x)),
            "diagnostics" => m.Diagnostics.Concat(Variants(m).SelectMany(v => v.Diagnostics)).Select(x => (SpecLinter.DebtKey(x.Code, x.Message), (object)x)),
            "variants" => Variants(m).Select(v => (v.Id, (object)new { v.Id, v.Name, v.Project, v.Class, v.Bases, v.Files, v.Layout })),
            "delta" => Variants(m).SelectMany(v => v.Delta).Select(x => (x.Id, (object)x)),
            "variantMethods" => Variants(m).SelectMany(v => v.Methods.Select(x => ($"{v.Id} {x.Id}", (object)x))),
            "factories" => (m.Factories ?? new()).Select(x => (x.Id, (object)x)),
            "contract" => m.Contract == null ? Array.Empty<(string, object)>() : m.Contract.Entries.Select(x => ($"entry {x.Handler}", (object)x))
                .Concat(m.Contract.Inputs.Select(x => ($"input {x.Kind} {x.Name}", (object)x)))
                .Concat(m.Contract.Outputs.Select(x => ($"output {x.Kind} {x.Name}", (object)x))),
            "sharedState" => (m.SharedState ?? new()).Select(x => (x.Id, (object)x)),
            "embeds" => (m.Embeds ?? new()).Select(x => (x.Id, (object)x)),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        // an id can repeat (one event wired to two handlers, a binding set twice): all entries under it are compared together
        return items.GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(x => JsonSerializer.SerializeToElement(x.Entry, x.Entry.GetType(), Json.Options)).OrderBy(e => e.GetRawText(), StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    private static List<VariantInfo> Variants(FormManifest m) => m.Variants ?? new();

    private static List<string> Fields(List<JsonElement> a, List<JsonElement> b)
    {
        if (a.Count != b.Count) return new() { $"entries {a.Count} -> {b.Count}" };
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < a.Count; i++) Diff(a[i], b[i], "", fields);
        return fields.ToList();
    }

    private static void Diff(JsonElement a, JsonElement b, string path, SortedSet<string> into)
    {
        var names = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        foreach (var n in names)
        {
            var hasA = a.TryGetProperty(n, out var va); var hasB = b.TryGetProperty(n, out var vb);
            if (hasA && hasB && JsonElement.DeepEquals(va, vb)) continue;
            if (hasA && hasB && path.Length == 0 && n != "loc" && va.ValueKind == JsonValueKind.Object && vb.ValueKind == JsonValueKind.Object) Diff(va, vb, n + ".", into);
            else into.Add(path + n);
        }
    }
}
