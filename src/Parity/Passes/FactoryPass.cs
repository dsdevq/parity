using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;

namespace Parity.Passes;

/// <summary>
/// Reads factory methods (issue #20): which form class a method hands out for which key. Understood shapes are a switch
/// statement or expression on the key, an if-chain comparing the key to a literal, and a dictionary field from key to
/// typeof(T) that the method reads. A when clause, or a condition between a keyed label and the new T(), joins the key with
/// &amp;&amp;, and every form a keyed shape guards is a case. Any other new T() / typeof(T) of a form in the method is its
/// "default" when it is an unconditional return, an else branch or the miss of a dictionary lookup, and otherwise a case
/// keyed by the source text of the condition guarding it.
/// Cases carry full class names; the caller maps them onto a unit and its variants.
/// </summary>
public static class FactoryPass
{
    public static List<FactoryInfo> Find(CSharpCompilation compilation, IEnumerable<string> files, ExtractConfig cfg, string baseDir, int minClasses = 2)
    {
        var wanted = files.ToHashSet(StringComparer.Ordinal);
        var result = new List<FactoryInfo>();
        foreach (var tree in compilation.SyntaxTrees.Where(t => wanted.Contains(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree, true);
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(method) is not IMethodSymbol sym) continue;
                var f = Read(method, sym, model, cfg, baseDir);
                if (f != null && f.Cases.Select(c => c.Class).Distinct().Count() >= minClasses) result.Add(f);
            }
        }
        return result.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
    }

    private static FactoryInfo? Read(MethodDeclarationSyntax method, IMethodSymbol sym, SemanticModel model, ExtractConfig cfg, string baseDir)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body == null) return null;
        var cases = new List<FactoryCase>();
        var consumed = new HashSet<SyntaxNode>();
        var on = new List<string>();

        string? FormOf(SyntaxNode n)
        {
            var type = n switch
            {
                ObjectCreationExpressionSyntax oc => model.GetTypeInfo(oc).Type,
                TypeOfExpressionSyntax to => model.GetTypeInfo(to.Type).Type,
                _ => null,
            };
            if (type is not INamedTypeSymbol nt || nt.TypeKind == TypeKind.Error) return null;
            return Syntax.TypeChain(nt).Skip(1).Any(b => Syntax.Matches(b, cfg.FormBaseTypes)) ? Syntax.FullName(nt) : null;
        }
        void Add(IReadOnlyList<string> keys, SyntaxNode scope)
        {
            foreach (var n in scope.DescendantNodesAndSelf().Where(n => n is ObjectCreationExpressionSyntax or TypeOfExpressionSyntax).ToList())
            {
                if (consumed.Contains(n) || FormOf(n) is not { } cls || Guard(n, scope, on) is not { } guard) continue;
                consumed.Add(n);
                foreach (var k in keys) cases.Add(new FactoryCase(guard.Length == 0 ? k : $"{k} && {guard}", cls, null, Syntax.LocOf(n, baseDir)));
            }
        }
        static string Key(SyntaxNode n) => n is LiteralExpressionSyntax lit ? lit.Token.ValueText : Syntax.Text(n);
        static string PatternKey(PatternSyntax p, WhenClauseSyntax? when)
        {
            var key = p is DiscardPatternSyntax ? "default" : p is ConstantPatternSyntax cp ? Key(cp.Expression) : Syntax.Text(p);
            return when == null ? key : $"{key} && {Syntax.Text(when.Condition)}";
        }

        foreach (var sw in body.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            var before = cases.Count;
            foreach (var section in sw.Sections)
            {
                var keys = section.Labels.Select(l => l switch
                {
                    CaseSwitchLabelSyntax c => Key(c.Value),
                    DefaultSwitchLabelSyntax => "default",
                    CasePatternSwitchLabelSyntax p => PatternKey(p.Pattern, p.WhenClause),
                    _ => Syntax.Text(l),
                }).ToList();
                Add(keys, section);
            }
            if (cases.Count > before) on.Add(Syntax.Text(sw.Expression));
        }
        foreach (var sw in body.DescendantNodes().OfType<SwitchExpressionSyntax>())
        {
            var before = cases.Count;
            foreach (var arm in sw.Arms)
                Add(new[] { PatternKey(arm.Pattern, arm.WhenClause) }, arm.Expression);
            if (cases.Count > before) on.Add(Syntax.Text(sw.GoverningExpression));
        }
        foreach (var iff in body.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (Compared(iff.Condition) is not { } cmp) continue;
            var before = cases.Count;
            Add(new[] { cmp.Key }, iff.Statement);
            if (cases.Count > before) on.Add(cmp.On);
        }

        // a dictionary field from key to type, read by this method
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(id).Symbol is not IFieldSymbol field || on.Contains(field.Name)) continue;
            if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var init }) continue;
            var fieldModel = model.Compilation.GetSemanticModel(init.SyntaxTree, true);
            var initializer = init switch
            {
                ObjectCreationExpressionSyntax oc => oc.Initializer,
                ImplicitObjectCreationExpressionSyntax ioc => ioc.Initializer,
                _ => null,
            };
            if (initializer == null) continue;
            var before = cases.Count;
            foreach (var e in initializer.Expressions)
            {
                var (k, v) = e switch
                {
                    InitializerExpressionSyntax { Expressions.Count: 2 } pair => (pair.Expressions[0], pair.Expressions[1]),
                    AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax ea } a when ea.ArgumentList.Arguments.Count == 1 => (ea.ArgumentList.Arguments[0].Expression, a.Right),
                    _ => (null, null),
                };
                if (k == null || v == null) continue;
                var hit = v.DescendantNodesAndSelf().FirstOrDefault(n => n is ObjectCreationExpressionSyntax or TypeOfExpressionSyntax);
                if (hit == null) continue;
                var type = hit is TypeOfExpressionSyntax to ? fieldModel.GetTypeInfo(to.Type).Type : fieldModel.GetTypeInfo(hit).Type;
                if (type is not INamedTypeSymbol nt || !Syntax.TypeChain(nt).Skip(1).Any(b => Syntax.Matches(b, cfg.FormBaseTypes))) continue;
                cases.Add(new FactoryCase(Key(k), Syntax.FullName(nt), null, Syntax.LocOf(hit, baseDir)));
            }
            if (cases.Count > before) on.Add(field.Name);
        }

        // a form the keyed shapes left over is what an unmatched key gets only when nothing conditional guards it; behind a
        // condition the pass does not understand it is a case keyed by that condition's source text
        if (cases.Count > 0)
        {
            foreach (var n in body.DescendantNodesAndSelf().Where(n => n is ObjectCreationExpressionSyntax or TypeOfExpressionSyntax).ToList())
            {
                if (consumed.Contains(n) || FormOf(n) is not { } cls || Guard(n, body, on) is not { } guard) continue;
                var key = guard.Length == 0 ? "default" : guard;
                if (cases.Any(c => c.Key == key)) continue;
                consumed.Add(n);
                cases.Add(new FactoryCase(key, cls, null, Syntax.LocOf(n, baseDir)));
            }
        }
        if (cases.Count == 0) return null;
        return new FactoryInfo(Syntax.MethodId(sym), string.Join(", ", on.Distinct()), cases, Syntax.LocOf(method, baseDir));
    }

    /// <summary>
    /// The conditions a form sits behind within <paramref name="scope"/> (the method body, or a keyed section, arm or if),
    /// joined with &amp;&amp; (empty: unconditional, an else branch, or the miss of a lookup in a dictionary the method reads);
    /// null when it belongs to a keyed case nested in the scope.
    /// </summary>
    private static string? Guard(SyntaxNode form, SyntaxNode scope, List<string> keyedOn)
    {
        var guards = new List<string>();
        for (var c = form; c != scope && c.Parent != scope; c = c.Parent!)
        {
            switch (c.Parent)
            {
                case SwitchSectionSyntax or SwitchExpressionArmSyntax:
                    return null;
                case IfStatementSyntax iff when iff.Statement == c:
                    if (Compared(iff.Condition) != null) return null;
                    if (!IsMiss(iff.Condition, keyedOn)) guards.Insert(0, Syntax.Text(iff.Condition));
                    break;
                case ConditionalExpressionSyntax ce when ce.WhenTrue == c:
                    guards.Insert(0, Syntax.Text(ce.Condition));
                    break;
            }
        }
        return string.Join(" && ", guards);
    }

    /// <summary>!map.TryGetValue(...) or !map.ContainsKey(...) on a dictionary the method keys by.</summary>
    private static bool IsMiss(ExpressionSyntax cond, List<string> keyedOn) =>
        cond is PrefixUnaryExpressionSyntax { OperatorToken.RawKind: (int)SyntaxKind.ExclamationToken, Operand: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "TryGetValue" or "ContainsKey" } ma } }
        && keyedOn.Contains(Syntax.Text(ma.Expression));

    /// <summary>key == "X", "X" == key, key.Equals("X"), string.Equals(key, "X"[, comparison]).</summary>
    private static (string On, string Key)? Compared(ExpressionSyntax cond)
    {
        static bool Lit(ExpressionSyntax e) => e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression);
        switch (cond)
        {
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression):
                if (Lit(b.Right)) return (Syntax.Text(b.Left), ((LiteralExpressionSyntax)b.Right).Token.ValueText);
                if (Lit(b.Left)) return (Syntax.Text(b.Right), ((LiteralExpressionSyntax)b.Left).Token.ValueText);
                return null;
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Equals" } ma } inv:
                var args = inv.ArgumentList.Arguments.Select(a => a.Expression).ToList();
                if (args.Count >= 1 && Lit(args[0]) && ma.Expression is not PredefinedTypeSyntax && Syntax.Text(ma.Expression) != "String")
                    return (Syntax.Text(ma.Expression), ((LiteralExpressionSyntax)args[0]).Token.ValueText);
                if (args.Count >= 2 && Lit(args[1])) return (Syntax.Text(args[0]), ((LiteralExpressionSyntax)args[1]).Token.ValueText);
                if (args.Count >= 2 && Lit(args[0])) return (Syntax.Text(args[1]), ((LiteralExpressionSyntax)args[0]).Token.ValueText);
                return null;
            default:
                return null;
        }
    }
}
