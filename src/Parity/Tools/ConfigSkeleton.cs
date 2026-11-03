using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Parity.Model;
using Parity.Project;

namespace Parity.Tools;

/// <summary>
/// legacy/extract.config.json for a repo that has none: the kit's template with the keys the legacy csproj determines filled in
/// (project, selfAssembly, referenceDirs, references and whether the embedded .NET references are used), the keys that are the
/// adopter's decision emptied (serviceBoundary, variants), and every other key, including ones a later template adds, left as the template has it.
/// </summary>
public static class ConfigSkeleton
{
    static readonly JsonSerializerOptions Out = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record Built(string Text, string? Project, List<string> Notes);

    /// <param name="configDir">Where the config will live; paths inside the repo are written relative to it, the rest absolute.</param>
    public static Built Build(string template, string repoDir, string configDir, string? projectArg)
    {
        var cfg = JsonNode.Parse(template, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        var notes = new List<string>();
        var defaults = new ExtractConfig();

        var csproj = projectArg != null ? Path.GetFullPath(Path.Combine(repoDir, projectArg)) : Detect(repoDir, notes);
        if (csproj != null && !File.Exists(csproj)) { notes.Add($"project not found: {csproj}"); csproj = null; }

        cfg["project"] = "";
        cfg["selfAssembly"] = null;
        cfg["referenceDirs"] = new JsonArray();
        cfg["referencesFromProject"] = true;
        cfg["serviceBoundary"] = new JsonArray();
        cfg["variants"] = new JsonArray();
        cfg["formBaseTypes"] = Strings(defaults.FormBaseTypes);
        cfg["ignoreCalls"] = Strings(defaults.IgnoreCalls);

        string? framework = null;
        if (csproj != null)
        {
            string Rel(string p) => IsUnder(repoDir, p) ? Path.GetRelativePath(configDir, p).Replace('\\', '/') : p.Replace('\\', '/');
            cfg["project"] = Rel(csproj);
            var proj = CsprojReader.Read(csproj);
            var (outputType, tfv, tf) = Props(csproj);
            framework = FrameworkFolder(tfv, tf);
            if (proj.OutputDir != null)
            {
                cfg["referenceDirs"] = Strings(new[] { Rel(proj.OutputDir.TrimEnd(Path.DirectorySeparatorChar, '/')) });
                var stem = Path.Combine(proj.OutputDir, proj.AssemblyName);
                var exts = outputType is "Exe" or "WinExe" ? new[] { ".exe", ".dll" } : new[] { ".dll", ".exe" };
                cfg["selfAssembly"] = Rel((exts.Select(e => stem + e).FirstOrDefault(File.Exists)) ?? stem + exts[0]);
            }
            else notes.Add("the csproj has no OutputPath: set referenceDirs and selfAssembly to where it is built");
        }

        cfg["includeRuntimeReferences"] = framework == null;
        var refs = new JsonArray();
        if (framework != null)
            foreach (var r in cfg["references"]?.AsArray() ?? new JsonArray())
                refs.Add(Regex.Replace(r!.GetValue<string>(), @"/v4\.\d+(?:\.\d+)?/", $"/{framework}/"));
        cfg["references"] = refs;
        if (cfg["selfAssembly"] == null) cfg.Remove("selfAssembly");

        notes.Add("serviceBoundary is empty: name the namespaces or proxy types that are the old API client, or no svc: id is produced");
        return new Built(cfg.ToJsonString(Out) + "\n", csproj, notes);
    }

    static JsonArray Strings(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    static bool IsUnder(string dir, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The only csproj in the repo outside build output, tool folders and legacy/ itself.</summary>
    static string? Detect(string repoDir, List<string> notes)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".claude", "legacy" };
        var found = new List<string>();
        void Walk(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.csproj")) found.Add(f);
            foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
                if (!skip.Contains(Path.GetFileName(d))) Walk(d);
        }
        Walk(repoDir);
        if (found.Count == 1) { notes.Add($"project detected: {Path.GetRelativePath(repoDir, found[0]).Replace('\\', '/')}"); return found[0]; }
        notes.Add(found.Count == 0
            ? "no csproj in this repo: pass --project <legacy csproj> or set project in the config"
            : $"{found.Count} csproj files in this repo, none picked: pass --project <legacy csproj> or set project in the config");
        return null;
    }

    public static (string? OutputType, string? TargetFrameworkVersion, string? TargetFramework) Props(string csproj)
    {
        var doc = XDocument.Load(csproj);
        string? P(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim();
        var tf = P("TargetFramework") ?? P("TargetFrameworks")?.Split(';')[0];
        return (P("OutputType"), P("TargetFrameworkVersion"), tf);
    }

    /// <summary>"v4.8" for a .NET Framework project (v4.8, net48, net472), null for anything newer: those use the embedded references.</summary>
    public static string? FrameworkFolder(string? targetFrameworkVersion, string? targetFramework)
    {
        if (targetFrameworkVersion != null && Regex.IsMatch(targetFrameworkVersion, @"^v[2-4]\.\d+(\.\d+)?$")) return targetFrameworkVersion;
        if (targetFramework != null && Regex.Match(targetFramework, @"^net(4)(\d)(\d)?$") is { Success: true } m)
            return $"v4.{m.Groups[2].Value}" + (m.Groups[3].Success ? $".{m.Groups[3].Value}" : "");
        return null;
    }
}
