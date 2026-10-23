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
        Assert.Equal(28, r.InitStatements);
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
        Assert.Contains("(<path>.cs:39)", text);
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
    public void issue_7_references_load_once_and_each_phase_is_timed()
    {
        Assert.Matches(@"references: \d+ loaded in \d+\.\ds", _f.Log);
        Assert.Matches(@"round 1: \d+ file\(s\), \d+ form\(s\), \d+\.\ds", _f.Log);
        Assert.Matches(@"render\+write: \d+\.\ds", _f.Log);
        Assert.Matches(@"peak-rss-mb: \d+", _f.Log);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(_f.Log, "references: "));
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
