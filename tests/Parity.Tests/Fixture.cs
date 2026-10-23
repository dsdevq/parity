using System.Text.Json;
using Parity.Model;
using Parity.Passes;
using Xunit;

namespace Parity.Tests;

/// <summary>Runs extract once over the synthetic project into a temp dir; every test reads from there.</summary>
public sealed class ExtractFixture : IDisposable
{
    public string RepoRoot { get; }
    public string OutRoot { get; }
    public string ModuleDir => Path.Combine(OutRoot, "legacy", "PolicyWizard");
    public List<ExtractSummary> Summaries { get; }
    public string Log { get; }

    public ExtractFixture()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Parity.sln"))) dir = Path.GetDirectoryName(dir);
        RepoRoot = dir ?? throw new InvalidOperationException("Parity.sln not found above " + AppContext.BaseDirectory);
        OutRoot = Path.Combine(Path.GetTempPath(), "parity-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(OutRoot);

        var cfg = ExtractConfig.Load(Path.Combine(RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(OutRoot, "legacy");
        var log = new StringWriter();
        Summaries = Extractor.Run(cfg, "PolicyWizard", "*", log);
        Log = log.ToString();
    }

    public FormManifest Manifest(string form) =>
        JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(ModuleDir, "manifests", form + ".json")), Json.Options)!;

    public ModuleIndex Module() =>
        JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(ModuleDir, "module.json")), Json.Options)!;

    /// <summary>Copies the example spec and fixtures next to the generated manifests, with the live hash patched in.</summary>
    public string InstallExampleSpec(string form, Func<string, string>? mutate = null)
    {
        var specDir = Path.Combine(ModuleDir, "spec");
        Directory.CreateDirectory(specDir);
        var fxDir = Path.Combine(ModuleDir, "fixtures");
        Directory.CreateDirectory(fxDir);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot, "examples", "PolicyWizard", "fixtures")))
            File.Copy(f, Path.Combine(fxDir, Path.GetFileName(f)), overwrite: true);
        var text = File.ReadAllText(Path.Combine(RepoRoot, "examples", "PolicyWizard", "spec", form + ".md")).Replace("\r\n", "\n");
        var hash = ManifestHash.Of(Path.Combine(ModuleDir, "manifests", form + ".json"));
        text = text.Replace("sha256:0000000000000000", hash);
        if (mutate != null) text = mutate(text);
        var path = Path.Combine(specDir, form + ".md");
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() { try { Directory.Delete(OutRoot, true); } catch { } }
}

[CollectionDefinition("extract")]
public sealed class ExtractCollection : ICollectionFixture<ExtractFixture> { }
