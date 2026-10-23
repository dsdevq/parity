using System.Xml.Linq;

namespace Parity.Project;

public sealed record AssemblyRef(string Name, string? HintPath);

public sealed record LegacyProject(
    string Path,
    string Dir,
    string AssemblyName,
    string RootNamespace,
    List<string> CompileFiles,
    List<string> ResxFiles,
    List<string> Defines,
    string? LangVersion,
    string? OutputPath,
    List<AssemblyRef> References,
    List<string> ProjectReferences)
{
    /// <summary>OutputPath resolved against the project directory, when present.</summary>
    public string? OutputDir => OutputPath == null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, OutputPath.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
}

/// <summary>
/// Reads an old-style (non-SDK) csproj by hand: the file list is explicit, so no MSBuild is needed.
/// SDK-style projects without an explicit list fall back to a directory glob.
/// </summary>
public static class CsprojReader
{
    public static LegacyProject Read(string csprojPath)
    {
        var full = System.IO.Path.GetFullPath(csprojPath);
        var dir = System.IO.Path.GetDirectoryName(full) ?? ".";
        var doc = XDocument.Load(full);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        string? Prop(string name) => doc.Descendants(ns + name).Select(e => e.Value).FirstOrDefault();

        var compile = doc.Descendants(ns + "Compile")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Norm(dir, v!))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (compile.Count == 0)
        {
            // SDK-style: everything under the directory except bin/obj
            compile = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}")
                         && !p.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        var resx = doc.Descendants(ns + "EmbeddedResource")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v != null && v.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            .Select(v => Norm(dir, v!))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (resx.Count == 0)
            resx = Directory.EnumerateFiles(dir, "*.resx", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();

        var defines = (Prop("DefineConstants") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !d.StartsWith("$("))
            .ToList();

        var refs = doc.Descendants(ns + "Reference")
            .Select(e => new AssemblyRef((e.Attribute("Include")?.Value ?? "").Split(',')[0].Trim(),
                e.Element(ns + "HintPath")?.Value is { } hp ? Norm(dir, hp) : null))
            .Where(r => r.Name.Length > 0)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        var projRefs = doc.Descendants(ns + "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Norm(dir, v!))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new LegacyProject(
            full, dir,
            Prop("AssemblyName") ?? System.IO.Path.GetFileNameWithoutExtension(full),
            Prop("RootNamespace") ?? System.IO.Path.GetFileNameWithoutExtension(full),
            compile, resx, defines, Prop("LangVersion"), Prop("OutputPath"), refs, projRefs);
    }

    private static string Norm(string dir, string include) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, include.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
}
