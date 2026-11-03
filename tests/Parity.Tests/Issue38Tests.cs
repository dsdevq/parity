using System.Diagnostics;
using System.Text;
using Parity.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace Parity.Tests;

/// <summary>Issue #38: the oracle diff on a large nested dataset (a wrapper root, wrapper nodes holding thousands of same-name siblings).</summary>
public sealed class Issue38Tests(ITestOutputHelper output)
{
    /// <summary>
    /// A generated dataset of the reported shape: one wrapper root with two children, the second holding <c>Items</c> wrappers of
    /// thousands of same-name siblings (4372, 1266, 1101, 564, then three <c>Item</c> groups), the first group's rows each with a
    /// repeating child of their own. Deterministic; <paramref name="field"/>, <paramref name="dropLastClause"/> and
    /// <paramref name="headerExtra"/> change it the way a new client might.
    /// </summary>
    internal static string NestedDataset(out int elements, Action<int, int, StringBuilder>? field = null, int dropLastClause = 0, bool headerExtra = false)
    {
        var sb = new StringBuilder();
        var count = 0;
        void Leaf(string name, string value) { sb.Append('<').Append(name).Append('>').Append(value).Append("</").Append(name).Append('>'); count++; }
        sb.Append("<EclipseDataSet>"); count++;
        sb.Append("<Header>"); count++;
        Leaf("PolicyNo", "P-0001"); Leaf("Version", "3");
        if (headerExtra) Leaf("Extra", "x");
        sb.Append("</Header>");
        sb.Append("<Policy>"); count++;
        Leaf("PolicyNo", "P-0001");
        var groups = new (string Name, int Rows)[] { ("Coverage", 4372), ("Rider", 1266), ("Fee", 1101), ("Clause", 564 - dropLastClause), ("Item", 1000), ("Item", 1000), ("Item", 800) };
        for (var g = 0; g < groups.Length; g++)
        {
            sb.Append("<Items>"); count++;
            for (var r = 1; r <= groups[g].Rows; r++)
            {
                sb.Append('<').Append(groups[g].Name).Append($" code=\"{g}-{r}\">"); count++;
                Leaf("Seq", r.ToString());
                Leaf("Name", $"{groups[g].Name} {r}");
                var amount = new StringBuilder($"{r * 7 % 1000}.{r % 100:00}");
                field?.Invoke(g, r, amount);
                Leaf("Amount", amount.ToString());
                Leaf("Start", "2026-01-01");
                Leaf("End", "2026-12-31");
                Leaf("Status", r % 3 == 0 ? "A" : "P");
                Leaf("Flag", r % 2 == 0 ? "true" : "false");
                Leaf("Ref", $"R{g}{r:00000}");
                Leaf("Note", "");
                if (g == 0)
                    for (var p = 1; p <= 2; p++)
                    {
                        sb.Append("<Premium>"); count++;
                        Leaf("Period", p.ToString());
                        Leaf("Value", $"{r + p}.00");
                        sb.Append("</Premium>");
                    }
                sb.Append("</").Append(groups[g].Name).Append('>');
            }
            sb.Append("</Items>");
        }
        sb.Append("</Policy></EclipseDataSet>");
        elements = count;
        return sb.ToString();
    }

    private static DiffResult Compare(string root, RecordingFile l, RecordingFile n, int maxCallMb = OracleDiff.DefaultMaxCallMb) =>
        OracleDiff.Compare(Path.Combine(root, "l"), l, Path.Combine(root, "n"), n, new NormSet(), maxCallMb);

    private static string Root() => Path.Combine(Path.GetTempPath(), "parity-38-" + Guid.NewGuid().ToString("N")[..8]);

    private static List<string> Lines(DiffResult r) => r.Differences.Select(d => $"{d.Part} {d.Kind} {d.Path} {d.Legacy}|{d.New}|{d.Detail}").ToList();

    [Fact]
    public void issue_38_a_nested_dataset_of_127k_elements_diffs_in_seconds_with_each_repeating_sibling_group_compared_as_a_table()
    {
        // input: the reported shape, generated (no real data): a wrapper root, Items wrappers holding groups of 4372, 1266, 1101 and
        // 564 same-name rows, three Item groups, a repeating Premium inside every Coverage; the new side changes one Coverage amount,
        // drops the last Clause and adds a Header field. Before the fix this diff ran for minutes (a 1-row table of ~118k columns
        // compared column against column list, and a sibling rescan per element).
        var legacy = NestedDataset(out var elements);
        Assert.True(elements >= 100_000, $"{elements} elements");
        var root = Root();
        var l = Side(root, "l", legacy);
        var n = Side(root, "n", NestedDataset(out _, (g, r, a) => { if (g == 0 && r == 100) a.Append('1'); }, dropLastClause: 1, headerExtra: true));
        var sw = Stopwatch.StartNew();
        var diff = Compare(root, l, n);
        var seconds = sw.Elapsed.TotalSeconds;
        output.WriteLine($"{elements} elements, {legacy.Length / 1024.0 / 1024.0:0.0} MB, compared in {seconds:0.00}s");
        Assert.True(seconds < 20, $"the diff took {seconds:0.0}s");

        // output: each repeating group a table named by its element path; a row is located by where it sits in the legacy reply
        Assert.Equal(new[]
        {
            "dataset extra Header/Extra |a column of the new reply|the legacy client's reply has this table without the column",
            "dataset changed Policy/Items/Clause rows 564|563|",
            "dataset changed Policy[1]/Items/Coverage[100]/Amount 700.00|700.001|",
        }, Lines(diff));
        Assert.Empty(diff.NotCompared);
        var tables = Payload.Tables(legacy)!;
        Assert.Equal(new[] { "Header 1", "Policy 1", "Policy/Items 7", "Policy/Items/Coverage 4372", "Policy/Items/Coverage/Premium 8744", "Policy/Items/Rider 1266", "Policy/Items/Fee 1101", "Policy/Items/Clause 564", "Policy/Items/Item 2800" },
            tables.Select(t => $"{t.Name} {t.Rows.Count}"));
        Assert.Equal(new[] { "@code", "Seq", "Name", "Amount", "Start", "End", "Status", "Flag", "Ref", "Note" }, tables.Single(t => t.Name == "Policy/Items/Coverage").Rows[0].Keys);
        Assert.Equal("Policy[1]/Items[7]/Item[800]", tables.Single(t => t.Name == "Policy/Items/Item").At[^1]);

        // the export path (redaction rewrites every dataset value) is linear too, and sees the same paths as before
        sw.Restart();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = Payload.RewriteDataset(legacy, (p, v) => { paths.Add(Payload.Unindexed(p)); return p.EndsWith("/Ref", StringComparison.Ordinal) ? "x" : v; }, out var changed);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"the rewrite took {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.Equal(4372 + 1266 + 1101 + 564 + 2800, changed);
        Assert.Contains("Policy/Items/Coverage/Premium/Value", paths);
        Assert.Contains("Policy/Items/Coverage/@code", paths);
        Assert.Contains("<Ref>x</Ref>", rewritten);
    }

    [Fact]
    public void issue_38_a_flat_dataset_compares_as_before_and_a_group_repeating_on_one_side_only_is_a_table_on_both()
    {
        // input: flat WriteXml (one element per row, every column a leaf) and a nested group with one element on one side, three on the other
        var root = Root();
        var flat = Compare(root,
            Side(root, "l", "<NewDataSet><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/><T><A>1</A><B>p</B></T><T><A>2</A><B>q</B></T><U><Z>1</Z></U></NewDataSet>"),
            Side(root, "n", "<NewDataSet><T><A>1</A><B>r</B></T><T><A>2</A><B>s</B></T><T><A>3</A><B>t</B></T></NewDataSet>"));
        // output: the same lines the JSON form of this dataset gives (OracleTests): row count, first differing row per column, U not compared
        Assert.Equal(new[] { "dataset changed T rows 2|3|", "dataset changed T[1]/B p|r|differs in 2 of 2 rows; the first is shown" }, Lines(flat));
        Assert.Equal("#1 Policy/GeneralDetails: U", Assert.Single(flat.NotCompared));

        var one = Compare(root,
            Side(root, "l", "<DS><Order><No>1</No><Lines><Line><Sku>a</Sku></Line></Lines></Order></DS>"),
            Side(root, "n", "<DS><Order><No>1</No><Lines><Line><Sku>a</Sku></Line><Line><Sku>b</Sku></Line><Line><Sku>c</Sku></Line></Lines></Order></DS>"));
        Assert.Equal(new[] { "dataset changed Order/Lines/Line rows 1|3|" }, Lines(one));

        // a nested table the new reply lacks under a top-level table it holds is compared as empty, not skipped (a wrapper that only
        // holds nested rows has no column of its own); a group of leaves is a table of one column, (text)
        var gone = Compare(root,
            Side(root, "l", "<DS><Order><No>1</No><Tag>x</Tag><Tag>y</Tag><Lines><Line><Sku>a</Sku></Line><Line><Sku>b</Sku></Line></Lines></Order></DS>"),
            Side(root, "n", "<DS><Order><No>1</No><Tag>x</Tag><Tag>z</Tag></Order></DS>"));
        Assert.Equal(new[] { "dataset changed Order/Lines/Line rows 2|0|", "dataset changed Order[1]/Tag[2]/(text) y|z|" }, Lines(gone));
    }

    [Fact]
    public void issue_38_a_call_over_the_per_call_size_limit_is_one_difference_naming_the_call_and_the_limit_never_a_silent_skip()
    {
        // input: the nested dataset (about 2.4 MB) on both sides, the limit at 1 MB, then at 0 (none)
        var legacy = NestedDataset(out _);
        var root = Root();
        var l = Side(root, "l", legacy);
        var n = Side(root, "n", legacy);
        var over = Compare(root, l, n, maxCallMb: 1);
        // output: one difference for the call, with both sizes and how to raise the limit; the report shows it and the exit stays 1
        var d = Assert.Single(over.Differences);
        Assert.Equal(("call", "not-compared", 1, "Policy/GeneralDetails"), (d.Part, d.Kind, d.LegacySeq!.Value, d.Call));
        Assert.Matches(@"^2\.\d MB \(largest part: dataset, 2\.\d MB\)$", d.Legacy);
        var report = OracleDiff.Render(over, _ => new());
        Assert.Contains("1. call not compared: #1 Policy/GeneralDetails\n", report);
        Assert.Contains("over the per-call limit of 1 MB on the legacy and the new side, so none of its request, reply, error or dataset was compared; raise the limit with --max-call-mb <n> (0 for none)", report);
        Assert.Empty(Compare(root, l, n, maxCallMb: 0).Differences);
        Assert.Empty(Compare(root, l, n).Differences);
        Assert.Throws<ArgumentException>(() => Compare(root, l, n, maxCallMb: -1));

        // the flag: a value that is not a whole number of MB is a usage error
        var cli = Cli("oracle", "diff", Path.Combine(root, "nowhere"), "scn:x", "--max-call-mb", "lots");
        Assert.Equal(2, cli.Code);
        Assert.Contains("--max-call-mb 'lots' is not a whole number of MB", cli.Err);
    }

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "parity.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, err.Result);
    }

    /// <summary>A one-call recording side whose call returns <paramref name="dataset"/>.</summary>
    internal static RecordingFile Side(string root, string name, string dataset, string key = "Policy/GeneralDetails")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "calls"));
        File.WriteAllText(Path.Combine(dir, "calls", "001.request.xml"), "<GeneralDetails><PolicyNo>P-0001</PolicyNo></GeneralDetails>");
        File.WriteAllText(Path.Combine(dir, "calls", "001.dataset.xml"), dataset);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in new[] { "calls/001.request.xml", "calls/001.dataset.xml" }) files[f] = Recording.Hash(File.ReadAllBytes(Path.Combine(dir, f)));
        var parts = key.Split('/');
        var calls = new List<RecordedCall> { new(1, parts[0], parts[1], false, "calls/001.request.xml", null, "calls/001.dataset.xml", null, null) };
        var r = new RecordingFile(1, "recording", "scn:x", 1, new Provenance("2026-11-03", null, null, "t", new(), 1, "test", Recording.Tool), 0, calls, files, "");
        return r with { Seal = Recording.SealOf(r) };
    }
}
