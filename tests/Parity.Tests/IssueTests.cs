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

    [Fact]
    public void issue_24_a_rule_covered_only_by_synthetic_fixtures_is_warned_an_unverified_synthetic_fixture_is_an_error_and_scaffold_labels_its_output()
    {
        // input: the example spec, whose svc:IPolicyService.GetPremiumLimits has one recorded reply (limits-term-life, used by
        // R-001 and R-003). The host cannot be made to return the other outcomes, so they are synthetic replies.
        const string Svc = "svc:IPolicyService.GetPremiumLimits";
        var fx = Path.Combine(_f.ModuleDir, "fixtures");
        var handmade = Path.Combine(fx, "limits-handmade.json");
        var recorded = Path.Combine(fx, "limits-term-life.json");
        var scaffolded = Path.Combine(fx, "limits-term-life.invalid.json");
        var outDir = Path.Combine(_f.OutRoot, "fixtures-24");
        void Handmade(string fields) => File.WriteAllText(handmade, $"{{ \"service\": \"{Svc}\", {fields}, \"request\": {{ \"productId\": 101 }}, \"response\": {{ \"min\": 0, \"max\": 0 }} }}");
        List<Finding> Gate(string path, out FixtureTally? tally) => SpecLinter.Lint(path, gate: true, implementDir: null, out tally);
        List<Finding> Errors(List<Finding> f) => f.Where(x => x.Severity == "error").ToList();
        try
        {
            // a hand-made synthetic reply is the only fixture of the service: today's lint accepted it silently.
            // R-001 lists only it and is warned; R-003 lists it beside the recorded riders-term-life, so it is not
            var only = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("limits-term-life", "limits-handmade"));
            Handmade("\"recorded\": \"synthetic\", \"note\": \"hand-made: a product with no limits\"");
            var f = Gate(only, out var tally);
            Assert.DoesNotContain(f, x => x.Code is "COV-SERVICE" or "FIXTURE-MISSING" or "FIXTURE-INVALID");
            var unverified = Assert.Single(Errors(f));
            Assert.Equal("FIXTURE-SYNTHETIC", unverified.Code);
            Assert.Equal($"limits-handmade.json is synthetic but lacks shapeVerified: true; check its shape against a real reply of {Svc} and say in the note what it stands for", unverified.Message);
            var warned = Assert.Single(f, x => x.Code == "FIXTURE-SYNTHETIC" && x.Severity == "warning");
            Assert.Equal("R-PolicyWizardStep3-001: every fixture it runs on is synthetic (limits-handmade); a test built on them must say it runs on synthetic replies", warned.Message);
            Assert.Equal(new FixtureTally(4, 1), tally);
            Assert.Equal($"1 error(s), {f.Count - 1} warning(s), fixtures: 4 (1 synthetic)", SpecLinter.Summary(f, tally));
            Assert.DoesNotContain(SpecLinter.Lint(only, gate: false, implementDir: null, out var draftTally), x => x.Code.StartsWith("FIXTURE", StringComparison.Ordinal));
            Assert.Equal(tally, draftTally);                                             // counted without --gate too

            // verified and explained, it passes the gate, still warned: a test on it must say so
            Handmade("\"recorded\": \"synthetic\", \"shapeVerified\": true, \"note\": \"hand-made: a product with no limits\"");
            f = Gate(only, out _);
            Assert.Empty(Errors(f));
            Assert.Single(f, x => x.Code == "FIXTURE-SYNTHETIC");
            Handmade("\"recorded\": \"synthetic\", \"shapeVerified\": true, \"note\": \" \"");
            Assert.Contains("lacks a note;", Assert.Single(Errors(Gate(only, out _))).Message);
            Handmade("\"recorded\": \"synthetic\", \"shapeVerified\": \"yes\", \"note\": \"hand-made\"");
            Assert.Equal(new[] { "FIXTURE-INVALID", "FIXTURE-SYNTHETIC" }, Errors(Gate(only, out _)).Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal));

            // recorded is a date or synthetic, nothing else
            foreach (var bad in new[] { "last week", "2026-13-01" })
            {
                Handmade($"\"recorded\": \"{bad}\"");
                var invalid = Assert.Single(Errors(Gate(only, out _)));
                Assert.Equal(("FIXTURE-INVALID", $"limits-handmade.json 'recorded' is '{bad}', neither a YYYY-MM-DD date nor synthetic"), (invalid.Code, invalid.Message));
            }

            // one recorded reply beside the synthetic one: no warning
            Handmade("\"recorded\": \"synthetic\", \"shapeVerified\": true, \"note\": \"hand-made: a product with no limits\"");
            var mixed = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("| limits-term-life |", "| limits-term-life, limits-handmade |"));
            f = Gate(mixed, out tally);
            Assert.DoesNotContain(f, x => x.Code == "FIXTURE-SYNTHETIC");
            Assert.Equal(new FixtureTally(5, 1), tally);

            // the issue's core case: a rule naming only the synthetic reply is warned although its service row carries the recorded one;
            // naming both, it is not
            var ownOnly = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n", "- fixtures: limits-handmade\n"));
            f = Gate(ownOnly, out tally);
            Assert.Empty(Errors(f));
            Assert.Equal("R-PolicyWizardStep3-001: every fixture it runs on is synthetic (limits-handmade); a test built on them must say it runs on synthetic replies",
                Assert.Single(f, x => x.Code == "FIXTURE-SYNTHETIC").Message);
            Assert.Equal(new FixtureTally(5, 1), tally);
            var ownBoth = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n", "- fixtures: limits-term-life, limits-handmade\n"));
            Assert.DoesNotContain(Gate(ownBoth, out _), x => x.Code == "FIXTURE-SYNTHETIC");

            // a rule that declares its outcomes needs a fixture variant per outcome, or a stated gap
            const string Outcomes = "- fixtures: limits-term-life\n- outcomes: valid, invalid, blank\n";
            var declared = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n", Outcomes));
            var gaps = Errors(Gate(declared, out _));
            Assert.All(gaps, x => Assert.Equal("FIXTURE-OUTCOME", x.Code));
            Assert.Equal(new[] { "blank", "invalid", "valid" }.Select(o => $"R-PolicyWizardStep3-001: outcome '{o}' has no listed fixture with variant '{o}' and no 'outcome-gap: {o} - <reason>'"), gaps.Select(x => x.Message));

            // scaffold the invalid reply from the recorded one: labelled, unverified, the request and response copied to edit
            Assert.Equal(scaffolded, FixtureScaffold.Run(Svc, recorded, "invalid", null));
            var copy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(scaffolded))!.AsObject();
            Assert.Equal(new[] { "service", "recorded", "variant", "source", "shapeVerified", "note", "request", "response" }, copy.Select(p => p.Key));
            Assert.Equal((Svc, "synthetic", "invalid", "scaffolded from limits-term-life.json by parity fixture scaffold", false, ""),
                ((string)copy["service"]!, (string)copy["recorded"]!, (string)copy["variant"]!, (string)copy["source"]!, (bool)copy["shapeVerified"]!, (string)copy["note"]!));
            var original = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recorded))!;
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(original["request"], copy["request"]) && System.Text.Json.Nodes.JsonNode.DeepEquals(original["response"], copy["response"]));

            // listed on the rule, the unverified copy is a gate error; the recorded reply is the valid variant and blank is a stated gap
            var varied = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n",
                "- fixtures: limits-term-life, limits-term-life.invalid\n- outcomes: valid, invalid, blank\n- outcome-gap: blank - the host fills limits for every product, so no blank reply exists\n"));
            Assert.Equal(new[] { "FIXTURE-OUTCOME" }, Errors(Gate(varied, out _)).Where(x => x.Message.Contains("outcome '")).Select(x => x.Code));
            File.WriteAllText(recorded, File.ReadAllText(recorded).Replace("\"recorded\": \"2026-10-22\",", "\"recorded\": \"2026-10-22\", \"variant\": \"valid\","));
            f = Gate(varied, out tally);
            var unedited = Assert.Single(Errors(f));
            Assert.Equal(("FIXTURE-SYNTHETIC", $"limits-term-life.invalid.json is synthetic but lacks shapeVerified: true and a note; check its shape against a real reply of {Svc} and say in the note what it stands for"), (unedited.Code, unedited.Message));
            Assert.DoesNotContain(f, x => x.Code == "FIXTURE-SYNTHETIC" && x.Severity == "warning");   // R-001 also has the recorded reply
            Assert.Equal(new FixtureTally(5, 1), tally);
            copy["shapeVerified"] = true;
            copy["note"] = "max below min: the reply a misconfigured product returns";
            File.WriteAllText(scaffolded, copy.ToJsonString());
            Assert.Empty(Errors(Gate(varied, out _)));

            // an outcome is judged by the rule's own fixtures: a variant only on its service row does not count
            var rowVariant = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n", "- fixtures: limits-term-life\n- outcomes: valid, invalid\n")
                .Replace("| limits-term-life |", "| limits-term-life, limits-term-life.invalid |"));
            File.WriteAllText(recorded, File.ReadAllText(recorded).Replace("\"recorded\": \"2026-10-22\",", "\"recorded\": \"2026-10-22\", \"variant\": \"valid\","));   // installing the spec restored the example's copy
            Assert.Equal(new[] { "R-PolicyWizardStep3-001: outcome 'invalid' has no listed fixture with variant 'invalid' and no 'outcome-gap: invalid - <reason>'" },
                Errors(Gate(rowVariant, out _)).Select(x => x.Message));

            // outcome-gap is checked like the rest of the grammar
            var badGap = _f.InstallExampleSpec("PolicyWizardStep3", s => s.Replace("- fixtures: limits-term-life\n", Outcomes + "- outcome-gap: error - no such outcome declared\n- outcome-gap: blank\n"));
            Assert.Equal(new[] { "R-PolicyWizardStep3-001: outcome-gap names 'error', which is not in outcomes", "R-PolicyWizardStep3-001: outcome-gap must be '<outcome> - <reason>'" },
                SpecLinter.Lint(badGap, gate: false, implementDir: null).Where(x => x.Code == "GRAMMAR").Select(x => x.Message));

            // scaffold refuses a synthetic source, a different service and an existing target; --out puts the copy elsewhere
            Assert.Contains("is itself synthetic", Assert.Throws<InvalidOperationException>(() => FixtureScaffold.Run(Svc, scaffolded, "blank", null)).Message);
            Assert.Equal($"limits-term-life.json records {Svc}, not svc:IPolicyService.GetProducts", Assert.Throws<InvalidOperationException>(() => FixtureScaffold.Run("svc:IPolicyService.GetProducts", recorded, "blank", null)).Message);
            Assert.EndsWith("exists; scaffold never overwrites a fixture", Assert.Throws<InvalidOperationException>(() => FixtureScaffold.Run(Svc, recorded, "invalid", null)).Message);
            Assert.Throws<ArgumentException>(() => FixtureScaffold.Run(Svc, recorded, "../blank", null));
            Assert.Equal(Path.Combine(outDir, "limits-term-life.blank.json"), FixtureScaffold.Run(Svc, recorded, "blank", outDir));
            Assert.False(File.Exists(Path.Combine(fx, "limits-term-life.blank.json")));
        }
        finally
        {
            File.Delete(handmade); File.Delete(scaffolded);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            File.Copy(Path.Combine(_f.RepoRoot, "examples", "PolicyWizard", "fixtures", "limits-term-life.json"), recorded, overwrite: true);
        }
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
    public void issue_25_a_re_extract_keeps_the_previous_manifest_and_rebind_rewrites_the_hash_only_when_nothing_cited_changed()
    {
        // input: PolicyWizardStep3 extracted, the example spec (CRLF) bound to that hash, then re-extracted with changed configs
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "rebind");
        var manifests = Path.Combine(cfg.Output, "M", "manifests");
        var current = Path.Combine(manifests, "PolicyWizardStep3.json");
        var prev = Path.Combine(manifests, "PolicyWizardStep3.prev.json");
        Extractor.Run(cfg, "M", "PolicyWizardStep3", new StringWriter());
        var h1 = ManifestHash.Of(current);
        var specDir = Path.Combine(cfg.Output, "M", "spec"); Directory.CreateDirectory(specDir);
        var spec = Path.Combine(specDir, "PolicyWizardStep3.md");
        File.WriteAllText(spec, File.ReadAllText(Path.Combine(_f.RepoRoot, "examples", "PolicyWizard", "spec", "PolicyWizardStep3.md")).Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("sha256:0000000000000000", h1));
        string Bound() => SpecParser.Parse(File.ReadAllText(spec)).Front["manifest"].Value;
        Assert.DoesNotContain(SpecLinter.Lint(spec, gate: false, implementDir: null), f => f.Severity == "error");

        // the same input again: nothing changes, so nothing is moved aside
        Extractor.Run(cfg, "M", "PolicyWizardStep3", new StringWriter());
        Assert.False(File.Exists(prev));
        Assert.Contains("already bound", Rebind.Run(spec, null).Text);

        // a change that touches nothing the spec cites (one diagnostic added): STALE, the old manifest is kept, rebind rewrites the hash
        cfg.FormBaseTypes.Add("LegacyApp.Framework.NoSuchBaseForm");
        Extractor.Run(cfg, "M", "PolicyWizardStep3", new StringWriter());
        Assert.Contains(SpecLinter.Lint(spec, gate: false, implementDir: null), f => f.Code == "STALE");
        Assert.Equal(h1, ManifestHash.Of(prev));
        var diff = ManifestDiff.Compare(ManifestDiff.Load(prev), ManifestDiff.Load(current));
        var only = Assert.Single(diff);
        Assert.Equal(("diagnostics", '+'), (only.Section, only.Kind));
        Assert.StartsWith("BASE-UNRESOLVED: formBaseTypes entry 'LegacyApp.Framework.NoSuchBaseForm'", only.Id);
        Assert.Contains("1 difference(s): 1 added, 0 removed, 0 changed", ManifestDiff.Render(prev, current, diff));
        Assert.Empty(ManifestDiff.Compare(ManifestDiff.Load(current), ManifestDiff.Load(current)));
        var r = Rebind.Run(spec, null);
        Assert.True(r.Rebound, r.Text);
        var h2 = ManifestHash.Of(current);
        Assert.Equal(h2, Bound());
        Assert.Contains("\r\n", File.ReadAllText(spec));                                  // line endings survive the rewrite
        Assert.DoesNotContain(SpecLinter.Lint(spec, gate: false, implementDir: null), f => f.Severity == "error");
        Assert.Equal(new[] { "form:PolicyWizardStep3" }, System.Text.Json.JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(cfg.Output, "M", "module.json")), Json.Options)!.Forms);

        // a cited handler that only moved (a line added above it) is not a change to what the spec says
        File.Copy(current, prev, overwrite: true);
        var moved = ManifestDiff.Load(current);
        moved = moved with { Handlers = moved.Handlers.Select(h => h.Name == "btnNext_Click" ? h with { Loc = h.Loc with { Start = h.Loc.Start + 1, End = h.Loc.End + 1 } } : h).ToList() };
        File.WriteAllText(current, System.Text.Json.JsonSerializer.Serialize(moved, Json.Options) + "\n");
        Assert.Equal("loc", Assert.Single(ManifestDiff.Compare(ManifestDiff.Load(prev), moved)).Fields.Single());
        Assert.True(Rebind.Run(spec, null).Rebound);
        var h3 = Bound();
        Assert.Equal(ManifestHash.Of(current), h3);

        // a rule that cites only the handler still depends on the callee's body: a changed callee blocks the rebind and names the path
        var text6 = File.ReadAllText(spec)
            .Replace("handler:PolicyWizardStep3/btnNext_Click, method:SampleLegacy.Forms.PolicyWizardController.Next(), method:SampleLegacy.Forms.PolicyWizardController.Show(int)", "handler:PolicyWizardStep3/btnNext_Click")
            .Replace("then: svc:IPolicyService.SavePolicyStep is called", "then: `svc:IPolicyService.SavePolicyStep` is called");
        File.WriteAllText(spec, text6);
        File.Copy(current, prev, overwrite: true);
        var callee = ManifestDiff.Load(current);
        callee = callee with { Methods = callee.Methods.Select(x => x.Id == "method:SampleLegacy.Forms.PolicyWizardController.Next()" ? x with { Source = x.Source + " /* changed */" } : x).ToList() };
        File.WriteAllText(current, System.Text.Json.JsonSerializer.Serialize(callee, Json.Options) + "\n");
        Assert.Equal("source", Assert.Single(ManifestDiff.Compare(ManifestDiff.Load(prev), callee)).Fields.Single());
        r = Rebind.Run(spec, null);
        Assert.False(r.Rebound, r.Text);
        Assert.Equal(h3, Bound());
        Assert.Contains("R-PolicyWizardStep3-006 via handler:PolicyWizardStep3/btnNext_Click -> method:SampleLegacy.Forms.PolicyWizardController.Next()\n  method:SampleLegacy.Forms.PolicyWizardController.Next()  changed: source", r.Text.Replace("\r\n", "\n"));
        File.Copy(prev, current, overwrite: true);
        Assert.Equal(h3, ManifestHash.Of(current));

        // a change to what rules cite (IPolicyService no longer a service boundary): handlers changed, svc: ids removed; the hash stays
        cfg.ServiceBoundary.Remove("SampleLegacy.Services.IPolicyService");
        Extractor.Run(cfg, "M", "PolicyWizardStep3", new StringWriter());
        Assert.Equal(h3, ManifestHash.Of(prev));
        r = Rebind.Run(spec, null);
        r = r with { Text = r.Text.Replace("\r\n", "\n") };
        Assert.False(r.Rebound, r.Text);
        Assert.Equal(h3, Bound());
        Assert.Contains("R-PolicyWizardStep3-003\n  cite handler:PolicyWizardStep3/cboProduct_EditValueChanged  changed: calls, serviceCalls\n  then svc:IPolicyService.GetAvailableRiders  removed\n  then svc:IPolicyService.GetPremiumLimits  removed\n", r.Text);
        Assert.Contains("Service calls\n  row svc:IPolicyService.GetAvailableRiders  removed\n", r.Text);
        Assert.Contains("R-PolicyWizardStep3-006\n", r.Text);
        Assert.Contains("  then svc:IPolicyService.SavePolicyStep  removed\n", r.Text);               // a backtick-quoted id resolves
        Assert.DoesNotContain("R-PolicyWizardStep3-004", r.Text);                           // cites nothing that changed
        Assert.Contains($"then set 'manifest: {ManifestHash.Of(current)}'", r.Text);

        // extracted again before rebinding: the kept manifest is no longer the one the spec was written against
        cfg.MaxCallDepth = 1;
        Extractor.Run(cfg, "M", "PolicyWizardStep3", new StringWriter());
        var refused = Assert.Throws<InvalidOperationException>(() => Rebind.Run(spec, null));
        Assert.Contains($"refusing to rebind: the spec was written against {h3}", refused.Message);
        Assert.Contains("--prev", refused.Message);
        Assert.Equal(h3, Bound());
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

    [Fact]
    public void issue_19_constants_and_resource_texts_are_resolved_with_their_values_and_a_missing_key_is_diagnosed()
    {
        // input: PremiumHistoryForm reads PolicyLimits (const, static readonly), Captions (generated resource class and
        // ResourceManager.GetString, default resx plus a fr culture, one key missing), and binds PremiumHistoryRow,
        // whose properties carry [Display], [Browsable(false)], [Required] and [StringLength], to gvHistory via bsHistory
        var m = _f.Manifest("PremiumHistoryForm");
        ConstantInfo C(string member) => Assert.Single(m.Constants, c => c.Member == member);

        var max = C("ReasonMaxLength");
        Assert.Equal(("const", "SampleLegacy.Helpers.PolicyLimits", "int", "40"), (max.Kind, max.Type, max.ValueType, max.Value));
        Assert.Equal(new Loc("Helpers/PolicyLimits.cs", 5, 5), max.Declared);
        Assert.Equal("Forms/PremiumHistoryForm.cs", max.FirstUse!.File);
        Assert.Equal(new[] { "ctrl:PremiumHistoryForm/colReason", "handler:PremiumHistoryForm/btnArchive_Click" }, max.UsedBy); // [StringLength(PolicyLimits.ReasonMaxLength)] too
        var category = C("HistoryCategory");
        Assert.Equal(("readonly", "string", "premium-history"), (category.Kind, category.ValueType, category.Value));

        var reasons = C("lsReasons");                                           // Captions.lsReasons, the generated property
        Assert.Equal(("resource", "SampleLegacy.Resources.Captions", "Reasons are kept for audit"), (reasons.Kind, reasons.Type, reasons.Value));
        Assert.Equal("Resources/Captions.resx", reasons.Declared!.File);
        Assert.Equal(new[] { "fr" }, reasons.Cultures);
        Assert.Equal(new[] { "handler:PremiumHistoryForm/PremiumHistoryForm_Load" }, reasons.UsedBy);
        var title = C("lsHistoryTitle");                                        // Captions.ResourceManager.GetString("lsHistoryTitle")
        Assert.Equal(("resource", "Premium history"), (title.Kind, title.Value));
        Assert.Empty(title.Cultures);                                            // the fr resx does not translate it
        var policyLog = C("Policy log");                                        // Captions.Policy_log: the resx key is the GetString literal, not the identifier
        Assert.Equal(("resource", "Policy log"), (policyLog.Kind, policyLog.Value));
        Assert.Equal("Resources/Captions.resx", policyLog.Declared!.File);
        Assert.DoesNotContain(m.Constants, c => c.Member == "Policy_log");
        Assert.DoesNotContain(m.Methods, x => x.Id.Contains("Captions.lsReasons", StringComparison.Ordinal)); // a fact, not a call
        Assert.DoesNotContain(m.Constants, c => c.Member == "Enter" || c.Type.StartsWith("System.", StringComparison.Ordinal));

        var missing = C("lsArchiveReasonTooLong");
        Assert.Null(missing.Value);
        var d = Assert.Single(m.Diagnostics, x => x.Code == "RESOURCE-KEY-MISSING");
        Assert.Equal("PremiumHistoryForm.lsArchiveReasonTooLong: resource key not found in the default-culture resx of 'SampleLegacy.Resources.Captions' (Resources/Captions.resx); read by handler:PremiumHistoryForm/btnArchive_Click", d.Message);
        Assert.Equal("$form.$id: resource key not found in the default-culture resx of '$id' (<path>.resx); read by handler:$id", Report.AnonMessage(d.Message));

        // the bound model's annotations are column facts next to the designer columns (#15)
        var ctl = m.Controls.ToDictionary(c => c.Field);
        Assert.Equal("SampleLegacy.Models.PremiumHistoryRow", ctl["bsHistory"].Props["@modelType"]);
        Assert.Equal("SampleLegacy.Models.PremiumHistoryRow", ctl["gvHistory"].Props["@modelType"]);
        Assert.Equal("Reason for change", ctl["colReason"].Props["@display"]);
        Assert.Equal("SampleLegacy.Resources.Captions.colReason", ctl["colReason"].Props["@display@resx"]);
        Assert.Equal("true", ctl["colReason"].Props["@required"]);
        Assert.Equal("40", ctl["colReason"].Props["@stringLength"]);
        Assert.Equal("Reason for change", ctl["colReason"].Caption);           // no designer caption: the grid shows the [Display] text
        Assert.Equal("Effective from", ctl["colEffectiveDate"].Props["@display"]);
        Assert.Equal("Amount", ctl["colAmount"].Props["@display"]);
        Assert.Equal("Browsable(false)", ctl["gvHistory"].Props["@model.HistoryId"]); // a property without a designer column
        Assert.Contains(m.Constants, c => c.Member == "colReason" && c.UsedBy.SequenceEqual(new[] { "ctrl:PremiumHistoryForm/colReason" }));

        // rendered: a Constants table, and the literal beside its first use inside the slice
        var md = File.ReadAllText(Path.Combine(_f.ModuleDir, "manifests", "PremiumHistoryForm.md"));
        Assert.Contains("## Constants and resource texts", md);
        Assert.Contains("| const | `SampleLegacy.Helpers.PolicyLimits.ReasonMaxLength` | `40` |", md);
        Assert.Contains("if (row.Reason != null && row.Reason.Length > PolicyLimits.ReasonMaxLength)  // PolicyLimits.ReasonMaxLength = 40", md);
        Assert.Contains("lblReasons.Text = Captions.lsReasons;  // Captions.lsReasons = \"Reasons are kept for audit\"", md);
    }

    [Fact]
    public void issue_19_a_resource_key_with_a_space_resolves_when_only_this_form_is_extracted()
    {
        // input: extract PremiumHistoryForm alone, so Captions is first seen as metadata of the built assembly (no getter source);
        // output: the collector loads Captions.Designer.cs and reads the key "Policy log" from the Policy_log getter, no false RESOURCE-KEY-MISSING
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "issue19-single");
        Extractor.Run(cfg, "M", "PremiumHistoryForm", new StringWriter());
        var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", "PremiumHistoryForm.json")), Json.Options)!;
        var policyLog = Assert.Single(m.Constants, c => c.Member == "Policy log");
        Assert.Equal("Policy log", policyLog.Value);
        Assert.DoesNotContain(m.Constants, c => c.Member == "Policy_log");
        Assert.DoesNotContain(m.Diagnostics, d => d.Code == "RESOURCE-KEY-MISSING" && d.Message.Contains("Policy_log", StringComparison.Ordinal));
        Assert.Single(m.Diagnostics, d => d.Code == "RESOURCE-KEY-MISSING"); // only lsArchiveReasonTooLong
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
