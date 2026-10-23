using System.Text.Json;
using Parity.Model;

namespace Parity.Tools;

/// <summary>One mapping of kit files into the consuming repo. <c>From</c> is a kit file or folder (adopt/..., templates/...), <c>To</c> a repo-relative path.</summary>
public sealed class KitRule
{
    public string Name { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class KitPhase
{
    public string Name { get; set; } = "";
    public string Skill { get; set; } = "";
    /// <summary>The headless agent's prompt; {module} and {form} are filled in by the driver.</summary>
    public string? Prompt { get; set; }
    /// <summary>Why the driver stops after this phase when more were asked for.</summary>
    public string? Pause { get; set; }
    public bool Driven { get; set; } = true;
}

/// <summary>adopt/kit.json: the whole contract between the kit's contents and the code that installs and checks them.</summary>
public sealed class KitManifest
{
    public List<KitRule> Install { get; set; } = new();
    public List<KitRule> Create { get; set; } = new();
    public KitRule Config { get; set; } = new();
    public KitRule Settings { get; set; } = new();
    public string PhaseFile { get; set; } = "legacy/.phase";
    public List<KitPhase> Phases { get; set; } = new();
}

/// <summary>
/// The adopt kit and the templates, as embedded in the binary (a release is one file; a folder beside it would be lost
/// when the file is copied onto PATH, and a kit older or newer than the tool would write config keys the tool does not read).
/// Keys are kit paths with forward slashes: <c>adopt/hooks/guard-legacy.sh</c>, <c>templates/spec.md</c>.
/// </summary>
public sealed class Kit
{
    const string Prefix = "kit/";
    public const string ManifestPath = "adopt/kit.json";

    public IReadOnlyDictionary<string, byte[]> Files { get; }
    public KitManifest Manifest { get; }

    Kit(IReadOnlyDictionary<string, byte[]> files)
    {
        Files = files;
        if (!files.TryGetValue(ManifestPath, out var raw)) throw new InvalidOperationException($"the kit has no {ManifestPath}");
        Manifest = JsonSerializer.Deserialize<KitManifest>(raw, Json.Options) ?? throw new InvalidOperationException($"{ManifestPath} is empty");
    }

    public static Kit Load()
    {
        var asm = typeof(Kit).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            files[name[Prefix.Length..].Replace('\\', '/')] = ms.ToArray();
        }
        return new Kit(files);
    }

    /// <summary>The kit files a rule installs: <c>From</c> itself when it is a file, else everything under it. Target is the repo-relative destination.</summary>
    public IEnumerable<(string Source, string Target)> Expand(KitRule rule)
    {
        if (Files.ContainsKey(rule.From)) { yield return (rule.From, rule.To); yield break; }
        var dir = rule.From.TrimEnd('/') + "/";
        foreach (var f in Files.Keys.Where(k => k.StartsWith(dir, StringComparison.Ordinal)))
            yield return (f, rule.To.TrimEnd('/') + "/" + f[dir.Length..]);
    }

    public string Text(string source) => System.Text.Encoding.UTF8.GetString(Files[source]);
}
