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

public sealed record DataTable(string Name, List<Dictionary<string, string>> Rows);

/// <summary>
/// Request, reply and dataset text as the host logs it: XML, JSON or opaque text. Field paths are <c>Root/Child/Leaf</c> for XML
/// (<c>[n]</c> on the second and later siblings of one name, the first bare; <c>@name</c> for an attribute; namespaces dropped), <c>a/b[n]/c</c> for JSON
/// (every array element indexed), and <c>(body)</c> for text. A dataset is a serialized DataSet: XML (<c>WriteXml</c>, with or
/// without an inline schema, or a diffgram) or a JSON object of tables, each an array of row objects; its paths are
/// <c>Table/Column</c>.
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
            WalkXml(doc.Root, Seg(doc.Root, null), list, node);
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

    private static string Seg(XElement e, XElement? parent)
    {
        var name = e.Name.LocalName;
        if (parent == null) return name;
        // the first of a name is bare and the next ones are [2], [3]: one element on one side still pairs with the first of
        // several on the other, and only the extra ones differ
        var index = parent.Elements().Where(x => x.Name.LocalName == name).ToList().IndexOf(e);
        return index > 0 ? $"{name}[{index + 1}]" : name;
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
        foreach (var c in e.Elements()) WalkXml(c, $"{path}/{Seg(c, e)}", list, node);
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

    private static List<(string Table, int Row, List<PayloadValue> Cells)> DatasetValues(string? text, out Func<string> serialize)
    {
        var rows = new List<(string, int, List<PayloadValue>)>();
        serialize = () => text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return rows;
        var t = text.TrimStart();
        if (t[0] == '<' && TryXml(text) is { Root: not null } doc)
        {
            var ds = doc.Root;
            if (ds.Name.LocalName == "diffgram")
                ds = ds.Elements().FirstOrDefault(x => x.Name.LocalName is not ("before" or "errors")) ?? ds;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in ds.Elements().Where(x => x.Name.LocalName != "schema"))
            {
                var table = row.Name.LocalName;
                counts[table] = counts.GetValueOrDefault(table) + 1;
                var cells = new List<PayloadValue>();
                foreach (var a in row.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
                {
                    var attr = a;
                    cells.Add(new PayloadValue($"{table}/{a.Name.LocalName}", a.Value, v => attr.Value = v));
                }
                foreach (var c in row.Elements()) WalkXml(c, $"{table}/{Seg(c, row)}", cells, null);
                rows.Add((table, counts[table], cells));
            }
            serialize = () => (doc.Declaration != null ? doc.Declaration + "" : "") + doc.Root!.ToString(SaveOptions.DisableFormatting);
            return rows;
        }
        if (t[0] == '{' && TryJson(text) is JsonObject tables)
        {
            foreach (var (name, value) in tables.ToList())
            {
                if (value is not JsonArray arr) continue;
                for (var i = 0; i < arr.Count; i++)
                {
                    var cells = new List<PayloadValue>();
                    if (arr[i] is JsonObject row)
                        foreach (var p in row.ToList()) WalkJson(row, p.Key, $"{name}/{p.Key}", cells, null);
                    rows.Add((name, i + 1, cells));
                }
            }
            serialize = () => tables.ToJsonString();
            return rows;
        }
        throw new InvalidOperationException("a dataset must be a serialized DataSet: XML or a JSON object of tables");
    }

    /// <summary>The dataset's tables in first-seen order, each row as column path (under the table) to value, through <paramref name="map"/>.</summary>
    public static List<DataTable>? Tables(string? text, Func<string, string, string>? map = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tables = new List<DataTable>();
        foreach (var (table, _, cells) in DatasetValues(text, out _))
        {
            var t = tables.FirstOrDefault(x => x.Name == table);
            if (t == null) tables.Add(t = new DataTable(table, new()));
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in cells) row.TryAdd(c.Path[(table.Length + 1)..], map == null ? c.Value : map(c.Path, c.Value));
            t.Rows.Add(row);
        }
        return tables;
    }

    public static string RewriteDataset(string? text, Func<string, string, string> map, out int changed)
    {
        changed = 0;
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var rows = DatasetValues(text, out var serialize);
        foreach (var (_, _, cells) in rows)
            foreach (var c in cells)
            {
                var nv = map(c.Path, c.Value);
                if (nv == c.Value) continue;
                c.Set(nv);
                changed++;
            }
        return changed == 0 ? text : serialize();
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
