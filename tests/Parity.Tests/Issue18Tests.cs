using System.Text.Json;
using Parity.Lint;
using Parity.Model;
using Parity.Passes;
using Xunit;

namespace Parity.Tests;

/// <summary>Issue #18: the pilot acceptance run - one unit with variants, hosts and replies taken through extract, map and the spec gate.</summary>
public sealed partial class IssueTests
{
    [Fact]
    public void issue_18_a_unit_with_two_variants_two_hosts_and_recorded_and_synthetic_replies_passes_extract_map_and_the_gate_with_no_side_files()
    {
        // input: PayeeLookupPanel (Larkspur and Thistle variants handed out by PayeeLookupPanelFactory), embedded by ClaimPayoutForm
        // on Load and by RefundPayoutForm on a click, each setting its inputs; it calls GetPayeeAccounts and VerifyPayoutAccount.
        // examples/Payouts holds the reviewed registry, the fixtures (two recorded, one synthetic) and the spec.
        // output: a green registry check and spec gate, nothing written outside the module's manifests, module.json, registry,
        // spec and fixtures, and every context question the pilot asked answered by those files
        var root = Path.Combine(_f.OutRoot, "issue18");
        var example = Path.Combine(_f.RepoRoot, "examples", "Payouts");
        var legacy = Path.Combine(_f.RepoRoot, "fixtures");
        var sourcesBefore = Snapshot(legacy, example);

        // extract
        var cfg = ExtractConfig.Load(Path.Combine(legacy, "extract.config.json"));
        cfg.Output = Path.Combine(root, "legacy");
        var summaries = Extractor.Run(cfg, "Payouts", "PayeeLookupPanel,ClaimPayoutForm,RefundPayoutForm", new StringWriter());
        Assert.Equal(new[] { "ClaimPayoutForm", "PayeeLookupPanel", "RefundPayoutForm" }, summaries.Select(s => s.Form).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(summaries, s => Assert.Equal((0, 0), (s.Raw, s.Diagnostics)));
        var dir = Path.Combine(cfg.Output, "Payouts");

        // map: the reviewed registry and fixtures go in, then the stamp anchors the registry to this extract
        foreach (var sub in new[] { "fixtures", "registry", "spec" })
        {
            Directory.CreateDirectory(Path.Combine(dir, sub));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(example, sub)))
                File.Copy(f, Path.Combine(dir, sub, Path.GetFileName(f)));
        }
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "REGISTRY-STALE");
        Registry.Stamp(dir);
        Assert.Empty(Registry.Check(dir, gate: true));

        // the spec gate
        var spec = Path.Combine(dir, "spec", "PayeeLookupPanel.md");
        File.WriteAllText(spec, File.ReadAllText(spec).Replace("sha256:0000000000000000", ManifestHash.Of(Path.Combine(dir, "manifests", "PayeeLookupPanel.json"))));
        var findings = SpecLinter.Lint(spec, gate: true, implementDir: null, out var tally);
        Assert.Empty(findings);
        Assert.Equal(new FixtureTally(3, 1), tally);

        // no side files: the run wrote only the module's own folders, and the legacy sources and the example are untouched
        var written = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).ToList();
        Assert.All(written, p => Assert.True(p == "module.json" || new[] { "manifests/", "registry/", "spec/", "fixtures/" }.Any(d => p.StartsWith(d, StringComparison.Ordinal)), $"side file {p}"));
        Assert.DoesNotContain(written, p => p.EndsWith(".prev.json", StringComparison.Ordinal));
        Assert.Equal(sourcesBefore, Snapshot(legacy, example));

        // the context questions, each answered from the manifests and the registries alone
        FormManifest Unit(string name) => JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(dir, "manifests", name + ".json")), Json.Options)!;
        var panel = Unit("PayeeLookupPanel");
        var module = JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(dir, "module.json")), Json.Options)!;
        var decisions = Registry.Decisions(dir);

        // who hosts the unit, and how: two hosts, each adds it to a container
        var hosts = Assert.Single(module.Composition!, c => c.Unit == "form:PayeeLookupPanel").Hosts;
        Assert.Equal(new[] { "form:ClaimPayoutForm", "form:RefundPayoutForm" }, hosts.Select(h => h.Unit));
        Assert.All(hosts, h => Assert.Equal("embedded", h.Mode));

        // which tenant gets which variant: each host goes through the factory, and its cases name every variant and the base
        var factory = Assert.Single(panel.Factories!);
        Assert.All(hosts, h => Assert.Equal(factory.Id, h.Factory));
        Assert.Equal(panel.Variants!.Select(v => v.Name).Append("base").Order(StringComparer.Ordinal), factory.Cases.Select(c => c.Variant!).Order(StringComparer.Ordinal));
        Assert.All(hosts, h => Assert.Equal(factory.Cases.Select(c => $"{c.Key} -> {c.Variant}"), h.Variants!));

        // what each host sets: every contract input is set by each host, or the host's silence is a recorded decision
        var contract = panel.Contract!;
        var entry = Assert.Single(contract.Entries, e => e.Kind == "entry");
        var embeds = new[] { "ClaimPayoutForm", "RefundPayoutForm" }.Select(h => Assert.Single(Unit(h).Embeds!)).ToList();
        foreach (var e in embeds)
            foreach (var input in contract.Inputs)
            {
                var set = input.Kind == "param"
                    ? e.Calls.Any(c => c.Member == input.Name.Split('.')[0] && c.Args.Count == contract.Inputs.Count(i => i.Kind == "param"))
                    : e.Sets.Any(s => s.Member == input.Name);
                Assert.True(set || decisions.Any(d => d.Cite.Contains(e.Id) && d.Question.Contains(input.Name, StringComparison.Ordinal)),
                    $"{e.Id} neither sets {input.Name} nor has a decision about it");
            }
        Assert.Equal("handler:PayeeLookupPanel/Setup", entry.Handler);

        // what the hosts do with the outputs: each output is read by a host
        Assert.All(contract.Outputs.Where(o => o.Kind == "property"), o => Assert.True(embeds.Any(e => e.Reads.Any(r => r.Member == o.Name)), $"no host reads {o.Name}"));

        // which services, called how, failing how: each has its callers and arguments, a recorded reply, its error cases backed
        // by a fixture, and every synthetic reply is shape-checked
        var catalog = Registry.Catalog(dir);
        Assert.Equal(module.ServiceCalls, catalog.Select(c => c.Id));
        Assert.All(catalog, c =>
        {
            Assert.All(c.Callers, k => Assert.NotEmpty(k.Args));
            Assert.Contains(c.Fixtures, x => x.Provenance == "recorded" && x.Date != null);
            Assert.All(c.Errors, x => Assert.Contains(c.Fixtures, fx => fx.Name == x.Fixture));
            Assert.All(c.Fixtures.Where(x => x.Provenance == "synthetic"), x => Assert.True(x.ShapeVerified));
        });

        // what is still open: the one question the spec raised is closed by a decision on record, and nothing else is open
        var open = File.ReadAllLines(spec).SkipWhile(l => l != "## Open questions").Skip(1).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        Assert.Equal("- none", open[^1]);
        Assert.All(open.SkipLast(1), q => Assert.Contains(decisions, d => q.StartsWith($"- closed by decision {d.Id}", StringComparison.Ordinal)));
    }

    /// <summary>Every file under the given roots with its size and last write, bin and obj left out.</summary>
    private static List<string> Snapshot(params string[] roots) => roots
        .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
        .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
        .Select(p => $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p):O}")
        .Order(StringComparer.Ordinal).ToList();
}
