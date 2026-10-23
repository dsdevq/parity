using Parity.Lint;
using Xunit;

namespace Parity.Tests;

[Collection("extract")]
public sealed class ExtractTests
{
    private readonly ExtractFixture _f;
    public ExtractTests(ExtractFixture f) => _f = f;

    [Fact]
    public void every_form_has_no_raw_statements_and_only_the_intended_diagnostics()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["BaseDialogForm"] = new[] { "NO-DESIGNER" },             // a base class without InitializeComponent
            ["BeneficiarySearchForm"] = new[] { "GRID-NO-COLUMNS", "UNREACHED-METHOD" }, // gvHistory's columns are built in code; btnExport_Click is never wired
            ["PremiumHistoryForm"] = new[] { "RESOURCE-KEY-MISSING" },  // lsArchiveReasonTooLong is not in Captions.resx (#19)
            // BeneficiaryHostForm and CoverSummaryForm (#17) have none: the reflection-invoked ApplyDefaults is an entry, not unreached
        };
        foreach (var s in _f.Summaries)
        {
            Assert.True(s.Raw == 0, $"{s.Form}: {s.Raw} raw statement(s)");
            var codes = _f.Manifest(s.Form).Diagnostics.Select(d => d.Code).OrderBy(x => x).ToArray();
            Assert.True((expected.GetValueOrDefault(s.Form) ?? Array.Empty<string>()).OrderBy(x => x).SequenceEqual(codes),
                $"{s.Form}: diagnostics " + string.Join("; ", _f.Manifest(s.Form).Diagnostics.Select(d => d.Code + " " + d.Message)));
        }
        Assert.Equal(9, _f.Summaries.Count);
    }

    [Fact]
    public void names_the_designer_uses_but_never_creates_are_resolved_not_diagnosed()
    {
        // issues #2, #3, #4: designer locals, declared-only fields, members inherited from a custom base form,
        // and form property paths (Appearance, IconOptions)
        var m = _f.Manifest("BeneficiarySearchForm");
        var c = m.Controls.ToDictionary(x => x.Field);
        Assert.Equal("local", c["editorButtonImageOptions1"].Category);
        Assert.Equal("DevExpress.XtraEditors.EditorButtonImageOptions", c["editorButtonImageOptions1"].Type);
        Assert.Equal("SampleLegacy.Forms.BaseDialogForm", c["btnOK"].Props["@inherited"]);
        Assert.Equal("action", c["btnOK"].Category);
        Assert.Equal("Select", c["btnOK"].Caption);
        Assert.Equal("ctrl:BeneficiarySearchForm/$this", c["pnlMain"].Parent);
        Assert.Equal("column", c["_lstResults_ColumnHeader_1"].Category);
        Assert.Equal("ctrl:BeneficiarySearchForm/lstResults", c["_lstResults_ColumnHeader_1"].Parent);
        Assert.True(c["_lstResults_ColumnHeader_1"].Props.ContainsKey("@declaredOnly"));
        Assert.False(c.ContainsKey("Appearance"));
        Assert.Equal("System.Drawing.Color.White", c["$this"].Props["Appearance.BackColor"]);
        Assert.Equal("false", c["$this"].Props["IconOptions.ShowIcon"]);
        // issue #1: ImageList.Images.SetKeyName is recorded, not raw
        Assert.Equal("0, \"person.png\"; 1, \"company.png\"", c["imgIcons"].Props["Images.SetKeyName()"]);
        // declared-only fields created in the constructor are not reported again as dynamic UI
        Assert.Empty(m.DynamicUi);
    }

    [Fact]
    public void unreached_method_diagnostic_says_what_was_checked()
    {
        // issue #5
        var d = Assert.Single(_f.Manifest("BeneficiarySearchForm").Diagnostics, x => x.Code == "UNREACHED-METHOD");
        Assert.Contains("btnExport_Click", d.Message);
        Assert.Contains("checked: designer wiring", d.Message);
        Assert.Contains("handler-shaped signature", d.Message);
        Assert.Contains("no other reference in the loaded files", d.Message);
    }

    [Fact]
    public void slices_never_contain_carriage_returns()
    {
        // issue #8: manifests must hash the same on Windows and Linux
        foreach (var s in _f.Summaries)
        {
            var m = _f.Manifest(s.Form);
            Assert.All(m.Handlers, h => Assert.DoesNotContain('\r', h.Source));
            Assert.All(m.Methods.Where(x => x.Source != null), x => Assert.DoesNotContain('\r', x.Source!));
        }
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C {\r\n  void M() {\r\n    int x = 1;\r\n  }\r\n}");
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single();
        Assert.DoesNotContain('\r', Parity.Passes.Syntax.Source(method));
        Assert.Equal("void M() {\n  int x = 1;\n}", Parity.Passes.Syntax.Source(method));
    }

    [Fact]
    public void designer_tree_follows_controls_add_layout_items_and_grid_collections()
    {
        var m = _f.Manifest("PolicyWizardStep3");
        var c = m.Controls.ToDictionary(x => x.Field);
        Assert.Equal("ctrl:PolicyWizardStep3/layoutControl1", c["txtPremium"].Parent);
        Assert.Equal("ctrl:PolicyWizardStep3/layoutControlItem2", c["txtPremium"].LayoutItem);
        Assert.Equal("ctrl:PolicyWizardStep3/gvRiders", c["colRiderName"].Parent);
        Assert.Equal("ctrl:PolicyWizardStep3/grdRiders", c["gvRiders"].Parent);
        Assert.Equal("ctrl:PolicyWizardStep3/$this", c["layoutControl1"].Parent);
        Assert.Equal("ctrl:PolicyWizardStep3/panelButtons", c["btnNext"].Parent);
        Assert.Equal("input", c["txtPremium"].Category);
        Assert.Equal("column", c["colLoading"].Category);
        Assert.Equal("rule", c["premiumRule"].Category);
        Assert.Equal("nonvisual", c["policyBindingSource"].Category);
    }

    [Fact]
    public void resx_captions_are_resolved()
    {
        var m = _f.Manifest("PolicyWizardStep3");
        var c = m.Controls.ToDictionary(x => x.Field);
        Assert.Equal("Single premium", c["chkSinglePremium"].Caption);
        Assert.Equal("Premium", c["layoutControlItem2"].Caption);
        Assert.Equal("[select a product]", c["cboProduct"].Props["Properties.NullText"]);
        Assert.Equal("cboProduct.Properties.NullText", c["cboProduct"].Props["Properties.NullText@resx"]);
        Assert.Equal("New Business - Step 3: Premium", m.Caption);
    }

    [Fact]
    public void wiring_comes_from_designer_code_lambdas_overrides_and_ctor()
    {
        var m = _f.Manifest("PolicyWizardStep3");
        var byId = m.Events.ToDictionary(e => e.Id);
        Assert.Equal("designer", byId["evt:PolicyWizardStep3/txtPremium.Validating"].Source);
        Assert.Equal("code", byId["evt:PolicyWizardStep3/btnNext.EnabledChanged"].Source);
        Assert.Equal("handler:PolicyWizardStep3/lambda@L25", byId["evt:PolicyWizardStep3/dtStart.EditValueChanged"].Handler);
        Assert.Equal("override", byId["evt:PolicyWizardStep3/$this.OnLoad"].Source);
        Assert.Equal("ctor", byId["evt:PolicyWizardStep3/$this.ctor(2)"].Source);
        Assert.Equal("designer", byId["evt:PolicyWizardStep3/$this.Load"].Source);
        Assert.Equal(15, m.Events.Count);
        Assert.Equal(15, m.Handlers.Count);
    }

    [Fact]
    public void handler_reads_writes_calls_and_service_calls_are_resolved()
    {
        var m = _f.Manifest("PolicyWizardStep3");
        var h = m.Handlers.Single(x => x.Name == "chkSinglePremium_CheckedChanged");
        Assert.Contains("ctrl:PolicyWizardStep3/chkSinglePremium", h.Reads);
        Assert.Contains("ctrl:PolicyWizardStep3/cboFrequency", h.Writes);
        Assert.Contains("ctrl:PolicyWizardStep3/txtInstallments", h.Writes);
        Assert.Empty(h.Unresolved);

        var next = m.Handlers.Single(x => x.Name == "btnNext_Click");
        Assert.Contains("svc:IPolicyService.SavePolicyStep", next.ServiceCalls);
        Assert.Contains("method:SampleLegacy.Forms.PolicyWizardController.Next()", next.Calls);
        Assert.Contains("method:SampleLegacy.Forms.PolicyWizardStep3.ValidatePremiumAgainstLimits()", next.Calls);

        var validating = m.Handlers.Single(x => x.Name == "txtPremium_Validating");
        Assert.Equal("validation", validating.Kind);
        Assert.Equal("Forms/PolicyWizardStep3.Validation.cs", validating.Loc.File);

        Assert.Equal(new[] { "svc:IPolicyService.GetAvailableRiders", "svc:IPolicyService.GetPremiumLimits", "svc:IPolicyService.GetProducts", "svc:IPolicyService.SavePolicyStep" }, m.ServiceCalls.Select(s => s.Id));
        Assert.Contains("_policy, 3", m.ServiceCalls.Single(s => s.Id == "svc:IPolicyService.SavePolicyStep").Args);
    }

    [Fact]
    public void transitive_methods_are_sliced_across_files_and_the_designer_is_never_followed()
    {
        var m = _f.Manifest("PolicyWizardStep3");
        var ids = m.Methods.Select(x => x.Id).ToList();
        Assert.Contains("method:SampleLegacy.Helpers.PremiumRules.IsWithinLimits(decimal, SampleLegacy.Services.PremiumLimitsDto)", ids);
        Assert.Contains("method:SampleLegacy.Forms.PolicyWizardController.CanGoBack.get", ids);
        Assert.DoesNotContain(ids, i => i.Contains("InitializeComponent"));
        Assert.All(m.Methods, x => Assert.NotNull(x.Source));
        var recalc = m.Methods.Single(x => x.Id == "method:SampleLegacy.Forms.PolicyWizardStep3.RecalculateTotal()");
        Assert.Contains("ctrl:PolicyWizardStep3/lblTotal", recalc.Writes);
        Assert.StartsWith("private void RecalculateTotal()", recalc.Source);
        Assert.StartsWith("private void RecalculateTotal()\n{", recalc.Source!); // dedented
    }

    [Fact]
    public void controls_created_at_runtime_are_reported_with_their_container_and_wiring()
    {
        var m = _f.Manifest("DynamicRidersForm");
        var d = Assert.Single(m.DynamicUi);
        Assert.Equal("dyn:DynamicRidersForm/DynamicRidersForm_Load/chk", d.Id);
        Assert.Equal("ctrl:DynamicRidersForm/pnlRiders", d.AddedTo);
        Assert.Contains(m.Events, e => e.Control == d.Id && e.Event == "CheckedChanged" && e.Source == "dynamic");
    }

    [Fact]
    public void duplicates_find_exact_copies_and_near_copies_across_forms()
    {
        var mod = _f.Module();
        var exact = mod.Duplicates.Where(c => c.Kind == "exact").ToList();
        Assert.Contains(exact, c => c.Members.Contains("handler:ClientSearchForm/btnSelect_Click") && c.Members.Contains("handler:ClaimClientSearchForm/btnSelect_Click"));
        Assert.DoesNotContain(exact, c => c.Members.Any(x => x.StartsWith("method:") && c.Members.Any(y => y.StartsWith("handler:") && y.EndsWith(x[(x[..x.IndexOf('(')].LastIndexOf('.') + 1)..x.IndexOf('(')]))));
        var near = Assert.Single(mod.Duplicates, c => c.Kind == "near");
        Assert.Equal(new[] { "handler:ClaimClientSearchForm/btnSearch_Click", "handler:ClientSearchForm/btnSearch_Click" }, near.Members);
    }

    [Fact]
    public void only_the_files_the_module_needs_are_loaded()
    {
        Assert.Contains("files loaded: 34 of 35 (+1 from variant projects)", _f.Log);
        Assert.Contains("references: ", _f.Log);
        Assert.Contains("round 1:", _f.Log);
    }
}

[Collection("extract")]
public sealed class LintTests
{
    private readonly ExtractFixture _f;
    public LintTests(ExtractFixture f) => _f = f;

    private List<Finding> Gate(Func<string, string>? mutate = null) =>
        SpecLinter.Lint(_f.InstallExampleSpec("PolicyWizardStep3", mutate), gate: true, implementDir: null);

    private static void AssertOnly(List<Finding> f, params string[] codes)
    {
        var errors = f.Where(x => x.Severity == "error").Select(x => x.Code).ToList();
        Assert.True(codes.OrderBy(x => x).SequenceEqual(errors.OrderBy(x => x)), "expected [" + string.Join(", ", codes) + "] got: " + string.Join(" | ", f.Where(x => x.Severity == "error")));
    }

    [Fact]
    public void the_example_spec_passes_the_gate_with_only_method_warnings()
    {
        var f = Gate();
        AssertOnly(f);
        Assert.All(f, x => Assert.Equal("COV-METHOD", x.Code));
    }

    [Fact] public void stale_hash_blocks_everything() => AssertOnly(Gate(s => s.Replace("manifest: sha256:", "manifest: sha256:dead")), "STALE");

    [Fact] public void unknown_id_in_cite() => AssertOnly(Gate(s => s.Replace("cite: handler:PolicyWizardStep3/btnBack_Click", "cite: handler:PolicyWizardStep3/btnBackk_Click")), "CITE-UNKNOWN", "COV-HANDLER");

    [Fact] public void wrong_kind_in_covers() => AssertOnly(Gate(s => s.Replace("covers: evt:PolicyWizardStep3/btnBack.Click", "covers: ctrl:PolicyWizardStep3/btnBack")), "CITE-KIND", "COV-EVENT");

    [Fact] public void hedging_is_an_error() => AssertOnly(Gate(s => s.Replace("- then: lblTotal is visible exactly when btnNext is enabled", "- then: lblTotal should probably be visible when btnNext is enabled")), "HEDGE");

    [Fact] public void removing_a_rule_surfaces_event_and_handler_coverage() =>
        AssertOnly(Gate(s => Cut(s, "### R-PolicyWizardStep3-012", "## No behavior")), "COV-EVENT", "COV-HANDLER");

    [Fact] public void removing_a_fields_row_is_a_control_gap() =>
        AssertOnly(Gate(s => s.Replace("| ctrl:PolicyWizardStep3/dtStart | Start date | input | policy.StartDate | field | |\n", "")), "COV-CONTROL");

    [Fact] public void dropped_needs_a_decision() =>
        AssertOnly(Gate(s => s.Replace("decided-by example-reviewer 2026-10-22 |", "|")), "DROP-REASON");

    [Fact] public void missing_fixture_file() =>
        AssertOnly(Gate(s => s.Replace("| svc:IPolicyService.GetProducts | R-PolicyWizardStep3-008 | products |", "| svc:IPolicyService.GetProducts | R-PolicyWizardStep3-008 | products-v2 |")), "FIXTURE-MISSING");

    [Fact] public void service_without_row() =>
        AssertOnly(Gate(s => s.Replace("| svc:IPolicyService.GetProducts | R-PolicyWizardStep3-008 | products |\n", "")), "COV-SERVICE");

    [Fact] public void open_question_blocks_the_gate() => AssertOnly(Gate(s => s.Replace("- none", "- is the loading compounding or additive?")), "OPEN");

    [Fact] public void draft_status_blocks_the_gate() => AssertOnly(Gate(s => s.Replace("status: reviewed", "status: draft")), "STATUS");

    [Fact]
    public void structure_lint_without_gate_ignores_coverage()
    {
        var path = _f.InstallExampleSpec("PolicyWizardStep3", s => Cut(s, "### R-PolicyWizardStep3-012", "## No behavior"));
        var f = SpecLinter.Lint(path, gate: false, implementDir: null);
        Assert.DoesNotContain(f, x => x.Severity == "error");
    }

    [Fact]
    public void implement_check_wants_every_rule_id_in_the_angular_tree()
    {
        var dir = Path.Combine(_f.OutRoot, "ng");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "step3.spec.ts"), "describe('R-PolicyWizardStep3-001', () => {});\n" +
            string.Join("\n", Enumerable.Range(2, 10).Select(i => $"it('R-PolicyWizardStep3-{i:000} holds', () => {{}});")));
        var f = SpecLinter.Lint(_f.InstallExampleSpec("PolicyWizardStep3"), gate: true, implementDir: dir);
        var missing = Assert.Single(f, x => x.Code == "COV-TEST");
        Assert.Contains("R-PolicyWizardStep3-012", missing.Message);
    }

    private static string Cut(string s, string from, string to)
    {
        var a = s.IndexOf(from, StringComparison.Ordinal);
        var b = s.IndexOf(to, StringComparison.Ordinal);
        return s[..a] + s[b..];
    }
}
