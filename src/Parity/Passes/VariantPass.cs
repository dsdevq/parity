using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;
using Parity.Project;
using Parity.Render;

namespace Parity.Passes;

/// <summary>A class claimed by a configured variant: the subclass, the unit it customises, and the variant's config.</summary>
public sealed record VariantClass(VariantConfig Config, FormClass Form, FormClass Base, string? Project);

/// <summary>
/// Describes a variant as a delta on its base unit (issue #20): the designer and handler passes run on the subclass,
/// with the base form's controls in scope, and everything the subclass adds, removes, re-tags, wires or overrides
/// becomes one var: entry with the variant's file:line. What the variant inherits unchanged is not repeated. Constants and
/// resource texts its code reads are compared with the base's constants[] by Type.Member and value.
/// </summary>
public sealed class VariantPass
{
    private readonly VariantClass _v;
    private readonly FormManifest _base;
    private readonly CSharpCompilation _compilation;
    private readonly string _baseDir;
    private readonly string _vName, _bName, _id;
    private readonly Dictionary<string, ControlInfo> _baseControls;
    private readonly HashSet<string> _added = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private readonly HashSet<string> _overrides = new(StringComparer.Ordinal);

    public VariantPass(VariantClass v, FormManifest baseManifest, CSharpCompilation compilation, string baseDir)
    {
        _v = v; _base = baseManifest; _compilation = compilation; _baseDir = baseDir;
        _vName = v.Form.Symbol.Name; _bName = baseManifest.ClassName; _id = v.Config.Id;
        _baseControls = baseManifest.Controls.ToDictionary(c => c.Field, c => c, StringComparer.Ordinal);
        foreach (var m in v.Form.Declarations.SelectMany(d => d.Members).OfType<MethodDeclarationSyntax>())
        {
            _declared.Add(m.Identifier.Text);
            if (m.Modifiers.Any(SyntaxKind.OverrideKeyword)) _overrides.Add(m.Identifier.Text);
        }
    }

    public VariantInfo Run(ExtractConfig cfg, ControlCategories cats, FileIndex index, HashSet<string> loaded, Dictionary<string, string> resx, HashSet<string> wanted,
        ConstantCollector constants)
    {
        var d = DesignerPass.Run(_v.Form, t => _compilation.GetSemanticModel(t, true), resx, cats, _baseDir);
        foreach (var (field, c) in d.Controls)
            if (!c.Props.ContainsKey("@inherited")) _added.Add(field);

        var owners = new List<INamedTypeSymbol>();
        for (var t = _v.Form.Symbol.BaseType; t != null; t = t.BaseType)
        {
            owners.Add(t);
            if (SymbolEqualityComparer.Default.Equals(t, _v.Base.Symbol)) break;
        }
        var controlNames = _baseControls.Keys.Where(k => k != "$this").Concat(d.Controls.Keys).ToHashSet(StringComparer.Ordinal);
        var h = new HandlerPass(_v.Form, _compilation, controlNames, cfg, cats, index, loaded, _baseDir, constants, owners).Run(d.Events);
        foreach (var w in h.WantedFiles.Concat(constants.WantedFiles)) wanted.Add(w);

        var delta = new List<VariantDelta>();
        var layout = new List<VariantChange>();
        var retags = new Dictionary<string, List<VariantChange>>(StringComparer.Ordinal);
        void Retag(string field, VariantChange ch)
        {
            if (!MarkdownRenderer.IsInterestingProp(ch.Property)) { layout.Add(ch with { Control = $"ctrl:{_bName}/{field}" }); return; }
            if (!retags.TryGetValue(field, out var l)) retags[field] = l = new List<VariantChange>();
            l.RemoveAll(x => x.Property == ch.Property);
            l.Add(ch);
        }
        var removed = new Dictionary<string, Loc?>(StringComparer.Ordinal);

        // the variant's designer: inherited controls it re-tags, controls it adds, the form's own properties
        var declared = d.Controls.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var c in d.Controls.Values.OrderBy(c => c.Field, StringComparer.Ordinal))
        {
            var loc = c.File == null ? null : new Loc(c.File, c.MinLine, c.MaxLine);
            if (c.Props.ContainsKey("@inherited"))
            {
                _baseControls.TryGetValue(c.Field, out var bc);
                foreach (var (k, val) in c.Props)
                {
                    if (k.StartsWith('@') || k.EndsWith("@resx", StringComparison.Ordinal)) continue;
                    if (k == "Controls.Remove()") { foreach (var x in RefsIn(val)) removed[x] = loc; continue; }
                    string? bv = null;
                    if (bc != null && bc.Props.TryGetValue(k, out var b0)) bv = b0;
                    if (bv != val) Retag(c.Field, new VariantChange(k, bv, val, loc));
                }
                var newParent = c.Parent == null ? null : MapControl(c.Parent == "$this" ? $"ctrl:{_vName}/$this" : $"ctrl:{_vName}/{c.Parent}");
                if (newParent != null && newParent != bc?.Parent) Retag(c.Field, new VariantChange("Parent", bc?.Parent, newParent, loc));
                continue;
            }
            var ci = DesignerPass.Freeze(c, _vName, cats, declared);
            var events = ci.Events.Select(e => $"var:{_id}/event/{c.Field}.{e.Key}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            delta.Add(new VariantDelta($"var:{_id}/added/{c.Field}", "added", ci.Parent == null ? null : MapControl(ci.Parent), ci.Caption, ci.Type, ci.Category,
                Props(ci.Props), events.Count == 0 ? null : events, null, null, null, null, null, ci.Loc));
        }
        var thisProps = _baseControls.TryGetValue("$this", out var bthis) ? bthis.Props : new SortedDictionary<string, string>();
        var init = _v.Form.Declarations.SelectMany(x => x.Members).OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "InitializeComponent");
        foreach (var (k, val) in d.FormProps)
        {
            if (k == "Controls.Remove()") { foreach (var x in RefsIn(val)) removed[x] = FormStatementLoc(init, k, x); continue; }
            if (k.EndsWith("@resx", StringComparison.Ordinal) || k == "Name") continue; // a subclass always renames the form
            var bv = thisProps.TryGetValue(k, out var b0) ? b0 : null;
            if (bv != val) Retag("$this", new VariantChange(k, bv, val, FormStatementLoc(init, k, null)));
        }

        // the variant's constructors: top-level re-tags, removals and dynamic controls are the delta; anything else is code
        var ctorHandlers = h.Handlers.Where(x => x.Kind == "ctor").ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        var dynProps = new Dictionary<string, List<VariantChange>>(StringComparer.Ordinal);
        var unexplainedCtors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ctor in _v.Form.Declarations.SelectMany(x => x.Members).OfType<ConstructorDeclarationSyntax>())
        {
            var name = ctor.ParameterList.Parameters.Count == 0 ? "ctor" : $"ctor({ctor.ParameterList.Parameters.Count})";
            var model = _compilation.GetSemanticModel(ctor.SyntaxTree, true);
            var dynLocals = h.DynamicUi.Where(x => x.Handler == $"handler:{_vName}/{name}").Select(x => x.Id[(x.Id.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);
            foreach (var stmt in ctor.Body?.Statements ?? default)
                if (!Explain(stmt, model, name, dynLocals, removed, dynProps, Retag)) unexplainedCtors.Add(name);
        }

        foreach (var (field, changes) in retags.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var target = _baseControls.ContainsKey(field) ? $"ctrl:{_bName}/{field}" : null;
            var detail = target == null ? $"{field} is not a control of {_bName}'s designer (declared on {Syntax.FullName(_v.Base.Symbol)} or a base)" : null;
            delta.Add(new VariantDelta($"var:{_id}/retagged/{field}", "retagged", target, detail, null, null,
                changes.OrderBy(x => x.Property, StringComparer.Ordinal).ToList(), null, null, null, null, null, null, Span(changes.Select(x => x.Loc))));
        }
        foreach (var (field, loc) in removed.OrderBy(x => x.Key, StringComparer.Ordinal))
            delta.Add(new VariantDelta($"var:{_id}/removed/{field}", "removed", _baseControls.ContainsKey(field) ? $"ctrl:{_bName}/{field}" : field, null,
                _baseControls.TryGetValue(field, out var rc) ? rc.Type : null, rc?.Category, null, null, null, null, null, null, null, loc));
        foreach (var dyn in h.DynamicUi)
        {
            var local = dyn.Id[(dyn.Id.LastIndexOf('/') + 1)..];
            var owner = dyn.Handler[(dyn.Handler.LastIndexOf('/') + 1)..];
            var changes = dynProps.TryGetValue(owner + "/" + local, out var dp) ? dp.OrderBy(x => x.Property, StringComparer.Ordinal).ToList() : null;
            var caption = changes?.FirstOrDefault(x => x.Property is "Text" or "Caption" or "Properties.Caption")?.Value;
            var events = h.Events.Where(e => e.Control == dyn.Id).Select(e => Map(e.Id)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            delta.Add(new VariantDelta(Map(dyn.Id), "added", dyn.AddedTo == null ? null : MapControl(dyn.AddedTo), caption, dyn.Type, cats.Classify(new List<string> { dyn.Type }),
                changes, events.Count == 0 ? null : events, null, null, null, null, dyn.Statement, dyn.Loc));
        }

        // constants and resource texts the variant's code reads: the base's Type.Member read again is nothing, the same member
        // name on another type (or another resource) is the base's value replaced, anything else is a read the base does not make
        var constantDiags = new List<Diagnostic>();
        var variantConstants = constants.Freeze(_vName, i => i, constantDiags);
        var variantReads = variantConstants.Select(k => (k.Type, k.Member)).ToHashSet();
        foreach (var k in variantConstants)
        {
            if (k.FirstUse == null || _base.Constants.Any(b => b.Type == k.Type && b.Member == k.Member)) continue;
            var counterpart = _base.Constants.FirstOrDefault(b => b.Member == k.Member && (b.Kind == "resource") == (k.Kind == "resource") && !variantReads.Contains((b.Type, b.Member)));
            var member = System.Text.RegularExpressions.Regex.Replace(k.Member, @"\s+", "_");
            delta.Add(new VariantDelta($"var:{_id}/constant/{SimpleName(k.Type)}.{member}", "constant", counterpart == null ? null : $"{SimpleName(counterpart.Type)}.{counterpart.Member}", k.Kind, k.Type, null,
                new List<VariantChange> { new("value", counterpart?.Value, k.Value, k.FirstUse) }, null, null, null, null, null, null, k.FirstUse));
        }

        // wiring the variant adds, in its designer or its code
        foreach (var e in d.Events.Concat(h.Events).Where(e => e.Source is "designer" or "code" or "dynamic").DistinctBy(e => e.Id + "|" + e.Handler))
            delta.Add(new VariantDelta(Map(e.Id), "event", MapControl(e.Control), Map(e.Handler), null, e.Source, null, null, null, null, null, null, null, e.Loc));

        // handlers: overrides say whether they still run the base, everything else is new code
        var handlerByLoc = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in _base.Handlers.Where(x => !x.Name.StartsWith("lambda@", StringComparison.Ordinal))) handlerByLoc.TryAdd(x.Loc.File + ":" + x.Loc.Start, x.Id);
        foreach (var x in h.Handlers.Where(x => !x.Name.StartsWith("lambda@", StringComparison.Ordinal))) handlerByLoc.TryAdd(x.Loc.File + ":" + x.Loc.Start, Map(x.Id));
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in h.Methods.Values)
            if (x.Loc != null && handlerByLoc.TryGetValue(x.Loc.File + ":" + x.Loc.Start, out var hid)) alias[x.Id] = hid;
        List<string> Ids(IEnumerable<string> ids) => ids.Select(i => alias.TryGetValue(i, out var a) ? a : Map(i)).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var replacedBy = new Dictionary<string, Loc>(StringComparer.Ordinal);
        foreach (var x in h.Handlers)
        {
            if (x.Kind == "ctor" && !unexplainedCtors.Contains(x.Name)) continue;
            var events = x.Events.Where(ev => !ev.StartsWith($"evt:{_vName}/$this.", StringComparison.Ordinal)).Select(Map).OrderBy(i => i, StringComparer.Ordinal).ToList();
            string kind = x.Kind == "ctor" ? "ctor" : _overrides.Contains(x.Name) ? "override" : "handler";
            string? target = null, detail = x.Kind;
            if (kind == "override")
            {
                var callsBase = x.Source.Contains($"base.{x.Name}(", StringComparison.Ordinal);
                var decl = _v.Form.Declarations.SelectMany(dd => dd.Members).OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == x.Name);
                var overridden = (_compilation.GetSemanticModel(decl.SyntaxTree, true).GetDeclaredSymbol(decl) as IMethodSymbol)?.OverriddenMethod;
                target = _base.Handlers.FirstOrDefault(b => b.Name == x.Name)?.Id
                         ?? (overridden != null && _base.Methods.Any(m => m.Id == Syntax.MethodId(overridden)) ? Syntax.MethodId(overridden) : null);
                detail = (callsBase ? "calls base" : "replaces base") + (target == null && overridden != null ? $"; overrides {overridden.ToDisplayString()}" : "");
                if (!callsBase && target != null) { replaced.Add(target); replacedBy[target] = x.Loc; }
            }
            else if (kind == "ctor") detail = "statements beyond re-tags, removals, added controls and wiring";
            delta.Add(new VariantDelta(Map(x.Id), kind, target, detail, null, null, null, events.Count == 0 ? null : events,
                Ids(x.Reads), Ids(x.Writes), Ids(x.Calls), x.ServiceCalls, x.Source, x.Loc));
        }

        // the service boundary: calls only the variant makes, and base calls the variant no longer reaches
        // a service entry cites the variant code that makes (or stops making) the call
        var codeLoc = delta.Where(x => x.Loc != null).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Loc!, StringComparer.Ordinal);
        foreach (var s in h.ServiceCalls.Values.Where(s => _base.ServiceCalls.All(b => b.Id != s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var from = Ids(s.CalledFrom);
            var loc = from.Select(c => codeLoc.GetValueOrDefault(c) ?? methodLoc(c)).FirstOrDefault(l => l != null);
            delta.Add(new VariantDelta($"var:{_id}/service-added/{s.Id[4..]}", "service-added", s.Id, string.Join(" / ", s.Args), s.Type, null,
                null, null, null, null, from, null, null, loc));
        }
        Loc? methodLoc(string id) => h.Methods.TryGetValue(id, out var mi) ? mi.Loc : null;
        var dead = new HashSet<string>(replaced, StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var m in _base.Methods)
            {
                if (dead.Contains(m.Id)) continue;
                var callers = _base.Handlers.Where(x => x.Calls.Contains(m.Id)).Select(x => x.Id).Concat(_base.Methods.Where(x => x.Calls.Contains(m.Id)).Select(x => x.Id)).ToList();
                if (callers.Count > 0 && callers.All(dead.Contains)) { dead.Add(m.Id); grew = true; }
            }
        }
        foreach (var s in _base.ServiceCalls.Where(s => s.CalledFrom.Count > 0 && s.CalledFrom.All(dead.Contains) && !h.ServiceCalls.ContainsKey(s.Id)))
            delta.Add(new VariantDelta($"var:{_id}/service-removed/{s.Id[4..]}", "service-removed", s.Id, $"only called from {string.Join(", ", s.CalledFrom)}, which {_id} replaces", s.Type, null,
                null, null, null, null, null, null, null, s.CalledFrom.Select(c => replacedBy.GetValueOrDefault(c)).FirstOrDefault(l => l != null) ?? replacedBy.Values.First()));

        var methods = h.Methods.Values.Where(x => !alias.ContainsKey(x.Id) && _base.Methods.All(b => b.Id != x.Id))
            .Select(x => x with { Reads = Ids(x.Reads), Writes = Ids(x.Writes), Calls = Ids(x.Calls) })
            .OrderBy(m => m.Depth).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var baseHandlerNames = _base.Handlers.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var diags = d.Diagnostics.Where(x => x.Code != "NO-DESIGNER").Concat(constantDiags)
            .Concat(h.Diagnostics.Where(x => !(x.Code == "HANDLER-MISSING" && baseHandlerNames.Any(n => x.Message.Contains($"'{n}'", StringComparison.Ordinal)))))
            .DistinctBy(x => x.Code + x.Message).OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
        var files = _v.Form.Declarations.Select(x => Path.GetRelativePath(_baseDir, x.SyntaxTree.FilePath).Replace('\\', '/')).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new VariantInfo($"var:{_id}", _id, _v.Project, Syntax.FullName(_v.Form.Symbol), Syntax.TypeChain(_v.Form.Symbol).Skip(1).ToList(), files,
            delta.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            layout.OrderBy(x => x.Control, StringComparer.Ordinal).ThenBy(x => x.Property, StringComparer.Ordinal).ToList(),
            methods, d.Raw.OrderBy(r => r.Loc.Start).ToList(), diags);
    }

    /// <summary>
    /// True when a constructor statement is fully described by the delta: InitializeComponent, a property set on an
    /// inherited control or the form, a Controls.Remove/Add, a control the variant creates and configures, or wiring.
    /// </summary>
    private bool Explain(StatementSyntax stmt, SemanticModel model, string ctor, HashSet<string> dynLocals, Dictionary<string, Loc?> removed,
        Dictionary<string, List<VariantChange>> dynProps, Action<string, VariantChange> retag)
    {
        var loc = Syntax.LocOf(stmt, _baseDir);
        if (stmt is LocalDeclarationStatementSyntax ld)
            return ld.Declaration.Variables.All(v => dynLocals.Contains(v.Identifier.Text));
        if (stmt is not ExpressionStatementSyntax { Expression: var expr }) return false;
        if (expr is AssignmentExpressionSyntax add && add.IsKind(SyntaxKind.AddAssignmentExpression)) return true; // wiring: an event entry
        if (expr is InvocationExpressionSyntax inv)
        {
            if (inv.Expression is IdentifierNameSyntax { Identifier.Text: "InitializeComponent" } or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.Text: "InitializeComponent" }) return true;
            if (inv.Expression is not MemberAccessExpressionSyntax ma || inv.ArgumentList.Arguments.Count != 1) return false;
            var container = Syntax.RootAndPath(ma.Expression);
            var arg = Syntax.FieldRef(inv.ArgumentList.Arguments[0].Expression);
            if (container is not { } cv || arg == null) return false;
            var isControls = cv.Path.EndsWith("Controls", StringComparison.Ordinal) || (cv.Root == "Controls" && cv.Path.Length == 0);
            if (!isControls) return false;
            var owner = cv.Root == "Controls" && cv.Path.Length == 0 ? "$this" : cv.Root;
            if (ma.Name.Identifier.Text == "Remove" && _baseControls.ContainsKey(arg)) { removed[arg] = loc; return true; }
            if (ma.Name.Identifier.Text == "Add" && dynLocals.Contains(arg)) return true;
            if (ma.Name.Identifier.Text == "Add" && _baseControls.TryGetValue(arg, out var moved))
            {
                retag(arg, new VariantChange("Parent", moved.Parent, owner == "$this" ? $"ctrl:{_bName}/$this" : MapControl($"ctrl:{_vName}/{owner}"), loc));
                return true;
            }
            return false;
        }
        if (expr is not AssignmentExpressionSyntax asg || !asg.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
        if (Syntax.RootAndPath(asg.Left) is not { } lhs) return false;
        var value = asg.Right is LiteralExpressionSyntax lit ? lit.Token.ValueText : Syntax.Text(asg.Right);
        if (lhs.Path.Length > 0 && dynLocals.Contains(lhs.Root))
        {
            var key = ctor + "/" + lhs.Root;
            if (!dynProps.TryGetValue(key, out var l)) dynProps[key] = l = new List<VariantChange>();
            l.Add(new VariantChange(lhs.Path, null, value, loc));
            return true;
        }
        if (lhs.Path.Length > 0 && _baseControls.TryGetValue(lhs.Root, out var bc) && lhs.Root != "$this")
        {
            retag(lhs.Root, new VariantChange(lhs.Path, bc.Props.TryGetValue(lhs.Path, out var bv) ? bv : null, value, loc));
            return true;
        }
        if (model.GetSymbolInfo(asg.Left).Symbol is IPropertySymbol p && !_baseControls.ContainsKey(lhs.Root)
            && Syntax.TypeChain(_v.Form.Symbol).Contains(Syntax.FullName(p.ContainingType)))
        {
            var path = lhs.Path.Length == 0 ? lhs.Root : lhs.Root + "." + lhs.Path;
            var bthis = _baseControls.TryGetValue("$this", out var t) ? t.Props : new SortedDictionary<string, string>();
            retag("$this", new VariantChange(path, bthis.TryGetValue(path, out var bv2) ? bv2 : null, value, loc));
            return true;
        }
        return false;
    }

    /// <summary>A handler-pass id of the subclass, renamed into the variant's id space or onto the base unit's id.</summary>
    private string Map(string id)
    {
        if (id.StartsWith($"ctrl:{_vName}/", StringComparison.Ordinal)) return MapControl(id);
        if (id.StartsWith($"dyn:{_vName}/", StringComparison.Ordinal)) return $"var:{_id}/added/{id[($"dyn:{_vName}/").Length..]}";
        if (id.StartsWith($"evt:{_vName}/", StringComparison.Ordinal)) return $"var:{_id}/event/{id[($"evt:{_vName}/").Length..]}";
        if (id.StartsWith($"handler:{_vName}/", StringComparison.Ordinal))
        {
            var name = id[($"handler:{_vName}/").Length..];
            if (name == "ctor" || name.StartsWith("ctor(", StringComparison.Ordinal)) return $"var:{_id}/{name}";
            if (_overrides.Contains(name)) return $"var:{_id}/override/{name}";
            if (_declared.Contains(name) || name.StartsWith("lambda@", StringComparison.Ordinal)) return $"var:{_id}/handler/{name}";
            return $"handler:{_bName}/{name}";
        }
        return id;
    }

    private string MapControl(string id)
    {
        if (!id.StartsWith($"ctrl:{_vName}/", StringComparison.Ordinal)) return id;
        var field = id[($"ctrl:{_vName}/").Length..];
        if (field == "$this") return $"ctrl:{_bName}/$this";
        return _added.Contains(field) ? $"var:{_id}/added/{field}" : $"ctrl:{_bName}/{field}";
    }

    /// <summary>The InitializeComponent statement that set a form property (this.Text = ...) or removed a control, else the method.</summary>
    private Loc? FormStatementLoc(MethodDeclarationSyntax? init, string key, string? removedField)
    {
        if (init?.Body == null) return null;
        foreach (var st in init.Body.Statements.OfType<ExpressionStatementSyntax>())
        {
            if (removedField != null)
            {
                if (st.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Remove" } } inv
                    && inv.ArgumentList.Arguments.Count == 1 && Syntax.FieldRef(inv.ArgumentList.Arguments[0].Expression) == removedField)
                    return Syntax.LocOf(st, _baseDir);
                continue;
            }
            if (st.Expression is AssignmentExpressionSyntax a && Syntax.RootAndPath(a.Left) is { } lhs
                && (lhs.Path.Length == 0 ? lhs.Root : lhs.Root + "." + lhs.Path) == key)
                return Syntax.LocOf(st, _baseDir);
        }
        return Syntax.LocOf(init, _baseDir);
    }

    private static string SimpleName(string typeName) => typeName[(typeName.LastIndexOf('.') + 1)..];

    private static List<VariantChange>? Props(SortedDictionary<string, string> props)
    {
        var l = props.Where(p => !p.Key.StartsWith('@') && MarkdownRenderer.IsInterestingProp(p.Key))
            .Select(p => new VariantChange(p.Key, null, p.Value, null)).ToList();
        return l.Count == 0 ? null : l;
    }

    /// <summary>"this.dtDob; this.btnX" (a designer call record) -> field names.</summary>
    private static IEnumerable<string> RefsIn(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => v.StartsWith("this.", StringComparison.Ordinal) ? v[5..] : v);

    private static Loc? Span(IEnumerable<Loc?> locs)
    {
        var l = locs.Where(x => x != null).Select(x => x!).ToList();
        if (l.Count == 0) return null;
        var file = l.GroupBy(x => x.File).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        var inFile = l.Where(x => x.File == file).ToList();
        return new Loc(file, inFile.Min(x => x.Start), inFile.Max(x => x.End));
    }
}
