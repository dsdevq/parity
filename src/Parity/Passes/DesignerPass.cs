using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;

namespace Parity.Passes;

/// <summary>Mutable control under construction; frozen into ControlInfo at the end.</summary>
public sealed class ControlBuilder
{
    public required string Field;
    public required string Type;
    public List<string> TypeChain = new();
    public string? Parent;
    public string? LayoutItem;
    public string? Caption;
    public SortedDictionary<string, string> Props = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Events = new(StringComparer.Ordinal);
    public List<string> Bindings = new();
    public int MinLine = int.MaxValue, MaxLine;
    public string? File;

    public void Touch(Loc loc)
    {
        File ??= loc.File;
        if (loc.Start < MinLine) MinLine = loc.Start;
        if (loc.End > MaxLine) MaxLine = loc.End;
    }
}

public sealed class DesignerResult
{
    public Dictionary<string, ControlBuilder> Controls = new(StringComparer.Ordinal);
    public List<EventWiring> Events = new();
    public List<BindingInfo> Bindings = new();
    public List<ValidationRuleInfo> Rules = new();
    public List<RawStatement> Raw = new();
    public List<Diagnostic> Diagnostics = new();
    public string? FormCaption;
    public SortedDictionary<string, string> FormProps = new(StringComparer.Ordinal);
    public bool Found;
}

/// <summary>
/// Interprets InitializeComponent statement by statement. The serialization format is regular, so
/// every statement either matches a known shape or lands in Raw with its line. Nothing is dropped silently.
/// </summary>
public static class DesignerPass
{
    private static readonly HashSet<string> SkipMethods = new(StringComparer.Ordinal)
        { "BeginInit", "EndInit", "SuspendLayout", "ResumeLayout", "PerformLayout" };

    private static readonly HashSet<string> ParentProps = new(StringComparer.Ordinal) { "Parent", "GridControl" };

    public static DesignerResult Run(FormClass form, Func<SyntaxTree, SemanticModel> Model, Dictionary<string, string> resx, ControlCategories cats, string baseDir)
    {
        var r = new DesignerResult();
        var formName = form.Symbol.Name;
        var init = form.Declarations
            .SelectMany(d => d.Members.OfType<MethodDeclarationSyntax>())
            .FirstOrDefault(m => m.Identifier.Text == "InitializeComponent");
        if (init?.Body == null) { r.Diagnostics.Add(new Diagnostic("NO-DESIGNER", $"{formName}: InitializeComponent not found", null)); return r; }
        r.Found = true;
        var model = Model(init.SyntaxTree);

        ControlBuilder Ctl(string field)
        {
            if (!r.Controls.TryGetValue(field, out var c))
                r.Controls[field] = c = new ControlBuilder { Field = field, Type = "?" };
            return c;
        }

        string EvalValue(ExpressionSyntax e, ControlBuilder? target, string path)
        {
            if (e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text == "GetString" && inv.ArgumentList.Arguments.Count == 1
                && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit)
            {
                var key = lit.Token.ValueText;
                if (resx.TryGetValue(key, out var v)) { target?.Props.TryAdd(path + "@resx", key); return v; }
                r.Diagnostics.Add(new Diagnostic("RESX-MISSING", $"{formName}: resource key '{key}' not found", Syntax.LocOf(e, baseDir)));
                return $"resources.GetString(\"{key}\")";
            }
            if (e is LiteralExpressionSyntax l) return l.Token.ValueText;
            return Syntax.Text(e);
        }

        // locals declared inside InitializeComponent (image options, reduce operations, the resources manager)
        var locals = new Dictionary<string, ITypeSymbol?>(StringComparer.Ordinal);
        foreach (var stmt in init.Body.Statements)
        {
            var loc = Syntax.LocOf(stmt, baseDir);
            if (stmt is LocalDeclarationStatementSyntax ld)
            {
                foreach (var v in ld.Declaration.Variables)
                {
                    var sym = model.GetDeclaredSymbol(v) as ILocalSymbol;
                    locals[v.Identifier.Text] = sym?.Type;
                    if (sym?.Type != null && sym.Type.TypeKind != TypeKind.Error && Syntax.FullName(sym.Type) != "System.ComponentModel.ComponentResourceManager")
                    {
                        var c = Ctl(v.Identifier.Text);
                        c.TypeChain = Syntax.TypeChain(sym.Type).ToList(); c.Type = c.TypeChain[0];
                        c.Props["@local"] = "true"; c.Touch(loc);
                    }
                }
                continue;
            }
            if (stmt is not ExpressionStatementSyntax es) { r.Raw.Add(new RawStatement(Syntax.Text(stmt), loc)); continue; }
            var expr = es.Expression;

            // --- X.Event += handler ---
            if (expr is AssignmentExpressionSyntax add && add.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                var rp = Syntax.RootAndPath(add.Left);
                if (rp is { } v0 && v0.Path.Length == 0 && !r.Controls.ContainsKey(v0.Root) && add.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                    rp = ("$this", v0.Root); // this.Load += ...
                if (rp is { } v && v.Path.Length > 0)
                {
                    var evt = v.Path[(v.Path.LastIndexOf('.') + 1)..];
                    var ctrlField = v.Path.Contains('.') ? v.Root + "." + v.Path[..v.Path.LastIndexOf('.')] : v.Root;
                    var handler = Syntax.HandlerNameOf(add.Right) ?? (Syntax.IsLambda(add.Right) ? $"lambda@L{loc.Start}" : null);
                    if (handler != null)
                    {
                        var ctrlId = ctrlField == "$this" ? $"ctrl:{formName}/$this" : $"ctrl:{formName}/{ctrlField}";
                        r.Events.Add(new EventWiring($"evt:{formName}/{ctrlField}.{evt}", ctrlId, evt, $"handler:{formName}/{handler}", "designer", loc));
                        if (ctrlField != "$this" && !ctrlField.Contains('.')) { var c = Ctl(ctrlField); c.Events[evt] = handler; c.Touch(loc); }
                        continue;
                    }
                }
                r.Raw.Add(new RawStatement(Syntax.Text(stmt), loc));
                continue;
            }

            // --- X = new T(...) | X.P.Q = value ---
            if (expr is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                var rp = Syntax.RootAndPath(asg.Left);
                if (rp is not { } lhs) { r.Raw.Add(new RawStatement(Syntax.Text(stmt), loc)); continue; }
                if (lhs.Path.Length == 0 && lhs.Root != "$this" && asg.Right is not ObjectCreationExpressionSyntax
                    && !r.Controls.ContainsKey(lhs.Root) && asg.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                    lhs = ("$this", lhs.Root); // this.Text = ..., this.Name = ...

                if (lhs.Path.Length == 0 && lhs.Root != "$this")
                {
                    if (asg.Right is ObjectCreationExpressionSyntax oc)
                    {
                        var c = Ctl(lhs.Root);
                        var type = model.GetTypeInfo(oc).Type;
                        if (type != null && type.TypeKind != TypeKind.Error)
                        {
                            c.TypeChain = Syntax.TypeChain(type).ToList();
                            c.Type = c.TypeChain[0];
                        }
                        else
                        {
                            c.Type = oc.Type.ToString();
                            c.TypeChain = new List<string> { c.Type };
                            r.Diagnostics.Add(new Diagnostic("TYPE-UNRESOLVED", $"{formName}.{lhs.Root}: type '{c.Type}' not resolved (missing reference?)", loc));
                        }
                        c.Touch(loc);
                        continue;
                    }
                    // this.x = this.y (aliasing) or similar
                    r.Raw.Add(new RawStatement(Syntax.Text(stmt), loc));
                    continue;
                }

                if (lhs.Root == "$this")
                {
                    var val = EvalValue(asg.Right, null, lhs.Path);
                    r.FormProps[lhs.Path] = val;
                    if (lhs.Path == "Text") r.FormCaption = val;
                    continue;
                }

                var target = Ctl(lhs.Root);
                target.Touch(loc);
                var valueRef = Syntax.FieldRef(asg.Right);
                if (lhs.Path == "Control" && valueRef != null && valueRef != "$this")
                {
                    // DevExpress layout item -> control
                    var inner = Ctl(valueRef);
                    inner.LayoutItem = lhs.Root;
                    target.Props["Control"] = valueRef;
                    continue;
                }
                if (ParentProps.Contains(lhs.Path) && valueRef != null)
                {
                    target.Parent = valueRef;
                    target.Props[lhs.Path] = valueRef;
                    continue;
                }
                var value = EvalValue(asg.Right, target, lhs.Path);
                target.Props[lhs.Path] = value;
                if (lhs.Path is "Text" or "Caption" or "Properties.Caption") target.Caption ??= value;
                if (lhs.Path == "Caption") target.Caption = value;
                if (lhs.Path is "DataSource" or "Properties.DataSource")
                    r.Bindings.Add(new BindingInfo($"bind:{formName}/{lhs.Root}.{lhs.Path}", $"ctrl:{formName}/{lhs.Root}", lhs.Path, value, "", "designer", loc));
                if (lhs.Path == "FieldName")
                    r.Bindings.Add(new BindingInfo($"bind:{formName}/{lhs.Root}.FieldName", $"ctrl:{formName}/{lhs.Root}", "FieldName", "", value, "designer", loc));
                continue;
            }

            // --- invocations ---
            if (expr is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ima)
            {
                var method = ima.Name.Identifier.Text;
                if (SkipMethods.Contains(method)) continue;
                var rp = Syntax.RootAndPath(ima.Expression);
                var args = inv.ArgumentList.Arguments;

                if (rp is { } tgt)
                {
                    var root = tgt.Root;
                    var path = tgt.Path;
                    if (path.Length == 0 && root != "$this" && !r.Controls.ContainsKey(root) && ima.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                    { path = root; root = "$this"; } // this.Controls.Add(...)

                    // X.DataBindings.Add(new Binding("Prop", src, "Member"[, bool])) | X.DataBindings.Add("Prop", src, "Member")
                    if (path.EndsWith("DataBindings", StringComparison.Ordinal) && method == "Add")
                    {
                        var bargs = args.Count == 1 && args[0].Expression is ObjectCreationExpressionSyntax boc && boc.ArgumentList != null
                            ? boc.ArgumentList.Arguments : args;
                        if (bargs.Count >= 3)
                        {
                            var prop = EvalValue(bargs[0].Expression, null, "");
                            var src = Syntax.FieldRef(bargs[1].Expression) ?? Syntax.Text(bargs[1].Expression);
                            var member = EvalValue(bargs[2].Expression, null, "");
                            var c = Ctl(root); c.Touch(loc);
                            c.Bindings.Add($"{prop} <- {src}.{member}");
                            r.Bindings.Add(new BindingInfo($"bind:{formName}/{root}.{prop}", $"ctrl:{formName}/{root}", prop, src, member, "designer", loc));
                            continue;
                        }
                    }

                    // X.<Collection>.Add(y) | AddRange(new T[] { y, z })
                    if (method is "Add" or "AddRange" && path.Length > 0)
                    {
                        var refs = new List<string>();
                        var nonRefs = false;
                        foreach (var a in args)
                        {
                            if (a.Expression is ArrayCreationExpressionSyntax arr && arr.Initializer != null)
                                foreach (var e in arr.Initializer.Expressions) { var f = Syntax.FieldRef(e); if (f != null) refs.Add(f); else nonRefs = true; }
                            else if (a.Expression is ImplicitArrayCreationExpressionSyntax iarr)
                                foreach (var e in iarr.Initializer.Expressions) { var f = Syntax.FieldRef(e); if (f != null) refs.Add(f); else nonRefs = true; }
                            else { var f = Syntax.FieldRef(a.Expression); if (f != null) refs.Add(f); else nonRefs = true; }
                        }
                        if (refs.Count > 0 && !nonRefs)
                        {
                            var parentId = root == "$this" ? "$this" : root;
                            if (root != "$this") Ctl(root).Touch(loc);
                            foreach (var f in refs)
                            {
                                var c = Ctl(f); c.Touch(loc);
                                c.Parent = parentId;
                                if (path != "Controls") c.Props.TryAdd("@collection", path);
                            }
                            continue;
                        }
                        if (root != "$this")
                        {
                            // value collections: combo items, lookup columns, etc.
                            var c = Ctl(root); c.Touch(loc);
                            c.Props[path + "." + method] = string.Join(", ", args.Select(a => Syntax.Text(a.Expression)));
                            continue;
                        }
                    }

                    // validationProvider.SetValidationRule(this.x, this.rule)
                    if (method == "SetValidationRule" && args.Count == 2)
                    {
                        var ctl = Syntax.FieldRef(args[0].Expression);
                        var rule = Syntax.FieldRef(args[1].Expression) ?? Syntax.Text(args[1].Expression);
                        if (ctl != null)
                        {
                            Ctl(ctl).Touch(loc);
                            r.Rules.Add(new ValidationRuleInfo($"rule:{formName}/{ctl}#{rule}", $"ctrl:{formName}/{ctl}", rule, "designer", loc));
                            continue;
                        }
                    }

                    // resources.ApplyResources(this.x, "x") -> every "x.*" key from the resx
                    if (method == "ApplyResources" && args.Count >= 2 && args[1].Expression is LiteralExpressionSyntax keyLit)
                    {
                        var prefix = keyLit.Token.ValueText + ".";
                        var ctl = Syntax.FieldRef(args[0].Expression);
                        var target = ctl == null || ctl == "$this" ? null : Ctl(ctl);
                        foreach (var kv in resx.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
                        {
                            var p = kv.Key[prefix.Length..];
                            if (target != null) { target.Props[p] = kv.Value; if (p == "Text") target.Caption ??= kv.Value; target.Touch(loc); }
                            else { r.FormProps[p] = kv.Value; if (p == "Text") r.FormCaption = kv.Value; }
                        }
                        continue;
                    }

                    // any other call on a control or on the form: keep it as a property-like record, appended per call
                    if (root == "$this" || r.Controls.ContainsKey(root) || locals.ContainsKey(root) || !(ima.Expression is IdentifierNameSyntax))
                    {
                        var key = (path.Length == 0 ? "" : path + ".") + method + "()";
                        var val = string.Join(", ", args.Select(a => Syntax.Text(a.Expression)));
                        if (root == "$this") r.FormProps[key] = r.FormProps.TryGetValue(key, out var prev0) ? prev0 + "; " + val : val;
                        else { var c = Ctl(root); c.Touch(loc); c.Props[key] = c.Props.TryGetValue(key, out var prev) ? prev + "; " + val : val; }
                        continue;
                    }
                }
            }

            r.Raw.Add(new RawStatement(Syntax.Text(stmt), loc));
        }

        // names used but never created here: a local, a member declared in the form or a base (field or property), or unresolved
        var chain = string.Join(" -> ", Syntax.TypeChain(form.Symbol).Take(6));
        foreach (var c in r.Controls.Values.Where(c => c.Type == "?").ToList())
        {
            var loc = c.File == null ? null : new Loc(c.File, c.MinLine, c.MaxLine);
            if (locals.TryGetValue(c.Field, out var lt))
            {
                c.Props["@local"] = "true";
                if (lt != null && lt.TypeKind != TypeKind.Error) { c.TypeChain = Syntax.TypeChain(lt).ToList(); c.Type = c.TypeChain[0]; }
                else c.Type = "local";
                continue;
            }
            var hit = Syntax.LookupMember(form.Symbol, c.Field);
            if (hit is { } h)
            {
                var mt = Syntax.MemberType(h.Member);
                var tchain = mt == null || mt.TypeKind == TypeKind.Error ? new List<string>() : Syntax.TypeChain(mt).ToList();
                var componentLike = tchain.Any(t => t is "System.ComponentModel.Component" or "System.Windows.Forms.Control")
                                    || (tchain.Count > 0 && cats.Classify(tchain) != "unknown");
                if (componentLike)
                {
                    c.TypeChain = tchain; c.Type = tchain[0];
                    if (SymbolEqualityComparer.Default.Equals(h.DeclaringType, form.Symbol)) c.Props["@declaredOnly"] = "created outside InitializeComponent";
                    else c.Props["@inherited"] = Syntax.FullName(h.DeclaringType);
                    continue;
                }
                // a property of the form itself (Appearance, IconOptions, LookAndFeel): fold into the form's properties
                foreach (var kv in c.Props) r.FormProps[c.Field + "." + kv.Key] = kv.Value;
                foreach (var ev in c.Events) r.FormProps[c.Field + "." + ev.Key + " +="] = ev.Value;
                r.Controls.Remove(c.Field);
                continue;
            }
            r.Diagnostics.Add(new Diagnostic("MEMBER-UNRESOLVED", $"{formName}.{c.Field}: used in InitializeComponent but neither created there, declared as a local, nor found as a field or property on {chain}", loc));
        }
        return r;
    }

    public static ControlInfo Freeze(ControlBuilder c, string formName, ControlCategories cats, HashSet<string> declared)
    {
        var category = c.Props.ContainsKey("@local") ? "local" : cats.Classify(c.TypeChain.Count > 0 ? c.TypeChain : new List<string> { c.Type });
        string? parent = c.Parent == null ? null : c.Parent == "$this" ? $"ctrl:{formName}/$this" : declared.Contains(c.Parent) ? $"ctrl:{formName}/{c.Parent}" : c.Parent;
        return new ControlInfo(
            $"ctrl:{formName}/{c.Field}", c.Field, c.Type, category, parent,
            c.LayoutItem == null ? null : $"ctrl:{formName}/{c.LayoutItem}",
            c.Caption, c.Props, c.Bindings, c.Events,
            c.File == null ? null : new Loc(c.File, c.MinLine, c.MaxLine));
    }
}
