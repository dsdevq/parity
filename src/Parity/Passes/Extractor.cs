using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;
using Parity.Project;
using Parity.Render;
using Parity.Tools;

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
        var baseIndex = FileIndex.Build(project.CompileFiles);
        var variantProjects = cfg.Variants.Select(v => v.Project == null ? null : CsprojReader.Read(cfg.Resolve(v.Project))).ToList();
        var variantIndexes = variantProjects.Select(p => p == null ? baseIndex : FileIndex.Build(p.CompileFiles)).ToList();
        var index = baseIndex.With(variantIndexes.Where(x => !ReferenceEquals(x, baseIndex)));
        var parse = CompilationBuilder.ParseOptions(project);
        var matcher = Glob(formGlob);

        // round 0: files that declare a class matching the glob, plus the designer partials next to them
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, files) in baseIndex.FilesByType)
            if (matcher(type)) foreach (var f in files) loaded.Add(f);
        if (loaded.Count == 0) throw new InvalidOperationException($"no class in {project.Path} matches '{formGlob}'");

        var resxIndex = new ResxIndex(project);

        // ... plus every configured variant's subclasses of a matched class, and the factories that choose between them (#20)
        var variantDiags = new List<Diagnostic>();
        var undescribed = new Dictionary<string, Diagnostic>(StringComparer.Ordinal);
        var variants = DiscoverVariants(cfg, baseIndex, variantIndexes, index, matcher, variantDiags, undescribed);
        foreach (var v in variants) foreach (var f in v.Files) loaded.Add(f);
        var factoryEntries = ResolveFactories(cfg, index, variantDiags).Where(e => variants.Any(v => v.Config.Id == e.Variant)).ToList();
        var factoryFiles = FactoryFiles(index, variants, factoryEntries);
        foreach (var f in factoryFiles) loaded.Add(f);
        foreach (var v in variants.GroupBy(v => v.Config.Id))
            log.WriteLine($"variant {v.Key}: {string.Join(", ", v.Select(x => $"{x.Class} on {x.Base}"))}");
        foreach (var d in variantDiags.Where(d => d.Message.StartsWith("variant", StringComparison.Ordinal)))
            log.WriteLine($"{d.Code}: {d.Message}");

        // a variant project's form resx (the main project's are in resxIndex)
        var variantResx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in variantProjects.Where(p => p != null).SelectMany(p => p!.ResxFiles))
            variantResx.TryAdd(Path.GetFileNameWithoutExtension(r), r);
        var outDir = Path.Combine(cfg.Resolve(cfg.Output), module);
        Directory.CreateDirectory(Path.Combine(outDir, "manifests"));

        var refDiags = new List<Diagnostic>();
        var tRefs = Stopwatch.StartNew();
        var refs = CompilationBuilder.BuildReferences(project, cfg, refDiags);
        log.WriteLine($"references: {refs.Count} loaded in {tRefs.Elapsed.TotalSeconds:0.0}s" + (refDiags.Count > 0 ? $" ({refDiags.Count} problem(s), see diagnostics)" : ""));

        List<FormManifest> manifests = new();
        var compilerVariantDiags = new List<Diagnostic>();
        var stateLine = "shared-state: skipped (no sharedState types configured)";
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
                foreach (var b in cfg.FormBaseTypes.Where(b => !b.EndsWith('*')))
                    if (compilation.GetTypeByMetadataName(b) == null)
                        refDiags.Add(new Diagnostic("BASE-UNRESOLVED", $"formBaseTypes entry '{b}' does not resolve against the loaded references (typo, or its assembly is missing from references)", null));
                diags = new List<Diagnostic>(refDiags);
            }
            var variantNames = variants.Select(v => v.Class).ToHashSet(StringComparer.Ordinal);
            var found = FormDiscovery.Find(compilation, cfg, n => matcher(n) || variantNames.Contains(n), out var rejected);
            var byName = found.GroupBy(f => f.Symbol.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var claimed = new Dictionary<string, VariantClass>(StringComparer.Ordinal);
            var unclaimed = new List<Diagnostic>();
            var spoken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in variants)
            {
                var derives = false;
                if (byName.TryGetValue(v.Class, out var vf) && byName.TryGetValue(v.Base, out var bf))
                {
                    for (var t = vf.Symbol.BaseType; t != null && !derives; t = t.BaseType) derives = SymbolEqualityComparer.Default.Equals(t, bf.Symbol);
                    if (derives) claimed[v.Class] = new VariantClass(v.Config, vf, bf, v.Config.Project?.Replace('\\', '/'));
                }
                if (derives) continue;
                if (byName.ContainsKey(v.Base))
                    unclaimed.Add(new Diagnostic("VARIANT-CONFIG", $"{v.Base}: variant {v.Config.Id}: {v.Class} is written as a subclass of {v.Base} but the compiler does not resolve it as a form deriving from it (another type of the same name, or a base outside formBaseTypes), so the variant class is not described", null));
                else if (v.Config.Subclasses.Contains(v.Class))
                    unclaimed.Add(new Diagnostic("VARIANT-CONFIG", $"variant {v.Config.Id}: subclass '{v.Class}' derives from no form of the main project (the compiler's base chain of {v.Base} reaches no formBaseTypes entry)", null));
                else continue;
                spoken.Add(v.Config.Id);
            }
            foreach (var (id, d) in undescribed)
                if (!spoken.Contains(id) && !claimed.Values.Any(c => c.Config.Id == id)) unclaimed.Add(d);
            compilerVariantDiags = unclaimed;
            var forms = found.Where(f => !claimed.ContainsKey(f.Symbol.Name) && (matcher(f.Symbol.Name) || !variantNames.Contains(f.Symbol.Name))).ToList();
            if (forms.Count == 0)
                throw new InvalidOperationException($"'{formGlob}' matched classes but none derives from a configured form base type. Matched: {(rejected.Count == 0 ? "(none with a base list)" : string.Join("; ", rejected.Take(8)))}. Unresolved base types: {string.Join(", ", refDiags.Where(d => d.Code == "BASE-UNRESOLVED").Select(d => d.Message))}");
            var factories = factoryFiles.Count == 0 ? new List<FactoryInfo>() : FactoryPass.Find(compilation, factoryFiles, cfg, project.Dir, 1);
            var roundVariantDiags = variantDiags.Concat(unclaimed).Concat(UnreadFactories(factoryEntries, factories)).ToList();

            manifests.Clear();
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var stateTypes = StateTypes.Find(compilation, cfg, index);
            int stateBodies = 0, stateAccesses = 0;
            var stateTime = TimeSpan.Zero;
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
                var alias = Aliases(h);
                var ctx = new ContextPass(form, h, cfg, index, stateTypes, project.Dir, i => alias.TryGetValue(i, out var a) ? a : i).Run();
                (stateBodies, stateAccesses, stateTime) = (stateBodies + ctx.StateBodies, stateAccesses + ctx.StateAccesses, stateTime + ctx.StateTime);
                var m = Assemble(form, project, d, h, alias, ctx, constants, cats, diags.Concat(roundVariantDiags.Where(x => x.Message.StartsWith(form.Symbol.Name + ":", StringComparison.Ordinal) || x.Message.StartsWith("variant", StringComparison.Ordinal))).ToList());
                var mine = claimed.Values.Where(v => SymbolEqualityComparer.Default.Equals(v.Base.Symbol, form.Symbol)).OrderBy(v => v.Config.Id, StringComparer.Ordinal).ToList();
                var vinfos = mine.Select(v =>
                {
                    var vresx = resxIndex.Find(Syntax.FullName(v.Form.Symbol), v.Form.Symbol.Name)?.Default.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal)
                                ?? (variantResx.TryGetValue(v.Form.Symbol.Name, out var vp) ? ResxReader.Read(vp) : new Dictionary<string, string>());
                    return new VariantPass(v, m, compilation, project.Dir).Run(cfg, cats, index, loaded, vresx, wanted, new ConstantCollector(compilation, resxIndex, index, loaded, project.Dir));
                }).ToList();
                var mineFactories = FactoriesFor(factories, form, mine, factoryEntries);
                manifests.Add(m with { Variants = vinfos.Count == 0 ? null : vinfos, Factories = mineFactories.Count == 0 ? null : mineFactories });
            }

            // names the compiler could not resolve that the project declares somewhere: load those files too
            foreach (var diag in compilation.GetDiagnostics())
            {
                if (diag.Id is not ("CS0246" or "CS0103" or "CS0234" or "CS0118" or "CS1061")) continue;
                foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(diag.GetMessage(), "'([A-Za-z_][A-Za-z0-9_]*)'"))
                    foreach (var f in index.FilesFor(mm.Groups[1].Value)) if (!loaded.Contains(f)) wanted.Add(f);
            }

            if (cfg.SharedState.Count > 0)
                stateLine = $"shared-state: {stateBodies} {(stateBodies == 1 ? "body" : "bodies")} to depth {ContextPass.StateDepth(cfg)}, {stateAccesses} access(es), {stateTime.TotalSeconds:0.0}s";
            round++;
            log.WriteLine($"round {round}: {loaded.Count} file(s), {forms.Count} form(s), {tRound.Elapsed.TotalSeconds:0.0}s" + (wanted.Count > 0 && round < MaxRounds ? $", +{wanted.Count} file(s) wanted (unresolved names and call graph)" : ""));
            if (wanted.Count == 0 || round >= MaxRounds) break;
            foreach (var w in wanted) loaded.Add(w);
        }

        foreach (var d in compilerVariantDiags.Where(d => d.Message.StartsWith("variant", StringComparison.Ordinal)))
            log.WriteLine($"{d.Code}: {d.Message}");
        log.WriteLine(stateLine);
        var tWrite = Stopwatch.StartNew();
        var summaries = new List<ExtractSummary>();
        foreach (var m in manifests)
        {
            var json = JsonSerializer.Serialize(m, Json.Options) + "\n";
            var jsonPath = Path.Combine(outDir, "manifests", m.ClassName + ".json");
            // one generation is kept, so parity diff and parity rebind work without git (issue #25)
            var prevPath = Path.Combine(outDir, "manifests", m.ClassName + ".prev.json");
            var replaced = File.Exists(jsonPath) && File.ReadAllText(jsonPath) != json;
            if (replaced) File.Move(jsonPath, prevPath, overwrite: true);
            File.WriteAllText(jsonPath, json);
            if (replaced) Changed(outDir, m.ClassName, prevPath, jsonPath, log);
            File.WriteAllText(Path.Combine(outDir, "manifests", m.ClassName + ".md"), MarkdownRenderer.Render(m, ManifestHash.Of(jsonPath)));
            summaries.Add(new ExtractSummary(m.ClassName, m.Controls.Count, m.Events.Count, m.Handlers.Count, m.Methods.Count, m.ServiceCalls.Count, m.DynamicUi.Count, m.Raw.Count, m.Diagnostics.Count));
        }

        // module.json is computed from every manifest on disk, so extracting forms one run at a time accumulates (issue #12)
        var all = Directory.EnumerateFiles(Path.Combine(outDir, "manifests"), "*.json").Where(f => !f.EndsWith(".prev.json", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(f), Json.Options)!).ToList();
        var dupes = Duplicates.Find(all.SelectMany(m => m.Handlers.Select(h => (h.Id, h.Source)).Concat(m.Methods.Where(x => x.Source != null).Select(x => (x.Id, x.Source!)))).DistinctBy(x => x.Item1));
        var modulePath = Path.Combine(outDir, "module.json");
        var prior = File.Exists(modulePath) ? TryRead<ModuleIndex>(modulePath) : null;
        var modIndex = new ModuleIndex(SchemaVersion, module, Path.GetRelativePath(cfg.BaseDir, project.Path).Replace('\\', '/'),
            all.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            all.SelectMany(m => m.ServiceCalls.Select(s => s.Id)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            dupes, ModuleContext.SharedState(all, cfg, prior?.SharedState), ModuleContext.Composition(all), ModuleContext.Dependencies(all));
        File.WriteAllText(modulePath, JsonSerializer.Serialize(modIndex, Json.Options) + "\n");
        if (all.Count > manifests.Count) log.WriteLine($"module.json: {all.Count} form(s) on disk ({manifests.Count} from this run)");
        // the registries are reviewed against an extract (#17): say when this one moved past the last review
        if (Directory.Exists(Path.Combine(outDir, "registry")) && Lint.Registry.Check(outDir, gate: false).Any(f => f.Code == "REGISTRY-STALE"))
            log.WriteLine("registry: STALE, review registry/ against this extract (parity registry check), then parity registry stamp");

        var peak = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        log.WriteLine($"render+write: {tWrite.Elapsed.TotalSeconds:0.0}s");
        var own = project.CompileFiles.ToHashSet(StringComparer.Ordinal);
        var fromVariants = loaded.Count(f => !own.Contains(f));
        log.WriteLine($"files loaded: {loaded.Count - fromVariants} of {project.CompileFiles.Count}" + (fromVariants > 0 ? $" (+{fromVariants} from variant projects)" : "")
            + $"   rounds: {round}   time: {sw.Elapsed.TotalSeconds:0.0}s   peak-rss-mb: {peak}");
        return summaries;
    }

    /// <summary>
    /// A re-extract replaced a manifest: say how many entries changed, and name the spec bound to the old one, so the
    /// accumulation protocol (#17) starts from the log. The counts are parity diff's; the spec is stale when its manifest: differs.
    /// </summary>
    private static void Changed(string outDir, string form, string prevPath, string jsonPath, TextWriter log)
    {
        var changes = ManifestDiff.Compare(ManifestDiff.Load(prevPath), ManifestDiff.Load(jsonPath));
        log.WriteLine($"changed: {form}  {changes.Count(c => c.Kind == '+')} added, {changes.Count(c => c.Kind == '-')} removed, {changes.Count(c => c.Kind == '~')} changed"
            + $"  (parity diff manifests/{form}.prev.json manifests/{form}.json)");
        var spec = Path.Combine(outDir, "spec", form + ".md");
        if (!File.Exists(spec)) return;
        var front = Lint.SpecParser.Parse(File.ReadAllText(spec)).Front;
        if (front.TryGetValue("manifest", out var bound) && bound.Value != ManifestHash.Of(jsonPath))
            log.WriteLine($"stale spec: spec/{form}.md  (parity rebind spec/{form}.md)");
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Method ids whose body is also a handler's (same file and line): the manifest names them by the handler id.</summary>
    private static Dictionary<string, string> Aliases(HandlerResult h)
    {
        var handlerByLoc = h.Handlers.Where(x => !x.Name.StartsWith("lambda@", StringComparison.Ordinal))
            .GroupBy(x => x.Loc.File + ":" + x.Loc.Start).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in h.Methods.Values)
            if (x.Loc != null && handlerByLoc.TryGetValue(x.Loc.File + ":" + x.Loc.Start, out var hid)) alias[x.Id] = hid;
        return alias;
    }

    private static FormManifest Assemble(FormClass form, LegacyProject project, DesignerResult d, HandlerResult h, Dictionary<string, string> alias, ContextResult ctx,
        ConstantCollector k, ControlCategories cats, List<Diagnostic> buildDiags)
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
        string One(string i) => alias.TryGetValue(i, out var a) ? a : i;
        List<string> Rewrite(List<string> ids) => ids.Select(i => alias.TryGetValue(i, out var a) ? a : i).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
        var handlers = h.Handlers.Select(x => x with { Calls = Rewrite(x.Calls) }).ToList();
        var methods = h.Methods.Values.Where(x => !alias.ContainsKey(x.Id)).Select(x => x with { Calls = Rewrite(x.Calls) })
            .OrderBy(m => m.Depth).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var svcs = h.ServiceCalls.Values.Select(s => s with { CalledFrom = Rewrite(s.CalledFrom),
            Sites = s.Sites?.Select(c => c with { Via = One(c.Via) }).DistinctBy(c => c.Via + "|" + string.Join("\u0001", c.Args)).OrderBy(c => c.Via, StringComparer.Ordinal).ToList() })
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
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
        var constants = k.Freeze(name, One, gridDiags);
        var diags = buildDiags.Concat(d.Diagnostics).Concat(h.Diagnostics).Concat(gridDiags).DistinctBy(x => x.Code + x.Message).OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
        var files = form.Declarations.Select(x => Path.GetRelativePath(project.Dir, x.SyntaxTree.FilePath).Replace('\\', '/')).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new FormManifest(Extractor.SchemaVersion, $"form:{name}", name, form.Symbol.ContainingNamespace.ToDisplayString(), form.Bases, files, d.FormCaption,
            controls, events, bindings, rules, handlers, methods, svcs, dyn, constants, d.Raw.OrderBy(r => r.Loc.Start).ToList(), diags,
            Contract: ctx.Contract, SharedState: ctx.SharedState, Embeds: ctx.Embeds);
    }

    private sealed record VariantCandidate(VariantConfig Config, string Class, string Base, List<string> Files);

    /// <summary>
    /// Per configured variant, the classes it declares that derive (by written base name) from a form of the main project;
    /// the nearest such form is the unit the variant customises, and only units the glob selects are described in this run. Variant classes in between are loaded but folded
    /// into the most derived one. The first variant to claim a class keeps it. A variant with candidates and nothing else to say
    /// has its 'declares no subclass' diagnostic in <paramref name="undescribed"/>: the compiler's verdict decides whether it applies.
    /// </summary>
    private static List<VariantCandidate> DiscoverVariants(ExtractConfig cfg, FileIndex baseIndex, List<FileIndex> variantIndexes, FileIndex index,
        Func<string, bool> matcher, List<Diagnostic> diags, Dictionary<string, Diagnostic> undescribed)
    {
        var result = new List<VariantCandidate>();
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < cfg.Variants.Count; i++)
        {
            var v = cfg.Variants[i];
            var own = variantIndexes[i];
            var inBase = ReferenceEquals(own, baseIndex);
            if (string.IsNullOrWhiteSpace(v.Id)) { diags.Add(new Diagnostic("VARIANT-CONFIG", $"variants[{i}]: id is required", null)); continue; }
            if (inBase && v.Subclasses.Count == 0) { diags.Add(new Diagnostic("VARIANT-CONFIG", $"variant {v.Id}: without project, subclasses is required (every subclass in the main project is not a tenant)", null)); continue; }
            var before = diags.Count;
            var elsewhere = false;
            var names = own.FilesByType.Keys.Where(t => inBase ? v.Subclasses.Contains(t) : baseIndex.FilesFor(t).Count == 0)
                .Where(t => v.Subclasses.Count == 0 || v.Subclasses.Contains(t)).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in v.Subclasses.Where(s => !names.Contains(s)))
                diags.Add(new Diagnostic("VARIANT-CONFIG", $"variant {v.Id}: subclass '{missing}' is not declared in {(inBase ? "the main project" : v.Project)}", null));

            // the chain from each class up to a form of the main project that is not itself part of this variant
            var chains = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in names)
            {
                var chain = new List<string>();
                var cur = n;
                for (var depth = 0; depth < 16; depth++)
                {
                    var b = index.BasesOf(cur).FirstOrDefault(x => names.Contains(x) || MayBeMainProjectForm(x, baseIndex, cfg.FormBaseTypes, 0));
                    if (b == null) { chain.Clear(); break; }
                    chain.Add(b);
                    if (!names.Contains(b)) break;
                    cur = b;
                }
                if (chain.Count > 0 && !names.Contains(chain[^1])) chains[n] = chain;
                else if (v.Subclasses.Contains(n))
                    diags.Add(new Diagnostic("VARIANT-CONFIG", $"variant {v.Id}: subclass '{n}' derives from no form of the main project (its base chain stops at {(index.BasesOf(cur).FirstOrDefault() is { } stop ? $"'{stop}'" : $"'{cur}', which has no base list")})", null));
            }
            var intermediate = chains.Values.SelectMany(c => c.Take(c.Count - 1)).ToHashSet(StringComparer.Ordinal);
            foreach (var (cls, chain) in chains.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (intermediate.Contains(cls)) continue;
                if (!matcher(chain[^1])) { elsewhere = true; continue; }
                if (taken.TryGetValue(cls, out var first))
                {
                    diags.Add(new Diagnostic("VARIANT-CONFIG", $"{chain[^1]}: {cls} is claimed by variants {first} and {v.Id}; {first} keeps it", null));
                    continue;
                }
                var baseName = chain[^1];
                if (result.Any(r => r.Config.Id == v.Id && r.Base == baseName))
                {
                    diags.Add(new Diagnostic("VARIANT-CONFIG", $"{baseName}: variant {v.Id} has more than one class on it ({result.First(r => r.Config.Id == v.Id && r.Base == baseName).Class}, {cls}); list the one to describe in subclasses", null));
                    continue;
                }
                taken[cls] = v.Id;
                var files = chain.Take(chain.Count - 1).Prepend(cls).SelectMany(own.FilesFor).Distinct().ToList();
                result.Add(new VariantCandidate(v, cls, baseName, files));
                foreach (var mid in chain.Take(chain.Count - 1))
                    diags.Add(new Diagnostic("VARIANT-INTERMEDIATE", $"{baseName}: variant {v.Id} reaches it through {mid}; only {cls}'s own declarations are described as the delta", null));
            }
            if (diags.Count > before || elsewhere) continue;
            var none = new Diagnostic("VARIANT-CONFIG", $"variant {v.Id}: {(inBase ? "the main project" : v.Project)} declares no subclass of a form of the main project, so the variant is not described", null);
            if (result.Any(r => ReferenceEquals(r.Config, v))) undescribed.TryAdd(v.Id, none);
            else diags.Add(none);
        }
        return result;
    }

    /// <summary>
    /// A main-project class that may be a form by its written base chain: false only when every written base is indexed in the
    /// main project and none is, or reaches, a formBaseTypes entry (full or simple name, wildcards allowed). A base the index does
    /// not know (one from a referenced assembly) leaves the chain possible, and the compiler's type chain decides.
    /// </summary>
    private static bool MayBeMainProjectForm(string type, FileIndex baseIndex, List<string> formBaseTypes, int depth) =>
        baseIndex.FilesFor(type).Count > 0 && (depth >= 16 || baseIndex.BasesOf(type).Any(b =>
            baseIndex.FilesFor(b).Count == 0 || Syntax.Matches(b, formBaseTypes)
            || baseIndex.FilesFor(b).Any(f => baseIndex.NamespaceOf(f) is { } ns && Syntax.Matches(ns + "." + b, formBaseTypes))
            || MayBeMainProjectForm(b, baseIndex, formBaseTypes, depth + 1)));

    /// <summary>A configured factories entry read against the file index: Type (Method null) or Type.Method, both by the type's simple name.</summary>
    private sealed record FactoryEntry(string Variant, string Entry, string Type, string? Method);

    private static string LastSegment(string name) => name[(name.LastIndexOf('.') + 1)..];

    /// <summary>
    /// A configured factories entry is a type or Type.Method, simple or namespace-qualified: the whole entry is tried as a
    /// type first, then the entry without its last segment as the type of a method. An entry that matches no type is VARIANT-CONFIG.
    /// </summary>
    private static List<FactoryEntry> ResolveFactories(ExtractConfig cfg, FileIndex index, List<Diagnostic> diags)
    {
        var result = new List<FactoryEntry>();
        foreach (var v in cfg.Variants)
            foreach (var entry in v.Factories)
            {
                var dot = entry.LastIndexOf('.');
                if (index.FilesFor(LastSegment(entry)).Count > 0) result.Add(new FactoryEntry(v.Id, entry, LastSegment(entry), null));
                else if (dot > 0 && index.FilesFor(LastSegment(entry[..dot])).Count > 0) result.Add(new FactoryEntry(v.Id, entry, LastSegment(entry[..dot]), entry[(dot + 1)..]));
                else diags.Add(new Diagnostic("VARIANT-CONFIG", $"variant {v.Id}: factories entry '{entry}' matches no type in the main project or a variant project (write a type, or Type.Method, simple or namespace-qualified)", null));
            }
        return result;
    }

    /// <summary>The declaring type (simple name) and method name of a method: id.</summary>
    private static (string Type, string Method) Declaring(string methodId)
    {
        var sig = methodId["method:".Length..];
        var qualified = sig.Contains('(') ? sig[..sig.IndexOf('(')] : sig;
        var dot = qualified.LastIndexOf('.');
        return (LastSegment(qualified[..Math.Max(dot, 0)]), qualified[(dot + 1)..]);
    }

    private static bool Reads(FactoryEntry e, FactoryInfo f)
    {
        var (type, method) = Declaring(f.Id);
        return type == e.Type && (e.Method == null || e.Method == method);
    }

    /// <summary>VARIANT-CONFIG for a configured entry whose file loaded but from which no factory was read.</summary>
    private static IEnumerable<Diagnostic> UnreadFactories(List<FactoryEntry> entries, List<FactoryInfo> factories) =>
        entries.Where(e => !factories.Any(f => Reads(e, f))).Select(e => new Diagnostic("VARIANT-CONFIG",
            $"variant {e.Variant}: factories entry '{e.Entry}' reads no factory: " + (e.Method == null
                ? $"{e.Type} has no method that hands out a form class by a key"
                : $"{e.Type} declares no method {e.Method} that hands out a form class by a key"), null));

    /// <summary>Files that construct two or more classes of one unit's family (the unit and its variants), plus configured factory types.</summary>
    private static List<string> FactoryFiles(FileIndex index, List<VariantCandidate> variants, List<FactoryEntry> entries)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in variants.GroupBy(v => v.Base))
        {
            var hits = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var n in family.Select(v => v.Class).Prepend(family.Key).Distinct())
                foreach (var f in index.FilesInstantiating(n)) hits[f] = hits.GetValueOrDefault(f) + 1;
            foreach (var (f, count) in hits) if (count >= 2) files.Add(f);
        }
        foreach (var e in entries)
            foreach (var f in index.FilesFor(e.Type)) files.Add(f);
        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>The factories that hand out this unit or one of its variants, with each case mapped to "base" or the variant.</summary>
    private static List<FactoryInfo> FactoriesFor(List<FactoryInfo> factories, FormClass form, List<VariantClass> variants, List<FactoryEntry> entries)
    {
        var baseName = Syntax.FullName(form.Symbol);
        var byClass = variants.ToDictionary(v => Syntax.FullName(v.Form.Symbol), v => v.Config.Id, StringComparer.Ordinal);
        var configured = entries.Where(e => variants.Any(x => x.Config.Id == e.Variant)).ToList();
        var result = new List<FactoryInfo>();
        foreach (var f in factories)
        {
            var cases = f.Cases.Select(c => c with { Variant = c.Class == baseName ? "base" : byClass.TryGetValue(c.Class, out var id) ? id : null }).ToList();
            if (cases.All(c => c.Variant == null)) continue;
            var distinct = cases.Select(c => c.Class).Distinct().Count();
            if (distinct < 2 && !configured.Any(e => Reads(e, f))) continue;
            result.Add(f with { Cases = cases });
        }
        return result;
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
