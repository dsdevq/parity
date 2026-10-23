using System.Diagnostics;
using Parity.Lint;
using Parity.Model;
using Parity.Passes;
using Parity.Tools;
using Xunit;

namespace Parity.Tests;

/// <summary>
/// One test per filed issue that the extractor tests do not already pin. Input and expected output are
/// stated in each test; docs/testing.md maps issue numbers to test names.
/// </summary>
[Collection("extract")]
public sealed class IssueTests
{
    private readonly ExtractFixture _f;
    public IssueTests(ExtractFixture f) => _f = f;

    [Fact]
    public void issue_6_probe_matches_a_lowercase_designer_companion_by_exact_form_name()
    {
        // input: BeneficiarySearchForm.cs + BeneficiarySearchForm.designer.cs (lowercase d); glob is the exact class name
        var r = Probe.Run(Path.Combine(_f.RepoRoot, "fixtures", "SampleLegacy", "SampleLegacy.csproj"), "BeneficiarySearchForm");
        Assert.Equal(2, r.MatchedFiles);
        Assert.Equal(1, r.DesignerFiles);
        Assert.Equal(35, r.InitStatements);
        Assert.Contains("this.$id.$id.$id($lit, $lit)", r.Shapes.Keys);   // the SetKeyName shape from issue #1
        Assert.Contains("outputPath: ", r.Text);                              // issue #10.8: where the bin folder is
        Assert.Contains("references: 5 (0 with HintPath)  projectReferences: 1", r.Text);
    }

    [Fact]
    public void issue_10_report_contains_no_identifier_from_the_codebase()
    {
        // input: the manifest of the form with the most private names; output: nothing from it survives except framework names
        var m = _f.Manifest("BeneficiarySearchForm");
        var text = Report.Render(m);
        var privateNames = m.Controls.Select(c => c.Field).Where(f => f != "$this")
            .Concat(m.Handlers.Select(h => h.Name))
            .Concat(m.Methods.Select(x => x.Display))
            .Concat(new[] { "BeneficiarySearchForm", "BaseDialogForm", "SampleLegacy", "Forms/", "btnExport", "person.png" })
            .Distinct().ToList();
        foreach (var name in privateNames)
            Assert.False(text.Contains(name, StringComparison.Ordinal), $"report leaks '{name}':\n{text}");
        Assert.Contains("UNREACHED-METHOD: $form.$id: not wired and not called", text);
        Assert.Contains("GRID-NO-COLUMNS: $form.$id: no column declared in the designer; columns are touched in code: method:$id (<path>.cs:", text);
        Assert.Matches(@"\(<path>\.cs:\d+\)", text);
        Assert.Contains("DevExpress.XtraEditors.XtraForm", text); // framework vocabulary is kept on purpose
    }

    [Fact]
    public void issue_10_report_anonymizes_every_diagnostic_shape_the_extractor_emits()
    {
        // input: one message of every diagnostic shape with private names; output: only $-placeholders and framework names remain
        var cases = new Dictionary<string, string>
        {
            ["PolicyForm.txtPremium: type 'AcmeAdmin.Controls.MoneyEdit' not resolved (missing reference?)"] = "$form.$id: type '$id' not resolved (missing reference?)",
            ["PolicyForm.btnSave: used in InitializeComponent but neither created there, declared as a local, nor found as a field or property on AcmeAdmin.Forms.PolicyForm -> AcmeAdmin.Framework.BaseForm -> DevExpress.XtraEditors.XtraForm"] =
                "$form.$id: used in InitializeComponent but neither created there, declared as a local, nor found as a field or property on $type -> $type -> DevExpress.XtraEditors.XtraForm",
            ["PolicyForm.Save_Click: not wired and not called (checked: designer wiring, += in the 7 loaded files, calls from loaded handlers and methods); called at Forms/Policy/PolicyForm.Logic.cs:120; handler-shaped signature: look for wiring in a user control, a base class, or a file outside this project"] =
                "$form.$id: not wired and not called (checked: designer wiring, += in the 7 loaded files, calls from loaded handlers and methods); called at <path>.cs:120; handler-shaped signature: look for wiring in a user control, a base class, or a file outside this project",
            ["PolicyForm: resource key 'lblClient.Text' not found"] = "$form.$id: resource key '$id' not found".Replace("$form.$id:", "PolicyForm:").Replace("'$id'", "'$id'"),
        };
        foreach (var (input, expected) in cases)
        {
            var actual = Report.AnonMessage(input);
            Assert.DoesNotContain("PolicyForm.", actual);
            Assert.DoesNotContain("AcmeAdmin", actual);
            Assert.DoesNotContain("Forms/Policy", actual);
            if (!expected.StartsWith("PolicyForm:")) Assert.Equal(expected, actual);
        }
        Assert.Equal("<path>.designer.cs", Report.AnonPath("Modules/Policy/Forms/PolicyForm.Designer.cs"));
        Assert.Equal("<path>.cs", Report.AnonPath("Forms\\PolicyForm.cs"));
    }

    [Fact]
    public void issue_8_a_spec_with_crlf_line_endings_lints_identically()
    {
        // input: the example spec converted to CRLF (what core.autocrlf=true produces); output: same gate result as LF
        var lf = SpecLinter.Lint(_f.InstallExampleSpec("PolicyWizardStep3"), gate: true, implementDir: null);
        var crlfPath = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("\n", "\r\n"));
        Assert.Contains("\r\n", File.ReadAllText(crlfPath));
        var crlf = SpecLinter.Lint(crlfPath, gate: true, implementDir: null);
        Assert.Equal(lf.Select(x => x.Code + x.Message), crlf.Select(x => x.Code + x.Message));
        Assert.DoesNotContain(crlf, x => x.Severity == "error");
    }

    [Fact]
    public void issue_10_an_unresolvable_form_base_type_is_a_diagnostic_and_a_glob_that_matches_no_form_explains_itself()
    {
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "basecheck");
        cfg.FormBaseTypes.Add("LegacyApp.Framework.NoSuchBaseForm");
        var log = new StringWriter();
        Extractor.Run(cfg, "M", "DynamicRidersForm", log);
        var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", "DynamicRidersForm.json")), Json.Options)!;
        var d = Assert.Single(m.Diagnostics, x => x.Code == "BASE-UNRESOLVED");
        Assert.Contains("LegacyApp.Framework.NoSuchBaseForm", d.Message);

        // a class that matches the glob but is not a form: the error names it and its bases
        var ex = Assert.Throws<InvalidOperationException>(() => Extractor.Run(cfg, "M", "PolicyWizardController", new StringWriter()));
        Assert.Contains("PolicyWizardController : object", ex.Message);
        Assert.Contains("NoSuchBaseForm", ex.Message);
    }

    [Fact]
    public void issue_11_set_child_index_is_the_control_s_z_order_not_raw()
    {
        // input: ClientSearchForm.Designer.cs ends with this.Controls.SetChildIndex(this.grdResults, 0) and (this.btnSearch, 1)
        var m = _f.Manifest("ClientSearchForm");
        Assert.Empty(m.Raw);
        var c = m.Controls.ToDictionary(x => x.Field);
        Assert.Equal("0", c["grdResults"].Props["ChildIndex"]);
        Assert.Equal("1", c["btnSearch"].Props["ChildIndex"]);
        Assert.False(c["txtSurname"].Props.ContainsKey("ChildIndex"));
    }

    [Fact]
    public void issue_12_module_json_accumulates_across_runs_and_globs_take_a_comma_list()
    {
        // input: two runs into the same module, one form each; output: module.json lists both and finds the cross-form duplicates
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "accumulate");
        Extractor.Run(cfg, "M", "ClientSearchForm", new StringWriter());
        var first = System.Text.Json.JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(cfg.Output, "M", "module.json")), Json.Options)!;
        Assert.Equal(new[] { "form:ClientSearchForm" }, first.Forms);
        Assert.Empty(first.Duplicates);

        var log = new StringWriter();
        Extractor.Run(cfg, "M", "ClaimClientSearchForm", log);
        var second = System.Text.Json.JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(cfg.Output, "M", "module.json")), Json.Options)!;
        Assert.Equal(new[] { "form:ClaimClientSearchForm", "form:ClientSearchForm" }, second.Forms);
        Assert.Contains("svc:IClientService.SearchClients", second.ServiceCalls);
        Assert.Contains(second.Duplicates, d => d.Kind == "near" && d.Members.Contains("handler:ClientSearchForm/btnSearch_Click"));
        Assert.Contains("module.json: 2 form(s) on disk (1 from this run)", log.ToString());

        // a comma-separated glob extracts both in one run
        cfg.Output = Path.Combine(_f.OutRoot, "commaglob");
        var summaries = Extractor.Run(cfg, "M", "PolicyWizardStep3,Dynamic*", new StringWriter());
        Assert.Equal(new[] { "DynamicRidersForm", "PolicyWizardStep3" }, summaries.Select(x => x.Form).OrderBy(x => x));
    }

    [Fact]
    public void issue_13_a_debt_line_acknowledges_one_diagnostic_not_a_whole_code()
    {
        // input: BeneficiarySearchForm has one UNREACHED-METHOD diagnostic; a spec for it with extractor-debt.md variants
        var specDir = Path.Combine(_f.ModuleDir, "spec"); Directory.CreateDirectory(specDir);
        var hash = ManifestHash.Of(Path.Combine(_f.ModuleDir, "manifests", "BeneficiarySearchForm.json"));
        var spec = Path.Combine(specDir, "BeneficiarySearchForm.md");
        File.WriteAllText(spec, $"---\nform: form:BeneficiarySearchForm\nmanifest: {hash}\nstatus: draft\n---\n# x\n## Fields\n## Rules\n## No behavior\n## Service calls\n## Open questions\n- none\n");
        var debt = Path.Combine(_f.ModuleDir, "extractor-debt.md");
        try
        {
            File.WriteAllText(debt, "- UNREACHED-METHOD - all of them, owner someone - until 2026-12-31\n");
            Assert.Contains(SpecLinter.Lint(spec, gate: true, implementDir: null), f => f.Code == "COV-RAW" && f.Message.Contains("UNREACHED-METHOD: BeneficiarySearchForm.btnExport_Click"));

            File.WriteAllText(debt, "- UNREACHED-METHOD: BeneficiarySearchForm.btnExport_Click - export button never wired, owner someone - until 2026-12-31\n");
            var remaining = SpecLinter.Lint(spec, gate: true, implementDir: null).Where(f => f.Code == "COV-RAW").ToList();
            Assert.Single(remaining);                                                   // one line, one diagnostic: the grid one is still open
            Assert.Contains("GRID-NO-COLUMNS: BeneficiarySearchForm.gvHistory", remaining[0].Message);
            File.AppendAllText(debt, "- GRID-NO-COLUMNS: BeneficiarySearchForm.gvHistory - columns built in InitHistoryColumns, owner someone - until 2026-12-31\n");
            Assert.DoesNotContain(SpecLinter.Lint(spec, gate: true, implementDir: null), f => f.Code == "COV-RAW");

            Assert.Equal("TYPE-UNRESOLVED: Form.txtX", SpecLinter.DebtKey("TYPE-UNRESOLVED", "Form.txtX: type 'A.B' not resolved (missing reference?)"));
            Assert.Equal("REF-MISSING: reference not found: C:/x.dll", SpecLinter.DebtKey("REF-MISSING", "reference not found: C:/x.dll"));
        }
        finally { File.Delete(debt); File.Delete(spec); }
    }

    [Fact]
    public void issue_14_non_private_methods_are_entry_points_with_slices_and_their_callees_are_reached()
    {
        // input: BeneficiarySearchForm.Setup(int, bool, ClientDto) is public and called by no loaded file; it calls private InitHistoryColumns
        var m = _f.Manifest("BeneficiarySearchForm");
        var setup = Assert.Single(m.Handlers, h => h.Name == "Setup");
        Assert.Equal("entry", setup.Kind);
        Assert.Equal(new[] { "evt:BeneficiarySearchForm/$this.Setup" }, setup.Events);
        Assert.StartsWith("public void Setup(int policyId, bool allowDeceased, ClientDto preselected)", setup.Source);
        Assert.Contains("ctrl:BeneficiarySearchForm/txtName", setup.Writes);
        Assert.Contains("method:SampleLegacy.Forms.BeneficiarySearchForm.InitHistoryColumns()", setup.Calls);
        Assert.Contains(m.Methods, x => x.Id == "method:SampleLegacy.Forms.BeneficiarySearchForm.InitHistoryColumns()" && x.Source != null);
        Assert.Contains(m.Events, e => e.Id == "evt:BeneficiarySearchForm/$this.Setup" && e.Source == "entry");
        Assert.DoesNotContain(m.Diagnostics, d => d.Code == "UNREACHED-METHOD" && (d.Message.Contains("Setup") || d.Message.Contains("InitHistoryColumns")));
        // the private, never-called handler-shaped method is still diagnosed
        Assert.Contains(m.Diagnostics, d => d.Code == "UNREACHED-METHOD" && d.Message.Contains("btnExport_Click"));
    }

    [Fact]
    public void issue_15_a_grid_view_without_designer_columns_is_a_named_diagnostic_pointing_at_the_code_that_builds_them()
    {
        var m = _f.Manifest("BeneficiarySearchForm");
        var d = Assert.Single(m.Diagnostics, x => x.Code == "GRID-NO-COLUMNS");
        Assert.StartsWith("BeneficiarySearchForm.gvHistory: no column declared in the designer; columns are touched in code: method:SampleLegacy.Forms.BeneficiarySearchForm.InitHistoryColumns() (Forms/BeneficiarySearchForm.cs:", d.Message);
        Assert.Equal("ctrl:BeneficiarySearchForm/gvHistory", m.Controls.Single(c => c.Field == "gvHistory").Id);
        // grids with designer columns are silent
        Assert.DoesNotContain(_f.Manifest("PolicyWizardStep3").Diagnostics, x => x.Code == "GRID-NO-COLUMNS");
        Assert.DoesNotContain(_f.Manifest("ClientSearchForm").Diagnostics, x => x.Code == "GRID-NO-COLUMNS");
    }

    [Fact]
    public void issue_21_a_spec_cites_evidence_outside_the_manifest_and_lint_reports_it_stale_when_the_source_moves()
    {
        // input: BeneficiarySearchForm.Setup shows preselected.Surname; ClientDto (Services/Dto.cs:21) is a model class the manifest
        // does not carry. The legacy root is a copy of the fixture's project directory so the test can move the source under the record.
        var root = Path.Combine(_f.OutRoot, "src-21");
        Directory.CreateDirectory(Path.Combine(root, "Services"));
        File.Copy(Path.Combine(_f.RepoRoot, "fixtures", "SampleLegacy", "SampleLegacy.csproj"), Path.Combine(root, "SampleLegacy.csproj"));
        var dto = Path.Combine(root, "Services", "Dto.cs");
        File.Copy(Path.Combine(_f.RepoRoot, "fixtures", "SampleLegacy", "Services", "Dto.cs"), dto);
        var original = File.ReadAllLines(dto);
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "legacy");
        cfg.Project = Path.Combine(root, "SampleLegacy.csproj");
        cfg.BaseDir = cfg.Output;                                                    // the layout's legacy/: config and .phase side by side
        var phase = Path.Combine(cfg.BaseDir, ".phase");
        var hash = ManifestHash.Of(Path.Combine(_f.ModuleDir, "manifests", "BeneficiarySearchForm.json"));
        var specDir = Path.Combine(_f.ModuleDir, "spec"); Directory.CreateDirectory(specDir);
        var spec = Path.Combine(specDir, "BeneficiarySearchForm.md");
        var evFile = Evidence.PathOf(_f.ModuleDir, "BeneficiarySearchForm");
        const string Answer = "- closed by evidence ev:BeneficiarySearchForm/1: which client field fills txtName? - Surname\n";
        List<Finding> Lint(string cite = "handler:BeneficiarySearchForm/Setup, ev:BeneficiarySearchForm/1", string closed = Answer, bool gate = false)
        {
            File.WriteAllText(spec, $"---\nform: form:BeneficiarySearchForm\nmanifest: {hash}\nstatus: draft\n---\n# x\n## Fields\n## Rules\n### R-BeneficiarySearchForm-001\n- kind: data\n- covers: evt:BeneficiarySearchForm/$this.Setup\n- cite: {cite}\n- given: a hosting form passes a preselected client\n- when: Setup runs\n- then: txtName shows the client's Surname\n## No behavior\n## Service calls\n## Open questions\n{closed}- none\n");
            return SpecLinter.Lint(spec, gate, implementDir: null);
        }
        try
        {
            // specify and implement may read evidence, never the source
            foreach (var p in new[] { "specify", "implement" })
            {
                File.WriteAllText(phase, p + "\n");
                var refused = Assert.Throws<InvalidOperationException>(() => Evidence.AddFromConfig(cfg, null, "BeneficiarySearchForm", "Services/Dto.cs:21-21", null));
                Assert.Equal($"phase '{p}' may read evidence but not the source; record evidence in the extract phase", refused.Message);
            }
            Assert.False(File.Exists(evFile));
            File.WriteAllText(phase, "extract\n");
            var ev = Evidence.AddFromConfig(cfg, null, "BeneficiarySearchForm", "Services/Dto.cs:21-21", "the Surname Setup shows");
            Assert.Equal("ev:BeneficiarySearchForm/1", ev.Id);
            Assert.Equal(("Services/Dto.cs", 21, 21), (ev.File, ev.Start, ev.End));
            Assert.Equal(new[] { original[20] }, ev.Quote);
            Assert.Equal(Evidence.QuoteHash(new[] { original[20] }), ev.QuoteHash);
            Assert.Matches("^sha256:[0-9a-f]{16}$", ev.QuoteHash);
            Assert.Equal("manifest:" + hash, ev.Revision);                           // not a git work tree: the fallback is named
            Assert.Equal("../../../src-21", Evidence.Load(evFile).Root);              // relative to the evidence file, so the pair can move together

            Assert.Empty(Lint());                                                       // cited like a manifest id, and the line still reads the same
            Assert.DoesNotContain(Lint(gate: true), x => x.Code == "OPEN");             // a question closed by evidence no longer blocks the gate
            Assert.Contains(Lint(cite: "handler:BeneficiarySearchForm/Setup, ev:BeneficiarySearchForm/2"), x => x.Code == "CITE-UNKNOWN" && x.Message == "R-BeneficiarySearchForm-001: 'ev:BeneficiarySearchForm/2' is not recorded in evidence/BeneficiarySearchForm.json");
            Assert.Contains(Lint(cite: "ev:BeneficiarySearchForm/1"), x => x.Code == "CITE-KIND");          // evidence supplements a handler, never replaces it
            Assert.Contains(Lint(closed: "- closed by evidence: which client field fills txtName? - Surname\n"), x => x.Code == "EV-UNLINKED");
            Assert.Contains(Lint(closed: "- closed: which client field fills txtName? - Surname\n", gate: true), x => x.Code == "OPEN");   // only evidence closes a question

            // the source moves under the record: every citing line is EV-STALE, naming the record, the file and what moved
            List<string> Stale() => Lint().Where(x => x.Code == "EV-STALE").Select(x => x.Message).ToList();
            File.WriteAllLines(dto, original.Select((l, i) => i == 20 ? l.Replace("Surname", "FamilyName") : l));
            var edited = Stale();
            Assert.Equal(2, edited.Count);                                              // the rule's cite and the closed question
            Assert.StartsWith("R-BeneficiarySearchForm-001: 'ev:BeneficiarySearchForm/1' no longer matches the source: Services/Dto.cs:21-21 changed since manifest:sha256:", edited[0]);
            Assert.EndsWith("; the quoted lines are nowhere in the file", edited[0]);
            File.WriteAllLines(dto, new[] { "// a line inserted above" }.Concat(original));
            Assert.EndsWith("; the quoted lines are now at Services/Dto.cs:22-22", Stale()[0]);
            File.WriteAllLines(dto, original.Take(10));
            Assert.Contains("Services/Dto.cs:21-21 is past the end of Services/Dto.cs, which now has 10 line(s)", Stale()[0]);
            File.Delete(dto);
            Assert.Contains("Services/Dto.cs does not exist under the legacy root", Stale()[0]);
            File.WriteAllLines(dto, original);
            Assert.Empty(Lint());

            // a quote edited by hand no longer proves anything
            var json = File.ReadAllText(evFile);
            File.WriteAllText(evFile, json.Replace("public string Surname", "public string Forename"));
            Assert.Contains(Lint(), x => x.Code == "EV-INVALID");
            File.WriteAllText(evFile, json);

            // in a git work tree the revision is HEAD, marked dirty when the quoted file differs from it
            if (Git(root, "init -q") && Git(root, "add -A") && Git(root, "-c user.name=fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit -q -m fixture"))
            {
                var clean = Evidence.AddFromConfig(cfg, null, "BeneficiarySearchForm", "Services/Dto.cs:20-20", null);
                Assert.Equal("ev:BeneficiarySearchForm/2", clean.Id);
                Assert.Matches("^git:[0-9a-f]{40,64}$", clean.Revision);
                File.AppendAllText(dto, "// uncommitted\n");
                Assert.Matches("^git:[0-9a-f]{40,64}\\+dirty$", Evidence.AddFromConfig(cfg, null, "BeneficiarySearchForm", "Services/Dto.cs:20-20", null).Revision);
            }
        }
        finally { File.Delete(spec); File.Delete(phase); if (Directory.Exists(Path.GetDirectoryName(evFile))) Directory.Delete(Path.GetDirectoryName(evFile)!, true); }
    }

    private static bool Git(string dir, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd(); p.WaitForExit(); err.Wait();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void issue_7_references_load_once_and_each_phase_is_timed()
    {
        Assert.Matches(@"references: \d+ loaded in \d+\.\ds", _f.Log);
        Assert.Matches(@"round 1: \d+ file\(s\), \d+ form\(s\), \d+\.\ds", _f.Log);
        Assert.Matches(@"render\+write: \d+\.\ds", _f.Log);
        Assert.Matches(@"peak-rss-mb: \d+", _f.Log);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(_f.Log, "references: "));
    }

    [Fact]
    public void issue_16_a_rule_id_in_a_todo_skip_comment_or_constant_is_not_a_test()
    {
        // input: the example spec (rules 001..012); 001 and 012 sit in active declarations, the rest only in placeholders
        var dir = Path.Combine(_f.OutRoot, "ng16");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "step3.spec.ts"), string.Join("\n",
            "it('R-PolicyWizardStep3-001 holds', () => {});",
            "it.todo('R-PolicyWizardStep3-002 later');",
            "// R-PolicyWizardStep3-003 is covered somewhere else",
            "it.skip('R-PolicyWizardStep3-004 flaky', () => {});",
            "xit('R-PolicyWizardStep3-005 flaky', () => {});",
            "describe.skip('R-PolicyWizardStep3-006', () => {});",
            "const ids = ['R-PolicyWizardStep3-007'];",
            "test.todo(\"R-PolicyWizardStep3-008 later\");",
            "xdescribe(`R-PolicyWizardStep3-009`, () => {});",
            "test.skip('R-PolicyWizardStep3-010 flaky', () => {});"));
        File.WriteAllText(Path.Combine(dir, "Step3Tests.cs"), string.Join("\n",
            "public class Step3Tests {",
            "  [Fact(Skip = \"later\")] public void R_PolicyWizardStep3_011() {}",
            "  [Fact(DisplayName = \"R-PolicyWizardStep3-012 holds\")] public void Holds() {}",
            "}"));
        var f = SpecLinter.Lint(_f.InstallExampleSpec("PolicyWizardStep3"), gate: true, implementDir: dir);

        var uncovered = f.Where(x => x.Code == "COV-TEST").Select(x => x.Message.Split(' ')[0]).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(2, 10).Select(i => $"R-PolicyWizardStep3-{i:000}"), uncovered);
        Assert.All(f.Where(x => x.Code == "COV-TEST"), x => Assert.Equal("error", x.Severity));

        // the placeholders get their own warning naming the file and the form; comments and constants do not
        var todo = f.Where(x => x.Code == "COV-TODO").ToList();
        Assert.All(todo, x => Assert.Equal("warning", x.Severity));
        var ids = todo.Select(x => x.Message.Split(' ')[0]).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 2, 4, 5, 6, 8, 9, 10, 11 }.Select(i => $"R-PolicyWizardStep3-{i:000}"), ids);
        Assert.Contains(todo, x => x.Message.Contains("step3.spec.ts") && x.Message.Contains("it.todo"));
        Assert.Contains(todo, x => x.Message.Contains("Step3Tests.cs") && x.Message.Contains("Skip"));
    }
}

/// <summary>The guard hook, run through sh. Skipped where no POSIX shell exists (plain Windows without Git Bash).</summary>
public sealed class GuardHookTests
{
    private static string? Sh() => new[] { "/bin/sh", "/usr/bin/sh", "sh" }.FirstOrDefault(p => { try { using var x = Process.Start(new ProcessStartInfo(p, "-c true") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }); x!.WaitForExit(); return x.ExitCode == 0; } catch { return false; } });

    private static (int Code, string Err) Run(string workDir, string stdin, bool hideJq)
    {
        var psi = new ProcessStartInfo(Sh()!, "legacy/hooks/guard-legacy.sh") { WorkingDirectory = workDir, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (hideJq) psi.Environment["PATH"] = Path.Combine(workDir, "nojq");
        psi.Environment.Remove("PARITY_LEGACY_ROOT");
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin); p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        return (p.ExitCode, err);
    }

    [Fact]
    public void issue_9_guard_fails_closed_without_a_root_and_works_without_jq()
    {
        if (Sh() == null) return; // no POSIX shell: the hook cannot run here at all
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Parity.sln"))) dir = Path.GetDirectoryName(dir);
        var work = Path.Combine(Path.GetTempPath(), "parity-hook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(work, "legacy", "hooks"));
        File.Copy(Path.Combine(dir!, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(work, "legacy", "hooks", "guard-legacy.sh"));
        // a PATH with only the core tools and no jq: cat, tr, sed, grep, dirname resolved via a symlink farm
        var nojq = Path.Combine(work, "nojq"); Directory.CreateDirectory(nojq);
        foreach (var tool in new[] { "cat", "tr", "sed", "grep", "dirname", "printf", "head", "sh" })
            foreach (var cand in new[] { "/usr/bin/" + tool, "/bin/" + tool })
                if (File.Exists(cand) && !File.Exists(Path.Combine(nojq, tool))) { File.CreateSymbolicLink(Path.Combine(nojq, tool), cand); break; }
        var legacyRead = "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:\\\\src\\\\Legacy\\\\AcmeAdmin\\\\Forms\\\\X.cs\"}}";
        var manifestRead = "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"/repo/legacy/M/manifests/X.md\"}}";

        File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "extract\n");
        Assert.Equal(0, Run(work, legacyRead, hideJq: true).Code);                        // extract phase: nothing guarded

        File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "specify\n");
        var closed = Run(work, legacyRead, hideJq: true);                                   // no config at all: fail closed
        Assert.Equal(2, closed.Code);
        Assert.Contains("legacy root is unknown", closed.Err);

        File.WriteAllText(Path.Combine(work, "legacy", "extract.config.json"), "{\n  \"project\": \"C:/src/Legacy/AcmeAdmin/AcmeAdmin.csproj\",\n  \"output\": \".\"\n}\n");
        var blocked = Run(work, legacyRead, hideJq: true);                                  // sed fallback finds the root
        Assert.Equal(2, blocked.Code);
        Assert.Contains("off limits", blocked.Err);
        Assert.Equal(0, Run(work, manifestRead, hideJq: true).Code);                       // manifests stay readable
        Assert.Equal(2, Run(work, legacyRead, hideJq: false).Code);                         // and the same with jq if present
        Directory.Delete(work, true);
    }
}
