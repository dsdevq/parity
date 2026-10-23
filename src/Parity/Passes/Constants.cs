using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parity.Model;
using Diagnostic = Parity.Model.Diagnostic;
using Parity.Project;

namespace Parity.Passes;

/// <summary>
/// Collects the static facts a slice or a bound model reads by name (#19): const and static readonly literals of
/// project types, and resource texts read through a generated resource class or ResourceManager.GetString("key"),
/// resolved against the targeted project's default-culture resx. They become <c>constants[]</c>, not citable ids.
/// </summary>
public sealed class ConstantCollector
{
    private sealed class Entry
    {
        public required string Kind, Type, Member, ValueType;
        public string? Value;
        public Loc? Declared;
        public List<string> Cultures = new();
        public string? MissingIn; // the default resx the key was looked up in and not found
        public List<(string Owner, Loc Loc, bool Slice)> Uses = new();
    }

    private readonly CSharpCompilation _compilation;
    private readonly ResxIndex _resx;
    private readonly FileIndex _index;
    private readonly HashSet<string> _loadedFiles;
    private readonly string _baseDir;
    public string BaseDir => _baseDir;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public HashSet<string> WantedFiles = new(StringComparer.Ordinal);

    public ConstantCollector(CSharpCompilation compilation, ResxIndex resx, FileIndex index, HashSet<string> loadedFiles, string baseDir)
    {
        _compilation = compilation; _resx = resx; _index = index; _loadedFiles = loadedFiles; _baseDir = baseDir;
    }

    /// <summary>A generated resource class: a static ResourceManager property (Properties.Resources, Captions, ...).</summary>
    public static bool IsResourceClass(INamedTypeSymbol? t) =>
        t != null && t.GetMembers("ResourceManager").OfType<IPropertySymbol>().Any(p => p.IsStatic && IsResourceManager(p.Type));

    private static bool IsResourceManager(ITypeSymbol? t) => Syntax.TypeChain(t).Contains("System.Resources.ResourceManager");

    private bool ProjectLocal(INamedTypeSymbol t) => t.DeclaringSyntaxReferences.Length > 0 || _index.FilesFor(t.Name).Count > 0;

    /// <summary>Every constant and resource read under <paramref name="node"/>, attributed to <paramref name="owner"/>.</summary>
    public void Visit(SyntaxNode node, SemanticModel model, string owner, bool slice)
    {
        foreach (var n in node.DescendantNodesAndSelf())
        {
            if (n is IdentifierNameSyntax id)
            {
                switch (model.GetSymbolInfo(id).Symbol)
                {
                    case IFieldSymbol f when (f.IsConst || (f.IsStatic && f.IsReadOnly)) && f.ContainingType is { TypeKind: not TypeKind.Enum } ft && ProjectLocal(ft):
                        Field(f, owner, Syntax.LocOf(id, _baseDir), slice);
                        break;
                    case IPropertySymbol p when p.IsStatic && p.Type.SpecialType == SpecialType.System_String && IsResourceClass(p.ContainingType):
                        Resource(Syntax.FullName(p.ContainingType), p.ContainingType.Name, p.Name, owner, Syntax.LocOf(id, _baseDir), slice);
                        break;
                }
            }
            else if (n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetString" } ma } inv
                     && inv.ArgumentList.Arguments.Count is 1 or 2
                     && model.GetConstantValue(inv.ArgumentList.Arguments[0].Expression) is { HasValue: true, Value: string key }
                     && IsResourceManager(model.GetTypeInfo(ma.Expression).Type)
                     && ResourceNameOf(ma.Expression, model) is { } res)
            {
                Resource(res.Name, res.Simple, key, owner, Syntax.LocOf(inv, _baseDir), slice);
            }
        }
    }

    /// <summary>The resource a ResourceManager expression reads: Class.ResourceManager, or a local or field created with new (Component)ResourceManager(typeof(T) | "Name", ...).</summary>
    private (string Name, string? Simple)? ResourceNameOf(ExpressionSyntax receiver, SemanticModel model)
    {
        var sym = model.GetSymbolInfo(receiver).Symbol;
        if (sym is IPropertySymbol { Name: "ResourceManager" } p && IsResourceClass(p.ContainingType)) return (Syntax.FullName(p.ContainingType), p.ContainingType.Name);
        if (sym is not (ILocalSymbol or IFieldSymbol)) return null;
        var init = sym.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().Select(v => v.Initializer?.Value).FirstOrDefault();
        if (init is not BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: > 0 } args }) return null;
        var m = init.SyntaxTree == model.SyntaxTree ? model : _compilation.GetSemanticModel(init.SyntaxTree, true);
        if (args[0].Expression is TypeOfExpressionSyntax tof && m.GetTypeInfo(tof.Type).Type is INamedTypeSymbol t && t.TypeKind != TypeKind.Error)
            return (Syntax.FullName(t), t.Name);
        return m.GetConstantValue(args[0].Expression) is { HasValue: true, Value: string name } ? (name, null) : null;
    }

    private void Field(IFieldSymbol f, string owner, Loc use, bool slice)
    {
        var type = Syntax.FullName(f.ContainingType);
        var key = $"field|{type}|{f.Name}";
        if (!_entries.TryGetValue(key, out var e))
        {
            var decl = f.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            object? value = null;
            var known = false;
            if (f.HasConstantValue) { value = f.ConstantValue; known = true; }
            else if (decl?.Initializer != null && _compilation.GetSemanticModel(decl.SyntaxTree, true).GetConstantValue(decl.Initializer.Value) is { HasValue: true } cv) { value = cv.Value; known = true; }
            if (decl == null)
                foreach (var w in _index.FilesFor(f.ContainingType.Name)) if (!_loadedFiles.Contains(w)) WantedFiles.Add(w);
            if (!known) return; // a static readonly without a literal initializer is state, not a static fact
            _entries[key] = e = new Entry
            {
                Kind = f.IsConst ? "const" : "readonly", Type = type, Member = f.Name,
                ValueType = f.Type.ToDisplayString(), Value = f.Type.TypeKind == TypeKind.Enum ? EnumMember(f.Type, value) : Format(value),
                Declared = decl == null ? null : Syntax.LocOf(decl, _baseDir),
            };
        }
        e.Uses.Add((owner, use, slice));
    }

    /// <summary>Records a read of resource <paramref name="key"/> and returns its default-culture text, or null when the key is missing or the resx is not in the project.</summary>
    public string? Resource(string resource, string? simpleName, string key, string owner, Loc use, bool slice)
    {
        var mapKey = $"resource|{resource}|{key}";
        if (!_entries.TryGetValue(mapKey, out var e))
        {
            e = new Entry { Kind = "resource", Type = resource, Member = key, ValueType = "string" };
            if (_resx.Find(resource, simpleName) is { } set)
            {
                if (set.Default.TryGetValue(key, out var entry))
                {
                    e.Value = entry.Value;
                    e.Declared = new Loc(Rel(set.DefaultPath), entry.Line, entry.Line);
                }
                else e.MissingIn = Rel(set.DefaultPath);
                e.Cultures = set.CulturesDefining(key);
            }
            _entries[mapKey] = e;
        }
        e.Uses.Add((owner, use, slice));
        return e.Value;
    }

    private string Rel(string path) => Path.GetRelativePath(_baseDir, path).Replace('\\', '/');

    private static string? EnumMember(ITypeSymbol enumType, object? v) =>
        enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(m => m.HasConstantValue && Equals(m.ConstantValue, v)) is { } m ? $"{enumType.Name}.{m.Name}" : Format(v);

    private static string? Format(object? v) => v switch
    {
        null => null,
        bool b => b ? "true" : "false",
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>The constants of a form, with owner ids rewritten through <paramref name="alias"/>, and a RESOURCE-KEY-MISSING per missing key.</summary>
    public List<ConstantInfo> Freeze(string formName, Func<string, string> alias, List<Diagnostic> diags)
    {
        var list = new List<ConstantInfo>();
        foreach (var e in _entries.Values)
        {
            var first = e.Uses.OrderBy(u => u.Slice ? 0 : 1).ThenBy(u => u.Loc.File, StringComparer.Ordinal).ThenBy(u => u.Loc.Start).First();
            var usedBy = e.Uses.Select(u => alias(u.Owner)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            list.Add(new ConstantInfo(e.Kind, e.Type, e.Member, e.ValueType, e.Value, e.Declared, e.Cultures, first.Loc, usedBy));
            if (e.MissingIn != null)
                diags.Add(new Diagnostic("RESOURCE-KEY-MISSING", $"{formName}.{e.Member}: resource key not found in the default-culture resx of '{e.Type}' ({e.MissingIn}); read by {string.Join(", ", usedBy)}", first.Loc));
        }
        return list.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Member, StringComparer.Ordinal).ToList();
    }
}
