using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Model;
using Parity.Passes;

namespace Parity.Lint;

public sealed record Finding(string File, int Line, string Code, string Severity, string Message)
{
    public override string ToString() => $"{File}:{Line} {Code} {Message}";
}

/// <summary>
/// Structure checks always; coverage checks with --gate. Coverage is computed from the manifest,
/// never from the spec, so "done" is what the extractor found minus what the spec accounts for.
/// </summary>
public static partial class SpecLinter
{
    private static readonly string[] HedgeWords = { "probably", "likely", "assume", "assumed", "assuming", "should", "seems", "seem", "typically", "usually", "maybe", "might", "i think", "presumably", "appears to" };
    private static readonly HashSet<string> RuleKinds = new(StringComparer.Ordinal) { "validation", "enablement", "navigation", "data", "calculation", "ui", "service", "lifecycle" };
    private static readonly HashSet<string> RowCategories = new(StringComparer.Ordinal) { "input", "action", "grid", "column", "display", "unknown" };
    private static readonly HashSet<string> Dispositions = new(StringComparer.Ordinal) { "field", "layout", "dropped", "nonvisual", "display" };

    [GeneratedRegex(@"decided-by\s+\S+.*\b\d{4}-\d{2}-\d{2}\b")] private static partial Regex Decided();
    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")] private static partial Regex DateRx();

    /// <summary>
    /// What a debt line must contain to acknowledge a diagnostic: the code plus the message's subject
    /// ("UNREACHED-METHOD: Form.btnExport_Click"), or the full message when there is no subject.
    /// One line acknowledges one diagnostic; a re-extract that adds a new one with the same code is not covered.
    /// </summary>
    public static string DebtKey(string code, string message)
    {
        var m = Regex.Match(message, @"^([A-Za-z_]\w*(?:\.[A-Za-z_$]\w*)?):");
        return m.Success ? code + ": " + m.Groups[1].Value : code + ": " + message;
    }

    public static List<Finding> Lint(string specPath, bool gate, string? implementDir)
    {
        var file = Path.GetRelativePath(Environment.CurrentDirectory, specPath).Replace('\\', '/');
        var f = new List<Finding>();
        void E(int line, string code, string msg) => f.Add(new Finding(file, line, code, "error", msg));
        void W(int line, string code, string msg) => f.Add(new Finding(file, line, code, "warning", msg));

        var spec = SpecParser.Parse(File.ReadAllText(specPath));
        foreach (var (code, line, msg) in spec.ParseErrors) E(line, code, msg);

        // front matter and manifest binding
        var formId = spec.Front.TryGetValue("form", out var fv) ? fv.Value : null;
        if (formId == null || !formId.StartsWith("form:", StringComparison.Ordinal)) { E(1, "GRAMMAR", "front matter needs 'form: form:<Name>'"); return f; }
        var formName = formId[5..];
        var specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".";
        var moduleDir = Path.GetFullPath(Path.Combine(specDir, ".."));
        var manifestPath = Path.Combine(moduleDir, "manifests", formName + ".json");
        if (!File.Exists(manifestPath)) { E(fv.Line, "STALE", $"manifest not found at {Path.GetRelativePath(Environment.CurrentDirectory, manifestPath)}; run parity extract"); return f; }
        var hash = ManifestHash.Of(manifestPath);
        if (!spec.Front.TryGetValue("manifest", out var mv)) E(1, "STALE", "front matter needs 'manifest: sha256:...' (printed at the top of the rendered manifest)");
        else if (mv.Value != hash) { E(mv.Line, "STALE", $"spec was written against {mv.Value}, manifest is now {hash}; re-read the manifest and update the spec"); return f; }

        var m = JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(manifestPath), Json.Options)!;
        var controlIds = m.Controls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var dynIds = m.DynamicUi.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var eventIds = m.Events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var handlerIds = m.Handlers.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var methodIds = m.Methods.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var svcIds = m.ServiceCalls.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var allIds = controlIds.Concat(dynIds).Concat(eventIds).Concat(handlerIds).Concat(methodIds).Concat(svcIds).ToHashSet(StringComparer.Ordinal);

        // ---- structure ----
        foreach (var s in new[] { "Fields", "Rules", "No behavior", "Service calls", "Open questions" })
            if (!spec.SectionsSeen.Contains(s)) E(1, "GRAMMAR", $"missing section '## {s}'");

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var cited = new HashSet<string>(StringComparer.Ordinal);
        var ruleFixtures = new List<(string Name, int Line)>();
        var seenRuleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in spec.Rules)
        {
            if (!r.Id.StartsWith($"R-{formName}-", StringComparison.Ordinal)) E(r.Line, "GRAMMAR", $"rule id must be R-{formName}-NNN, got '{r.Id}'");
            if (!seenRuleIds.Add(r.Id)) E(r.Line, "GRAMMAR", $"duplicate rule id {r.Id}");
            foreach (var key in new[] { "kind", "covers", "cite", "given", "when", "then" })
                if (r.Get(key) is null or "") E(r.Line, "GRAMMAR", $"{r.Id}: missing '- {key}:'");
            if (r.Get("kind") is { } kind && kind.Length > 0 && !RuleKinds.Contains(kind)) E(r.LineOf("kind"), "GRAMMAR", $"{r.Id}: kind '{kind}' is not one of {string.Join("|", RuleKinds)}");

            var covers = SpecParser.SplitIds(r.Get("covers"));
            if (covers.Count == 0 && r.Get("covers") != null) E(r.LineOf("covers"), "ORPHAN-RULE", $"{r.Id} covers nothing");
            foreach (var id in covers)
            {
                if (!allIds.Contains(id)) E(r.LineOf("covers"), "CITE-UNKNOWN", $"{r.Id}: '{id}' is not in the manifest");
                else if (!id.StartsWith("evt:", StringComparison.Ordinal) && !id.StartsWith("dyn:", StringComparison.Ordinal)) E(r.LineOf("covers"), "CITE-KIND", $"{r.Id}: covers must list evt: (or dyn:) ids, got '{id}'");
                else covered.Add(id);
            }
            foreach (var id in SpecParser.SplitIds(r.Get("cite")))
            {
                if (!allIds.Contains(id)) E(r.LineOf("cite"), "CITE-UNKNOWN", $"{r.Id}: '{id}' is not in the manifest");
                else if (!id.StartsWith("handler:", StringComparison.Ordinal) && !id.StartsWith("method:", StringComparison.Ordinal)) E(r.LineOf("cite"), "CITE-KIND", $"{r.Id}: cite must list handler: or method: ids, got '{id}'");
                else cited.Add(id);
            }
            if (r.Get("then") is { } then && then.Length > 0 && !Regex.IsMatch(then, @"\b(ctrl|dyn|svc|form):\S+") && !Regex.IsMatch(then, @"\b[a-z][A-Za-z0-9]*[A-Z][A-Za-z0-9]*\b"))
                E(r.LineOf("then"), "GRAMMAR", $"{r.Id}: 'then' must name an observable outcome: a control (ctrl:/dyn: id or control name), a service (svc:) or a form shown (form:)");
            foreach (var key in new[] { "given", "when", "then" })
                if (r.Get(key) is { } v && HedgeWords.Any(h => Regex.IsMatch(v, $@"\b{Regex.Escape(h)}\b", RegexOptions.IgnoreCase)))
                    E(r.LineOf(key), "HEDGE", $"{r.Id}: '{key}' hedges ({HedgeWords.First(h => Regex.IsMatch(v, $@"\b{Regex.Escape(h)}\b", RegexOptions.IgnoreCase))}); state the legacy fact or move it to Open questions");
            if (r.Get("quirk") is { } q)
            {
                if (!q.StartsWith("preserve", StringComparison.Ordinal) && !q.StartsWith("diverge", StringComparison.Ordinal)) E(r.LineOf("quirk"), "GRAMMAR", $"{r.Id}: quirk must start with preserve or diverge");
                if (!Decided().IsMatch(q)) E(r.LineOf("quirk"), "DROP-REASON", $"{r.Id}: quirk needs 'decided-by <name> <YYYY-MM-DD>'");
            }
            foreach (var fx in SpecParser.SplitIds(r.Get("fixtures"))) ruleFixtures.Add((fx, r.LineOf("fixtures")));
        }

        var fieldRows = new Dictionary<string, SpecRow>(StringComparer.Ordinal);
        foreach (var row in spec.Fields)
        {
            var id = row.Cell(0).Trim('`');
            if (!controlIds.Contains(id) && !dynIds.Contains(id)) { E(row.Line, "CITE-UNKNOWN", $"Fields: '{id}' is not a ctrl: or dyn: id in the manifest"); continue; }
            if (!fieldRows.TryAdd(id, row)) E(row.Line, "GRAMMAR", $"Fields: duplicate row for {id}");
            var disp = row.Cell(4);
            var dispKind = disp.Contains(':') ? disp[..disp.IndexOf(':')] : disp;
            if (!Dispositions.Contains(dispKind) && dispKind != "shared") E(row.Line, "GRAMMAR", $"Fields: disposition '{disp}' must be field|layout|display|nonvisual|dropped|shared:<Component>");
            if (dispKind == "dropped" && !Decided().IsMatch(row.Cell(5))) E(row.Line, "DROP-REASON", $"Fields: dropped {id} needs a note with 'decided-by <name> <YYYY-MM-DD>'");
        }

        var noBehavior = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in spec.NoBehavior)
        {
            var id = row.Cell(0).Trim('`');
            if (!eventIds.Contains(id)) E(row.Line, "CITE-UNKNOWN", $"No behavior: '{id}' is not an evt: id in the manifest");
            else if (covered.Contains(id)) E(row.Line, "GRAMMAR", $"No behavior: {id} is also covered by a rule; pick one");
            else noBehavior.Add(id);
            if (row.Cell(1).Length == 0) E(row.Line, "GRAMMAR", $"No behavior: {id} needs a reason");
        }

        var svcRows = new Dictionary<string, SpecRow>(StringComparer.Ordinal);
        foreach (var row in spec.ServiceCalls)
        {
            var id = row.Cell(0).Trim('`');
            if (!svcIds.Contains(id)) E(row.Line, "CITE-UNKNOWN", $"Service calls: '{id}' is not a svc: id in the manifest");
            else svcRows[id] = row;
        }

        if (!gate) return f.OrderBy(x => x.Line).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();

        // ---- gate: coverage against the manifest ----
        var eventsByControl = m.Events.GroupBy(e => e.Control, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var c in m.Controls)
        {
            var needsRow = RowCategories.Contains(c.Category) || eventsByControl.ContainsKey(c.Id);
            if (c.Field == "$this") needsRow = false;
            if (needsRow && !fieldRows.ContainsKey(c.Id)) E(0, "COV-CONTROL", $"{c.Id} ({c.Category}) has no Fields row");
        }
        foreach (var d in m.DynamicUi)
            if (!fieldRows.ContainsKey(d.Id)) E(0, "COV-DYNAMIC", $"{d.Id} (created in {d.Handler}) has no Fields row");

        foreach (var e in m.Events)
            if (!covered.Contains(e.Id) && !noBehavior.Contains(e.Id)) E(0, "COV-EVENT", $"{e.Id} -> {e.Handler} is neither covered by a rule nor listed under No behavior");

        foreach (var h in m.Handlers)
        {
            if (cited.Contains(h.Id)) continue;
            var allNoBehavior = h.Events.Count > 0 && h.Events.All(ev => noBehavior.Contains(ev) || !ev.StartsWith("evt:", StringComparison.Ordinal) && false);
            if (allNoBehavior) continue;
            if (h.Kind == "ctor" && h.Writes.Count == 0 && h.ServiceCalls.Count == 0 && h.Calls.Count == 0) continue; // trivial ctor
            E(0, "COV-HANDLER", $"{h.Id} ({h.Kind}) is not cited by any rule");
        }
        foreach (var x in m.Methods.Where(x => x.Source != null))
            if (!cited.Contains(x.Id)) W(0, "COV-METHOD", $"{x.Id} (depth {x.Depth}) is reached from a handler but cited by no rule");

        var fixturesDir = Path.Combine(moduleDir, "fixtures");
        var fixtureService = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(fixturesDir))
            foreach (var fx in Directory.EnumerateFiles(fixturesDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(fx));
                    if (doc.RootElement.TryGetProperty("service", out var sv) && sv.ValueKind == JsonValueKind.String)
                        fixtureService[Path.GetFileNameWithoutExtension(fx)] = sv.GetString() ?? "";
                }
                catch (JsonException ex) { E(0, "FIXTURE-INVALID", $"{Path.GetFileName(fx)}: {ex.Message}"); }
            }
        foreach (var s in m.ServiceCalls)
        {
            if (!svcRows.TryGetValue(s.Id, out var row)) { E(0, "COV-SERVICE", $"{s.Id} has no row under Service calls"); continue; }
            var fxs = SpecParser.SplitIds(row.Cell(2));
            if (fxs.Count == 0) E(row.Line, "COV-SERVICE", $"{s.Id} lists no fixtures");
            foreach (var fx in fxs)
            {
                if (!fixtureService.TryGetValue(fx, out var svc)) E(row.Line, "FIXTURE-MISSING", $"fixture '{fx}' not found at fixtures/{fx}.json");
                else if (svc != s.Id) E(row.Line, "FIXTURE-MISSING", $"fixture '{fx}' records {svc}, not {s.Id}");
            }
        }
        foreach (var (name, line) in ruleFixtures)
            if (!fixtureService.ContainsKey(name)) E(line, "FIXTURE-MISSING", $"fixture '{name}' not found at fixtures/{name}.json");

        if (m.Raw.Count > 0 || m.Diagnostics.Count > 0)
        {
            var debtPath = Path.Combine(moduleDir, "extractor-debt.md");
            var debt = File.Exists(debtPath) ? File.ReadAllLines(debtPath) : Array.Empty<string>();
            bool Acknowledged(string key) => debt.Any(l => l.Contains(key, StringComparison.Ordinal) && l.Contains("owner", StringComparison.OrdinalIgnoreCase) && DateRx().IsMatch(l));
            foreach (var r in m.Raw)
                if (!Acknowledged($"{r.Loc.File}:{r.Loc.Start}")) E(0, "COV-RAW", $"raw designer statement at {r.Loc.File}:{r.Loc.Start} is not acknowledged in extractor-debt.md (line must carry '{r.Loc.File}:{r.Loc.Start}', an owner and a YYYY-MM-DD)");
            foreach (var d in m.Diagnostics)
            {
                var key = DebtKey(d.Code, d.Message);
                if (!Acknowledged(key)) E(0, "COV-RAW", $"diagnostic '{key}' is not acknowledged in extractor-debt.md (a line must carry exactly that text, an owner and a YYYY-MM-DD; the bare code acknowledges nothing)");
            }
        }

        if (spec.OpenQuestions.Count != 1 || !string.Equals(spec.OpenQuestions[0].Text, "none", StringComparison.OrdinalIgnoreCase))
            foreach (var (text, line) in spec.OpenQuestions.Where(q => !string.Equals(q.Text, "none", StringComparison.OrdinalIgnoreCase)))
                E(line, "OPEN", $"open question blocks the gate: {text}");
        if (spec.OpenQuestions.Count == 0) E(0, "OPEN", "Open questions must contain '- none' to pass the gate");

        if (!spec.Front.TryGetValue("status", out var st) || st.Value != "reviewed") E(st.Line, "STATUS", "status must be 'reviewed' (set by the human reviewer, with reviewed-by)");
        else if (!spec.Front.TryGetValue("reviewed-by", out var rb) || !DateRx().IsMatch(rb.Value)) E(st.Line, "STATUS", "reviewed needs 'reviewed-by: <name> YYYY-MM-DD'");

        if (implementDir != null)
        {
            var files = Directory.Exists(implementDir) ? Directory.EnumerateFiles(implementDir, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".ts") || p.EndsWith(".spec.ts") || p.EndsWith(".cs") || p.EndsWith(".js")).ToList() : new();
            var tests = TestDeclarations.Scan(files);
            foreach (var r in spec.Rules)
            {
                if (tests.Active.Any(t => TestDeclarations.Mentions(t.Title, r.Id))) continue;
                E(r.Line, "COV-TEST", $"{r.Id} is in the title of no active test declaration under {implementDir}");
                foreach (var t in tests.Inactive.Where(t => TestDeclarations.Mentions(t.Title, r.Id)))
                    W(r.Line, "COV-TODO", $"{r.Id} is only in a {t.Form} declaration in {Path.GetFileName(t.File)}, a placeholder that runs no test");
            }
        }

        return f.OrderBy(x => x.Line).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
    }
}
