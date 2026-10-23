using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;

namespace Parity.Passes;

public sealed record FormClass(INamedTypeSymbol Symbol, List<ClassDeclarationSyntax> Declarations, List<string> Bases);

public static class FormDiscovery
{
    /// <summary>Every class in the compilation whose base chain hits a configured form base type.</summary>
    public static List<FormClass> Find(CSharpCompilation compilation, ExtractConfig cfg, Func<string, bool> nameFilter) => Find(compilation, cfg, nameFilter, out _);

    public static List<FormClass> Find(CSharpCompilation compilation, ExtractConfig cfg, Func<string, bool> nameFilter, out List<string> rejected)
    {
        var result = new Dictionary<string, FormClass>(StringComparer.Ordinal);
        rejected = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!nameFilter(cls.Identifier.Text)) continue;
                if (model.GetDeclaredSymbol(cls) is not INamedTypeSymbol sym) continue;
                var chain = Syntax.TypeChain(sym).Skip(1).ToList();
                var isForm = chain.Any(b => Syntax.Matches(b, cfg.FormBaseTypes));
                if (!isForm && cfg.FormBaseTypes.Count > 0)
                {
                    // base type may be unresolved (error type): fall back to the written base name
                    var written = cls.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new();
                    isForm = written.Any(w => cfg.FormBaseTypes.Any(b => b.EndsWith("." + w, StringComparison.Ordinal) || b == w));
                    if (isForm) chain = written;
                }
                if (!isForm)
                {
                    rejected.Add($"{cls.Identifier.Text} : {(chain.Count == 0 ? "object" : string.Join(", ", chain.Take(3)))}");
                    continue;
                }
                var key = Syntax.FullName(sym);
                if (!result.TryGetValue(key, out var fc))
                    result[key] = fc = new FormClass(sym, new List<ClassDeclarationSyntax>(), chain);
                fc.Declarations.Add(cls);
            }
        }
        return result.Values.OrderBy(f => f.Symbol.Name, StringComparer.Ordinal).ToList();
    }
}
