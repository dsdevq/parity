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

    private readonly Dictionary<string, List<string>> _filesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nsByFile = new(StringComparer.Ordinal);

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
        }
        return idx;
    }

    public IReadOnlyList<string> FilesFor(string typeName) =>
        _filesByType.TryGetValue(typeName, out var l) ? l : Array.Empty<string>();

    public string? NamespaceOf(string file) => _nsByFile.TryGetValue(file, out var ns) ? ns : null;
}
