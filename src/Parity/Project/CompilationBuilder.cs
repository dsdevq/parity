using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;

namespace Parity.Project;

/// <summary>
/// Builds one CSharpCompilation from a chosen subset of files plus metadata references.
/// No MSBuild, no solution. Unresolved symbols are tolerated and surface as diagnostics.
/// </summary>
public static class CompilationBuilder
{
    public static CSharpParseOptions ParseOptions(LegacyProject project)
    {
        var lang = LanguageVersion.Latest;
        if (project.LangVersion != null && LanguageVersionFacts.TryParse(project.LangVersion, out var parsed)) lang = parsed;
        return new CSharpParseOptions(lang, DocumentationMode.None, SourceCodeKind.Regular, project.Defines);
    }

    public static SyntaxTree Parse(string file, CSharpParseOptions options)
    {
        var text = File.ReadAllText(file);
        return CSharpSyntaxTree.ParseText(text, options, path: file);
    }

    public static CSharpCompilation Build(LegacyProject project, IEnumerable<SyntaxTree> trees, List<MetadataReference> refs)
    {
        var options = new CSharpCompilationOptions(OutputKind.WindowsApplication,
            allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable,
            generalDiagnosticOption: ReportDiagnostic.Suppress);
        return CSharpCompilation.Create(project.AssemblyName + ".parity", trees, refs, options);
    }

    /// <summary>Loaded once per run and reused across rounds; each DLL is opened exactly once.</summary>
    public static List<MetadataReference> BuildReferences(LegacyProject project, ExtractConfig cfg, List<Diagnostic> diags)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRef(string path)
        {
            var name = Path.GetFileName(path);
            if (!seen.Add(name)) return;
            try { refs.Add(MetadataReference.CreateFromFile(path)); }
            catch (Exception ex) { diags.Add(new Diagnostic("REF-LOAD", $"{path}: {ex.Message}", null)); }
        }

        if (cfg.IncludeRuntimeReferences)
        {
            // embedded .NET reference assemblies: identical behaviour from the SDK and from a single-file publish
            var asm = typeof(CompilationBuilder).Assembly;
            var names = asm.GetManifestResourceNames().Where(n => n.StartsWith("refs/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count == 0) diags.Add(new Diagnostic("REF-MISSING", "no embedded runtime reference assemblies in this build", null));
            foreach (var n in names)
            {
                if (!seen.Add(Path.GetFileName(n))) continue;
                using var st = asm.GetManifestResourceStream(n)!;
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                refs.Add(MetadataReference.CreateFromImage(ms.ToArray(), filePath: n));
            }
        }

        foreach (var pattern in cfg.References)
        {
            var full = cfg.Resolve(pattern);
            if (full.Contains('*'))
            {
                var dir = Path.GetDirectoryName(full) ?? ".";
                var glob = Path.GetFileName(full);
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, glob).OrderBy(x => x, StringComparer.Ordinal)) AddRef(f);
                else diags.Add(new Diagnostic("REF-MISSING", $"reference directory not found: {dir}", null));
            }
            else if (File.Exists(full)) AddRef(full);
            else diags.Add(new Diagnostic("REF-MISSING", $"reference not found: {full}", null));
        }

        // the project's own built assembly stands in for every file we did not load as source;
        // types also present as source win (CS0436), which is what we want
        if (cfg.SelfAssembly != null)
        {
            var self = cfg.Resolve(cfg.SelfAssembly);
            if (File.Exists(self)) AddRef(self);
            else diags.Add(new Diagnostic("REF-MISSING", $"selfAssembly not found: {self}", null));
        }
        foreach (var v in cfg.Variants.Where(v => v.SelfAssembly != null))
        {
            var self = cfg.Resolve(v.SelfAssembly!);
            if (File.Exists(self)) AddRef(self);
            else diags.Add(new Diagnostic("REF-MISSING", $"variant {v.Id} selfAssembly not found: {self}", null));
        }

        if (cfg.ReferencesFromProject)
        {
            var dirs = cfg.ReferenceDirs.Select(cfg.Resolve).ToList();
            if (project.OutputDir != null) dirs.Add(project.OutputDir);
            dirs = dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dirs.Count == 0) diags.Add(new Diagnostic("REF-MISSING", "referencesFromProject: none of referenceDirs or the csproj OutputPath exists", null));

            string? Find(string assemblyName)
            {
                foreach (var d in dirs)
                {
                    var p = Path.Combine(d, assemblyName + ".dll");
                    if (File.Exists(p)) return p;
                }
                return null;
            }

            foreach (var r in project.References)
            {
                if (r.HintPath != null && File.Exists(r.HintPath)) { AddRef(r.HintPath); continue; }
                var found = Find(r.Name);
                if (found != null) AddRef(found);
                else if (!r.Name.StartsWith("System", StringComparison.Ordinal) && r.Name is not ("mscorlib" or "Microsoft.CSharp" or "WindowsBase" or "PresentationCore" or "PresentationFramework"))
                    diags.Add(new Diagnostic("REF-MISSING", $"csproj Reference '{r.Name}' not found in referenceDirs", null));
            }
            foreach (var pr in project.ProjectReferences)
            {
                string name;
                try { name = File.Exists(pr) ? CsprojReader.Read(pr).AssemblyName : Path.GetFileNameWithoutExtension(pr); }
                catch { name = Path.GetFileNameWithoutExtension(pr); }
                var found = Find(name);
                if (found != null) AddRef(found);
                else diags.Add(new Diagnostic("REF-MISSING", $"csproj ProjectReference '{name}' not found in referenceDirs", null));
            }
        }

        return refs;
    }
}
