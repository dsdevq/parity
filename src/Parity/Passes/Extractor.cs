using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;
using Parity.Project;
using Parity.Render;

namespace Parity.Passes;

public sealed record ExtractSummary(string Form, int Controls, int Events, int Handlers, int Methods, int ServiceCalls, int DynamicUi, int Raw, int Diagnostics);

/// <summary>
/// Orchestrates one module: pick the form files, build a bounded compilation, run the passes,
/// grow the compilation by the files the call graph asks for, repeat up to the configured depth,
/// then write manifests, rendered markdown and the module index.
/// </summary>
public static class Extractor
{
    public const string SchemaVersion = "1";
    private const int MaxRounds = 8;

    public static List<ExtractSummary> Run(ExtractConfig cfg, string module, string formGlob, TextWriter log)
    {
        var sw = Stopwatch.StartNew();
        var project = CsprojReader.Read(cfg.Resolve(cfg.Project));
        var cats = ControlCategories.Load(cfg.Resolve(cfg.ControlCategories));
        var index = FileIndex.Build(project.CompileFiles);
        var parse = CompilationBuilder.ParseOptions(project);
        var matcher = Glob(formGlob);

        // round 0: files that declare a class matching the glob, plus the designer partials next to them
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, files) in index.FilesByType)
            if (matcher(type)) foreach (var f in files) loaded.Add(f);
        if (loaded.Count == 0) throw new InvalidOperationException($"no class in {project.Path} matches '{formGlob}'");

        var resxIndex = new ResxIndex(project);
        var outDir = Path.Combine(cfg.Resolve(cfg.Output), module);
        Directory.CreateDirectory(Path.Combine(outDir, "manifests"));

        var refDiags = new List<Diagnostic>();
        var tRefs = Stopwatch.StartNew();
        var refs = CompilationBuilder.BuildReferences(project, cfg, refDiags);
        log.WriteLine($"references: {refs.Count} loaded in {tRefs.Elapsed.TotalSeconds:0.0}s" + (refDiags.Count > 0 ? $" ({refDiags.Count} problem(s), see diagnostics)" : ""));

        List<FormManifest> manifests = new();
        var round = 0;
        var baseChecked = false;
        while (true)
        {
            var tRound = Stopwatch.StartNew();
            var diags = new List<Diagnostic>(refDiags);
            var trees = loaded.OrderBy(f => f, StringComparer.Ordinal).Select(f => CompilationBuilder.Parse(f, parse)).ToList();
            var compilation = CompilationBuilder.Build(project, trees, refs);
            if (!baseChecked)
            {
                baseChecked = true;
                foreach (var b in cfg.FormBaseTypes)
                    if (compilation.GetTypeByMetadataName(b) == null)
                        refDiags.Add(new Diagnostic("BASE-UNRESOLVED", $"formBaseTypes entry '{b}' does not resolve against the loaded references (typo, or its assembly is missing from references)", null));
                diags = new List<Diagnostic>(refDiags);
            }
            var forms = FormDiscovery.Find(compilation, cfg, matcher, out var rejected);
            if (forms.Count == 0)
                throw new InvalidOperationException($"'{formGlob}' matched classes but none derives from a configured form base type. Matched: {(rejected.Count == 0 ? "(none with a base list)" : string.Join("; ", rejected.Take(8)))}. Unresolved base types: {string.Join(", ", refDiags.Where(d => d.Code == "BASE-UNRESOLVED").Select(d => d.Message))}");

            manifests.Clear();
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var form in forms)
            {
                var resx = resxIndex.Find(Syntax.FullName(form.Symbol), form.Symbol.Name)?.Default.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal)
                           ?? new Dictionary<string, string>();
                var d = DesignerPass.Run(form, t => compilation.GetSemanticModel(t, true), resx, cats, project.Dir);
                var controlNames = d.Controls.Keys.ToHashSet(StringComparer.Ordinal);
                var constants = new ConstantCollector(compilation, resxIndex, index, loaded, project.Dir);
                var h = new HandlerPass(form, compilation, controlNames, cfg, cats, index, loaded, project.Dir, constants).Run(d.Events);
                ModelFacts.Apply(form.Symbol.Name, d, h, constants, compilation, index, loaded, wanted);
                foreach (var w in h.WantedFiles.Concat(constants.WantedFiles)) wanted.Add(w);
                manifests.Add(Assemble(form, project, d, h, constants, cats, diags));
            }

            // names the compiler could not resolve that the project declares somewhere: load those files too
            foreach (var diag in compilation.GetDiagnostics())
            {
                if (diag.Id is not ("CS0246" or "CS0103" or "CS0234" or "CS0118" or "CS1061")) continue;
                foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(diag.GetMessage(), "'([A-Za-z_][A-Za-z0-9_]*)'"))
                    foreach (var f in index.FilesFor(mm.Groups[1].Value)) if (!loaded.Contains(f)) wanted.Add(f);
            }

            round++;
            log.WriteLine($"round {round}: {loaded.Count} file(s), {forms.Count} form(s), {tRound.Elapsed.TotalSeconds:0.0}s" + (wanted.Count > 0 && round < MaxRounds ? $", +{wanted.Count} file(s) wanted (unresolved names and call graph)" : ""));
            if (wanted.Count == 0 || round >= MaxRounds) break;
            foreach (var w in wanted) loaded.Add(w);
        }

        var tWrite = Stopwatch.StartNew();
        var summaries = new List<ExtractSummary>();
        foreach (var m in manifests)
        {
            var json = JsonSerializer.Serialize(m, Json.Options);
            var jsonPath = Path.Combine(outDir, "manifests", m.ClassName + ".json");
            File.WriteAllText(jsonPath, json + "\n");
            File.WriteAllText(Path.Combine(outDir, "manifests", m.ClassName + ".md"), MarkdownRenderer.Render(m, ManifestHash.Of(jsonPath)));
            summaries.Add(new ExtractSummary(m.ClassName, m.Controls.Count, m.Events.Count, m.Handlers.Count, m.Methods.Count, m.ServiceCalls.Count, m.DynamicUi.Count, m.Raw.Count, m.Diagnostics.Count));
        }

        // module.json is computed from every manifest on disk, so extracting forms one run at a time accumulates (issue #12)
        var all = Directory.EnumerateFiles(Path.Combine(outDir, "manifests"), "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(f), Json.Options)!).ToList();
        var dupes = Duplicates.Find(all.SelectMany(m => m.Handlers.Select(h => (h.Id, h.Source)).Concat(m.Methods.Where(x => x.Source != null).Select(x => (x.Id, x.Source!)))).DistinctBy(x => x.Item1));
        var modIndex = new ModuleIndex(SchemaVersion, module, Path.GetRelativePath(cfg.BaseDir, project.Path).Replace('\\', '/'),
            all.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            all.SelectMany(m => m.ServiceCalls.Select(s => s.Id)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            dupes);
        File.WriteAllText(Path.Combine(outDir, "module.json"), JsonSerializer.Serialize(modIndex, Json.Options) + "\n");
        if (all.Count > manifests.Count) log.WriteLine($"module.json: {all.Count} form(s) on disk ({manifests.Count} from this run)");

        var peak = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        log.WriteLine($"render+write: {tWrite.Elapsed.TotalSeconds:0.0}s");
        log.WriteLine($"files loaded: {loaded.Count} of {project.CompileFiles.Count}   rounds: {round}   time: {sw.Elapsed.TotalSeconds:0.0}s   peak-rss-mb: {peak}");
        return summaries;
    }

    private static FormManifest Assemble(FormClass form, LegacyProject project, DesignerResult d, HandlerResult h, ConstantCollector k, ControlCategories cats, List<Diagnostic> buildDiags)
    {
        var name = form.Symbol.Name;
        var declared = d.Controls.Keys.ToHashSet(StringComparer.Ordinal);
        var controls = d.Controls.Values.Select(c => DesignerPass.Freeze(c, name, cats, declared)).OrderBy(c => c.Field, StringComparer.Ordinal).ToList();

        var formEvents = d.Events.Concat(h.Events).Where(e => e.Control.EndsWith("/$this", StringComparison.Ordinal))
            .ToDictionary(e => e.Event, e => e.Handler[(e.Handler.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        var firstDecl = form.Declarations.OrderBy(x => x.SyntaxTree.FilePath, StringComparer.Ordinal).First();
        controls.Insert(0, new ControlInfo($"ctrl:{name}/$this", "$this", form.Bases.FirstOrDefault() ?? "?", "form", null, null, d.FormCaption,
            d.FormProps, new List<string>(), new SortedDictionary<string, string>(formEvents, StringComparer.Ordinal), Syntax.LocOf(firstDecl, project.Dir)));

        var events = d.Events.Concat(h.Events).DistinctBy(e => e.Id + "|" + e.Handler).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var bindings = d.Bindings.Concat(h.Bindings).DistinctBy(b => b.Id + "|" + b.Loc.Start).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        var rules = d.Rules.Concat(h.Rules).DistinctBy(r => r.Id).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var handlerByLoc = h.Handlers.Where(x => !x.Name.StartsWith("lambda@", StringComparison.Ordinal))
            .GroupBy(x => x.Loc.File + ":" + x.Loc.Start).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in h.Methods.Values)
            if (x.Loc != null && handlerByLoc.TryGetValue(x.Loc.File + ":" + x.Loc.Start, out var hid)) alias[x.Id] = hid;
        List<string> Rewrite(List<string> ids) => ids.Select(i => alias.TryGetValue(i, out var a) ? a : i).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
        var handlers = h.Handlers.Select(x => x with { Calls = Rewrite(x.Calls) }).ToList();
        var methods = h.Methods.Values.Where(x => !alias.ContainsKey(x.Id)).Select(x => x with { Calls = Rewrite(x.Calls) })
            .OrderBy(m => m.Depth).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var svcs = h.ServiceCalls.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var dyn = h.DynamicUi.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        // a grid view without designer columns is a visible gap: say so, and say where code touches its columns (#15)
        var gridDiags = new List<Diagnostic>();
        var columnParents = controls.Where(c => c.Category == "column" && c.Parent != null).Select(c => c.Parent!).ToHashSet(StringComparer.Ordinal);
        var allSources = handlers.Select(x => (Id: x.Id, Source: x.Source, Loc: x.Loc)).Concat(methods.Where(x => x.Source != null).Select(x => (Id: x.Id, Source: x.Source!, Loc: x.Loc!))).ToList();
        foreach (var g in controls.Where(c => c.Category == "grid" && IsColumnOwner(c.Type) && !columnParents.Contains(c.Id)))
        {
            var touches = allSources.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Source, $@"\b{System.Text.RegularExpressions.Regex.Escape(g.Field)}\.Columns\b"))
                .Select(x => $"{x.Id} ({x.Loc.File}:{x.Loc.Start})").Take(4).ToList();
            var hint = touches.Count > 0 ? "columns are touched in code: " + string.Join(", ", touches)
                     : "no code in the loaded files touches its Columns either: a layout XML, a base class, a data-driven grid, or a file outside the loaded set";
            gridDiags.Add(new Diagnostic("GRID-NO-COLUMNS", $"{name}.{g.Field}: no column declared in the designer; {hint}", g.Loc));
        }
        var constants = k.Freeze(name, i => alias.TryGetValue(i, out var a) ? a : i, gridDiags);
        var diags = buildDiags.Concat(d.Diagnostics).Concat(h.Diagnostics).Concat(gridDiags).DistinctBy(x => x.Code + x.Message).OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
        var files = form.Declarations.Select(x => Path.GetRelativePath(project.Dir, x.SyntaxTree.FilePath).Replace('\\', '/')).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new FormManifest(Extractor.SchemaVersion, $"form:{name}", name, form.Symbol.ContainingNamespace.ToDisplayString(), form.Bases, files, d.FormCaption,
            controls, events, bindings, rules, handlers, methods, svcs, dyn, constants, d.Raw.OrderBy(r => r.Loc.Start).ToList(), diags);
    }

    public static bool IsColumnOwner(string type) =>
        type.EndsWith("GridView", StringComparison.Ordinal) || type.EndsWith("ColumnView", StringComparison.Ordinal) || type.EndsWith("BandedGridView", StringComparison.Ordinal)
        || type.EndsWith("TreeList", StringComparison.Ordinal) || type.EndsWith("DataGridView", StringComparison.Ordinal) || type == "System.Windows.Forms.ListView";

    /// <summary>One or more globs separated by commas: 'PolicyWizard*,ClientSearchForm'.</summary>
    public static Func<string, bool> Glob(string pattern)
    {
        var res = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .ToList();
        return s => res.Any(re => re.IsMatch(s));
    }
}

public static class ManifestHash
{
    public static string Of(string jsonPath)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(jsonPath));
        return "sha256:" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
