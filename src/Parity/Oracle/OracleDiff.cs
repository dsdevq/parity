using System.Text;

namespace Parity.Oracle;

/// <summary>
/// One difference between the legacy client's recorded calls and the new client's replayed ones. Part is <c>call</c> (a call made
/// by one side only), <c>order</c>, <c>request</c>, <c>reply</c>, <c>dataset</c> or <c>error</c>; Kind is <c>missing</c> (the
/// legacy side has it, the new side does not), <c>extra</c>, <c>changed</c> or <c>reordered</c>. Fields counts the leaves under a
/// missing or extra block; Detail carries what the line needs besides the values.
/// </summary>
public sealed record Difference(string Part, string Kind, int? LegacySeq, int? NewSeq, string Call, string? Path, string? Legacy, string? New, int Fields, string? Detail);

public sealed record DiffResult(string Scenario, RecordingFile Legacy, RecordingFile Replay, NormSet Rules, List<Difference> Differences, List<string> NotCompared);

/// <summary>
/// The comparison: calls are paired by Category/Type and order within it (an alignment per call key, so a skipped call does not
/// shift every pair after it); reads may come in any order between two writes, and nothing may cross a write. A pair is compared
/// on its request and reply fields, its error, and its dataset restricted to the tables the new client's reply holds, all after
/// normalization.
/// </summary>
public static class OracleDiff
{
    private const int Skip = 1_000_000;

    private sealed class Side
    {
        public required string Dir;
        public required RecordingFile File;
        private readonly Dictionary<int, string> _requests = new();
        public string Request(RecordedCall c) => _requests.TryGetValue(c.Seq, out var t) ? t : _requests[c.Seq] = Recording.Read(Dir, c.Request);
        public string? Reply(RecordedCall c) => c.Reply == null ? null : Recording.Read(Dir, c.Reply);
        public string? Dataset(RecordedCall c) => c.Dataset == null ? null : Recording.Read(Dir, c.Dataset);
    }

    public static DiffResult Compare(string recordingDir, RecordingFile legacy, string replayDir, RecordingFile replay, NormSet rules)
    {
        var L = new Side { Dir = recordingDir, File = legacy };
        var N = new Side { Dir = replayDir, File = replay };
        var scn = legacy.Scenario;
        string M(string target, RecordedCall c, string path, string value) => rules.Mask(target, c.Key, scn, path, value);
        FlatPayload Req(Side s, RecordedCall c) => Payload.Flatten(s.Request(c), (p, v) => M("request", c, p, v));
        FlatPayload Rep(Side s, RecordedCall c) => Payload.Flatten(s.Reply(c), (p, v) => M("reply", c, p, v));

        // 1. pair the calls: per call key, an order-preserving alignment that pairs whenever it can and, when one side has fewer
        //    calls of a key, skips the ones whose requests fit worst
        var pairs = new List<(RecordedCall L, RecordedCall N)>();
        var diffs = new List<Difference>();
        foreach (var key in legacy.Calls.Select(c => c.Key).Concat(replay.Calls.Select(c => c.Key)).Distinct(StringComparer.Ordinal))
        {
            var ls = legacy.Calls.Where(c => c.Key == key).ToList();
            var ns = replay.Calls.Where(c => c.Key == key).ToList();
            foreach (var (l, n) in Align(ls, ns, (a, b) => ls.Count == ns.Count ? 0 : FieldDiff(Payload.Flatten(L.Request(a)), Payload.Flatten(N.Request(b)), "request", a, b).Count))
            {
                if (l != null && n != null) pairs.Add((l, n));
                else if (l != null) diffs.Add(new Difference("call", "missing", l.Seq, null, key, null, null, null, 0, "the legacy client makes this call; the new client never does"));
                else diffs.Add(new Difference("call", "extra", null, n!.Seq, key, null, null, null, 0, "the new client makes this call; the legacy client never does"));
            }
        }
        rules.Masked.Clear();
        pairs = pairs.OrderBy(p => p.L.Seq).ToList();

        // 2. order: a pair whose place relative to a paired write differs between the sides
        var reported = new HashSet<int>();
        foreach (var w in pairs.Where(p => p.L.Write))
            foreach (var c in pairs.Where(p => p.L.Seq != w.L.Seq))
            {
                if (Math.Sign(c.L.Seq - w.L.Seq) == Math.Sign(c.N.Seq - w.N.Seq)) continue;
                var moved = c.L.Write && c.L.Seq < w.L.Seq ? w : c;
                var anchor = moved.L.Seq == c.L.Seq ? w : c;
                if (!reported.Add(moved.L.Seq)) continue;
                var where = moved.L.Seq < anchor.L.Seq ? "before" : "after";
                var other = where == "before" ? "after" : "before";
                diffs.Add(new Difference("order", "reordered", moved.L.Seq, moved.N.Seq, moved.L.Key, null, null, null, 0,
                    $"the legacy client makes this call {where} the write #{anchor.L.Seq} {anchor.L.Key}; the new client makes it {other} (reads may reorder between writes, nothing crosses a write)"));
            }

        // 3. each pair's content
        var notCompared = new List<string>();
        foreach (var (l, n) in pairs)
        {
            diffs.AddRange(FieldDiff(Req(L, l), Req(N, n), "request", l, n));
            diffs.AddRange(FieldDiff(Rep(L, l), Rep(N, n), "reply", l, n));
            var le = l.Error == null ? null : M("error", l, "error", l.Error);
            var ne = n.Error == null ? null : M("error", n, "error", n.Error);
            if (!string.Equals(le?.Trim(), ne?.Trim(), StringComparison.Ordinal))
                diffs.Add(new Difference("error", "changed", l.Seq, n.Seq, l.Key, "error", le ?? "(no error)", ne ?? "(no error)", 0, null));
            var lt = Payload.Tables(L.Dataset(l), (p, v) => M("dataset", l, p, v));
            var nt = Payload.Tables(N.Dataset(n), (p, v) => M("dataset", n, p, v));
            if (lt is { Count: > 0 } && nt is not { Count: > 0 })
                diffs.Add(new Difference("dataset", "missing", l.Seq, n.Seq, l.Key, null, $"tables {string.Join(", ", lt.Select(t => t.Name))}", null, 0,
                    "the legacy client's reply holds a dataset; the new client's capture holds none, so nothing was compared"));
            else if (lt != null)
            {
                var asked = nt?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                var skipped = lt.Where(t => !asked.Contains(t.Name)).Select(t => t.Name).ToList();
                if (skipped.Count > 0) notCompared.Add($"#{l.Seq} {l.Key}: {string.Join(", ", skipped)}");
            }
            foreach (var t in nt ?? new())
            {
                var lTable = lt?.FirstOrDefault(x => x.Name == t.Name);
                if (lTable == null)
                {
                    diffs.Add(new Difference("dataset", "extra", l.Seq, n.Seq, l.Key, t.Name, null, $"{t.Rows.Count} row(s)", 0, "the new client's reply holds this table; the legacy client's reply does not"));
                    continue;
                }
                diffs.AddRange(TableDiff(lTable, t, l, n));
            }
        }

        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["call"] = 0, ["order"] = 1, ["request"] = 2, ["reply"] = 3, ["error"] = 4, ["dataset"] = 5 };
        diffs = diffs.OrderBy(d => d.LegacySeq ?? int.MaxValue).ThenBy(d => d.NewSeq ?? 0).ThenBy(d => order[d.Part]).ThenBy(d => d.Path, StringComparer.Ordinal).ToList();
        return new DiffResult(scn, legacy, replay, rules, diffs, notCompared);
    }

    /// <summary>
    /// Order-preserving alignment of two call lists of one key: equal counts pair in order; otherwise the pairing with the lowest
    /// total cost, a skip costing more than any pair.
    /// </summary>
    private static List<(RecordedCall? L, RecordedCall? N)> Align(List<RecordedCall> ls, List<RecordedCall> ns, Func<RecordedCall, RecordedCall, int> cost)
    {
        int a = ls.Count, b = ns.Count;
        var f = new long[a + 1, b + 1];
        for (var i = 0; i <= a; i++)
            for (var j = 0; j <= b; j++)
            {
                if (i == 0 && j == 0) continue;
                var best = long.MaxValue;
                if (i > 0) best = Math.Min(best, f[i - 1, j] + Skip);
                if (j > 0) best = Math.Min(best, f[i, j - 1] + Skip);
                if (i > 0 && j > 0) best = Math.Min(best, f[i - 1, j - 1] + cost(ls[i - 1], ns[j - 1]));
                f[i, j] = best;
            }
        var path = new List<(RecordedCall?, RecordedCall?)>();
        for (int i = a, j = b; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && f[i, j] == f[i - 1, j - 1] + cost(ls[i - 1], ns[j - 1])) { path.Add((ls[i - 1], ns[j - 1])); i--; j--; }
            else if (i > 0 && f[i, j] == f[i - 1, j] + Skip) { path.Add((ls[i - 1], null)); i--; }
            else { path.Add((null, ns[j - 1])); j--; }
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Leaves compared by path. A run of missing (or extra) leaves under one node the other side lacks is one block difference, not
    /// one per field.
    /// </summary>
    private static List<Difference> FieldDiff(FlatPayload l, FlatPayload n, string part, RecordedCall lc, RecordedCall nc)
    {
        var diffs = new List<Difference>();
        foreach (var p in l.Order.Where(n.Leaves.ContainsKey))
            if (l.Leaves[p] != n.Leaves[p]) diffs.Add(new Difference(part, "changed", lc.Seq, nc.Seq, lc.Key, p, l.Leaves[p], n.Leaves[p], 0, null));
        diffs.AddRange(Blocks(l, n, "missing", part, lc, nc));
        diffs.AddRange(Blocks(n, l, "extra", part, lc, nc));
        return diffs;
    }

    private static IEnumerable<Difference> Blocks(FlatPayload has, FlatPayload lacks, string kind, string part, RecordedCall lc, RecordedCall nc)
    {
        var groups = new List<(string Root, List<string> Leaves)>();
        foreach (var p in has.Order.Where(p => !lacks.Leaves.ContainsKey(p)))
        {
            var root = p;
            var segs = p.Split('/');
            for (var k = 1; k <= segs.Length; k++)
            {
                var prefix = string.Join('/', segs[..k]);
                if (!lacks.Nodes.Contains(prefix)) { root = prefix; break; }
            }
            var g = groups.FirstOrDefault(x => x.Root == root);
            if (g.Root == null) groups.Add((root, new List<string> { p })); else g.Leaves.Add(p);
        }
        foreach (var (root, leaves) in groups)
        {
            var block = leaves.Count > 1 || leaves[0] != root;
            var values = string.Join("; ", leaves.Select(x => block ? $"{x} = \"{has.Leaves[x]}\"" : $"\"{has.Leaves[x]}\""));
            yield return new Difference(part, kind, lc.Seq, nc.Seq, lc.Key, root, kind == "missing" ? values : null, kind == "extra" ? values : null, block ? leaves.Count : 0, null);
        }
    }

    /// <summary>A table: row count, then per column one difference saying in how many rows it differs and the first such row.</summary>
    private static IEnumerable<Difference> TableDiff(DataTable l, DataTable n, RecordedCall lc, RecordedCall nc)
    {
        if (l.Rows.Count != n.Rows.Count)
            yield return new Difference("dataset", "changed", lc.Seq, nc.Seq, lc.Key, $"{l.Name} rows", l.Rows.Count.ToString(), n.Rows.Count.ToString(), 0, null);
        var lCols = l.Rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).ToList();
        var nCols = n.Rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).ToList();
        foreach (var c in lCols.Where(c => !nCols.Contains(c)))
            if (n.Rows.Count > 0) yield return new Difference("dataset", "missing", lc.Seq, nc.Seq, lc.Key, $"{l.Name}/{c}", "a column of the legacy reply", null, 0, "the new client's reply has this table without the column");
        foreach (var c in nCols.Where(c => !lCols.Contains(c)))
            if (l.Rows.Count > 0) yield return new Difference("dataset", "extra", lc.Seq, nc.Seq, lc.Key, $"{l.Name}/{c}", null, "a column of the new reply", 0, "the legacy client's reply has this table without the column");
        var rows = Math.Min(l.Rows.Count, n.Rows.Count);
        foreach (var c in lCols.Where(nCols.Contains))
        {
            var differing = Enumerable.Range(0, rows).Where(i => l.Rows[i].GetValueOrDefault(c) != n.Rows[i].GetValueOrDefault(c)).ToList();
            if (differing.Count == 0) continue;
            var first = differing[0];
            yield return new Difference("dataset", "changed", lc.Seq, nc.Seq, lc.Key, $"{l.Name}[{first + 1}]/{c}", l.Rows[first].GetValueOrDefault(c) ?? "(absent)", n.Rows[first].GetValueOrDefault(c) ?? "(absent)", 0,
                differing.Count > 1 ? $"differs in {differing.Count} of {rows} rows; the first is shown" : null);
        }
    }

    // ---- the report ----

    /// <summary>
    /// The work list, readable without domain knowledge: one summary line, then each difference with the call, the field, both values,
    /// and where the legacy client makes the call (from the svc: ids whose <c>wire</c> names it in registry/dependencies.json).
    /// </summary>
    public static string Render(DiffResult r, Func<string, List<string>> where)
    {
        var sb = new StringBuilder();
        sb.Append($"{r.Scenario}: {r.Legacy.Calls.Count} recorded call(s), {r.Replay.Calls.Count} replayed, {r.Differences.Count} difference(s)\n");
        var p = r.Legacy.Provenance;
        sb.Append($"recording: recorded {p.Recorded}" + (p.RecordedBy != null ? $" by {p.RecordedBy}" : "") + (p.Environment != null ? $", {p.Environment}" : "")
                  + (p.LegacyBuild != null ? $", legacy build {p.LegacyBuild}" : "") + $"; rules: {(r.Rules.Rules.Count == 0 ? "none" : "normalization.json" + (r.Rules.Version > 0 ? $" version {r.Rules.Version}" : ""))}");
        if (r.Rules.Masked.Count > 0) sb.Append(" (masked: " + string.Join(", ", r.Rules.Masked.Select(x => $"{x.Key} {x.Value}")) + ")");
        sb.Append('\n');
        var i = 0;
        foreach (var d in r.Differences)
        {
            i++;
            var at = d.LegacySeq != null ? $"#{d.LegacySeq}" : $"new #{d.NewSeq}";
            var what = (d.Part, d.Kind) switch
            {
                ("call", "missing") => "missing call",
                ("call", "extra") => "extra call",
                ("order", _) => "call order",
                ("dataset", "missing") when d.Path == null => "dataset missing",
                (_, "missing") when d.Fields > 0 => $"{d.Part} block missing",
                (_, "extra") when d.Fields > 0 => $"{d.Part} block extra",
                (_, "missing") => $"{d.Part} field missing",
                (_, "extra") => $"{d.Part} field extra",
                _ => $"{d.Part} differs",
            };
            sb.Append($"\n{i}. {what}: {at} {d.Call}" + (d.Path != null ? $" {d.Path}" + (d.Fields > 0 ? $" ({d.Fields} fields)" : "") : "") + "\n");
            if (d.Detail != null) sb.Append($"   {d.Detail}\n");
            if (d.Kind == "missing" && d.Part != "call" && d.Legacy != null) sb.Append($"   legacy: {d.Legacy}\n   new: (absent)\n");
            else if (d.Kind == "extra" && d.Part != "call" && d.New != null) sb.Append($"   legacy: (absent)\n   new: {d.New}\n");
            else if (d.Kind == "changed") sb.Append($"   legacy: \"{d.Legacy}\"\n   new: \"{d.New}\"\n");
            var callers = where(d.Call);
            sb.Append(callers.Count > 0
                ? "   legacy client: " + string.Join("; ", callers) + "\n"
                : $"   legacy client: no svc: id maps to {d.Call} yet (add \"wire\": [\"{d.Call}\"] to its entry in registry/dependencies.json)\n");
        }
        if (r.NotCompared.Count > 0)
            sb.Append($"\nnot compared (legacy reply tables the new client does not ask for): {string.Join("; ", r.NotCompared)}\n");
        return sb.ToString();
    }
}
