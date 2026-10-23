using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Parity.Lint;

namespace Parity.Tools;

/// <summary>
/// <c>parity fixture scaffold</c>: a synthetic variant of a recorded reply, for an outcome the host cannot be made to
/// return. The copy is labelled (<c>recorded: synthetic</c>, <c>variant</c>, <c>source</c>) and starts unverified with an
/// empty note, so the gate stays red until the author has edited it, checked its shape and said what it stands for.
/// </summary>
public static partial class FixtureScaffold
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$")] private static partial Regex VariantRx();

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <returns>the path written: <c>&lt;from-stem&gt;.&lt;variant&gt;.json</c> beside <paramref name="from"/>, or in <paramref name="outDir"/>.</returns>
    public static string Run(string service, string from, string variant, string? outDir)
    {
        if (!VariantRx().IsMatch(variant)) throw new ArgumentException($"--variant must be an outcome name of letters, digits, - and _ (invalid, blank), got '{variant}'");
        if (!File.Exists(from)) throw new ArgumentException($"--from: {from} does not exist");
        var fromName = Path.GetFileName(from);
        FixtureFile src;
        try { src = Fixtures.Read(from); }
        catch (JsonException ex) { throw new InvalidOperationException($"{fromName} is not valid JSON: {ex.Message}"); }
        if (src.Synthetic) throw new InvalidOperationException($"{fromName} is itself synthetic; scaffold from a recorded reply, so the variant's shape comes from the host");
        if (src.Problems.Count > 0) throw new InvalidOperationException($"{fromName} {string.Join("; ", src.Problems)}; scaffold copies a valid recorded fixture");
        if (src.Service != service) throw new InvalidOperationException($"{fromName} records {src.Service}, not {service}");
        var target = Path.Combine(outDir ?? Path.GetDirectoryName(Path.GetFullPath(from))!, Path.GetFileNameWithoutExtension(from) + "." + variant + ".json");
        if (File.Exists(target)) throw new InvalidOperationException($"{target} exists; scaffold never overwrites a fixture");

        var recorded = JsonNode.Parse(File.ReadAllText(from))!.AsObject();
        var o = new JsonObject
        {
            ["service"] = service,
            ["recorded"] = Fixtures.Synthetic,
            ["variant"] = variant,
        };
        if (src.Scenario != null) o["scenario"] = src.Scenario;
        o["source"] = $"scaffolded from {fromName} by parity fixture scaffold";
        o["shapeVerified"] = false;
        o["note"] = "";
        foreach (var key in new[] { "request", "response" })
            if (recorded.TryGetPropertyValue(key, out var v)) o[key] = v?.DeepClone();
        if (outDir != null) Directory.CreateDirectory(outDir);
        File.WriteAllText(target, o.ToJsonString(Write) + "\n");
        return target;
    }
}
