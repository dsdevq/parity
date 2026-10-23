using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Project;

namespace Parity.Passes;

/// <summary>
/// A bound model's data annotations as column facts (#19, next to the #15 grid columns). The model type comes from
/// <c>X.DataSource = typeof(T)</c> or a DataSource expression whose rows are T, follows BindingSource references to the
/// grid and from the grid to its views; each model property's [Display]/[DisplayName], [Browsable(false)], [Required]
/// and [StringLength] lands on the column whose FieldName or DataPropertyName names it, or on the view as
/// <c>@model.&lt;Property&gt;</c> when no designer column does.
/// </summary>
public static class ModelFacts
{
    /// <summary>The row type a DataSource expression carries: typeof(T), T[], IEnumerable&lt;T&gt;, or a single project object.</summary>
    public static ITypeSymbol? RowType(ExpressionSyntax e, SemanticModel model)
    {
        var t = e is TypeOfExpressionSyntax tof ? model.GetTypeInfo(tof.Type).Type : model.GetTypeInfo(e).Type;
        if (t is IArrayTypeSymbol arr) t = arr.ElementType;
        else if (t is INamedTypeSymbol nt && nt.SpecialType != SpecialType.System_String
                 && (nt.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T ? nt : nt.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)) is { } en)
            t = en.TypeArguments[0];
        if (t == null || t.TypeKind is TypeKind.Error or TypeKind.TypeParameter || t.SpecialType != SpecialType.None) return null;
        var name = Syntax.FullName(t);
        return name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("DevExpress.", StringComparison.Ordinal) ? null : t;
    }

    public static void Apply(string formName, DesignerResult d, HandlerResult h, ConstantCollector k, CSharpCompilation compilation,
        FileIndex index, HashSet<string> loadedFiles, HashSet<string> wanted)
    {
        var types = new Dictionary<string, List<ITypeSymbol>>(StringComparer.Ordinal);
        void Add(string field, ITypeSymbol t)
        {
            if (!d.Controls.ContainsKey(field)) return;
            if (!types.TryGetValue(field, out var l)) types[field] = l = new List<ITypeSymbol>();
            if (!l.Any(x => SymbolEqualityComparer.Default.Equals(x, t))) l.Add(t);
        }
        foreach (var (f, t) in d.ModelTypes.Concat(h.ModelTypes)) Add(f, t);
        if (types.Count == 0) return;
        var refs = d.DataSourceRefs.Concat(h.DataSourceRefs).ToList();
        for (var pass = 0; pass < 3; pass++) // bs -> bs -> grid chains
            foreach (var (target, src) in refs)
                if (types.TryGetValue(src, out var l)) foreach (var t in l.ToList()) Add(target, t);
        foreach (var c in d.Controls.Values.Where(c => c.Parent != null && Extractor.IsColumnOwner(c.Type) && types.ContainsKey(c.Parent!)).ToList())
            foreach (var t in types[c.Parent!].ToList()) Add(c.Field, t);

        foreach (var (field, list) in types)
        {
            var owner = d.Controls[field];
            owner.Props["@modelType"] = string.Join("; ", list.Select(Syntax.FullName));
            if (!Extractor.IsColumnOwner(owner.Type)) continue;
            var columns = d.Controls.Values.Where(c => c.Parent == field)
                .Select(c => (Col: c, Member: c.Props.TryGetValue("FieldName", out var fn) ? fn : c.Props.TryGetValue("DataPropertyName", out var dp) ? dp : null))
                .Where(x => x.Member != null).ToList();
            foreach (var t in list)
            {
                if (t.DeclaringSyntaxReferences.Length == 0)
                    foreach (var w in index.FilesFor(t.Name)) if (!loadedFiles.Contains(w)) wanted.Add(w); // attribute arguments need the source
                foreach (var p in Properties(t))
                {
                    var col = columns.FirstOrDefault(x => x.Member == p.Name).Col;
                    var ownerId = $"ctrl:{formName}/{(col ?? owner).Field}";
                    var facts = Read(p, ownerId, k, compilation);
                    if (facts.Count == 0) continue;
                    if (col != null)
                    {
                        foreach (var (key, value) in facts) col.Props.TryAdd(key, value);
                        if (col.Caption == null && facts.TryGetValue("@display", out var shown)) col.Caption = shown; // no designer caption: the grid shows the annotation
                    }
                    else owner.Props.TryAdd("@model." + p.Name, Summary(facts));
                }
            }
        }
    }

    private static IEnumerable<IPropertySymbol> Properties(ITypeSymbol t)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = t; cur != null && cur.SpecialType != SpecialType.System_Object; cur = cur.BaseType)
            foreach (var p in cur.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public))
                if (seen.Add(p.Name)) yield return p;
    }

    private static SortedDictionary<string, string> Read(IPropertySymbol p, string ownerId, ConstantCollector k, CSharpCompilation compilation)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in p.GetAttributes())
        {
            var syntax = a.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
            var model = syntax == null ? null : compilation.GetSemanticModel(syntax.SyntaxTree, true);
            var name = a.AttributeClass is { TypeKind: not TypeKind.Error } ac ? ac.Name : syntax?.Name.ToString().Split('.').Last() ?? "";
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            if (syntax?.ArgumentList != null && model != null) k.Visit(syntax.ArgumentList, model, ownerId, slice: false);
            switch (name)
            {
                case "Display":
                    var display = Named(a, syntax, model, "Name") as string;
                    if (display == null) break;
                    if (Named(a, syntax, model, "ResourceType") is INamedTypeSymbol rt)
                    {
                        facts["@display@resx"] = Syntax.FullName(rt) + "." + display;
                        if (syntax != null) display = k.Resource(Syntax.FullName(rt), rt.Name, display, ownerId, Syntax.LocOf(syntax, k.BaseDir), slice: false) ?? display;
                    }
                    facts["@display"] = display;
                    break;
                case "DisplayName" when Positional(a, syntax, model, 0) is string dn:
                    facts["@display"] = dn;
                    break;
                case "Browsable" when Positional(a, syntax, model, 0) is false:
                    facts["@browsable"] = "false";
                    break;
                case "Required":
                    facts["@required"] = "true";
                    break;
                case "StringLength" when Positional(a, syntax, model, 0) is int max:
                    facts["@stringLength"] = Named(a, syntax, model, "MinimumLength") is int min and > 0 ? $"{min}..{max}" : max.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }
        return facts;
    }

    private static string Summary(SortedDictionary<string, string> facts) => string.Join(", ", facts.Where(f => f.Key != "@display@resx").Select(f => f.Key switch
    {
        "@browsable" => "Browsable(false)",
        "@required" => "Required",
        "@stringLength" => $"StringLength({f.Value})",
        _ => $"Display=\"{f.Value}\"",
    }));

    /// <summary>A constructor argument, from the bound attribute or, when its type did not resolve, from the syntax.</summary>
    private static object? Positional(AttributeData a, AttributeSyntax? s, SemanticModel? m, int i)
    {
        if (a.ConstructorArguments.Length > i) return a.ConstructorArguments[i].Value;
        var args = s?.ArgumentList?.Arguments.Where(x => x.NameEquals == null && x.NameColon == null).ToList();
        return args != null && args.Count > i && m != null ? Value(args[i].Expression, m) : null;
    }

    private static object? Named(AttributeData a, AttributeSyntax? s, SemanticModel? m, string name)
    {
        foreach (var kv in a.NamedArguments) if (kv.Key == name) return kv.Value.Value;
        var arg = s?.ArgumentList?.Arguments.FirstOrDefault(x => x.NameEquals?.Name.Identifier.Text == name);
        return arg != null && m != null ? Value(arg.Expression, m) : null;
    }

    private static object? Value(ExpressionSyntax e, SemanticModel m) =>
        e is TypeOfExpressionSyntax tof ? m.GetTypeInfo(tof.Type).Type is { TypeKind: not TypeKind.Error } t ? t : null
        : m.GetConstantValue(e) is { HasValue: true } cv ? cv.Value : null;
}
