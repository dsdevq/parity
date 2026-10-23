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

    private static string HookCall(string tool, object input, string cwd) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["cwd"] = cwd, ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input });

    [Fact]
    public void issue_22_guard_allow_lists_readable_roots_denies_code_index_tools_and_logs_every_denial()
    {
        // input: the guard run through sh with hook JSON on stdin, the legacy tree a sibling of the consuming repo;
        // output: exit 0 for the manifest/spec/fixtures/evidence folders, exit 2 (and one log line each) for everything else
        var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_f.RepoRoot, "adopt", ".claude", "settings.json")));
        var matcher = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString()!;
        foreach (var tool in new[] { "Read", "Grep", "Glob", "Bash", "PowerShell", "Edit", "Write", "MultiEdit", "NotebookEdit", "Monitor", "mcp__codegraph__get_symbol", "mcp__docs__search" })
            Assert.Matches(matcher, tool);
        Assert.DoesNotMatch(matcher, "TodoWrite");
        Assert.DoesNotMatch(matcher, "WebSearch");

        if (GuardHookTests.Sh() == null) return; // no POSIX shell: the hook cannot run here at all
        var baseDir = Path.Combine(Path.GetTempPath(), "parity-guard-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(baseDir, "repo");
        var legacy = Path.Combine(baseDir, "Legacy", "AcmeAdmin");
        Directory.CreateDirectory(Path.Combine(work, "legacy", "hooks"));
        Directory.CreateDirectory(Path.Combine(legacy, "Forms"));
        Directory.CreateDirectory(Path.Combine(baseDir, "shared"));
        File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(work, "legacy", "hooks", "guard-legacy.sh"));
        var config = Path.Combine(work, "legacy", "extract.config.json");
        File.WriteAllText(config, "{\n  \"project\": \"" + legacy.Replace('\\', '/') + "/AcmeAdmin.csproj\",\n  \"output\": \".\"\n}\n");
        var log = Path.Combine(work, "legacy", ".guard-denials.log");
        try
        {
            var legacyForm = Path.Combine(legacy, "Forms", "X.cs");
            // calls a restricted phase must let through: the manifest, spec, fixtures and evidence folders, by relative and absolute path
            var allowed = new (string Tool, object Input, string Cwd)[]
            {
                ("Read", new { file_path = "legacy/M/manifests/X.md" }, work),
                ("Read", new { file_path = Path.Combine(work, "legacy", "M", "spec", "X.md") }, work),
                ("Read", new { file_path = "spec/X.md" }, Path.Combine(work, "legacy", "M")),
                ("Edit", new { file_path = "legacy/M/spec/X.md", old_string = "a", new_string = "b" }, work),
                ("Grep", new { pattern = "Rule", path = "legacy/M/fixtures" }, work),
                ("Grep", new { pattern = "Rule" }, work),
                ("Glob", new { pattern = "legacy/M/evidence/**/*.png" }, work),
                ("Glob", new { pattern = "**/*.md", path = "legacy/M/manifests" }, work),
                ("Bash", new { command = "cat legacy/M/manifests/X.md | head -5 2>/dev/null" }, work),
                ("Bash", new { command = "cd legacy/M && ls fixtures evidence && dotnet test --filter \"Name=Rule\"" }, work),
                ("PowerShell", new { command = "Get-Content legacy\\M\\spec\\X.md" }, work),
                ("Monitor", new { command = "tail -f legacy/M/evidence/run.log" }, work),
                ("mcp__docs__search_docs", new { query = "angular signals" }, work),
                ("Bash", new { command = "grep \"Rule.*\" 'legacy/M/spec/X.md' && echo \"it's fine\"" }, work),               // quoted arguments and whole-token quoted paths are not splices
                ("Bash", new { command = "cat legacy/M/manifests/*.md" }, work),                                                 // a wildcard in the last segment only
                ("Bash", new { command = "npx eslint \"src/**/*.ts\" && cat src/*/x.ts" }, work),                                // wildcard segments that cannot match ..
                ("Bash", new { command = "sed -i 's/a.*b/c/' src/x.ts" }, work),                                                 // regexes
                ("Bash", new { command = "grep -E \"foo|.*bar\" src && grep -E \"foo|[a-z]+\" src" }, work),
                ("Bash", new { command = "git commit -m \"fix user's/admin's\"" }, work),                                         // apostrophes inside a message
                ("mcp__filesystem__read_text_file", new { path = "legacy/M/spec/X.md" }, work),
                ("mcp__fs__search", new { path = "legacy/M", pattern = "x" }, work),
            };
            // calls it must refuse
            var denied = new (string Tool, object Input, string Cwd, string Why)[]
            {
                ("PowerShell", new { command = "Get-Content " + legacyForm.Replace('/', '\\') }, work, "outside"),                     // the shell tool on Windows, root named
                ("Bash", new { command = "cat " + legacyForm }, work, "outside"),
                ("mcp__docs__search_docs", new { query = "x", path = legacyForm }, work, "outside"),                           // a path value in an MCP tool's input
                ("mcp__filesystem__read_text_file", new { path = "../Legacy/App/X.cs" }, work, "outside"),                       // relative, the root never named
                ("mcp__fs__search", new { path = "..", pattern = "x" }, work, "outside"),                                       // a bare .. with no separator
                ("mcp__fs__list_directory", new { path = "~" }, work, "home directory"),
                ("mcp__filesystem__read_multiple_files", new { paths = new[] { "legacy/M/spec/X.md", "..\\Legacy\\AcmeAdmin\\Forms\\X.cs" } }, work, "outside"),
                ("mcp__codegraph__get_symbol", new { symbol = "PolicyForm" }, work, "code-index"),                               // no path at all
                ("mcp__serena__find_symbol", new { name_path = "Save" }, work, "code-index"),
                ("Read", new { file_path = "../Legacy/AcmeAdmin/Forms/X.cs" }, work, "outside"),                                 // relative path
                ("Read", new { file_path = "Forms/X.cs" }, legacy, "outside"),                                                    // short path from inside the tree
                ("Read", new { file_path = "legacy/../../Legacy/AcmeAdmin/Forms/X.cs" }, work, "outside"),                        // dot-dot detour
                ("Read", new { file_path = "../../etc/hostname" }, work, "outside"),                                              // outside the allow-list, not the legacy tree
                ("Grep", new { pattern = "Save", path = "../Legacy" }, work, "outside"),
                ("Grep", new { pattern = "Save" }, legacy, "outside"),                                                            // default search root is the cwd
                ("Glob", new { pattern = "../Legacy/**/*.cs" }, work, "outside"),
                ("Write", new { file_path = "../shared/x.txt", content = "x" }, work, "outside"),
                ("Bash", new { command = "cd ../Legacy/AcmeAdmin && cat Forms/X.cs" }, work, "directory"),                      // cd into the tree, relative reads
                ("Bash", new { command = "cat Forms/X.cs" }, legacy, "outside"),                                                 // session sitting in the tree
                ("Bash", new { command = "cat ../Legacy/AcmeAdmin/Forms/X.cs" }, work, "outside"),
                ("Bash", new { command = "git -C .. log" }, work, "outside"),
                ("Bash", new { command = "cd $HOME && ls" }, work, "directory"),
                ("Bash", new { command = "cat ~/notes.txt" }, work, "outside"),
                ("Bash", new { command = "cat $ROOT/Forms/X.cs" }, work, "resolve"),
                ("PowerShell", new { command = "Set-Location -Path ..\\Legacy; Get-Content Forms\\X.cs" }, work, "directory"),
                ("Monitor", new { command = "tail -f ../Legacy/AcmeAdmin/Forms/X.cs" }, work, "outside"),
                ("Bash", new { command = "cat .''./Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),                              // quote splice of ..
                ("Bash", new { command = "cat .\\./Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),                              // backslash escape inside ..
                ("Bash", new { command = "ls .?/Legacy/AcmeAdmin/Forms" }, work, "resolve"),                                      // wildcard that matches ..
                ("Bash", new { command = "ls -d .*/Legacy" }, work, "resolve"),
                ("Bash", new { command = "ls .*" }, work, "resolve"),
                ("Bash", new { command = "ls [.][.]/Legacy" }, work, "resolve"),
                ("Bash", new { command = "cat .{.,}/Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),                              // brace expansion
                ("Bash", new { command = "cd .''. && ls" }, work, "resolve"),                                                      // the same through cd
                ("Monitor", new { command = "tail -f .''./Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),
                ("PowerShell", new { command = "Get-Content .`.\\Legacy\\AcmeAdmin\\Forms\\X.cs" }, work, "resolve"),             // backtick escape
                ("PowerShell", new { command = "Get-Content .?\\Legacy\\AcmeAdmin\\Forms\\X.cs" }, work, "resolve"),
                ("Bash", new { command = "powershell -Command \"gc ..\\Legacy\\AcmeAdmin\\Forms\\X.cs\"" }, work, "outside"),       // backslash path through a Bash call
                ("Bash", new { command = "powershell -c \"cd ..\\Legacy; gc AcmeAdmin\\Forms\\X.cs\"" }, work, "directory"),
                ("Bash", new { command = "gcc -I../Legacy/AcmeAdmin x.c" }, work, "outside"),                                    // path glued to a short option
                ("Bash", new { command = "cat src/*/../../../Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),                   // .. after a wildcard
                ("Bash", new { command = "cat " + work.Replace('\\', '/') + "*/x" }, work, "outside"),                          // a wildcard on the repo name reaches its siblings
                ("Glob", new { pattern = "legacy/*/../../../Legacy/**/*.cs" }, work, "resolve"),
                ("Read", new { }, work, "path"),                                                                                  // nothing to decide on
                ("NotAKnownTool", new { x = 1 }, work, "tool"),
            };
            var denials = 0;
            foreach (var phase in new[] { "specify", "implement" })
            {
                File.WriteAllText(Path.Combine(work, "legacy", ".phase"), phase + "\n");
                foreach (var (tool, input, cwd) in allowed)
                {
                    var r = GuardHookTests.Run(work, HookCall(tool, input, cwd), hideJq: false);
                    Assert.True(r.Code == 0, $"{phase}: {tool} {System.Text.Json.JsonSerializer.Serialize(input)} should pass:\n{r.Err}");
                }
                foreach (var (tool, input, cwd, why) in denied)
                {
                    var r = GuardHookTests.Run(work, HookCall(tool, input, cwd), hideJq: false);
                    Assert.True(r.Code == 2, $"{phase}: {tool} {System.Text.Json.JsonSerializer.Serialize(input)} should be refused");
                    Assert.Contains(why, r.Err);
                    Assert.Single(r.Err.Trim().Split('\n'));                                                                      // a one-line reason
                    denials++;
                }
                // an input the hook cannot parse is refused, not waved through
                var junk = GuardHookTests.Run(work, "this is not json", hideJq: false);
                Assert.Equal(2, junk.Code); denials++;
            }
            // the log: one line per denial, date, phase, tool, reason; the legitimate calls left no line
            var lines = File.ReadAllLines(log);
            Assert.Equal(denials, lines.Length);
            Assert.All(lines, l => { var f = l.Split('\t'); Assert.Equal(4, f.Length); Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", f[0]); Assert.Contains(f[1], new[] { "specify", "implement" }); Assert.NotEmpty(f[2]); Assert.NotEmpty(f[3]); });
            Assert.Contains(lines, l => l.Split('\t')[2] == "mcp__filesystem__read_text_file" && l.Contains("outside"));
            Assert.Contains(lines, l => l.Split('\t')[2] == "mcp__codegraph__get_symbol");

            // the settings command, run from a session cwd below the repo root: CLAUDE_PROJECT_DIR keeps the phase readable
            var hookCommand = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
            File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "specify\n");
            var sub = Path.Combine(work, "legacy", "M"); Directory.CreateDirectory(sub);
            var before = File.ReadAllLines(log).Length;
            var fromSub = GuardHookTests.RunCommand(sub, hookCommand, work, HookCall("Read", new { file_path = "../../../Legacy/AcmeAdmin/Forms/X.cs" }, sub));
            Assert.Equal(2, fromSub.Code); Assert.Contains("outside", fromSub.Err);
            Assert.Equal(0, GuardHookTests.RunCommand(sub, hookCommand, work, HookCall("Read", new { file_path = "spec/X.md" }, sub)).Code);
            Assert.Equal(before + 1, File.ReadAllLines(log).Length);
            Assert.Equal(2, GuardHookTests.RunCommand(sub, hookCommand, Path.Combine(work, "missing"), HookCall("Read", new { file_path = "spec/X.md" }, sub)).Code);

            // extract phase stays unguarded and logs nothing
            File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "extract\n");
            Assert.Equal(0, GuardHookTests.Run(work, HookCall("Read", new { file_path = legacyForm }, work), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(work, HookCall("mcp__codegraph__get_symbol", new { symbol = "X" }, work), hideJq: false).Code);
            Assert.Equal(denials + 1, File.ReadAllLines(log).Length);

            // guard.denyTools extends the deny-list, guard.allowRoots the allow-list
            File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "specify\n");
            Assert.Equal(0, GuardHookTests.Run(work, HookCall("mcp__house-lookup__find", new { q = "x" }, work), hideJq: false).Code);
            Assert.Equal(2, GuardHookTests.Run(work, HookCall("Read", new { file_path = "../shared/x.txt" }, work), hideJq: false).Code);
            File.WriteAllText(config, "{\n  \"project\": \"" + legacy.Replace('\\', '/') + "/AcmeAdmin.csproj\",\n  \"output\": \".\",\n  \"guard\": { \"denyTools\": [\"mcp__house-lookup__*\"], \"allowRoots\": [\"../../shared\"] }\n}\n");
            var custom = GuardHookTests.Run(work, HookCall("mcp__house-lookup__find", new { q = "x" }, work), hideJq: false);
            Assert.Equal(2, custom.Code); Assert.Contains("code-index", custom.Err);
            Assert.Equal(0, GuardHookTests.Run(work, HookCall("Read", new { file_path = "../shared/x.txt" }, work), hideJq: false).Code);
            Assert.Equal(2, GuardHookTests.Run(work, HookCall("Read", new { file_path = "../Legacy/AcmeAdmin/Forms/X.cs" }, work), hideJq: false).Code);

            // the legacy root named as a Windows drive path, in every spelling a Windows host produces
            File.WriteAllText(config, "{\n  \"project\": \"C:/src/Legacy/AcmeAdmin/AcmeAdmin.csproj\"\n}\n");
            foreach (var spelling in new[] { "C:\\src\\Legacy\\AcmeAdmin\\Forms\\X.cs", "c:/SRC/legacy/acmeadmin/Forms/X.cs", "/c/src/Legacy/AcmeAdmin/Forms/X.cs", "/mnt/c/src/Legacy/AcmeAdmin/Forms/X.cs" })
                Assert.Equal(2, GuardHookTests.Run(work, HookCall("Read", new { file_path = spelling }, work), hideJq: false).Code);

            // without jq nothing in a restricted phase can be parsed: refused, with the reason
            var nojq = Path.Combine(work, "nojq"); Directory.CreateDirectory(nojq);
            foreach (var tool in new[] { "cat", "tr", "sed", "grep", "dirname", "printf", "head", "date", "sh" })
                foreach (var cand in new[] { "/usr/bin/" + tool, "/bin/" + tool })
                    if (File.Exists(cand) && !File.Exists(Path.Combine(nojq, tool))) { File.CreateSymbolicLink(Path.Combine(nojq, tool), cand); break; }
            var noJq = GuardHookTests.Run(work, HookCall("Read", new { file_path = "legacy/M/manifests/X.md" }, work), hideJq: true);
            Assert.Equal(2, noJq.Code); Assert.Contains("jq", noJq.Err);
            Assert.Contains(File.ReadAllLines(log), l => l.Contains("jq"));
        }
        finally { Directory.Delete(baseDir, true); }
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
}

/// <summary>The guard hook, run through sh. Skipped where no POSIX shell exists (plain Windows without Git Bash).</summary>
public sealed class GuardHookTests
{
    internal static string? Sh() => new[] { "/bin/sh", "/usr/bin/sh", "sh" }.FirstOrDefault(p => { try { using var x = Process.Start(new ProcessStartInfo(p, "-c true") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }); x!.WaitForExit(); return x.ExitCode == 0; } catch { return false; } });

    internal static (int Code, string Err) Run(string workDir, string stdin, bool hideJq)
    {
        var psi = new ProcessStartInfo(Sh()!, "legacy/hooks/guard-legacy.sh") { WorkingDirectory = workDir, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (hideJq) psi.Environment["PATH"] = Path.Combine(workDir, "nojq");
        psi.Environment.Remove("PARITY_LEGACY_ROOT");
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        return Exec(psi, stdin);
    }

    /// <summary>The hook command as settings.json spells it, run by sh -c from <paramref name="procDir"/> with CLAUDE_PROJECT_DIR set.</summary>
    internal static (int Code, string Err) RunCommand(string procDir, string command, string projectDir, string stdin)
    {
        var psi = new ProcessStartInfo(Sh()!) { WorkingDirectory = procDir, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command);
        psi.Environment.Remove("PARITY_LEGACY_ROOT");
        psi.Environment["CLAUDE_PROJECT_DIR"] = projectDir;
        return Exec(psi, stdin);
    }

    static (int Code, string Err) Exec(ProcessStartInfo psi, string stdin)
    {
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin); p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd(); p.WaitForExit();
        return (p.ExitCode, err);
    }

    [Fact]
    public void issue_9_guard_fails_closed_without_a_root_and_without_jq()
    {
        if (Sh() == null) return; // no POSIX shell: the hook cannot run here at all
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Parity.sln"))) dir = Path.GetDirectoryName(dir);
        var work = Path.Combine(Path.GetTempPath(), "parity-hook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(work, "legacy", "hooks"));
        File.Copy(Path.Combine(dir!, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(work, "legacy", "hooks", "guard-legacy.sh"));
        // a PATH with only the core tools and no jq: cat, tr, sed, grep, dirname resolved via a symlink farm
        var nojq = Path.Combine(work, "nojq"); Directory.CreateDirectory(nojq);
        foreach (var tool in new[] { "cat", "tr", "sed", "grep", "dirname", "printf", "head", "date", "sh" })
            foreach (var cand in new[] { "/usr/bin/" + tool, "/bin/" + tool })
                if (File.Exists(cand) && !File.Exists(Path.Combine(nojq, tool))) { File.CreateSymbolicLink(Path.Combine(nojq, tool), cand); break; }
        var legacyRead = "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:\\\\src\\\\Legacy\\\\AcmeAdmin\\\\Forms\\\\X.cs\"}}";
        var manifestRead = "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"legacy/M/manifests/X.md\"}}";

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
        var noJq = Run(work, manifestRead, hideJq: true);                                   // sed finds the root, but parsed input needs jq (#22 d): refused, not waved through
        Assert.Equal(2, noJq.Code);
        Assert.Contains("jq is required", noJq.Err);
        Assert.Equal(0, Run(work, manifestRead, hideJq: false).Code);                      // manifests stay readable with jq
        Assert.Equal(2, Run(work, legacyRead, hideJq: false).Code);                         // and the same with jq if present
        Directory.Delete(work, true);
    }
}
