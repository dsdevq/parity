using System.Globalization;
using System.Text.Json;

namespace Parity.Lint;

/// <summary>
/// <c>fixtures/&lt;name&gt;.json</c>: one service reply. <c>recorded</c> is the date it was captured from the host, or
/// <c>synthetic</c> for a reply written by hand (or by <c>parity fixture scaffold</c>) because the host cannot be made to
/// return it. <see cref="Problems"/> lists what makes the file FIXTURE-INVALID; the rest of the record is still read.
/// </summary>
public sealed record FixtureFile(string Name, string? Service, string? Recorded, string? Variant, string? Scenario, string? Source, bool? ShapeVerified, string? Note, List<string> Problems)
{
    public bool Synthetic => Recorded == Fixtures.Synthetic;
}

/// <summary>What one lint run read of the fixtures its spec lists: printed on the summary line as <c>fixtures: N (M synthetic)</c>.</summary>
public sealed record FixtureTally(int Listed, int Synthetic)
{
    public override string ToString() => $"fixtures: {Listed} ({Synthetic} synthetic)";
}

public static class Fixtures
{
    public const string Synthetic = "synthetic";
    private static readonly string[] Strings = { "variant", "scenario", "source", "note" };

    /// <summary>Every <c>*.json</c> in the directory by name; a file that is not JSON goes to <paramref name="unreadable"/> instead.</summary>
    public static Dictionary<string, FixtureFile> Load(string dir, List<(string File, string Message)> unreadable)
    {
        var result = new Dictionary<string, FixtureFile>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { var fx = Read(path); result[fx.Name] = fx; }
            catch (JsonException ex) { unreadable.Add((Path.GetFileName(path), ex.Message)); }
        }
        return result;
    }

    /// <summary>Throws <see cref="JsonException"/> only when the file is not JSON; a wrong shape is a problem on the record.</summary>
    public static FixtureFile Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var name = Path.GetFileNameWithoutExtension(path);
        var problems = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) return new FixtureFile(name, null, null, null, null, null, null, null, new() { "is not a JSON object" });
        string? Str(string key)
        {
            if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            problems.Add($"'{key}' must be a string");
            return null;
        }

        var service = Str("service");
        if (service == null && !problems.Contains("'service' must be a string")) problems.Add("has no 'service' (the svc: id it records)");
        var recorded = Str("recorded");
        if (recorded != Synthetic && !DateOnly.TryParseExact(recorded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            problems.Add(recorded == null ? "has no 'recorded' (the YYYY-MM-DD it was captured, or synthetic)" : $"'recorded' is '{recorded}', neither a YYYY-MM-DD date nor synthetic");
        var s = Strings.ToDictionary(k => k, Str);
        bool? shapeVerified = null;
        if (root.TryGetProperty("shapeVerified", out var sv) && sv.ValueKind != JsonValueKind.Null)
        {
            if (sv.ValueKind is JsonValueKind.True or JsonValueKind.False) shapeVerified = sv.GetBoolean();
            else problems.Add("'shapeVerified' must be true or false");
        }
        return new FixtureFile(name, service, recorded, s["variant"], s["scenario"], s["source"], shapeVerified, s["note"], problems);
    }
}
