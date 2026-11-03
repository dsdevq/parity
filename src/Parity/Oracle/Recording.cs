using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Model;

namespace Parity.Oracle;

/// <summary>One call of a recording or a replay: its place, its wire name, whether it writes, and its files (relative to the folder).</summary>
public sealed record RecordedCall(int Seq, string Category, string Type, bool Write, string Request, string? Reply, string? Dataset, string? Error, double? DurationMs)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Key => $"{Category}/{Type}";
}

/// <summary>Where a recording came from. Never the session id or the user of the log: those are host data.</summary>
public sealed record Provenance(string Recorded, string? Environment, string? LegacyBuild, string? RecordedBy, List<string> Logs, int Rows, string Selection, string Tool);

/// <summary>
/// <c>recording.json</c>: the ordered index of one scenario's calls, its provenance, and a seal over every file of the folder. Kind is
/// <c>recording</c> for the legacy client (under <c>recordings/</c>) and <c>replay</c> for the new client (under <c>replays/</c>); the
/// format is the same, so both sides go through the same export and the same redaction.
/// </summary>
public sealed record RecordingFile(int Schema, string Kind, string Scenario, int ScenarioVersion, Provenance Provenance, int Redacted,
    List<RecordedCall> Calls, SortedDictionary<string, string> Files, string Seal);

public sealed record ExportRequest(string ModuleDir, string Scenario, string Kind, List<string> Logs, List<string> Sessions, DateTimeOffset? From, DateTimeOffset? To,
    string? RecordedBy, string? Environment, string? LegacyBuild, bool Replace, OracleConfig Config, string? RedactKey, DateOnly Today);

public static partial class Recording
{
    public const int Schema = 1;
    public const string IndexFile = "recording.json";
    public const string Tool = "parity oracle export";

    [GeneratedRegex(@"^scn:[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex ScnRx();

    /// <summary>The folder name of a scenario: its id without <c>scn:</c>.</summary>
    public static string Slug(string scenario)
    {
        if (!ScnRx().IsMatch(scenario)) throw new ArgumentException($"'{scenario}' is not a scenario id (scn:<lowercase-words>)");
        return scenario[4..];
    }

    public static string RecordingDir(string moduleDir, string scenario) => Path.Combine(moduleDir, "recordings", Slug(scenario));
    public static string ReplayDir(string moduleDir, string scenario) => Path.Combine(moduleDir, "replays", Slug(scenario));
    public static string VerifiedFile(string moduleDir, string scenario) => Path.Combine(moduleDir, "verified", Slug(scenario) + ".json");

    public static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    /// <summary>The hash of a recorded text file with CRLF read as LF, so a checkout that converts line endings does not look edited.</summary>
    public static string TextHash(byte[] bytes) => Hash(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")));

    /// <summary>The seal: one hash over the index with the seal blanked, which carries each file's hash.</summary>
    public static string SealOf(RecordingFile r) =>
        Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r with { Seal = "", Files = new SortedDictionary<string, string>(r.Files, StringComparer.Ordinal) }, Json.Options).Replace("\r\n", "\n")));

    public static RecordingFile Load(string dir)
    {
        var path = Path.Combine(dir, IndexFile);
        if (!File.Exists(path)) throw new InvalidOperationException($"no {Rel(path)}");
        try { return JsonSerializer.Deserialize<RecordingFile>(File.ReadAllText(path), Json.Options) ?? throw new InvalidOperationException($"{Rel(path)} is empty"); }
        catch (JsonException ex) { throw new InvalidOperationException($"{Rel(path)} is not a recording index: {ex.Message}"); }
    }

    /// <summary>What no longer matches the seal: an edited, missing or added file, or an edited index. Empty when intact.</summary>
    public static List<string> Tampered(string dir, RecordingFile r)
    {
        var problems = new List<string>();
        if (SealOf(r) != r.Seal) problems.Add($"{IndexFile} was edited after export");
        var listed = new HashSet<string>(r.Files.Keys, StringComparer.Ordinal);
        foreach (var (rel, hash) in r.Files)
        {
            var f = Path.Combine(dir, rel);
            if (!File.Exists(f)) problems.Add($"{rel} is missing");
            else if (TextHash(File.ReadAllBytes(f)) != hash) problems.Add($"{rel} was edited after export");
        }
        var calls = Path.Combine(dir, "calls");
        if (Directory.Exists(calls))
            foreach (var f in Directory.EnumerateFiles(calls).Select(f => "calls/" + Path.GetFileName(f)).Where(f => !listed.Contains(f)).OrderBy(f => f, StringComparer.Ordinal))
                problems.Add($"{f} was added after export");
        foreach (var c in r.Calls)
            foreach (var rel in new[] { c.Request, c.Reply, c.Dataset }.OfType<string>().Where(x => !listed.Contains(x)))
                problems.Add($"call #{c.Seq} names {rel}, which the seal does not cover");
        return problems;
    }

    public static string Read(string dir, string? rel) => rel == null ? "" : File.ReadAllText(Path.Combine(dir, rel)).Replace("\r\n", "\n");

    /// <summary>
    /// <c>parity oracle export</c> (kind recording) and the capture import of <c>diff</c> and <c>verify</c> (kind replay): selects the
    /// rows, redacts them, and writes the folder with its index and seal. A recording is selective (a session or a time window is
    /// required) and never overwritten without <c>Replace</c>; a replay always replaces the last one.
    /// </summary>
    public static (string Dir, RecordingFile File) Export(ExportRequest q)
    {
        var replay = q.Kind == "replay";
        var dir = replay ? ReplayDir(q.ModuleDir, q.Scenario) : RecordingDir(q.ModuleDir, q.Scenario);
        if (!replay && q.Sessions.Count == 0 && q.From == null && q.To == null)
            throw new InvalidOperationException("export is selective: name the session (--session) or the time window (--from, --to) of the one scenario run");
        if (q.Logs.Count == 0) throw new InvalidOperationException("no host log given (--log)");
        var version = OracleModule.ScenarioVersion(q.ModuleDir, q.Scenario);
        if (!replay && string.IsNullOrWhiteSpace(q.RecordedBy)) throw new InvalidOperationException("a recording needs --recorded-by: who ran the scenario in the legacy client");
        if (!replay && Directory.Exists(dir) && !q.Replace)
            throw new InvalidOperationException($"{Rel(dir)} exists; a recording is replaced only on purpose (--replace)");

        var rows = HostLog.ReadAll(q.Logs)
            .Where(r => q.Sessions.Count == 0 || (r.Session != null && q.Sessions.Contains(r.Session)))
            .Where(r => q.From == null || (r.Time != null && r.Time >= q.From))
            .Where(r => q.To == null || (r.Time != null && r.Time <= q.To))
            .ToList();
        if (rows.Count == 0) throw new InvalidOperationException("no log row matches the selection; check --session, --from and --to against the dump");

        var redactor = new Redactor(q.Config.Redact, q.RedactKey);
        var tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(Path.Combine(tmp, "calls"));
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string? Write(int seq, string part, string? text)
            {
                if (text == null) return null;
                var rel = $"calls/{seq:000}.{part}.{Payload.Format(text)}";
                var bytes = Encoding.UTF8.GetBytes(text);
                File.WriteAllBytes(Path.Combine(tmp, rel), bytes);
                files[rel] = TextHash(bytes);
                return rel;
            }
            var calls = new List<RecordedCall>();
            foreach (var (row, i) in rows.Select((r, i) => (r, i + 1)))
            {
                var key = $"{row.Category}/{row.Type}";
                var request = redactor.Text("request", row.Request);
                var reply = row.Reply == null ? null : redactor.Text("reply", row.Reply);
                string? dataset;
                try { dataset = row.Dataset == null ? null : redactor.DatasetText(row.Dataset); _ = Payload.Tables(dataset); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"row {i} of the selection ({key}): {ex.Message}"); }
                var error = row.Error == null ? null : redactor.Text("error", row.Error);
                calls.Add(new RecordedCall(i, row.Category, row.Type, q.Config.Writes.Any(w => Payload.CallMatch(w, key)),
                    Write(i, "request", request)!, Write(i, "reply", reply), Write(i, "dataset", dataset), error, row.DurationMs));
            }
            var sessions = rows.Select(r => r.Session).Distinct().Count();
            var selection = replay ? $"every row of the capture ({sessions} session(s))"
                : string.Join(", ", new[]
                {
                    q.Sessions.Count > 0 ? $"{q.Sessions.Count} session(s)" : null,
                    q.From != null ? $"from {q.From:O}" : null,
                    q.To != null ? $"to {q.To:O}" : null,
                }.OfType<string>());
            var prov = new Provenance(q.Today.ToString("yyyy-MM-dd"), q.Environment, q.LegacyBuild, q.RecordedBy, q.Logs.Select(Path.GetFileName).OfType<string>().ToList(),
                rows.Count, selection, Tool);
            var rec = new RecordingFile(Schema, replay ? "replay" : "recording", q.Scenario, version, prov, redactor.Count, calls, files, "");
            rec = rec with { Seal = SealOf(rec) };
            File.WriteAllText(Path.Combine(tmp, IndexFile), JsonSerializer.Serialize(rec, Json.Options).Replace("\r\n", "\n") + "\n");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            Directory.Move(tmp, dir);
            return (dir, rec);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    public static string Rel(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path).Replace('\\', '/');
}

/// <summary>Applies the configured redaction rules; a value is replaced by <c>redacted:&lt;8 hex&gt;</c>, keyed so equal values stay equal.</summary>
public sealed class Redactor
{
    private readonly List<(RedactRule Rule, Regex? Match)> _rules;
    private readonly byte[]? _key;
    public int Count { get; private set; }

    public Redactor(List<RedactRule> rules, string? key)
    {
        _rules = new();
        foreach (var r in rules)
        {
            if (r.Target is not ("request" or "reply" or "dataset" or "error" or "any"))
                throw new InvalidOperationException($"oracle.redact: target '{r.Target}' is not request, reply, dataset, error or any");
            if (string.IsNullOrWhiteSpace(r.Field)) throw new InvalidOperationException("oracle.redact: a rule needs a field glob");
            Regex? rx;
            try { rx = r.Match == null ? null : new Regex(r.Match, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { throw new InvalidOperationException($"oracle.redact: '{r.Match}' is not a regular expression: {ex.Message}"); }
            _rules.Add((r, rx));
        }
        if (_rules.Count > 0 && string.IsNullOrEmpty(key))
            throw new InvalidOperationException("oracle.redact has rules but PARITY_REDACT_KEY is not set; the key keeps pseudonyms equal across recording and replay "
                + "without making them reversible. Set it to the same private value wherever recordings and replays are exported, and keep it out of the repo");
        _key = string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    private string Pseudonym(string value) =>
        "redacted:" + Convert.ToHexString(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    private string Apply(string target, string path, string value)
    {
        foreach (var (r, rx) in _rules)
        {
            if (r.Target != "any" && r.Target != target) continue;
            if (!Payload.GlobMatch(r.Field, path)) continue;
            if (value.StartsWith("redacted:", StringComparison.Ordinal) || value.Length == 0) continue;
            var nv = rx == null ? Pseudonym(value) : rx.Replace(value, m => Pseudonym(m.Value));
            if (nv == value) continue;
            Count++;
            value = nv;
        }
        return value;
    }

    public string Text(string target, string text) => _rules.Count == 0 ? text : Payload.Rewrite(text, (p, v) => Apply(target, p, v), out _);

    public string DatasetText(string text) => _rules.Count == 0 ? text : Payload.RewriteDataset(text, (p, v) => Apply("dataset", p, v), out _);
}
