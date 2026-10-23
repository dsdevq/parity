using System.Text.RegularExpressions;

namespace Parity.Project;

/// <summary>
/// A cheap textual index of the project: which file declares which type. Built with a regex scan,
/// never a parse, so it costs one read per file and a few bytes per type. It decides which files
/// enter the compilation, so memory scales with the module, not the project.
/// </summary>
public sealed partial class FileIndex
{
    [GeneratedRegex(@"\b(?:partial\s+)?(?:class|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled)]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex NsDecl();

    [GeneratedRegex(@"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s*<[^>{]*>)?\s*:\s*([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled)]
    private static partial Regex BaseDecl();

    [GeneratedRegex(@"\bnew\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\(|\btypeof\s*\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\)", RegexOptions.Compiled)]
    private static partial Regex Instantiation();

    private readonly Dictionary<string, List<string>> _filesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nsByFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _basesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _instantiatedIn = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, List<string>> FilesByType => _filesByType;

    public static FileIndex Build(IEnumerable<string> files)
    {
        var idx = new FileIndex();
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            var ns = NsDecl().Match(text);
            if (ns.Success) idx._nsByFile[f] = ns.Groups[1].Value;
            foreach (Match m in TypeDecl().Matches(text))
            {
                var name = m.Groups[1].Value;
                if (!idx._filesByType.TryGetValue(name, out var list)) idx._filesByType[name] = list = new List<string>();
                if (!list.Contains(f)) list.Add(f);
            }
            foreach (Match m in BaseDecl().Matches(text))
            {
                var b = m.Groups[2].Value;
                Add(idx._basesByType, m.Groups[1].Value, b[(b.LastIndexOf('.') + 1)..]);
            }
            foreach (Match m in Instantiation().Matches(text))
            {
                var t = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                Add(idx._instantiatedIn, t[(t.LastIndexOf('.') + 1)..], f);
            }
        }
        return idx;
    }

    private static void Add(Dictionary<string, HashSet<string>> d, string key, string value)
    {
        if (!d.TryGetValue(key, out var set)) d[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }

    /// <summary>
    /// This index with <paramref name="extra"/> behind it: a type the primary declares is never looked up in the extras,
    /// so a variant project that re-declares a type of the main project cannot pull a second definition into the compilation.
    /// </summary>
    public FileIndex With(IEnumerable<FileIndex> extra)
    {
        var idx = new FileIndex();
        foreach (var src in new[] { this }.Concat(extra))
        {
            foreach (var (t, files) in src._filesByType) idx._filesByType.TryAdd(t, files.ToList());
            foreach (var (f, ns) in src._nsByFile) idx._nsByFile.TryAdd(f, ns);
            foreach (var (t, bases) in src._basesByType) foreach (var b in bases) Add(idx._basesByType, t, b);
            foreach (var (t, files) in src._instantiatedIn) foreach (var f in files) Add(idx._instantiatedIn, t, f);
        }
        return idx;
    }

    /// <summary>Class names whose written base list starts with <paramref name="baseName"/> (last segment, no generics).</summary>
    public IEnumerable<string> SubclassesOf(string baseName) =>
        _basesByType.Where(kv => kv.Value.Contains(baseName)).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal);

    public IReadOnlyCollection<string> BasesOf(string typeName) =>
        _basesByType.TryGetValue(typeName, out var s) ? s : (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>Files that write 'new T(' or 'typeof(T)' for this type name.</summary>
    public IReadOnlyCollection<string> FilesInstantiating(string typeName) =>
        _instantiatedIn.TryGetValue(typeName, out var s) ? s : (IReadOnlyCollection<string>)Array.Empty<string>();

    public IReadOnlyList<string> FilesFor(string typeName) =>
        _filesByType.TryGetValue(typeName, out var l) ? l : Array.Empty<string>();

    public string? NamespaceOf(string file) => _nsByFile.TryGetValue(file, out var ns) ? ns : null;
}
