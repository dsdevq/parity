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
    /// <summary>Tenant or customer variants of the module's forms (issue #20). Each one's subclasses become a delta on their base form.</summary>
    public List<VariantConfig> Variants { get; set; } = new();

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

/// <summary>
/// One variant. Project is the variant's csproj, relative to this config (omit it when the variant lives in the main project).
/// Subclasses limits the variant to these class names (required without Project). Factories names extra types or
/// Type.Method factories to read when the factory does not mention two implementations of the same form.
/// </summary>
public sealed class VariantConfig
{
    public string Id { get; set; } = "";
    public string? Project { get; set; }
    public string? SelfAssembly { get; set; }
    public List<string> Subclasses { get; set; } = new();
    public List<string> Factories { get; set; } = new();
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
