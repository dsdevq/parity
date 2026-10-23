using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Parity.Project;

namespace Parity.Passes;

/// <summary>The configured shared-state types (sharedState globs) declared in the loaded files, and their member names.</summary>
public sealed class StateTypes
{
    public HashSet<INamedTypeSymbol> Types { get; } = new(SymbolEqualityComparer.Default);
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The project's top-level types (by the file index, so a type resolved from selfAssembly counts too) whose full name
    /// matches sharedState, as the compilation resolves them.
    /// </summary>
    public static StateTypes Find(CSharpCompilation compilation, ExtractConfig cfg, FileIndex index)
    {
        var st = new StateTypes();
        if (cfg.SharedState.Count == 0) return st;
        foreach (var (type, files) in index.FilesByType)
            foreach (var ns in files.Select(index.NamespaceOf).Distinct())
            {
                var full = ns == null ? type : ns + "." + type;
                if (!Syntax.Matches(full, cfg.SharedState) || compilation.GetTypeByMetadataName(full) is not { } t || !st.Types.Add(t)) continue;
                foreach (var m in t.GetMembers().Where(m => m is IFieldSymbol { IsImplicitlyDeclared: false } or IPropertySymbol)) st.Names.Add(m.Name);
            }
        return st;
    }
}

public sealed record ContextResult(UnitContract Contract, List<SharedStateUse>? SharedState, List<EmbedInfo>? Embeds, int StateBodies, int StateAccesses, TimeSpan StateTime);

/// <summary>
/// The context around a unit (#17), read from the bodies the handler pass already analysed: the shared state it reads and
/// writes (syntactic, bounded by sharedStateDepth), its Inputs/Outputs contract, and the other units it creates and sets up.
/// </summary>
public sealed class ContextPass
{
    private readonly FormClass _form;
    private readonly HandlerResult _h;
    private readonly ExtractConfig _cfg;
    private readonly FileIndex _index;
    private readonly StateTypes _state;
    private readonly string _baseDir;
    private readonly Func<string, string> _alias;
    private readonly List<UnitBody> _bodies;

    /// <param name="alias">Maps a method id to the handler id with the same body, as the manifest names it.</param>
    public ContextPass(FormClass form, HandlerResult h, ExtractConfig cfg, FileIndex index, StateTypes state, string baseDir, Func<string, string> alias)
    {
        _form = form; _h = h; _cfg = cfg; _index = index; _state = state; _baseDir = baseDir; _alias = alias;
        _bodies = h.Bodies.Where(b => b.Body != null).DistinctBy(b => b.Decl).ToList();
    }

    public static int StateDepth(ExtractConfig cfg) => Math.Min(cfg.SharedStateDepth ?? cfg.MaxCallDepth, cfg.MaxCallDepth);

    public ContextResult Run()
    {
        var sw = Stopwatch.StartNew();
        var (state, bodies, accesses) = SharedState();
        var stateTime = sw.Elapsed;
        return new ContextResult(Contract(state), state, Embeds(), bodies, accesses, stateTime);
    }

    // ---- shared state ----

    private (List<SharedStateUse>? Uses, int Bodies, int Accesses) SharedState()
    {
        if (_cfg.SharedState.Count == 0) return (null, 0, 0);
        var depth = StateDepth(_cfg);
        var uses = new SortedDictionary<string, (ISymbol Member, SortedSet<string> Reads, SortedSet<string> Writes)>(StringComparer.Ordinal);
        int bodies = 0, accesses = 0;
        foreach (var b in _bodies.Where(b => b.Depth <= depth))
        {
            bodies++;
            if (_state.Names.Count == 0) continue;
            var model = _h.Model(b.Body!.SyntaxTree);
            foreach (var id in b.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (!_state.Names.Contains(id.Identifier.Text)) continue;
                var sym = model.GetSymbolInfo(id).Symbol;
                if (sym is not (IFieldSymbol or IPropertySymbol) || !_state.Types.Contains(sym.ContainingType.OriginalDefinition)) continue;
                // the static accessor of the type's own instance (WizardSession.Current) is the way in, not state
                if (sym.IsStatic && SymbolEqualityComparer.Default.Equals(Syntax.MemberType(sym)?.OriginalDefinition, sym.ContainingType.OriginalDefinition)) continue;
                var key = $"state:{sym.ContainingType.Name}.{sym.Name}";
                if (!uses.TryGetValue(key, out var u)) uses[key] = u = (sym, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
                var (read, write) = Access(id);
                if (read) u.Reads.Add(_alias(b.Id));
                if (write) u.Writes.Add(_alias(b.Id));
                accesses++;
            }
        }
        if (uses.Count == 0) return (null, bodies, accesses);
        return (uses.Select(x => new SharedStateUse(x.Key, Syntax.FullName(x.Value.Member.ContainingType), x.Value.Member.Name,
            Syntax.MemberType(x.Value.Member)!.ToDisplayString(), x.Value.Reads.ToList(), x.Value.Writes.ToList())).ToList(), bodies, accesses);
    }

    /// <summary>
    /// Whether an expression (a name, or the member access ending in it) is read, written or both: assignment target is a
    /// write, a compound assignment, ++ or -- and a ref argument both; anything else is a read. Syntax only: see docs/design.md.
    /// </summary>
    private static (bool Read, bool Write) Access(ExpressionSyntax e)
    {
        if (e is SimpleNameSyntax && e.Parent is MemberAccessExpressionSyntax ma && ma.Name == e) e = ma;
        return e.Parent switch
        {
            AssignmentExpressionSyntax a when a.Left == e => (!a.IsKind(SyntaxKind.SimpleAssignmentExpression), true),
            PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax when e.Parent.IsKind(SyntaxKind.PostIncrementExpression) || e.Parent.IsKind(SyntaxKind.PostDecrementExpression)
                || e.Parent.IsKind(SyntaxKind.PreIncrementExpression) || e.Parent.IsKind(SyntaxKind.PreDecrementExpression) => (true, true),
            ArgumentSyntax arg when arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) => (false, true),
            ArgumentSyntax arg when arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) => (true, true),
            _ => (true, false),
        };
    }

    // ---- contract ----

    private static readonly string[] EntryKinds = { "ctor", "entry", "reflection" };

    private UnitContract Contract(List<SharedStateUse>? state)
    {
        var entries = new List<(ContractEntry Entry, IMethodSymbol Symbol, UnitBody Body)>();
        foreach (var b in _bodies.Where(b => b.Depth == 0 && EntryKinds.Contains(b.Kind) && b.Decl is BaseMethodDeclarationSyntax))
        {
            if (_h.Model(b.Decl.SyntaxTree).GetDeclaredSymbol(b.Decl) is not IMethodSymbol ms) continue;
            var name = ms.MethodKind == MethodKind.Constructor ? _form.Symbol.Name : ms.Name;
            var sig = $"{name}({string.Join(", ", ms.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})";
            _h.InvokedFrom.TryGetValue(b.Id, out var from);
            entries.Add((new ContractEntry(b.Id, b.Kind, sig, Syntax.LocOf(b.Decl, _baseDir), from), ms, b));
        }
        entries = entries.OrderBy(e => Array.IndexOf(EntryKinds, e.Entry.Kind)).ThenBy(e => e.Entry.Handler, StringComparer.Ordinal).ThenBy(e => e.Entry.Signature, StringComparer.Ordinal).ToList();

        var inputs = new List<ContractValue>();
        var outputs = new List<ContractValue>();
        foreach (var (e, ms, _) in entries)
            foreach (var p in ms.Parameters)
                inputs.Add(new ContractValue("param", $"{e.Handler[(e.Handler.LastIndexOf('/') + 1)..]}.{p.Name}", p.Type.ToDisplayString(), new() { e.Handler }));

        // objects handed in: a ctor or entry parameter of a project type, and the fields the entry stores it in
        var roots = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var (_, ms, b) in entries)
        {
            var handed = ms.Parameters.Where(p => Handed(p.Type)).ToList();
            if (handed.Count == 0) continue;
            foreach (var p in handed) roots[p] = p.Name;
            var model = _h.Model(b.Body!.SyntaxTree);
            foreach (var asg in b.Body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
                if (model.GetSymbolInfo(asg.Right).Symbol is IParameterSymbol ps && handed.Contains(ps, SymbolEqualityComparer.Default)
                    && model.GetSymbolInfo(asg.Left).Symbol is (IFieldSymbol or IPropertySymbol) and var target
                    && SymbolEqualityComparer.Default.Equals(target.ContainingType, _form.Symbol))
                    roots[target] = target.Name;
        }

        var own = _form.Symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.DeclaringSyntaxReferences.Length > 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
        var values = new Dictionary<(bool Output, string Kind, string Name), (string? Type, SortedSet<string> Via, SortedSet<string>? Values)>();
        void Add(bool output, string kind, string name, string? type, string via, string? value = null)
        {
            if (!values.TryGetValue((output, kind, name), out var v)) values[(output, kind, name)] = v = (type, new(StringComparer.Ordinal), value == null ? null : new(StringComparer.Ordinal));
            v.Via.Add(via);
            if (value != null) v.Values!.Add(value);
        }

        foreach (var b in _bodies.Where(b => b.Own))
        {
            var model = _h.Model(b.Body!.SyntaxTree);
            var via = _alias(b.Id);
            foreach (var node in b.Body.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case IdentifierNameSyntax id when own.TryGetValue(id.Identifier.Text, out var prop)
                        && (id.Parent is not MemberAccessExpressionSyntax pm || pm.Name != id || pm.Expression is ThisExpressionSyntax)
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, prop):
                    {
                        var (read, write) = Access(id);
                        if (read && prop.SetMethod is { DeclaredAccessibility: not Accessibility.Private }) Add(false, "property", prop.Name, prop.Type.ToDisplayString(), via);
                        if (write && prop.GetMethod is { DeclaredAccessibility: Accessibility.Public }) Add(true, "property", prop.Name, prop.Type.ToDisplayString(), via);
                        break;
                    }
                    case AssignmentExpressionSyntax asg when asg.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && asg.Left is IdentifierNameSyntax { Identifier.Text: "DialogResult" } or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.Text: "DialogResult" }:
                        Add(true, "dialog-result", "DialogResult", null, via, asg.Right is MemberAccessExpressionSyntax rm ? rm.Name.Identifier.Text : Syntax.Text(asg.Right));
                        break;
                    case MemberAccessExpressionSyntax ma when roots.Count > 0 && ma.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                    {
                        if (model.GetSymbolInfo(ma.Expression).Symbol is not { } rs || !roots.TryGetValue(rs, out var root)) break;
                        var member = model.GetSymbolInfo(ma).Symbol;
                        var name = $"{root}.{ma.Name.Identifier.Text}";
                        if (ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma) { Add(true, "host-call", name + "()", null, via); break; }
                        var type = member == null ? null : Syntax.MemberType(member)?.ToDisplayString();
                        var (read, write) = Access(ma);
                        if (read) Add(false, "host", name, type, via);
                        if (write) Add(true, "host", name, type, via);
                        break;
                    }
                }
            }
        }
        foreach (var s in state ?? new())
        {
            var name = s.Id["state:".Length..];
            if (s.Reads.Count > 0) inputs.Add(new ContractValue("state", name, s.ValueType, s.Reads));
            if (s.Writes.Count > 0) outputs.Add(new ContractValue("state", name, s.ValueType, s.Writes));
        }
        ContractValue Value(KeyValuePair<(bool Output, string Kind, string Name), (string? Type, SortedSet<string> Via, SortedSet<string>? Values)> x) =>
            new(x.Key.Kind, x.Key.Name, x.Value.Type, x.Value.Via.ToList(), x.Value.Values?.ToList());
        string[] inOrder = { "param", "property", "state", "host" }, outOrder = { "property", "dialog-result", "state", "host", "host-call" };
        inputs.AddRange(values.Where(x => !x.Key.Output).Select(Value));
        outputs.AddRange(values.Where(x => x.Key.Output).Select(Value));
        // params keep declaration order; everything else is ordinal by name within its kind
        static List<ContractValue> Sort(List<ContractValue> l, string[] order) => l.Select((v, i) => (v, i))
            .OrderBy(x => Array.IndexOf(order, x.v.Kind)).ThenBy(x => x.v.Kind == "param" ? x.i.ToString("D6") : x.v.Name, StringComparer.Ordinal).Select(x => x.v).ToList();
        return new UnitContract(entries.Select(e => e.Entry).ToList(), Sort(inputs, inOrder), Sort(outputs, outOrder));
    }

    /// <summary>A parameter type whose members the unit reads or writes on the caller's object: a project class or interface.</summary>
    private bool Handed(ITypeSymbol t) =>
        t is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } n && (n.DeclaringSyntaxReferences.Length > 0 || _index.FilesFor(n.Name).Count > 0)
        && !_state.Types.Contains(n.OriginalDefinition) && !SymbolEqualityComparer.Default.Equals(n, _form.Symbol);

    // ---- composition ----

    private static readonly string[] Modes = { "created", "show", "dialog", "embedded" };

    private List<EmbedInfo>? Embeds()
    {
        var result = new Dictionary<string, EmbedInfo>(StringComparer.Ordinal);
        foreach (var b in _bodies.Where(b => b.Own))
        {
            var model = _h.Model(b.Body!.SyntaxTree);
            foreach (var node in b.Body.DescendantNodesAndSelf())
            {
                ITypeSymbol? unit = null;
                string? factory = null;
                List<string>? factoryArgs = null;
                if (node is ObjectCreationExpressionSyntax oc && model.GetTypeInfo(oc).Type is { } t && FormDiscovery.IsUnit(t, _cfg, _index)) unit = t;
                else if (node is InvocationExpressionSyntax inv && model.GetSymbolInfo(inv).Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, ReturnsVoid: false } fm
                         && fm.ContainingType != null && _index.FilesFor(fm.ContainingType.Name).Count > 0 && FormDiscovery.IsUnit(fm.ReturnType, _cfg, _index))
                {
                    unit = fm.ReturnType;
                    factory = Syntax.MethodId(fm);
                    factoryArgs = inv.ArgumentList.Arguments.Select(a => Syntax.Text(a.Expression)).ToList();
                }
                if (unit == null) continue;
                var creation = (ExpressionSyntax)node;
                var loc = Syntax.LocOf(creation, _baseDir);
                ISymbol? holder = null;
                string local;
                switch (creation.Parent)
                {
                    case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax vd }:
                        local = vd.Identifier.Text; holder = model.GetDeclaredSymbol(vd); break;
                    case AssignmentExpressionSyntax asg when asg.Right == creation:
                        holder = model.GetSymbolInfo(asg.Left).Symbol; local = holder?.Name ?? Syntax.Text(asg.Left); break;
                    default:
                        local = $"anon@L{loc.Start}"; break;
                }
                var mode = 0;
                var calls = new List<EmbedUse>();
                var sets = new List<EmbedUse>();
                var reads = new List<EmbedUse>();
                void Use(ExpressionSyntax e)
                {
                    if (e.Parent is MemberAccessExpressionSyntax ma && ma.Expression == e)
                    {
                        var member = ma.Name.Identifier.Text;
                        if (ma.Parent is InvocationExpressionSyntax mi && mi.Expression == ma)
                        {
                            if (member == "ShowDialog") mode = Math.Max(mode, 2);
                            else if (member == "Show") mode = Math.Max(mode, 1);
                            else calls.Add(new EmbedUse(member, mi.ArgumentList.Arguments.Select(a => Syntax.Text(a.Expression)).ToList(), Syntax.LocOf(mi, _baseDir)));
                            return;
                        }
                        var (read, write) = Access(ma);
                        if (write && ma.Parent is AssignmentExpressionSyntax sa) sets.Add(new EmbedUse(member, new() { Syntax.Text(sa.Right) }, Syntax.LocOf(sa, _baseDir)));
                        else if (read && reads.All(r => r.Member != member)) reads.Add(new EmbedUse(member, new(), Syntax.LocOf(ma, _baseDir)));
                        return;
                    }
                    if (e.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax cm)
                    {
                        if (cm.Name.Identifier.Text == "Add" && Syntax.RootAndPath(cm.Expression) is { } rp && (rp.Path.EndsWith("Controls", StringComparison.Ordinal) || rp is ("Controls", "")))
                            mode = 3;
                        // typeof(X).GetMethod("Name", ...).Invoke(instance, args): a call by name
                        else if (cm.Name.Identifier.Text == "Invoke" && call.ArgumentList.Arguments[0] == arg
                                 && cm.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetMethod" } } gm
                                 && gm.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                            calls.Add(new EmbedUse(lit.Token.ValueText, ReflectedArgs(call), Syntax.LocOf(call, _baseDir), true));
                    }
                }
                if (holder == null) Use(creation);
                else
                    foreach (var id in b.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                        if (id.Identifier.Text == local && id.SpanStart > creation.SpanStart && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, holder)) Use(id);
                var embedId = $"embed:{_form.Symbol.Name}/{b.Name}/{local}";
                if (result.ContainsKey(embedId)) embedId += $"@L{loc.Start}";
                var stmt = creation.FirstAncestorOrSelf<StatementSyntax>();
                result[embedId] = new EmbedInfo(embedId, $"form:{unit.Name}", Syntax.FullName(unit), _alias(b.Id), Modes[mode], factory, calls, sets, reads, loc,
                    stmt != null ? Syntax.Text(stmt) : Syntax.Text(creation), factoryArgs);
            }
        }
        return result.Count == 0 ? null : result.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The arguments of a reflected call: Invoke(instance, null) passes none, Invoke(instance, new object[] { a, b }) passes a and b.</summary>
    private static List<string> ReflectedArgs(InvocationExpressionSyntax invoke)
    {
        var rest = invoke.ArgumentList.Arguments.Skip(1).Select(a => a.Expression).ToList();
        if (rest.Count == 0 || rest[0] is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }) return new();
        var init = rest[0] switch
        {
            ArrayCreationExpressionSyntax ac => ac.Initializer,
            ImplicitArrayCreationExpressionSyntax ic => ic.Initializer,
            _ => null,
        };
        return init != null ? init.Expressions.Select(Syntax.Text).ToList() : rest.Select(Syntax.Text).ToList();
    }
}

/// <summary>The module's view of the context (#17), computed from every manifest on disk like the rest of module.json.</summary>
public static class ModuleContext
{
    /// <summary>
    /// Producers and consumers per shared-state field. A field keeps its version while its value type stays; a changed type is
    /// the next version, and a field no manifest touches any more stays retired, so a state: id never names two things.
    /// </summary>
    public static SharedStateGraph? SharedState(List<FormManifest> all, ExtractConfig cfg, SharedStateGraph? prior)
    {
        if (cfg.SharedState.Count == 0) return null;
        var before = (prior?.Fields ?? new()).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var fields = new List<SharedStateField>();
        foreach (var g in all.SelectMany(m => (m.SharedState ?? new()).Select(u => (Unit: m.Id, Use: u))).GroupBy(x => x.Use.Id, StringComparer.Ordinal))
        {
            var first = g.First().Use;
            var version = before.TryGetValue(g.Key, out var old) ? old.ValueType == first.ValueType ? old.Version : old.Version + 1 : 1;
            List<StateEnd> Ends(Func<SharedStateUse, List<string>> pick) => g.SelectMany(x => pick(x.Use).Select(v => new StateEnd(x.Unit, v)))
                .Distinct().OrderBy(e => e.Unit, StringComparer.Ordinal).ThenBy(e => e.Via, StringComparer.Ordinal).ToList();
            fields.Add(new SharedStateField(g.Key, version, first.Type, first.ValueType, Ends(u => u.Writes), Ends(u => u.Reads)));
        }
        foreach (var (id, old) in before)
            if (fields.All(f => f.Id != id)) fields.Add(old with { Producers = new(), Consumers = new(), Retired = true });
        return new SharedStateGraph(cfg.SharedState.ToList(), fields.OrderBy(f => f.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>Per unit some other unit creates: each host, how it opens it, and the setup it does first (factory call, calls, sets).</summary>
    public static List<CompositionEntry>? Composition(List<FormManifest> all)
    {
        var byId = all.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var entries = all.SelectMany(m => (m.Embeds ?? new()).Select(e => (Host: m.Id, Embed: e)))
            .GroupBy(x => x.Embed.Unit, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CompositionEntry(g.Key, g.OrderBy(x => x.Embed.Id, StringComparer.Ordinal).Select(x =>
            {
                var e = x.Embed;
                var setup = new List<string>();
                if (e.Factory != null) setup.Add($"{MethodName(e.Factory)}({string.Join(", ", e.FactoryArgs ?? new())})");
                setup.AddRange(e.Calls.Select(c => $"{c.Member}({string.Join(", ", c.Args)})" + (c.Reflection == true ? " (reflection)" : "")));
                setup.AddRange(e.Sets.Select(s => $"{s.Member} = {string.Join(", ", s.Args)}"));
                var cases = e.Factory != null && byId.TryGetValue(e.Unit, out var unit) ? unit.Factories?.FirstOrDefault(f => f.Id == e.Factory)?.Cases : null;
                return new CompositionHost(x.Host, e.Id, e.Mode, e.Factory, cases?.Select(c => $"{c.Key} -> {c.Variant ?? c.Class}").ToList(), setup);
            }).ToList())).ToList();
        return entries.Count == 0 ? null : entries;
    }

    /// <summary>Per svc: id, its shape (from the first manifest that has one) and every call site across the module.</summary>
    public static List<DependencyEntry>? Dependencies(List<FormManifest> all)
    {
        var entries = all.SelectMany(m => m.ServiceCalls.Select(s => (Unit: m.Id, Call: s))).GroupBy(x => x.Call.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var shape = g.Select(x => x.Call).FirstOrDefault(c => c.Signature != null) ?? g.First().Call;
                var callers = g.SelectMany(x => x.Call.Sites?.Select(s => new DependencyCaller(x.Unit, s.Via, s.Args))
                                               ?? x.Call.CalledFrom.Select(v => new DependencyCaller(x.Unit, v, x.Call.Args)))
                    .OrderBy(c => c.Unit, StringComparer.Ordinal).ThenBy(c => c.Via, StringComparer.Ordinal).ToList();
                return new DependencyEntry(g.Key, shape.Type, shape.Method, shape.Signature, shape.Request?.DeepClone(), shape.Reply?.DeepClone(), callers);
            }).ToList();
        return entries.Count == 0 ? null : entries;
    }

    private static string MethodName(string methodId)
    {
        var sig = methodId.Contains('(') ? methodId[..methodId.IndexOf('(')] : methodId;
        return sig[(sig.LastIndexOf('.') + 1)..];
    }
}
