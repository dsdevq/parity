using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Parity.Model;
using Parity.Passes;

namespace Parity.Lint;

/// <summary>
/// A scenario of <c>registry/scenarios.json</c>: a situation the module is exercised in, by id and version. Only invented or
/// structural fields: a title, a role and rights as lowercase tokens, ids it exercises and cites, and dates. Fixtures is derived
/// from the fixtures that name it, never written by hand.
/// </summary>
public sealed record Scenario(string Id, int Version, string Title, string Role, List<string> Rights, bool Negative, List<string> Exercises,
    string? LastVerified, List<string> Cite, List<string> Fixtures);

public sealed record ScenarioQuery(string? Touches = null, string? Role = null, string? Right = null, bool? Negative = null, bool Unverified = false);

/// <summary>A human answer of <c>registry/decisions.json</c>: who decided, when, and the ids the answer is about.</summary>
public sealed record Decision(string Id, string Question, string Answer, string Owner, string Date, List<string> Cite);

public sealed record CatalogError(string Case, string Reply, string? Fixture, List<string> Cite);

public sealed record CatalogFixture(string Name, string Provenance, string? Date, string? Environment, string? Variant, string? Scenario, bool? ShapeVerified);

public sealed record CatalogEntry(string Id, string? Signature, JsonNode? Request, JsonNode? Reply, List<DependencyCaller> Callers, List<CatalogError> Errors, List<CatalogFixture> Fixtures);

/// <summary>
/// The module's registries (#17), under <c>legacy/&lt;module&gt;/registry/</c>: scenarios, decisions and the error cases of each
/// dependency. Each file carries <c>module</c>, the revision of the extract it was last reviewed against; a re-extract that moves
/// the revision makes the registry STALE until someone reviews it and runs <c>parity registry stamp</c>.
/// </summary>
public static partial class Registry
{
    public const string ScenariosFile = "scenarios.json", DecisionsFile = "decisions.json", DependenciesFile = "dependencies.json";
    private static readonly string[] AllFiles = { DecisionsFile, DependenciesFile, ScenariosFile };
    private static readonly string[] ScenarioKeys = { "id", "version", "title", "role", "rights", "negative", "exercises", "lastVerified", "cite" };
    private static readonly string[] DecisionKeys = { "id", "question", "answer", "owner", "date", "cite" };
    private static readonly string[] DependencyKeys = { "id", "errors" };
    private static readonly string[] ErrorKeys = { "case", "reply", "fixture", "cite" };

    [GeneratedRegex(@"^scn:[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex ScnIdRx();
    [GeneratedRegex(@"^dec:[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex DecIdRx();
    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$")] private static partial Regex TokenRx();
    [GeneratedRegex(@"""module""\s*:\s*""[^""]*""")] private static partial Regex StampRx();
    [GeneratedRegex(@"^(scn:[^@\s]+)(?:@(\d+))?$")] public static partial Regex ScenarioRefRx();

    public static string DirOf(string moduleDir) => Path.Combine(moduleDir, "registry");

    /// <summary>The module's extract revision: one hash over every manifest's hash, so any re-extract that changes a manifest moves it.</summary>
    public static string Revision(string moduleDir)
    {
        var dir = Path.Combine(moduleDir, "manifests");
        var lines = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Where(f => !f.EndsWith(".prev.json", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetFileNameWithoutExtension(f) + " " + ManifestHash.Of(f))
            : Enumerable.Empty<string>();
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return "sha256:" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Null when every registry file is stamped with the module's current revision, else why it is STALE.</summary>
    public static string? Stale(string moduleDir)
    {
        var rev = Revision(moduleDir);
        var stamps = AllFiles.Where(f => File.Exists(Path.Combine(DirOf(moduleDir), f)))
            .Select(f => (File: f, Stamp: StampRx().Match(File.ReadAllText(Path.Combine(DirOf(moduleDir), f))) is { Success: true } m ? m.Value.Split('"')[3] : "(none)"))
            .Where(x => x.Stamp != rev).ToList();
        if (stamps.Count == 0) return null;
        return $"the module is at {rev}, the registry was reviewed against {string.Join(", ", stamps.Select(x => $"{x.Stamp} ({x.File})"))}; "
            + "review registry/ against this extract (parity diff of each changed manifest, then scenarios, decisions and dependencies that cite it), then parity registry stamp";
    }

    // ---- loading ----

    private sealed class Loaded
    {
        public readonly List<Finding> Findings = new();
        public readonly List<(Scenario S, int Line)> Scenarios = new();
        public readonly List<(Decision D, int Line)> Decisions = new();
        public readonly List<(string Id, List<CatalogError> Errors, int Line)> Dependencies = new();
        public bool HasScenarios, HasDecisions, HasDependencies;
    }

    /// <summary>
    /// Reads the three files strictly: an unknown key, a missing field or a field of the wrong shape is REGISTRY-INVALID (DECISION-INVALID
    /// for a decision), so nothing rides along that the schema does not name.
    /// </summary>
    private static Loaded Load(string moduleDir)
    {
        var l = new Loaded();
        var reg = DirOf(moduleDir);
        foreach (var name in AllFiles)
        {
            var path = Path.Combine(reg, name);
            if (!File.Exists(path)) continue;
            var file = Rel(path);
            var text = File.ReadAllText(path);
            int LineOf(string? id) => id == null ? 0 : text.Replace("\r\n", "\n").Split('\n').Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Contains($"\"{id}\"", StringComparison.Ordinal)).i + 1;
            JsonElement root;
            try { root = JsonDocument.Parse(text).RootElement; }
            catch (JsonException ex) { l.Findings.Add(new Finding(file, 0, "REGISTRY-INVALID", "error", $"is not JSON: {ex.Message}")); continue; }
            var list = name switch { ScenariosFile => "scenarios", DecisionsFile => "decisions", _ => "dependencies" };
            void Bad(int line, string msg, string code = "REGISTRY-INVALID") => l.Findings.Add(new Finding(file, line, code, "error", msg));
            if (root.ValueKind != JsonValueKind.Object) { Bad(1, "must be a JSON object with 'module' and '" + list + "'"); continue; }
            foreach (var p in root.EnumerateObject().Where(p => p.Name != "module" && p.Name != list)) Bad(LineOf(p.Name), $"'{p.Name}' is not a key of {name}; it holds 'module' and '{list}'");
            if (!root.TryGetProperty("module", out var mod) || mod.ValueKind != JsonValueKind.String) Bad(1, "needs 'module': the revision it was reviewed against (written by parity registry init and stamp)");
            if (!root.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) { Bad(1, $"needs '{list}': [...]"); continue; }
            switch (name)
            {
                case ScenariosFile: l.HasScenarios = true; break;
                case DecisionsFile: l.HasDecisions = true; break;
                default: l.HasDependencies = true; break;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { Bad(0, $"every entry of '{list}' must be an object"); continue; }
                var id = Str(item, "id");
                var line = LineOf(id);
                var who = id ?? $"an entry of '{list}'";
                var keys = name switch { ScenariosFile => ScenarioKeys, DecisionsFile => DecisionKeys, _ => DependencyKeys };
                var code = name == DecisionsFile ? "DECISION-INVALID" : "REGISTRY-INVALID";
                foreach (var p in item.EnumerateObject().Where(p => !keys.Contains(p.Name)))
                    Bad(line, $"{who}: '{p.Name}' is not a field of {name} (fields: {string.Join(", ", keys)}); the registry holds only invented or structural fields, never data from the host", code);
                if (id != null && !seen.Add(id)) Bad(line, $"{id} is listed twice", code);
                switch (name)
                {
                    case ScenariosFile:
                    {
                        if (id == null || !ScnIdRx().IsMatch(id)) Bad(line, $"{who}: id must be scn:<lowercase-words>, got '{id}'");
                        var version = item.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 1 ? n : 0;
                        if (version == 0) Bad(line, $"{who}: version must be a whole number from 1 (raise it when the scenario's meaning changes)");
                        var title = Str(item, "title");
                        if (string.IsNullOrWhiteSpace(title)) Bad(line, $"{who}: needs a title");
                        var role = Str(item, "role");
                        if (role == null || !TokenRx().IsMatch(role)) Bad(line, $"{who}: role must be a lowercase token such as agent or supervisor (a role, never a person), got '{role}'");
                        var rights = Strs(item, "rights", out var rightsOk);
                        if (!rightsOk || rights.Any(r => !TokenRx().IsMatch(r))) Bad(line, $"{who}: rights must be a list of lowercase tokens such as policy.edit");
                        if (!item.TryGetProperty("negative", out var neg) || neg.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Bad(line, $"{who}: negative must be true or false");
                        var exercises = Strs(item, "exercises", out var exOk);
                        if (!exOk || exercises.Count == 0) Bad(line, $"{who}: exercises must list the ids it runs through (handler:, svc:, state:, ...)");
                        var last = Str(item, "lastVerified");
                        if (item.TryGetProperty("lastVerified", out var lv) && lv.ValueKind != JsonValueKind.Null && !IsDate(last)) Bad(line, $"{who}: lastVerified must be a YYYY-MM-DD date");
                        var cite = Strs(item, "cite", out var citeOk);
                        if (!citeOk) Bad(line, $"{who}: cite must be a list of ids");
                        if (id != null) l.Scenarios.Add((new Scenario(id, version, title ?? "", role ?? "", rights, neg.ValueKind == JsonValueKind.True, exercises, IsDate(last) ? last : null, cite, new()), line));
                        break;
                    }
                    case DecisionsFile:
                    {
                        if (id == null || !DecIdRx().IsMatch(id)) Bad(line, $"{who}: id must be dec:<lowercase-words>, got '{id}'", code);
                        string? Need(string key) { var s = Str(item, key); if (string.IsNullOrWhiteSpace(s)) Bad(line, $"{who}: needs '{key}'", code); return s; }
                        var q = Need("question"); var a = Need("answer"); var owner = Need("owner"); var date = Need("date");
                        if (date != null && !IsDate(date)) Bad(line, $"{who}: date must be YYYY-MM-DD, got '{date}'", code);
                        var cite = Strs(item, "cite", out var citeOk);
                        if (!citeOk || cite.Count == 0) Bad(line, $"{who}: needs 'cite': the ids the answer is about", code);
                        if (id != null) l.Decisions.Add((new Decision(id, q ?? "", a ?? "", owner ?? "", date ?? "", cite), line));
                        break;
                    }
                    default:
                    {
                        if (id == null || !id.StartsWith("svc:", StringComparison.Ordinal)) Bad(line, $"{who}: id must be a svc: id");
                        var errors = new List<CatalogError>();
                        if (!item.TryGetProperty("errors", out var errs) || errs.ValueKind != JsonValueKind.Array) Bad(line, $"{who}: needs 'errors': [...] (empty when the service has no error case)");
                        else foreach (var e in errs.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) { Bad(line, $"{who}: every error case must be an object"); continue; }
                            foreach (var p in e.EnumerateObject().Where(p => !ErrorKeys.Contains(p.Name))) Bad(line, $"{who}: '{p.Name}' is not a field of an error case (fields: {string.Join(", ", ErrorKeys)})");
                            var c = Str(e, "case");
                            if (c == null || !TokenRx().IsMatch(c)) Bad(line, $"{who}: an error case needs 'case', a lowercase token, got '{c}'");
                            var reply = Str(e, "reply");
                            if (string.IsNullOrWhiteSpace(reply)) Bad(line, $"{who}: error '{c}' needs 'reply', what the service answers");
                            var cite = Strs(e, "cite", out var citeOk);
                            if (!citeOk) Bad(line, $"{who}: error '{c}': cite must be a list of ids");
                            errors.Add(new CatalogError(c ?? "", reply ?? "", Str(e, "fixture"), cite));
                        }
                        if (id != null) l.Dependencies.Add((id, errors, line));
                        break;
                    }
                }
            }
        }
        return l;
    }

    private static string? Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strs(JsonElement o, string key, out bool ok)
    {
        ok = true;
        if (!o.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) { ok = false; return new(); }
        return v.EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    private static bool IsDate(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string Rel(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path).Replace('\\', '/');

    /// <summary>Every id the module's manifests and module.json define, for resolving what the registry cites and exercises.</summary>
    private static HashSet<string> ModuleIds(string moduleDir, ModuleIndex? module)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(moduleDir, "manifests");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.json").Where(f => !f.EndsWith(".prev.json", StringComparison.Ordinal)))
            {
                var m = JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(f), Json.Options)!;
                ids.Add(m.Id);
                ids.UnionWith(m.Controls.Select(x => x.Id).Concat(m.DynamicUi.Select(x => x.Id)).Concat(m.Events.Select(x => x.Id)).Concat(m.Handlers.Select(x => x.Id))
                    .Concat(m.Methods.Select(x => x.Id)).Concat(m.ServiceCalls.Select(x => x.Id)).Concat((m.Factories ?? new()).Select(x => x.Id))
                    .Concat((m.SharedState ?? new()).Select(x => x.Id)).Concat((m.Embeds ?? new()).Select(x => x.Id))
                    .Concat((m.Variants ?? new()).SelectMany(v => v.Delta.Select(d => d.Id))));
            }
        if (module?.SharedState != null) ids.UnionWith(module.SharedState.Fields.Select(x => x.Id));
        return ids;
    }

    private static ModuleIndex? ReadModule(string moduleDir)
    {
        var path = Path.Combine(moduleDir, "module.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(path), Json.Options) : null;
    }

    /// <summary>The scenario ids each spec rule names under <c>- scenarios:</c>, across the module's specs.</summary>
    private static HashSet<string> SpecScenarioRefs(string moduleDir)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(moduleDir, "spec");
        if (!Directory.Exists(dir)) return refs;
        foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            foreach (var r in SpecParser.Parse(File.ReadAllText(f)).Rules)
                foreach (var s in SpecParser.SplitIds(r.Get("scenarios")))
                    if (ScenarioRefRx().Match(s) is { Success: true } m) refs.Add(m.Groups[1].Value);
        return refs;
    }

    // ---- check ----

    /// <summary>
    /// <c>parity registry check</c>: STALE, schema, gaps against module.json, citations (manifest ids and ev: records re-read from the
    /// source), and scenarios nothing references or nobody verified. Those last two are warnings until <paramref name="gate"/>.
    /// </summary>
    public static List<Finding> Check(string moduleDir, bool gate)
    {
        var reg = DirOf(moduleDir);
        var f = new List<Finding>();
        if (!Directory.Exists(reg)) { f.Add(new Finding(Rel(reg), 0, "REGISTRY-MISSING", "error", "no registry for this module; run parity registry init")); return f; }
        var module = ReadModule(moduleDir);
        if (module == null) { f.Add(new Finding(Rel(moduleDir), 0, "REGISTRY-MISSING", "error", "no module.json; run parity extract first")); return f; }

        if (Stale(moduleDir) is { } why) f.Add(new Finding(Rel(reg), 0, "REGISTRY-STALE", "error", why));
        var l = Load(moduleDir);
        f.AddRange(l.Findings);
        foreach (var missing in AllFiles.Where(x => !File.Exists(Path.Combine(reg, x))))
            f.Add(new Finding(Rel(Path.Combine(reg, missing)), 0, "REGISTRY-MISSING", "error", "is missing; parity registry init writes the files a registry lacks"));

        var ids = ModuleIds(moduleDir, module);
        var evidence = new EvidenceSet(moduleDir);
        void Cites(string file, int line, string who, IEnumerable<string> cites, bool evOk = true)
        {
            foreach (var id in cites)
            {
                if (id.StartsWith("ev:", StringComparison.Ordinal) && evOk) { if (evidence.Check(id) is { } bad) f.Add(new Finding(file, line, bad.Code, "error", $"{who}: '{id}' {bad.Message}")); }
                else if (!ids.Contains(id)) f.Add(new Finding(file, line, "CITE-UNKNOWN", "error", $"{who}: '{id}' is not an id of the module's manifests"));
            }
        }

        var unreadable = new List<(string File, string Message)>();
        var fixtures = Fixtures.Load(Path.Combine(moduleDir, "fixtures"), unreadable);
        var scnFile = Rel(Path.Combine(reg, ScenariosFile));
        var depFile = Rel(Path.Combine(reg, DependenciesFile));
        var decFile = Rel(Path.Combine(reg, DecisionsFile));

        // dependencies: every svc: id of module.json has an entry, and every error case is backed by a fixture or a citation
        if (l.HasDependencies)
        {
            var listed = l.Dependencies.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var svc in module.ServiceCalls.Where(x => !listed.Contains(x)))
                f.Add(new Finding(depFile, 0, "REGISTRY-GAP", "error", $"{svc} is called in the module but has no entry; add {{ \"id\": \"{svc}\", \"errors\": [...] }} after reviewing its error cases"));
            foreach (var (id, errors, line) in l.Dependencies)
            {
                if (!module.ServiceCalls.Contains(id)) f.Add(new Finding(depFile, line, "REGISTRY-ORPHAN", "error", $"{id} is no longer called in the module; remove the entry or re-extract the form that calls it"));
                foreach (var e in errors)
                {
                    var who = $"{id} error '{e.Case}'";
                    if (e.Fixture == null && e.Cite.Count == 0) f.Add(new Finding(depFile, line, "REGISTRY-UNCITED", "error", $"{who} names neither a fixture nor a cite; nothing backs the claim"));
                    if (e.Fixture != null && !fixtures.TryGetValue(e.Fixture, out var fx)) f.Add(new Finding(depFile, line, "FIXTURE-MISSING", "error", $"{who}: fixture '{e.Fixture}' not found at fixtures/{e.Fixture}.json"));
                    else if (e.Fixture != null && fixtures[e.Fixture].Service is { } s && s != id) f.Add(new Finding(depFile, line, "FIXTURE-MISSING", "error", $"{who}: fixture '{e.Fixture}' records {s}, not {id}"));
                    Cites(depFile, line, who, e.Cite);
                }
            }
        }

        // scenarios: ids resolve, a citation backs each, and each is referenced (by a fixture or a spec rule) and verified
        var scenarioIds = l.Scenarios.Select(x => x.S.Id).ToHashSet(StringComparer.Ordinal);
        var specRefs = SpecScenarioRefs(moduleDir);
        var sev = gate ? "error" : "warning";
        foreach (var (s, line) in l.Scenarios)
        {
            foreach (var id in s.Exercises.Where(x => !ids.Contains(x))) f.Add(new Finding(scnFile, line, "CITE-UNKNOWN", "error", $"{s.Id}: exercises '{id}', which is not an id of the module's manifests"));
            if (s.Cite.Count == 0) f.Add(new Finding(scnFile, line, "REGISTRY-UNCITED", "error", $"{s.Id} cites nothing; cite the handler:, method: or ev: ids that show it"));
            Cites(scnFile, line, s.Id, s.Cite);
            if (!specRefs.Contains(s.Id) && !fixtures.Values.Any(x => x.Scenario == s.Id))
                f.Add(new Finding(scnFile, line, "SCENARIO-UNREFERENCED", sev, $"{s.Id}: no fixture names it and no spec rule lists it under '- scenarios:'"));
            if (s.LastVerified == null) f.Add(new Finding(scnFile, line, "SCENARIO-UNVERIFIED", sev, $"{s.Id} has no lastVerified; run it against the host and set the date"));
        }
        if (l.HasScenarios)
            foreach (var fx in fixtures.Values.Where(x => x.Scenario != null && !scenarioIds.Contains(x.Scenario)).OrderBy(x => x.Name, StringComparer.Ordinal))
                f.Add(new Finding(scnFile, 0, "SCENARIO-UNKNOWN", "error", $"fixtures/{fx.Name}.json names {fx.Scenario}, which the registry lacks"));

        foreach (var (d, line) in l.Decisions) Cites(decFile, line, d.Id, d.Cite);

        return f.OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
    }

    // ---- what specs read ----

    /// <summary>Each scenario's id and version, or null when the module has no readable scenarios file.</summary>
    public static Dictionary<string, int>? ScenarioVersions(string moduleDir)
    {
        if (!File.Exists(Path.Combine(DirOf(moduleDir), ScenariosFile))) return null;
        var l = Load(moduleDir);
        return l.HasScenarios ? l.Scenarios.GroupBy(x => x.S.Id).ToDictionary(g => g.Key, g => g.First().S.Version, StringComparer.Ordinal) : null;
    }

    public static List<Decision> Decisions(string moduleDir) => Load(moduleDir).Decisions.Select(x => x.D).ToList();

    // ---- query and catalog ----

    /// <summary><c>parity registry query</c>: the scenarios matching every filter given, by id, each with the fixtures that name it.</summary>
    public static List<Scenario> Query(string moduleDir, ScenarioQuery q)
    {
        if (!File.Exists(Path.Combine(DirOf(moduleDir), ScenariosFile))) throw new InvalidOperationException($"no {Rel(Path.Combine(DirOf(moduleDir), ScenariosFile))}; run parity registry init");
        var fixtures = Fixtures.Load(Path.Combine(moduleDir, "fixtures"), new());
        return Load(moduleDir).Scenarios.Select(x => x.S)
            .Where(s => q.Touches == null || s.Exercises.Contains(q.Touches))
            .Where(s => q.Role == null || s.Role == q.Role)
            .Where(s => q.Right == null || s.Rights.Contains(q.Right))
            .Where(s => q.Negative == null || s.Negative == q.Negative)
            .Where(s => !q.Unverified || s.LastVerified == null)
            .Select(s => s with { Fixtures = fixtures.Values.Where(x => x.Scenario == s.Id).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList() })
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <c>parity registry catalog</c>: module.json's dependencies (shape and callers, from the extract) joined with the registry's error
    /// cases and the provenance of every fixture that records the service.
    /// </summary>
    public static List<CatalogEntry> Catalog(string moduleDir)
    {
        var module = ReadModule(moduleDir) ?? throw new InvalidOperationException($"no module.json under {Rel(moduleDir)}; run parity extract first");
        var errors = Load(moduleDir).Dependencies.ToDictionary(x => x.Id, x => x.Errors, StringComparer.Ordinal);
        var fixtures = Fixtures.Load(Path.Combine(moduleDir, "fixtures"), new());
        return (module.Dependencies ?? new()).Select(d => new CatalogEntry(d.Id, d.Signature, d.Request, d.Reply, d.Callers,
            errors.TryGetValue(d.Id, out var e) ? e : new(),
            fixtures.Values.Where(x => x.Service == d.Id).OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new CatalogFixture(x.Name, x.Synthetic ? "synthetic" : "recorded", x.Synthetic ? null : x.Recorded, x.Environment, x.Variant, x.Scenario, x.ShapeVerified)).ToList())).ToList();
    }

    public static string RenderCatalog(List<CatalogEntry> catalog)
    {
        var sb = new StringBuilder("# Dependency catalog\n\nGenerated by parity registry catalog from module.json, registry/dependencies.json and fixtures/. Do not edit.\n");
        foreach (var c in catalog)
        {
            sb.Append($"\n## {c.Id}\n\n");
            if (c.Signature != null) sb.Append($"`{c.Signature}`\n\n");
            sb.Append($"- request: `{c.Request?.ToJsonString() ?? "none"}`\n- reply: `{c.Reply?.ToJsonString() ?? "none"}`\n");
            sb.Append("\nCallers:\n");
            foreach (var x in c.Callers) sb.Append($"- {x.Unit} `{x.Via}` ({string.Join(", ", x.Args)})\n");
            sb.Append("\nErrors:\n");
            if (c.Errors.Count == 0) sb.Append("- none recorded\n");
            foreach (var e in c.Errors)
                sb.Append($"- {e.Case}: {e.Reply}" + (e.Fixture != null ? $" (fixture {e.Fixture})" : "") + (e.Cite.Count > 0 ? $" (cite {string.Join(", ", e.Cite)})" : "") + "\n");
            sb.Append("\nFixtures:\n");
            if (c.Fixtures.Count == 0) sb.Append("- none\n");
            foreach (var x in c.Fixtures)
            {
                var what = x.Provenance == "synthetic" ? "synthetic" + (x.ShapeVerified == true ? ", shape verified" : ", shape not verified") : $"recorded {x.Date}" + (x.Environment != null ? $", {x.Environment}" : "");
                sb.Append($"- {x.Name}: {what}" + (x.Variant != null ? $"; variant {x.Variant}" : "") + (x.Scenario != null ? $"; {x.Scenario}" : "") + "\n");
            }
        }
        return sb.ToString();
    }

    // ---- stamp and init ----

    /// <summary>
    /// <c>parity registry stamp</c>: records that the registry was reviewed against the current extract. Refuses while check finds any
    /// error other than STALE itself, so a gap (a new svc: id) or a broken citation cannot be stamped over.
    /// </summary>
    public static string Stamp(string moduleDir)
    {
        var blocking = Check(moduleDir, gate: false).Where(x => x.Severity == "error" && x.Code != "REGISTRY-STALE").ToList();
        if (blocking.Count > 0)
            throw new InvalidOperationException($"registry stamp refused: {blocking.Count} error(s) to fix first:\n" + string.Join("\n", blocking.Select(x => x.ToString())));
        var rev = Revision(moduleDir);
        foreach (var name in AllFiles)
        {
            var path = Path.Combine(DirOf(moduleDir), name);
            File.WriteAllText(path, StampRx().Replace(File.ReadAllText(path), $"\"module\": \"{rev}\"", 1));
        }
        return rev;
    }

    /// <summary>
    /// <c>parity registry init</c>: writes the three files stamped with the current revision, dependencies seeded with every svc: id of
    /// module.json and no error case. Never overwrites: a registry that exists is refused whole.
    /// </summary>
    public static List<string> Init(string moduleDir)
    {
        var module = ReadModule(moduleDir) ?? throw new InvalidOperationException($"no module.json under {Rel(moduleDir)}; run parity extract first");
        var reg = DirOf(moduleDir);
        var existing = AllFiles.Where(x => File.Exists(Path.Combine(reg, x))).ToList();
        if (existing.Count > 0) throw new InvalidOperationException($"{Rel(reg)} already has {string.Join(", ", existing)}; init never overwrites a registry");
        Directory.CreateDirectory(reg);
        var rev = Revision(moduleDir);
        var deps = string.Join(",\n", module.ServiceCalls.Select(s => $"    {{ \"id\": \"{s}\", \"errors\": [] }}"));
        File.WriteAllText(Path.Combine(reg, DependenciesFile), $"{{\n  \"module\": \"{rev}\",\n  \"dependencies\": [\n{deps}\n  ]\n}}\n");
        File.WriteAllText(Path.Combine(reg, ScenariosFile), $"{{\n  \"module\": \"{rev}\",\n  \"scenarios\": []\n}}\n");
        File.WriteAllText(Path.Combine(reg, DecisionsFile), $"{{\n  \"module\": \"{rev}\",\n  \"decisions\": []\n}}\n");
        return AllFiles.Select(x => "registry/" + x).ToList();
    }
}
