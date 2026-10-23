using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;

namespace Parity.Passes;

/// <summary>Small syntax helpers shared by the passes.</summary>
public static class Syntax
{
    public static Loc LocOf(SyntaxNode node, string? baseDir = null)
    {
        var span = node.GetLocation().GetLineSpan();
        var file = span.Path;
        // relative even outside baseDir (a variant project next to the main one reads ../Other/File.cs), so no machine path leaks into a manifest
        if (baseDir != null && Path.IsPathRooted(file))
            file = Path.GetRelativePath(baseDir, file);
        return new Loc(file.Replace('\\', '/'), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    /// <summary>"this.a.b.c" -> root "a", path "b.c". "this" alone -> root "$this". Returns null for anything else.</summary>
    public static (string Root, string Path)? RootAndPath(ExpressionSyntax expr)
    {
        var parts = new List<string>();
        var cur = expr;
        while (true)
        {
            switch (cur)
            {
                case MemberAccessExpressionSyntax ma:
                    parts.Add(ma.Name.Identifier.Text);
                    cur = ma.Expression;
                    continue;
                case ThisExpressionSyntax:
                    parts.Reverse();
                    if (parts.Count == 0) return ("$this", "");
                    return (parts[0], string.Join('.', parts.Skip(1)));
                case IdentifierNameSyntax id:
                    parts.Add(id.Identifier.Text);
                    parts.Reverse();
                    return (parts[0], string.Join('.', parts.Skip(1)));
                case ParenthesizedExpressionSyntax p:
                    cur = p.Expression;
                    continue;
                case CastExpressionSyntax c:
                    cur = c.Expression;
                    continue;
                default:
                    return null;
            }
        }
    }

    /// <summary>"this.x" or "x" -> "x"; "this" -> "$this"; otherwise null.</summary>
    public static string? FieldRef(ExpressionSyntax expr)
    {
        var rp = RootAndPath(expr);
        return rp is { } v && v.Path.Length == 0 ? v.Root : null;
    }

    public static string Text(SyntaxNode node) => node.WithoutTrivia().ToFullString().Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Source(SyntaxNode node)
    {
        // method text with its own leading indentation removed, trailing trivia dropped
        var text = node.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        if (lines.Length == 1) return text.Trim();
        var indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select((l, i) => i == 0 ? l.TrimStart() : l.Length >= indent ? l[indent..] : l.TrimStart())).TrimEnd();
    }

    public static string? HandlerNameOf(ExpressionSyntax rhs)
    {
        // new EventHandler(this.m) | new EventHandler(m) | this.m | m
        return rhs switch
        {
            ObjectCreationExpressionSyntax oc when oc.ArgumentList?.Arguments.Count == 1 => HandlerNameOf(oc.ArgumentList.Arguments[0].Expression),
            MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };
    }

    public static bool IsLambda(ExpressionSyntax rhs) =>
        rhs is LambdaExpressionSyntax || rhs is AnonymousMethodExpressionSyntax ||
        (rhs is ObjectCreationExpressionSyntax oc && oc.ArgumentList?.Arguments.Count == 1 && IsLambda(oc.ArgumentList.Arguments[0].Expression));

    public static SyntaxNode? LambdaOf(ExpressionSyntax rhs) => rhs switch
    {
        LambdaExpressionSyntax l => l,
        AnonymousMethodExpressionSyntax a => a,
        ObjectCreationExpressionSyntax oc when oc.ArgumentList?.Arguments.Count == 1 => LambdaOf(oc.ArgumentList.Arguments[0].Expression),
        _ => null,
    };

    /// <summary>Field or property named <paramref name="name"/> on the type or any base, including metadata bases.</summary>
    public static (ISymbol Member, INamedTypeSymbol DeclaringType)? LookupMember(INamedTypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMembers(name).FirstOrDefault(x => x is IFieldSymbol or IPropertySymbol);
            if (m != null) return (m, t);
        }
        return null;
    }

    public static ITypeSymbol? MemberType(ISymbol m) => m switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => null };

    public static IEnumerable<string> TypeChain(ITypeSymbol? type)
    {
        for (var t = type; t != null; t = t.BaseType)
            yield return t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
    }

    public static string FullName(ISymbol s) => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

    public static readonly SymbolDisplayFormat MethodIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string MethodId(IMethodSymbol m) => "method:" + m.OriginalDefinition.ToDisplayString(MethodIdFormat);

    public static bool Matches(string fullName, IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
        {
            if (p.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = p[..^1];
                if (fullName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            else if (p.EndsWith('*'))
            {
                if (fullName.StartsWith(p[..^1], StringComparison.Ordinal)) return true;
            }
            else if (fullName == p || fullName.EndsWith("." + p, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
