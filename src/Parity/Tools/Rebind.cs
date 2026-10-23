using System.Text;
using System.Text.RegularExpressions;
using Parity.Lint;
using Parity.Passes;

namespace Parity.Tools;

public sealed record RebindResult(bool Rebound, string Text);

/// <summary>
/// Moves a STALE spec to the re-extracted manifest when nothing it cites changed (issue #25). The diff runs between
/// the manifest the spec was written against (<c>manifests/&lt;Form&gt;.prev.json</c>, or <c>--prev</c>) and the one on
/// disk. A citation is affected when its id vanished or changed in more than its location: a rule's covers, cite and
/// the ctrl:/dyn:/svc: ids in its then, and the ids of the Fields, No behavior and Service calls rows. Nothing affected:
/// the manifest line is rewritten in place. Anything affected: the hash stays and the work list is printed.
/// New evt:/dyn: ids the spec does not mention are listed either way; the gate asks for them.
/// </summary>
public static partial class Rebind
{
    [GeneratedRegex(@"\b(?:ctrl|dyn|svc):[^\s,;]+")] private static partial Regex ThenId();

    public static RebindResult Run(string specPath, string? prevPath)
    {
        var text = File.ReadAllText(specPath);
        var spec = SpecParser.Parse(text);
        var formId = spec.Front.TryGetValue("form", out var fv) ? fv.Value : null;
        if (formId == null || !formId.StartsWith("form:", StringComparison.Ordinal)) throw new InvalidOperationException($"{specPath}: front matter needs 'form: form:<Name>'");
        if (!spec.Front.TryGetValue("manifest", out var mv)) throw new InvalidOperationException($"{specPath}: front matter has no 'manifest:' line; there is nothing to rebind");
        var formName = formId[5..];
        var manifests = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".", "..", "manifests");
        var currentPath = Path.GetFullPath(Path.Combine(manifests, formName + ".json"));
        if (!File.Exists(currentPath)) throw new InvalidOperationException($"manifest not found at {Rel(currentPath)}; run parity extract");
        var current = ManifestHash.Of(currentPath);
        if (mv.Value == current) return new RebindResult(true, $"{Rel(specPath)}: already bound to {current}; nothing to do\n");

        prevPath = Path.GetFullPath(prevPath ?? Path.Combine(manifests, formName + ".prev.json"));
        var how = $"recover the manifest the spec was written against (for instance git show <commit>:<path to {formName}.json> > old.json) and run parity rebind {Rel(specPath)} --prev old.json";
        if (!File.Exists(prevPath)) throw new InvalidOperationException($"refusing to rebind: no previous manifest at {Rel(prevPath)} (extract keeps one generation, from the first re-extract that changes the manifest); {how}");
        var prev = ManifestHash.Of(prevPath);
        if (prev != mv.Value) throw new InvalidOperationException($"refusing to rebind: the spec was written against {mv.Value} but {Rel(prevPath)} is {prev} (extracted more than once since the spec was bound?); {how}");

        var changes = ManifestDiff.Compare(ManifestDiff.Load(prevPath), ManifestDiff.Load(currentPath));
        var hit = changes.Where(c => c.Significant).GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // work list: per rule, then per table, every cited id that vanished or changed
        var work = new List<(string Owner, List<string> Lines)>();
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in spec.Rules)
        {
            var cites = SpecParser.SplitIds(r.Get("covers")).Select(id => ("covers", id))
                .Concat(SpecParser.SplitIds(r.Get("cite")).Select(id => ("cite", id)))
                .Concat(ThenId().Matches(r.Get("then") ?? "").Select(m => ("then", m.Value.TrimEnd('.', ')', ':', '"'))))
                .Distinct().ToList();
            foreach (var (_, id) in cites) mentioned.Add(id);
            Collect(r.Id, cites, hit, work);
        }
        foreach (var (table, rows) in new[] { ("Fields", spec.Fields), ("No behavior", spec.NoBehavior), ("Service calls", spec.ServiceCalls) })
        {
            var cites = rows.Select(row => ("row", row.Cell(0).Trim('`'))).Distinct().ToList();
            foreach (var (_, id) in cites) mentioned.Add(id);
            Collect(table, cites, hit, work);
        }
        var uncovered = changes.Where(c => c.Kind == '+' && c.Section is ("events" or "dynamicUi") && !mentioned.Contains(c.Id)).Select(c => c.Id).ToList();

        var sb = new StringBuilder();
        var rebound = work.Count == 0;
        if (rebound)
        {
            var lines = text.Split('\n');
            lines[mv.Line - 1] = lines[mv.Line - 1].Replace(mv.Value, current, StringComparison.Ordinal);
            File.WriteAllText(specPath, string.Join('\n', lines));
            sb.AppendLine($"rebound {Rel(specPath)}: {mv.Value} -> {current}; {changes.Count} manifest difference(s), none in an id the spec cites (parity diff {Rel(prevPath)} {Rel(currentPath)} lists them)");
        }
        else
        {
            sb.AppendLine($"not rebound {Rel(specPath)}: {work.Count} rule(s) or table(s) cite ids that changed or vanished between {mv.Value} and {current}.");
            sb.AppendLine($"Re-read each in manifests/{formName}.md, update the spec, then set 'manifest: {current}'. parity diff {Rel(prevPath)} {Rel(currentPath)} shows every difference.");
            foreach (var (owner, ls) in work)
            {
                sb.AppendLine(owner);
                foreach (var l in ls) sb.AppendLine(l);
            }
        }
        if (uncovered.Count > 0)
        {
            sb.AppendLine("new ids the spec does not mention yet (the gate asks for them):");
            foreach (var id in uncovered) sb.AppendLine("  + " + id);
        }
        return new RebindResult(rebound, sb.ToString());
    }

    private static void Collect(string owner, List<(string Where, string Id)> cites, Dictionary<string, EntryChange> hit, List<(string, List<string>)> work)
    {
        var lines = cites.Where(c => hit.ContainsKey(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => $"  {c.Where} {c.Id}  " + (hit[c.Id].Kind == '-' ? "removed" : "changed: " + string.Join(", ", hit[c.Id].Fields))).ToList();
        if (lines.Count > 0) work.Add((owner, lines));
    }

    private static string Rel(string p) => Path.GetRelativePath(Environment.CurrentDirectory, p).Replace('\\', '/');
}
