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
    [GeneratedRegex(@"^closed by evidence\b")] private static partial Regex ClosedByEvidence();
    [GeneratedRegex(@"^closed by decision\b")] private static partial Regex ClosedByDecision();
    [GeneratedRegex(@"\bdecided-by\s+(dec:\S+?)[.,;)]?(?:\s|$)")] private static partial Regex DecidedByDecision();
    [GeneratedRegex(@"\bdec:[a-z0-9]+(?:-[a-z0-9]+)*")] private static partial Regex DecIdRx();
    [GeneratedRegex(@"\bev:[A-Za-z_]\w*/\d+")] private static partial Regex EvIdRx();
    [GeneratedRegex(@"\bsvc:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*")] private static partial Regex SvcIdRx();
    [GeneratedRegex(@"\bR-\w+-\d+\b")] private static partial Regex RuleIdRx();
    [GeneratedRegex(@"^(\S+)\s+-\s+(\S.*)$")] private static partial Regex OutcomeGapRx();

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

    public static List<Finding> Lint(string specPath, bool gate, string? implementDir) => Lint(specPath, gate, implementDir, out _);

    /// <summary>The line printed after the findings: error and warning counts, then the fixture tally when lint got that far.</summary>
    public static string Summary(IReadOnlyCollection<Finding> findings, FixtureTally? fixtures)
    {
        var errors = findings.Count(x => x.Severity == "error");
        return $"{errors} error(s), {findings.Count - errors} warning(s)" + (fixtures == null ? "" : $", {fixtures}");
    }

    /// <param name="fixtures">the fixtures the spec lists that exist, and how many of them are synthetic; null when lint stopped before reading the spec.</param>
    public static List<Finding> Lint(string specPath, bool gate, string? implementDir, out FixtureTally? fixtures)
    {
        fixtures = null;
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
        else if (mv.Value != hash) { E(mv.Line, "STALE", $"spec was written against {mv.Value}, manifest is now {hash}; run parity rebind {file}: it moves the spec when nothing it cites changed, else lists the rules to re-read"); return f; }

        var m = JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(manifestPath), Json.Options)!;
        var controlIds = m.Controls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var dynIds = m.DynamicUi.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var eventIds = m.Events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var handlerIds = m.Handlers.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var methodIds = m.Methods.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var svcIds = m.ServiceCalls.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        // variants (#20): each delta id belongs to one variant; a rule's scope is base plus every variant unless it says 'variants:'
        var variants = m.Variants ?? new List<VariantInfo>();
        var deltaOf = variants.SelectMany(v => v.Delta.Select(d => (v, d))).ToDictionary(x => x.d.Id, x => x, StringComparer.Ordinal);
        var replacedIn = variants.ToDictionary(v => v.Name, v => v.Delta.Where(d => d.Kind == "override" && d.Detail?.StartsWith("replaces base", StringComparison.Ordinal) == true && d.Target != null)
            .ToDictionary(d => d.Target!, d => d.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        var removedIn = variants.ToDictionary(v => v.Name, v => v.Delta.Where(d => d.Kind == "removed" && d.Target != null).ToDictionary(d => d.Target!, d => d.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var x in variants.SelectMany(v => v.Delta).Where(d => d.Kind == "service-added" && d.Target != null)) svcIds.Add(x.Target!);
        var allIds = controlIds.Concat(dynIds).Concat(eventIds).Concat(handlerIds).Concat(methodIds).Concat(svcIds)
            .Concat((m.Factories ?? new()).Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
        var variantCovered = variants.ToDictionary(v => v.Name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        // ev: ids live outside the manifest, in evidence/<Unit>.json; each is re-read from the legacy source
        var evidence = new EvidenceSet(moduleDir);
        void Ev(int line, string who, string id) { if (evidence.Check(id) is { } bad) E(line, bad.Code, $"{who}: '{id}' {bad.Message}"); }
        // dec: and scn: ids live in registry/ (#17): a decision stands for its owner and date, a scenario is named with its version
        var decisions = new Lazy<HashSet<string>>(() => Registry.Decisions(moduleDir).Select(x => x.Id).ToHashSet(StringComparer.Ordinal));
        var scenarioVersions = new Lazy<Dictionary<string, int>?>(() => Registry.ScenarioVersions(moduleDir));
        bool KnownDecision(int line, string who, string id)
        {
            if (decisions.Value.Contains(id)) return true;
            E(line, "DECISION-UNKNOWN", $"{who}: '{id}' is not in registry/decisions.json; record the answer there (question, answer, owner, date, cite) first");
            return false;
        }
        // a 'decided-by' note: either a dec: id, or a name and a date written inline
        bool DecidedBy(int line, string who, string text)
        {
            if (DecidedByDecision().Match(text) is { Success: true } dm) { KnownDecision(line, who, dm.Groups[1].Value); return true; }
            return Decided().IsMatch(text);
        }

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

            var scope = new HashSet<string>(StringComparer.Ordinal);
            if (r.Get("variants") is { } vs)
            {
                foreach (var name in SpecParser.SplitIds(vs))
                {
                    if (name != "base" && !variantCovered.ContainsKey(name))
                        E(r.LineOf("variants"), "VAR-UNKNOWN", variants.Count == 0 ? $"{r.Id}: {formName} has no variants; drop 'variants:'" : $"{r.Id}: variant '{name}' is not one of base, {string.Join(", ", variants.Select(v => v.Name))}");
                    else scope.Add(name);
                }
                if (scope.Count == 0) E(r.LineOf("variants"), "GRAMMAR", $"{r.Id}: 'variants:' lists nothing (omit it for base and every variant)");
            }
            else { scope.Add("base"); foreach (var v in variants) scope.Add(v.Name); }
            var inBase = scope.Contains("base");

            var covers = SpecParser.SplitIds(r.Get("covers"));
            if (covers.Count == 0 && r.Get("covers") != null) E(r.LineOf("covers"), "ORPHAN-RULE", $"{r.Id} covers nothing");
            foreach (var id in covers)
            {
                if (id.StartsWith("ev:", StringComparison.Ordinal)) E(r.LineOf("covers"), "CITE-KIND", $"{r.Id}: covers must list evt: (or dyn:) ids, got '{id}'");
                else if (id.StartsWith("var:", StringComparison.Ordinal)) VarRef(r, "covers", id, scope);
                else if (!allIds.Contains(id)) E(r.LineOf("covers"), "CITE-UNKNOWN", $"{r.Id}: '{id}' is not in the manifest");
                else if (!id.StartsWith("evt:", StringComparison.Ordinal) && !id.StartsWith("dyn:", StringComparison.Ordinal)) E(r.LineOf("covers"), "CITE-KIND", $"{r.Id}: covers must list evt: (or dyn:) ids, got '{id}'");
                else
                {
                    if (inBase) covered.Add(id);
                    var ctl = m.Events.FirstOrDefault(e => e.Id == id)?.Control;
                    foreach (var v in scope.Where(n => n != "base"))
                        if (ctl != null && removedIn[v].TryGetValue(ctl, out var rid))
                            E(r.LineOf("covers"), "VAR-SCOPE", $"{r.Id}: covers {id}, but {v} removes that control ({rid}); scope the rule without {v}");
                }
            }
            var cites = SpecParser.SplitIds(r.Get("cite"));
            if (cites.Count > 0 && cites.All(id => id.StartsWith("ev:", StringComparison.Ordinal)))
                E(r.LineOf("cite"), "CITE-KIND", $"{r.Id}: cite needs a handler: or method: id beside its ev: ids");
            foreach (var id in cites)
            {
                if (id.StartsWith("ev:", StringComparison.Ordinal)) Ev(r.LineOf("cite"), r.Id, id);
                else if (id.StartsWith("var:", StringComparison.Ordinal)) VarRef(r, "cite", id, scope);
                else if (!allIds.Contains(id)) E(r.LineOf("cite"), "CITE-UNKNOWN", $"{r.Id}: '{id}' is not in the manifest");
                else if (!id.StartsWith("handler:", StringComparison.Ordinal) && !id.StartsWith("method:", StringComparison.Ordinal)) E(r.LineOf("cite"), "CITE-KIND", $"{r.Id}: cite must list handler:, method: or ev: ids, got '{id}'");
                else
                {
                    if (inBase) cited.Add(id);
                    foreach (var v in scope.Where(n => n != "base"))
                        if (replacedIn[v].TryGetValue(id, out var oid))
                            E(r.LineOf("cite"), "VAR-SCOPE", $"{r.Id}: cites {id}, which {v} replaces ({oid}); scope the rule without {v} and describe {v} in a rule citing {oid}");
                }
            }
            if (r.Get("then") is { } then && then.Length > 0 && !Regex.IsMatch(then, @"\b(ctrl|dyn|svc|form|var):\S+") && !Regex.IsMatch(then, @"\b[a-z][A-Za-z0-9]*[A-Z][A-Za-z0-9]*\b"))
                E(r.LineOf("then"), "GRAMMAR", $"{r.Id}: 'then' must name an observable outcome: a control (ctrl:/dyn: id or control name), a service (svc:) or a form shown (form:)");
            foreach (var key in new[] { "given", "when", "then" })
                if (r.Get(key) is { } v && HedgeWords.Any(h => Regex.IsMatch(v, $@"\b{Regex.Escape(h)}\b", RegexOptions.IgnoreCase)))
                    E(r.LineOf(key), "HEDGE", $"{r.Id}: '{key}' hedges ({HedgeWords.First(h => Regex.IsMatch(v, $@"\b{Regex.Escape(h)}\b", RegexOptions.IgnoreCase))}); state the legacy fact or move it to Open questions");
            if (r.Get("quirk") is { } q)
            {
                if (!q.StartsWith("preserve", StringComparison.Ordinal) && !q.StartsWith("diverge", StringComparison.Ordinal)) E(r.LineOf("quirk"), "GRAMMAR", $"{r.Id}: quirk must start with preserve or diverge");
                if (!DecidedBy(r.LineOf("quirk"), r.Id, q)) E(r.LineOf("quirk"), "DROP-REASON", $"{r.Id}: quirk needs 'decided-by dec:<id>' or 'decided-by <name> <YYYY-MM-DD>'");
            }
            foreach (var fx in SpecParser.SplitIds(r.Get("fixtures"))) ruleFixtures.Add((fx, r.LineOf("fixtures")));
            foreach (var s in SpecParser.SplitIds(r.Get("scenarios")))
            {
                var line = r.LineOf("scenarios");
                if (Registry.ScenarioRefRx().Match(s) is not { Success: true } sm) { E(line, "GRAMMAR", $"{r.Id}: scenarios lists scn:<id>@<version>, got '{s}'"); continue; }
                var sid = sm.Groups[1].Value;
                if (scenarioVersions.Value == null) E(line, "SCENARIO-UNKNOWN", $"{r.Id}: '{sid}' cannot be checked: the module has no readable registry/scenarios.json");
                else if (!scenarioVersions.Value.TryGetValue(sid, out var v)) E(line, "SCENARIO-UNKNOWN", $"{r.Id}: '{sid}' is not in registry/scenarios.json");
                else if (!sm.Groups[2].Success) E(line, "SCENARIO-VERSION", $"{r.Id}: '{sid}' names no version; write {sid}@{v} (registry has v{v})");
                else if (int.Parse(sm.Groups[2].Value) != v) E(line, "SCENARIO-VERSION", $"{r.Id}: '{s}' was written against v{sm.Groups[2].Value}, registry has v{v}; re-read the scenario, then update the rule and its version");
            }

            // outcomes are declared, never inferred from prose; each one is a fixture variant or a stated gap
            var outcomes = SpecParser.SplitIds(r.Get("outcomes"));
            if (r.Get("outcomes") != null && outcomes.Count == 0) E(r.LineOf("outcomes"), "GRAMMAR", $"{r.Id}: outcomes lists nothing");
            foreach (var dup in outcomes.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1)) E(r.LineOf("outcomes"), "GRAMMAR", $"{r.Id}: outcome '{dup.Key}' is listed twice");
            var gapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (gap, line) in r.OutcomeGaps)
            {
                var gm = OutcomeGapRx().Match(gap);
                if (!gm.Success) E(line, "GRAMMAR", $"{r.Id}: outcome-gap must be '<outcome> - <reason>'");
                else if (!outcomes.Contains(gm.Groups[1].Value)) E(line, "GRAMMAR", $"{r.Id}: outcome-gap names '{gm.Groups[1].Value}', which is not in outcomes");
                else if (!gapped.Add(gm.Groups[1].Value)) E(line, "GRAMMAR", $"{r.Id}: outcome '{gm.Groups[1].Value}' has two outcome-gap lines");
            }
        }

        // a var: id names one variant's delta: it is described only by a rule scoped to that variant, never the base
        void VarRef(SpecRule r, string key, string id, HashSet<string> scope)
        {
            if (!deltaOf.TryGetValue(id, out var hit)) { E(r.LineOf(key), "VAR-UNKNOWN", $"{r.Id}: '{id}' is not a delta id of any variant in the manifest"); return; }
            var code = hit.d.Kind is "override" or "handler" or "ctor";
            if (code != (key == "cite")) { E(r.LineOf(key), "CITE-KIND", $"{r.Id}: {id} is a {hit.d.Kind} delta; it belongs in {(code ? "cite" : "covers")}"); return; }
            if (!scope.Contains(hit.v.Name) || scope.Contains("base"))
            {
                E(r.LineOf(key), "VAR-SCOPE", $"{r.Id}: {id} exists only in {hit.v.Name}; the rule needs 'variants: {hit.v.Name}' (without base)");
                return;
            }
            variantCovered[hit.v.Name].Add(id);
        }

        var noRule = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in spec.VariantNoRule)
        {
            var id = row.Cell(0).Trim('`');
            if (!deltaOf.TryGetValue(id, out var hit)) { E(row.Line, "VAR-UNKNOWN", $"Variant deltas without a rule: '{id}' is not a delta id of any variant in the manifest"); continue; }
            if (variantCovered[hit.v.Name].Contains(id)) E(row.Line, "GRAMMAR", $"Variant deltas without a rule: {id} is also described by a rule; pick one");
            else noRule.Add(id);
            if (row.Cell(1).Length == 0) E(row.Line, "GRAMMAR", $"Variant deltas without a rule: {id} needs a reason");
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
            if (dispKind == "dropped" && !DecidedBy(row.Line, "Fields", row.Cell(5))) E(row.Line, "DROP-REASON", $"Fields: dropped {id} needs a note with 'decided-by dec:<id>' or 'decided-by <name> <YYYY-MM-DD>'");
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

        // a question closed by evidence or by a decision stays listed and links the record that answers it; only the rest are open
        var open = new List<(string Text, int Line)>();
        foreach (var (text, line) in spec.OpenQuestions)
        {
            if (ClosedByDecision().IsMatch(text))
            {
                var decIds = DecIdRx().Matches(text).Select(x => x.Value).Distinct().ToList();
                if (decIds.Count == 0) E(line, "DECISION-UNKNOWN", $"Open questions: closed by decision but links no dec: id: {text}");
                if (decIds.Count == 0 || !decIds.All(id => KnownDecision(line, "Open questions", id))) open.Add((text, line));
                continue;
            }
            if (!ClosedByEvidence().IsMatch(text)) { open.Add((text, line)); continue; }
            var evIds = EvIdRx().Matches(text).Select(x => x.Value).Distinct().ToList();
            if (evIds.Count == 0) E(line, "EV-UNLINKED", $"Open questions: closed by evidence but links no ev: id: {text}");
            foreach (var id in evIds) Ev(line, "Open questions", id);
        }

        // the fixtures this spec lists, each with the first line that lists it; counted with or without --gate
        var fixturesDir = Path.Combine(moduleDir, "fixtures");
        var unreadable = new List<(string File, string Message)>();
        var fixtureFiles = Fixtures.Load(fixturesDir, unreadable);
        var listed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, line) in svcRows.Values.SelectMany(row => SpecParser.SplitIds(row.Cell(2)).Select(n => (n, row.Line))).Concat(ruleFixtures))
            listed[name] = listed.TryGetValue(name, out var first) ? Math.Min(first, line) : line;
        var present = listed.Keys.Where(fixtureFiles.ContainsKey).Select(n => fixtureFiles[n]).ToList();
        fixtures = new FixtureTally(present.Count, present.Count(x => x.Synthetic));

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

        foreach (var (name, msg) in unreadable) E(0, "FIXTURE-INVALID", $"{name}: {msg}");
        foreach (var (name, line) in listed)
        {
            if (!fixtureFiles.TryGetValue(name, out var fx)) continue;                          // FIXTURE-MISSING below
            foreach (var p in fx.Problems) E(line, "FIXTURE-INVALID", $"{name}.json {p}");
            if (!fx.Synthetic) continue;
            var lacks = new[] { fx.ShapeVerified == true ? null : "shapeVerified: true", string.IsNullOrWhiteSpace(fx.Note) ? "a note" : null }.OfType<string>().ToList();
            if (lacks.Count > 0) E(line, "FIXTURE-SYNTHETIC", $"{name}.json is synthetic but lacks {string.Join(" and ", lacks)}; check its shape against a real reply of {fx.Service} and say in the note what it stands for");
        }
        foreach (var s in m.ServiceCalls)
        {
            if (!svcRows.TryGetValue(s.Id, out var row)) { E(0, "COV-SERVICE", $"{s.Id} has no row under Service calls"); continue; }
            var fxs = SpecParser.SplitIds(row.Cell(2));
            if (fxs.Count == 0) E(row.Line, "COV-SERVICE", $"{s.Id} lists no fixtures");
            foreach (var fx in fxs)
            {
                if (!fixtureFiles.TryGetValue(fx, out var ff)) E(row.Line, "FIXTURE-MISSING", $"fixture '{fx}' not found at fixtures/{fx}.json");
                else if (ff.Service != null && ff.Service != s.Id) E(row.Line, "FIXTURE-MISSING", $"fixture '{fx}' records {ff.Service}, not {s.Id}");
            }
        }
        foreach (var (name, line) in ruleFixtures)
            if (!fixtureFiles.ContainsKey(name)) E(line, "FIXTURE-MISSING", $"fixture '{name}' not found at fixtures/{name}.json");

        // per rule: its own fixtures: when it lists any, otherwise the Service calls rows of the services it touches (a row naming it, a svc: in then)
        foreach (var r in spec.Rules)
        {
            var names = SpecParser.SplitIds(r.Get("fixtures"));
            if (names.Count == 0)
            {
                var services = svcRows.Where(kv => RuleIdRx().Matches(kv.Value.Cell(1)).Any(x => x.Value == r.Id)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
                foreach (Match sm in SvcIdRx().Matches(r.Get("then") ?? "")) if (svcRows.ContainsKey(sm.Value)) services.Add(sm.Value);
                names = services.SelectMany(svc => SpecParser.SplitIds(svcRows[svc].Cell(2))).ToList();
            }
            var usable = names.Where(fixtureFiles.ContainsKey).Select(n => fixtureFiles[n]).DistinctBy(x => x.Name).ToList();
            if (usable.Count > 0 && usable.All(x => x.Synthetic))
                W(r.Line, "FIXTURE-SYNTHETIC", $"{r.Id}: every fixture it runs on is synthetic ({string.Join(", ", usable.Select(x => x.Name))}); a test built on them must say it runs on synthetic replies");
            var gapped = r.OutcomeGaps.Select(g => OutcomeGapRx().Match(g.Value)).Where(x => x.Success).Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var o in SpecParser.SplitIds(r.Get("outcomes")).Distinct(StringComparer.Ordinal))
                if (!gapped.Contains(o) && !usable.Any(x => x.Variant == o))
                    E(r.LineOf("outcomes"), "FIXTURE-OUTCOME", $"{r.Id}: outcome '{o}' has no listed fixture with variant '{o}' and no 'outcome-gap: {o} - <reason>'");
        }

        foreach (var v in variants)
            foreach (var d in v.Delta.Where(d => !variantCovered[v.Name].Contains(d.Id) && !noRule.Contains(d.Id)))
                E(0, "VAR-UNCOVERED", $"{d.Id} ({d.Kind}) is neither described by a rule scoped 'variants: {v.Name}' nor listed under Variant deltas without a rule");

        var raw = m.Raw.Concat(variants.SelectMany(v => v.Raw)).ToList();
        var allDiags = m.Diagnostics.Concat(variants.SelectMany(v => v.Diagnostics)).ToList();
        if (raw.Count > 0 || allDiags.Count > 0)
        {
            var debtPath = Path.Combine(moduleDir, "extractor-debt.md");
            var debt = File.Exists(debtPath) ? File.ReadAllLines(debtPath) : Array.Empty<string>();
            bool Acknowledged(string key) => debt.Any(l => l.Contains(key, StringComparison.Ordinal) && l.Contains("owner", StringComparison.OrdinalIgnoreCase) && DateRx().IsMatch(l));
            foreach (var r in raw)
                if (!Acknowledged($"{r.Loc.File}:{r.Loc.Start}")) E(0, "COV-RAW", $"raw designer statement at {r.Loc.File}:{r.Loc.Start} is not acknowledged in extractor-debt.md (line must carry '{r.Loc.File}:{r.Loc.Start}', an owner and a YYYY-MM-DD)");
            foreach (var d in allDiags)
            {
                var key = DebtKey(d.Code, d.Message);
                if (!Acknowledged(key)) E(0, "COV-RAW", $"diagnostic '{key}' is not acknowledged in extractor-debt.md (a line must carry exactly that text, an owner and a YYYY-MM-DD; the bare code acknowledges nothing)");
            }
        }

        if (open.Count != 1 || !string.Equals(open[0].Text, "none", StringComparison.OrdinalIgnoreCase))
            foreach (var (text, line) in open.Where(q => !string.Equals(q.Text, "none", StringComparison.OrdinalIgnoreCase)))
                E(line, "OPEN", $"open question blocks the gate: {text}");
        if (open.Count == 0) E(0, "OPEN", "Open questions must contain '- none' to pass the gate");

        // the accumulation protocol (#17): a module with a registry is not done until the registry was reviewed against this extract
        if (Directory.Exists(Registry.DirOf(moduleDir)) && Registry.Stale(moduleDir) is { } stale) E(0, "REGISTRY-STALE", $"registry/: {stale}");

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
