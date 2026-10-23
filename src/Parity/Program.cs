using System.Diagnostics;
using System.Text.RegularExpressions;
using Parity.Lint;
using Parity.Model;
using Parity.Passes;
using Parity.Project;
using Parity.Render;
using Parity.Tools;

const string Usage = """
parity - reverse-engineer a legacy WinForms module into a citable manifest and gate its spec.

  parity init     [--merge] [--project <legacy.csproj>] [--module <Name>] [--phase-driver [sh|cmd]]
                                                       lay the adopt kit out in this repo (skills, hooks, templates, config skeleton, settings);
                                                       without --merge, files that differ or need merging are reported, not changed
  parity doctor   [--config <extract.config.json>]     one line per setup check (pass, warn, fail); exit 1 on any fail
  parity extract  --config <extract.config.json> --module <Name> --forms <glob>
  parity render   <manifest.json>                      rewrite the .md next to it
  parity lint     <spec.md> [--gate] [--implement <dir>]
  parity evidence add <Unit> <file>:<start>-<end> [--note <text>] [--config <extract.config.json>] [--module <Name>]
                                                       record legacy lines the manifest lacks as ev:<Unit>/<n>; refused in specify and implement
  parity fixture scaffold <svc-id> --from <recorded.json> --variant <name> [--out <dir>]
                                                       write <from>.<variant>.json, a synthetic copy of a recorded reply to edit
  parity probe    <project.csproj> [--forms <glob>]    anonymized statement shapes; paste into an issue
  parity report   <manifest.json>                      anonymized raw/diagnostics/unresolved; review, then paste into an issue
  parity hash     <manifest.json>
  parity diff     <old.json> <new.json>                entries added, removed and changed, by id per section
  parity rebind   <spec.md> [--prev <old.json>]        move a STALE spec to the new manifest when nothing it cites changed

Exit codes: 0 ok, 1 lint errors / manifests differ / rebind left a work list / init has changes waiting for --merge / doctor failed a check, 2 usage, 3 failure (a refused rebind or fixture scaffold too).
""";

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help") { Console.Error.WriteLine(Usage); return 2; }
    var opts = ParseOpts(args.Skip(1).ToArray());
    switch (args[0])
    {
        case "init":
        {
            var phaseDriver = opts.TryGetValue("phase-driver", out var pd) ? pd : null;
            var r = Init.Run(Kit.Load(), new InitOptions(Directory.GetCurrentDirectory(), opts.ContainsKey("merge"), Opt(opts, "project"), Opt(opts, "module"), phaseDriver));
            foreach (var l in r.Lines) Console.WriteLine(l);
            foreach (var n in r.Notes) Console.Error.WriteLine($"note: {n}");
            if (!r.Pending) Console.Error.WriteLine("next: parity doctor");
            return r.Pending ? 1 : 0;
        }
        case "doctor":
        {
            var checks = Doctor.Run(Kit.Load(), Directory.GetCurrentDirectory(), Opt(opts, "config"));
            foreach (var c in checks) Console.WriteLine(c);
            var fails = checks.Count(c => c.Status == "fail");
            Console.Error.WriteLine($"{checks.Count(c => c.Status == "pass")} pass, {checks.Count(c => c.Status == "warn")} warn, {fails} fail");
            return fails > 0 ? 1 : 0;
        }
        case "extract":
        {
            var cfg = ExtractConfig.Load(Opt(opts, "config") ?? "extract.config.json");
            var module = Opt(opts, "module") ?? throw new ArgumentException("--module is required");
            var forms = Opt(opts, "forms") ?? "*";
            var summaries = Extractor.Run(cfg, module, forms, Console.Error);
            Console.WriteLine("form\tcontrols\tevents\thandlers\tmethods\tsvc\tdynamic\traw\tdiag");
            foreach (var s in summaries)
                Console.WriteLine($"{s.Form}\t{s.Controls}\t{s.Events}\t{s.Handlers}\t{s.Methods}\t{s.ServiceCalls}\t{s.DynamicUi}\t{s.Raw}\t{s.Diagnostics}");
            return 0;
        }
        case "render":
        {
            var path = Positional(opts, 0) ?? throw new ArgumentException("manifest.json path required");
            var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(path), Json.Options)!;
            File.WriteAllText(Path.ChangeExtension(path, ".md"), MarkdownRenderer.Render(m, ManifestHash.Of(path)));
            return 0;
        }
        case "hash":
            Console.WriteLine(ManifestHash.Of(Positional(opts, 0) ?? throw new ArgumentException("manifest.json path required")));
            return 0;
        case "diff":
        {
            var a = Positional(opts, 0) ?? throw new ArgumentException("old manifest.json path required");
            var b = Positional(opts, 1) ?? throw new ArgumentException("new manifest.json path required");
            var changes = ManifestDiff.Compare(ManifestDiff.Load(a), ManifestDiff.Load(b));
            Console.Write(ManifestDiff.Render(a, b, changes));
            return changes.Count == 0 ? 0 : 1;
        }
        case "rebind":
        {
            var r = Rebind.Run(Positional(opts, 0) ?? throw new ArgumentException("spec.md path required"), Opt(opts, "prev"));
            Console.Write(r.Text);
            return r.Rebound ? 0 : 1;
        }
        case "lint":
        {
            var path = Positional(opts, 0) ?? throw new ArgumentException("spec.md path required");
            var findings = SpecLinter.Lint(path, opts.ContainsKey("gate"), Opt(opts, "implement"), out var fixtures);
            foreach (var x in findings) Console.WriteLine(x.Severity == "warning" ? $"{x} [warning]" : x.ToString());
            Console.Error.WriteLine(SpecLinter.Summary(findings, fixtures));
            return findings.Any(x => x.Severity == "error") ? 1 : 0;
        }
        case "evidence":
        {
            if (Positional(opts, 0) != "add") throw new ArgumentException("evidence: the subcommand is add");
            var unit = Positional(opts, 1) ?? throw new ArgumentException("evidence add needs <Unit>");
            var loc = Positional(opts, 2) ?? throw new ArgumentException("evidence add needs <file>:<start>-<end>, the file relative to the legacy root");
            var cfgPath = Opt(opts, "config") ?? (File.Exists("legacy/extract.config.json") ? "legacy/extract.config.json" : "extract.config.json");
            var rec = Evidence.AddFromConfig(ExtractConfig.Load(cfgPath), Opt(opts, "module"), unit, loc, Opt(opts, "note"));
            Console.WriteLine($"{rec.Id}\t{rec.File}:{rec.Start}-{rec.End}\t{rec.QuoteHash}\t{rec.Revision}");
            return 0;
        }
        case "fixture":
        {
            if (Positional(opts, 0) != "scaffold") throw new ArgumentException("fixture: the subcommand is scaffold");
            var svc = Positional(opts, 1) ?? throw new ArgumentException("fixture scaffold needs <svc-id>");
            var from = Opt(opts, "from") ?? throw new ArgumentException("fixture scaffold needs --from <recorded.json>");
            var variant = Opt(opts, "variant") ?? throw new ArgumentException("fixture scaffold needs --variant <name>");
            Console.WriteLine(FixtureScaffold.Run(svc, from, variant, Opt(opts, "out")));
            Console.Error.WriteLine($"edit request and response into the {variant} reply, check the shape against a real one, then set shapeVerified: true and write the note");
            return 0;
        }
        case "probe":
        {
            var r = Probe.Run(Positional(opts, 0) ?? throw new ArgumentException("csproj path required"), Opt(opts, "forms") ?? "*");
            Console.Write(r.Text);
            Console.Error.WriteLine($"time: {r.Seconds:0.0}s  peak-rss-mb: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}");
            return 0;
        }
        case "report":
            Console.Write(Report.Render(Positional(opts, 0) ?? throw new ArgumentException("manifest.json path required")));
            Console.Error.WriteLine("review the output before pasting: $id/$lit/$form/$type/<path> mark removed identifiers; anything else is framework vocabulary or the tool's own words");
            return 0;
        default:
            Console.Error.WriteLine(Usage); return 2;
    }
}
catch (ArgumentException ex) { Console.Error.WriteLine($"usage: {ex.Message}"); Console.Error.WriteLine(Usage); return 2; }
catch (Exception ex) { Console.Error.WriteLine($"parity: {ex.Message}"); return 3; }

static Dictionary<string, string> ParseOpts(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    var pos = 0;
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal))
        {
            var k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) d[k] = a[++i]; else d[k] = "";
        }
        else d[$"#{pos++}"] = a[i];
    }
    return d;
}
static string? Opt(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : null;
static string? Positional(Dictionary<string, string> o, int i) => o.TryGetValue($"#{i}", out var v) ? v : null;
