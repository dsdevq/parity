using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;
using Parity.Project;

namespace Parity.Passes;

public sealed class HandlerResult
{
    public List<HandlerInfo> Handlers = new();
    public Dictionary<string, MethodInfo> Methods = new(StringComparer.Ordinal);
    public Dictionary<string, ServiceCallInfo> ServiceCalls = new(StringComparer.Ordinal);
    public List<DynamicUiInfo> DynamicUi = new();
    public List<EventWiring> Events = new();
    public List<BindingInfo> Bindings = new();
    public List<ValidationRuleInfo> Rules = new();
    public List<Diagnostic> Diagnostics = new();
    public HashSet<string> WantedFiles = new(StringComparer.Ordinal);
    public List<(string Field, ITypeSymbol Type)> ModelTypes = new();
    public List<(string Target, string Source)> DataSourceRefs = new();
}

/// <summary>
/// Everything the designer does not show: code-behind wiring, lambdas, overrides, the constructor,
/// handler bodies with what they read, write and call, the transitive call graph to a bounded depth,
/// service-boundary calls, bindings and validation rules set in code, and controls created at runtime.
/// </summary>
public sealed class HandlerPass
{
    private readonly FormClass _form;
    private readonly CSharpCompilation _compilation;
    private readonly HashSet<string> _controls;
    private readonly ExtractConfig _cfg;
    private readonly ControlCategories _cats;
    private readonly FileIndex _index;
    private readonly HashSet<string> _loadedFiles;
    private readonly string _baseDir;
    private readonly string _formName;
    private readonly ConstantCollector _k;
    private readonly HandlerResult _r = new();
    private readonly Queue<(IMethodSymbol Method, int Depth)> _work = new();
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    private readonly HashSet<INamedTypeSymbol> _owners = new(SymbolEqualityComparer.Default);

    /// <param name="controlOwners">Types whose fields count as the form's controls; a variant passes its chain down to the base form.</param>
    public HandlerPass(FormClass form, CSharpCompilation compilation, HashSet<string> controls, ExtractConfig cfg,
        ControlCategories cats, FileIndex index, HashSet<string> loadedFiles, string baseDir, ConstantCollector constants,
        IEnumerable<INamedTypeSymbol>? controlOwners = null)
    {
        _form = form; _compilation = compilation; _controls = controls; _cfg = cfg; _cats = cats; _k = constants;
        _index = index; _loadedFiles = loadedFiles; _baseDir = baseDir; _formName = form.Symbol.Name;
        _owners.Add(form.Symbol);
        foreach (var o in controlOwners ?? Enumerable.Empty<INamedTypeSymbol>()) _owners.Add(o);
    }

    private SemanticModel Model(SyntaxTree t)
    {
        if (!_models.TryGetValue(t, out var m)) _models[t] = m = _compilation.GetSemanticModel(t, ignoreAccessibility: true);
        return m;
    }

    public HandlerResult Run(List<EventWiring> designerEvents)
    {
        var members = _form.Declarations.SelectMany(d => d.Members).ToList();
        var methodsByName = members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text is not ("InitializeComponent" or "Dispose"))
            .GroupBy(m => m.Identifier.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // 1. wiring outside the designer, and lambdas anywhere
        var lambdas = new Dictionary<string, (SyntaxNode Node, List<string> Events)>(StringComparer.Ordinal);
        foreach (var decl in _form.Declarations)
        {
            foreach (var add in decl.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)))
            {
                var enclosing = add.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (enclosing?.Identifier.Text == "InitializeComponent") continue;
                var ownerName = enclosing?.Identifier.Text ?? (add.Ancestors().OfType<ConstructorDeclarationSyntax>().Any() ? "ctor" : "field");
                var rp = Syntax.RootAndPath(add.Left);
                if (rp is { } v0 && v0.Path.Length == 0 && !_controls.Contains(v0.Root) && add.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                    rp = ("$this", v0.Root); // this.Load += ...
                if (rp is not { } v || v.Path.Length == 0) continue;
                var evt = v.Path[(v.Path.LastIndexOf('.') + 1)..];
                var ctrlField = v.Path.Contains('.') ? v.Root + "." + v.Path[..v.Path.LastIndexOf('.')] : v.Root;
                var loc = Syntax.LocOf(add, _baseDir);
                string ctrlId;
                if (ctrlField == "$this") ctrlId = $"ctrl:{_formName}/$this";
                else if (_controls.Contains(ctrlField)) ctrlId = $"ctrl:{_formName}/{ctrlField}";
                else ctrlId = $"dyn:{_formName}/{ownerName}/{ctrlField}";
                var handlerName = Syntax.HandlerNameOf(add.Right);
                if (handlerName == null && Syntax.LambdaOf(add.Right) is { } lam)
                {
                    handlerName = $"lambda@L{loc.Start}";
                    if (!lambdas.TryGetValue(handlerName, out var l)) lambdas[handlerName] = l = (lam, new List<string>());
                    l.Events.Add($"evt:{_formName}/{ctrlField}.{evt}");
                }
                if (handlerName == null) { _r.Diagnostics.Add(new Diagnostic("WIRING-UNKNOWN", $"{_formName}: cannot name the handler in '{Syntax.Text(add)}'", loc)); continue; }
                var source = ctrlId.StartsWith("dyn:", StringComparison.Ordinal) ? "dynamic" : "code";
                _r.Events.Add(new EventWiring($"evt:{_formName}/{ctrlField}.{evt}", ctrlId, evt, $"handler:{_formName}/{handlerName}", source, loc));
            }
        }

        var allEvents = designerEvents.Concat(_r.Events).ToList();
        var eventsByHandler = allEvents.GroupBy(e => e.Handler, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // 2. the handler set
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (handlerId, events) in eventsByHandler)
        {
            var name = handlerId[(handlerId.LastIndexOf('/') + 1)..];
            if (lambdas.TryGetValue(name, out var lam))
            {
                var body = lam.Node is LambdaExpressionSyntax le ? (SyntaxNode?)le.Body : lam.Node is AnonymousMethodExpressionSyntax am ? am.Block : null;
                EmitHandler(handlerId, name, KindOf(events, "lambda"), events, lam.Node, body ?? lam.Node);
                handled.Add(name);
                continue;
            }
            if (methodsByName.TryGetValue(name, out var decls))
            {
                foreach (var m in decls) EmitHandler(handlerId, name, KindOf(events, "method"), events, m, (SyntaxNode?)m.Body ?? m.ExpressionBody);
                handled.Add(name);
                continue;
            }
            _r.Diagnostics.Add(new Diagnostic("HANDLER-MISSING", $"{_formName}: handler '{name}' is wired but not declared in the class", null));
        }

        // overrides and constructors are handlers without wiring
        foreach (var m in members.OfType<MethodDeclarationSyntax>())
        {
            if (!m.Modifiers.Any(SyntaxKind.OverrideKeyword) || m.Identifier.Text == "Dispose") continue;
            var name = m.Identifier.Text;
            if (handled.Contains(name)) continue;
            var evId = $"evt:{_formName}/$this.{name}";
            _r.Events.Add(new EventWiring(evId, $"ctrl:{_formName}/$this", name, $"handler:{_formName}/{name}", "override", Syntax.LocOf(m, _baseDir)));
            EmitHandler($"handler:{_formName}/{name}", name, "lifecycle", new List<string> { evId }, m, (SyntaxNode?)m.Body ?? m.ExpressionBody);
            handled.Add(name);
        }
        foreach (var c in members.OfType<ConstructorDeclarationSyntax>())
        {
            var name = c.ParameterList.Parameters.Count == 0 ? "ctor" : $"ctor({c.ParameterList.Parameters.Count})";
            var evId = $"evt:{_formName}/$this.{name}";
            _r.Events.Add(new EventWiring(evId, $"ctrl:{_formName}/$this", name, $"handler:{_formName}/{name}", "ctor", Syntax.LocOf(c, _baseDir)));
            EmitHandler($"handler:{_formName}/{name}", name, "ctor", new List<string> { evId }, c, (SyntaxNode?)c.Body ?? c.ExpressionBody);
            handled.Add(name);
        }

        // non-private methods nobody wired are entry points: hosting forms call them to hand the form its inputs (#14)
        foreach (var m in members.OfType<MethodDeclarationSyntax>())
        {
            var name = m.Identifier.Text;
            if (handled.Contains(name) || name is "InitializeComponent" or "Dispose") continue;
            if (!m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword) || x.IsKind(SyntaxKind.InternalKeyword) || x.IsKind(SyntaxKind.ProtectedKeyword))) continue;
            if (m.Modifiers.Any(SyntaxKind.OverrideKeyword)) continue;
            var evId = $"evt:{_formName}/$this.{name}";
            _r.Events.Add(new EventWiring(evId, $"ctrl:{_formName}/$this", name, $"handler:{_formName}/{name}", "entry", Syntax.LocOf(m, _baseDir)));
            EmitHandler($"handler:{_formName}/{name}", name, "entry", new List<string> { evId }, m, (SyntaxNode?)m.Body ?? m.ExpressionBody);
            handled.Add(name);
        }

        // 3. transitive methods
        while (_work.Count > 0)
        {
            var (method, depth) = _work.Dequeue();
            var id = Syntax.MethodId(method);
            if (_r.Methods.ContainsKey(id)) continue;
            var decl = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl == null)
            {
                var typeName = method.ContainingType?.Name ?? "";
                var files = _index.FilesFor(typeName).Where(f => !_loadedFiles.Contains(f)).ToList();
                foreach (var f in files) _r.WantedFiles.Add(f);
                _r.Methods[id] = new MethodInfo(id, method.ToDisplayString(), depth, new(), new(), new(), new(), new(), null, null, method.ContainingAssembly?.Name);
                continue;
            }
            SyntaxNode? body = decl switch
            {
                MethodDeclarationSyntax md => (SyntaxNode?)md.Body ?? md.ExpressionBody,
                ConstructorDeclarationSyntax cd => (SyntaxNode?)cd.Body ?? cd.ExpressionBody,
                AccessorDeclarationSyntax ad => (SyntaxNode?)ad.Body ?? ad.ExpressionBody,
                PropertyDeclarationSyntax pd => pd.ExpressionBody,
                ArrowExpressionClauseSyntax arrow => arrow,
                _ => decl,
            };
            var a = Analyze(body, id, depth);
            _r.Methods[id] = new MethodInfo(id, method.ToDisplayString(), depth, a.Reads, a.Writes, a.Calls, a.ServiceCalls, a.Unresolved,
                Syntax.LocOf(decl, _baseDir), Syntax.Source(decl), null);
        }

        // 4. unreached private methods in the form are suspicious
        foreach (var (name, decls) in methodsByName)
        {
            if (handled.Contains(name)) continue;
            foreach (var m in decls)
            {
                var sym = Model(m.SyntaxTree).GetDeclaredSymbol(m);
                if (sym != null && _r.Methods.ContainsKey(Syntax.MethodId(sym))) continue;
                var handlerShaped = m.ParameterList.Parameters.Count == 2 && m.ParameterList.Parameters[1].Type?.ToString().EndsWith("EventArgs", StringComparison.Ordinal) == true;
                var refs = new List<string>();
                foreach (var tree in _compilation.SyntaxTrees)
                {
                    foreach (var tok in tree.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name))
                    {
                        if (tok.Parent?.Parent == m || tok.Parent == m) continue; // the declaration itself
                        var where = Syntax.LocOf(tok.Parent!, _baseDir);
                        var kind = tok.Parent?.Ancestors().Any(a => a is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.AddAssignmentExpression)) == true ? "subscribed"
                                 : tok.Parent?.Parent is InvocationExpressionSyntax || tok.Parent?.Parent?.Parent is InvocationExpressionSyntax ? "called"
                                 : "mentioned";
                        refs.Add($"{kind} at {where.File}:{where.Start}");
                    }
                    foreach (var lit in tree.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.Contains(name, StringComparison.Ordinal)))
                        refs.Add($"named in a string at {Syntax.LocOf(lit.Parent!, _baseDir).File}:{Syntax.LocOf(lit.Parent!, _baseDir).Start} (reflection?)");
                }
                var checkedWhat = $"checked: designer wiring, += in the {_compilation.SyntaxTrees.Count()} loaded files, calls from loaded handlers and methods";
                var detail = refs.Count == 0 ? "no other reference in the loaded files" : string.Join("; ", refs.Distinct().Take(5));
                var shape = handlerShaped ? "; handler-shaped signature: look for wiring in a user control, a base class, or a file outside this project" : "";
                _r.Diagnostics.Add(new Diagnostic("UNREACHED-METHOD", $"{_formName}.{name}: not wired and not called ({checkedWhat}); {detail}{shape}", Syntax.LocOf(m, _baseDir)));
            }
        }

        _r.Handlers.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return _r;
    }

    private static string KindOf(List<string> events, string fallback)
    {
        var names = events.Select(e => e[(e.LastIndexOf('.') + 1)..]).ToList();
        if (names.Any(n => n.StartsWith("Validat", StringComparison.Ordinal) || n == "ValidateRow")) return "validation";
        if (names.Any(n => n is "Click" or "DoubleClick" or "ButtonClick" or "ItemClick")) return "action";
        if (names.Any(n => n.EndsWith("Changed", StringComparison.Ordinal))) return "change";
        if (names.Any(n => n is "Load" or "Shown" or "FormClosing" or "FormClosed" or "Activated")) return "lifecycle";
        if (names.Any(n => n is "Tick")) return "timer";
        return fallback == "lambda" ? "ui" : "ui";
    }

    private void EmitHandler(string id, string name, string kind, List<string> events, SyntaxNode decl, SyntaxNode? body)
    {
        var a = Analyze(body, id, 0);
        if (a.Writes.Any(w => w.Contains("errorProvider", StringComparison.OrdinalIgnoreCase)) || a.Calls.Any(c => c.Contains("SetError") || c.Contains(".Validate(")))
            kind = kind is "ui" or "change" ? "validation" : kind;
        _r.Handlers.Add(new HandlerInfo(id, name, kind, events, a.Reads, a.Writes, a.Calls, a.ServiceCalls, a.Unresolved, Syntax.LocOf(decl, _baseDir), Syntax.Source(decl)));
    }

    private sealed class Analysis
    {
        public SortedSet<string> ReadsSet = new(StringComparer.Ordinal), WritesSet = new(StringComparer.Ordinal), CallsSet = new(StringComparer.Ordinal), SvcSet = new(StringComparer.Ordinal), UnresolvedSet = new(StringComparer.Ordinal);
        public List<string> Reads => ReadsSet.ToList();
        public List<string> Writes => WritesSet.ToList();
        public List<string> Calls => CallsSet.ToList();
        public List<string> ServiceCalls => SvcSet.ToList();
        public List<string> Unresolved => UnresolvedSet.ToList();
    }

    private Analysis Analyze(SyntaxNode? body, string ownerId, int depth)
    {
        var a = new Analysis();
        if (body == null) return a;
        var model = Model(body.SyntaxTree);
        var ownerName = ownerId[(ownerId.LastIndexOf('/') + 1)..];
        var dynamicLocals = new Dictionary<string, string>(StringComparer.Ordinal); // local/field name -> dyn id
        _k.Visit(body, model, ownerId, slice: true);

        // reads / writes of designer controls
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!_controls.Contains(id.Identifier.Text)) continue;
            var sym = model.GetSymbolInfo(id).Symbol;
            if (sym is not IFieldSymbol f || !_owners.Contains(f.ContainingType)) continue;
            var ctrlId = $"ctrl:{_formName}/{f.Name}";
            if (IsWrite(id)) a.WritesSet.Add(ctrlId); else a.ReadsSet.Add(ctrlId);
        }

        // calls
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv:
                {
                    var info = model.GetSymbolInfo(inv);
                    var m = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (m == null)
                    {
                        a.UnresolvedSet.Add(Syntax.Text(inv.Expression) + "(...)");
                        if (Syntax.RootAndPath(inv.Expression) is { } urp && urp.Root != "$this")
                            foreach (var wf in _index.FilesFor(urp.Root)) if (!_loadedFiles.Contains(wf)) _r.WantedFiles.Add(wf);
                        break;
                    }
                    RecordCall(m, inv.ArgumentList.Arguments.Select(x => Syntax.Text(x.Expression)).ToList(), ownerId, depth, a);
                    // bindings and rules set in code
                    if (inv.Expression is MemberAccessExpressionSyntax ma)
                    {
                        var rp = Syntax.RootAndPath(ma.Expression);
                        var args = inv.ArgumentList.Arguments;
                        if (rp is { } v && v.Path.EndsWith("DataBindings", StringComparison.Ordinal) && ma.Name.Identifier.Text == "Add" && _controls.Contains(v.Root))
                        {
                            var bargs = args.Count == 1 && args[0].Expression is ObjectCreationExpressionSyntax boc && boc.ArgumentList != null ? boc.ArgumentList.Arguments : args;
                            if (bargs.Count >= 3)
                                _r.Bindings.Add(new BindingInfo($"bind:{_formName}/{v.Root}.{Syntax.Text(bargs[0].Expression).Trim('"')}", $"ctrl:{_formName}/{v.Root}",
                                    Syntax.Text(bargs[0].Expression).Trim('"'), Syntax.Text(bargs[1].Expression), Syntax.Text(bargs[2].Expression).Trim('"'), "code", Syntax.LocOf(inv, _baseDir)));
                        }
                        if (ma.Name.Identifier.Text == "SetValidationRule" && args.Count == 2 && Syntax.FieldRef(args[0].Expression) is { } ctl)
                            _r.Rules.Add(new ValidationRuleInfo($"rule:{_formName}/{ctl}#{Syntax.Text(args[1].Expression)}", $"ctrl:{_formName}/{ctl}", Syntax.Text(args[1].Expression), "code", Syntax.LocOf(inv, _baseDir)));
                    }
                    break;
                }
                case ObjectCreationExpressionSyntax oc:
                {
                    var type = model.GetTypeInfo(oc).Type;
                    var chain = type != null && type.TypeKind != TypeKind.Error ? Syntax.TypeChain(type).ToList() : new List<string> { oc.Type.ToString() };
                    var isControl = chain.Any(t => t is "System.Windows.Forms.Control") || _cats.Classify(chain) is not ("unknown" or "nonvisual" or "rule");
                    // creating a control the designer already knows (declared-only field) is its creation site, not a new control
                    var assignedField = oc.Parent is AssignmentExpressionSyntax asg0 ? Syntax.FieldRef(asg0.Left) : null;
                    if (isControl && depth == 0 && !(assignedField != null && _controls.Contains(assignedField)))
                    {
                        var local = oc.Parent switch
                        {
                            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax vd } => vd.Identifier.Text,
                            AssignmentExpressionSyntax asg when Syntax.FieldRef(asg.Left) is { } fr => fr,
                            _ => $"anon@L{Syntax.LocOf(oc, _baseDir).Start}",
                        };
                        var dynId = $"dyn:{_formName}/{ownerName}/{local}";
                        dynamicLocals[local] = dynId;
                        string? addedTo = null;
                        foreach (var addInv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (addInv.Expression is MemberAccessExpressionSyntax am && am.Name.Identifier.Text == "Add"
                                && Syntax.RootAndPath(am.Expression) is { } arp && (arp.Path.EndsWith("Controls", StringComparison.Ordinal) || arp is ("Controls", ""))
                                && addInv.ArgumentList.Arguments.Count == 1 && Syntax.FieldRef(addInv.ArgumentList.Arguments[0].Expression) == local)
                            { addedTo = arp.Root is "$this" or "Controls" && arp.Path.Length == 0 ? $"ctrl:{_formName}/$this" : _controls.Contains(arp.Root) ? $"ctrl:{_formName}/{arp.Root}" : arp.Root; break; }
                        }
                        var stmt = oc.FirstAncestorOrSelf<StatementSyntax>();
                        _r.DynamicUi.Add(new DynamicUiInfo(dynId, chain[0], addedTo, ownerId, Syntax.LocOf(oc, _baseDir), stmt != null ? Syntax.Text(stmt) : Syntax.Text(oc)));
                    }
                    var ctor = model.GetSymbolInfo(oc).Symbol as IMethodSymbol;
                    if (ctor != null && ctor.DeclaringSyntaxReferences.Length > 0)
                        RecordCall(ctor, oc.ArgumentList?.Arguments.Select(x => Syntax.Text(x.Expression)).ToList() ?? new(), ownerId, depth, a);
                    break;
                }
                case AssignmentExpressionSyntax asg when asg.IsKind(SyntaxKind.SimpleAssignmentExpression):
                {
                    var rp = Syntax.RootAndPath(asg.Left);
                    if (rp is { } v && _controls.Contains(v.Root) && v.Path is "DataSource" or "Properties.DataSource")
                    {
                        _r.Bindings.Add(new BindingInfo($"bind:{_formName}/{v.Root}.{v.Path}", $"ctrl:{_formName}/{v.Root}", v.Path, Syntax.Text(asg.Right), "", "code", Syntax.LocOf(asg, _baseDir)));
                        if (Syntax.FieldRef(asg.Right) is { } src && _controls.Contains(src)) _r.DataSourceRefs.Add((v.Root, src));
                        else if (ModelFacts.RowType(asg.Right, model) is { } rowType) _r.ModelTypes.Add((v.Root, rowType));
                    }
                    break;
                }
                case MemberAccessExpressionSyntax pma when pma.Parent is not InvocationExpressionSyntax:
                {
                    // property getters with a body in source (e.g. controller.CanGoBack) carry logic too; a generated
                    // resource class's getters are resource texts (constants[]), not logic
                    if (model.GetSymbolInfo(pma).Symbol is IPropertySymbol p && p.GetMethod != null && p.GetMethod.DeclaringSyntaxReferences.Length > 0
                        && !SymbolEqualityComparer.Default.Equals(p.ContainingType, _form.Symbol) && !ConstantCollector.IsResourceClass(p.ContainingType))
                    {
                        var decl = p.GetMethod.DeclaringSyntaxReferences[0].GetSyntax();
                        var hasBody = decl is AccessorDeclarationSyntax ad ? (ad.Body != null || ad.ExpressionBody != null) : decl is ArrowExpressionClauseSyntax;
                        if (hasBody) RecordCall(p.GetMethod, new(), ownerId, depth, a);
                    }
                    break;
                }
            }
        }
        return a;
    }

    private void RecordCall(IMethodSymbol m, List<string> args, string ownerId, int depth, Analysis a)
    {
        var type = m.ContainingType;
        var fullType = type == null ? "" : Syntax.FullName(type);
        if (_cfg.ServiceBoundary.Count > 0 && Syntax.Matches(fullType, _cfg.ServiceBoundary))
        {
            var id = $"svc:{type!.Name}.{m.Name}";
            a.SvcSet.Add(id);
            if (!_r.ServiceCalls.TryGetValue(id, out var sc)) _r.ServiceCalls[id] = sc = new ServiceCallInfo(id, fullType, m.Name, new(), new());
            if (!sc.CalledFrom.Contains(ownerId)) sc.CalledFrom.Add(ownerId);
            var argText = string.Join(", ", args);
            if (argText.Length > 0 && !sc.Args.Contains(argText)) sc.Args.Add(argText);
            return;
        }
        if (Syntax.Matches(fullType, _cfg.IgnoreCalls)) return;
        if (m.Name == "InitializeComponent") return;
        if (m.MethodKind is MethodKind.DelegateInvoke or MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator) return;
        var inSource = m.DeclaringSyntaxReferences.Length > 0;
        var id2 = Syntax.MethodId(m);
        a.CallsSet.Add(id2);
        if (inSource && depth + 1 > _cfg.MaxCallDepth) return;
        if (!inSource && m.ContainingType != null && _index.FilesFor(m.ContainingType.Name).Count == 0) return; // framework-ish, not project-local
        _work.Enqueue((m, depth + 1));
    }

    private static bool IsWrite(IdentifierNameSyntax id)
    {
        // the control is the root of an assignment's left side: x.Enabled = ..., this.x.Text = ...
        for (SyntaxNode? n = id; n != null; n = n.Parent)
        {
            if (n.Parent is AssignmentExpressionSyntax asg && asg.Left == n)
                return Syntax.RootAndPath(asg.Left) is { } rp && rp.Root == id.Identifier.Text;
            if (n.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
            {
                var name = ma.Name.Identifier.Text;
                if (name is "SetError" or "SetValidationRule" && ((ArgumentListSyntax)arg.Parent).Arguments[0] == arg) return true;
                return false;
            }
            if (n.Parent is InvocationExpressionSyntax inv2 && inv2.Expression == n && n is MemberAccessExpressionSyntax m2)
            {
                var name = m2.Name.Identifier.Text;
                if (name is "SelectAll" or "Focus" or "Clear" or "Add" or "AddRange" or "Remove" or "ClearErrors" or "RefreshData" or "ResetBindings" or "BringToFront" or "Invalidate")
                    return Syntax.RootAndPath(m2.Expression) is { } rp2 && rp2.Root == id.Identifier.Text;
                return false;
            }
            if (n is StatementSyntax) return false;
        }
        return false;
    }
}
