using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Parity.Model;

namespace Parity.Passes;

/// <summary>
/// Canonical token streams: identifiers and literals become placeholders, keywords and punctuation stay.
/// Exact clusters are copies. Near clusters are same-named handlers across forms whose token 3-grams overlap.
/// </summary>
public static class Duplicates
{
    public static string Canonical(string source)
    {
        var tree = CSharpSyntaxTree.ParseText("class __C { " + source + " }");
        var sb = new StringBuilder();
        foreach (var tok in tree.GetRoot().DescendantTokens())
        {
            if (tok.IsKind(SyntaxKind.IdentifierToken)) sb.Append("$id ");
            else if (tok.IsKind(SyntaxKind.StringLiteralToken) || tok.IsKind(SyntaxKind.NumericLiteralToken) || tok.IsKind(SyntaxKind.CharacterLiteralToken)) sb.Append("$lit ");
            else sb.Append(tok.Text).Append(' ');
        }
        var s = sb.ToString();
        // strip the wrapper
        var start = s.IndexOf("{ ", StringComparison.Ordinal) + 2;
        var end = s.LastIndexOf(" } ", StringComparison.Ordinal);
        return end > start ? s[start..end] : s;
    }

    public static string Hash(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static List<DuplicateCluster> Find(IEnumerable<(string Id, string Source)> items, double nearThreshold = 0.6)
    {
        var canon = items.Select(i => (i.Id, Canon: Canonical(i.Source))).Where(i => i.Canon.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12).ToList();
        var clusters = new List<DuplicateCluster>();

        foreach (var g in canon.GroupBy(c => Hash(c.Canon)).Where(g => g.Count() > 1))
            clusters.Add(new DuplicateCluster(g.Key, "exact", g.First().Canon.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList()));

        var exactIds = clusters.SelectMany(c => c.Members).ToHashSet(StringComparer.Ordinal);
        // near: same handler name, different forms
        var byName = canon.Where(c => !exactIds.Contains(c.Id)).GroupBy(c => c.Id[(c.Id.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        foreach (var g in byName)
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var sim = Jaccard(Grams(list[i].Canon), Grams(list[j].Canon));
                    if (sim >= nearThreshold)
                        clusters.Add(new DuplicateCluster($"near:{sim:0.00}", "near", Math.Min(list[i].Canon.Length, list[j].Canon.Length) / 4,
                            new List<string> { list[i].Id, list[j].Id }.OrderBy(x => x, StringComparer.Ordinal).ToList()));
                }
        }
        return clusters.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Members[0], StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> Grams(string canon)
    {
        var toks = canon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < toks.Length; i++) set.Add(toks[i] + " " + toks[i + 1] + " " + toks[i + 2]);
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }
}
