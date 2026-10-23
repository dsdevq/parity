using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;

namespace Parity.Passes;

/// <summary>
/// The shape of a service call for the dependency catalog (#17): its signature, and request and reply templates. A template maps
/// each name to its type; a class declared in the project opens one level into its public properties, a collection becomes a
/// one-element array of its element's template. Types only, never values: the catalog holds no data.
/// </summary>
public static class ServiceShape
{
    public static string Signature(IMethodSymbol m) =>
        $"{m.ReturnType.ToDisplayString()} {m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})";

    public static JsonNode Request(IMethodSymbol m)
    {
        var o = new JsonObject();
        foreach (var p in m.Parameters) o[p.Name] = Of(p.Type, 0);
        return o;
    }

    public static JsonNode? Reply(IMethodSymbol m) => m.ReturnsVoid ? null : Of(m.ReturnType, 0);

    private static JsonNode Of(ITypeSymbol t, int level)
    {
        if (Element(t) is { } e) return new JsonArray(Of(e, level));
        if (level == 0 && t is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } n && n.DeclaringSyntaxReferences.Length > 0)
        {
            var o = new JsonObject();
            foreach (var p in n.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public))
                o[p.Name] = Of(p.Type, level + 1);
            return o;
        }
        return JsonValue.Create(t.ToDisplayString())!;
    }

    private static ITypeSymbol? Element(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return a.ElementType;
        if (t.SpecialType == SpecialType.System_String || t is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } g) return null;
        var name = g.OriginalDefinition.ToDisplayString();
        return name is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.ObjectModel.Collection<T>" or "System.ComponentModel.BindingList<T>"
            ? g.TypeArguments[0] : null;
    }
}
