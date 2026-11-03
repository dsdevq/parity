using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Oracle;

/// <summary>
/// One difference between the legacy client's recorded calls and the new client's replayed ones. Part is <c>call</c> (a call made
/// by one side only, or a pair too large to compare), <c>order</c>, <c>request</c>, <c>reply</c>, <c>dataset</c> or <c>error</c>; Kind
/// is <c>missing</c> (the legacy side has it, the new side does not), <c>extra</c>, <c>changed</c>, <c>reordered</c> or
/// <c>not-compared</c> (the pair is over the per-call size limit). Legacy and New are the values after normalization, null where that
/// side has none (a missing field's new value, an error only one side logged); a missing or extra block's value is its leaves as one
/// text. Fields counts the leaves under a missing or extra block; Leaves lists them, one per field; Detail carries what the line
/// needs besides the values.
/// </summary>
public sealed record Difference(string Part, string Kind, int? LegacySeq, int? NewSeq, string Call, string? Path, string? Legacy, string? New, int Fields, string? Detail, List<Leaf>? Leaves = null);

/// <summary>One field of a missing or extra block: its full path and its value after normalization.</summary>
public sealed record Leaf(string Path, string Value);

/// <summary>
/// One call of the comparison: a recorded call with the capture call it was paired with (null when the new client never makes it),
/// or a capture call no recorded call pairs with (LegacySeq null). NotCompared names the legacy reply tables the pair did not compare.
/// </summary>
public sealed record CallMatch(int? LegacySeq, int? NewSeq, string Call, List<string> NotCompared);

public sealed record DiffResult(string Scenario, RecordingFile Legacy, RecordingFile Replay, NormSet Rules, List<Difference> Differences, List<string> NotCompared, List<CallMatch> Calls);

/// <summary>
/// The comparison: calls are paired by Category/Type and order within it (an alignment per call key, so a skipped call does not
/// shift every pair after it); reads may come in any order between two writes, and nothing may cross a write. A pair is compared
/// on its request and reply fields, its error, and its dataset restricted to the tables the new client's reply holds, all after
/// normalization. A pair whose payload on either side is over the per-call size limit is not compared and is one difference.
/// </summary>
public static class OracleDiff
{
    private const int Skip = 1_000_000;

    /// <summary>The default per-call limit in MB (<c>--max-call-mb</c>): the request, reply and dataset files of one side of one call.</summary>
    public const int DefaultMaxCallMb = 64;

    private sealed class Side
    {
        public required string Dir;
        public required RecordingFile File;
        private readonly Dictionary<int, string> _requests = new();
        public string Request(RecordedCall c) => _requests.TryGetValue(c.Seq, out var t) ? t : _requests[c.Seq] = Recording.Read(Dir, c.Request);
        public string? Reply(RecordedCall c) => c.Reply == null ? null : Recording.Read(Dir, c.Reply);
        public string? Dataset(RecordedCall c) => c.Dataset == null ? null : Recording.Read(Dir, c.Dataset);

        /// <summary>The size of the call's files on disk, in bytes, and the largest part with its size.</summary>
        public (long Total, string Part, long Size) Size(RecordedCall c)
        {
            var parts = new[] { ("request", c.Request), ("reply", c.Reply), ("dataset", c.Dataset) }
                .Select(p => (p.Item1, p.Item2 == null ? 0L : new FileInfo(Path.Combine(Dir, p.Item2)).Length)).ToList();
            var max = parts.MaxBy(p => p.Item2);
            return (parts.Sum(p => p.Item2), max.Item1, max.Item2);
        }
    }

    /// <summary>
    /// The diff of a replay against a recording. <paramref name="maxCallMb"/> bounds one side of one call (request, reply and dataset
    /// files together); a pair over it is reported as not compared, never skipped silently. 0 is no limit.
    /// </summary>
    public static DiffResult Compare(string recordingDir, RecordingFile legacy, string replayDir, RecordingFile replay, NormSet rules, int maxCallMb = DefaultMaxCallMb)
    {
        if (maxCallMb < 0) throw new ArgumentException($"--max-call-mb must be 0 (no limit) or more, got {maxCallMb}");
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
        var unpaired = new List<CallMatch>();
        foreach (var key in legacy.Calls.Select(c => c.Key).Concat(replay.Calls.Select(c => c.Key)).Distinct(StringComparer.Ordinal))
        {
            var ls = legacy.Calls.Where(c => c.Key == key).ToList();
            var ns = replay.Calls.Where(c => c.Key == key).ToList();
            foreach (var (l, n) in Align(ls, ns, (a, b) => ls.Count == ns.Count ? 0 : FieldDiff(Payload.Flatten(L.Request(a)), Payload.Flatten(N.Request(b)), "request", a, b).Count))
            {
                if (l != null && n != null) pairs.Add((l, n));
                else
                {
                    unpaired.Add(new CallMatch(l?.Seq, n?.Seq, key, new()));
                    if (l != null) diffs.Add(new Difference("call", "missing", l.Seq, null, key, null, null, null, 0, "the legacy client makes this call; the new client never does"));
                    else diffs.Add(new Difference("call", "extra", null, n!.Seq, key, null, null, null, 0, "the new client makes this call; the legacy client never does"));
                }
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
        var calls = new List<CallMatch>();
        foreach (var (l, n) in pairs)
        {
            var skippedTables = new List<string>();
            calls.Add(new CallMatch(l.Seq, n.Seq, l.Key, skippedTables));
            if (TooLarge(L, l, N, n, maxCallMb) is { } over) { diffs.Add(over); continue; }
            diffs.AddRange(FieldDiff(Req(L, l), Req(N, n), "request", l, n));
            diffs.AddRange(FieldDiff(Rep(L, l), Rep(N, n), "reply", l, n));
            var le = l.Error == null ? null : M("error", l, "error", l.Error);
            var ne = n.Error == null ? null : M("error", n, "error", n.Error);
            if (!string.Equals(le?.Trim(), ne?.Trim(), StringComparison.Ordinal))
                diffs.Add(new Difference("error", "changed", l.Seq, n.Seq, l.Key, "error", le, ne, 0, null));
            diffs.AddRange(DatasetDiff(L.Dataset(l), N.Dataset(n), l, n, (side, p, v) => M("dataset", side, p, v), notCompared, skippedTables));
        }

        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["call"] = 0, ["order"] = 1, ["request"] = 2, ["reply"] = 3, ["error"] = 4, ["dataset"] = 5 };
        diffs = diffs.OrderBy(d => d.LegacySeq ?? int.MaxValue).ThenBy(d => d.NewSeq ?? 0).ThenBy(d => order[d.Part]).ThenBy(d => d.Path, StringComparer.Ordinal).ToList();
        calls = calls.Concat(unpaired).OrderBy(c => c.LegacySeq ?? int.MaxValue).ThenBy(c => c.NewSeq ?? 0).ToList();
        return new DiffResult(scn, legacy, replay, rules, diffs, notCompared, calls);
    }

    /// <summary>The not-compared difference for a pair whose payload on either side is over the limit; null when both fit.</summary>
    private static Difference? TooLarge(Side L, RecordedCall l, Side N, RecordedCall n, int maxCallMb)
    {
        if (maxCallMb == 0) return null;
        var limit = maxCallMb * 1024L * 1024L;
        var (ls, ns) = (L.Size(l), N.Size(n));
        if (ls.Total <= limit && ns.Total <= limit) return null;
        static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";
        static string Of((long Total, string Part, long Size) s) => $"{Mb(s.Total)} (largest part: {s.Part}, {Mb(s.Size)})";
        return new Difference("call", "not-compared", l.Seq, n.Seq, l.Key, null, Of(ls), Of(ns), 0,
            $"this call is over the per-call limit of {maxCallMb} MB on the {(ls.Total > limit && ns.Total > limit ? "legacy and the new" : ls.Total > limit ? "legacy" : "new")} side, " +
            "so none of its request, reply, error or dataset was compared; raise the limit with --max-call-mb <n> (0 for none) to compare it");
    }

    /// <summary>
    /// The dataset of a pair: tables by name, nested tables (<see cref="Payload.Groups"/>, both sides' groups together) with their
    /// top-level table. Only the top-level tables the new client's reply holds are compared, with every table nested in them; the
    /// legacy reply's other top-level tables are listed as not compared.
    /// </summary>
    private static IEnumerable<Difference> DatasetDiff(string? legacyText, string? newText, RecordedCall l, RecordedCall n, Func<RecordedCall, string, string, string> mask, List<string> notCompared, List<string> skippedTables)
    {
        var lds = Payload.ParseDataset(legacyText);
        var nds = Payload.ParseDataset(newText);
        var groups = Payload.Groups(lds);
        groups.UnionWith(Payload.Groups(nds));
        var lt = lds == null ? null : Payload.Tables(lds, groups, (p, v) => mask(l, p, v));
        var nt = nds == null ? null : Payload.Tables(nds, groups, (p, v) => mask(n, p, v));
        if (lt is { Count: > 0 } && nt is not { Count: > 0 })
        {
            yield return new Difference("dataset", "missing", l.Seq, n.Seq, l.Key, null, $"tables {string.Join(", ", lt.Select(t => t.Top).Distinct())}", null, 0,
                "the legacy client's reply holds a dataset; the new client's capture holds none, so nothing was compared");
            yield break;
        }
        if (nt == null) yield break;
        var lByName = (lt ?? new()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var nByName = nt.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var asked = nt.Select(t => t.Top).ToHashSet(StringComparer.Ordinal);
        if (lt != null)
        {
            var skipped = lt.Select(t => t.Top).Distinct().Where(t => !asked.Contains(t)).ToList();
            if (skipped.Count > 0) notCompared.Add($"#{l.Seq} {l.Key}: {string.Join(", ", skipped)}");
            skippedTables.AddRange(skipped);
        }
        foreach (var t in nt)
        {
            if (lByName.TryGetValue(t.Name, out var lTable)) { foreach (var d in TableDiff(lTable, t, l, n)) yield return d; }
            else if (t.Name == t.Top)
                yield return new Difference("dataset", "extra", l.Seq, n.Seq, l.Key, t.Name, null, $"{t.Rows.Count} row(s)", 0, "the new client's reply holds this table; the legacy client's reply does not");
            else if (lByName.ContainsKey(t.Top))
                foreach (var d in TableDiff(new DataTable(t.Name, new(), new()), t, l, n)) yield return d;
            // else the top-level table is extra as a whole, which says it
        }
        foreach (var t in lt ?? new())
            if (t.Name != t.Top && asked.Contains(t.Top) && !nByName.ContainsKey(t.Name) && nByName.ContainsKey(t.Top))
                foreach (var d in TableDiff(t, new DataTable(t.Name, new(), new()), l, n)) yield return d;
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
        var byRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in has.Order.Where(p => !lacks.Leaves.ContainsKey(p)))
        {
            // the shortest prefix the other side lacks
            var root = p;
            for (var k = p.IndexOf('/'); k >= 0; k = p.IndexOf('/', k + 1))
                if (!lacks.Nodes.Contains(p[..k])) { root = p[..k]; break; }
            if (byRoot.TryGetValue(root, out var leaves)) leaves.Add(p);
            else groups.Add((root, byRoot[root] = new List<string> { p }));
        }
        foreach (var (root, leaves) in groups)
        {
            var block = leaves.Count > 1 || leaves[0] != root;
            var values = block ? string.Join("; ", leaves.Select(x => $"{x} = \"{has.Leaves[x]}\"")) : has.Leaves[leaves[0]];
            yield return new Difference(part, kind, lc.Seq, nc.Seq, lc.Key, root, kind == "missing" ? values : null, kind == "extra" ? values : null, block ? leaves.Count : 0, null,
                leaves.Select(x => new Leaf(x, has.Leaves[x])).ToList());
        }
    }

    /// <summary>
    /// A table: row count, then per column one difference saying in how many rows it differs and the first such row (by where it
    /// sits in the legacy reply). Linear in the cells: columns are sets, and each row is read once.
    /// </summary>
    private static IEnumerable<Difference> TableDiff(DataTable l, DataTable n, RecordedCall lc, RecordedCall nc)
    {
        if (l.Rows.Count != n.Rows.Count)
            yield return new Difference("dataset", "changed", lc.Seq, nc.Seq, lc.Key, $"{l.Name} rows", l.Rows.Count.ToString(), n.Rows.Count.ToString(), 0, null);
        var lCols = l.Rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).ToList();
        var nCols = n.Rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).ToList();
        var lSet = lCols.ToHashSet(StringComparer.Ordinal);
        var nSet = nCols.ToHashSet(StringComparer.Ordinal);
        foreach (var c in lCols.Where(c => !nSet.Contains(c)))
            if (n.Rows.Count > 0) yield return new Difference("dataset", "missing", lc.Seq, nc.Seq, lc.Key, $"{l.Name}/{c}", "a column of the legacy reply", null, 0, "the new client's reply has this table without the column");
        foreach (var c in nCols.Where(c => !lSet.Contains(c)))
            if (l.Rows.Count > 0) yield return new Difference("dataset", "extra", lc.Seq, nc.Seq, lc.Key, $"{l.Name}/{c}", null, "a column of the new reply", 0, "the legacy client's reply has this table without the column");
        var rows = Math.Min(l.Rows.Count, n.Rows.Count);
        var differing = new Dictionary<string, (int Count, int First)>(StringComparer.Ordinal);
        void Differs(string c, int i) => differing[c] = differing.TryGetValue(c, out var d) ? (d.Count + 1, d.First) : (1, i);
        for (var i = 0; i < rows; i++)
        {
            foreach (var (c, v) in l.Rows[i])
                if (nSet.Contains(c) && (!n.Rows[i].TryGetValue(c, out var nv) || nv != v)) Differs(c, i);
            foreach (var c in n.Rows[i].Keys)
                if (lSet.Contains(c) && !l.Rows[i].ContainsKey(c)) Differs(c, i);
        }
        foreach (var c in lCols)
        {
            if (!differing.TryGetValue(c, out var d)) continue;
            yield return new Difference("dataset", "changed", lc.Seq, nc.Seq, lc.Key, $"{l.At[d.First]}/{c}", l.Rows[d.First].GetValueOrDefault(c) ?? "(absent)", n.Rows[d.First].GetValueOrDefault(c) ?? "(absent)", 0,
                d.Count > 1 ? $"differs in {d.Count} of {rows} rows; the first is shown" : null);
        }
    }

    // ---- the report ----

    /// <summary>
    /// The work list, readable without domain knowledge: one summary line, then each difference with the call, the field, both values,
    /// and where the legacy client makes the call (from the svc: ids whose <c>wire</c> names it in registry/dependencies.json).
    /// </summary>
    /// <remarks>
    /// With <paramref name="where"/> null (the standalone diff, which reads no registry) the "legacy client" line is left out, and
    /// <paramref name="note"/>, when given, follows the provenance line.
    /// </remarks>
    public static string Render(DiffResult r, Func<string, List<string>>? where, string? note = null)
    {
        var sb = new StringBuilder();
        sb.Append($"{r.Scenario}: {r.Legacy.Calls.Count} recorded call(s), {r.Replay.Calls.Count} replayed, {r.Differences.Count} difference(s)\n");
        var p = r.Legacy.Provenance;
        sb.Append($"recording: recorded {p.Recorded}" + (p.RecordedBy != null ? $" by {p.RecordedBy}" : "") + (p.Environment != null ? $", {p.Environment}" : "")
                  + (p.LegacyBuild != null ? $", legacy build {p.LegacyBuild}" : "") + $"; rules: {(r.Rules.Rules.Count == 0 ? "none" : "normalization.json" + (r.Rules.Version > 0 ? $" version {r.Rules.Version}" : ""))}");
        if (r.Rules.Masked.Count > 0) sb.Append(" (masked: " + string.Join(", ", r.Rules.Masked.Select(x => $"{x.Key} {x.Value}")) + ")");
        sb.Append('\n');
        if (note != null) sb.Append(note).Append('\n');
        var i = 0;
        foreach (var d in r.Differences)
        {
            i++;
            var at = d.LegacySeq != null ? $"#{d.LegacySeq}" : $"new #{d.NewSeq}";
            var what = (d.Part, d.Kind) switch
            {
                ("call", "missing") => "missing call",
                ("call", "extra") => "extra call",
                ("call", "not-compared") => "call not compared",
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
            string Shown(string v) => d.Part is "request" or "reply" && d.Fields == 0 ? $"\"{v}\"" : v;               // one field quoted; a block's leaves already are
            if (d.Kind == "missing" && d.Part != "call" && d.Legacy != null) sb.Append($"   legacy: {Shown(d.Legacy)}\n   new: (absent)\n");
            else if (d.Kind == "extra" && d.Part != "call" && d.New != null) sb.Append($"   legacy: (absent)\n   new: {Shown(d.New)}\n");
            else if (d.Kind == "changed") sb.Append($"   legacy: \"{d.Legacy ?? "(no error)"}\"\n   new: \"{d.New ?? "(no error)"}\"\n");
            else if (d.Kind == "not-compared") sb.Append($"   legacy: {d.Legacy}\n   new: {d.New}\n");
            if (where == null) continue;
            var callers = where(d.Call);
            sb.Append(callers.Count > 0
                ? "   legacy client: " + string.Join("; ", callers) + "\n"
                : $"   legacy client: no svc: id maps to {d.Call} yet (add \"wire\": [\"{d.Call}\"] to its entry in registry/dependencies.json)\n");
        }
        if (r.NotCompared.Count > 0)
            sb.Append($"\nnot compared (legacy reply tables the new client does not ask for): {string.Join("; ", r.NotCompared)}\n");
        return sb.ToString();
    }

    // ---- the per-call JSON (parity oracle diff --json, docs/oracle.md) ----

    /// <summary>The version of the <c>--json</c> shape; bumped on a change that breaks a consumer, never on an added field.</summary>
    public const int JsonSchema = 1;

    public sealed record JsonReport(int Schema, string Scenario, int RecordedCalls, int ReplayedCalls, int Differences, JsonRecording Recording, JsonReplay Replay, JsonRules Rules, List<JsonCall> Calls);
    public sealed record JsonRecording(string Seal, string Recorded, string? RecordedBy, string? Environment, string? LegacyBuild);
    public sealed record JsonReplay(string Seal);
    public sealed record JsonRules(string Hash, int? Version, SortedDictionary<string, int> Masked);
    public sealed record JsonCall(int? RecordedSeq, int? CaptureSeq, string Call, List<string> NotCompared, List<JsonDifference> Differences);
    public sealed record JsonDifference(string Level, string Kind, string? Path, string? Legacy, string? New, List<Leaf> Leaves, string? Detail);

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,                                                       // a null says "none", so it is written
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,                                                    // read by programs, never put in HTML
    };

    /// <summary>
    /// The machine-readable result: per call, recorded calls in their order and then the capture calls no recorded call pairs with,
    /// each with the sequence on both sides and its differences (none for a call that matched). The values are the report's: read
    /// from the redacted recording and replay, after normalization.
    /// </summary>
    public static JsonReport ToJson(DiffResult r)
    {
        var byCall = r.Differences.ToLookup(d => (d.LegacySeq, d.LegacySeq == null ? d.NewSeq : null));                // a difference belongs to its recorded call, else its capture call
        var calls = r.Calls.Select(c => new JsonCall(c.LegacySeq, c.NewSeq, c.Call, c.NotCompared,
            byCall[(c.LegacySeq, c.LegacySeq == null ? c.NewSeq : null)].Select(d => new JsonDifference(
                d.Part,
                d.Fields > 0 ? $"block {d.Kind}" : d.Kind,
                d.Path, d.Legacy, d.New,
                d.Fields > 0 ? d.Leaves ?? new() : new(),
                d.Detail)).ToList())).ToList();
        var p = r.Legacy.Provenance;
        return new JsonReport(JsonSchema, r.Scenario, r.Legacy.Calls.Count, r.Replay.Calls.Count, r.Differences.Count,
            new JsonRecording(r.Legacy.Seal, p.Recorded, p.RecordedBy, p.Environment, p.LegacyBuild), new JsonReplay(r.Replay.Seal),
            new JsonRules(r.Rules.Hash, r.Rules.Version > 0 ? r.Rules.Version : null, new SortedDictionary<string, int>(r.Rules.Masked, StringComparer.Ordinal)),
            calls);
    }

    public static string RenderJson(DiffResult r) => JsonSerializer.Serialize(ToJson(r), JsonOut).Replace("\r\n", "\n") + "\n";
}
