using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Lint;
using Parity.Model;

namespace Parity.Oracle;

/// <summary>
/// One normalization rule: values of <see cref="Target"/> (request, reply, dataset, or any of them) at <see cref="Field"/> (and matching <see cref="Match"/>, when given) are
/// masked on both sides before comparing, within <see cref="Scope"/>: <c>global</c>, <c>call:&lt;Category/Type glob&gt;</c> or
/// <c>scn:&lt;id&gt;</c>. Every rule names the human decision that allows it.
/// </summary>
public sealed record NormRule(string Id, string Decision, string Scope, string Target, string Field, string? Match);

/// <summary>The loaded rule set of a module: its optional version label, its hash (what a verification is bound to) and the rules.</summary>
public sealed class NormSet
{
    public int Version { get; init; }
    public string Hash { get; init; } = "none";
    public List<NormRule> Rules { get; init; } = new();
    private readonly Dictionary<string, Regex?> _rx = new(StringComparer.Ordinal);
    /// <summary>How many values each rule masked in the last comparison, both sides counted.</summary>
    public readonly SortedDictionary<string, int> Masked = new(StringComparer.Ordinal);

    internal void Compile() { foreach (var r in Rules) _rx[r.Id] = r.Match == null ? null : new Regex(r.Match, RegexOptions.CultureInvariant); }

    private static bool InScope(string scope, string callKey, string scenario) =>
        scope == "global" || scope == scenario || (scope.StartsWith("call:", StringComparison.Ordinal) && Payload.CallMatch(scope[5..], callKey));

    /// <summary>The value with every applicable rule's mask applied.</summary>
    public string Mask(string target, string callKey, string scenario, string path, string value)
    {
        foreach (var r in Rules)
        {
            if ((r.Target != target && r.Target != "any") || !InScope(r.Scope, callKey, scenario) || !Payload.GlobMatch(r.Field, path)) continue;
            var mask = $"<{r.Id}>";
            var rx = _rx[r.Id];
            var nv = rx == null ? mask : rx.Replace(value, mask);
            if (nv == value) continue;
            Masked[r.Id] = Masked.GetValueOrDefault(r.Id) + 1;
            value = nv;
        }
        return value;
    }
}

/// <summary>
/// <c>legacy/&lt;Module&gt;/recordings/normalization.json</c>: the rule set the diff applies. It sits under
/// <c>recordings/</c>, so the guard keeps it read-only to the agent outside the record phase, and every rule must name a
/// <c>dec:</c> entry of <c>registry/decisions.json</c> that cites the rule back: a rule nobody decided is refused, not applied.
/// </summary>
public static partial class Normalization
{
    public const string FileName = "normalization.json";
    private static readonly string[] RuleKeys = { "id", "decision", "scope", "target", "field", "match" };

    [GeneratedRegex(@"^norm:[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex IdRx();
    [GeneratedRegex(@"^(global|call:[^/\s]+/[^/\s]+|scn:[a-z0-9]+(?:-[a-z0-9]+)*)$")] private static partial Regex ScopeRx();

    public static string PathOf(string moduleDir) => Path.Combine(moduleDir, "recordings", FileName);

    /// <summary>
    /// Reads and checks the rule set: schema (NORM-INVALID) and a decision behind every rule (NORM-UNDECIDED). The hash of the file
    /// is what a verification is bound to, so any change to the rules unbacks it. An absent file is no rules.
    /// </summary>
    public static (NormSet Set, List<Finding> Findings) Load(string moduleDir, IReadOnlyCollection<Decision> decisions)
    {
        var path = PathOf(moduleDir);
        var f = new List<Finding>();
        if (!File.Exists(path)) return (new NormSet(), f);
        var file = Recording.Rel(path);
        var text = File.ReadAllText(path);
        int LineOf(string? id) => id == null ? 0 : text.Replace("\r\n", "\n").Split('\n').Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Contains($"\"{id}\"", StringComparison.Ordinal)).i + 1;
        void Bad(int line, string msg, string code = "NORM-INVALID") => f.Add(new Finding(file, line, code, "error", msg));
        JsonElement root;
        try { root = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false }).RootElement; }
        catch (JsonException ex) { Bad(0, $"is not JSON: {ex.Message}"); return (new NormSet(), f); }
        if (root.ValueKind != JsonValueKind.Object) { Bad(1, "must be a JSON object with 'version' and 'rules'"); return (new NormSet(), f); }
        foreach (var p in root.EnumerateObject().Where(p => p.Name is not ("version" or "rules"))) Bad(LineOf(p.Name), $"'{p.Name}' is not a key of {FileName}; it holds 'version' and 'rules'");
        var version = 0;
        if (root.TryGetProperty("version", out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 1) version = n;
            else Bad(1, "'version' is an optional label and must be a whole number from 1");
        }
        var rules = new List<NormRule>();
        if (!root.TryGetProperty("rules", out var items) || items.ValueKind != JsonValueKind.Array) { Bad(1, "needs 'rules': [...]"); return (new NormSet(), f); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byId = decisions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { Bad(0, "every rule must be an object"); continue; }
            string? Str(string key) => item.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            var id = Str("id");
            var line = LineOf(id);
            var who = id ?? "a rule";
            var ok = true;
            foreach (var p in item.EnumerateObject().Where(p => !RuleKeys.Contains(p.Name))) { Bad(line, $"{who}: '{p.Name}' is not a field of a rule (fields: {string.Join(", ", RuleKeys)})"); ok = false; }
            if (id == null || !IdRx().IsMatch(id)) { Bad(line, $"{who}: id must be norm:<lowercase-words>, got '{id}'"); ok = false; }
            else if (!seen.Add(id)) { Bad(line, $"{id} is listed twice"); ok = false; }
            var scope = Str("scope");
            if (scope == null || !ScopeRx().IsMatch(scope)) { Bad(line, $"{who}: scope must be global, call:<Category>/<Type> or scn:<id>, got '{scope}'"); ok = false; }
            var target = Str("target");
            if (target is not ("request" or "reply" or "dataset" or "any")) { Bad(line, $"{who}: target must be request, reply, dataset or any, got '{target}'"); ok = false; }
            var field = Str("field");
            if (string.IsNullOrWhiteSpace(field)) { Bad(line, $"{who}: needs 'field', a field path glob such as **/SessionId"); ok = false; }
            var match = Str("match");
            if (item.TryGetProperty("match", out var m) && m.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) { Bad(line, $"{who}: match must be a regular expression or null"); ok = false; }
            if (match != null)
                try { _ = new Regex(match); }
                catch (ArgumentException ex) { Bad(line, $"{who}: match '{match}' is not a regular expression: {ex.Message}"); ok = false; }
            var decision = Str("decision");
            if (decision == null) { Bad(line, $"{who} names no decision; a normalization rule hides a difference, so a person decides it: add a dec: entry to registry/decisions.json that cites {who} and says why the value may differ, then name it in 'decision'", "NORM-UNDECIDED"); ok = false; }
            else if (!byId.TryGetValue(decision, out var d)) { Bad(line, $"{who}: decision {decision} is not in registry/decisions.json", "NORM-UNDECIDED"); ok = false; }
            else if (id != null && !d.Cite.Contains(id)) { Bad(line, $"{who}: {decision} does not cite {id}; the decision must name the rule it allows", "NORM-UNDECIDED"); ok = false; }
            if (ok) rules.Add(new NormRule(id!, decision!, scope!, target!, field!, match));
        }
        var hash = Recording.Hash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        var set = new NormSet { Version = version, Hash = hash, Rules = rules };
        set.Compile();
        return (set, f);
    }
}
