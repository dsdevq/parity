using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Parity.Oracle;

/// <summary>A leaf of a payload: its field path, its value, and how to replace the value in the tree it came from.</summary>
public sealed record PayloadValue(string Path, string Value, Action<string> Set);

/// <summary>A payload flattened for comparison: leaf paths to values, and every node path (so a missing block is told from missing fields).</summary>
public sealed class FlatPayload
{
    public readonly Dictionary<string, string> Leaves = new(StringComparer.Ordinal);
    public readonly List<string> Order = new();
    public readonly HashSet<string> Nodes = new(StringComparer.Ordinal);

    public void Add(string path, string value)
    {
        if (Leaves.TryAdd(path, value)) Order.Add(path);
        Nodes.Add(path);
    }
}

/// <summary>
/// A dataset table: each row as column (path under the row) to value, and where each row sits, for the report (<c>T[2]</c> for a
/// top-level row, <c>T[1]/Items/Item[17]</c> for a row of a nested table). A nested table's name is its element path with the
/// indexes removed (<c>T/Items/Item</c>).
/// </summary>
public sealed record DataTable(string Name, List<Dictionary<string, string>> Rows, List<string> At)
{
    /// <summary>The top-level table a nested table sits under; its own name for a top-level table.</summary>
    public string Top => Name.Split('/')[0];
}

/// <summary>A dataset parsed once, so its repeating groups and its tables come from one tree. Null parts are absent.</summary>
public sealed class Dataset
{
    internal XElement? Xml { get; init; }
    internal JsonObject? Json { get; init; }
    internal Func<string> Serialize { get; init; } = () => "";

    /// <summary>The top-level rows of an XML dataset: the children of the DataSet element, an inline schema skipped.</summary>
    internal IEnumerable<XElement> Rows() => Xml?.Elements().Where(x => x.Name.LocalName != "schema") ?? Enumerable.Empty<XElement>();
}

/// <summary>
/// Request, reply and dataset text as the host logs it: XML, JSON or opaque text. Field paths are <c>Root/Child/Leaf</c> for XML
/// (<c>[n]</c> on the second and later siblings of one name, the first bare; <c>@name</c> for an attribute; namespaces dropped), <c>a/b[n]/c</c> for JSON
/// (every array element indexed), and <c>(body)</c> for text. A dataset is a serialized DataSet: XML (<c>WriteXml</c>, with or
/// without an inline schema, or a diffgram) or a JSON object of tables, each an array of row objects; its paths are
/// <c>Table/Column</c>. In an XML dataset, a same-name sibling group that repeats below a row is a nested table of its own, named
/// by its element path (<see cref="Groups"/>); a flat <c>WriteXml</c> has none.
/// </summary>
public static partial class Payload
{
    public const string Body = "(body)";

    [GeneratedRegex(@"\[\d+\]")] private static partial Regex IndexRx();

    /// <summary>The path a glob is matched against: indexes removed, so a rule names a field, not one occurrence of it.</summary>
    public static string Unindexed(string path) => IndexRx().Replace(path, "");

    public static string Format(string? text)
    {
        var t = text?.TrimStart();
        if (string.IsNullOrEmpty(t)) return "txt";
        if (t[0] == '<' && TryXml(text!) != null) return "xml";
        if (t[0] is '{' or '[' && TryJson(text!) != null) return "json";
        return "txt";
    }

    private static XDocument? TryXml(string text)
    {
        try { return XDocument.Parse(text, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return null; }
    }

    private static JsonNode? TryJson(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    // ---- request and reply ----

    /// <summary>Every leaf of a request or reply, in document order, with node paths reported to <paramref name="node"/>.</summary>
    private static List<PayloadValue> Values(string? text, out Func<string> serialize, Action<string>? node = null)
    {
        var list = new List<PayloadValue>();
        serialize = () => text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return list;
        var t = text.TrimStart();
        if (t[0] == '<' && TryXml(text) is { Root: not null } doc)
        {
            WalkXml(doc.Root, doc.Root.Name.LocalName, list, node);
            serialize = () => (doc.Declaration != null ? doc.Declaration + "" : "") + doc.Root!.ToString(SaveOptions.DisableFormatting);
            return list;
        }
        if (t[0] is '{' or '[' && TryJson(text) is { } json)
        {
            var holder = new JsonArray(json);
            WalkJson(holder, 0, "", list, node);
            serialize = () => holder[0]!.ToJsonString();
            return list;
        }
        var body = text;
        list.Add(new PayloadValue(Body, body, v => body = v));
        node?.Invoke(Body);
        serialize = () => body;
        return list;
    }

    /// <summary>
    /// Each child element with its path segment, the siblings of one name counted in one pass (issue 38: a rescan per element is
    /// quadratic in a group of thousands). The first of a name is bare and the next ones are [2], [3]: one element on one side still
    /// pairs with the first of several on the other, and only the extra ones differ.
    /// </summary>
    private static IEnumerable<(XElement Child, string Seg)> Children(XElement e)
    {
        Dictionary<string, int>? seen = null;
        foreach (var c in e.Elements())
        {
            var name = c.Name.LocalName;
            seen ??= new Dictionary<string, int>(StringComparer.Ordinal);
            var n = seen[name] = seen.GetValueOrDefault(name) + 1;
            yield return (c, n > 1 ? $"{name}[{n}]" : name);
        }
    }

    private static void WalkXml(XElement e, string path, List<PayloadValue> list, Action<string>? node)
    {
        node?.Invoke(path);
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            var attr = a;
            list.Add(new PayloadValue($"{path}/@{a.Name.LocalName}", a.Value, v => attr.Value = v));
            node?.Invoke($"{path}/@{a.Name.LocalName}");
        }
        if (!e.HasElements)
        {
            var el = e;
            list.Add(new PayloadValue(path, e.Value, v => el.Value = v));
            return;
        }
        foreach (var (c, seg) in Children(e)) WalkXml(c, $"{path}/{seg}", list, node);
    }

    private static void WalkJson(JsonNode parent, object key, string path, List<PayloadValue> list, Action<string>? node)
    {
        var n = key is int i ? parent.AsArray()[i] : parent.AsObject()[(string)key];
        if (path.Length > 0) node?.Invoke(path);
        switch (n)
        {
            case JsonObject o:
                foreach (var p in o.ToList()) WalkJson(o, p.Key, path.Length == 0 ? p.Key : $"{path}/{p.Key}", list, node);
                break;
            case JsonArray a:
                for (var j = 0; j < a.Count; j++) WalkJson(a, j, $"{path}[{j + 1}]", list, node);
                break;
            default:
            {
                var value = n == null ? "null" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
                list.Add(new PayloadValue(path.Length == 0 ? Body : path, value, v =>
                {
                    if (key is int k) parent.AsArray()[k] = JsonValue.Create(v); else parent.AsObject()[(string)key] = JsonValue.Create(v);
                }));
                break;
            }
        }
    }

    /// <summary>A request or reply as leaves and nodes, each value passed through <paramref name="map"/> (normalization).</summary>
    public static FlatPayload Flatten(string? text, Func<string, string, string>? map = null)
    {
        var flat = new FlatPayload();
        var nodes = new List<string>();
        foreach (var v in Values(text, out _, nodes.Add)) flat.Add(v.Path, map == null ? v.Value : map(v.Path, v.Value));
        flat.Nodes.UnionWith(nodes);
        return flat;
    }

    /// <summary>Rewrites a request or reply with each value passed through <paramref name="map"/>; the original text when nothing changed.</summary>
    public static string Rewrite(string? text, Func<string, string, string> map, out int changed)
    {
        changed = 0;
        var values = Values(text, out var serialize);
        foreach (var v in values)
        {
            var nv = map(v.Path, v.Value);
            if (nv == v.Value) continue;
            v.Set(nv);
            changed++;
        }
        return changed == 0 ? text ?? "" : serialize();
    }

    // ---- datasets ----

    /// <summary>
    /// Parses a serialized DataSet once: XML (<c>WriteXml</c>, with or without an inline schema, or a diffgram, whose current rows are
    /// read) or a JSON object of tables. Null for empty text; anything else is refused.
    /// </summary>
    public static Dataset? ParseDataset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.TrimStart();
        if (t[0] == '<' && TryXml(text) is { Root: not null } doc)
        {
            var ds = doc.Root;
            if (ds.Name.LocalName == "diffgram")
                ds = ds.Elements().FirstOrDefault(x => x.Name.LocalName is not ("before" or "errors")) ?? ds;
            return new Dataset { Xml = ds, Serialize = () => (doc.Declaration != null ? doc.Declaration + "" : "") + doc.Root!.ToString(SaveOptions.DisableFormatting) };
        }
        if (t[0] == '{' && TryJson(text) is JsonObject tables) return new Dataset { Json = tables, Serialize = () => tables.ToJsonString() };
        throw new InvalidOperationException("a dataset must be a serialized DataSet: XML or a JSON object of tables");
    }

    /// <summary>Every leaf of a dataset, with its <c>Table/Column</c> path (indexes on repeated elements below a row), in document order.</summary>
    private static List<PayloadValue> DatasetValues(Dataset ds)
    {
        var cells = new List<PayloadValue>();
        foreach (var row in ds.Rows())
        {
            var table = row.Name.LocalName;
            foreach (var a in row.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
            {
                var attr = a;
                cells.Add(new PayloadValue($"{table}/{a.Name.LocalName}", a.Value, v => attr.Value = v));
            }
            foreach (var (c, seg) in Children(row)) WalkXml(c, $"{table}/{seg}", cells, null);
        }
        if (ds.Json != null)
            foreach (var (name, value) in ds.Json.ToList())
            {
                if (value is not JsonArray arr) continue;
                foreach (var row in arr.OfType<JsonObject>())
                    foreach (var p in row.ToList()) WalkJson(row, p.Key, $"{name}/{p.Key}", cells, null);
            }
        return cells;
    }

    /// <summary>
    /// The nested tables of an XML dataset: the element path, indexes removed, of every same-name sibling group that repeats below a
    /// top-level row (<c>Policy/Items/Item</c> when some <c>Items</c> holds two or more <c>Item</c>). A JSON dataset has none: its
    /// format is tables of row objects. The diff takes the union of both sides' groups, so a group with one element on one side and
    /// several on the other is a table on both.
    /// </summary>
    public static HashSet<string> Groups(Dataset? ds)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (ds == null) return groups;
        foreach (var row in ds.Rows()) Visit(row, row.Name.LocalName);
        return groups;

        void Visit(XElement e, string path)
        {
            foreach (var (c, seg) in Children(e))
            {
                var p = $"{path}/{c.Name.LocalName}";
                if (seg.EndsWith("[2]", StringComparison.Ordinal)) groups.Add(p);
                if (c.HasElements) Visit(c, p);
            }
        }
    }

    /// <summary>The dataset's tables in first-seen order, each row as column path (under the row) to value, through <paramref name="map"/>.</summary>
    public static List<DataTable>? Tables(string? text, Func<string, string, string>? map = null) =>
        ParseDataset(text) is { } ds ? Tables(ds, Groups(ds), map) : null;

    /// <summary>
    /// The tables of a parsed dataset, <paramref name="groups"/> (<see cref="Groups"/>) read as nested tables: a row of one is taken
    /// out of the row it sits in, its leaves are its columns, and its own text, when it holds no element, is the column
    /// <c>(text)</c>. <paramref name="map"/> sees every value with the same path export's redaction sees.
    /// </summary>
    public static List<DataTable> Tables(Dataset ds, IReadOnlySet<string> groups, Func<string, string, string>? map = null)
    {
        var tables = new List<DataTable>();
        var byName = new Dictionary<string, DataTable>(StringComparer.Ordinal);
        string Map(string path, string value) => map == null ? value : map(path, value);
        Dictionary<string, string> NewRow(string table, string at)
        {
            if (!byName.TryGetValue(table, out var t)) tables.Add(byName[table] = t = new DataTable(table, new(), new()));
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            t.Rows.Add(row);
            t.At.Add(at);
            return row;
        }

        if (ds.Json != null)
            foreach (var (name, value) in ds.Json.ToList())
            {
                if (value is not JsonArray arr) continue;
                for (var i = 0; i < arr.Count; i++)
                {
                    var row = NewRow(name, $"{name}[{i + 1}]");
                    if (arr[i] is not JsonObject obj) continue;
                    var cells = new List<PayloadValue>();
                    foreach (var p in obj.ToList()) WalkJson(obj, p.Key, $"{name}/{p.Key}", cells, null);
                    foreach (var c in cells) row.TryAdd(c.Path[(name.Length + 1)..], Map(c.Path, c.Value));
                }
            }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var top in ds.Rows())
        {
            var name = top.Name.LocalName;
            var row = NewRow(name, $"{name}[{counts[name] = counts.GetValueOrDefault(name) + 1}]");
            foreach (var a in top.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
                row.TryAdd(a.Name.LocalName, Map($"{name}/{a.Name.LocalName}", a.Value));
            Walk(top, name, name, "", row, $"{name}[{counts[name]}]");
        }
        return tables;

        // e's children into row, the row's columns relative to it (rel), a repeating group's elements into rows of their own table
        void Walk(XElement e, string path, string group, string rel, Dictionary<string, string> row, string at)
        {
            foreach (var (c, seg) in Children(e))
            {
                var cPath = $"{path}/{seg}";
                var cGroup = $"{group}/{c.Name.LocalName}";
                var cRel = rel.Length == 0 ? seg : $"{rel}/{seg}";
                if (groups.Contains(cGroup))
                {
                    var cAt = $"{at}/{cRel}";
                    var nested = NewRow(cGroup, cAt);
                    foreach (var a in c.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
                        nested.TryAdd($"@{a.Name.LocalName}", Map($"{cPath}/@{a.Name.LocalName}", a.Value));
                    if (c.HasElements) Walk(c, cPath, cGroup, "", nested, cAt);
                    else nested.TryAdd("(text)", Map(cPath, c.Value));
                    continue;
                }
                foreach (var a in c.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    row.TryAdd($"{cRel}/@{a.Name.LocalName}", Map($"{cPath}/@{a.Name.LocalName}", a.Value));
                if (c.HasElements) Walk(c, cPath, cGroup, cRel, row, at);
                else row.TryAdd(cRel, Map(cPath, c.Value));
            }
        }
    }

    public static string RewriteDataset(string? text, Func<string, string, string> map, out int changed)
    {
        changed = 0;
        if (ParseDataset(text) is not { } ds) return text ?? "";
        foreach (var c in DatasetValues(ds))
        {
            var nv = map(c.Path, c.Value);
            if (nv == c.Value) continue;
            c.Set(nv);
            changed++;
        }
        return changed == 0 ? text! : ds.Serialize();
    }

    // ---- globs ----

    /// <summary>
    /// A field glob against a path with its indexes removed: segments split on '/', <c>*</c> any run of characters within a segment,
    /// <c>**</c> any number of segments (none included). Case-sensitive, as the payload is.
    /// </summary>
    public static bool GlobMatch(string glob, string path)
    {
        var g = glob.Split('/');
        var p = Unindexed(path).Split('/');
        return Match(g, 0, p, 0);

        static bool Match(string[] g, int gi, string[] p, int pi)
        {
            if (gi == g.Length) return pi == p.Length;
            if (g[gi] == "**")
            {
                for (var k = pi; k <= p.Length; k++) if (Match(g, gi + 1, p, k)) return true;
                return false;
            }
            return pi < p.Length && SegMatch(g[gi], p[pi]) && Match(g, gi + 1, p, pi + 1);
        }

        static bool SegMatch(string g, string s) =>
            Regex.IsMatch(s, "^" + string.Join(".*", g.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant);
    }

    /// <summary>A <c>Category/Type</c> glob against a call key, case-insensitive.</summary>
    public static bool CallMatch(string glob, string callKey) =>
        Regex.IsMatch(callKey, "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
