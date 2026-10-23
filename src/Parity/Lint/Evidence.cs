using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Model;
using Parity.Passes;

namespace Parity.Lint;

/// <summary>
/// One fact from the legacy source that the manifest lacks: the quoted lines, their hash, and the source
/// revision they were read at. A spec cites it as <c>ev:Unit/n</c>; lint re-reads the lines and compares.
/// </summary>
public sealed record EvidenceRecord(string Id, string File, int Start, int End, string QuoteHash, string Revision, string Created, string? Note, List<string> Quote);

/// <summary><c>legacy/&lt;Module&gt;/evidence/&lt;Unit&gt;.json</c>. <c>root</c> is the legacy root, relative to this file's directory or absolute.</summary>
public sealed record EvidenceFile(string SchemaVersion, string Unit, string Root, List<EvidenceRecord> Records);

public static partial class Evidence
{
    public const string SchemaVersion = "1";

    [GeneratedRegex(@"^ev:([A-Za-z_]\w*)/([1-9]\d*)$")] internal static partial Regex IdRx();
    [GeneratedRegex(@"^(.+):(\d+)-(\d+)$")] private static partial Regex LocRx();

    public static string PathOf(string moduleDir, string unit) => Path.Combine(moduleDir, "evidence", unit + ".json");

    /// <summary>Like <see cref="ManifestHash"/>: sha256 of the quoted lines joined by \n, first 16 hex. Line endings never count.</summary>
    public static string QuoteHash(IEnumerable<string> lines) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16].ToLowerInvariant();

    public static EvidenceFile Load(string path)
    {
        var ef = JsonSerializer.Deserialize<EvidenceFile>(File.ReadAllText(path), Json.Options) ?? throw new JsonException("empty file");
        if (string.IsNullOrEmpty(ef.Root)) throw new JsonException("no 'root' (the legacy root the records are relative to)");
        if (ef.Records == null) throw new JsonException("no 'records'");
        foreach (var r in ef.Records)
            if (r.Id == null || r.File == null || r.QuoteHash == null || r.Start < 1 || r.End < r.Start || r.Quote == null || r.Quote.Count != r.End - r.Start + 1)
                throw new JsonException($"record '{r.Id}' needs id, file, start <= end, quoteHash and one quote line per line in the range");
        return ef;
    }

    /// <summary>
    /// <c>parity evidence add</c>: refuses in the specify and implement phases (<c>.phase</c> next to the config), finds the
    /// module holding the unit's manifest, and records the lines against the legacy root (the directory of <c>project</c>).
    /// </summary>
    public static EvidenceRecord AddFromConfig(ExtractConfig cfg, string? module, string unit, string loc, string? note)
    {
        var phaseFile = Path.Combine(cfg.BaseDir, ".phase");
        var phase = File.Exists(phaseFile) ? File.ReadAllText(phaseFile).Trim() : "";
        if (phase is "specify" or "implement")
            throw new InvalidOperationException($"phase '{phase}' may read evidence but not the source; record evidence in the extract phase");
        var m = LocRx().Match(loc);
        if (!m.Success) throw new ArgumentException($"expected <file>:<start>-<end>, got '{loc}'");
        var outDir = cfg.Resolve(cfg.Output);
        if (module == null)
        {
            var found = Directory.Exists(outDir)
                ? Directory.EnumerateDirectories(outDir).Where(d => File.Exists(Path.Combine(d, "manifests", unit + ".json"))).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList()
                : new List<string?>();
            module = found.Count switch
            {
                1 => found[0]!,
                0 => throw new InvalidOperationException($"no module under {outDir} has manifests/{unit}.json; run parity extract first or pass --module"),
                _ => throw new InvalidOperationException($"{unit} is in modules {string.Join(", ", found)}; pass --module"),
            };
        }
        var root = Path.GetDirectoryName(cfg.Resolve(cfg.Project)) ?? ".";
        return Add(Path.Combine(outDir, module), unit, root, m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), note);
    }

    /// <summary>Appends <c>ev:Unit/n</c> (n one past the highest recorded) to the unit's evidence file. Ids are never reused.</summary>
    public static EvidenceRecord Add(string moduleDir, string unit, string legacyRoot, string file, int start, int end, string? note)
    {
        var manifest = Path.Combine(moduleDir, "manifests", unit + ".json");
        if (!File.Exists(manifest)) throw new InvalidOperationException($"no manifest at {manifest}; evidence belongs to an extracted unit");
        legacyRoot = Path.GetFullPath(legacyRoot);
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(legacyRoot, file));
        if (!File.Exists(full)) throw new InvalidOperationException($"{file} not found under the legacy root {legacyRoot}");
        var lines = File.ReadAllLines(full);
        if (start < 1 || end < start || end > lines.Length) throw new InvalidOperationException($"{file} has {lines.Length} line(s); {start}-{end} is not a range in it");

        var path = PathOf(moduleDir, unit);
        var dir = Path.GetDirectoryName(path)!;
        var records = File.Exists(path) ? Load(path).Records : new List<EvidenceRecord>();
        var n = records.Select(r => IdRx().Match(r.Id)).Where(x => x.Success).Select(x => int.Parse(x.Groups[2].Value)).DefaultIfEmpty(0).Max() + 1;
        var rel = Path.GetRelativePath(legacyRoot, full).Replace('\\', '/');
        var quote = lines[(start - 1)..end].ToList();
        var rec = new EvidenceRecord($"ev:{unit}/{n}", rel, start, end, QuoteHash(quote), Revision(legacyRoot, rel, manifest),
            DateTime.UtcNow.ToString("yyyy-MM-dd"), string.IsNullOrWhiteSpace(note) ? null : note, quote);
        records.Add(rec);
        var ef = new EvidenceFile(SchemaVersion, unit, Path.GetRelativePath(dir, legacyRoot).Replace('\\', '/'), records);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(ef, Json.Options) + "\n");
        return rec;
    }

    /// <summary><c>git:&lt;HEAD&gt;</c> (<c>+dirty</c> when the quoted file differs from HEAD) when the legacy root is in a git work tree, else <c>manifest:&lt;hash&gt;</c>.</summary>
    private static string Revision(string root, string relFile, string manifest)
    {
        var head = Git(root, "rev-parse", "HEAD");
        if (head == null) return "manifest:" + ManifestHash.Of(manifest);
        return "git:" + head + (string.IsNullOrEmpty(Git(root, "status", "--porcelain", "--", relFile)) ? "" : "+dirty");
    }

    private static string? Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(); err.Wait();
            return p.ExitCode == 0 ? o : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // no git on PATH
    }
}

/// <summary>The evidence files of one module, loaded on demand. Lint resolves each cited <c>ev:</c> id here against the legacy source.</summary>
public sealed class EvidenceSet
{
    private readonly string _moduleDir;
    private readonly Dictionary<string, (EvidenceFile? File, string? Error)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Code, string Message)?> _results = new(StringComparer.Ordinal);

    public EvidenceSet(string moduleDir) => _moduleDir = moduleDir;

    /// <summary>Null when the record exists and its lines still read the same; otherwise the lint code and the reason.</summary>
    public (string Code, string Message)? Check(string id)
    {
        if (!_results.TryGetValue(id, out var r)) _results[id] = r = Resolve(id);
        return r;
    }

    private (string Code, string Message)? Resolve(string id)
    {
        var m = Evidence.IdRx().Match(id);
        if (!m.Success) return ("CITE-UNKNOWN", "is not an ev:<Unit>/<n> id");
        var unit = m.Groups[1].Value;
        var path = Evidence.PathOf(_moduleDir, unit);
        if (!_files.TryGetValue(unit, out var ef))
        {
            try { ef = File.Exists(path) ? (Evidence.Load(path), null) : (null, null); }
            catch (JsonException ex) { ef = (null, ex.Message); }
            _files[unit] = ef;
        }
        if (ef.Error != null) return ("EV-INVALID", $"cannot be read: evidence/{unit}.json: {ef.Error}");
        var rec = ef.File?.Records.FirstOrDefault(x => x.Id == id);
        if (rec == null) return ("CITE-UNKNOWN", $"is not recorded in evidence/{unit}.json");
        if (Evidence.QuoteHash(rec.Quote) != rec.QuoteHash) return ("EV-INVALID", $"has a quote that does not hash to its quoteHash {rec.QuoteHash}; records are written by parity evidence add, not by hand");

        // the guard hook's override first, so a moved checkout can be pointed at without rewriting the files
        var root = Environment.GetEnvironmentVariable("PARITY_LEGACY_ROOT") is { Length: > 0 } env ? env : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ef.File!.Root));
        var full = Path.GetFullPath(Path.Combine(root, rec.File));
        var at = $"{rec.File}:{rec.Start}-{rec.End}";
        if (!File.Exists(full)) return ("EV-STALE", $"no longer matches the source: {rec.File} does not exist under the legacy root {root} (recorded at {rec.Revision})");
        var lines = File.ReadAllLines(full);
        if (rec.End > lines.Length) return ("EV-STALE", $"no longer matches the source: {at} is past the end of {rec.File}, which now has {lines.Length} line(s) (recorded at {rec.Revision}){MovedTo(rec, lines)}");
        var now = Evidence.QuoteHash(lines[(rec.Start - 1)..rec.End]);
        if (now != rec.QuoteHash) return ("EV-STALE", $"no longer matches the source: {at} changed since {rec.Revision} (quote {rec.QuoteHash}, now {now}){MovedTo(rec, lines)}");
        return null;
    }

    /// <summary>Where the quoted lines sit now, when they survived intact somewhere else in the file.</summary>
    private static string MovedTo(EvidenceRecord rec, string[] lines)
    {
        var len = rec.End - rec.Start + 1;
        for (var s = 0; s + len <= lines.Length; s++)
            if (lines[s] == rec.Quote[0] && Evidence.QuoteHash(lines[s..(s + len)]) == rec.QuoteHash)
                return $"; the quoted lines are now at {rec.File}:{s + 1}-{s + len}";
        return "; the quoted lines are nowhere in the file";
    }
}
