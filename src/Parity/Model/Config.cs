using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Model;

/// <summary>extract.config.json - the only place project-specific knowledge lives.</summary>
public sealed class ExtractConfig
{
    public string Project { get; set; } = "";
    public List<string> References { get; set; } = new();
    /// <summary>Directories searched for the DLLs named by the csproj's Reference/ProjectReference items.</summary>
    public List<string> ReferenceDirs { get; set; } = new();
    /// <summary>Derive references from the csproj items instead of (or in addition to) globs. Never point globs at a solution-wide bin.</summary>
    public bool ReferencesFromProject { get; set; }
    public bool IncludeRuntimeReferences { get; set; } = true;
    public string? SelfAssembly { get; set; }
    public List<string> FormBaseTypes { get; set; } = new()
    {
        "System.Windows.Forms.Form", "System.Windows.Forms.UserControl",
        "DevExpress.XtraEditors.XtraForm", "DevExpress.XtraEditors.XtraUserControl",
    };
    public List<string> ServiceBoundary { get; set; } = new();
    public string ControlCategories { get; set; } = "control-categories.json";
    public int MaxCallDepth { get; set; } = 2;
    public string Output { get; set; } = "legacy";
    public List<string> IgnoreCalls { get; set; } = new() { "System.*", "DevExpress.*" };

    [JsonIgnore] public string BaseDir { get; set; } = ".";

    public static ExtractConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<ExtractConfig>(json, Json.Options)
                  ?? throw new InvalidOperationException("empty config");
        cfg.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return cfg;
    }

    public string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(BaseDir, p));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
