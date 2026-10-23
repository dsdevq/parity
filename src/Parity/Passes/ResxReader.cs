using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Parity.Project;

namespace Parity.Passes;

/// <summary>A string entry of a resx file and the line of its data element.</summary>
public sealed record ResxEntry(string Value, int Line);

public static class ResxReader
{
    public static Dictionary<string, string> Read(string path) => ReadEntries(path).ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

    public static Dictionary<string, ResxEntry> ReadEntries(string path)
    {
        var dict = new Dictionary<string, ResxEntry>(StringComparer.Ordinal);
        XDocument doc;
        try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); } catch { return dict; }
        foreach (var d in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
        {
            var name = d.Attribute("name")?.Value;
            var value = d.Element("value")?.Value;
            if (name != null && value != null && d.Attribute("type") == null && d.Attribute("mimetype") == null)
                dict[name] = new ResxEntry(value, ((IXmlLineInfo)d).LineNumber);
        }
        return dict;
    }
}

/// <summary>
/// One resource: the default-culture resx and its culture variants (X.resx, X.fr.resx, X.de-CH.resx in the same folder).
/// <see cref="Name"/> is the manifest resource name the generated class and ResourceManager use: RootNamespace + folder + file.
/// </summary>
public sealed class ResxSet
{
    public required string Name;
    public required string DefaultPath;
    public required SortedDictionary<string, string> CulturePaths;
    private Dictionary<string, ResxEntry>? _default;
    private readonly Dictionary<string, Dictionary<string, ResxEntry>> _cultures = new(StringComparer.Ordinal);

    public Dictionary<string, ResxEntry> Default => _default ??= ResxReader.ReadEntries(DefaultPath);

    /// <summary>The cultures other than the default whose resx defines <paramref name="key"/>.</summary>
    public List<string> CulturesDefining(string key) => CulturePaths
        .Where(c => (_cultures.TryGetValue(c.Key, out var d) ? d : _cultures[c.Key] = ResxReader.ReadEntries(c.Value)).ContainsKey(key))
        .Select(c => c.Key).ToList();
}

/// <summary>
/// The resx files of the targeted project only (never the solution's), grouped by resource and read on first use.
/// </summary>
public sealed class ResxIndex
{
    private static readonly Regex CultureSuffix = new(@"^(.+)\.([a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, ResxSet> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ResxSet>> _byFile = new(StringComparer.Ordinal);

    public ResxIndex(LegacyProject project)
    {
        var files = project.ResxFiles.ToHashSet(StringComparer.Ordinal);
        var root = string.IsNullOrEmpty(project.RootNamespace) ? project.AssemblyName : project.RootNamespace;
        foreach (var f in project.ResxFiles.Where(f => !IsCultureVariant(f, files)))
        {
            var rel = Path.GetRelativePath(project.Dir, Path.ChangeExtension(f, null)).Replace('\\', '/');
            var name = rel.StartsWith("../", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(f) : root + "." + rel.Replace('/', '.');
            var cultures = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in project.ResxFiles)
                if (CultureSuffix.Match(Path.GetFileNameWithoutExtension(c)) is { Success: true } m
                    && Path.Combine(Path.GetDirectoryName(c) ?? "", m.Groups[1].Value + ".resx") == f)
                    cultures[m.Groups[2].Value] = c;
            var set = new ResxSet { Name = name, DefaultPath = f, CulturePaths = cultures };
            _byName.TryAdd(name, set);
            var file = Path.GetFileNameWithoutExtension(f);
            if (!_byFile.TryGetValue(file, out var l)) _byFile[file] = l = new List<ResxSet>();
            l.Add(set);
        }
    }

    private static bool IsCultureVariant(string f, HashSet<string> files) =>
        CultureSuffix.Match(Path.GetFileNameWithoutExtension(f)) is { Success: true } m && files.Contains(Path.Combine(Path.GetDirectoryName(f) ?? "", m.Groups[1].Value + ".resx"));

    /// <summary>
    /// The resource named <paramref name="name"/> (a manifest resource name such as a generated class's full name);
    /// failing that, the only resx whose file name is <paramref name="simpleName"/>.
    /// </summary>
    public ResxSet? Find(string name, string? simpleName)
    {
        if (_byName.TryGetValue(name, out var s)) return s;
        simpleName ??= name[(name.LastIndexOf('.') + 1)..];
        return _byFile.TryGetValue(simpleName, out var l) && l.Count == 1 ? l[0] : null;
    }
}
