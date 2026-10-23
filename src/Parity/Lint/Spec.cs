namespace Parity.Lint;

public sealed class SpecRule
{
    public string Id = ""; public int Line;
    public Dictionary<string, (string Value, int Line)> Fields = new(StringComparer.Ordinal);
    /// <summary><c>- outcome-gap: &lt;outcome&gt; - &lt;reason&gt;</c>, the one key a rule may repeat (one line per outcome).</summary>
    public List<(string Value, int Line)> OutcomeGaps = new();
    public string? Get(string key) => Fields.TryGetValue(key, out var v) ? v.Value : null;
    public int LineOf(string key) => Fields.TryGetValue(key, out var v) ? v.Line : Line;
}

public sealed class SpecRow
{
    public int Line; public List<string> Cells = new();
    public string Cell(int i) => i < Cells.Count ? Cells[i] : "";
}

public sealed class Spec
{
    public Dictionary<string, (string Value, int Line)> Front = new(StringComparer.Ordinal);
    public List<SpecRow> Fields = new();
    public List<SpecRule> Rules = new();
    public List<SpecRow> NoBehavior = new();
    public List<SpecRow> ServiceCalls = new();
    public List<(string Text, int Line)> OpenQuestions = new();
    public List<(string Code, int Line, string Message)> ParseErrors = new();
    public HashSet<string> SectionsSeen = new(StringComparer.Ordinal);
}

/// <summary>Line-grammar parser for spec.md. Forgiving on whitespace, strict on structure.</summary>
public static class SpecParser
{
    public static Spec Parse(string text)
    {
        var spec = new Spec();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            i = 1;
            for (; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                var l = lines[i];
                var colon = l.IndexOf(':');
                if (colon <= 0) { spec.ParseErrors.Add(("GRAMMAR", i + 1, "front matter line is not key: value")); continue; }
                var key = l[..colon].Trim();
                var val = l[(colon + 1)..];
                var hash = val.IndexOf('#'); if (hash >= 0) val = val[..hash];
                spec.Front[key] = (val.Trim(), i + 1);
            }
            i++;
        }
        else spec.ParseErrors.Add(("GRAMMAR", 1, "missing front matter (--- form/manifest/status ---)"));

        string section = "";
        SpecRule? rule = null;
        bool headerSeen = false;
        for (; i < lines.Length; i++)
        {
            var raw = lines[i]; var l = raw.Trim(); var ln = i + 1;
            if (l.StartsWith("## ", StringComparison.Ordinal))
            {
                section = l[3..].Trim(); rule = null; headerSeen = false; spec.SectionsSeen.Add(section);
                continue;
            }
            if (l.Length == 0 || l.StartsWith("# ", StringComparison.Ordinal)) continue;

            switch (section)
            {
                case "Fields":
                case "No behavior":
                case "Service calls":
                {
                    if (!l.StartsWith('|')) { if (!l.StartsWith("Purpose", StringComparison.Ordinal)) spec.ParseErrors.Add(("GRAMMAR", ln, $"{section}: expected a table row")); continue; }
                    var cells = l.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    if (!headerSeen) { headerSeen = true; continue; }          // header
                    if (cells.All(c => c.Length == 0 || c.All(ch => ch == '-' || ch == ':'))) continue; // separator
                    var row = new SpecRow { Line = ln, Cells = cells };
                    (section == "Fields" ? spec.Fields : section == "No behavior" ? spec.NoBehavior : spec.ServiceCalls).Add(row);
                    break;
                }
                case "Rules":
                {
                    if (l.StartsWith("### ", StringComparison.Ordinal))
                    {
                        rule = new SpecRule { Id = l[4..].Trim(), Line = ln };
                        spec.Rules.Add(rule);
                        continue;
                    }
                    if (rule == null) { spec.ParseErrors.Add(("GRAMMAR", ln, "Rules: text before the first ### R- heading")); continue; }
                    if (!l.StartsWith("- ", StringComparison.Ordinal)) { spec.ParseErrors.Add(("GRAMMAR", ln, $"{rule.Id}: expected '- key: value'")); continue; }
                    var body = l[2..];
                    var colon = body.IndexOf(':');
                    if (colon <= 0) { spec.ParseErrors.Add(("GRAMMAR", ln, $"{rule.Id}: expected '- key: value'")); continue; }
                    var key = body[..colon].Trim();
                    if (key == "outcome-gap") { rule.OutcomeGaps.Add((body[(colon + 1)..].Trim(), ln)); break; }
                    if (rule.Fields.ContainsKey(key)) spec.ParseErrors.Add(("GRAMMAR", ln, $"{rule.Id}: duplicate '{key}'"));
                    rule.Fields[key] = (body[(colon + 1)..].Trim(), ln);
                    break;
                }
                case "Open questions":
                    if (l.StartsWith("- ", StringComparison.Ordinal)) spec.OpenQuestions.Add((l[2..].Trim(), ln));
                    else spec.ParseErrors.Add(("GRAMMAR", ln, "Open questions: expected '- text' or '- none'"));
                    break;
                default:
                    // title area: Purpose line is free text; anything else outside a section is tolerated
                    break;
            }
        }
        return spec;
    }

    /// <summary>Comma-separated ids; commas inside parentheses belong to method parameter lists.</summary>
    public static List<string> SplitIds(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        var depth = 0; var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || (value[i] == ',' && depth == 0))
            {
                var part = value[start..i].Trim().Trim('`').Trim();
                if (part.Length > 0) result.Add(part);
                start = i + 1;
            }
            else if (value[i] == '(') depth++;
            else if (value[i] == ')') depth--;
        }
        return result;
    }
}
