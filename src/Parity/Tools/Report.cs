using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Model;

namespace Parity.Tools;

/// <summary>
/// The parts of a manifest an issue needs, with business identifiers removed: raw statements as shapes,
/// diagnostics with the form/member subject and quoted names replaced, file paths reduced to their extension,
/// unresolved calls as shapes. Framework namespaces (System, DevExpress, Microsoft) are kept because they are
/// the vocabulary the extractor is written against.
/// </summary>
public static class Report
{
    private static bool Framework(string t) => t.StartsWith("System.", StringComparison.Ordinal) || t.StartsWith("DevExpress.", StringComparison.Ordinal) || t.StartsWith("Microsoft.", StringComparison.Ordinal);

    public static string AnonPath(string file) => "<path>" + Regex.Match(file, @"(\.designer)?\.\w+$", RegexOptions.IgnoreCase).Value.ToLowerInvariant();

    public static string AnonMessage(string msg)
    {
        msg = Regex.Replace(msg, @"^[A-Za-z_]\w*\.[A-Za-z_]\w*:", "$form.$id:");
        msg = Regex.Replace(msg, @"'([^']*)'", mm => Framework(mm.Groups[1].Value) ? mm.Value : "'$id'");
        msg = Regex.Replace(msg, @"\b(method|handler|ctrl|dyn|evt|svc|form|bind|rule):\S+", "$1:$id");
        msg = Regex.Replace(msg, @"(?:[A-Za-z_][\w.-]*[/\\])*[A-Za-z_][\w.-]*\.(?:designer\.)?cs:(\d+)", mm => "<path>.cs:" + mm.Groups[1].Value, RegexOptions.IgnoreCase);
        msg = Regex.Replace(msg, @"\bon ([\w.]+(?: -> [\w.]+)*)", mm => "on " + string.Join(" -> ", mm.Groups[1].Value.Split(" -> ").Select(t => Framework(t) || t == "object" ? t : "$type")));
        return msg;
    }

    public static string Render(string manifestPath) => Render(JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(manifestPath), Json.Options)!);

    public static string Render(FormManifest m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"form: $form  controls: {m.Controls.Count}  events: {m.Events.Count}  handlers: {m.Handlers.Count}  methods: {m.Methods.Count}  svc: {m.ServiceCalls.Count}  dynamic: {m.DynamicUi.Count}  raw: {m.Raw.Count}  diagnostics: {m.Diagnostics.Count}");
        sb.AppendLine($"bases: {string.Join(" -> ", m.Bases.Select(b => Framework(b) || b == "object" ? b : "$type"))}");
        sb.AppendLine();
        sb.AppendLine("raw (as shapes):");
        foreach (var g in m.Raw.GroupBy(r => Shape.Of(r.Statement)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"{g.Count(),6}  {g.Key}   e.g. {AnonPath(g.First().Loc.File)}:{g.First().Loc.Start}");
        if (m.Raw.Count == 0) sb.AppendLine("     none");
        sb.AppendLine();
        sb.AppendLine("diagnostics:");
        foreach (var d in m.Diagnostics)
            sb.AppendLine($"  {d.Code}: {AnonMessage(d.Message)}{(d.Loc == null ? "" : $" ({AnonPath(d.Loc.File)}:{d.Loc.Start})")}");
        if (m.Diagnostics.Count == 0) sb.AppendLine("  none");
        var unresolved = m.Handlers.SelectMany(h => h.Unresolved).Concat(m.Methods.SelectMany(x => x.Unresolved)).Select(Shape.Of).GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        sb.AppendLine();
        sb.AppendLine("unresolved calls (as shapes):");
        foreach (var g in unresolved) sb.AppendLine($"{g.Count(),6}  {g.Key}");
        if (unresolved.Count == 0) sb.AppendLine("     none");
        return sb.ToString();
    }
}
