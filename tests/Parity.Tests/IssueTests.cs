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
                ("Bash", new { command = "echo cd .. && cat ../README.md" }, Path.Combine(work, "legacy")),                     // cd as an argument moves nothing
                ("Bash", new { command = "(cd legacy/M && ls fixtures); cat legacy/M/spec/X.md" }, work),                        // a subshell's cd ends with it
                ("Bash", new { command = "bash -c \"cd legacy/M && ls fixtures\" && cat legacy/M/spec/X.md" }, work),           // so does a nested shell's
                ("Bash", new { command = "bash --norc -o pipefail -c \"cd legacy/M && ls fixtures\"" }, work),                  // option words before -c
                ("Glob", new { pattern = "**/*.{ts,tsx}" }, work),                                                               // brace alternatives that cannot be ..
                ("Bash", new { command = "grep -E \"[0-9]{2,4}\" src/x.cs" }, work),                                            // braces inside quotes are not expansion
                ("Bash", new { command = "curl -d '{\"a\":1,\"b\":2}' http://localhost:5000/x" }, work),
                ("Bash", new { command = "dotnet build /p:Configuration=Release && msbuild x.sln /t:Rebuild /v:m" }, work),     // MSBuild switches are options
                ("PowerShell", new { command = "dotnet test /p:Foo=1" }, work),
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
                ("mcp__graft__graft_find_code", new { query = "PolicyForm" }, work, "code-index"),                              // Graft by server name
                ("mcp__graft__graft_file_api", new { file = "Forms/X.cs" }, work, "code-index"),
                ("mcp__graft__graft_trace_calls", new { symbol = "Save" }, work, "code-index"),
                ("mcp__graft__graft_find_all", new { symbol = "Save" }, work, "code-index"),
                ("mcp__graft__graft_repo_map", new { }, work, "code-index"),
                ("mcp__graft__graft_check_freshness", new { }, work, "code-index"),
                ("mcp__acme-graph__graft_find_code", new { query = "PolicyForm" }, work, "code-index"),                          // Graft tools under another server name, by the graft_ prefix
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
                ("Bash", new { command = "echo \"it's\" && cat .{.,}/Legacy/AcmeAdmin/Forms/X.cs" }, work, "brace"),               // after a quote that holds the other quote
                ("Bash", new { command = "bash -c \"cat .{.,}/Legacy/AcmeAdmin/Forms/X.cs\"" }, work, "brace"),                  // inside a nested shell's command
                ("Bash", new { command = "cd .''. && ls" }, work, "resolve"),                                                      // the same through cd
                ("Monitor", new { command = "tail -f .''./Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),
                ("PowerShell", new { command = "Get-Content .`.\\Legacy\\AcmeAdmin\\Forms\\X.cs" }, work, "resolve"),             // backtick escape
                ("PowerShell", new { command = "Get-Content .?\\Legacy\\AcmeAdmin\\Forms\\X.cs" }, work, "resolve"),
                ("Bash", new { command = "powershell -Command \"gc ..\\Legacy\\AcmeAdmin\\Forms\\X.cs\"" }, work, "outside"),       // backslash path through a Bash call
                ("Bash", new { command = "powershell -c \"cd ..\\Legacy; gc AcmeAdmin\\Forms\\X.cs\"" }, work, "directory"),
                ("Bash", new { command = "git commit -m \"cd up\" && cat ../Legacy/App/X.cs" }, work, "outside"),                 // cd inside a message is not a directory change
                ("Bash", new { command = "git commit -m \"fix; cd legacy/M\" && cat ../../Legacy/App/X.cs" }, work, "outside"),   // nor after a quoted ;
                ("Bash", new { command = "gcc -I../Legacy/AcmeAdmin x.c" }, work, "outside"),                                    // path glued to a short option
                ("Bash", new { command = "cat src/*/../../../Legacy/AcmeAdmin/Forms/X.cs" }, work, "resolve"),                   // .. after a wildcard
                ("Bash", new { command = "cat " + work.Replace('\\', '/') + "*/x" }, work, "outside"),                          // a wildcard on the repo name reaches its siblings
                ("Glob", new { pattern = "legacy/*/../../../Legacy/**/*.cs" }, work, "resolve"),
                ("Glob", new { pattern = "{..,x}/Legacy/**/*.cs" }, work, "resolve"),                                            // a brace alternative that is ..
                ("Grep", new { pattern = "Save", glob = "{..,x}/Legacy/**/*.cs" }, work, "resolve"),
                ("Glob", new { pattern = "**/{..}/Legacy/*.cs" }, work, "resolve"),
                ("Read", new { }, work, "path"),                                                                                  // nothing to decide on
                ("NotAKnownTool", new { x = 1 }, work, "tool"),
            };
            // from a cwd below the repo root a missed cd .. leaves ../Legacy looking like a repo path: every form must be followed
            var below = Path.Combine(work, "legacy");
            const string Up = "cat ../Legacy/AcmeAdmin/Forms/X.cs";
            const string PsUp = "gc ..\\Legacy\\AcmeAdmin\\Forms\\X.cs";
            denied = denied.Concat(new (string Tool, object Input, string Cwd, string Why)[]
            {
                ("Bash", new { command = "if true; then cd ..; " + Up + "; fi" }, below, "outside"),                             // reserved words keep command position
                ("Bash", new { command = "for d in a; do cd ..; " + Up + "; done" }, below, "outside"),
                ("Bash", new { command = "command cd .. && " + Up }, below, "outside"),                                          // wrappers too
                ("Bash", new { command = "builtin cd .. && " + Up }, below, "outside"),
                ("Bash", new { command = "! cd .. && " + Up }, below, "outside"),
                ("Bash", new { command = "time cd .. && " + Up }, below, "outside"),
                ("Bash", new { command = "CDPATH= cd .. && " + Up }, below, "outside"),
                ("Bash", new { command = "bash -c \"cd ..; " + Up + "\"" }, below, "outside"),                                   // a nested shell's command
                ("Bash", new { command = "sh -c 'cd .. && " + Up + "'" }, below, "outside"),
                ("PowerShell", new { command = "if ($true) { cd .. }; " + PsUp }, below, "outside"),                            // PowerShell blocks
                ("PowerShell", new { command = "foreach ($x in 1) { Set-Location .. }; " + PsUp }, below, "outside"),
                ("PowerShell", new { command = "1 | ForEach-Object { cd .. }; " + PsUp }, below, "outside"),
                ("Bash", new { command = "bash --norc -c \"cd $HOME && cat Legacy/AcmeAdmin/Forms/X.cs\"" }, below, "directory"),  // long options before -c
                ("Bash", new { command = "bash --login -c \"cd; cat Legacy/AcmeAdmin/Forms/X.cs\"" }, below, "directory"),
                ("Bash", new { command = "bash -o pipefail -c \"cd $HOME && cat Legacy/AcmeAdmin/Forms/X.cs\"" }, below, "directory"),
                ("Bash", new { command = "sudo cd .. && ls" }, below, "first word"),                                             // a cd the guard cannot follow
                ("Bash", new { command = "sudo cd $HOME && ls" }, below, "first word"),
                ("Bash", new { command = "sudo cd && ls" }, below, "first word"),
                ("Bash", new { command = "xargs cd %USERPROFILE%" }, below, "first word"),
            }).ToArray();
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

            // a config parity accepts (a comment, a trailing comma) that jq cannot read: the refusal names the cause
            File.WriteAllText(config, "{\n  // the legacy project\n  \"project\": \"" + legacy.Replace('\\', '/') + "/AcmeAdmin.csproj\",\n}\n");
            Assert.EndsWith("AcmeAdmin.csproj", ExtractConfig.Load(config).Project);
            var lenient = GuardHookTests.Run(work, HookCall("Read", new { file_path = "legacy/M/manifests/X.md" }, work), hideJq: false);
            Assert.Equal(2, lenient.Code); Assert.Contains("not strict JSON", lenient.Err);

            // the legacy root named as a Windows drive path, in every spelling a Windows host produces
            File.WriteAllText(config, "{\n  \"project\": \"C:/src/Legacy/AcmeAdmin/AcmeAdmin.csproj\"\n}\n");
            foreach (var spelling in new[] { "C:\\src\\Legacy\\AcmeAdmin\\Forms\\X.cs", "c:/SRC/legacy/acmeadmin/Forms/X.cs", "/c/src/Legacy/AcmeAdmin/Forms/X.cs", "/mnt/c/src/Legacy/AcmeAdmin/Forms/X.cs" })
                Assert.Equal(2, GuardHookTests.Run(work, HookCall("Read", new { file_path = spelling }, work), hideJq: false).Code);

            // a repo whose path starts with the legacy root's path is not the legacy root
            var next = legacy + "-next";
            Directory.CreateDirectory(Path.Combine(next, "legacy", "hooks"));
            File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(next, "legacy", "hooks", "guard-legacy.sh"));
            File.WriteAllText(Path.Combine(next, "legacy", "extract.config.json"), "{\n  \"project\": \"../../AcmeAdmin/AcmeAdmin.csproj\"\n}\n");
            File.WriteAllText(Path.Combine(next, "legacy", ".phase"), "specify\n");
            Assert.Equal(0, GuardHookTests.Run(next, HookCall("Bash", new { command = "ls" }, next), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(next, HookCall("Read", new { file_path = "legacy/M/manifests/X.md" }, next), hideJq: false).Code);
            Assert.Equal(2, GuardHookTests.Run(next, HookCall("Read", new { file_path = "../AcmeAdmin/Forms/X.cs" }, next), hideJq: false).Code);

            // a legacy root inside the repo: a bare directory name reaches it as surely as a path
            var inner = Path.Combine(baseDir, "inner");
            Directory.CreateDirectory(Path.Combine(inner, "legacy", "hooks"));
            Directory.CreateDirectory(Path.Combine(inner, "LegacyApp"));
            File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(inner, "legacy", "hooks", "guard-legacy.sh"));
            File.WriteAllText(Path.Combine(inner, "legacy", "extract.config.json"), "{\n  \"project\": \"../LegacyApp/LegacyApp.csproj\"\n}\n");
            File.WriteAllText(Path.Combine(inner, "legacy", ".phase"), "specify\n");
            foreach (var c in new[] { "grep -r Foo LegacyApp", "ls LegacyApp", "cp -r LegacyApp out" })
            {
                var r = GuardHookTests.Run(inner, HookCall("Bash", new { command = c }, inner), hideJq: false);
                Assert.True(r.Code == 2, $"{c} should be refused"); Assert.Contains("legacy source root", r.Err);
            }
            Assert.Equal(2, GuardHookTests.Run(inner, HookCall("mcp__fs__list_directory", new { path = "LegacyApp" }, inner), hideJq: false).Code);
            foreach (var c in new[] { "ls", "grep -r Foo src", "cat legacy/M/spec/X.md" })
                Assert.Equal(0, GuardHookTests.Run(inner, HookCall("Bash", new { command = c }, inner), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(inner, HookCall("mcp__docs__search_docs", new { query = "Foo" }, inner), hideJq: false).Code);
            // ... and a search rooted at a directory above it reaches it without naming it
            foreach (var (tool, input) in new (string, object)[]
            {
                ("Grep", new { pattern = "Foo" }),
                ("Grep", new { pattern = "Foo", path = "." }),
                ("Glob", new { pattern = "**/*.cs" }),
                ("Glob", new { pattern = "*/Forms/*.cs" }),
                ("Grep", new { pattern = "Foo", glob = "*.cs" }),
                ("mcp__fs__search", new { path = ".", pattern = "x" }),
                ("mcp__fs__search", new { path = "", pattern = "x" }),
            })
            {
                var r = GuardHookTests.Run(inner, HookCall(tool, input, inner), hideJq: false);
                Assert.True(r.Code == 2, $"{tool} {System.Text.Json.JsonSerializer.Serialize(input)} should be refused");
                Assert.Contains("contains the legacy source root", r.Err);
                Assert.Contains("legacyapp", r.Err);
            }
            Assert.Equal(0, GuardHookTests.Run(inner, HookCall("Grep", new { pattern = "Foo", path = "src" }, inner), hideJq: false).Code);

            // without jq nothing in a restricted phase can be parsed: refused, with the reason
            var nojq = Path.Combine(work, "nojq"); Directory.CreateDirectory(nojq);
            foreach (var tool in new[] { "cat", "tr", "sed", "grep", "awk", "dirname", "printf", "head", "date", "sh" })
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

    [Fact]
    public void issue_20_tenant_variants_fold_into_the_base_manifest_with_a_delta_and_factories()
    {
        // input: ClientSearchForm plus two tenant subclasses (Larkspur in the base project, Thistle in its own csproj) and
        // ClientSearchFormFactory; extract.config.json declares both variants. output: one base unit carrying both deltas.
        var dir = Path.Combine(_f.ModuleDir, "manifests");
        Assert.False(File.Exists(Path.Combine(dir, "LarkspurClientSearchForm.json")), "a variant is not a unit of its own");
        Assert.DoesNotContain(_f.Summaries, s => s.Form is "LarkspurClientSearchForm" or "ThistleClientSearchForm");
        var m = _f.Manifest("ClientSearchForm");
        Assert.NotNull(m.Variants);
        Assert.Equal(new[] { "var:Larkspur", "var:Thistle" }, m.Variants!.Select(v => v.Id));
        var lrk = m.Variants[0];
        var ths = m.Variants[1];
        Assert.Equal("SampleLegacy.Tenants.Larkspur.LarkspurClientSearchForm", lrk.Class);
        Assert.Null(lrk.Project);
        Assert.Equal("SampleLegacy.Thistle/SampleLegacy.Thistle.csproj", ths.Project);
        Assert.All(m.Variants, v => { Assert.Empty(v.Raw); Assert.Empty(v.Diagnostics); });

        Assert.Equal(new[]
        {
            "var:Larkspur/added/chkLoyaltyOnly",
            "var:Larkspur/constant/LarkspurLimits.MaxResults",
            "var:Larkspur/event/chkLoyaltyOnly.CheckedChanged",
            "var:Larkspur/handler/chkLoyaltyOnly_CheckedChanged",
            "var:Larkspur/override/btnSearch_Click",
            "var:Larkspur/retagged/$this",
            "var:Larkspur/retagged/lblPostcode",
            "var:Larkspur/service-added/IClientService.SearchLoyaltyClients",
            "var:Larkspur/service-removed/IClientService.SearchClients",
        }, lrk.Delta.Select(d => d.Id));
        Assert.Equal(new[]
        {
            "var:Thistle/added/ctor/lblTenant",
            "var:Thistle/event/txtPostcode.KeyDown",
            "var:Thistle/handler/txtPostcode_KeyDown",
            "var:Thistle/override/btnSearch_Click",
            "var:Thistle/removed/dtDob",
            "var:Thistle/retagged/btnSelect",
            "var:Thistle/retagged/lblSurname",
        }, ths.Delta.Select(d => d.Id));
        VariantDelta D(VariantInfo v, string id) => v.Delta.Single(d => d.Id == id);

        // every entry cites the variant's own file:line; Thistle's project sits next to the base one
        Assert.All(m.Variants.SelectMany(v => v.Delta), d => Assert.NotNull(d.Loc));
        Assert.All(ths.Delta, d => Assert.StartsWith("../SampleLegacy.Thistle/Forms/ThistleClientSearchForm.cs", d.Loc!.File));
        var postcode = D(lrk, "var:Larkspur/retagged/lblPostcode");
        Assert.Equal("ctrl:ClientSearchForm/lblPostcode", postcode.Target);
        var text = Assert.Single(postcode.Changes!);
        Assert.Equal(("Text", "Postcode", "Zip code"), (text.Property, text.Base, text.Value));
        Assert.Equal(new Loc("Tenants/Larkspur/LarkspurClientSearchForm.Designer.cs", 13, 13), postcode.Loc);
        Assert.Equal(("Visible", (string?)null, "false"), D(ths, "var:Thistle/retagged/btnSelect").Changes!.Select(c => (c.Property, c.Base, c.Value)).Single());
        Assert.Equal("ctrl:ClientSearchForm/dtDob", D(ths, "var:Thistle/removed/dtDob").Target);
        Assert.Equal("ctrl:ClientSearchForm/$this", D(ths, "var:Thistle/added/ctor/lblTenant").Target);

        // a constant the variant reads in place of the base's (same member name, another type, another value) is a delta with both values
        var limit = D(lrk, "var:Larkspur/constant/LarkspurLimits.MaxResults");
        Assert.Equal(("constant", "SearchLimits.MaxResults", "const"), (limit.Kind, limit.Target, limit.Detail));
        var change0 = Assert.Single(limit.Changes!);
        Assert.Equal(("value", "50", "200"), (change0.Property, change0.Base, change0.Value));
        Assert.Equal(new Loc("Tenants/Larkspur/LarkspurClientSearchForm.cs", 27, 27), limit.Loc);
        Assert.DoesNotContain(ths.Delta, d => d.Kind == "constant"); // Thistle reads the base's constant through its base call: nothing to account for

        // an override either replaces the base handler or extends it; only a replacement drops the base's service call
        var lo = D(lrk, "var:Larkspur/override/btnSearch_Click");
        Assert.Equal(("handler:ClientSearchForm/btnSearch_Click", "replaces base"), (lo.Target, lo.Detail));
        Assert.Equal("calls base", D(ths, "var:Thistle/override/btnSearch_Click").Detail);
        Assert.Equal(new[] { "var:Larkspur/override/btnSearch_Click" }, D(lrk, "var:Larkspur/service-added/IClientService.SearchLoyaltyClients").Calls);
        Assert.Equal("svc:IClientService.SearchClients", D(lrk, "var:Larkspur/service-removed/IClientService.SearchClients").Target);
        Assert.DoesNotContain(ths.Delta, d => d.Kind.StartsWith("service", StringComparison.Ordinal));
        Assert.Equal(new[] { "var:Larkspur/handler/chkLoyaltyOnly_CheckedChanged" }, new[] { D(lrk, "var:Larkspur/event/chkLoyaltyOnly.CheckedChanged").Detail });

        // which class a tenant code actually gets is a cited fact
        Assert.NotNull(m.Factories);
        var create = m.Factories!.Single(x => x.Id == "method:SampleLegacy.Forms.ClientSearchFormFactory.Create(string)");
        Assert.Equal("tenantCode", create.On);
        Assert.Equal(new[] { ("LRK", "Larkspur"), ("THS", "Thistle"), ("THS-NORTH", "Thistle"), ("default", "base") },
            create.Cases.Select(c => (c.Key, c.Variant!)));
        Assert.All(create.Cases, c => Assert.Equal("Forms/ClientSearchFormFactory.cs", c.Loc!.File));
        var region = m.Factories.Single(x => x.Id.Contains("ForRegion", StringComparison.Ordinal));
        Assert.Equal("ByRegion", region.On);
        Assert.Equal(new[] { "Larkspur", "Thistle", "base" }, region.Cases.Select(c => c.Variant!).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        // a factory branch the pass does not understand is a case keyed by its condition text, never the default
        var site = m.Factories.Single(x => x.Id.Contains(".ForSite(", StringComparison.Ordinal));
        Assert.Equal("siteCode", site.On);
        Assert.Equal(new[] { ("THS-PILOT", "Thistle"), ("siteCode.StartsWith(\"LRK\") && pilot", "Larkspur"), ("default", "base") },
            site.Cases.Select(c => (c.Key, c.Variant!)));
        Assert.Equal("default", region.Cases.Single(c => c.Variant == "base").Key);
        // a when clause or a condition inside a keyed case joins its key, and every form the case guards is a case of its own
        var desk = m.Factories.Single(x => x.Id.Contains(".ForDesk(", StringComparison.Ordinal));
        Assert.Equal("deskCode", desk.On);
        Assert.Equal(new[] { ("LRK && pilot", "Larkspur"), ("THS && pilot", "Thistle"), ("THS", "base"), ("default", "base") },
            desk.Cases.Select(c => (c.Key, c.Variant!)));
        var kiosk = m.Factories.Single(x => x.Id.Contains(".ForKiosk(", StringComparison.Ordinal));
        Assert.Equal(new[] { ("LRK && pilot", "Larkspur"), ("THS", "Thistle"), ("default", "base") },
            kiosk.Cases.Select(c => (c.Key, c.Variant!)));
        // a unit without variants keeps its manifest shape
        Assert.Null(_f.Manifest("PolicyWizardStep3").Variants);
        Assert.Null(_f.Manifest("PolicyWizardStep3").Factories);

        // a re-extract that changes a delta is a diff entry, so rebind does not move a spec citing it
        var relabeled = m with { Variants = new() { lrk with { Delta = lrk.Delta.Select(d => d.Id == postcode.Id ? d with { Changes = new() { text with { Value = "ZIP" } } } : d).ToList() }, ths } };
        var change = Assert.Single(ManifestDiff.Compare(m, relabeled));
        Assert.Equal(("delta", postcode.Id, '~', "changes"), (change.Section, change.Id, change.Kind, string.Join(",", change.Fields)));

        var md = File.ReadAllText(Path.Combine(dir, "ClientSearchForm.md"));
        Assert.Contains("### Variant Larkspur", md);
        Assert.Contains("### Variant Thistle", md);
        Assert.Contains("## Factories", md);
        Assert.Contains("| `var:Thistle/removed/dtDob` | removed | `ctrl:ClientSearchForm/dtDob` |", md);
        Assert.Contains("| `var:Larkspur/constant/LarkspurLimits.MaxResults` | constant | `SearchLimits.MaxResults` | const |", md);
        Assert.Contains("  - value: 50 -> 200 `Tenants/Larkspur/LarkspurClientSearchForm.cs:27-27`", md);

        // lint: a delta no rule scoped to its variant describes, and no 'Variant deltas without a rule' row accounts for, fails the gate
        var specDir = Path.Combine(_f.ModuleDir, "spec");
        Directory.CreateDirectory(specDir);
        var spec = Path.Combine(specDir, "ClientSearchForm.md");
        string Spec(string rules, string noRule) =>
            "---\nform: form:ClientSearchForm\nmanifest: " + ManifestHash.Of(Path.Combine(dir, "ClientSearchForm.json")) + "\nstatus: reviewed\nreviewed-by: example-reviewer 2026-10-23\n---\n\n" +
            "# ClientSearchForm\n\n## Rules\n\n" + rules + "\n## Variant deltas without a rule\n\n| delta | reason |\n|---|---|\n" + noRule + "\n## Open questions\n\n- none\n";
        const string Rule = "### {0}\n- kind: data\n{1}- covers: {2}\n- cite: {3}\n- given: any state\n- when: the user acts\n- then: ctrl:ClientSearchForm/grdResults is refilled\n\n";
        string R(string id, string? variants, string covers, string cite) => string.Format(Rule, id, variants == null ? "" : $"- variants: {variants}\n", covers, cite);
        List<Finding> Var(string rules, string noRule)
        {
            File.WriteAllText(spec, Spec(rules, noRule));
            return SpecLinter.Lint(spec, gate: true, implementDir: null).Where(x => x.Code.StartsWith("VAR", StringComparison.Ordinal)).ToList();
        }
        try
        {
            var all = string.Join("\n", lrk.Delta.Concat(ths.Delta).Select(d => $"| {d.Id} | no rule needed in this test |"));
            Assert.Empty(Var(R("R-ClientSearchForm-001", "base, Thistle", "evt:ClientSearchForm/btnSearch.Click", "handler:ClientSearchForm/btnSearch_Click"), all));

            // dropping one row surfaces exactly that delta
            var f = Var("", string.Join("\n", all.Split('\n').Where(l => !l.Contains("var:Thistle/removed/dtDob"))));
            Assert.Equal("VAR-UNCOVERED", Assert.Single(f).Code);
            Assert.Contains("var:Thistle/removed/dtDob", f[0].Message);

            // the constant delta is gated the same way
            f = Var("", string.Join("\n", all.Split('\n').Where(l => !l.Contains("var:Larkspur/constant/LarkspurLimits.MaxResults"))));
            Assert.Equal("VAR-UNCOVERED", Assert.Single(f).Code);
            Assert.Contains("var:Larkspur/constant/LarkspurLimits.MaxResults", f[0].Message);

            // a rule scoped to Larkspur accounts for its entries; the rest stay uncovered
            f = Var(R("R-ClientSearchForm-002", "Larkspur", "var:Larkspur/event/chkLoyaltyOnly.CheckedChanged, var:Larkspur/added/chkLoyaltyOnly",
                "var:Larkspur/handler/chkLoyaltyOnly_CheckedChanged, var:Larkspur/override/btnSearch_Click"), "| var:Larkspur/retagged/lblPostcode | caption only |");
            Assert.All(f, x => Assert.Equal("VAR-UNCOVERED", x.Code));
            Assert.Equal(new[] { "var:Larkspur/constant/LarkspurLimits.MaxResults", "var:Larkspur/retagged/$this", "var:Larkspur/service-added/IClientService.SearchLoyaltyClients", "var:Larkspur/service-removed/IClientService.SearchClients" }
                .Concat(ths.Delta.Select(d => d.Id)), f.Select(x => x.Message.Split(' ')[0]));

            // scope errors: an unscoped rule cites a base handler Larkspur replaces; a Larkspur rule names a Thistle delta; an unknown variant
            f = Var(R("R-ClientSearchForm-003", null, "evt:ClientSearchForm/btnSearch.Click", "handler:ClientSearchForm/btnSearch_Click")
                + R("R-ClientSearchForm-004", "Larkspur", "var:Thistle/event/txtPostcode.KeyDown", "var:Larkspur/override/btnSearch_Click")
                + R("R-ClientSearchForm-005", "Ivy", "evt:ClientSearchForm/txtSurname.KeyDown", "handler:ClientSearchForm/txtSurname_KeyDown")
                + R("R-ClientSearchForm-006", "base, Thistle", "evt:ClientSearchForm/txtSurname.KeyDown", "handler:ClientSearchForm/txtSurname_KeyDown"), all);
            Assert.Equal(new[] { "VAR-SCOPE", "VAR-SCOPE", "VAR-UNKNOWN" }, f.Select(x => x.Code));
            Assert.Contains("which Larkspur replaces (var:Larkspur/override/btnSearch_Click)", f[0].Message);
            Assert.Contains("var:Thistle/event/txtPostcode.KeyDown exists only in Thistle", f[1].Message);
            Assert.Contains("variant 'Ivy'", f[2].Message);
        }
        finally { File.Delete(spec); }
    }

    [Fact]
    public void issue_20_a_configured_factories_entry_is_a_type_or_a_type_dot_method_and_one_that_matches_nothing_is_diagnosed()
    {
        // input: four entries on the Larkspur variant - a namespace-qualified type, a simple Type.Method, a method the type does
        // not declare, a type no file declares; output: the first two read their factories silently, the last two are VARIANT-CONFIG
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "issue20-factories");
        cfg.Variants[0].Factories.AddRange(new[]
        {
            "SampleLegacy.Forms.ClientSearchFormFactory", "ClientSearchFormFactory.ForRegion",
            "SampleLegacy.Forms.ClientSearchFormFactory.Missing", "NoSuchFactory.Create",
        });
        Extractor.Run(cfg, "M", "ClientSearchForm", new StringWriter());
        var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", "ClientSearchForm.json")), Json.Options)!;
        var problems = m.Diagnostics.Where(d => d.Code == "VARIANT-CONFIG" && d.Message.Contains("factories entry", StringComparison.Ordinal)).Select(d => d.Message).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("variant Larkspur: factories entry 'NoSuchFactory.Create' matches no type", problems[0]);
        Assert.StartsWith("variant Larkspur: factories entry 'SampleLegacy.Forms.ClientSearchFormFactory.Missing' reads no factory", problems[1]);
        Assert.Contains(m.Factories!, f => f.Id == "method:SampleLegacy.Forms.ClientSearchFormFactory.Create(string)");
    }

    [Fact]
    public void issue_20_a_configured_subclass_that_derives_from_no_matched_form_is_diagnosed_not_dropped()
    {
        // input: a third variant Ivy listing PolicyWizardStep3 (an XtraForm, not a ClientSearchForm subclass) next to the
        // fixture's Larkspur and Thistle; output: Ivy is no delta, and every manifest of the run says why in one VARIANT-CONFIG
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "issue20-unclaimed");
        cfg.Variants.Add(new VariantConfig { Id = "Ivy", Subclasses = { "PolicyWizardStep3" } });
        Extractor.Run(cfg, "M", "ClientSearchForm", new StringWriter());
        var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", "ClientSearchForm.json")), Json.Options)!;
        Assert.Equal(new[] { "var:Larkspur", "var:Thistle" }, m.Variants!.Select(v => v.Id));
        var problem = Assert.Single(m.Diagnostics, d => d.Code == "VARIANT-CONFIG");
        Assert.Equal("variant Ivy: subclass 'PolicyWizardStep3' derives from no form of the main project (its base chain stops at 'XtraForm')", problem.Message);
    }

    [Fact]
    public void issue_20_variants_of_a_form_the_glob_does_not_select_are_not_a_config_error()
    {
        // input: fixtures/extract.config.json (Larkspur and Thistle customise ClientSearchForm, Larkspur with a configured factory)
        // extracting only DynamicRidersForm; output: neither variant applies to this run, and DynamicRidersForm carries no variants
        // and no VARIANT-CONFIG, not even for the factory of a unit this run does not describe, whose file the run does not load
        string Loaded(string log) => log[log.IndexOf("files loaded:", StringComparison.Ordinal)..log.IndexOf("   rounds:", StringComparison.Ordinal)];
        var plain = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        plain.Output = Path.Combine(_f.OutRoot, "issue20-unselected-plain");
        var plainLog = new StringWriter();
        Extractor.Run(plain, "M", "DynamicRidersForm", plainLog);
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "issue20-unselected");
        cfg.Variants[0].Factories.Add("SampleLegacy.Forms.ClientSearchFormFactory");
        var log = new StringWriter();
        Extractor.Run(cfg, "M", "DynamicRidersForm", log);
        Assert.Equal(Loaded(plainLog.ToString()), Loaded(log.ToString()));
        var m = System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", "DynamicRidersForm.json")), Json.Options)!;
        Assert.Null(m.Variants);
        Assert.DoesNotContain(m.Diagnostics, d => d.Code is "VARIANT-CONFIG" or "VARIANT-INTERMEDIATE");
    }

    [Fact]
    public void issue_20_a_base_chain_that_leaves_the_main_project_is_left_to_the_compiler()
    {
        // input: a project Wren whose OrderSearchForm derives from Common.BaseForm, declared in a referenced Wren.Common.dll
        // (BaseForm : XtraForm), and whose InvoiceForm derives from Shell.ShellForm, a main-project class with no base list that
        // formBaseTypes selects with the wildcard Wren.Shell.*; a variant project Heron lists a subclass of each.
        // output: both forms carry var:Heron with a delta and no VARIANT-CONFIG; without the wildcard, InvoiceForm's written chain
        // is fully indexed and reaches no form, so HeronInvoiceForm is VARIANT-CONFIG and OrderSearchForm keeps its variant
        var root = Path.Combine(_f.OutRoot, "issue20-referenced");
        void Write(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        string Csproj(params string[] files) => "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <ItemGroup>\n"
            + string.Concat(files.Select(f => $"    <Compile Include=\"{f}\" />\n")) + "  </ItemGroup>\n</Project>\n";
        string Form(string ns, string name, string baseName, string button) =>
            $"using System;\n\nnamespace {ns}\n{{\n    public class {name} : {baseName}\n    {{\n        private DevExpress.XtraEditors.SimpleButton {button};\n\n"
            + $"        public {name}()\n        {{\n            InitializeComponent();\n        }}\n\n        private void InitializeComponent()\n        {{\n"
            + $"            this.{button} = new DevExpress.XtraEditors.SimpleButton();\n            this.{button}.Name = \"{button}\";\n"
            + $"            this.{button}.Click += new System.EventHandler(this.{button}_Click);\n            this.Controls.Add(this.{button});\n        }}\n\n"
            + $"        protected virtual void {button}_Click(object sender, EventArgs e)\n        {{\n            Text = \"{name}\";\n        }}\n    }}\n}}\n";
        string Variant(string name, string baseName, string button) =>
            $"using System;\n\nnamespace Wren.Tenants.Heron\n{{\n    public class {name} : {baseName}\n    {{\n"
            + $"        protected override void {button}_Click(object sender, EventArgs e)\n        {{\n            base.{button}_Click(sender, e);\n        }}\n    }}\n}}\n";
        Write("Wren/Wren.csproj", Csproj("Forms\\OrderSearchForm.cs", "Forms\\InvoiceForm.cs", "Shell\\ShellForm.cs"));
        Write("Wren/Forms/OrderSearchForm.cs", Form("Wren.Orders", "OrderSearchForm", "Common.BaseForm", "btnFind"));
        Write("Wren/Forms/InvoiceForm.cs", Form("Wren.Invoices", "InvoiceForm", "Shell.ShellForm", "btnPay"));
        Write("Wren/Shell/ShellForm.cs", "namespace Wren.Shell\n{\n    public class ShellForm\n    {\n        public string Text { get; set; }\n"
            + "        public System.Collections.Generic.List<object> Controls { get; } = new System.Collections.Generic.List<object>();\n    }\n}\n");
        Write("Heron/Heron.csproj", Csproj("Forms\\HeronOrderSearchForm.cs", "Forms\\HeronInvoiceForm.cs"));
        Write("Heron/Forms/HeronOrderSearchForm.cs", Variant("HeronOrderSearchForm", "Wren.Orders.OrderSearchForm", "btnFind"));
        Write("Heron/Forms/HeronInvoiceForm.cs", Variant("HeronInvoiceForm", "Wren.Invoices.InvoiceForm", "btnPay"));

        // the referenced assembly: built against the fixture's references, never part of the Wren project's files
        var fixtureCfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        var refs = Parity.Project.CompilationBuilder.BuildReferences(Parity.Project.CsprojReader.Read(fixtureCfg.Resolve(fixtureCfg.Project)), fixtureCfg, new List<Diagnostic>());
        var common = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Wren.Common",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("namespace Wren.Common\n{\n    public class BaseForm : DevExpress.XtraEditors.XtraForm { }\n}\n") },
            refs, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var dll = Path.Combine(root, "lib", "Wren.Common.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        using (var fs = File.Create(dll)) Assert.True(common.Emit(fs).Success);

        ExtractConfig Config(string output, bool wildcard)
        {
            var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
            cfg.Project = Path.Combine(root, "Wren", "Wren.csproj");
            cfg.Output = Path.Combine(_f.OutRoot, output);
            cfg.SelfAssembly = null;
            cfg.References.Add(dll);
            if (wildcard) cfg.FormBaseTypes.Add("Wren.Shell.*");
            cfg.Variants = new() { new VariantConfig { Id = "Heron", Project = Path.Combine(root, "Heron", "Heron.csproj"), Subclasses = { "HeronOrderSearchForm", "HeronInvoiceForm" } } };
            return cfg;
        }
        FormManifest Read(ExtractConfig cfg, string form) =>
            System.Text.Json.JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(cfg.Output, "M", "manifests", form + ".json")), Json.Options)!;

        var wild = Config("issue20-referenced-wildcard", wildcard: true);
        Extractor.Run(wild, "M", "*", new StringWriter());
        foreach (var (form, cls, button) in new[] { ("OrderSearchForm", "HeronOrderSearchForm", "btnFind"), ("InvoiceForm", "HeronInvoiceForm", "btnPay") })
        {
            var m = Read(wild, form);
            var v = Assert.Single(m.Variants!);
            Assert.Equal(("var:Heron", $"Wren.Tenants.Heron.{cls}"), (v.Id, v.Class));
            Assert.Contains($"var:Heron/override/{button}_Click", v.Delta.Select(d => d.Id));
            Assert.DoesNotContain(m.Diagnostics, d => d.Code is "VARIANT-CONFIG" or "BASE-UNRESOLVED");
        }

        var plain = Config("issue20-referenced-plain", wildcard: false);
        Extractor.Run(plain, "M", "*", new StringWriter());
        Assert.False(File.Exists(Path.Combine(plain.Output, "M", "manifests", "InvoiceForm.json")), "InvoiceForm is no form without the wildcard");
        var order = Read(plain, "OrderSearchForm");
        Assert.Equal("var:Heron", Assert.Single(order.Variants!).Id);
        var problem = Assert.Single(order.Diagnostics, d => d.Code == "VARIANT-CONFIG");
        Assert.Equal("variant Heron: subclass 'HeronInvoiceForm' derives from no form of the main project (its base chain stops at 'InvoiceForm')", problem.Message);
    }

    // ---- issue 23: parity init / doctor / self-contained skills / phase driver ------------------------------

    static Dictionary<string, string> InitActions(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => p[1].Trim().Split("  (")[0].Trim(), p => p[0]);

    static Dictionary<string, string> Snapshot(ConsumingRepo repo) =>
        Directory.EnumerateFiles(repo.Dir, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .ToDictionary(f => Path.GetRelativePath(repo.Dir, f).Replace('\\', '/'), f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    static string Norm(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void issue_23_init_lays_out_the_consuming_repo_and_merges_settings_without_clobbering()
    {
        // input: a consuming repo that already has .claude/settings.json (a foreign PreToolUse and Stop hook, a permission)
        // and a skill of its own, next to a legacy csproj. Expected: parity init lays out legacy/ and the skills from the
        // kit, leaves the settings for --merge, and --merge adds parity's hooks after the repo's own, in order.
        using var repo = new ConsumingRepo(_f.RepoRoot);
        var settingsBefore = repo.Read(".claude/settings.json");
        var foreignSkill = repo.Read(".claude/skills/house-style/SKILL.md");

        var first = repo.Parity("init", "--module", "Orders");
        Assert.Equal(1, first.Code);   // the settings are waiting for --merge
        var acts = InitActions(first.Out);
        Assert.Equal("would-merge", acts[".claude/settings.json"]);
        Assert.Equal(settingsBefore, repo.Read(".claude/settings.json"));   // untouched without --merge
        Assert.Equal(foreignSkill, repo.Read(".claude/skills/house-style/SKILL.md"));

        // the layout, from the kit: every kit file is where adopt/kit.json says
        foreach (var f in new[] { "legacy/extract.config.json", "legacy/control-categories.json", "legacy/.phase", "legacy/hooks/guard-legacy.sh", "legacy/hooks/lint-on-stop.sh",
                     "legacy/templates/spec.md", "legacy/templates/fixture.json", ".claude/skills/parity-extract/SKILL.md", ".claude/skills/parity-specify/SKILL.md",
                     ".claude/skills/parity-implement/SKILL.md", ".claude/skills/parity-bug/SKILL.md", "legacy/Orders/spec/.gitkeep", "legacy/Orders/fixtures/.gitkeep" })
            Assert.True(repo.Exists(f), f);
        Assert.Equal("extract\n", repo.Read("legacy/.phase"));
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(_f.RepoRoot, "adopt", "hooks", "guard-legacy.sh"))), Norm(repo.Read("legacy/hooks/guard-legacy.sh")));
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(_f.RepoRoot, "templates", "control-categories.json"))), Norm(repo.Read("legacy/control-categories.json")));

        // the config skeleton comes from the detected csproj, is strict JSON (the guard's jq reads it) and loads
        using (var doc = System.Text.Json.JsonDocument.Parse(repo.Read("legacy/extract.config.json")))
        {
            var root = doc.RootElement;
            Assert.Equal("../LegacyApp/SampleLegacy/SampleLegacy.csproj", root.GetProperty("project").GetString());
            Assert.Equal("../LegacyApp/SampleLegacy/bin/Debug/SampleLegacy.exe", root.GetProperty("selfAssembly").GetString());
            Assert.Equal("../LegacyApp/SampleLegacy/bin/Debug", root.GetProperty("referenceDirs")[0].GetString());
            Assert.False(root.GetProperty("includeRuntimeReferences").GetBoolean());   // the csproj is .NET Framework 4.8
            Assert.Contains("/v4.8/", root.GetProperty("references")[0].GetString());
            Assert.Equal(0, root.GetProperty("serviceBoundary").GetArrayLength());     // the adopter's decision, not a template's invented name
        }
        var loaded = ExtractConfig.Load(repo.Path_("legacy/extract.config.json"));
        Assert.True(File.Exists(loaded.Resolve(loaded.Project)));
        Assert.Contains("note: serviceBoundary is empty", first.Err);

        // the kit shipped inside the binary is the kit in this repo, byte for byte, and nothing in it is forgotten
        var kit = Kit.Load();
        foreach (var (key, bytes) in kit.Files)
            Assert.Equal(Norm(File.ReadAllText(Path.Combine(_f.RepoRoot, key))), Norm(System.Text.Encoding.UTF8.GetString(bytes)));
        var onDisk = Directory.EnumerateFiles(Path.Combine(_f.RepoRoot, "adopt"), "*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Path.Combine(_f.RepoRoot, "templates"), "*", SearchOption.AllDirectories))
            .Select(x => Path.GetRelativePath(_f.RepoRoot, x).Replace('\\', '/')).Where(x => x != "adopt/README.md").ToList();
        Assert.Empty(onDisk.Except(kit.Files.Keys));

        // --merge: parity's entries after the repo's own, the repo's entries and permission exactly as they were
        var merged = repo.Parity("init", "--merge");
        Assert.Equal(0, merged.Code);
        Assert.Equal("merge", InitActions(merged.Out)[".claude/settings.json"]);
        var settings = repo.Settings();
        var kitSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_f.RepoRoot, "adopt", ".claude", "settings.json")))!;
        var before = System.Text.Json.Nodes.JsonNode.Parse(settingsBefore)!;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before["permissions"], settings["permissions"]));
        foreach (var ev in new[] { "PreToolUse", "Stop" })
        {
            var entries = settings["hooks"]![ev]!.AsArray();
            Assert.Equal(2, entries.Count);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(before["hooks"]![ev]![0], entries[0]));          // the foreign hook, first and unchanged
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(kitSettings["hooks"]![ev]![0], entries[1]));     // issue 22's matcher and the guard command, exactly as on main
        }
        Assert.Equal(ConsumingRepo.ForeignCommand, settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        // a second init is a no-op that says so, with and without --merge
        var snap = Snapshot(repo);
        foreach (var args in new[] { new[] { "init", "--merge", "--module", "Orders" }, new[] { "init" } })
        {
            var again = repo.Parity(args);
            Assert.Equal(0, again.Code);
            Assert.All(InitActions(again.Out).Values, a => Assert.Contains(a, new[] { "unchanged", "skip" }));
            Assert.Equal(snap, Snapshot(repo));
        }

        // a kit file the repo changed is reported, not overwritten, until --merge; the repo's own files are never touched
        File.WriteAllText(repo.Path_("legacy/hooks/lint-on-stop.sh"), "#!/bin/sh\nexit 0\n");
        File.AppendAllText(repo.Path_("legacy/extract.config.json"), "\n");
        File.WriteAllText(repo.Path_("legacy/.phase"), "specify\n");
        var drift = repo.Parity("init");
        Assert.Equal(1, drift.Code);
        Assert.Equal("would-update", InitActions(drift.Out)["legacy/hooks/lint-on-stop.sh"]);
        Assert.Equal("#!/bin/sh\nexit 0\n", repo.Read("legacy/hooks/lint-on-stop.sh"));
        Assert.Equal(0, repo.Parity("init", "--merge").Code);
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(_f.RepoRoot, "adopt", "hooks", "lint-on-stop.sh"))), Norm(repo.Read("legacy/hooks/lint-on-stop.sh")));
        Assert.Equal("specify\n", repo.Read("legacy/.phase"));
        Assert.Equal(foreignSkill, repo.Read(".claude/skills/house-style/SKILL.md"));

        // a repo with no settings.json gets the kit's, as is
        using var fresh = new ConsumingRepo(_f.RepoRoot, foreignSetup: false);
        Assert.Equal(0, fresh.Parity("init").Code);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(kitSettings, fresh.Settings()));

        // settings.json that is not strict JSON (comments: rewriting would drop them; a duplicate key: which one counts is
        // unclear) is refused as a failure, not a usage error, before init writes anything
        foreach (var bad in new[] { "{\n  // keep me\n  \"hooks\": {}\n}\n", "{ \"hooks\": {}, \"hooks\": {} }\n" })
        {
            using var refusing = new ConsumingRepo(_f.RepoRoot, foreignSetup: false);
            Directory.CreateDirectory(refusing.Path_(".claude"));
            File.WriteAllText(refusing.SettingsPath, bad);
            var before = Snapshot(refusing);
            var refused = refusing.Parity("init", "--merge");
            Assert.Equal(3, refused.Code);
            Assert.Contains("not strict JSON", refused.Err);
            Assert.Equal(before, Snapshot(refusing));
        }
    }

    [Fact]
    public void issue_23_the_installed_skills_are_self_contained()
    {
        // input: the skills as init installs them. Expected: the spec template sits beside the specify skill and equals
        // templates/spec.md; every repo-relative file a skill names exists in the consuming repo; every command a skill
        // gives (a span whose first word names parity) runs through sh with PARITY_BIN set and nothing named parity on PATH,
        // and reaches that binary - a bare `parity`, a shell variable or any other spelling fails with "not found".
        using var repo = new ConsumingRepo(_f.RepoRoot);
        repo.Parity("init", "--merge");

        var template = repo.Read(".claude/skills/parity-specify/spec.md");
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(_f.RepoRoot, "templates", "spec.md"))), Norm(template));
        var commands = new List<string>();
        foreach (var skill in repo.Files(".claude/skills").Where(f => f.EndsWith("/SKILL.md") && f.Contains("/parity-")))
        {
            var spans = System.Text.RegularExpressions.Regex.Matches(repo.Read(skill), "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();
            foreach (var sp in spans.Where(sp => !sp.Contains(' ') && !sp.Contains('<') && sp.Contains('/') && System.Text.RegularExpressions.Regex.IsMatch(sp, @"\.(md|json|sh|phase)$")))
                Assert.True(repo.Exists(sp), $"{skill} names {sp}, which the consuming repo lacks");
            commands.AddRange(spans.Where(sp => sp.Contains(' ') && (sp.Contains("PARITY_BIN") || sp.Split(' ')[0].Trim('"', '$', '{', '}', '\'').Contains("parity", StringComparison.OrdinalIgnoreCase))));
        }
        Assert.NotEmpty(commands);
        if (OperatingSystem.IsWindows() || GuardHookTests.Sh() == null) return;

        // a skill's span is a template: <placeholders> stand for values the agent fills in
        string Fill(string span) => System.Text.RegularExpressions.Regex.Replace(span, "<[^>]*>", "x");
        (int Code, string Out, string Err) Sh(string command, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(GuardHookTests.Sh()!) { WorkingDirectory = repo.Dir, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command);
            psi.Environment.Remove("PARITY_BIN");
            foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            return (p.ExitCode, o, err.Result);
        }

        var tools = Path.Combine(repo.Dir, "..", "tools");
        Directory.CreateDirectory(tools);
        var empty = Path.Combine(repo.Dir, "..", "empty-path");
        Directory.CreateDirectory(empty);
        var record = Path.Combine(tools, "record-argv");
        var calls = Path.Combine(tools, "calls.log");
        File.WriteAllText(record, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> \"$CALLS\"\n");
        File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var verbs = new HashSet<string>();
        foreach (var command in commands.Distinct())
        {
            File.Delete(calls);
            var r = Sh(Fill(command), new() { ["PARITY_BIN"] = record, ["CALLS"] = calls, ["PATH"] = empty });
            Assert.True(File.Exists(calls), $"`{command}` never reached PARITY_BIN: exit {r.Code}: {r.Err}");
            verbs.Add(File.ReadAllLines(calls).Single());
        }
        Assert.Superset(new HashSet<string> { "extract", "probe", "lint", "rebind" }, verbs);

        // with PARITY_BIN unset the same spelling falls back to parity on PATH, and that is the real binary
        var wrapper = Path.Combine(tools, "on-path");
        Directory.CreateDirectory(wrapper);
        var script = Path.Combine(wrapper, "parity");
        File.WriteAllText(script, $"#!/bin/sh\nexec '{Environment.ProcessPath}' '{Path.Combine(AppContext.BaseDirectory, "parity.dll")}' \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lint = Fill(commands.First(c => c.Contains(" lint ") && c.EndsWith(".md")));
        var viaPath = Sh(lint, new() { ["PATH"] = wrapper + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") });
        Assert.True(viaPath.Code == 3 && viaPath.Err.StartsWith("parity:"), $"via PATH: exit {viaPath.Code}: {viaPath.Err}");   // the binary ran: it refused the missing spec itself
    }

    static string StatusOf(List<(string Status, string Line)> lines, string name) => lines.Single(l => l.Line.StartsWith(name + " ")).Status;
    static string DetailOf(List<(string Status, string Line)> lines, string name) => lines.Single(l => l.Line.StartsWith(name + " ")).Line;

    [Fact]
    public void issue_23_doctor_prints_a_line_per_check_and_exits_non_zero_on_a_fail()
    {
        // input: an init-ed consuming repo whose legacy project is not built, then the same repo set up properly, then
        // one thing broken at a time. Expected: one pass/warn/fail line per check, exit 1 on any fail, 0 otherwise.
        using var repo = new ConsumingRepo(_f.RepoRoot);
        Assert.Equal(0, repo.Parity("init", "--merge").Code);
        if (OperatingSystem.IsWindows() || GuardHookTests.Sh() == null) return;   // the checks look tools up on a controlled PATH

        Dictionary<string, string?> Env(string bin, string? parityBin = "") => new() { ["PATH"] = bin, ["PARITY_BIN"] = parityBin == "" ? Path.Combine(bin, "parity") : parityBin, ["PARITY_LEGACY_ROOT"] = null };
        var bin = repo.Path_Only("jq", "sh", "parity");

        // not built, reference assemblies missing: fails, each on its own line, and a non-zero exit
        var first = repo.Doctor(Env(bin));
        Assert.Equal(1, first.Code);
        Assert.All(first.Lines, l => Assert.Contains(l.Status, new[] { "pass", "warn", "fail" }));
        foreach (var name in new[] { ".net sdk", "parity binary", "sh", "jq", "config", "legacy root", "legacy project", "reference assemblies", "output path", "built", "references",
                     "skills", "hooks", "templates", "spec template", "hook PreToolUse guard-legacy.sh", "hook Stop lint-on-stop.sh", "phase", "git ignore" })
            Assert.Single(first.Lines, l => l.Line.StartsWith(name + " "));
        Assert.Equal("fail", StatusOf(first.Lines, "output path"));
        Assert.Equal("fail", StatusOf(first.Lines, "built"));
        Assert.Equal("fail", StatusOf(first.Lines, "reference assemblies"));
        Assert.Equal("warn", StatusOf(first.Lines, ".net sdk"));   // no dotnet on this PATH: fine with a release binary
        Assert.Equal("pass", StatusOf(first.Lines, "parity binary"));
        Assert.Equal("pass", StatusOf(first.Lines, "hook PreToolUse guard-legacy.sh"));
        Assert.Equal("pass", StatusOf(first.Lines, "git ignore"));
        Assert.Matches(@"\d+ pass, \d+ warn, [1-9]\d* fail", first.Err);

        // set up properly: built, reference assemblies present. Some csproj References are not in the output: a warn, exit 0
        repo.Build();
        var refs = repo.References(3);
        repo.EditConfig(c => c["references"] = new System.Text.Json.Nodes.JsonArray(refs + "/*.dll"));
        var ok = repo.Doctor(Env(bin));
        Assert.True(ok.Code == 0, string.Join("\n", ok.Lines.Where(l => l.Status == "fail").Select(l => l.Line)));
        Assert.DoesNotContain(ok.Lines, l => l.Status == "fail");
        Assert.Equal("warn", StatusOf(ok.Lines, "references"));
        Assert.Contains("not found", DetailOf(ok.Lines, "references"));

        // each of these breaks one check
        var noJq = repo.Path_Only("sh", "parity");
        var r = repo.Doctor(Env(noJq));
        Assert.Equal(1, r.Code); Assert.Equal("fail", StatusOf(r.Lines, "jq"));
        r = repo.Doctor(Env(bin, parityBin: null));   // PATH has parity, PARITY_BIN unset: still fine
        Assert.Equal("pass", StatusOf(r.Lines, "parity binary"));
        var empty = repo.Path_Only("jq", "sh");
        r = repo.Doctor(Env(empty, parityBin: null));
        Assert.Equal(1, r.Code); Assert.Equal("fail", StatusOf(r.Lines, "parity binary"));
        r = repo.Doctor(Env(bin, parityBin: "/nowhere/parity"));
        Assert.Equal("fail", StatusOf(r.Lines, "parity binary"));
        var env = Env(bin); env["PARITY_LEGACY_ROOT"] = Path.Combine(repo.Dir, "no-such-root");
        Assert.Equal("fail", StatusOf(repo.Doctor(env).Lines, "legacy root"));

        // above ~200 reference DLLs: a warn that says how many
        repo.References(201);
        r = repo.Doctor(Env(bin));
        Assert.True(r.Code == 0, string.Join("\n", r.Lines.Where(l => l.Status == "fail").Select(l => l.Line)));
        Assert.Equal("warn", StatusOf(r.Lines, "references"));
        Assert.Contains("202 DLLs", DetailOf(r.Lines, "references"));   // 201 references plus the project's own
        repo.EditConfig(c => c["includeRuntimeReferences"] = true);
        Assert.Equal("warn", StatusOf(repo.Doctor(Env(bin)).Lines, "reference assemblies"));   // .NET Framework project with the embedded .NET references
        repo.EditConfig(c => c["includeRuntimeReferences"] = false);

        // hooks: wiring and script
        File.Delete(repo.Path_("legacy/hooks/guard-legacy.sh"));
        r = repo.Doctor(Env(bin));
        Assert.Equal(1, r.Code);
        Assert.Equal("fail", StatusOf(r.Lines, "hooks"));
        Assert.Equal("fail", StatusOf(r.Lines, "hook PreToolUse guard-legacy.sh"));
        Assert.Equal(0, repo.Parity("init").Code);   // restored
        File.WriteAllText(repo.SettingsPath, "{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"sh legacy/hooks/lint-on-stop.sh\" } ] } ] } }");
        r = repo.Doctor(Env(bin));
        Assert.Equal("fail", StatusOf(r.Lines, "hook PreToolUse guard-legacy.sh"));
        Assert.Equal("pass", StatusOf(r.Lines, "hook Stop lint-on-stop.sh"));
        repo.Parity("init", "--merge");
        // a guard wired with a narrower matcher than the kit's is a warn
        var kitMatcher = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_f.RepoRoot, "adopt", ".claude", "settings.json")))!["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>();
        Assert.Equal(0, repo.Parity("init", "--merge").Code);
        var s = repo.Settings();
        var guard = s["hooks"]!["PreToolUse"]!.AsArray().Single(e => e!["matcher"]!.GetValue<string>() == kitMatcher)!;
        guard["matcher"] = "Bash";
        File.WriteAllText(repo.SettingsPath, s.ToJsonString());
        Assert.Equal("warn", StatusOf(repo.Doctor(Env(bin)).Lines, "hook PreToolUse guard-legacy.sh"));
        guard["matcher"] = kitMatcher;
        File.WriteAllText(repo.SettingsPath, s.ToJsonString());
        Assert.Equal("pass", StatusOf(repo.Doctor(Env(bin)).Lines, "hook PreToolUse guard-legacy.sh"));

        // skills, templates, phase, config
        Directory.Delete(repo.Path_(".claude/skills/parity-bug"), true);
        r = repo.Doctor(Env(bin));
        Assert.Equal("fail", StatusOf(r.Lines, "skills"));
        repo.Parity("init");
        File.WriteAllText(repo.Path_("legacy/.phase"), "ponder\n");
        Assert.Equal("fail", StatusOf(repo.Doctor(Env(bin)).Lines, "phase"));
        File.WriteAllText(repo.Path_("legacy/.phase"), "specify\n");
        var cfgText = repo.Read("legacy/extract.config.json");
        File.WriteAllText(repo.Path_("legacy/extract.config.json"), "// a comment parity accepts and jq does not\n" + cfgText);
        r = repo.Doctor(Env(bin));
        Assert.Equal("fail", StatusOf(r.Lines, "config"));
        Assert.Contains("not strict JSON", DetailOf(r.Lines, "config"));
        File.WriteAllText(repo.Path_("legacy/extract.config.json"), cfgText);

        // git: an ignored legacy/ or module folder is a fail
        Directory.CreateDirectory(repo.Path_("legacy/Orders/spec"));
        File.WriteAllText(repo.Path_("legacy/Orders/spec/x.md"), "x");
        File.WriteAllText(repo.Path_(".gitignore"), "legacy/Orders/\n");
        r = repo.Doctor(Env(bin));
        Assert.Equal("fail", StatusOf(r.Lines, "git ignore"));
        Assert.Contains("legacy/Orders", DetailOf(r.Lines, "git ignore"));
        File.WriteAllText(repo.Path_(".gitignore"), "legacy/\n");
        Assert.Equal("fail", StatusOf(repo.Doctor(Env(bin)).Lines, "git ignore"));
        File.Delete(repo.Path_(".gitignore"));
        Assert.Equal("pass", StatusOf(repo.Doctor(Env(bin)).Lines, "git ignore"));

        // not a git repo: a warn, not a fail
        using var plain = new ConsumingRepo(_f.RepoRoot, foreignSetup: false, git: false);
        plain.Parity("init");
        var pbin = plain.Path_Only("jq", "sh", "parity");
        var pr = plain.Doctor(new Dictionary<string, string?> { ["PATH"] = pbin, ["PARITY_BIN"] = null, ["PARITY_LEGACY_ROOT"] = null, ["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(plain.Dir) });
        Assert.Equal("warn", StatusOf(pr.Lines, "git ignore"));
    }

    [Fact]
    public void issue_23_phase_driver_runs_each_phase_in_a_fresh_session_with_its_prompt_and_phase()
    {
        // input: parity init --phase-driver, then the generated script with a stand-in for the headless agent that logs its
        // process id, legacy/.phase and prompt. Expected: one process per phase, in order, each with the phase file set and
        // that phase's prompt; the phases named on the command line, or all driven ones; a failing session stops the run.
        using var repo = new ConsumingRepo(_f.RepoRoot, foreignSetup: false);
        Assert.False(repo.Exists("legacy/run-phases.sh"));
        Assert.Equal(0, repo.Parity("init", "--phase-driver").Code);
        Assert.True(repo.Exists("legacy/run-phases.sh"));
        Assert.Equal("unchanged", InitActions(repo.Parity("init", "--phase-driver").Out)["legacy/run-phases.sh"]);

        if (OperatingSystem.IsWindows() || GuardHookTests.Sh() == null) return;
        var log = Path.Combine(repo.Dir, "..", "agent.log");
        var agent = Path.Combine(repo.Dir, "..", "agent.sh");
        File.WriteAllText(agent, "#!/bin/sh\nprintf '%s|%s|%s\\n' \"$$\" \"$(cat legacy/.phase)\" \"$1\" >> \"$AGENT_LOG\"\n[ -z \"${AGENT_FAIL:-}\" ] || [ \"$(cat legacy/.phase)\" != \"$AGENT_FAIL\" ]\n");
        (int Code, string Out, string Err) Drive(Dictionary<string, string?> env, params string[] args)
        {
            var psi = new ProcessStartInfo(GuardHookTests.Sh()!) { WorkingDirectory = Path.GetDirectoryName(repo.Dir)!, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add(Path.Combine(repo.Dir, "legacy", "run-phases.sh"));
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PARITY_AGENT"] = "sh " + agent; psi.Environment["AGENT_LOG"] = log;
            psi.Environment.Remove("AGENT_FAIL");
            foreach (var (k, v) in env) { if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
            using var pr = Process.Start(psi)!;
            var err = pr.StandardError.ReadToEndAsync(); var o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
            return (pr.ExitCode, o, err.Result);
        }
        string[] Log() => File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>();
        string Prompt(string phase, string form, string module) => phase switch
        {
            "extract" => $"Run the parity-extract skill for form {form} of module {module}.",
            "specify" => $"Run the parity-specify skill for form {form} of module {module}.",
            _ => $"Run the parity-implement skill for form {form} of module {module}.",
        };

        // run from outside the repo: the script finds its own repo. The module name has a space: the prompt stays one argument
        var straight = Drive(new(), "Order Entry", "OrderForm");
        Assert.Equal(0, straight.Code);
        var lines = Log().Select(l => l.Split('|')).ToList();
        Assert.Equal(new[] { "extract", "specify", "implement" }, lines.Select(l => l[1]));
        Assert.Equal(3, lines.Select(l => l[0]).Distinct().Count());                         // three processes: three fresh sessions
        Assert.Equal(new[] { "extract", "specify", "implement" }.Select(p => Prompt(p, "OrderForm", "Order Entry")), lines.Select(l => l[2]));
        Assert.Equal("implement\n", repo.Read("legacy/.phase"));

        // named phases run alone, in the order given
        File.Delete(log);
        Assert.Equal(0, Drive(new(), "Orders", "OrderForm", "specify", "extract").Code);
        Assert.Equal(new[] { "specify|" + Prompt("specify", "OrderForm", "Orders"), "extract|" + Prompt("extract", "OrderForm", "Orders") },
            Log().Select(l => l.Split('|')).Select(l => l[1] + "|" + l[2]));
        Assert.Equal("extract\n", repo.Read("legacy/.phase"));

        // a failing session stops the run; an unknown phase and missing arguments are refused before anything runs
        File.Delete(log);
        var failed = Drive(new() { ["AGENT_FAIL"] = "specify" }, "Orders", "OrderForm");
        Assert.Equal(1, failed.Code);
        Assert.Equal(new[] { "extract", "specify" }, Log().Select(l => l.Split('|')[1]));
        File.Delete(log);
        var phaseBefore = repo.Read("legacy/.phase");
        Assert.Equal(2, Drive(new(), "Orders", "OrderForm", "extract", "bug").Code);
        Assert.Equal(2, Drive(new(), "Orders").Code);
        Assert.Empty(Log());
        Assert.Equal(phaseBefore, repo.Read("legacy/.phase"));
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
        foreach (var tool in new[] { "cat", "tr", "sed", "grep", "awk", "dirname", "printf", "head", "date", "sh" })
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
