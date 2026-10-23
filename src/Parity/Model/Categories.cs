using System.Text.Json;

namespace Parity.Model;

/// <summary>Control type to category. Exact name first, then simple name, then the base-type chain.</summary>
public sealed class ControlCategories
{
    private readonly Dictionary<string, string> _byName;

    public ControlCategories(Dictionary<string, string> byName) => _byName = byName;

    public static ControlCategories Load(string path)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json.Options)
                   ?? new Dictionary<string, string>();
        return new ControlCategories(new Dictionary<string, string>(dict, StringComparer.Ordinal));
    }

    public string Classify(IEnumerable<string> typeChain)
    {
        foreach (var t in typeChain)
        {
            if (_byName.TryGetValue(t, out var c)) return c;
            var simple = t[(t.LastIndexOf('.') + 1)..];
            if (_byName.TryGetValue(simple, out c)) return c;
        }
        return "unknown";
    }
}
